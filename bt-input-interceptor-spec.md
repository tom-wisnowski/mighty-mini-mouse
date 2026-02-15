# Bluetooth HID Input Interceptor — Design Specification

## 1. Overview

This application intercepts input from a Bluetooth handheld mouse/pointer device on Windows, detects configurable gesture patterns (button holds, multi-press sequences, and chord combinations across both mouse and keyboard HID codes), and dispatches custom actions in response.

The application runs as a **system tray application** (.NET 8, C#, WPF for tray UI) with an optional configuration GUI. It installs low-level hooks for both mouse (`WH_MOUSE_LL`) and keyboard (`WH_KEYBOARD_LL`) input to cover Bluetooth devices that may emit either or both HID code types.

---

## 2. Goals

- Intercept mouse button events (Left, Right, Middle, XButton1, XButton2, Scroll)
- Intercept keyboard events that may originate from the Bluetooth device (media keys, custom macro keys)
- Optionally filter input to a **specific Bluetooth device** using Raw Input API so normal mouse/keyboard is unaffected
- Detect gesture patterns: single press, double/triple press, long hold, button sequences, and chord combinations
- Execute configurable actions: launch process, send keystrokes, call HTTP webhook, run PowerShell script, trigger Windows notification
- Suppress (swallow) intercepted input so it doesn't pass through to the OS
- Run at Windows startup with minimal resource usage
- Persist configuration in a local JSON file

---

## 3. Architecture

```
┌─────────────────────────────────────────────────────────┐
│                    System Tray App                       │
│  ┌──────────┐  ┌──────────────┐  ┌───────────────────┐  │
│  │  Tray UI  │  │  Config GUI  │  │  Config Manager   │  │
│  │ (NotifyIcon│  │  (WPF Window │  │  (JSON read/write)│  │
│  │  + Menu)  │  │   optional)  │  │                   │  │
│  └──────────┘  └──────────────┘  └───────────────────┘  │
│        │                                    │            │
│  ┌─────┴────────────────────────────────────┴──────┐     │
│  │              Input Pipeline                     │     │
│  │  ┌────────────┐  ┌────────────┐  ┌───────────┐ │     │
│  │  │ Mouse Hook │  │  KB Hook   │  │ Raw Input │ │     │
│  │  │ WH_MOUSE_LL│  │WH_KEYBOARD │  │ (Device   │ │     │
│  │  │            │  │   _LL      │  │  Filter)  │ │     │
│  │  └─────┬──────┘  └─────┬──────┘  └─────┬─────┘ │     │
│  │        └───────┬───────┘              │       │     │
│  │                ▼                      │       │     │
│  │  ┌──────────────────────┐             │       │     │
│  │  │   Device Filter      │◄────────────┘       │     │
│  │  │ (optional: only BT   │                     │     │
│  │  │  device events)      │                     │     │
│  │  └──────────┬───────────┘                     │     │
│  │             ▼                                 │     │
│  │  ┌──────────────────────┐                     │     │
│  │  │   Gesture Engine     │                     │     │
│  │  │  (State Machine)     │                     │     │
│  │  └──────────┬───────────┘                     │     │
│  │             ▼                                 │     │
│  │  ┌──────────────────────┐                     │     │
│  │  │   Action Dispatcher  │                     │     │
│  │  └──────────────────────┘                     │     │
│  └───────────────────────────────────────────────┘     │
└─────────────────────────────────────────────────────────┘
```

---

## 4. Project Structure

```
BtInputInterceptor/
├── BtInputInterceptor.sln
├── src/
│   ├── Program.cs                  # Entry point, tray app bootstrap
│   ├── TrayApplication.cs          # NotifyIcon, context menu, lifecycle
│   ├── Hooks/
│   │   ├── MouseHook.cs            # WH_MOUSE_LL implementation
│   │   ├── KeyboardHook.cs         # WH_KEYBOARD_LL implementation
│   │   ├── RawInputManager.cs      # Raw Input device registration & filtering
│   │   └── NativeMethods.cs        # All P/Invoke declarations (single file)
│   ├── Gestures/
│   │   ├── GestureEngine.cs        # State machine coordinator
│   │   ├── GestureDefinition.cs    # Model: what constitutes a gesture
│   │   ├── GestureState.cs         # Tracks in-progress gesture recognition
│   │   └── InputEvent.cs           # Unified input event model
│   ├── Actions/
│   │   ├── IAction.cs              # Action interface
│   │   ├── LaunchProcessAction.cs
│   │   ├── SendKeystrokeAction.cs
│   │   ├── WebhookAction.cs
│   │   ├── PowerShellAction.cs
│   │   └── NotificationAction.cs
│   ├── Config/
│   │   ├── AppConfig.cs            # Configuration model
│   │   └── ConfigManager.cs        # Load/save JSON config
│   └── Logging/
│       └── Logger.cs               # Simple file logger
├── config.json                     # User configuration
└── README.md
```

---

## 5. Technology Stack

| Component | Technology |
|---|---|
| Runtime | .NET 8 (Windows target) |
| Language | C# 12 |
| UI Framework | WPF (for system tray and config window) |
| Input Hooking | Win32 P/Invoke (`user32.dll`) |
| Raw Input | Win32 P/Invoke (`user32.dll`) |
| Config Format | JSON via `System.Text.Json` |
| Logging | Serilog or simple file logger |
| Build | `dotnet publish -r win-x64 --self-contained` |

---

## 6. Component Specifications

### 6.1 Native Methods (P/Invoke Declarations)

All Win32 interop goes in a single `NativeMethods.cs` file. This is the foundation everything else depends on.

```csharp
// NativeMethods.cs
using System;
using System.Runtime.InteropServices;

namespace BtInputInterceptor.Hooks;

internal static class NativeMethods
{
    // ── Hook types ──
    public const int WH_MOUSE_LL = 14;
    public const int WH_KEYBOARD_LL = 13;

    // ── Mouse messages ──
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_RBUTTONUP = 0x0205;
    public const int WM_MBUTTONDOWN = 0x0207;
    public const int WM_MBUTTONUP = 0x0208;
    public const int WM_MOUSEWHEEL = 0x020A;
    public const int WM_XBUTTONDOWN = 0x020B;
    public const int WM_XBUTTONUP = 0x020C;

    // ── XButton identifiers (in mouseData high word) ──
    public const int XBUTTON1 = 0x0001;
    public const int XBUTTON2 = 0x0002;

    // ── Keyboard messages ──
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;

    // ── Flags ──
    public const int LLKHF_INJECTED = 0x00000010;
    public const int LLMHF_INJECTED = 0x00000001;

    // ── Raw Input ──
    public const int RID_INPUT = 0x10000003;
    public const int RIM_TYPEMOUSE = 0;
    public const int RIM_TYPEKEYBOARD = 1;
    public const int RIM_TYPEHID = 2;
    public const int RIDEV_INPUTSINK = 0x00000100;
    public const int WM_INPUT = 0x00FF;

    public delegate IntPtr LowLevelHookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelHookProc lpfn,
        IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode,
        IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    // ── Raw Input functions ──
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterRawInputDevices(
        RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetRawInputData(
        IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetRawInputDeviceList(
        [Out] RAWINPUTDEVICELIST[]? pRawInputDeviceList, ref uint puiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern uint GetRawInputDeviceInfo(
        IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

    public const uint RIDI_DEVICENAME = 0x20000007;
    public const uint RIDI_DEVICEINFO = 0x2000000b;

    // ── Structs ──
    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICELIST
    {
        public IntPtr hDevice;
        public uint dwType;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWMOUSE
    {
        public ushort usFlags;
        public uint ulButtons; // contains usButtonFlags and usButtonData
        public uint ulRawButtons;
        public int lLastX;
        public int lLastY;
        public uint ulExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct RAWINPUT
    {
        [FieldOffset(0)] public RAWINPUTHEADER header;
        [FieldOffset(24)] public RAWMOUSE mouse;     // 64-bit offset
        [FieldOffset(24)] public RAWKEYBOARD keyboard;
    }
}
```

> **IMPORTANT:** The `RAWINPUT` struct field offsets differ between 32-bit and 64-bit. The values above are for **x64** builds. If you need to support x86, use conditional compilation or `Marshal.SizeOf` for the header.

---

### 6.2 Mouse Hook

```csharp
// MouseHook.cs
using System;
using System.Diagnostics;
using static BtInputInterceptor.Hooks.NativeMethods;

namespace BtInputInterceptor.Hooks;

public sealed class MouseHook : IDisposable
{
    private IntPtr _hookId = IntPtr.Zero;
    private readonly LowLevelHookProc _proc;

    // Fires for every mouse event. Return true from handler to suppress the input.
    public event Func<MouseHookEventArgs, bool>? OnMouseEvent;

    public MouseHook()
    {
        _proc = HookCallback;
    }

    public void Install()
    {
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule!;
        _hookId = SetWindowsHookEx(WH_MOUSE_LL, _proc,
            GetModuleHandle(module.ModuleName), 0);

        if (_hookId == IntPtr.Zero)
            throw new InvalidOperationException(
                $"Failed to install mouse hook. Error: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var hookStruct = System.Runtime.InteropServices.Marshal
                .PtrToStructure<MSLLHOOKSTRUCT>(lParam);

            // Skip injected events (ones we or other software generated)
            if ((hookStruct.flags & LLMHF_INJECTED) != 0)
                return CallNextHookEx(_hookId, nCode, wParam, lParam);

            var args = new MouseHookEventArgs
            {
                MessageId = (int)wParam,
                Point = hookStruct.pt,
                MouseData = hookStruct.mouseData,
                Timestamp = hookStruct.time
            };

            bool suppress = OnMouseEvent?.Invoke(args) ?? false;
            if (suppress)
                return (IntPtr)1; // Block the input
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }
}

public class MouseHookEventArgs
{
    public int MessageId { get; init; }
    public POINT Point { get; init; }
    public uint MouseData { get; init; }
    public uint Timestamp { get; init; }

    /// <summary>
    /// Resolves which mouse button this event represents.
    /// </summary>
    public MouseButton GetButton() => MessageId switch
    {
        WM_LBUTTONDOWN or WM_LBUTTONUP => MouseButton.Left,
        WM_RBUTTONDOWN or WM_RBUTTONUP => MouseButton.Right,
        WM_MBUTTONDOWN or WM_MBUTTONUP => MouseButton.Middle,
        WM_XBUTTONDOWN or WM_XBUTTONUP =>
            (MouseData >> 16) == XBUTTON1 ? MouseButton.XButton1 : MouseButton.XButton2,
        WM_MOUSEWHEEL => MouseButton.Wheel,
        _ => MouseButton.Unknown
    };

    public bool IsDown => MessageId is WM_LBUTTONDOWN or WM_RBUTTONDOWN
        or WM_MBUTTONDOWN or WM_XBUTTONDOWN;

    public bool IsUp => MessageId is WM_LBUTTONUP or WM_RBUTTONUP
        or WM_MBUTTONUP or WM_XBUTTONUP;
}

public enum MouseButton
{
    Unknown, Left, Right, Middle, XButton1, XButton2, Wheel
}
```

---

### 6.3 Keyboard Hook

```csharp
// KeyboardHook.cs
using System;
using System.Diagnostics;
using static BtInputInterceptor.Hooks.NativeMethods;

namespace BtInputInterceptor.Hooks;

public sealed class KeyboardHook : IDisposable
{
    private IntPtr _hookId = IntPtr.Zero;
    private readonly LowLevelHookProc _proc;

    public event Func<KeyboardHookEventArgs, bool>? OnKeyEvent;

    public KeyboardHook()
    {
        _proc = HookCallback;
    }

    public void Install()
    {
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule!;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc,
            GetModuleHandle(module.ModuleName), 0);

        if (_hookId == IntPtr.Zero)
            throw new InvalidOperationException(
                $"Failed to install keyboard hook. Error: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var hookStruct = System.Runtime.InteropServices.Marshal
                .PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

            // Skip injected events
            if ((hookStruct.flags & LLKHF_INJECTED) != 0)
                return CallNextHookEx(_hookId, nCode, wParam, lParam);

            var args = new KeyboardHookEventArgs
            {
                VirtualKeyCode = hookStruct.vkCode,
                ScanCode = hookStruct.scanCode,
                IsKeyDown = (int)wParam is WM_KEYDOWN or WM_SYSKEYDOWN,
                IsKeyUp = (int)wParam is WM_KEYUP or WM_SYSKEYUP,
                Timestamp = hookStruct.time
            };

            bool suppress = OnKeyEvent?.Invoke(args) ?? false;
            if (suppress)
                return (IntPtr)1;
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }
}

public class KeyboardHookEventArgs
{
    public uint VirtualKeyCode { get; init; }
    public uint ScanCode { get; init; }
    public bool IsKeyDown { get; init; }
    public bool IsKeyUp { get; init; }
    public uint Timestamp { get; init; }
}
```

---

### 6.4 Raw Input Manager (Device Filtering)

This component is used to **identify which physical device** generated an input event. The low-level hooks alone cannot distinguish between devices — they just see "a mouse event happened." Raw Input solves this.

**Strategy:** Register for Raw Input on a hidden message-only window. When a raw input event arrives, record the `hDevice` handle. The hooks fire *after* raw input, so we compare timestamps to correlate them. If the `hDevice` doesn't match our target Bluetooth device, we let the hook pass through.

```csharp
// RawInputManager.cs — key concepts (not full implementation)

public class RawInputManager
{
    private IntPtr _targetDeviceHandle = IntPtr.Zero;
    private IntPtr _lastSeenDevice = IntPtr.Zero;
    private uint _lastSeenTimestamp = 0;

    /// <summary>
    /// Enumerate all HID devices and let the user pick their Bluetooth device.
    /// Device paths for Bluetooth devices typically contain "BTHLE" or "BTHENUM".
    /// </summary>
    public List<DeviceInfo> EnumerateDevices()
    {
        uint deviceCount = 0;
        uint size = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        GetRawInputDeviceList(null, ref deviceCount, size);

        var devices = new RAWINPUTDEVICELIST[deviceCount];
        GetRawInputDeviceList(devices, ref deviceCount, size);

        var result = new List<DeviceInfo>();
        foreach (var device in devices)
        {
            // Get device name (path)
            uint nameSize = 0;
            GetRawInputDeviceInfo(device.hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref nameSize);
            var namePtr = Marshal.AllocHGlobal((int)nameSize * 2);
            GetRawInputDeviceInfo(device.hDevice, RIDI_DEVICENAME, namePtr, ref nameSize);
            string name = Marshal.PtrToStringAuto(namePtr) ?? "";
            Marshal.FreeHGlobal(namePtr);

            result.Add(new DeviceInfo
            {
                Handle = device.hDevice,
                Type = device.dwType,
                DevicePath = name,
                IsBluetooth = name.Contains("BTHLE", StringComparison.OrdinalIgnoreCase)
                           || name.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase)
            });
        }
        return result;
    }

    /// <summary>
    /// Set which device handle we care about.
    /// </summary>
    public void SetTargetDevice(IntPtr handle) => _targetDeviceHandle = handle;

    /// <summary>
    /// Called when WM_INPUT arrives. Records the source device.
    /// </summary>
    public void ProcessRawInput(IntPtr hRawInput)
    {
        uint size = 0;
        GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size,
            (uint)Marshal.SizeOf<RAWINPUTHEADER>());

        var buffer = Marshal.AllocHGlobal((int)size);
        GetRawInputData(hRawInput, RID_INPUT, buffer, ref size,
            (uint)Marshal.SizeOf<RAWINPUTHEADER>());

        var raw = Marshal.PtrToStructure<RAWINPUT>(buffer);
        _lastSeenDevice = raw.header.hDevice;
        // Store timestamp for correlation with hook events
        Marshal.FreeHGlobal(buffer);
    }

    /// <summary>
    /// Returns true if the most recent raw input came from our target device.
    /// Called by the hook callbacks to decide whether to process or ignore.
    /// </summary>
    public bool IsFromTargetDevice()
    {
        if (_targetDeviceHandle == IntPtr.Zero) return true; // No filter = accept all
        return _lastSeenDevice == _targetDeviceHandle;
    }
}

public class DeviceInfo
{
    public IntPtr Handle { get; init; }
    public uint Type { get; init; }
    public string DevicePath { get; init; } = "";
    public bool IsBluetooth { get; init; }
}
```

> **Note on timing correlation:** Low-level hooks and Raw Input events are dispatched on the same thread's message queue. Raw Input (`WM_INPUT`) arrives *before* the hook callback, so by the time the hook fires, `_lastSeenDevice` is already set for that input event. This means simple "check last device" works reliably without complex timestamp matching.

---

### 6.5 Unified Input Event Model

Both hooks feed into a single normalized event type for the gesture engine.

```csharp
// InputEvent.cs

namespace BtInputInterceptor.Gestures;

public enum InputType { MouseButton, KeyPress }
public enum PressState { Down, Up }

public record InputEvent
{
    public InputType Type { get; init; }
    public PressState State { get; init; }

    // For mouse events
    public MouseButton? Button { get; init; }

    // For keyboard events
    public uint? VirtualKeyCode { get; init; }

    public uint Timestamp { get; init; } // OS tick count

    /// <summary>
    /// A unified string key for use in gesture matching.
    /// Examples: "Mouse.XButton1", "Mouse.Right", "Key.VolumeUp", "Key.F13"
    /// </summary>
    public string InputKey => Type switch
    {
        InputType.MouseButton => $"Mouse.{Button}",
        InputType.KeyPress => $"Key.{(ConsoleKey)VirtualKeyCode!}",
        _ => "Unknown"
    };
}
```

---

### 6.6 Gesture Engine (State Machine)

The gesture engine is the brain of the application. It takes a stream of `InputEvent` objects and matches them against configured gesture definitions.

**Supported gesture types:**

| Gesture Type | Description | Example |
|---|---|---|
| `SinglePress` | One press and release | XButton1 tap |
| `MultiPress` | N presses within a time window | XButton1 double-tap |
| `LongHold` | Button held for N milliseconds | XButton2 held 800ms |
| `Sequence` | Ordered button presses within a window | XButton1 → XButton2 → XButton1 |
| `Chord` | Two or more buttons held simultaneously | XButton1 + XButton2 held together |

```csharp
// GestureDefinition.cs

namespace BtInputInterceptor.Gestures;

public enum GestureType
{
    SinglePress,
    MultiPress,
    LongHold,
    Sequence,
    Chord
}

public class GestureDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public GestureType Type { get; set; }

    /// <summary>
    /// The input keys involved. For SinglePress/LongHold: one key.
    /// For MultiPress: one key (pressed N times). For Sequence: ordered list.
    /// For Chord: set of keys that must be held simultaneously.
    /// </summary>
    public List<string> InputKeys { get; set; } = [];

    /// <summary>
    /// For MultiPress: how many presses. Default 2 (double press).
    /// </summary>
    public int PressCount { get; set; } = 2;

    /// <summary>
    /// Time window in milliseconds. For MultiPress: max gap between presses.
    /// For LongHold: minimum hold duration. For Sequence: max total duration.
    /// </summary>
    public int TimeWindowMs { get; set; } = 400;

    /// <summary>
    /// The action to execute when this gesture is recognized.
    /// </summary>
    public ActionConfig Action { get; set; } = new();

    /// <summary>
    /// Whether to suppress (swallow) the input events that make up this gesture.
    /// </summary>
    public bool SuppressInput { get; set; } = true;
}
```

```csharp
// GestureEngine.cs — core logic

namespace BtInputInterceptor.Gestures;

public class GestureEngine
{
    private readonly List<GestureDefinition> _gestures;
    private readonly Dictionary<string, GestureTracker> _trackers = new();
    private readonly System.Timers.Timer _holdCheckTimer;

    // Tracks currently held buttons/keys
    private readonly HashSet<string> _currentlyHeld = new();

    public event Action<GestureDefinition>? OnGestureRecognized;

    public GestureEngine(List<GestureDefinition> gestures)
    {
        _gestures = gestures;

        // Timer ticks every 50ms to check for long-hold gestures
        _holdCheckTimer = new System.Timers.Timer(50);
        _holdCheckTimer.Elapsed += CheckHoldGestures;
        _holdCheckTimer.Start();
    }

    /// <summary>
    /// Feed every input event into this method. Returns true if the event
    /// should be suppressed (swallowed).
    /// </summary>
    public bool ProcessInput(InputEvent input)
    {
        bool shouldSuppress = false;

        if (input.State == PressState.Down)
        {
            _currentlyHeld.Add(input.InputKey);
            RecordPressDown(input);
            shouldSuppress |= CheckChordGestures(input);
        }
        else // Up
        {
            _currentlyHeld.Remove(input.InputKey);
            shouldSuppress |= CheckPressGestures(input);
        }

        shouldSuppress |= CheckSequenceGestures(input);
        return shouldSuppress;
    }

    private void RecordPressDown(InputEvent input)
    {
        // Create or update tracker for this input key
        if (!_trackers.TryGetValue(input.InputKey, out var tracker))
        {
            tracker = new GestureTracker();
            _trackers[input.InputKey] = tracker;
        }

        tracker.LastDownTimestamp = input.Timestamp;
        tracker.PressCount++;

        // Reset press count if too much time has elapsed since last press
        if (input.Timestamp - tracker.LastUpTimestamp > 400) // MultiPress window
            tracker.PressCount = 1;
    }

    private bool CheckPressGestures(InputEvent input)
    {
        if (!_trackers.TryGetValue(input.InputKey, out var tracker))
            return false;

        tracker.LastUpTimestamp = input.Timestamp;
        uint holdDuration = input.Timestamp - tracker.LastDownTimestamp;

        foreach (var gesture in _gestures)
        {
            if (!gesture.InputKeys.Contains(input.InputKey)) continue;

            switch (gesture.Type)
            {
                // SinglePress: one tap, released quickly (not a hold)
                case GestureType.SinglePress:
                    if (tracker.PressCount == 1 && holdDuration < 300)
                    {
                        // Delay recognition briefly to rule out double-press
                        // (Use a short timer callback — simplified here)
                        OnGestureRecognized?.Invoke(gesture);
                        tracker.Reset();
                        return gesture.SuppressInput;
                    }
                    break;

                // MultiPress: N taps within time window
                case GestureType.MultiPress:
                    if (tracker.PressCount >= gesture.PressCount)
                    {
                        OnGestureRecognized?.Invoke(gesture);
                        tracker.Reset();
                        return gesture.SuppressInput;
                    }
                    break;
            }
        }
        return false;
    }

    private void CheckHoldGestures(object? sender, System.Timers.ElapsedEventArgs e)
    {
        uint now = (uint)Environment.TickCount;

        foreach (var gesture in _gestures.Where(g => g.Type == GestureType.LongHold))
        {
            string key = gesture.InputKeys[0];
            if (_currentlyHeld.Contains(key) && _trackers.TryGetValue(key, out var tracker))
            {
                uint held = now - tracker.LastDownTimestamp;
                if (held >= (uint)gesture.TimeWindowMs && !tracker.HoldFired)
                {
                    tracker.HoldFired = true;
                    OnGestureRecognized?.Invoke(gesture);
                }
            }
        }
    }

    private bool CheckChordGestures(InputEvent input)
    {
        foreach (var gesture in _gestures.Where(g => g.Type == GestureType.Chord))
        {
            if (gesture.InputKeys.All(k => _currentlyHeld.Contains(k)))
            {
                OnGestureRecognized?.Invoke(gesture);
                return gesture.SuppressInput;
            }
        }
        return false;
    }

    private bool CheckSequenceGestures(InputEvent input)
    {
        // Sequence tracking is per-gesture, not per-key.
        // Each sequence gesture has its own progress tracker.
        // On each Down event, check if it matches the next expected key in any sequence.
        // If the sequence completes within the time window, fire.
        // Implementation left to builder — pattern is: maintain a sequenceIndex per
        // gesture, advance on match, reset on mismatch or timeout.
        return false;
    }
}

internal class GestureTracker
{
    public uint LastDownTimestamp { get; set; }
    public uint LastUpTimestamp { get; set; }
    public int PressCount { get; set; }
    public bool HoldFired { get; set; }

    public void Reset()
    {
        PressCount = 0;
        HoldFired = false;
    }
}
```

> **Critical implementation note — SinglePress vs MultiPress disambiguation:** If you have both a SinglePress and a DoublePress on the same button, the SinglePress must be **delayed** until the MultiPress time window expires. Otherwise, the first tap of a double-press will trigger the single-press action. Use a `System.Threading.Timer` callback after the time window (e.g., 400ms) to confirm no second press arrived.

---

### 6.7 Action Dispatcher

```csharp
// IAction.cs
namespace BtInputInterceptor.Actions;

public interface IAction
{
    Task ExecuteAsync(CancellationToken ct = default);
}

// ActionConfig.cs — serializable config model
public class ActionConfig
{
    public string Type { get; set; } = "notification"; // notification, launch, keystroke, webhook, powershell
    public string? Path { get; set; }          // For launch: exe path. For powershell: script path.
    public string? Arguments { get; set; }     // For launch: command line args
    public string? Url { get; set; }           // For webhook
    public string? HttpMethod { get; set; }    // For webhook: GET, POST, etc.
    public string? Body { get; set; }          // For webhook: request body
    public string? Keystroke { get; set; }     // For keystroke: e.g. "Ctrl+Shift+M"
    public string? Message { get; set; }       // For notification: toast message
}

// LaunchProcessAction.cs — example
public class LaunchProcessAction : IAction
{
    private readonly string _path;
    private readonly string? _arguments;

    public LaunchProcessAction(string path, string? arguments = null)
    {
        _path = path;
        _arguments = arguments;
    }

    public Task ExecuteAsync(CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(_path)
        {
            Arguments = _arguments ?? "",
            UseShellExecute = true
        };
        Process.Start(psi);
        return Task.CompletedTask;
    }
}

// ActionFactory.cs — creates IAction from config
public static class ActionFactory
{
    public static IAction Create(ActionConfig config) => config.Type switch
    {
        "launch" => new LaunchProcessAction(config.Path!, config.Arguments),
        "keystroke" => new SendKeystrokeAction(config.Keystroke!),
        "webhook" => new WebhookAction(config.Url!, config.HttpMethod ?? "POST", config.Body),
        "powershell" => new PowerShellAction(config.Path!, config.Arguments),
        "notification" => new NotificationAction(config.Message ?? "Gesture triggered"),
        _ => throw new ArgumentException($"Unknown action type: {config.Type}")
    };
}
```

---

### 6.8 Configuration

```jsonc
// config.json
{
  "targetDevice": {
    "enabled": true,
    "devicePath": "\\\\?\\HID#VID_046D&PID_B023#...",  // Set via device picker or leave null for all devices
    "friendlyName": "Logitech Spotlight"
  },
  "gestures": [
    {
      "id": "voice-assistant",
      "name": "Launch Voice Assistant",
      "type": "LongHold",
      "inputKeys": ["Mouse.XButton1"],
      "timeWindowMs": 800,
      "suppressInput": true,
      "action": {
        "type": "launch",
        "path": "C:\\Apps\\HeyNexus\\heynexus.exe"
      }
    },
    {
      "id": "quick-note",
      "name": "Quick Note",
      "type": "MultiPress",
      "inputKeys": ["Mouse.XButton2"],
      "pressCount": 3,
      "timeWindowMs": 500,
      "suppressInput": true,
      "action": {
        "type": "keystroke",
        "keystroke": "Win+Alt+N"
      }
    },
    {
      "id": "mute-toggle",
      "name": "Toggle Microphone Mute",
      "type": "Chord",
      "inputKeys": ["Mouse.XButton1", "Mouse.XButton2"],
      "timeWindowMs": 0,
      "suppressInput": true,
      "action": {
        "type": "keystroke",
        "keystroke": "Ctrl+Shift+M"
      }
    },
    {
      "id": "webhook-trigger",
      "name": "Trigger Home Automation",
      "type": "Sequence",
      "inputKeys": ["Mouse.XButton1", "Mouse.XButton2", "Mouse.XButton1"],
      "timeWindowMs": 1500,
      "suppressInput": true,
      "action": {
        "type": "webhook",
        "url": "http://homeassistant.local:8123/api/services/scene/turn_on",
        "httpMethod": "POST",
        "body": "{\"entity_id\": \"scene.presentation_mode\"}"
      }
    }
  ],
  "logging": {
    "enabled": true,
    "logFile": "interceptor.log",
    "logLevel": "Info"
  },
  "startWithWindows": true
}
```

```csharp
// ConfigManager.cs
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BtInputInterceptor.Config;

public class ConfigManager
{
    private static readonly string ConfigPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static AppConfig Load()
    {
        if (!File.Exists(ConfigPath))
        {
            var defaults = new AppConfig();
            Save(defaults);
            return defaults;
        }

        var json = File.ReadAllText(ConfigPath);
        return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
    }

    public static void Save(AppConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(ConfigPath, json);
    }
}
```

---

### 6.9 Application Entry Point

```csharp
// Program.cs
using System;
using System.Threading;
using System.Windows.Forms; // For NotifyIcon and message loop

namespace BtInputInterceptor;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Single instance enforcement
        using var mutex = new Mutex(true, "BtInputInterceptor_SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("BT Input Interceptor is already running.", "Already Running");
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var trayApp = new TrayApplication();
        trayApp.Initialize();

        // Application.Run starts the Windows message loop.
        // This is REQUIRED for:
        //   1. Low-level hooks to receive callbacks
        //   2. NotifyIcon (tray icon) to work
        //   3. Raw Input WM_INPUT messages to be dispatched
        Application.Run();

        trayApp.Shutdown();
    }
}
```

> **CRITICAL:** Low-level hooks (`WH_MOUSE_LL` and `WH_KEYBOARD_LL`) require a Windows message pump on the thread that installed them. Without `Application.Run()` (or equivalent message loop), hook callbacks will **never fire**. This is the #1 mistake people make.

---

## 7. Threading & Performance Rules

1. **Hook callbacks execute on the UI thread's message loop.** They must return within ~300ms or Windows will silently unhook them. **Never do blocking I/O in a hook callback.**

2. **Action dispatch must be async and off-thread.** When a gesture is recognized, queue the action to a background thread or `Task.Run`. The hook callback should return immediately.

3. **The gesture engine should be fast.** It's called on every mouse/keyboard event system-wide. Keep it O(n) where n = number of gesture definitions (which will be small, typically < 20).

4. **Use `Environment.TickCount` (or the hook timestamp) for timing** — not `DateTime.Now`. The hook struct provides a `time` field in milliseconds that is monotonic and lightweight.

---

## 8. Startup & Lifecycle

| Event | Action |
|---|---|
| App launches | Load config → enumerate devices → install hooks → show tray icon |
| Tray icon right-click | Show menu: Configure, Device Picker, View Log, Enable/Disable, Exit |
| "Configure" clicked | Open JSON config in default editor (or future: config GUI) |
| "Device Picker" clicked | Enumerate devices, show list, save selection |
| "Enable/Disable" toggled | Unhook/rehook without exiting |
| "Exit" clicked | Unhook → dispose → `Application.Exit()` |
| Windows startup | Register in `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` if configured |

---

## 9. Discovery Mode (First Run)

On first run (or via menu), the app should enter **Discovery Mode**:

1. Show a small overlay window: "Press any button on your Bluetooth device..."
2. Log all incoming mouse and keyboard events with their device info (via Raw Input)
3. When the user presses a button on the target device, highlight it
4. Let the user confirm: "Is this your device?"
5. Save the device handle/path to config

This is essential UX because users won't know their device's HID path.

---

## 10. Testing & Debugging Strategy

**Step 1: Verify hooks are receiving events.**
Create a simple console logger that prints every hook event before building the gesture engine. This confirms the hooks work and reveals whether your Bluetooth device sends mouse codes, keyboard codes, or both.

**Step 2: Identify what your device sends.**
Run the logger, press each button on the device, and record:
- Does it fire mouse hook, keyboard hook, or both?
- What button/key codes does it send?
- Are events paired (down + up) or single-fire?

**Step 3: Test gesture recognition in isolation.**
Unit test the `GestureEngine` by feeding it synthetic `InputEvent` sequences and asserting that the correct gestures are recognized.

**Step 4: Test suppression.**
Verify that returning `(IntPtr)1` from the hook callback actually prevents the input from reaching other applications. Open Notepad, configure a gesture on a key, and confirm the key press doesn't appear.

---

## 11. Known Gotchas & Edge Cases

| Issue | Mitigation |
|---|---|
| **Windows unhooks LL hooks if callback is slow** | Keep hook callbacks < 100ms. Offload all work to background threads. |
| **Some BT devices send keyboard media keys, not mouse codes** | This is why we hook both. Discovery Mode will reveal what your device sends. |
| **UAC-elevated windows can't receive hooks from non-elevated processes** | Run the interceptor as admin if you need to intercept input in elevated windows. Or sign and register as a Windows service. |
| **Remote Desktop suppresses local hooks** | LL hooks don't work over RDP. Use locally only. |
| **BT device reconnects get new handle** | Device path (VID/PID) is stable across reconnects, but the `hDevice` handle changes. Match on device path substring, not handle. |
| **MultiPress vs SinglePress conflict** | Delay SinglePress recognition by the MultiPress time window to disambiguate. |
| **Chord detection order** | Fire chord gesture on the *last* key down that completes the chord. Track all held keys. |
| **WH_MOUSE_LL doesn't receive horizontal scroll** | Use `WM_MOUSEHWHEEL` (0x020E) if needed — often omitted from examples. |

---

## 12. Future Enhancements

- **WPF Configuration GUI** — visual gesture builder with live preview
- **Profile Switching** — different gesture sets per active application (detect foreground window)
- **Named Pipe / gRPC IPC** — allow other apps (like Hey Nexus / Electron apps) to register gesture callbacks at runtime
- **Plugin System** — load action DLLs dynamically
- **Gesture recording** — "record a gesture" mode that watches input and creates the definition automatically
