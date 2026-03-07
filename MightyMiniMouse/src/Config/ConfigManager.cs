using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MightyMiniMouse.Logging;

namespace MightyMiniMouse.Config;

public class ConfigManager
{
    /// <summary>
    /// Current schema version. Bump this when making breaking config changes.
    /// </summary>
    public const int CurrentConfigVersion = 2;

    /// <summary>
    /// User-writable config directory: %AppData%\MightyMiniMouse
    /// </summary>
    public static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MightyMiniMouse");

    public static readonly string ConfigPath = Path.Combine(AppDataDir, "config.json");

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

        AppConfig config;
        if (!File.Exists(ConfigPath))
        {
            config = CreateDefaults();
            Save(config);
            return config;
        }

        var json = File.ReadAllText(ConfigPath);
        config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? CreateDefaults();
        MigrateIfNeeded(config);
        return config;
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

    /// <summary>
    /// Version-driven migration. Each upgrade step runs in order:
    ///   v0/v1 → v2: wrap legacy flat Gestures into a "Default" mode.
    ///   (future: v2 → v3, etc.)
    /// </summary>
    private static void MigrateIfNeeded(AppConfig config)
    {
        bool migrated = false;

        // Legacy configs won't have configVersion in the JSON at all,
        // so it deserializes as the default value (2). We detect the legacy
        // format by checking: no modes exist but gestures do (or no modes at all).
        bool isLegacyFormat = config.Modes.Count == 0;

        if (isLegacyFormat)
        {
            // Force version to 1 since this is clearly a pre-modes config
            config.ConfigVersion = 1;
        }

        // ── v1 → v2: Introduce modes ──
        if (config.ConfigVersion < 2)
        {
            Logger.Instance.Info($"Migrating config v{config.ConfigVersion} → v2: converting flat gestures to modes...");

            var defaultMode = new ModeDefinition
            {
                Name = "Default",
                Gestures = config.Gestures.Count > 0
                    ? new(config.Gestures)
                    : []
            };
            config.Modes.Add(defaultMode);
            config.ActiveModeId ??= defaultMode.Id;

            // Clear the legacy list so it doesn't linger in the JSON
            config.Gestures = [];

            Logger.Instance.Info($"  Created 'Default' mode with {defaultMode.Gestures.Count} gesture(s).");
            config.ConfigVersion = 2;
            migrated = true;
        }

        // ── (future: v2 → v3, etc.) ──

        // Ensure ActiveModeId points to a valid mode
        if (config.ActiveModeId == null ||
            !config.Modes.Any(m => m.Id == config.ActiveModeId))
        {
            config.ActiveModeId = config.Modes[0].Id;
        }

        if (migrated)
        {
            Logger.Instance.Info($"Config migration complete. Now at v{config.ConfigVersion}.");
            Save(config);
        }
    }

    private static AppConfig CreateDefaults()
    {
        var defaultMode = new ModeDefinition { Name = "Default" };
        return new AppConfig
        {
            ConfigVersion = CurrentConfigVersion,
            TargetDevice = new DeviceConfig
            {
                Enabled = false,
                DevicePath = null,
                FriendlyName = null
            },
            Gestures = [],
            Modes = [defaultMode],
            ActiveModeId = defaultMode.Id,
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

