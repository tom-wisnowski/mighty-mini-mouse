using BtInputInterceptor.Actions;
using BtInputInterceptor.Gestures;

namespace BtInputInterceptor.Config;

public class AppConfig
{
    public DeviceConfig TargetDevice { get; set; } = new();
    public List<GestureDefinition> Gestures { get; set; } = [];
    public LoggingConfig Logging { get; set; } = new();
    public bool StartWithWindows { get; set; } = false;
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
}
