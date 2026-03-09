using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static MightyMiniMouse.Hooks.NativeMethods;

namespace MightyMiniMouse.Hooks;

public sealed class MouseHook : IDisposable
{
    private IntPtr _hookId = IntPtr.Zero;
    private readonly LowLevelHookProc _proc;

    /// <summary>
    /// Fires for every mouse event. Return true from handler to suppress the input.
    /// </summary>
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
                $"Failed to install mouse hook. Error: {Marshal.GetLastWin32Error()}");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

                // Skip injected events (ones we or other software generated)
                if ((hookStruct.flags & LLMHF_INJECTED) != 0)
                    return CallNextHookEx(_hookId, nCode, wParam, lParam);

                var args = new MouseHookEventArgs
                {
                    MessageId = (int)wParam,
                    PointX = hookStruct.pt.X,
                    PointY = hookStruct.pt.Y,
                    MouseData = hookStruct.mouseData,
                    Timestamp = hookStruct.time
                };

                bool suppress = OnMouseEvent?.Invoke(args) ?? false;
                if (suppress)
                    return (IntPtr)1; // Block the input
            }
        }
        catch (Exception ex)
        {
            Logging.DiagnosticOutput.LogError(Logging.DiagnosticOutput.CategoryMouseButton, "Exception in mouse hook callback", ex);
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
    public int PointX { get; init; }
    public int PointY { get; init; }
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
        WM_MOUSEWHEEL or WM_MOUSEHWHEEL => MouseButton.Wheel,
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
