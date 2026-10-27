# Mighty Mini Mouse for macOS — Technical Specification

## Architecture Overview

```
┌─────────────────────────────────────────────────────────┐
│                    MenuBarApp                            │
│  (NSStatusItem, NSMenu, app lifecycle, permissions)      │
├─────────────────────────────────────────────────────────┤
│                                                          │
│  ┌──────────────┐  ┌──────────────┐  ┌───────────────┐  │
│  │  EventTap     │  │  HIDDevice   │  │  InputInjector│  │
│  │  Manager      │  │  Manager     │  │               │  │
│  │              │  │              │  │  CGEventPost   │  │
│  │  CGEventTap  │  │  IOHIDManager│  │  CGEventCreate │  │
│  │  mouse+kbd   │  │  VID/PID     │  │  KeyboardEvent │  │
│  └──────┬───────┘  └──────┬───────┘  └───────────────┘  │
│         │                 │                              │
│         ▼                 ▼                              │
│  ┌─────────────────────────────┐                        │
│  │     InputRouter             │                        │
│  │  (device filter + dispatch) │                        │
│  └──────────┬──────────────────┘                        │
│             ▼                                           │
│  ┌─────────────────────────────┐                        │
│  │     GestureEngine           │                        │
│  │  (state machine, timers)    │                        │
│  └──────────┬──────────────────┘                        │
│             ▼                                           │
│  ┌─────────────────────────────┐                        │
│  │     ActionDispatcher        │                        │
│  │  (keystroke, launch, etc.)  │                        │
│  └─────────────────────────────┘                        │
│                                                          │
│  ┌──────────────┐  ┌──────────────┐  ┌───────────────┐  │
│  │  ConfigManager│  │  Logger      │  │  Permissions  │  │
│  │  (JSON, v3)  │  │  (file+os)   │  │  Manager      │  │
│  └──────────────┘  └──────────────┘  └───────────────┘  │
└─────────────────────────────────────────────────────────┘
```

---

## Technology Stack

| Component | Technology | Rationale |
|-----------|-----------|-----------|
| Language | Swift 5.9+ | Native macOS, direct C API access, no interop overhead |
| UI Framework | AppKit (NSStatusItem, NSMenu) | Menu bar apps require AppKit; SwiftUI can be used for settings windows |
| Build System | Xcode / Swift Package Manager | Standard macOS toolchain |
| Minimum Target | macOS 13 (Ventura) | `SMAppService` for login items, modern Swift concurrency |
| Distribution | DMG + notarization | App Sandbox blocks CGEventTap; must distribute outside App Store |
| Architecture | Universal Binary (arm64 + x86_64) | Support both Apple Silicon and Intel Macs |

---

## Module Design

### 1. EventTapManager

**Responsibility:** Install and manage the CGEventTap for system-wide input interception.

**API Surface:**
```swift
class EventTapManager {
    /// Callback type — return nil to suppress, return event to pass through
    typealias EventHandler = (CGEvent, CGEventType) -> CGEvent?

    var onMouseEvent: EventHandler?
    var onKeyboardEvent: EventHandler?

    /// true when the tap is active and receiving events
    var isActive: Bool { get }

    func start() throws    // Creates tap, adds to run loop
    func stop()            // Removes tap from run loop, destroys
    func checkHealth()     // Detects if macOS disabled the tap, re-enables
}
```

**Implementation Details:**

