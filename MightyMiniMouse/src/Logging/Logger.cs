using System;
using System.IO;

namespace MightyMiniMouse.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
    Fatal
}

public sealed class Logger : IDisposable
{
    private readonly StreamWriter? _writer;
    private readonly LogLevel _minLevel;
    private readonly object _lock = new();
    private bool _disposed;

    public static Logger Instance { get; private set; } = new(enabled: false);

    public Logger(bool enabled, string? logFile = null, LogLevel minLevel = LogLevel.Info)
    {
        _minLevel = minLevel;

        if (enabled && !string.IsNullOrWhiteSpace(logFile))
        {
            var logPath = Config.ConfigManager.ResolveLogPath(logFile);

            _writer = new StreamWriter(logPath, append: true)
            {
                AutoFlush = true
            };
        }
    }

    public static void Initialize(bool enabled, string? logFile = null, LogLevel minLevel = LogLevel.Info)
    {
        Instance?.Dispose();
        Instance = new Logger(enabled, logFile, minLevel);
    }

    public void Log(LogLevel level, string message)
    {
        if (level < _minLevel || _writer == null || _disposed) return;

        lock (_lock)
        {
            _writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level,-7}] {message}");
        }
    }

    public void Debug(string message) => Log(LogLevel.Debug, message);
    public void Info(string message) => Log(LogLevel.Info, message);
    public void Warning(string message) => Log(LogLevel.Warning, message);
    public void Error(string message) => Log(LogLevel.Error, message);
    public void Fatal(string message) => Log(LogLevel.Fatal, message);

    /// <summary>
    /// Log an error with full exception details including stack trace.
    /// </summary>
    public void Error(string message, Exception ex) =>
        Log(LogLevel.Error, $"{message}\n  Exception: {ex.GetType().FullName}: {ex.Message}\n  StackTrace: {ex.StackTrace}");

    /// <summary>
    /// Log a fatal/unhandled exception with full details.
    /// </summary>
    public void Fatal(string message, Exception ex) =>
        Log(LogLevel.Fatal, $"{message}\n  Exception: {ex.GetType().FullName}: {ex.Message}\n  StackTrace: {ex.StackTrace}");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writer?.Flush();
        _writer?.Dispose();
    }
}

