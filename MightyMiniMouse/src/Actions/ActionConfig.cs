namespace MightyMiniMouse.Actions;

/// <summary>
/// Serializable configuration model for an action.
/// </summary>
public class ActionConfig
{
    /// <summary>
    /// Action type identifier: notification, launch, keystroke, webhook, powershell
    /// </summary>
    public string Type { get; set; } = "notification";

    /// <summary>For launch: exe path. For powershell: script path.</summary>
    public string? Path { get; set; }

    /// <summary>For launch: command line args.</summary>
    public string? Arguments { get; set; }

    /// <summary>For webhook: URL to call.</summary>
    public string? Url { get; set; }

    /// <summary>For webhook: HTTP method (GET, POST, etc.).</summary>
    public string? HttpMethod { get; set; }

    /// <summary>For webhook: request body.</summary>
    public string? Body { get; set; }

    /// <summary>For keystroke: e.g. "Ctrl+Shift+M".</summary>
    public string? Keystroke { get; set; }

    /// <summary>For notification: toast message to display.</summary>
    public string? Message { get; set; }
}