```swift
// Event mask covering all mouse and keyboard events
let eventMask: CGEventMask = (
    (1 << CGEventType.leftMouseDown.rawValue) |
    (1 << CGEventType.leftMouseUp.rawValue) |
    (1 << CGEventType.rightMouseDown.rawValue) |
    (1 << CGEventType.rightMouseUp.rawValue) |
    (1 << CGEventType.otherMouseDown.rawValue) |
    (1 << CGEventType.otherMouseUp.rawValue) |
    (1 << CGEventType.scrollWheel.rawValue) |
    (1 << CGEventType.keyDown.rawValue) |
    (1 << CGEventType.keyUp.rawValue) |
    (1 << CGEventType.flagsChanged.rawValue)  // modifier key state changes
)

// Create the tap
let tap = CGEvent.tapCreate(
    tap: .cgSessionEventTap,        // session-level (like WH_*_LL)
    place: .headInsertEventTap,     // first in chain
    options: .defaultTap,           // active (can modify/suppress)
    eventsOfInterest: eventMask,
    callback: eventTapCallback,     // C function pointer
    userInfo: pointer               // reference back to EventTapManager
)

// Add to current run loop
let source = CFMachPortCreateRunLoopSource(nil, tap, 0)
CFRunLoopAddSource(CFRunLoopGetCurrent(), source, .commonModes)
CGEvent.tapEnable(tap: tap, enable: true)
```

**Injected Event Detection:**

On Windows, `LLMHF_INJECTED` flags identify synthetic events. macOS equivalent:

```swift
// Option A: Use a custom field to tag injected events
private static let injectedFieldKey = CGEventField(rawValue: 87)! // unused user field
private static let injectedMarker: Int64 = 0x4D4D4D // "MMM" marker

// When injecting:
event.setIntegerValueField(injectedFieldKey, value: injectedMarker)

// In tap callback — skip our own injected events:
if event.getIntegerValueField(injectedFieldKey) == injectedMarker {
    return Unmanaged.passRetained(event)
}
```

**Health Monitoring:**

macOS disables event taps if the callback takes too long (~300ms). The Windows version has `Reinstall()` for a similar issue. macOS approach:

```swift
func checkHealth() {
    guard let tap = eventTap else { return }
    if !CGEvent.tapIsEnabled(tap: tap) {
        Logger.log(.warning, "Event tap was disabled by macOS, re-enabling")
        CGEvent.tapEnable(tap: tap, enable: true)
    }
}
// Run on a 5-second timer
```

---

### 2. HIDDeviceManager

**Responsibility:** Enumerate HID devices, filter by VID/PID, track which device sent the most recent input.

**API Surface:**
```swift
class HIDDeviceManager {
    struct DeviceInfo {
        let vendorID: Int
        let productID: Int
        let deviceRef: IOHIDDevice
        let name: String
        let isBluetooth: Bool
        var vidPid: String { "VID_\(hex(vendorID))&PID_\(hex(productID))" }
    }

    var targetDeviceVidPid: String?      // nil = accept all devices
    var lastActiveDeviceVidPid: String?   // updated on each input event
    var onDeviceActivity: ((String) -> Void)?  // fires with VID_PID on input

    func start()                          // Opens HID manager, begins monitoring
    func stop()                           // Closes HID manager
    func enumerateDevices() -> [DeviceInfo]
    func isFromTargetDevice() -> Bool     // Checks lastActive against target
}
```

**Implementation Details:**

```swift
let manager = IOHIDManagerCreate(kCFAllocatorDefault, IOOptionBits(kIOHIDOptionsTypeNone))

// Register for mouse-type devices only
let matchDicts: [[String: Any]] = [
    [kIOHIDDeviceUsagePageKey: kHIDPage_GenericDesktop, kIOHIDDeviceUsageKey: kHIDUsage_GD_Mouse],
    [kIOHIDDeviceUsagePageKey: kHIDPage_GenericDesktop, kIOHIDDeviceUsageKey: kHIDUsage_GD_Pointer]
]
IOHIDManagerSetDeviceMatchingMultiple(manager, matchDicts as CFArray)

// Per-device input callback — fires on every HID report
IOHIDManagerRegisterInputValueCallback(manager, { context, result, sender, value in
    let self = Unmanaged<HIDDeviceManager>.fromOpaque(context!).takeUnretainedValue()
    guard let device = sender else { return }
    let vendorID = IOHIDDeviceGetProperty(device, kIOHIDVendorIDKey as CFString) as? Int ?? 0
    let productID = IOHIDDeviceGetProperty(device, kIOHIDProductIDKey as CFString) as? Int ?? 0
    self.lastActiveDeviceVidPid = "VID_\(hex(vendorID))&PID_\(hex(productID))"
    self.onDeviceActivity?(self.lastActiveDeviceVidPid!)
}, pointer)

IOHIDManagerScheduleWithRunLoop(manager, CFRunLoopGetCurrent(), CFRunLoopMode.commonModes.rawValue)
IOHIDManagerOpen(manager, IOOptionBits(kIOHIDOptionsTypeNone))
```

