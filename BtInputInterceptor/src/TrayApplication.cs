using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using BtInputInterceptor.Actions;
using BtInputInterceptor.Config;
using BtInputInterceptor.Gestures;
using BtInputInterceptor.Hooks;
using BtInputInterceptor.Logging;
using Microsoft.Win32;

namespace BtInputInterceptor;

public class TrayApplication : IDisposable
{
    private NotifyIcon? _trayIcon;
    private ContextMenuStrip? _contextMenu;

    private MouseHook? _mouseHook;
    private KeyboardHook? _keyboardHook;
    private RawInputManager? _rawInputManager;
    private RawInputWindow? _rawInputWindow;
    private GestureEngine? _gestureEngine;

    private AppConfig _config = new();
    private bool _enabled = true;
    private bool _disposed;
    private bool _dialogOpen;
    private string? _selectedDevicePath;
    private SynchronizationContext? _syncContext;

    // ── Settings dialog recording mode ──
    // When non-null, the next mouse/key event is captured here for the dialog
    private Action<string>? _recordCallback;

    /// <summary>
    /// Temporarily enters "recording" mode. The next intercepted input event
    /// from the target device will be passed as an InputKey string to the callback,
    /// then recording mode is automatically cleared.
    /// </summary>
    public void StartRecording(Action<string> callback) => _recordCallback = callback;
    public void StopRecording() => _recordCallback = null;

