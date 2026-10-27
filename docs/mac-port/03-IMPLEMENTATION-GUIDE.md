# Mighty Mini Mouse for macOS — Implementation Guide

This guide provides a phased implementation plan with detailed instructions for each step. It's ordered to build foundational layers first and stack features on top, so you always have something runnable.

---

## Phase 1: Skeleton Menu Bar App

**Goal:** Running menu bar app with icon, dropdown menu, and quit functionality.

### Steps

1. **Create Xcode project**
   - macOS App template, Swift, no storyboard (programmatic UI)
   - Set `LSUIElement = true` in Info.plist (hides dock icon)
   - Remove the default window/view controller

2. **AppDelegate setup**
   ```swift
   @main
   class AppDelegate: NSObject, NSApplicationDelegate {
       private var statusItem: NSStatusItem!

       func applicationDidFinishLaunching(_ notification: Notification) {
           statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)

           if let button = statusItem.button {
               // Use SF Symbol for menu bar icon
               button.image = NSImage(systemSymbolName: "computermouse.fill", accessibilityDescription: "Mighty Mini Mouse")
           }

           let menu = NSMenu()
           menu.addItem(NSMenuItem(title: "Enabled", action: #selector(toggleEnabled), keyEquivalent: ""))
           menu.addItem(.separator())
           menu.addItem(NSMenuItem(title: "Quit", action: #selector(quit), keyEquivalent: "q"))
           statusItem.menu = menu
       }

       @objc func toggleEnabled(_ sender: NSMenuItem) {
           sender.state = sender.state == .on ? .off : .on
       }

       @objc func quit(_ sender: NSMenuItem) {
           NSApplication.shared.terminate(self)
       }
   }
   ```

3. **Verify:** App shows in menu bar, dropdown works, no dock icon.

### Lesson from Windows
The Windows version uses `Application.Run()` with no main form — the `NotifyIcon` is the entire UI. Same principle here: `NSApplication.run()` with no window, just the status item.

---

## Phase 2: Accessibility Permission Flow

**Goal:** Check for Accessibility permission, guide user to grant it, detect when granted.

### Steps

1. **Create PermissionsManager**
   ```swift
   import ApplicationServices

   class PermissionsManager {
       static let shared = PermissionsManager()

       var isAccessibilityGranted: Bool {
           AXIsProcessTrusted()
       }

       func promptForAccessibility() {
           let options = [kAXTrustedCheckOptionPrompt.takeRetainedValue(): true] as CFDictionary
           AXIsProcessTrustedWithOptions(options)
       }

       func waitForGrant(onGranted: @escaping () -> Void) {
           guard !isAccessibilityGranted else {
               onGranted()
               return
           }
           Timer.scheduledTimer(withTimeInterval: 1.0, repeats: true) { timer in
               if AXIsProcessTrusted() {
                   timer.invalidate()
                   onGranted()
               }
           }
       }
   }
   ```

2. **Wire into AppDelegate**
   ```swift
   func applicationDidFinishLaunching(_ notification: Notification) {
       setupMenuBar()

       if PermissionsManager.shared.isAccessibilityGranted {
           initializeInputPipeline()
       } else {
           PermissionsManager.shared.promptForAccessibility()
           PermissionsManager.shared.waitForGrant { [weak self] in
               self?.initializeInputPipeline()
               self?.updateMenuBarIcon(enabled: true)
           }
       }
   }
   ```

3. **Visual feedback:** Gray icon while waiting for permission, blue/colored when active.

### Gotcha
During development, every new build may require re-granting Accessibility permission because the code signature changes. To avoid this, consistently sign with the same certificate or add your Xcode build output path to Accessibility once and don't clean the build folder.

---

## Phase 3: CGEventTap — Input Interception

**Goal:** Intercept all mouse and keyboard events system-wide, log them, pass through.

### Steps