**Bluetooth Detection:**
```swift
func isBluetooth(device: IOHIDDevice) -> Bool {
    let transport = IOHIDDeviceGetProperty(device, kIOHIDTransportKey as CFString) as? String
    return transport?.lowercased().contains("bluetooth") ?? false
}
```

**Device-Event Correlation (differs from Windows):**

On Windows, Raw Input and LL hooks are separate systems that must be correlated by timing. On macOS, we have two options:

1. **IOHIDManager callback fires before CGEventTap** — update `lastActiveDeviceVidPid`, then the CGEventTap callback reads it. This mirrors the Windows approach (RawInput WM_INPUT fires before LL hook callback).

2. **Attach device info to CGEvent** — use `CGEventSetIntegerValueField` with a custom field to tag events. Less reliable since CGEvents don't carry HID source info natively.

**Recommended: Option 1** — same pattern as Windows. The IOHIDManager input value callback fires on the same run loop, and empirically fires before (or concurrently with) the CGEventTap callback for the same physical event. Use a timestamp-based correlation window (e.g., 50ms) as a safety margin, same as the Windows implementation.

---

### 3. InputRouter

**Responsibility:** Bridge between raw events and the gesture engine, applying device filtering.

```swift
class InputRouter {
    let hidManager: HIDDeviceManager
    let gestureEngine: GestureEngine

    /// Called from EventTapManager callbacks.
    /// Returns true if the event should be suppressed.
    func processMouseEvent(_ event: CGEvent, type: CGEventType) -> Bool {
        // 1. Check device filter
        guard hidManager.targetDeviceVidPid == nil ||
              hidManager.isFromTargetDevice() else {
            return false  // Not from target device, pass through
        }

        // 2. Convert to InputEvent
        let inputEvent = InputEvent(
            type: .mouseButton,
            state: type.isDown ? .down : .up,
            button: resolveMouseButton(event, type),
            deviceId: hidManager.lastActiveDeviceVidPid ?? "",
            timestamp: UInt32(event.timestamp / 1_000_000) // ns → ms
        )

        // 3. Feed to gesture engine
        return gestureEngine.processInput(inputEvent)
    }
}
```

**Mouse Button Resolution:**
```swift
func resolveMouseButton(_ event: CGEvent, _ type: CGEventType) -> MouseButton {
    switch type {
    case .leftMouseDown, .leftMouseUp:      return .left
    case .rightMouseDown, .rightMouseUp:    return .right
    case .otherMouseDown, .otherMouseUp:
        let buttonNumber = event.getIntegerValueField(.mouseEventButtonNumber)
        switch buttonNumber {
        case 2: return .middle
        case 3: return .xButton1
        case 4: return .xButton2
        default: return .unknown(Int(buttonNumber))
        }
    case .scrollWheel: return .wheel
    default: return .unknown(-1)
    }
}
```

---

### 4. GestureEngine

**Responsibility:** The core state machine. This is a direct port of the Windows `GestureEngine.cs`.

The logic is platform-agnostic. Port the following from the C# version:

**Data Structures:**
```swift
struct InputEvent {
    let type: InputType           // .mouseButton, .keyPress
    let state: PressState         // .down, .up
    let button: MouseButton?      // for mouse events
    let virtualKeyCode: UInt16?   // for keyboard events (macOS key codes)
    let deviceId: String
    let timestamp: UInt32

    var inputKey: String {
        switch type {
        case .mouseButton: return "Mouse.\(button!)"
        case .keyPress: return "Key.\(keyName(virtualKeyCode!))"
        }
    }
}

class GestureTracker {
    var lastDownTimestamp: UInt32 = 0
    var lastUpTimestamp: UInt32 = 0
    var pressCount: Int = 0
    var holdFired: Bool = false
    var deviceId: String = ""
}

class SequenceTracker {
    var currentIndex: Int = 0
    var startTimestamp: UInt32 = 0
    var deviceId: String = ""
}
```

