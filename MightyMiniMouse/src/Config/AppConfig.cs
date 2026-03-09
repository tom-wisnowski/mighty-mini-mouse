using MightyMiniMouse.Actions;
using MightyMiniMouse.Gestures;

namespace MightyMiniMouse.Config;

public class AppConfig
{
    /// <summary>
    /// Schema version for upgrade detection.
    /// v1 = legacy flat gestures, v2 = modes-based, v3 = suppressedCategories.
    /// </summary>
    public int ConfigVersion { get; set; } = 3;

    public DeviceConfig TargetDevice { get; set; } = new();

    /// <summary>
    /// Legacy flat gesture list. Kept for backward-compat deserialization.
    /// On load, ConfigManager migrates this into Modes if Modes is empty.
    /// </summary>
    public List<GestureDefinition> Gestures { get; set; } = [];

    public List<ModeDefinition> Modes { get; set; } = [];

    /// <summary>
    /// ID of the last-used mode. Persisted across restarts.
    /// </summary>
    public string? ActiveModeId { get; set; }

    public List<MouseDevice> KnownDevices { get; set; } = [];
    public LoggingConfig Logging { get; set; } = new();
    public bool StartWithWindows { get; set; } = false;
}

/// <summary>
/// A named collection of gesture mappings.
/// </summary>
public class ModeDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "Default";
    public List<GestureDefinition> Gestures { get; set; } = [];
}

public class DeviceConfig
{
    public bool Enabled { get; set; } = false;
    public string? DevicePath { get; set; }
    public string? FriendlyName { get; set; }
}

public class LoggingConfig
{
    public bool Enabled { get; set; } = true;
    public string LogFile { get; set; } = "interceptor.log";
    public string LogLevel { get; set; } = "Info";

    /// <summary>
    /// Diagnostic output categories to suppress. Events in these categories
    /// will not appear in either the log file or the debug output stream.
    /// Remove a category from this list to re-enable it.
    /// </summary>
    public List<string> SuppressedCategories { get; set; } = ["MouseMove", "RawInput"];
}
