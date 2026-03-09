using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace MightyMiniMouse.Logging;

/// <summary>
/// Centralized diagnostic output utility that wraps both file logging (Logger)
/// and debug output stream (Debug.WriteLine) with category-based filtering.
/// 
/// Usage:
///   DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryMouseMove, "message");
///   DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryLifecycle, "message");
///   DiagnosticOutput.LogError(DiagnosticOutput.CategoryLifecycle, "message", ex);
/// 
/// Categories in the SuppressedCategories list are silently dropped from both
/// the log file and the debug output stream. To re-enable, remove the category
/// from the suppressedCategories array in config.json and restart.
/// </summary>
public static class DiagnosticOutput
{
    // ── Predefined category constants ──
    public const string CategoryMouseMove = "MouseMove";
    public const string CategoryRawInput = "RawInput";
    public const string CategoryMouseButton = "MouseButton";
    public const string CategoryKeyHook = "KeyHook";
    public const string CategoryGestureEngine = "GestureEngine";
    public const string CategoryAction = "Action";
    public const string CategoryLifecycle = "Lifecycle";
    public const string CategoryConfig = "Config";
    public const string CategoryDevice = "Device";

    private static readonly HashSet<string> _suppressedCategories =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object _lock = new();

    /// <summary>
    /// Configure which categories are suppressed. Called at startup from config.
    /// </summary>
    public static void Configure(List<string>? suppressedCategories)
    {
        lock (_lock)
        {
            _suppressedCategories.Clear();
            if (suppressedCategories != null)
            {
                foreach (var cat in suppressedCategories)
                    _suppressedCategories.Add(cat);
            }
        }
    }

    /// <summary>
    /// Enable or disable suppression for a specific category at runtime.
    /// </summary>
    public static void SetSuppressed(string category, bool suppressed)
    {
        lock (_lock)
        {
            if (suppressed)
                _suppressedCategories.Add(category);
            else
                _suppressedCategories.Remove(category);
        }
    }

    /// <summary>
    /// Returns true if the given category is currently suppressed.
    /// </summary>
    public static bool IsSuppressed(string category)
    {
        lock (_lock)
        {
            return _suppressedCategories.Contains(category);
        }
    }

    // ── Core log method ──

    /// <summary>
    /// Log a message to both the file logger and the debug output stream,
    /// unless the category is suppressed.
    /// </summary>
    public static void Log(string category, LogLevel level, string message)
    {
        if (IsSuppressed(category)) return;

        string prefix = $"[MMM][{category}]";

        // File logger
        Logger.Instance.Log(level, $"[{category}] {message}");

        // Debug output stream
        Debug.WriteLine($"{prefix} {message}");
    }

    // ── Convenience methods ──

    public static void LogDebug(string category, string message) =>
        Log(category, LogLevel.Debug, message);

    public static void LogInfo(string category, string message) =>
        Log(category, LogLevel.Info, message);

    public static void LogWarning(string category, string message) =>
        Log(category, LogLevel.Warning, message);

    public static void LogError(string category, string message) =>
        Log(category, LogLevel.Error, message);

    public static void LogError(string category, string message, Exception ex) =>
        Log(category, LogLevel.Error,
            $"{message}\n  Exception: {ex.GetType().FullName}: {ex.Message}\n  StackTrace: {ex.StackTrace}");

    public static void LogFatal(string category, string message) =>
        Log(category, LogLevel.Fatal, message);

    public static void LogFatal(string category, string message, Exception ex) =>
        Log(category, LogLevel.Fatal,
            $"{message}\n  Exception: {ex.GetType().FullName}: {ex.Message}\n  StackTrace: {ex.StackTrace}");
}