**Core Logic to Port (preserve exactly):**

1. **ProcessInput()** — routes Down events to chord/press checks, Up events to press/sequence checks
2. **RecordPressDown()** — increments press count, resets if gap exceeds multi-press window
3. **CheckPressGestures()** — disambiguation between SinglePress and MultiPress using delayed timers
4. **CheckHoldGestures()** — 50ms timer checks held keys against LongHold thresholds
5. **CheckChordGestures()** — all chord keys must be in `currentlyHeld` set
6. **CheckSequenceGestures()** — ordered key tracking with timeout and device consistency
7. **ShouldSuppressDown()** — suppresses Down events for gestures with `suppressInput = true`
8. **ScheduleSinglePress()** — deferred recognition timer for single-press disambiguation
9. **UpdateGestures()** — atomic mode switch: clears all state, disposes timers, swaps gesture list

**Timing:**
- Use `mach_absolute_time()` converted to milliseconds, or `ProcessInfo.processInfo.systemUptime * 1000`
- Hold check timer: `DispatchSource.makeTimerSource` with 50ms interval
- Single-press delay timer: `DispatchWorkItem` scheduled on main queue

**Critical Behaviors to Preserve:**
- Device-specific gestures sorted before wildcard (Any Device) gestures
- Multi-press window reset when gap between presses exceeds threshold
- Hold gesture fires only once per hold (`holdFired` flag)
- SinglePress delayed if any MultiPress exists on the same key
- Sequence resets on timeout/device-mismatch but re-checks if current key starts sequence
- Chord triggers on final key down (when all chord keys are held)
- Mode switch clears ALL state (trackers, held keys, timers)

---

### 5. Action System

**Protocol:**
```swift
protocol Action {
    func execute() async throws
}
```

**Implementations:**

#### KeystrokeAction
```swift
class KeystrokeAction: Action {
    let keystroke: String  // e.g., "Cmd+Shift+Z"

    func execute() async throws {
        let keys = keystroke.split(separator: "+")
        var flags: CGEventFlags = []
        var keyCode: CGKeyCode = 0

        for key in keys {
            switch key.trimmingCharacters(in: .whitespaces).lowercased() {
            case "cmd", "command":   flags.insert(.maskCommand)
            case "shift":            flags.insert(.maskShift)
            case "alt", "option":    flags.insert(.maskAlternate)
            case "ctrl", "control":  flags.insert(.maskControl)
            case "fn":               flags.insert(.maskSecondaryFn)
            default:                 keyCode = resolveKeyCode(String(key))
            }
        }

        let down = CGEvent(keyboardEventSource: nil, virtualKey: keyCode, keyDown: true)!
        down.flags = flags
        tagAsInjected(down)
        down.post(tap: .cghidEventTap)

        let up = CGEvent(keyboardEventSource: nil, virtualKey: keyCode, keyDown: false)!
        up.flags = flags
        tagAsInjected(up)
        up.post(tap: .cghidEventTap)
    }
}
```

