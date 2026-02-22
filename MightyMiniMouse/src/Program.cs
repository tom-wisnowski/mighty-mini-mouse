using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using MightyMiniMouse.Logging;

namespace MightyMiniMouse;

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
            Debug.WriteLine($"[MMM][FATAL] Unhandled: {ex?.Message}");
        };

        Application.ThreadException += (_, e) =>
        {
            Logger.Instance.Fatal("UNHANDLED EXCEPTION (UI thread)", e.Exception);
            Debug.WriteLine($"[MMM][FATAL] UI thread: {e.Exception.Message}");
        };

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        // Single instance enforcement
        using var mutex = new Mutex(true, "MightyMiniMouse_SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("Mighty Mini Mouse is already running.", "Already Running",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Debug.WriteLine("[MMM] ========================================");
        Debug.WriteLine("[MMM] Mighty Mini Mouse starting...");
        Debug.WriteLine($"[MMM] PID: {Environment.ProcessId}");
        Debug.WriteLine($"[MMM] .NET: {Environment.Version}");
        Debug.WriteLine($"[MMM] OS: {Environment.OSVersion}");
        Debug.WriteLine($"[MMM] 64-bit: {Environment.Is64BitProcess}");
        Debug.WriteLine($"[MMM] Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Debug.WriteLine("[MMM] ========================================");

        TrayApplication? trayApp = null;
        try
        {
            trayApp = new TrayApplication();
            trayApp.Initialize();

            Debug.WriteLine("[MMM] Initialization complete. Entering message loop.");

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
            Debug.WriteLine($"[MMM][FATAL] {ex.GetType().Name}: {ex.Message}");
            MessageBox.Show($"Fatal error: {ex.Message}\n\nSee log for details.",
                "Mighty Mini Mouse", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Debug.WriteLine("[MMM] ========================================");
            Debug.WriteLine("[MMM] Shutting down...");
            Logger.Instance.Info("=== Application shutting down ===");

            trayApp?.Shutdown();
            trayApp?.Dispose();

            Logger.Instance.Info("=== Application shutdown complete ===");
            Debug.WriteLine("[MMM] Shutdown complete.");
            Debug.WriteLine("[MMM] ========================================");
        }
    }
}
