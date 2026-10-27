# Mighty Mini Mouse for macOS — macOS API Reference

Quick reference for the macOS system APIs used in each component, mapped to their Windows equivalents.

---

## 1. Input Interception — CGEventTap

### Windows Equivalent: `SetWindowsHookEx(WH_MOUSE_LL / WH_KEYBOARD_LL)`

### Key Functions

| Function | Purpose |
|----------|---------|
| `CGEvent.tapCreate(tap:place:options:eventsOfInterest:callback:userInfo:)` | Install system-wide event tap |
| `CGEvent.tapEnable(tap:enable:)` | Enable/disable an existing tap |
| `CGEvent.tapIsEnabled(tap:)` | Check if tap is still active (health check) |
| `CFMachPortCreateRunLoopSource(_, tap, _)` | Create run loop source from tap |
| `CFRunLoopAddSource(_, source, .commonModes)` | Add tap to current run loop |

### Event Types

```swift
// Mouse events
CGEventType.leftMouseDown      // = 1
CGEventType.leftMouseUp        // = 2
CGEventType.rightMouseDown     // = 3
CGEventType.rightMouseUp       // = 4
CGEventType.otherMouseDown     // = 25 (middle, X1, X2, etc.)
CGEventType.otherMouseUp       // = 26
CGEventType.scrollWheel        // = 22

// Keyboard events
CGEventType.keyDown            // = 10
CGEventType.keyUp              // = 11
CGEventType.flagsChanged       // = 12 (modifier keys)

// Tap lifecycle
CGEventType.tapDisabledByTimeout   // = 0xFFFFFFFE — tap callback was too slow
CGEventType.tapDisabledByUserInput // = 0xFFFFFFFF — user disabled in settings
```

### CGEvent Fields

```swift
// Mouse button number (for otherMouseDown/Up)
event.getIntegerValueField(.mouseEventButtonNumber)
// 0 = left, 1 = right, 2 = middle, 3 = button4/XButton1, 4 = button5/XButton2

// Scroll wheel delta
event.getIntegerValueField(.scrollWheelEventDeltaAxis1)  // vertical
event.getIntegerValueField(.scrollWheelEventDeltaAxis2)  // horizontal

// Keyboard virtual key code
event.getIntegerValueField(.keyboardEventKeycode)

// Keyboard modifier flags
event.flags  // CGEventFlags: .maskShift, .maskControl, .maskAlternate, .maskCommand

// Timestamp (mach absolute time in nanoseconds)
event.timestamp
```

### Callback Signature

```swift
// Must be a C-compatible function (not a closure)
typealias CGEventTapCallBack = @convention(c) (
    CGEventTapProxy,           // proxy (pass through, don't use)
    CGEventType,               // event type
    CGEvent,                   // the event
    UnsafeMutableRawPointer?   // userInfo pointer
) -> Unmanaged<CGEvent>?       // return event to pass through, nil to suppress
```

### Suppressing Events

```swift
// In callback:
return Unmanaged.passRetained(event)  // pass through (like CallNextHookEx)
return nil                             // suppress (like returning 1 from LL hook)
```

---

## 2. Device Filtering — IOKit HID

### Windows Equivalent: Raw Input API (`RegisterRawInputDevices`, `GetRawInputData`)

### Key Functions

| Function | Purpose |
|----------|---------|
| `IOHIDManagerCreate(_, _)` | Create HID manager instance |
| `IOHIDManagerSetDeviceMatchingMultiple(_, matchDicts)` | Filter to specific device types |
| `IOHIDManagerRegisterInputValueCallback(_, callback, context)` | Per-input-report callback |
| `IOHIDManagerRegisterDeviceMatchingCallback(_, callback, context)` | Device connected |
| `IOHIDManagerRegisterDeviceRemovalCallback(_, callback, context)` | Device disconnected |
| `IOHIDManagerScheduleWithRunLoop(_, runLoop, mode)` | Attach to run loop |
| `IOHIDManagerOpen(_, options)` | Start monitoring |
| `IOHIDManagerCopyDevices(_)` | Get set of matched devices |
| `IOHIDDeviceGetProperty(_, key)` | Read device property (VID, PID, name, transport) |

### Device Properties