**macOS Virtual Key Codes (subset):**
```swift
// Unlike Windows VK_ codes, macOS uses hardware key codes
// These are from Events.h / Carbon HIToolbox
let keyCodeMap: [String: CGKeyCode] = [
    "a": 0x00, "s": 0x01, "d": 0x02, "f": 0x03, "h": 0x04,
    "g": 0x05, "z": 0x06, "x": 0x07, "c": 0x08, "v": 0x09,
    "b": 0x0B, "q": 0x0C, "w": 0x0D, "e": 0x0E, "r": 0x0F,
    "y": 0x10, "t": 0x11, "1": 0x12, "2": 0x13, "3": 0x14,
    "4": 0x15, "6": 0x16, "5": 0x17, "9": 0x19, "7": 0x1A,
    "8": 0x1C, "0": 0x1D, "return": 0x24, "tab": 0x30,
    "space": 0x31, "delete": 0x33, "escape": 0x35,
    "f1": 0x7A, "f2": 0x78, "f3": 0x63, "f4": 0x76,
    "f5": 0x60, "f6": 0x61, "f7": 0x62, "f8": 0x64,
    "f9": 0x65, "f10": 0x6D, "f11": 0x67, "f12": 0x6F,
    "f13": 0x69, "f14": 0x6B, "f15": 0x71, "f16": 0x6A,
    "up": 0x7E, "down": 0x7D, "left": 0x7B, "right": 0x7C,
    "volumeup": 0x48, "volumedown": 0x49, "mute": 0x4A,
    // ... complete list in implementation
]
```

#### LaunchAction
```swift
class LaunchAction: Action {
    let path: String
    let arguments: String?

    func execute() async throws {
        if arguments == nil {
            NSWorkspace.shared.open(URL(fileURLWithPath: path))
        } else {
            let process = Process()
            process.executableURL = URL(fileURLWithPath: path)
            process.arguments = arguments?.split(separator: " ").map(String.init)
            try process.run()
        }
    }
}
```

#### WebhookAction
```swift
class WebhookAction: Action {
    let url: String
    let httpMethod: String
    let body: String?

    func execute() async throws {
        var request = URLRequest(url: URL(string: url)!)
        request.httpMethod = httpMethod
        request.timeoutInterval = 10
        if let body, ["POST", "PUT", "PATCH"].contains(httpMethod.uppercased()) {
            request.httpBody = body.data(using: .utf8)
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        }
        let (_, response) = try await URLSession.shared.data(for: request)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        Logger.log(.info, "Webhook \(httpMethod) \(url) → \(status)")
    }
}
```

#### ShellScriptAction
```swift
class ShellScriptAction: Action {
    let path: String
    let arguments: String?

    func execute() async throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/zsh")
        process.arguments = ["-c", "\(path) \(arguments ?? "")"]
        process.standardOutput = Pipe()
        process.standardError = Pipe()
        try process.run()
        process.waitUntilExit()
        Logger.log(.info, "Shell script exited with code \(process.terminationStatus)")
    }
}
```

#### NotificationAction
```swift
class NotificationAction: Action {
    let message: String

    func execute() async throws {
        let content = UNMutableNotificationContent()
        content.title = "Mighty Mini Mouse"
        content.body = message
        let request = UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil)
        try await UNUserNotificationCenter.current().add(request)
    }
}
```

---

### 6. ConfigManager

**Responsibility:** Load, save, and migrate `config.json`. Direct port of Windows `ConfigManager.cs`.

```swift
class ConfigManager {
    static let appSupportDir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
        .appendingPathComponent("MightyMiniMouse")

    static let configPath = appSupportDir.appendingPathComponent("config.json")

    func load() -> AppConfig
    func save(_ config: AppConfig)
    func migrateIfNeeded(_ config: inout AppConfig) -> Bool
}
```

**Config Models (Codable):**
```swift
struct AppConfig: Codable {
    var configVersion: Int = 3
    var targetDevice: DeviceConfig = DeviceConfig()
    var modes: [ModeDefinition] = []
    var activeModeId: String?
    var knownDevices: [MouseDevice] = []
    var logging: LoggingConfig = LoggingConfig()
    // startWithWindows ignored on macOS
}

struct ModeDefinition: Codable {
    var id: String = UUID().uuidString
    var name: String
    var gestures: [GestureDefinition] = []
}

struct GestureDefinition: Codable {
    var id: String = UUID().uuidString
    var name: String = ""
    var type: GestureType
    var inputKeys: [String] = []
    var pressCount: Int = 1
    var timeWindowMs: Int = 400
    var suppressInput: Bool = true
    var targetDeviceId: String?
    var action: ActionConfig
}

struct ActionConfig: Codable {
    var type: String           // "keystroke", "launch", "webhook", "shell", "notification"
    var keystroke: String?
    var path: String?
    var arguments: String?
    var url: String?
    var httpMethod: String?
    var body: String?
    var message: String?
}
```

