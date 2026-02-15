using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BtInputInterceptor.Config;

public class ConfigManager
{
    /// <summary>
    /// User-writable config directory: %AppData%\BtInputInterceptor
    /// </summary>
    public static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BtInputInterceptor");

    private static readonly string ConfigPath = Path.Combine(AppDataDir, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static AppConfig Load()
    {
        // Ensure the AppData directory exists
        Directory.CreateDirectory(AppDataDir);

        if (!File.Exists(ConfigPath))
        {
            var defaults = CreateDefaults();
            Save(defaults);
            return defaults;
        }

        var json = File.ReadAllText(ConfigPath);
        return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? CreateDefaults();
    }

    public static void Save(AppConfig config)
    {
        Directory.CreateDirectory(AppDataDir);
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(ConfigPath, json);
    }

    /// <summary>
    /// Resolve a log file path. Relative paths are resolved against AppDataDir.
    /// </summary>
    public static string ResolveLogPath(string logFile)
    {
        return Path.IsPathRooted(logFile)
            ? logFile
            : Path.Combine(AppDataDir, logFile);
    }

    private static AppConfig CreateDefaults()
    {
        return new AppConfig
        {
            TargetDevice = new DeviceConfig
            {
                Enabled = false,
                DevicePath = null,
                FriendlyName = null
            },
            Gestures = [],
            Logging = new LoggingConfig
            {
                Enabled = true,
                LogFile = "interceptor.log",
                LogLevel = "Info"
            },
            StartWithWindows = false
        };
    }
}

