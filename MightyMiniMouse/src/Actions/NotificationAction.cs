using System.Windows.Forms;
using MightyMiniMouse.Logging;

namespace MightyMiniMouse.Actions;

public class NotificationAction : IAction
{
    private readonly string _message;

    public NotificationAction(string message)
    {
        _message = message;
    }

    public Task ExecuteAsync(CancellationToken ct = default)
    {
        try
        {
            // Use the tray icon's balloon tip if available, otherwise fall back to MessageBox
            // The TrayApplication sets this statically so actions can use it
            if (TrayNotificationBridge.TrayIcon != null)
            {
                TrayNotificationBridge.TrayIcon.BalloonTipTitle = "Mighty Mini Mouse";
                TrayNotificationBridge.TrayIcon.BalloonTipText = _message;
                TrayNotificationBridge.TrayIcon.BalloonTipIcon = ToolTipIcon.Info;
                TrayNotificationBridge.TrayIcon.ShowBalloonTip(3000);
            }
            else
            {
                // Fallback — shouldn't happen in normal operation
                MessageBox.Show(_message, "Mighty Mini Mouse", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryAction, $"Notification shown: {_message}");
        }
        catch (Exception ex)
        {
            DiagnosticOutput.LogError(DiagnosticOutput.CategoryAction, "Failed to show notification", ex);
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// Static bridge so notification actions can access the system tray icon
/// for balloon tip display without tight coupling.
/// </summary>
public static class TrayNotificationBridge
{
    public static NotifyIcon? TrayIcon { get; set; }
}
