# BT Input Interceptor

A Windows system tray application that intercepts input from Bluetooth handheld mouse/pointer devices, detects configurable gesture patterns, and dispatches custom actions.

## Features

- **Input Interception** — Low-level hooks for both mouse (`WH_MOUSE_LL`) and keyboard (`WH_KEYBOARD_LL`)
- **Bluetooth Device Filtering** — Optional Raw Input API filtering to target a specific Bluetooth device
- **5 Gesture Types** — SinglePress, MultiPress (double/triple tap), LongHold, Sequence, and Chord
- **5 Action Types** — Launch process, send keystroke combo, HTTP webhook, PowerShell script, Windows notification
- **Input Suppression** — Swallows intercepted events so they don't pass through to the OS
- **System Tray** — Runs quietly with right-click context menu for configuration
- **Single Instance** — Mutex-based enforcement prevents multiple instances

## Requirements

- Windows 10/11 (x64)
- .NET 8.0 Runtime (or build self-contained)

## Build

```bash
dotnet build BtInputInterceptor/src/BtInputInterceptor.csproj
```

### Publish (self-contained)

```bash
dotnet publish BtInputInterceptor/src/BtInputInterceptor.csproj -c Release -r win-x64 --self-contained
```

## Configuration

Edit `config.json` in the application directory. See [bt-input-interceptor-spec.md](bt-input-interceptor-spec.md) §6.8 for the full config schema.

### Example: Long-hold XButton1 to launch an app

```json
{
  "gestures": [
    {
      "name": "Launch Voice Assistant",
      "type": "LongHold",
      "inputKeys": ["Mouse.XButton1"],
      "timeWindowMs": 800,
      "suppressInput": true,
      "action": {
        "type": "launch",
        "path": "C:\\path\\to\\app.exe"
      }
    }
  ]
}
```

## Project Structure

```
BtInputInterceptor/
├── src/
│   ├── Program.cs              # Entry point + message loop
│   ├── TrayApplication.cs      # Tray icon, lifecycle, wiring
│   ├── Hooks/
│   │   ├── NativeMethods.cs    # Win32 P/Invoke declarations
│   │   ├── MouseHook.cs        # WH_MOUSE_LL hook
│   │   ├── KeyboardHook.cs     # WH_KEYBOARD_LL hook
│   │   └── RawInputManager.cs  # Device enumeration & filtering
│   ├── Gestures/
│   │   ├── InputEvent.cs       # Unified input event model
│   │   ├── GestureDefinition.cs # Gesture pattern model
│   │   ├── GestureState.cs     # Recognition state trackers
│   │   └── GestureEngine.cs    # State machine recognizer
│   ├── Actions/
│   │   ├── IAction.cs          # Action interface
│   │   ├── ActionConfig.cs     # Serializable config model
│   │   ├── ActionFactory.cs    # Factory for creating actions
│   │   ├── LaunchProcessAction.cs
│   │   ├── SendKeystrokeAction.cs
│   │   ├── WebhookAction.cs
│   │   ├── PowerShellAction.cs
│   │   └── NotificationAction.cs
│   ├── Config/
│   │   ├── AppConfig.cs        # Configuration models
│   │   └── ConfigManager.cs    # JSON load/save
│   └── Logging/
│       └── Logger.cs           # File logger
└── config.json                 # User configuration
```

## Tray Menu

| Menu Item | Action |
|---|---|
| Configure... | Opens `config.json` in your default editor |
| Device Picker... | Lists connected Bluetooth input devices |
| ✓ Enabled | Toggle interception on/off |
| View Log... | Opens the log file |
| Exit | Clean shutdown |