1. **Create EventTapManager**

   The CGEventTap callback must be a C function (not a closure). Use `Unmanaged` pointer to reference the manager instance.

   ```swift
   class EventTapManager {
       private var eventTap: CFMachPort?
       private var runLoopSource: CFRunLoopSource?

       var onEvent: ((CGEvent, CGEventType) -> CGEvent?)?

       func start() throws {
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
               (1 << CGEventType.flagsChanged.rawValue)
           )

           let userInfo = Unmanaged.passUnretained(self).toOpaque()

           guard let tap = CGEvent.tapCreate(
               tap: .cgSessionEventTap,
               place: .headInsertEventTap,
               options: .defaultTap,
               eventsOfInterest: eventMask,
               callback: EventTapManager.tapCallback,
               userInfo: userInfo
           ) else {
               throw EventTapError.failedToCreate
           }

           self.eventTap = tap
           self.runLoopSource = CFMachPortCreateRunLoopSource(nil, tap, 0)
           CFRunLoopAddSource(CFRunLoopGetCurrent(), runLoopSource, .commonModes)
           CGEvent.tapEnable(tap: tap, enable: true)
       }

       func stop() {
           if let tap = eventTap {
               CGEvent.tapEnable(tap: tap, enable: false)
           }
           if let source = runLoopSource {
               CFRunLoopRemoveSource(CFRunLoopGetCurrent(), source, .commonModes)
           }
           eventTap = nil
           runLoopSource = nil
       }

       // MUST be a static/global C-compatible function
       private static let tapCallback: CGEventTapCallBack = {
           proxy, type, event, userInfo -> Unmanaged<CGEvent>? in

           // Handle tap disabled by system
           if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
               let manager = Unmanaged<EventTapManager>.fromOpaque(userInfo!).takeUnretainedValue()
               if let tap = manager.eventTap {
                   CGEvent.tapEnable(tap: tap, enable: true)
               }
               return Unmanaged.passRetained(event)
           }

           let manager = Unmanaged<EventTapManager>.fromOpaque(userInfo!).takeUnretainedValue()

           if let result = manager.onEvent?(event, type) {
               return Unmanaged.passRetained(result)
           } else {
               return nil  // suppress the event
           }
       }
   }
   ```

2. **Test with logging**
   ```swift
   eventTapManager.onEvent = { event, type in
       print("Event: \(type.rawValue), button: \(event.getIntegerValueField(.mouseEventButtonNumber))")
       return event  // pass through everything for now
   }
   ```

3. **Verify:** Console shows all mouse clicks and key presses. Normal input still works.

### Lessons from Windows

- **Injected event detection:** The Windows version checks `LLMHF_INJECTED`. On macOS, tag injected events with a custom integer field (field 87 is unused) and check it in the callback. This prevents infinite loops when `KeystrokeAction` fires.

- **Health monitoring:** The Windows `KeyboardHook` has `Reinstall()` because Windows silently removes hooks. macOS equivalent: the tap callback receives `tapDisabledByTimeout` when callbacks are too slow. Handle this by re-enabling the tap immediately (shown above).

- **Return quickly:** The Windows version catches exceptions in hook callbacks to prevent crashes. Same rule applies — the tap callback must return fast. Never do I/O or network calls in the callback. Dispatch async.

---

## Phase 4: IOHIDManager — Device Filtering

**Goal:** Identify which physical device sent each input event.

### Steps