**Migration Logic:**
- v1 → v2: Wrap flat `gestures` array into a "Default" mode
- v2 → v3: Ensure `logging.suppressedCategories` exists with defaults `["MouseMove", "RawInput"]`
- Same chain as Windows — config files portable across platforms

**JSON Encoding:**
```swift
let encoder = JSONEncoder()
encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
encoder.keyEncodingStrategy = .convertToSnakeCase  // or use CodingKeys for camelCase
// Use camelCase to match Windows config format
```

---

### 7. Logger

**Responsibility:** Thread-safe file + console logging with category filtering.

```swift
class Logger {
    enum Level: Int, Comparable { case debug, info, warning, error, fatal }
    enum Category: String { case mouseMove, rawInput, mouseButton, keyHook, gestureEngine, action, lifecycle, config, device }

    static let shared = Logger()

    var logLevel: Level = .info
    var suppressedCategories: Set<String> = ["MouseMove", "RawInput"]
    private var fileHandle: FileHandle?

    func log(_ level: Level, _ category: Category? = nil, _ message: String) {
        guard level >= logLevel else { return }
        if let cat = category, suppressedCategories.contains(cat.rawValue) { return }
        let line = "[\(timestamp())] [\(level)] \(message)"
        fileHandle?.write(line.data(using: .utf8)!)
        #if DEBUG
        print(line)
        #endif
    }
}
```

---

### 8. PermissionsManager

**Responsibility:** Check and request macOS permissions, guide user through granting.

```swift
class PermissionsManager {
    /// Check if Accessibility permission is granted
    var isAccessibilityGranted: Bool {
        AXIsProcessTrusted()
    }

    /// Prompt the system permission dialog
    func promptAccessibility() {
        let options = [kAXTrustedCheckOptionPrompt.takeRetainedValue(): true] as CFDictionary
        AXIsProcessTrustedWithOptions(options)
    }

    /// Open System Settings to Accessibility pane
    func openAccessibilitySettings() {
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")!)
    }

    /// Poll for permission grant (call on timer after prompting)
    func waitForPermission(completion: @escaping () -> Void) {
        Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { timer in
            if AXIsProcessTrusted() {
                timer.invalidate()
                completion()
            }
        }
    }
}
```

---

## Threading Model

```
Main Thread (NSApplication.run / CFRunLoop)
├── CGEventTap callback (fires on main run loop)
├── IOHIDManager input callback (fires on main run loop)
├── NSMenu actions
├── Timer callbacks (hold check, health check)
│
└── Async action dispatch
    └── Task { await action.execute() }  // Swift structured concurrency
```

**Key Threading Rules:**
1. CGEventTap callback **must return quickly** (< 300ms or macOS disables the tap)
2. IOHIDManager callbacks fire on the same run loop as the tap — device ID is updated before tap callback processes the same physical event
3. Action execution is dispatched asynchronously via `Task {}` — never blocks the tap callback
4. Gesture engine state is accessed only from the main thread (single-threaded, same as Windows message pump model)
5. Config file I/O can be synchronous (only happens on user action, not in hot path)

---

## Key Code Mapping

macOS uses hardware-based key codes (not virtual key codes like Windows). The config file uses platform-neutral string keys (`"Key.F13"`, `"Mouse.XButton1"`). Each platform maps these to native codes internally.

**Platform Translation Table (examples):**

