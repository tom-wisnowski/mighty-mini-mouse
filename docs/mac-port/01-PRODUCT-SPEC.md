# Mighty Mini Mouse for macOS — Product Specification

## Overview

Mighty Mini Mouse for macOS is a menu bar application that intercepts input from Bluetooth handheld mouse/pointer devices, recognizes configurable gesture patterns, and dispatches custom actions. It is the macOS counterpart to the Windows version, maintaining feature parity and config compatibility.

## Goals

1. **Feature parity** with the Windows version's gesture recognition and action system
2. **Config compatibility** — same `config.json` schema so users can share mappings across platforms
3. **Native macOS experience** — Swift, menu bar app, macOS permissions flow, native notifications
4. **Device filtering** — intercept only the target Bluetooth device without affecting normal mice/trackpads

## Target Platform

- macOS 13 (Ventura) and later
- Apple Silicon (arm64) primary, Intel (x86_64) supported via Universal Binary
- Distributed outside the Mac App Store (DMG with notarization) — required because `CGEventTap` is blocked by App Sandbox

---

## Feature Set

### Input Interception

| Feature | Windows Implementation | macOS Implementation |
|---------|----------------------|---------------------|
| System-wide mouse hook | `SetWindowsHookEx(WH_MOUSE_LL)` | `CGEventTapCreate` with mouse event mask |
| System-wide keyboard hook | `SetWindowsHookEx(WH_KEYBOARD_LL)` | `CGEventTapCreate` with keyboard event mask |
| Input suppression | Return `(IntPtr)1` from hook callback | Return `nil` from `CGEventTap` callback |
| Injected event filtering | Check `LLMHF_INJECTED` / `LLKHF_INJECTED` flags | Check `CGEvent` source state ID or use a custom flag via `CGEventSetIntegerValueField` |
| Device filtering | Raw Input API (`WM_INPUT` + VID/PID matching) | `IOHIDManager` with VID/PID matching dictionary |
| Synthetic input injection | `SendInput` with `INPUT` structs | `CGEventPost` / `CGEventCreateKeyboardEvent` |

### Supported Mouse Buttons

| Button | Windows Constant | macOS CGEvent |
|--------|-----------------|---------------|
| Left | `WM_LBUTTONDOWN/UP` | `kCGEventLeftMouseDown/Up` (button 0) |
| Right | `WM_RBUTTONDOWN/UP` | `kCGEventRightMouseDown/Up` (button 1) |
| Middle | `WM_MBUTTONDOWN/UP` | `kCGEventOtherMouseDown/Up` (button 2) |
| XButton1 (Back) | `WM_XBUTTONDOWN` + `XBUTTON1` | `kCGEventOtherMouseDown/Up` (button 3) |
| XButton2 (Forward) | `WM_XBUTTONDOWN` + `XBUTTON2` | `kCGEventOtherMouseDown/Up` (button 4) |
| Scroll Wheel | `WM_MOUSEWHEEL` | `kCGEventScrollWheel` |

### Gesture Types (5 — identical to Windows)

1. **SinglePress** — single button/key press, disambiguated from multi-press via delay
2. **MultiPress** — double/triple/N-tap within configurable time window (default 400ms)
3. **LongHold** — hold button/key for minimum duration (default 400ms, checked every 50ms)
4. **Sequence** — ordered key sequence within time window (e.g., XButton1 then Right)
5. **Chord** — multiple keys/buttons held simultaneously, triggers on final key down

### Action Types (5 — adapted for macOS)

| Action | Windows | macOS |
|--------|---------|-------|
| **Launch** | `Process.Start(path, args)` | `NSWorkspace.shared.open(url)` or `Process` with `/usr/bin/open` |
| **Keystroke** | `SendInput` with virtual key codes | `CGEventPost` with macOS virtual key codes |
| **Webhook** | `HttpClient.SendAsync` | `URLSession.shared.data(for:)` |
| **Shell Script** | `pwsh -File script.ps1` | `Process` with `/bin/zsh -c` or direct script path |
| **Notification** | `NotifyIcon.ShowBalloonTip` | `UNUserNotificationCenter` or `NSUserNotification` |

### Modes

- Named collections of gesture mappings (e.g., "Default", "Photoshop", "Gaming")
- Quick-switch from menu bar dropdown
- Active mode persisted across restarts
- Mode switching clears all gesture state (held keys, timers, sequence progress)

### Device Management

- Enumerate connected HID mice via `IOHIDManager`
- Filter by VID/PID (survives Bluetooth reconnections)
- User-assigned nicknames for known devices
- Live activity indicator showing which device is sending input
- "All Mice" option disables device filtering