```swift
kIOHIDVendorIDKey       // Int — Vendor ID (e.g., 0x045E for Microsoft)
kIOHIDProductIDKey      // Int — Product ID
kIOHIDProductKey        // String — Product name ("Bluetooth Mouse")
kIOHIDTransportKey      // String — "Bluetooth", "USB", "Bluetooth Low Energy"
kIOHIDManufacturerKey   // String — Manufacturer name
kIOHIDSerialNumberKey   // String — Serial number (may be empty)
```

### Usage Page / Usage Constants

```swift
// For matching mouse devices:
kHIDPage_GenericDesktop  = 0x01
kHIDUsage_GD_Mouse      = 0x02
kHIDUsage_GD_Pointer     = 0x01
kHIDUsage_GD_Keyboard    = 0x06
```

---

## 3. Synthetic Input — CGEvent Creation & Posting

### Windows Equivalent: `SendInput` with `INPUT` structs

### Key Functions

| Function | Purpose |
|----------|---------|
| `CGEvent(keyboardEventSource:virtualKey:keyDown:)` | Create keyboard event |
| `CGEvent(mouseEventSource:mouseType:mouseCursorPosition:mouseButton:)` | Create mouse event |
| `event.post(tap:)` | Post event into the system |
| `event.setIntegerValueField(_, value:)` | Set event field (for injection tagging) |
| `CGEventSourceCreate(_)` | Create event source (optional) |

### Posting Locations

```swift
event.post(tap: .cghidEventTap)       // Posts at HID level (before event tap) — recommended
event.post(tap: .cgSessionEventTap)   // Posts at session level (after event tap)
event.post(tap: .cgAnnotatedSessionEventTap) // Posts with annotation
```

**Use `.cghidEventTap`** for synthetic input — this is closest to real hardware input. It will pass through your own event tap, so you must tag and filter injected events.

### Keyboard Event Example

```swift
// Press Cmd+Z
let source = CGEventSource(stateID: .combinedSessionState)

let keyDown = CGEvent(keyboardEventSource: source, virtualKey: 0x06 /* Z */, keyDown: true)!
keyDown.flags = .maskCommand
keyDown.post(tap: .cghidEventTap)

let keyUp = CGEvent(keyboardEventSource: source, virtualKey: 0x06, keyDown: false)!
keyUp.flags = .maskCommand
keyUp.post(tap: .cghidEventTap)
```

### Mouse Event Example

```swift
// Click at current mouse position
let pos = CGEvent(source: nil)!.location  // get current cursor position

let mouseDown = CGEvent(mouseEventSource: nil, mouseType: .leftMouseDown, mouseCursorPosition: pos, mouseButton: .left)!
mouseDown.post(tap: .cghidEventTap)

let mouseUp = CGEvent(mouseEventSource: nil, mouseType: .leftMouseUp, mouseCursorPosition: pos, mouseButton: .left)!
mouseUp.post(tap: .cghidEventTap)
```

---

## 4. Accessibility Permission

### No Windows Equivalent (Windows hooks don't require permission)

| Function | Purpose |
|----------|---------|
| `AXIsProcessTrusted()` | Returns `true` if app has Accessibility permission |
| `AXIsProcessTrustedWithOptions(_)` | Check + optionally show system prompt dialog |

```swift
import ApplicationServices

// Check without prompting
let trusted = AXIsProcessTrusted()

// Check and prompt if not trusted
let opts = [kAXTrustedCheckOptionPrompt.takeRetainedValue(): true] as CFDictionary
let prompted = AXIsProcessTrustedWithOptions(opts)

// Open System Settings directly
NSWorkspace.shared.open(URL(string:
    "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")!)
```

---

## 5. Menu Bar (System Tray)

### Windows Equivalent: `NotifyIcon` + `ContextMenuStrip`

| Class | Purpose |
|-------|---------|
| `NSStatusBar.system.statusItem(withLength:)` | Create menu bar item |
| `NSStatusItem.button` | The clickable button (set image here) |
| `NSStatusItem.menu` | Dropdown menu |
| `NSMenu` / `NSMenuItem` | Menu structure |

### Menu Bar Icon

```swift
// Template images auto-adapt to light/dark mode
let icon = NSImage(systemSymbolName: "computermouse.fill", accessibilityDescription: nil)
icon?.isTemplate = true  // required for proper menu bar appearance
statusItem.button?.image = icon
```

### Dynamic Menu Updates