    public void Initialize()
    {
        // Load configuration
        try
        {
            _config = ConfigManager.Load();
        }
        catch (Exception ex)
        {
            Logger.Instance.Error("Failed to load configuration, using defaults", ex);
            MessageBox.Show($"Failed to load configuration: {ex.Message}\n\nUsing defaults.",
                "Config Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _config = new AppConfig();
        }

        // SyncContext will be captured lazily on the first hook callback,
        // after Application.Run() has installed the WinForms context.
        _syncContext = null;

        // Initialize logger
        var logLevel = Enum.TryParse<LogLevel>(_config.Logging.LogLevel, true, out var level)
            ? level : LogLevel.Info;
        Logger.Initialize(_config.Logging.Enabled, _config.Logging.LogFile, logLevel);

        Logger.Instance.Info("===================================================");
        Logger.Instance.Info("=== BT Input Interceptor starting ===");
        Logger.Instance.Info($"  PID: {Environment.ProcessId}");
        Logger.Instance.Info($"  .NET: {Environment.Version}");
        Logger.Instance.Info($"  OS: {Environment.OSVersion}");
        Logger.Instance.Info($"  Config: {_config.Gestures.Count} gesture(s) loaded");
        Logger.Instance.Info($"  Config dir: {ConfigManager.AppDataDir}");
        Logger.Instance.Info($"  Logging: level={logLevel}, file={_config.Logging.LogFile}");
        if (_config.TargetDevice.Enabled)
            Logger.Instance.Info($"  Target device: {_config.TargetDevice.DevicePath ?? "(not set)"}");
        else
            Logger.Instance.Info("  Target device: filtering disabled (accepting all)");
        Logger.Instance.Info("===================================================");

        // Set up system tray icon
        SetupTrayIcon();
        Logger.Instance.Info("Tray icon initialized.");

        // Initialize input pipeline
        InitializeHooks();

        // Set up startup registration if configured
        if (_config.StartWithWindows)
            RegisterStartup();

        Logger.Instance.Info("Initialization complete. Interceptor is active.");
        Debug.WriteLine("[BtInput] Initialization complete. Interceptor is active.");
    }

    private void SetupTrayIcon()
    {
        _contextMenu = new ContextMenuStrip();

        var enableItem = new ToolStripMenuItem(_enabled ? "✓ Enabled" : "  Disabled");
        enableItem.Click += (_, _) => ToggleEnabled(enableItem);

        // Device picker — opens a dialog
        var devicePickerItem = new ToolStripMenuItem("Select Device...");
        devicePickerItem.Click += (_, _) => ShowDevicePicker();

        _contextMenu.Items.Add(devicePickerItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(new ToolStripMenuItem("Settings...", null, (_, _) => ShowSettingsDialog()));
        _contextMenu.Items.Add(new ToolStripMenuItem("Configure JSON...", null, (_, _) => OpenConfig()));
        _contextMenu.Items.Add(new ToolStripMenuItem("View Log...", null, (_, _) => OpenLog()));
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(enableItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApplication()));

        _trayIcon = new NotifyIcon
        {
            Text = "BT Input Interceptor",
            Icon = CreateDefaultIcon(),
            ContextMenuStrip = _contextMenu,
            Visible = true
        };

        _trayIcon.DoubleClick += (_, _) => ShowSettingsDialog();

        // Set up the notification bridge so actions can show balloon tips
        TrayNotificationBridge.TrayIcon = _trayIcon;
    }

    private void InitializeHooks()
    {
        // Initialize Raw Input manager + hidden window for WM_INPUT
        _rawInputManager = new RawInputManager();
        _rawInputWindow = new RawInputWindow(_rawInputManager);
        Debug.WriteLine($"[BtInput][INIT] Raw input window created, handle={_rawInputWindow.Handle}");

        // If device filtering is enabled, try to find the target device
        if (_config.TargetDevice.Enabled && !string.IsNullOrWhiteSpace(_config.TargetDevice.DevicePath))
        {
            if (!_rawInputManager.SetTargetDeviceByPath(_config.TargetDevice.DevicePath))
            {
                Logger.Instance.Warning($"Target device not found: {_config.TargetDevice.DevicePath}. Accepting all devices.");
            }
        }

        // Initialize gesture engine
        _gestureEngine = new GestureEngine(_config.Gestures);
        _gestureEngine.OnGestureRecognized += OnGestureRecognized;
        Logger.Instance.Info($"Gesture engine initialized with {_config.Gestures.Count} definition(s).");

        // Install mouse hook
        try
        {
            _mouseHook = new MouseHook();
            _mouseHook.OnMouseEvent += OnMouseEvent;
            _mouseHook.Install();
            Logger.Instance.Info("Mouse hook installed successfully.");
        }
        catch (Exception ex)
        {
            Logger.Instance.Error("FAILED to install mouse hook", ex);
        }

        // Install keyboard hook
        try
        {
            _keyboardHook = new KeyboardHook();
            _keyboardHook.OnKeyEvent += OnKeyEvent;
            _keyboardHook.Install();
            Logger.Instance.Info("Keyboard hook installed successfully.");
        }
        catch (Exception ex)
        {
            Logger.Instance.Error("FAILED to install keyboard hook", ex);
        }
    }

    private bool OnMouseEvent(MouseHookEventArgs args)
    {
        if (!_enabled || _dialogOpen) return false;

        // Lazily capture SyncContext on the first hook callback
        _syncContext ??= SynchronizationContext.Current;

        var button = args.GetButton();
        var buttonName = button.ToString();

        // Recording mode — capture from ANY device (before device filter)
        if (_recordCallback != null && args.IsDown && button != MouseButton.Unknown)
        {
            string inputKey = $"Mouse.{button}";
            Debug.WriteLine($"[BtInput][RECORD] ✓ Captured mouse: {inputKey}");
            var cb = _recordCallback;
            _recordCallback = null;
            cb.Invoke(inputKey);
            return true;
        }

        // Check device filter
        bool fromTarget = _selectedDevicePath == null || _rawInputManager!.IsFromTargetDevice();

        // Only log actual button events (skip mouse-move / Unknown)
        if (button != MouseButton.Unknown)
        {
            string action = args.IsDown ? "DOWN" : args.IsUp ? "UP" : "EVENT";
            string targetTag = fromTarget ? "" : " [other device]";
            Debug.WriteLine($"[BtInput][MOUSE] {buttonName,-12} {action,-6}  pos=({args.PointX},{args.PointY})  data=0x{args.MouseData:X4}{targetTag}");
        }

        if (!fromTarget) return false;
        if (button == MouseButton.Unknown) return false;

        PressState state;
        if (args.IsDown) state = PressState.Down;
        else if (args.IsUp) state = PressState.Up;
        else return false;

        var inputEvent = new InputEvent
        {
            Type = InputType.MouseButton,
            State = state,
            Button = button,
            Timestamp = args.Timestamp
        };

        Debug.WriteLine($"[BtInput][GESTURE-IN] Feeding: {inputEvent.InputKey} {state}");
        return _gestureEngine?.ProcessInput(inputEvent) ?? false;
    }

    private bool OnKeyEvent(KeyboardHookEventArgs args)
    {
        // ALWAYS log key events to trace finger mouse keyboard input
        string keyName;
        try { keyName = ((ConsoleKey)args.VirtualKeyCode).ToString(); }
        catch { keyName = $"0x{args.VirtualKeyCode:X2}"; }
        Debug.WriteLine($"[BtInput][KEY-HOOK] {keyName} {(args.IsKeyDown ? "DOWN" : "UP")}  vk=0x{args.VirtualKeyCode:X2}  enabled={_enabled} dialogOpen={_dialogOpen} recording={_recordCallback != null}");

        if (!_enabled || _dialogOpen) return false;

        // Recording mode — capture from ANY device (before device filter)
        if (_recordCallback != null && args.IsKeyDown)
        {
            string inputKey = $"Key.{keyName}";
            Debug.WriteLine($"[BtInput][RECORD] ✓ Captured key: {inputKey}");
            var cb = _recordCallback;
            _recordCallback = null;
            cb.Invoke(inputKey);
            return true;
        }

        // Check device filter
        bool fromTarget = _selectedDevicePath == null || _rawInputManager!.IsFromTargetDevice();

        string action = args.IsKeyDown ? "DOWN" : "UP";
        string targetTag = fromTarget ? "" : " [other device]";
        Debug.WriteLine($"[BtInput][KEY]   {keyName,-12} {action,-6}  vk=0x{args.VirtualKeyCode:X2}  scan=0x{args.ScanCode:X4}{targetTag}");

        if (!fromTarget) return false;

        var inputEvent = new InputEvent
        {
            Type = InputType.KeyPress,
            State = args.IsKeyDown ? PressState.Down : PressState.Up,
            VirtualKeyCode = args.VirtualKeyCode,
            Timestamp = args.Timestamp
        };

        return _gestureEngine?.ProcessInput(inputEvent) ?? false;
    }

    private void OnGestureRecognized(GestureDefinition gesture)
    {
        Logger.Instance.Info($"Gesture recognized: {gesture.Name} [{gesture.Type}]");
        Debug.WriteLine($"[BtInput][GESTURE] ★ RECOGNIZED: {gesture.Name} [{gesture.Type}]");
        Debug.WriteLine($"[BtInput][GESTURE]   Action type: {gesture.Action.Type}");
        Debug.WriteLine($"[BtInput][GESTURE]   Keystroke: {gesture.Action.Keystroke}");

        try
        {
            var action = ActionFactory.Create(gesture.Action);
            Debug.WriteLine($"[BtInput][ACTION] Created: {action.GetType().Name}");

            // SendInput MUST run on a thread attached to a desktop.
            // Use SynchronizationContext captured during init to dispatch on UI thread.
            if (_syncContext != null)
            {
                _syncContext.Post(async _ =>
                {
                    try
                    {
                        Debug.WriteLine($"[BtInput][ACTION] Executing {action.GetType().Name} on UI thread...");
                        await action.ExecuteAsync();
                        Debug.WriteLine($"[BtInput][ACTION] Execution complete.");
                    }
                    catch (Exception ex)
                    {
                        Logger.Instance.Error($"Action execution failed for gesture '{gesture.Name}'", ex);
                        Debug.WriteLine($"[BtInput][ACTION] EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                    }
                }, null);
            }
            else
            {
                Debug.WriteLine($"[BtInput][ACTION] WARNING: No SyncContext, running on Task.Run");
                Task.Run(async () =>
                {
                    try
                    {
                        await action.ExecuteAsync();
                    }
                    catch (Exception ex)
                    {
                        Logger.Instance.Error($"Action execution failed for gesture '{gesture.Name}'", ex);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Instance.Error($"Failed to create action for gesture '{gesture.Name}'", ex);
            Debug.WriteLine($"[BtInput][ACTION] FAILED to create: {ex.Message}");
        }
    }

    private void ToggleEnabled(ToolStripMenuItem menuItem)
    {
        _enabled = !_enabled;
        menuItem.Text = _enabled ? "✓ Enabled" : "  Disabled";
        Logger.Instance.Info($"Interceptor {(_enabled ? "enabled" : "disabled")}.");
        Debug.WriteLine($"[BtInput] Interceptor {(_enabled ? "enabled" : "disabled")}.");

        _trayIcon!.Icon = _enabled ? CreateDefaultIcon() : CreateDisabledIcon();
    }

    private void ShowSettingsDialog()
    {
        var dialog = new SettingsDialog(this, _config);
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            // Reload gestures into the running engine
            _config = ConfigManager.Load();
            _gestureEngine?.Dispose();
            _gestureEngine = new GestureEngine(_config.Gestures);
            _gestureEngine.OnGestureRecognized += OnGestureRecognized;
            Debug.WriteLine("[BtInput] Configuration reloaded from settings dialog.");
        }
    }

    private void OpenConfig()
    {
        try
        {
            var configPath = System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "config.json");

            if (System.IO.File.Exists(configPath))
            {
                var psi = new System.Diagnostics.ProcessStartInfo(configPath)
                {
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
            else
            {
                MessageBox.Show("Config file not found.", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            Logger.Instance.Error("Failed to open config", ex);
        }
    }

    private void ShowDevicePicker()
    {
        if (_rawInputManager == null) return;

        // Pause gesture/keystroke processing while dialog is open
        _dialogOpen = true;
        Debug.WriteLine("[BtInput][PICKER] Dialog opening — gesture processing PAUSED");

        try
        {
            using var dialog = new DevicePickerDialog(_rawInputManager, _selectedDevicePath);
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                if (dialog.AllDevicesSelected)
                {
                    SelectDevice(null, null);
                }
                else if (dialog.SelectedDevice != null)
                {
                    SelectDevice(dialog.SelectedDevice.Handle, dialog.SelectedDevice.DevicePath);
                }
            }
        }
        finally
        {
            _dialogOpen = false;
            Debug.WriteLine("[BtInput][PICKER] Dialog closed — gesture processing RESUMED");
        }
    }

    private void SelectDevice(IntPtr? handle, string? devicePath)
    {
        _selectedDevicePath = devicePath;

        if (handle == null || devicePath == null)
        {
            _rawInputManager?.SetTargetDevice(IntPtr.Zero);
            Debug.WriteLine("[BtInput][DEVICE] Filter cleared — accepting all mice");
        }
        else
        {
            // Use path-based matching — raw input handles from WM_INPUT don't
            // always match enumeration handles, so match by device path instead.
            if (!(_rawInputManager?.SetTargetDeviceByPath(devicePath) ?? false))
            {
                // Fallback to direct handle if path match fails
                _rawInputManager?.SetTargetDevice(handle.Value);
                Debug.WriteLine($"[BtInput][DEVICE] Path match failed, using handle: {handle.Value}");
            }
            Debug.WriteLine($"[BtInput][DEVICE] Now targeting: {devicePath}");
        }
    }

    private void OpenLog()
    {
        try
        {
            var logPath = System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, _config.Logging.LogFile);

            if (System.IO.File.Exists(logPath))
            {
                var psi = new System.Diagnostics.ProcessStartInfo(logPath)
                {
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
            else
            {
                MessageBox.Show("Log file not found.", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            Logger.Instance.Error("Failed to open log", ex);
        }
    }

    private static void RegisterStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (key != null && exePath != null)
            {
                key.SetValue("BtInputInterceptor", $"\"{exePath}\"");
                Logger.Instance.Info("Registered for Windows startup.");
            }
        }
        catch (Exception ex)
        {
            Logger.Instance.Error("Failed to register startup", ex);
        }
    }

    private void ExitApplication()
    {
        Logger.Instance.Info("=== BT Input Interceptor shutting down ===");
        Debug.WriteLine("[BtInput] Shutting down.");
        Application.Exit();
    }

    public void Shutdown()
    {
        Dispose();
    }

    private static Icon CreateDefaultIcon()
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);

        using var brush = new SolidBrush(Color.FromArgb(0, 120, 215));
        g.FillEllipse(brush, 1, 1, 14, 14);

        using var font = new Font("Segoe UI", 8, FontStyle.Bold);
        using var textBrush = new SolidBrush(Color.White);
        g.DrawString("B", font, textBrush, 2, 0);

        return Icon.FromHandle(bmp.GetHicon());
    }

    private static Icon CreateDisabledIcon()
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);

        using var brush = new SolidBrush(Color.FromArgb(128, 128, 128));
        g.FillEllipse(brush, 1, 1, 14, 14);

        using var font = new Font("Segoe UI", 8, FontStyle.Bold);
        using var textBrush = new SolidBrush(Color.White);
        g.DrawString("B", font, textBrush, 2, 0);

        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _mouseHook?.Dispose();
        _keyboardHook?.Dispose();
        _rawInputWindow?.Dispose();
        _gestureEngine?.Dispose();

        TrayNotificationBridge.TrayIcon = null;
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        _contextMenu?.Dispose();
        Logger.Instance.Dispose();
    }
}
