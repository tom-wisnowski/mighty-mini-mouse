using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using BtInputInterceptor.Logging;

namespace BtInputInterceptor;

static class Program
{
    [STAThread]
    static void Main()
    {
        // ── Global exception handlers — log EVERYTHING before crashing ──
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Logger.Instance.Fatal("UNHANDLED EXCEPTION (AppDomain)", ex ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown"));
            Debug.WriteLine($"[BtInput][FATAL] Unhandled: {ex?.Message}");
        };

        Application.ThreadException += (_, e) =>
        {
            Logger.Instance.Fatal("UNHANDLED EXCEPTION (UI thread)", e.Exception);
            Debug.WriteLine($"[BtInput][FATAL] UI thread: {e.Exception.Message}");
        };

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        // Single instance enforcement
        using var mutex = new Mutex(true, "BtInputInterceptor_SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("BT Input Interceptor is already running.", "Already Running",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Debug.WriteLine("[BtInput] ========================================");
        Debug.WriteLine("[BtInput] BT Input Interceptor starting...");
        Debug.WriteLine($"[BtInput] PID: {Environment.ProcessId}");
        Debug.WriteLine($"[BtInput] .NET: {Environment.Version}");
        Debug.WriteLine($"[BtInput] OS: {Environment.OSVersion}");
        Debug.WriteLine($"[BtInput] 64-bit: {Environment.Is64BitProcess}");
        Debug.WriteLine($"[BtInput] Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Debug.WriteLine("[BtInput] ========================================");

        TrayApplication? trayApp = null;
        try
        {
            trayApp = new TrayApplication();
            trayApp.Initialize();

            Debug.WriteLine("[BtInput] Initialization complete. Entering message loop.");

            // Application.Run starts the Windows message loop.
            // This is REQUIRED for:
            //   1. Low-level hooks to receive callbacks
            //   2. NotifyIcon (tray icon) to work
            //   3. Raw Input WM_INPUT messages to be dispatched
            Application.Run();
        }
        catch (Exception ex)
        {
            Logger.Instance.Fatal("FATAL: Exception during application run", ex);
            Debug.WriteLine($"[BtInput][FATAL] {ex.GetType().Name}: {ex.Message}");
            MessageBox.Show($"Fatal error: {ex.Message}\n\nSee log for details.",
                "BT Input Interceptor", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Debug.WriteLine("[BtInput] ========================================");
            Debug.WriteLine("[BtInput] Shutting down...");
            Logger.Instance.Info("=== Application shutting down ===");

            trayApp?.Shutdown();
            trayApp?.Dispose();

            Logger.Instance.Info("=== Application shutdown complete ===");
            Debug.WriteLine("[BtInput] Shutdown complete.");
            Debug.WriteLine("[BtInput] ========================================");
        }
    }
}