1. **Create HIDDeviceManager**

   ```swift
   import IOKit
   import IOKit.hid

   class HIDDeviceManager {
       struct DeviceInfo: Identifiable {
           let id: String  // "VID_XXXX&PID_XXXX"
           let vendorID: Int
           let productID: Int
           let name: String
           let isBluetooth: Bool
       }

       private var manager: IOHIDManager?
       private(set) var lastActiveDeviceVidPid: String?
       var targetVidPid: String?  // nil = accept all
       var onDeviceActivity: ((String) -> Void)?

       func start() {
           manager = IOHIDManagerCreate(kCFAllocatorDefault, IOOptionBits(kIOHIDOptionsTypeNone))
           guard let manager else { return }

           // Match mouse and pointer devices
           let matchDicts: [[String: Any]] = [
               [kIOHIDDeviceUsagePageKey as String: 0x01, kIOHIDDeviceUsageKey as String: 0x02], // Mouse
               [kIOHIDDeviceUsagePageKey as String: 0x01, kIOHIDDeviceUsageKey as String: 0x01], // Pointer
           ]
           IOHIDManagerSetDeviceMatchingMultiple(manager, matchDicts as CFArray)

           let context = Unmanaged.passUnretained(self).toOpaque()

           // Input value callback — fires on every HID report from matched devices
           IOHIDManagerRegisterInputValueCallback(manager, { context, result, sender, value in
               guard let context, let sender else { return }
               let mgr = Unmanaged<HIDDeviceManager>.fromOpaque(context).takeUnretainedValue()
               let device = Unmanaged<IOHIDDevice>.fromOpaque(sender).takeUnretainedValue()
               let vid = IOHIDDeviceGetProperty(device, kIOHIDVendorIDKey as CFString) as? Int ?? 0
               let pid = IOHIDDeviceGetProperty(device, kIOHIDProductIDKey as CFString) as? Int ?? 0
               let vidPid = String(format: "VID_%04X&PID_%04X", vid, pid)
               mgr.lastActiveDeviceVidPid = vidPid
               mgr.onDeviceActivity?(vidPid)
           }, context)

           // Device connect/disconnect callbacks
           IOHIDManagerRegisterDeviceMatchingCallback(manager, { context, result, sender, device in
               // Device connected — log it
           }, context)

           IOHIDManagerRegisterDeviceRemovalCallback(manager, { context, result, sender, device in
               // Device disconnected — clear target if it was this device
           }, context)

           IOHIDManagerScheduleWithRunLoop(manager, CFRunLoopGetCurrent(), CFRunLoopMode.commonModes.rawValue)
           IOHIDManagerOpen(manager, IOOptionBits(kIOHIDOptionsTypeNone))
       }

       func isFromTargetDevice() -> Bool {
           guard let target = targetVidPid else { return true }  // no filter = accept all
           return lastActiveDeviceVidPid == target
       }

       func enumerateDevices() -> [DeviceInfo] {
           guard let manager else { return [] }
           guard let devices = IOHIDManagerCopyDevices(manager) as? Set<IOHIDDevice> else { return [] }

           var seen = Set<String>()
           return devices.compactMap { device in
               let vid = IOHIDDeviceGetProperty(device, kIOHIDVendorIDKey as CFString) as? Int ?? 0
               let pid = IOHIDDeviceGetProperty(device, kIOHIDProductIDKey as CFString) as? Int ?? 0
               let vidPid = String(format: "VID_%04X&PID_%04X", vid, pid)
               guard seen.insert(vidPid).inserted else { return nil }  // dedupe

               let name = IOHIDDeviceGetProperty(device, kIOHIDProductKey as CFString) as? String ?? "Unknown"
               let transport = IOHIDDeviceGetProperty(device, kIOHIDTransportKey as CFString) as? String ?? ""
               let isBT = transport.lowercased().contains("bluetooth")

               return DeviceInfo(id: vidPid, vendorID: vid, productID: pid, name: name, isBluetooth: isBT)
           }
       }
   }
   ```

2. **Wire device filtering into EventTap callback**
   ```swift
   eventTapManager.onEvent = { [weak self] event, type in
       guard let self else { return event }
       // Device filter (IOHIDManager callback fires first on same run loop)
       guard hidDeviceManager.isFromTargetDevice() else {
           return event  // not target device, pass through
       }
       // ... gesture processing
   }
   ```

### Lesson from Windows
The Windows version correlates Raw Input and LL hooks by timing — `WM_INPUT` fires before the LL hook callback for the same physical event. On macOS, `IOHIDManager` input value callbacks and `CGEventTap` both fire on the same run loop. Empirically, the HID callback fires first. If you encounter ordering issues, add a small timestamp correlation window (the Windows version implicitly relies on this).

The Windows `RawInputManager` caches device paths to avoid repeated lookups. Do the same — `IOHIDDeviceGetProperty` calls are cheap but there's no reason to call them on every event. Cache VID/PID per device reference.

