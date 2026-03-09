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
    public const int CurrentConfigVersion = 3;

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
    ///   v2 → v3: add SuppressedCategories to logging config.
    ///   (future: v3 → v4, etc.)
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
            DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryConfig, $"Migrating config v{config.ConfigVersion} → v2: converting flat gestures to modes...");

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

            DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryConfig, $"  Created 'Default' mode with {defaultMode.Gestures.Count} gesture(s).");
            config.ConfigVersion = 2;
            migrated = true;
        }

        // ── v2 → v3: Add SuppressedCategories to logging ──
        if (config.ConfigVersion < 3)
        {
            DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryConfig, $"Migrating config v{config.ConfigVersion} → v3: adding suppressedCategories to logging...");

            // Populate with defaults if the list is empty (it will be empty for existing v2 configs
            // since the JSON had no suppressedCategories field and the C# default-initializer
            // only applies to freshly constructed objects, not deserialized ones).
            if (config.Logging.SuppressedCategories == null || config.Logging.SuppressedCategories.Count == 0)
            {
                config.Logging.SuppressedCategories = ["MouseMove", "RawInput"];
            }

            DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryConfig, $"  SuppressedCategories: [{string.Join(", ", config.Logging.SuppressedCategories)}]");
            config.ConfigVersion = 3;
            migrated = true;
        }

        // Ensure ActiveModeId points to a valid mode
        if (config.ActiveModeId == null ||
            !config.Modes.Any(m => m.Id == config.ActiveModeId))
        {
            config.ActiveModeId = config.Modes[0].Id;
        }

        if (migrated)
        {
            DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryConfig, $"Config migration complete. Now at v{config.ConfigVersion}.");
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

