using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static MightyMiniMouse.Hooks.NativeMethods;

namespace MightyMiniMouse.Hooks;

public sealed class KeyboardHook : IDisposable
{
    private IntPtr _hookId = IntPtr.Zero;
    private readonly LowLevelHookProc _proc;

    /// <summary>
    /// Fires for every keyboard event. Return true from handler to suppress the input.
    /// </summary>
    public event Func<KeyboardHookEventArgs, bool>? OnKeyEvent;

    /// <summary>
    /// Timestamp of the last keyboard event received by the hook.
    /// Used for hook health monitoring — if this stops updating, Windows may have silently removed the hook.
    /// </summary>
    public DateTime LastEventTime { get; private set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether the hook handle is currently valid (non-zero).
    /// </summary>
    public bool IsInstalled => _hookId != IntPtr.Zero;

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
                $"Failed to install keyboard hook. Error: {Marshal.GetLastWin32Error()}");

        LastEventTime = DateTime.UtcNow;
        Logging.DiagnosticOutput.LogInfo(Logging.DiagnosticOutput.CategoryKeyHook, $"Keyboard hook installed, handle={_hookId}");
    }

    /// <summary>
    /// Reinstall the keyboard hook. Call this when Windows has silently removed it.
    /// </summary>
    public void Reinstall()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
        Install();
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        LastEventTime = DateTime.UtcNow;
        try
        {
            if (nCode >= 0)
            {
                var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

                // RAW hook-level log — before any filtering
                string rawKeyName;
                try { rawKeyName = ((ConsoleKey)hookStruct.vkCode).ToString(); }
                catch { rawKeyName = $"0x{hookStruct.vkCode:X2}"; }
                bool isInjected = (hookStruct.flags & LLKHF_INJECTED) != 0;
                string injectedTag = isInjected ? " [INJECTED]" : "";
                Logging.DiagnosticOutput.LogInfo(Logging.DiagnosticOutput.CategoryKeyHook, $"Key={rawKeyName} vk=0x{hookStruct.vkCode:X2} flags=0x{hookStruct.flags:X4}{injectedTag}");

                // Skip injected events
                if (isInjected)
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
        }
        catch (Exception ex)
        {
            Logging.DiagnosticOutput.LogError(Logging.DiagnosticOutput.CategoryKeyHook, "Exception in keyboard hook callback", ex);
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
