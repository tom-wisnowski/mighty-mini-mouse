using System;
using System.Drawing;
using System.Windows.Forms;
using MightyMiniMouse.Actions;
using MightyMiniMouse.Config;
using MightyMiniMouse.Gestures;
using MightyMiniMouse.Hooks;
using MightyMiniMouse.Logging;
using Microsoft.Win32;

namespace MightyMiniMouse;

public class TrayApplication : IDisposable
{
    private NotifyIcon? _trayIcon;
    private ContextMenuStrip? _contextMenu;
    private ToolStripMenuItem? _modeSubmenu;

    private MouseHook? _mouseHook;
    private KeyboardHook? _keyboardHook;
    private RawInputManager? _rawInputManager;
    private RawInputWindow? _rawInputWindow;
    private GestureEngine? _gestureEngine;
    private MightyMiniMouse.Services.DeviceManager? _deviceManager;

    private AppConfig _config = new();
    private ModeDefinition _activeMode = new();
    private bool _enabled = true;
    private bool _disposed;
    private bool _dialogOpen;
    private Form? _activeDialog;
    private string? _selectedDevicePath;
    private SynchronizationContext? _syncContext;

    // ── Settings dialog recording mode ──
    // When non-null, the next mouse/key event is captured here for the dialog
    // Passed arguments: <inputKey>, <deviceId>
    private Action<string, string>? _recordCallback;

    /// <summary>
    /// Temporarily enters "recording" mode. The next intercepted input event
    /// from the target device will be passed as an InputKey string and device identifier 
    /// to the callback, then recording mode is automatically cleared.
    /// </summary>
    public void StartRecording(Action<string, string> callback) => _recordCallback = callback;
    public void StopRecording() => _recordCallback = null;

    /// <summary>
    /// Fetches the device ID for the most recent hardware input event processed by the raw input message pump.
    /// This is used for fallback recording scenarios where WinForms UI events fire instead of low-level hooks.
    /// </summary>
    public string GetLastInputDeviceId()
    {
        if (_rawInputManager != null)
        {
            var hDevice = _rawInputManager.GetLastSeenDeviceHandle();
            if (hDevice != IntPtr.Zero)
            {
                string path = RawInputManager.GetDeviceName(hDevice);
                return RawInputManager.ExtractVidPid(path) ?? path;
            }
        }
        return "";
    }