### System Integration

| Feature | Windows | macOS |
|---------|---------|-------|
| System tray / menu bar | `NotifyIcon` + `ContextMenuStrip` | `NSStatusItem` + `NSMenu` |
| Auto-start at login | Windows Registry `Run` key | `SMAppService.mainApp.register()` (Login Item) |
| Single instance | Named `Mutex` | `NSRunningApplication` check or file lock |
| Config location | `%AppData%\MightyMiniMouse\` | `~/Library/Application Support/MightyMiniMouse/` |
| Log location | Same as config dir | Same as config dir |

---

## Permissions

macOS requires explicit user consent for input monitoring. The app must handle this gracefully:

1. **Accessibility Permission** (required for `CGEventTap`)
   - On first launch, check `AXIsProcessTrusted()`
   - If not trusted, show an onboarding dialog explaining why permission is needed
   - Open System Settings > Privacy & Security > Accessibility via `NSWorkspace`
   - Poll or observe for permission grant, then initialize hooks
   - If permission revoked while running, detect tap disabled state and prompt user

2. **Notification Permission** (required for Notification action)
   - Request via `UNUserNotificationCenter.requestAuthorization`
   - Gracefully degrade if denied (log warning, skip notification)

3. **No other permissions required** — no Input Monitoring permission needed separately when using `CGEventTap` with Accessibility (the Accessibility permission covers both)

---

## Config File Format

The config.json schema is shared with Windows. macOS-specific adaptations:

```json
{
  "configVersion": 3,
  "targetDevice": {
    "enabled": true,
    "devicePath": "VID_045E&PID_07B2",
    "friendlyName": "Bluetooth Presenter"
  },
  "modes": [
    {
      "id": "a1b2c3d4-...",
      "name": "Default",
      "gestures": [
        {
          "id": "e5f6a7b8-...",
          "name": "Back Button → Cmd+Z",
          "type": "SinglePress",
          "inputKeys": ["Mouse.XButton1"],
          "pressCount": 1,
          "timeWindowMs": 400,
          "suppressInput": true,
          "targetDeviceId": "VID_045E&PID_07B2",
          "action": {
            "type": "keystroke",
            "keystroke": "Cmd+Z"
          }
        }
      ]
    }
  ],
  "activeModeId": "a1b2c3d4-...",
  "knownDevices": [
    { "deviceId": "VID_045E&PID_07B2", "nickname": "Presenter Remote" }
  ],
  "logging": {
    "enabled": true,
    "logFile": "interceptor.log",
    "logLevel": "Info",
    "suppressedCategories": ["MouseMove", "RawInput"]
  },
  "startWithWindows": false
}
```

**Platform differences in config:**
- `keystroke` values use `Cmd` instead of `Win` (e.g., `"Cmd+Z"` vs `"Win+Z"`)
- `action.type: "powershell"` becomes `"shell"` on macOS (runs via `/bin/zsh`)
- `startWithWindows` is ignored on macOS; macOS uses Login Items (not stored in config — managed via system API)
- `devicePath` uses the same `VID_XXXX&PID_XXXX` format for cross-platform compatibility

---

## User Experience Flow

### First Launch
1. App starts, shows menu bar icon (gray — disabled state)
2. Checks `AXIsProcessTrusted()` → false
3. Shows onboarding window: "Mighty Mini Mouse needs Accessibility permission to intercept mouse input"
4. Button opens System Settings > Accessibility
5. After user grants permission, app detects it and initializes hooks
6. Menu bar icon turns blue — enabled state
7. Default mode active with empty gesture list

### Normal Operation
1. Menu bar icon shows current state (blue = enabled, gray = disabled)
2. Click menu bar icon → dropdown menu:
   - Mode submenu (radio buttons for each mode)
   - Separator
   - Device Settings...
   - Mappings...
   - Open Config File
   - View Log
   - Separator
   - Enabled/Disabled toggle
   - Quit
3. Input events flow through: CGEventTap → device filter → gesture engine → action dispatch

### Settings Dialog
- Native SwiftUI window (or AppKit sheet)
- Mode selector (tabs or dropdown)
- Gesture list with add/edit/delete
- Input recording: "Press a button on your device..." overlay
- Keystroke capture: records modifier+key combos
- Save persists to config.json and reloads gesture engine

---

## Non-Goals (v1)

- No GUI config editor in v1 — use JSON editing (same as early Windows versions); add SwiftUI settings in v2
- No multi-device simultaneous filtering (one target device at a time)
- No Apple Shortcuts integration (future enhancement)
- No Touch Bar support (deprecated by Apple)
