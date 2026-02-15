using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using BtInputInterceptor.Logging;
using static BtInputInterceptor.Hooks.NativeMethods;

namespace BtInputInterceptor.Hooks;

/// <summary>
/// Hidden window that receives WM_INPUT messages and dispatches them to
/// <see cref="RawInputManager.ProcessRawInput"/>. This window must exist
/// for raw input device-handle tracking to work.
/// </summary>
internal class RawInputWindow : NativeWindow, IDisposable
{
    private readonly RawInputManager _rawInputManager;
    private bool _disposed;

    public RawInputWindow(RawInputManager rawInputManager)
    {
        _rawInputManager = rawInputManager;

        // Create a message-only window (HWND_MESSAGE parent)
        var cp = new CreateParams
        {
            Caption = "BtInputInterceptor_RawInput",
            Parent = new IntPtr(-3) // HWND_MESSAGE
        };
        CreateHandle(cp);

        Debug.WriteLine($"[BtInput][RAW-WINDOW] Hidden raw input window created: handle={Handle}");

        // Register for raw input on this window
        _rawInputManager.RegisterForRawInput(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_INPUT)
        {
            _rawInputManager.ProcessRawInput(m.LParam);
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (Handle != IntPtr.Zero)
        {
            Debug.WriteLine("[BtInput][RAW-WINDOW] Destroying raw input window");
            DestroyHandle();
        }
    }
}