    public void Initialize()
    {
        // Load configuration — two phases:
        // 1) Pre-initialize logger from the raw config so migration messages are captured
        // 2) Run full Load() which may trigger version migration
        try
        {
            // Pre-read logging config to initialize logger early (before migration runs)
            PreInitializeLogger();
            _config = ConfigManager.Load();
        }
        catch (Exception ex)
        {
            DiagnosticOutput.LogError(DiagnosticOutput.CategoryConfig, "Failed to load configuration, using defaults", ex);
            MessageBox.Show($"Failed to load configuration: {ex.Message}\n\nUsing defaults.",
                "Config Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _config = new AppConfig();
        }

        // SyncContext will be captured lazily on the first hook callback,
        // after Application.Run() has installed the WinForms context.
        _syncContext = null;

        // Re-initialize logger with final config (in case migration changed anything)
        var logLevel = Enum.TryParse<LogLevel>(_config.Logging.LogLevel, true, out var level)
            ? level : LogLevel.Info;
        Logger.Initialize(_config.Logging.Enabled, _config.Logging.LogFile, logLevel);

        // Configure diagnostic output category filtering
        DiagnosticOutput.Configure(_config.Logging.SuppressedCategories);

        // Resolve active mode
        _activeMode = _config.Modes.FirstOrDefault(m => m.Id == _config.ActiveModeId)
                      ?? _config.Modes[0];

        DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryLifecycle, "===================================================");
        DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryLifecycle, "=== Mighty Mini Mouse starting ===");
        DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryLifecycle, $"  PID: {Environment.ProcessId}");
        DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryLifecycle, $"  .NET: {Environment.Version}");
        DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryLifecycle, $"  OS: {Environment.OSVersion}");
        DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryLifecycle, $"  Active mode: {_activeMode.Name} ({_activeMode.Gestures.Count} gesture(s))");
        DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryLifecycle, $"  Modes: {_config.Modes.Count} defined");
        DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryLifecycle, $"  Config dir: {ConfigManager.AppDataDir}");
        DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryLifecycle, $"  Logging: level={logLevel}, file={_config.Logging.LogFile}");
        if (_config.TargetDevice.Enabled)
            DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryLifecycle, $"  Target device: {_config.TargetDevice.DevicePath ?? "(not set)"}");
        else
            DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryLifecycle, "  Target device: filtering disabled (accepting all)");
        DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryLifecycle, "===================================================");

        // Set up system tray icon
        SetupTrayIcon();
        DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryLifecycle, "Tray icon initialized.");

        // Initialize input pipeline
        InitializeHooks();

        // Set up startup registration if configured
        if (_config.StartWithWindows)
            RegisterStartup();

        DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryLifecycle, "Initialization complete. Interceptor is active.");
    }

    /// <summary>
    /// Pre-initialize the logger by reading logging settings from the raw config file.
    /// This ensures migration log messages are captured before ConfigManager.Load() runs.
    /// </summary>
    private static void PreInitializeLogger()
    {
        try
        {
            if (!System.IO.File.Exists(ConfigManager.ConfigPath)) return;

            var json = System.IO.File.ReadAllText(ConfigManager.ConfigPath);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            bool enabled = true;
            string logFile = "interceptor.log";
            string logLevelStr = "Info";
            List<string>? suppressedCategories = null;

            if (root.TryGetProperty("logging", out var logging))
            {
                if (logging.TryGetProperty("enabled", out var e)) enabled = e.GetBoolean();
                if (logging.TryGetProperty("logFile", out var f)) logFile = f.GetString() ?? logFile;
                if (logging.TryGetProperty("logLevel", out var l)) logLevelStr = l.GetString() ?? logLevelStr;
                if (logging.TryGetProperty("suppressedCategories", out var sc) && sc.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    suppressedCategories = [];
                    foreach (var item in sc.EnumerateArray())
                    {
                        var val = item.GetString();
                        if (val != null) suppressedCategories.Add(val);
                    }
                }
            }

            var logLevel = Enum.TryParse<LogLevel>(logLevelStr, true, out var level)
                ? level : LogLevel.Info;
            Logger.Initialize(enabled, logFile, logLevel);
            DiagnosticOutput.Configure(suppressedCategories);
        }
        catch
        {
            // If pre-init fails, Logger stays as the disabled default — migration messages
            // won't be logged but the app will still work fine.
        }
    }

    private void SetupTrayIcon()
    {
        _contextMenu = new ContextMenuStrip();

        var enableItem = new ToolStripMenuItem(_enabled ? "✓ Enabled" : "  Disabled");
        enableItem.Click += (_, _) => ToggleEnabled(enableItem);

        // Mode submenu — quick-switch active mode
        _modeSubmenu = new ToolStripMenuItem("Mode");
        BuildModeSubmenu();
        _contextMenu.Items.Add(_modeSubmenu);
        _contextMenu.Items.Add(new ToolStripSeparator());

        // Device picker — opens a dialog
        var devicePickerItem = new ToolStripMenuItem("Device Settings...");
        devicePickerItem.Click += (_, _) => ShowDevicePicker();

        _contextMenu.Items.Add(devicePickerItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(new ToolStripMenuItem("Mappings...", null, (_, _) => ShowSettingsDialog()));
        _contextMenu.Items.Add(new ToolStripMenuItem("Configure JSON...", null, (_, _) => OpenConfig()));
        _contextMenu.Items.Add(new ToolStripMenuItem("View Log...", null, (_, _) => OpenLog()));
        _contextMenu.Items.Add(new ToolStripSeparator());

        var startupItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = _config.StartWithWindows,
            CheckOnClick = true
        };
        startupItem.CheckedChanged += (_, _) => ToggleStartWithWindows(startupItem);
        _contextMenu.Items.Add(startupItem);

        _contextMenu.Items.Add(enableItem);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApplication()));

        _trayIcon = new NotifyIcon
        {
            Text = $"Mighty Mini Mouse — {_activeMode.Name}",
            Icon = CreateDefaultIcon(),
            ContextMenuStrip = _contextMenu,
            Visible = true
        };

        _trayIcon.DoubleClick += (_, _) => ShowSettingsDialog();

        // Set up the notification bridge so actions can show balloon tips
        TrayNotificationBridge.TrayIcon = _trayIcon;
    }

    private void BuildModeSubmenu()
    {
        if (_modeSubmenu == null) return;
        _modeSubmenu.DropDownItems.Clear();

        foreach (var mode in _config.Modes)
        {
            var item = new ToolStripMenuItem(mode.Name)
            {
                Checked = mode.Id == _activeMode.Id,
                Tag = mode.Id
            };
            item.Click += (s, _) =>
            {
                if (s is ToolStripMenuItem mi && mi.Tag is string modeId)
                    SwitchMode(modeId);
            };
            _modeSubmenu.DropDownItems.Add(item);
        }
    }

    private void SwitchMode(string modeId)
    {
        var mode = _config.Modes.FirstOrDefault(m => m.Id == modeId);
        if (mode == null)
        {
            DiagnosticOutput.LogError(DiagnosticOutput.CategoryLifecycle, $"SwitchMode: mode ID '{modeId}' not found in config ({_config.Modes.Count} modes available)");
            return;
        }

        string previousModeName = _activeMode.Name;
        int previousGestureCount = _activeMode.Gestures.Count;

        _activeMode = mode;
        _config.ActiveModeId = mode.Id;
        ConfigManager.Save(_config);

        DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryLifecycle, $"Mode switch: '{previousModeName}' ({previousGestureCount} gestures) → '{_activeMode.Name}' ({_activeMode.Gestures.Count} gestures)");

        // Update the gesture engine with the new mode's gestures
        _gestureEngine?.UpdateGestures(_activeMode.Gestures, _activeMode.Name);

        // Update menu check marks
        if (_modeSubmenu != null)
        {
            foreach (ToolStripMenuItem mi in _modeSubmenu.DropDownItems)
                mi.Checked = (mi.Tag as string) == modeId;
        }

        // Update tooltip
        if (_trayIcon != null)
            _trayIcon.Text = $"Mighty Mini Mouse — {_activeMode.Name}";

        DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryLifecycle, $"════════════════════════════════════════");
        DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryLifecycle, $"  MODE SWITCHED → {_activeMode.Name} ({_activeMode.Gestures.Count} gesture(s))");
        DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryLifecycle, $"════════════════════════════════════════");

        // CRITICAL: The mode switch runs inside the context menu's modal message loop.
        // When this modal loop exits, Windows can silently stop delivering keyboard
        // hook events until the main message pump processes a new input message.
        // Fix: schedule a deferred keyboard hook reinstall via SyncContext.Post.
        // This only executes AFTER the context menu's modal loop has fully returned
        // control to Application.Run()'s message pump, which is exactly when it's
        // safe to reinstall the hook.
        if (_keyboardHook != null && _syncContext != null)
        {
            DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryLifecycle, "Scheduling deferred keyboard hook reinstall (post-context-menu)");
            _syncContext.Post(_ =>
            {
                if (_keyboardHook == null) return;
                DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryLifecycle, "Deferred hook reinstall: executing now (message pump is active)");
                try
                {
                    _keyboardHook.Reinstall();
                    DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryLifecycle, $"Keyboard hook reinstalled successfully, new handle={(_keyboardHook.IsInstalled ? "valid" : "INVALID")}");
                }
                catch (Exception ex)
                {
                    DiagnosticOutput.LogError(DiagnosticOutput.CategoryLifecycle, "Failed to reinstall keyboard hook (deferred)", ex);
                }
            }, null);
        }
    }

    private void InitializeHooks()
    {
        _rawInputManager = new RawInputManager();
        _deviceManager = new MightyMiniMouse.Services.DeviceManager(_config, _rawInputManager);
        _rawInputWindow = new RawInputWindow(_rawInputManager);
        DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryLifecycle, $"Raw input window created, handle={_rawInputWindow.Handle}");

        // If device filtering is enabled, try to find the target device
        if (_config.TargetDevice.Enabled && !string.IsNullOrWhiteSpace(_config.TargetDevice.DevicePath))
        {
            if (!_rawInputManager.SetTargetDeviceByPath(_config.TargetDevice.DevicePath))
            {
                DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryDevice, $"Target device not found: {_config.TargetDevice.DevicePath}. Accepting all devices.");
            }
        }

        // Initialize gesture engine with active mode's gestures
        _gestureEngine = new GestureEngine(_activeMode.Gestures, _activeMode.Name);
        _gestureEngine.OnGestureRecognized += OnGestureRecognized;
        DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryGestureEngine, $"Gesture engine initialized with {_activeMode.Gestures.Count} definition(s) from mode '{_activeMode.Name}'.");

        // Install mouse hook
        try
        {
            _mouseHook = new MouseHook();
            _mouseHook.OnMouseEvent += OnMouseEvent;
            _mouseHook.Install();
            DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryLifecycle, "Mouse hook installed successfully.");
        }
        catch (Exception ex)
        {
            DiagnosticOutput.LogError(DiagnosticOutput.CategoryLifecycle, "FAILED to install mouse hook", ex);
        }

        // Install keyboard hook
        try
        {
            _keyboardHook = new KeyboardHook();
            _keyboardHook.OnKeyEvent += OnKeyEvent;
            _keyboardHook.Install();
            DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryLifecycle, "Keyboard hook installed successfully.");
        }
        catch (Exception ex)
        {
            DiagnosticOutput.LogError(DiagnosticOutput.CategoryLifecycle, "FAILED to install keyboard hook", ex);
        }
    }

    private bool OnMouseEvent(MouseHookEventArgs args)
    {
        if (!_enabled || _dialogOpen) return false;

        // Lazily capture SyncContext on the first hook callback
        _syncContext ??= SynchronizationContext.Current;

        var button = args.GetButton();
        var buttonName = button.ToString();

        // Get originating device ID
        string deviceId = "";
        if (_rawInputManager != null)
        {
            var hDevice = _rawInputManager.GetLastSeenDeviceHandle();
            if (hDevice != IntPtr.Zero)
            {
                string path = RawInputManager.GetDeviceName(hDevice);
                deviceId = RawInputManager.ExtractVidPid(path) ?? path;
                DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryMouseMove, $"Resolved deviceId='{deviceId}' from hDevice={hDevice}");
            }
        }

        // Recording mode — capture from ANY device (before device filter)
        if (_recordCallback != null && args.IsDown && button != MouseButton.Unknown)
        {
            string inputKey = $"Mouse.{button}";
            var cb = _recordCallback;
            _recordCallback = null;
            
            // Dispatch asynchronously to let WM_INPUT process and update raw device handle
            _syncContext?.Post(async _ =>
            {
                await System.Threading.Tasks.Task.Delay(30);
                string captureDeviceId = "";
                if (_rawInputManager != null)
                {
                    var hDevice = _rawInputManager.GetLastSeenDeviceHandle();
                    if (hDevice != IntPtr.Zero)
                    {
                        string path = RawInputManager.GetDeviceName(hDevice);
                        captureDeviceId = RawInputManager.ExtractVidPid(path) ?? path;
                    }
                }
                DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryDevice, $"Captured mouse: {inputKey} on device {captureDeviceId}");
                cb.Invoke(inputKey, captureDeviceId);
            }, null);

            return true;
        }

        // Check device filter
        bool fromTarget = _selectedDevicePath == null || _rawInputManager!.IsFromTargetDevice();

        // Only log actual button events (skip mouse-move / Unknown)
        if (button != MouseButton.Unknown)
        {
            string action = args.IsDown ? "DOWN" : args.IsUp ? "UP" : "EVENT";
            string targetTag = fromTarget ? "" : " [other device]";
            DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryMouseButton, $"{buttonName,-12} {action,-6}  pos=({args.PointX},{args.PointY})  data=0x{args.MouseData:X4}{targetTag}");
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
            Timestamp = args.Timestamp,
            DeviceId = deviceId
        };

        DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryGestureEngine, $"Feeding: {inputEvent.InputKey} {state} (Device: {deviceId})");
        bool suppress = _gestureEngine?.ProcessInput(inputEvent) ?? false;
        if (suppress)
        {
            DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryMouseButton, $"Mouse input suppressed: {inputEvent.InputKey} {state}");
        }
        return suppress;
    }

    private bool OnKeyEvent(KeyboardHookEventArgs args)
    {
        // ALWAYS log key events to trace finger mouse keyboard input
        string keyName;
        try { keyName = ((ConsoleKey)args.VirtualKeyCode).ToString(); }
        catch { keyName = $"0x{args.VirtualKeyCode:X2}"; }
        DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryKeyHook, $"{keyName} {(args.IsKeyDown ? "DOWN" : "UP")}  vk=0x{args.VirtualKeyCode:X2}  enabled={_enabled} dialogOpen={_dialogOpen} recording={_recordCallback != null}");

        if (!_enabled || _dialogOpen) return false;

        // Get originating device ID
        string deviceId = "";
        if (_rawInputManager != null)
        {
            var hDevice = _rawInputManager.GetLastSeenDeviceHandle();
            if (hDevice != IntPtr.Zero)
            {
                string path = RawInputManager.GetDeviceName(hDevice);
                deviceId = RawInputManager.ExtractVidPid(path) ?? path;
                DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryKeyHook, $"Resolved deviceId='{deviceId}' from hDevice={hDevice}");
            }
        }

        // Recording mode — capture from ANY device (before device filter)
        if (_recordCallback != null && args.IsKeyDown)
        {
            string inputKey = $"Key.{keyName}";
            var cb = _recordCallback;
            _recordCallback = null;
            
            // Dispatch asynchronously to let WM_INPUT process and update raw device handle
            _syncContext?.Post(async _ =>
            {
                await System.Threading.Tasks.Task.Delay(30);
                string captureDeviceId = "";
                if (_rawInputManager != null)
                {
                    var hDevice = _rawInputManager.GetLastSeenDeviceHandle();
                    if (hDevice != IntPtr.Zero)
                    {
                        string path = RawInputManager.GetDeviceName(hDevice);
                        captureDeviceId = RawInputManager.ExtractVidPid(path) ?? path;
                    }
                }
                DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryDevice, $"Captured key: {inputKey} on device {captureDeviceId}");
                cb.Invoke(inputKey, captureDeviceId);
            }, null);

            return true;
        }

        // Check device filter
        bool fromTarget = _selectedDevicePath == null || _rawInputManager!.IsFromTargetDevice();

        string action = args.IsKeyDown ? "DOWN" : "UP";
        string targetTag = fromTarget ? "" : " [other device]";
        DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryKeyHook, $"{keyName,-12} {action,-6}  vk=0x{args.VirtualKeyCode:X2}  scan=0x{args.ScanCode:X4}{targetTag}");

        if (!fromTarget) return false;

        var inputEvent = new InputEvent
        {
            Type = InputType.KeyPress,
            State = args.IsKeyDown ? PressState.Down : PressState.Up,
            VirtualKeyCode = args.VirtualKeyCode,
            Timestamp = args.Timestamp,
            DeviceId = deviceId
        };

        bool suppress = _gestureEngine?.ProcessInput(inputEvent) ?? false;
        if (suppress)
        {
            DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryKeyHook, $"Keyboard input suppressed: {inputEvent.InputKey} {inputEvent.State}");
        }
        return suppress;
    }

    private void OnGestureRecognized(GestureDefinition gesture)
    {
        DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryGestureEngine, $"★ RECOGNIZED: {gesture.Name} [{gesture.Type}] [Mode: {_activeMode.Name}]");
        DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryAction, $"Action type: {gesture.Action.Type}");
        DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryAction, $"Keystroke: {gesture.Action.Keystroke}");

        try
        {
            var action = ActionFactory.Create(gesture.Action);
            DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryAction, $"Created: {action.GetType().Name}");

            // SendInput MUST run on a thread attached to a desktop.
            // Use SynchronizationContext captured during init to dispatch on UI thread.
            if (_syncContext != null)
            {
                _syncContext.Post(async _ =>
                {
                    try
                    {
                        DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryAction, $"Executing {action.GetType().Name} on UI thread...");
                        await action.ExecuteAsync();
                        DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryAction, "Execution complete.");
                    }
                    catch (Exception ex)
                    {
                        DiagnosticOutput.LogError(DiagnosticOutput.CategoryAction, $"Action execution failed for gesture '{gesture.Name}'", ex);
                    }
                }, null);
            }
            else
            {
                DiagnosticOutput.LogWarning(DiagnosticOutput.CategoryAction, "No SyncContext, running on Task.Run");
                Task.Run(async () =>
                {
                    try
                    {
                        await action.ExecuteAsync();
                    }
                    catch (Exception ex)
                    {
                        DiagnosticOutput.LogError(DiagnosticOutput.CategoryAction, $"Action execution failed for gesture '{gesture.Name}'", ex);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            DiagnosticOutput.LogError(DiagnosticOutput.CategoryAction, $"Failed to create action for gesture '{gesture.Name}'", ex);
        }
    }

    private void ToggleEnabled(ToolStripMenuItem menuItem)
    {
        _enabled = !_enabled;
        menuItem.Text = _enabled ? "✓ Enabled" : "  Disabled";
        DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryLifecycle, $"Interceptor {(_enabled ? "enabled" : "disabled")}.");

        _trayIcon!.Icon = _enabled ? CreateDefaultIcon() : CreateDisabledIcon();
    }

    private void ShowSettingsDialog()
    {
        if (_activeDialog != null)
        {
            if (_activeDialog.WindowState == FormWindowState.Minimized) _activeDialog.WindowState = FormWindowState.Normal;
            _activeDialog.Activate();
            return;
        }

        var dialog = new SettingsDialog(this, _config, _deviceManager!);
        _activeDialog = dialog;
        dialog.FormClosed += (_, _) => _activeDialog = null;

        if (dialog.ShowDialog() == DialogResult.OK)
        {
            // Reload config and refresh mode-related state
            _config = ConfigManager.Load();
            _activeMode = _config.Modes.FirstOrDefault(m => m.Id == _config.ActiveModeId)
                          ?? _config.Modes[0];

            _gestureEngine?.Dispose();
            _gestureEngine = new GestureEngine(_activeMode.Gestures, _activeMode.Name);
            _gestureEngine.OnGestureRecognized += OnGestureRecognized;

            // Rebuild mode submenu to reflect any added/removed/renamed modes
            BuildModeSubmenu();

            if (_trayIcon != null)
                _trayIcon.Text = $"Mighty Mini Mouse — {_activeMode.Name}";

            DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryConfig, $"Configuration reloaded. Active mode: {_activeMode.Name}");
        }
    }

    private void OpenConfig()
    {
        try
        {
            var configPath = Config.ConfigManager.ConfigPath;

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
                MessageBox.Show($"Config file not found:\n{configPath}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            DiagnosticOutput.LogError(DiagnosticOutput.CategoryConfig, "Failed to open config", ex);
        }
    }

    private void ShowDevicePicker()
    {
        if (_rawInputManager == null) return;
        
        if (_activeDialog != null)
        {
            if (_activeDialog.WindowState == FormWindowState.Minimized) _activeDialog.WindowState = FormWindowState.Normal;
            _activeDialog.Activate();
            return;
        }

        // Pause gesture/keystroke processing while dialog is open
        _dialogOpen = true;
        DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryDevice, "Dialog opening — gesture processing PAUSED");

        try
        {
            using var dialog = new DevicePickerDialog(_rawInputManager, _selectedDevicePath, _deviceManager!);
            _activeDialog = dialog;
            
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
            _activeDialog = null;
            _dialogOpen = false;
            DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryDevice, "Dialog closed — gesture processing RESUMED");
        }
    }

    private void SelectDevice(IntPtr? handle, string? devicePath)
    {
        _selectedDevicePath = devicePath;

        if (handle == null || devicePath == null)
        {
            _rawInputManager?.SetTargetDevice(IntPtr.Zero);
            DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryDevice, "Filter cleared — accepting all mice");
        }
        else
        {
            // Use path-based matching — raw input handles from WM_INPUT don't
            // always match enumeration handles, so match by device path instead.
            if (!(_rawInputManager?.SetTargetDeviceByPath(devicePath) ?? false))
            {
                // Fallback to direct handle if path match fails
                _rawInputManager?.SetTargetDevice(handle.Value);
                DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryDevice, $"Path match failed, using handle: {handle.Value}");
            }
            DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryDevice, $"Now targeting: {devicePath}");
        }
    }

    private void OpenLog()
    {
        try
        {
            var logPath = Config.ConfigManager.ResolveLogPath(_config.Logging.LogFile);

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
                MessageBox.Show($"Log file not found:\n{logPath}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            DiagnosticOutput.LogError(DiagnosticOutput.CategoryConfig, "Failed to open log", ex);
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
                key.SetValue("MightyMiniMouse", $"\"{exePath}\"");
                DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryConfig, "Registered for Windows startup.");
            }
        }
        catch (Exception ex)
        {
            DiagnosticOutput.LogError(DiagnosticOutput.CategoryConfig, "Failed to register startup", ex);
        }
    }

    private static void UnregisterStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            key?.DeleteValue("MightyMiniMouse", throwOnMissingValue: false);
            DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryConfig, "Unregistered from Windows startup.");
        }
        catch (Exception ex)
        {
            DiagnosticOutput.LogError(DiagnosticOutput.CategoryConfig, "Failed to unregister startup", ex);
        }
    }

    private void ToggleStartWithWindows(ToolStripMenuItem item)
    {
        _config.StartWithWindows = item.Checked;
        if (item.Checked)
            RegisterStartup();
        else
            UnregisterStartup();

        ConfigManager.Save(_config);
        DiagnosticOutput.LogDebug(DiagnosticOutput.CategoryConfig, $"Start with Windows: {item.Checked}");
    }

    private void ExitApplication()
    {
        DiagnosticOutput.LogInfo(DiagnosticOutput.CategoryLifecycle, "=== Mighty Mini Mouse shutting down ===");
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