```swift
func rebuildMenu() {
    let menu = NSMenu()

    // Mode submenu
    for mode in config.modes {
        let item = NSMenuItem(title: mode.name, action: #selector(switchMode), keyEquivalent: "")
        item.representedObject = mode.id
        item.state = mode.id == config.activeModeId ? .on : .off
        menu.addItem(item)
    }

    menu.addItem(.separator())
    // ... other items
    statusItem.menu = menu
}
```

---

## 6. Login Items

### Windows Equivalent: Registry `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`

```swift
import ServiceManagement

// macOS 13+
func registerLoginItem() throws {
    try SMAppService.mainApp.register()
}

func unregisterLoginItem() throws {
    try SMAppService.mainApp.unregister()
}

// Check status
let status = SMAppService.mainApp.status
// .enabled, .notRegistered, .notFound, .requiresApproval
```

---

## 7. Notifications

### Windows Equivalent: `NotifyIcon.ShowBalloonTip`

```swift
import UserNotifications

// Request permission (once at startup)
UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound]) { granted, error in
    // handle
}

// Send notification
let content = UNMutableNotificationContent()
content.title = "Mighty Mini Mouse"
content.body = "Gesture recognized: Back → Undo"
content.sound = .default

let request = UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil)
try await UNUserNotificationCenter.current().add(request)
```

---

## 8. File System

### Config & Log Paths

```swift
// Application Support directory (equivalent to %AppData%)
let appSupport = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
let appDir = appSupport.appendingPathComponent("MightyMiniMouse")

// Create directory if needed
try FileManager.default.createDirectory(at: appDir, withIntermediateDirectories: true)

// Paths
let configPath = appDir.appendingPathComponent("config.json")
let logPath = appDir.appendingPathComponent("interceptor.log")
```

### Atomic File Writes

```swift
// Crash-safe write (equivalent to Windows atomic write pattern)
try data.write(to: configPath, options: .atomic)
```

### Open File in Default Editor

```swift
// Open config in default JSON editor
NSWorkspace.shared.open(configPath)

// Open log in Console.app
NSWorkspace.shared.open(logPath)
```

---

## macOS Virtual Key Code Table

Complete mapping of config key names to macOS CGKeyCode values:

```
Key Name        macOS Code    Windows VK
─────────────────────────────────────────
A               0x00          0x41
S               0x01          0x53
D               0x02          0x44
F               0x03          0x46
H               0x04          0x48
G               0x05          0x47
Z               0x06          0x5A
X               0x07          0x58
C               0x08          0x43
V               0x09          0x56
B               0x0B          0x42
Q               0x0C          0x51
W               0x0D          0x57
E               0x0E          0x45
R               0x0F          0x52
Y               0x10          0x59
T               0x11          0x54
1               0x12          0x31
2               0x13          0x32
3               0x14          0x33
4               0x15          0x34
5               0x17          0x35
6               0x16          0x36
7               0x1A          0x37
8               0x1C          0x38
9               0x19          0x39
0               0x1D          0x30
Return          0x24          0x0D
Tab             0x30          0x09
Space           0x31          0x20
Delete/Backsp   0x33          0x08
Escape          0x35          0x1B
Command (Cmd)   0x37          (Win 0x5B)
Shift           0x38          0x10
CapsLock        0x39          0x14
Option (Alt)    0x3A          0x12
Control         0x3B          0x11
RightShift      0x3C          0xA1
RightOption     0x3D          0xA5
RightControl    0x3E          0xA3
Fn              0x3F          (none)
F1              0x7A          0x70
F2              0x78          0x71
F3              0x63          0x72
F4              0x76          0x73
F5              0x60          0x74
F6              0x61          0x75
F7              0x62          0x76
F8              0x64          0x77
F9              0x65          0x78
F10             0x6D          0x79
F11             0x67          0x7A
F12             0x6F          0x7B
F13             0x69          0x7C
F14             0x6B          0x7D
F15             0x71          0x7E
F16             0x6A          0x7F
Up              0x7E          0x26
Down            0x7D          0x28
Left            0x7B          0x25
Right           0x7C          0x27
Home            0x73          0x24
End             0x77          0x23
PageUp          0x74          0x21
PageDown        0x79          0x22
ForwardDelete   0x75          0x2E
VolumeUp        0x48          0xAF
VolumeDown      0x49          0xAE
Mute            0x4A          0xAD
```

**Note:** macOS key codes are hardware-based (positional), not character-based. The mapping above assumes a US ANSI keyboard layout. For international keyboards, the physical key position stays the same but the character changes. The gesture engine works on key codes (positions), not characters, so this is consistent.