---

## Phase 5: Gesture Engine Port

**Goal:** Port the C# GestureEngine to Swift with identical behavior.

### Approach

This is a line-by-line port. The gesture engine is the most complex and most critical component. Do not redesign it — preserve the exact same logic, edge cases, and timing behavior.

### Key Mapping Decisions

1. **InputEvent model:**
   ```swift
   enum InputType { case mouseButton, keyPress }
   enum PressState { case down, up }
   enum MouseButton: String { case left, right, middle, xButton1, xButton2, wheel }

   struct InputEvent {
       let type: InputType
       let state: PressState
       let button: MouseButton?
       let virtualKeyCode: UInt16?
       let deviceId: String
       let timestamp: UInt32  // milliseconds

       var inputKey: String {
           switch type {
           case .mouseButton: return "Mouse.\(button!.rawValue.capitalizedFirst)"
           case .keyPress: return "Key.\(macOSKeyName(virtualKeyCode!))"
           }
       }
   }
   ```

2. **Timestamp source:** Use `ProcessInfo.processInfo.systemUptime * 1000` truncated to `UInt32`. This gives millisecond precision similar to Windows `Environment.TickCount`.

3. **Timer implementations:**
   - Hold check (50ms repeating): `DispatchSource.makeTimerSource(queue: .main)`
   - Single-press delay: `DispatchWorkItem` scheduled via `DispatchQueue.main.asyncAfter`

### Porting Checklist

Port each method one-for-one from `GestureEngine.cs`:

- [ ] `processInput(InputEvent) -> Bool` — main entry point
- [ ] `recordPressDown(inputKey, timestamp, deviceId)` — press counting
- [ ] `checkPressGestures(inputKey, ...) -> Bool` — single/multi-press with disambiguation
- [ ] `checkHoldGestures()` — timer-driven long-hold detection
- [ ] `checkChordGestures(inputKey, deviceId) -> Bool` — simultaneous keys
- [ ] `checkSequenceGestures(inputKey, timestamp, deviceId) -> Bool` — ordered sequences
- [ ] `shouldSuppressDown(inputKey, deviceId) -> Bool` — down-event suppression
- [ ] `scheduleSinglePress(gesture, inputKey, delayMs)` — deferred recognition
- [ ] `updateGestures(newGestures, modeName)` — mode switching

### Critical Behaviors (do not change)

1. **Gesture priority:** Device-specific gestures sort before Any Device gestures
2. **Multi-press window:** `PressCount` resets when gap between Up and next Down exceeds the window
3. **Hold fires once:** `holdFired` flag prevents repeated recognition during single hold
4. **SinglePress delay:** Only delayed if a MultiPress gesture exists on the same key
5. **Sequence restart:** If an out-of-order key matches the first key in a sequence, restart the sequence immediately
6. **Chord on last key:** Recognition triggers when the final chord key enters `currentlyHeld`
7. **Mode switch cleanup:** Clear ALL state — trackers, held keys, pending timers, sequence progress

### Test Cases to Validate