| Config Key | Windows VK | macOS Key Code |
|-----------|-----------|---------------|
| `Key.A` | `0x41` | `0x00` |
| `Key.Z` | `0x5A` | `0x06` |
| `Key.Enter` | `0x0D` | `0x24` |
| `Key.Escape` | `0x1B` | `0x35` |
| `Key.F1` | `0x70` | `0x7A` |
| `Key.F13` | `0x7C` | `0x69` |
| `Key.VolumeUp` | `0xAF` | `0x48` |
| `Key.Space` | `0x20` | `0x31` |
| `Mouse.Left` | Button 0 | Button 0 |
| `Mouse.Right` | Button 1 | Button 1 |
| `Mouse.Middle` | Button 2 | Button 2 |
| `Mouse.XButton1` | Button 3 | Button 3 |
| `Mouse.XButton2` | Button 4 | Button 4 |

---

## Error Handling Strategy

Mirrors the Windows approach — catch and log, never crash:

1. **EventTap callback** — wrap in do/catch, log errors, always return the event (never suppress on error)
2. **Action execution** — catch all errors, log with category `.action`
3. **Config loading** — fall back to defaults if JSON is malformed, log warning
4. **Permission loss** — detect disabled tap, show menu bar alert, prompt re-authorization
5. **Device disconnection** — HID manager fires removal callback, clear target state, continue accepting all input

---

## Build & Distribution

### Xcode Project Structure
```
MightyMiniMouse-macOS/
├── MightyMiniMouse.xcodeproj
├── Sources/
│   ├── App/
│   │   ├── AppDelegate.swift          // NSApplicationDelegate, lifecycle
│   │   ├── MenuBarController.swift    // NSStatusItem, NSMenu
│   │   └── PermissionsManager.swift   // Accessibility permission flow
│   ├── Input/
│   │   ├── EventTapManager.swift      // CGEventTap wrapper
│   │   ├── HIDDeviceManager.swift     // IOHIDManager wrapper
│   │   └── InputRouter.swift          // Device filter + dispatch
│   ├── Gestures/
│   │   ├── GestureEngine.swift        // State machine (port from C#)
│   │   ├── GestureDefinition.swift    // Config model
│   │   ├── GestureState.swift         // Tracker structs
│   │   └── InputEvent.swift           // Unified input event
│   ├── Actions/
│   │   ├── Action.swift               // Protocol
│   │   ├── ActionFactory.swift        // Type → implementation
│   │   ├── KeystrokeAction.swift
│   │   ├── LaunchAction.swift
│   │   ├── WebhookAction.swift
│   │   ├── ShellScriptAction.swift
│   │   └── NotificationAction.swift
│   ├── Config/
│   │   ├── AppConfig.swift            // Codable models
│   │   ├── ConfigManager.swift        // Load/save/migrate
│   │   └── MouseDevice.swift
│   └── Logging/
│       └── Logger.swift
├── Resources/
│   ├── Assets.xcassets/               // Menu bar icon (template image)
│   ├── Info.plist
│   └── MightyMiniMouse.entitlements
└── Tests/
    ├── GestureEngineTests.swift
    └── ConfigMigrationTests.swift
```

### Entitlements
```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "...">
<plist version="1.0">
<dict>
    <!-- NO app sandbox — required for CGEventTap -->
    <key>com.apple.security.app-sandbox</key>
    <false/>
</dict>
</plist>
```

### Info.plist Keys
```xml
<!-- Menu bar app (no dock icon) -->
<key>LSUIElement</key>
<true/>

<!-- App description for Accessibility permission dialog -->
<key>NSAccessibilityUsageDescription</key>
<string>Mighty Mini Mouse needs Accessibility access to intercept and remap mouse button presses from your Bluetooth device.</string>
```

### Notarization (for distribution outside App Store)
```bash
# Build archive
xcodebuild archive -scheme MightyMiniMouse -archivePath build/MightyMiniMouse.xcarchive

# Sign
codesign --deep --force --options runtime --sign "Developer ID Application: ..." build/MightyMiniMouse.app

# Notarize
xcrun notarytool submit build/MightyMiniMouse.zip --apple-id "..." --team-id "..." --password "..."

# Staple
xcrun stapler staple build/MightyMiniMouse.app

# Create DMG
hdiutil create -volname "Mighty Mini Mouse" -srcfolder build/MightyMiniMouse.app -ov build/MightyMiniMouse.dmg
```
