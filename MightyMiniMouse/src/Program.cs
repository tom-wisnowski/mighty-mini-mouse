using System;
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
        // These use Logger directly because DiagnosticOutput may not be initialized,
        // and fatal errors must NEVER be filtered.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            DiagnosticOutput.LogFatal(DiagnosticOutput.CategoryLifecycle, "UNHANDLED EXCEPTION (AppDomain)",
                ex ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown"));
        };

        Application.ThreadException += (_, e) =>
        {
            DiagnosticOutput.LogFatal(DiagnosticOutput.CategoryLifecycle, "UNHANDLED EXCEPTION (UI thread)", e.Exception);
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

        DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryLifecycle, "========================================");
        DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryLifecycle, "Mighty Mini Mouse starting...");
        DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryLifecycle, $"PID: {Environment.ProcessId}");
        DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryLifecycle, $".NET: {Environment.Version}");
        DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryLifecycle, $"OS: {Environment.OSVersion}");
        DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryLifecycle, $"64-bit: {Environment.Is64BitProcess}");
        DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryLifecycle, $"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryLifecycle, "========================================");

        TrayApplication? trayApp = null;
        try
        {
            trayApp = new TrayApplication();
            trayApp.Initialize();

            DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryLifecycle, "Initialization complete. Entering message loop.");

            // Application.Run starts the Windows message loop.
            // This is REQUIRED for:
            //   1. Low-level hooks to receive callbacks
            //   2. NotifyIcon (tray icon) to work
            //   3. Raw Input WM_INPUT messages to be dispatched
            Application.Run();
        }
        catch (Exception ex)
        {
            DiagnosticOutput.LogFatal(DiagnosticOutput.CategoryLifecycle, "FATAL: Exception during application run", ex);
            MessageBox.Show($"Fatal error: {ex.Message}\n\nSee log for details.",
                "Mighty Mini Mouse", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryLifecycle, "========================================");
            DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryLifecycle, "Shutting down...");

            trayApp?.Shutdown();
            trayApp?.Dispose();

            DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryLifecycle, "Application shutdown complete.");
            DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryLifecycle, "========================================");
        }
    }
}