Write unit tests for these scenarios (the Windows version doesn't have tests — this is an opportunity to add them):

1. SinglePress fires after delay when MultiPress exists on same key
2. SinglePress fires immediately when no MultiPress exists on same key
3. MultiPress fires immediately on Nth press, cancels pending SinglePress
4. LongHold fires after threshold, only once per hold
5. LongHold does NOT fire if button released before threshold
6. Sequence completes in order within time window
7. Sequence resets on timeout
8. Sequence resets on wrong key but restarts if wrong key is the first sequence key
9. Chord fires when all keys are held simultaneously
10. Mode switch clears all pending state
11. Device-specific gesture matches before Any Device gesture
12. SinglePress does NOT fire if hold duration > 300ms

---

## Phase 6: Action System

**Goal:** Implement all 5 action types.

### Implementation Order

1. **NotificationAction** — easiest to test visually
2. **KeystrokeAction** — core functionality, needs key code map
3. **LaunchAction** — simple process launch
4. **WebhookAction** — HTTP client
5. **ShellScriptAction** — process with zsh

### KeystrokeAction — The Tricky One

**macOS key codes are completely different from Windows VK codes.** You need a mapping table from config key names to macOS CGKeyCode values.

Build a bidirectional map:
```swift
// Config string → macOS key code
let keyCodeFromName: [String: CGKeyCode] = [
    "A": 0x00, "S": 0x01, "D": 0x02, "F": 0x03, ...
    "Return": 0x24, "Tab": 0x30, "Space": 0x31, "Delete": 0x33,
    "Escape": 0x35, "F1": 0x7A, "F2": 0x78, ...
]

// macOS key code → config string (for input recording)
let nameFromKeyCode: [CGKeyCode: String] = Dictionary(uniqueKeysWithValues:
    keyCodeFromName.map { ($0.value, $0.key) }
)
```

**Modifier key handling:**
```swift
// Config uses platform-neutral names with macOS aliases
// "Cmd+Z", "Command+Z" → .maskCommand
// "Ctrl+C", "Control+C" → .maskControl
// "Alt+Tab", "Option+Tab" → .maskAlternate
// "Shift+A" → .maskShift
```

**Injected event tagging (critical):**
```swift
func tagAsInjected(_ event: CGEvent) {
    event.setIntegerValueField(CGEventField(rawValue: 87)!, value: 0x4D4D4D)
}

func isInjected(_ event: CGEvent) -> Bool {
    event.getIntegerValueField(CGEventField(rawValue: 87)!) == 0x4D4D4D
}
```

### Action Dispatch Pattern

Mirror the Windows pattern — dispatch async from gesture recognition:

```swift
gestureEngine.onGestureRecognized = { gesture in
    Logger.log(.info, .action, "Gesture recognized: \(gesture.name)")
    Task { @MainActor in
        do {
            let action = ActionFactory.create(from: gesture.action)
            try await action.execute()
        } catch {
            Logger.log(.error, .action, "Action failed: \(error)")
        }
    }
}
```

---

## Phase 7: Config System

**Goal:** Load/save/migrate config.json, compatible with Windows format.

### Steps

1. **Define Codable models** matching the Windows schema exactly (camelCase JSON keys)
2. **Implement ConfigManager** with load/save/migrate
3. **Migration chain:** v1 → v2 → v3 (same as Windows)
4. **File watcher:** Use `DispatchSource.makeFileSystemObjectSource` to detect external config edits and reload

### Config File Location
```swift
let appSupport = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
let configDir = appSupport.appendingPathComponent("MightyMiniMouse")
// ~/.Library/Application Support/MightyMiniMouse/config.json
```

### Platform-Specific Config Handling

When loading a config created on Windows:
- `"powershell"` action type → treat as `"shell"` (or support both)
- `"Win+X"` keystroke → map `Win` to `Cmd` (or warn user)
- `startWithWindows` → ignore on macOS

When saving on macOS:
- Use `"shell"` for shell script actions
- Use `"Cmd"` for Command key in keystrokes
- Keep all other fields identical

---

## Phase 8: Mode System & Menu Bar Integration

**Goal:** Mode switching from menu bar, with state persistence.

### Menu Structure
```
[Icon] Mighty Mini Mouse
├── ● Default                    (radio button — active mode)
├── ○ Photoshop
├── ○ Gaming
├── ─────────────────────
├── Device Settings...
├── Mappings...
├── Open Config File             → opens config.json in default editor
├── View Log                     → opens log file in Console.app or default editor
├── ─────────────────────
├── ✓ Enabled                    (toggle)
├── Start at Login               (toggle, uses SMAppService)
├── ─────────────────────
└── Quit
```

### Mode Switching
```swift
@objc func switchMode(_ sender: NSMenuItem) {
    guard let modeId = sender.representedObject as? String else { return }
    config.activeModeId = modeId
    configManager.save(config)

    let mode = config.modes.first { $0.id == modeId }!
    gestureEngine.updateGestures(mode.gestures, modeName: mode.name)
    rebuildModeMenu()
}
```

### Login Item Registration (macOS 13+)
```swift
import ServiceManagement

func setStartAtLogin(_ enabled: Bool) {
    do {
        if enabled {
            try SMAppService.mainApp.register()
        } else {
            try SMAppService.mainApp.unregister()
        }
    } catch {
        Logger.log(.error, .lifecycle, "Failed to set login item: \(error)")
    }
}
```

---

## Phase 9: Device Picker UI

**Goal:** Let users select which Bluetooth device to filter.

### Approach

For v1, keep it simple — an NSWindow (or SwiftUI sheet) showing a list of connected mice with live activity indicators.

```swift
struct DevicePickerView: View {
    @State var devices: [HIDDeviceManager.DeviceInfo] = []
    @State var activeDeviceId: String?
    @State var selectedDeviceId: String?

    var body: some View {
        List(devices) { device in
            HStack {
                // Activity dot (orange when recently active)
                Circle()
                    .fill(device.id == activeDeviceId ? .orange : .clear)
                    .frame(width: 8, height: 8)

                VStack(alignment: .leading) {
                    Text(device.name)
                    Text(device.id).font(.caption).foregroundColor(.secondary)
                    if device.isBluetooth { Text("Bluetooth").font(.caption2) }
                }

                Spacer()

                if device.id == selectedDeviceId {
                    Image(systemName: "checkmark")
                }
            }
            .onTapGesture { selectedDeviceId = device.id }
        }
    }
}
```

### Lesson from Windows
The Windows `DevicePickerDialog` uses a 200ms timer to poll for device activity and an owner-drawn ListView with custom dot rendering. On macOS, SwiftUI makes this much simpler — bind `activeDeviceId` to the `HIDDeviceManager.onDeviceActivity` callback and the UI updates automatically.

---

## Phase 10: Logging

**Goal:** File-based logging with category filtering, matching Windows log format.

### Implementation
```swift
class Logger {
    static let shared = Logger()

    enum Level: Int, Comparable {
        case debug = 0, info, warning, error, fatal
        static func < (lhs: Level, rhs: Level) -> Bool { lhs.rawValue < rhs.rawValue }
    }

    var minLevel: Level = .info
    var suppressedCategories: Set<String> = ["MouseMove", "RawInput"]
    private var fileHandle: FileHandle?
    private let queue = DispatchQueue(label: "com.mightymini.logger")

    func configure(logFile: URL, level: Level, suppressed: [String]) {
        FileManager.default.createFile(atPath: logFile.path, contents: nil)
        fileHandle = try? FileHandle(forWritingTo: logFile)
        fileHandle?.seekToEndOfFile()
        minLevel = level
        suppressedCategories = Set(suppressed)
    }

    func log(_ level: Level, _ category: String? = nil, _ message: String) {
        guard level >= minLevel else { return }
        if let cat = category, suppressedCategories.contains(cat) { return }

        let timestamp = ISO8601DateFormatter().string(from: Date())
        let line = "[\(timestamp)] [\(level)] \(message)\n"

        queue.async { [weak self] in
            self?.fileHandle?.write(line.data(using: .utf8)!)
        }

        #if DEBUG
        print(line, terminator: "")
        #endif
    }
}
```

### Log Location
```
~/Library/Application Support/MightyMiniMouse/interceptor.log
```

---

## Phase 11: Single Instance & Error Handling

### Single Instance
```swift
func ensureSingleInstance() -> Bool {
    let runningApps = NSWorkspace.shared.runningApplications
    let myBundleId = Bundle.main.bundleIdentifier!
    let instances = runningApps.filter { $0.bundleIdentifier == myBundleId }
    if instances.count > 1 {
        // Another instance is already running
        let alert = NSAlert()
        alert.messageText = "Mighty Mini Mouse is already running."
        alert.runModal()
        return false
    }
    return true
}
```

### Global Error Handling
```swift
// In AppDelegate
func applicationDidFinishLaunching(_ notification: Notification) {
    NSSetUncaughtExceptionHandler { exception in
        Logger.shared.log(.fatal, nil, "Uncaught exception: \(exception)")
        Logger.shared.log(.fatal, nil, "Stack trace: \(exception.callStackSymbols.joined(separator: "\n"))")
    }

    // Also catch Swift errors
    signal(SIGABRT) { _ in Logger.shared.log(.fatal, nil, "SIGABRT received") }
    signal(SIGSEGV) { _ in Logger.shared.log(.fatal, nil, "SIGSEGV received") }
}
```

---

## Implementation Order Summary

| Phase | Component | Estimated Scope | Dependencies |
|-------|-----------|----------------|--------------|
| 1 | Menu bar skeleton | Small | None |
| 2 | Accessibility permissions | Small | Phase 1 |
| 3 | CGEventTap (input interception) | Medium | Phase 2 |
| 4 | IOHIDManager (device filtering) | Medium | Phase 3 |
| 5 | Gesture engine (port from C#) | Large | Phase 3 |
| 6 | Action system (5 action types) | Medium | Phase 5 |
| 7 | Config system (JSON, migration) | Medium | Phase 6 |
| 8 | Mode system & menu integration | Small | Phase 7 |
| 9 | Device picker UI | Medium | Phase 4, 8 |
| 10 | Logging system | Small | Any phase |
| 11 | Single instance & error handling | Small | Any phase |

**Recommended order:** 1 → 2 → 3 → 10 → 4 → 5 → 6 → 7 → 8 → 9 → 11

Move logging (Phase 10) earlier so all subsequent phases can emit diagnostic output during development.

---

## Pitfalls & Lessons from the Windows Build

### 1. Hook Reliability
**Windows problem:** Hooks get silently removed by Windows after modal dialogs. Required `Reinstall()` and health check timer.
**macOS equivalent:** Event taps get disabled if callback takes >300ms. Handle `tapDisabledByTimeout` in the callback and re-enable immediately. Add a 5-second health check timer as backup.

### 2. Injected Event Loops
**Windows problem:** `SendInput` triggers the same LL hooks, causing infinite loops. Fixed by checking `LLMHF_INJECTED` flag.
**macOS equivalent:** `CGEventPost` events pass through the event tap. Tag injected events with a custom integer field and skip them in the callback.

### 3. Device Filtering Timing
**Windows problem:** Raw Input and LL hooks are separate systems. RawInput `WM_INPUT` message fires before LL hook callback for the same event, but timing can be tight.
**macOS advantage:** Both `IOHIDManager` callbacks and `CGEventTap` run on the same run loop. The HID callback fires before the tap callback for the same physical event. Same correlation pattern, slightly more reliable.

### 4. Bluetooth Reconnection
**Windows problem:** Device handles change on BT reconnect. Fixed by matching VID/PID from device path instead of handle.
**macOS equivalent:** Same approach — match by VID/PID, not by `IOHIDDevice` reference. The HID manager fires a removal callback followed by a matching callback on reconnect. Update the device reference but keep the VID/PID filter.

### 5. Mode Switching State Leak
**Windows problem:** Stale gesture state (held keys, pending timers) could bleed across mode switches. Fixed by clearing all state in `UpdateGestures()`.
**macOS:** Same fix — `updateGestures()` must cancel all pending `DispatchWorkItem`s, clear `currentlyHeld`, reset all trackers and sequence trackers.

### 6. Keyboard Hook After Context Menu
**Windows problem:** After the tray context menu closes (modal loop), keyboard hook stops receiving events. Fixed by scheduling deferred `Reinstall()` via `SyncContext.Post()` after mode switch.
**macOS:** `NSMenu` tracking runs its own run loop mode. Event taps should continue working across menu tracking, but test this. If issues arise, re-enable the tap after menu closes.

### 7. Config File Corruption
**Windows approach:** Pretty-print JSON, write atomically. Migration handles version upgrades gracefully.
**macOS:** Use `Data.write(to:options:.atomic)` for crash-safe writes. Same migration chain.
