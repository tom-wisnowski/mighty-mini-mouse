using System;
using System.Diagnostics;
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

    private MouseHook? _mouseHook;
    private KeyboardHook? _keyboardHook;
    private RawInputManager? _rawInputManager;
    private RawInputWindow? _rawInputWindow;
    private GestureEngine? _gestureEngine;
    private MightyMiniMouse.Services.DeviceManager? _deviceManager;

    private AppConfig _config = new();
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
        Logger.Instance.Info("=== Mighty Mini Mouse starting ===");
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
        Debug.WriteLine("[MMM] Initialization complete. Interceptor is active.");
    }

    private void SetupTrayIcon()
    {
        _contextMenu = new ContextMenuStrip();

        var enableItem = new ToolStripMenuItem(_enabled ? "✓ Enabled" : "  Disabled");
        enableItem.Click += (_, _) => ToggleEnabled(enableItem);

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
            Text = "Mighty Mini Mouse",
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
        _rawInputManager = new RawInputManager();
        _deviceManager = new MightyMiniMouse.Services.DeviceManager(_config, _rawInputManager);
        _rawInputWindow = new RawInputWindow(_rawInputManager);
        Debug.WriteLine($"[MMM][INIT] Raw input window created, handle={_rawInputWindow.Handle}");

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

        // Get originating device ID
        string deviceId = "";
        if (_rawInputManager != null)
        {
            var hDevice = _rawInputManager.GetLastSeenDeviceHandle();
            if (hDevice != IntPtr.Zero)
            {
                string path = RawInputManager.GetDeviceName(hDevice);
                deviceId = RawInputManager.ExtractVidPid(path) ?? path;
                Debug.WriteLine($"[MMM][MOUSE-HOOK] Resolved deviceId='{deviceId}' from hDevice={hDevice}");
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
                Debug.WriteLine($"[MMM][RECORD] ✓ Captured mouse: {inputKey} on device {captureDeviceId}");
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
            Debug.WriteLine($"[MMM][MOUSE] {buttonName,-12} {action,-6}  pos=({args.PointX},{args.PointY})  data=0x{args.MouseData:X4}{targetTag}");
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

        Debug.WriteLine($"[MMM][GESTURE-IN] Feeding: {inputEvent.InputKey} {state} (Device: {deviceId})");
        return _gestureEngine?.ProcessInput(inputEvent) ?? false;
    }

    private bool OnKeyEvent(KeyboardHookEventArgs args)
    {
        // ALWAYS log key events to trace finger mouse keyboard input
        string keyName;
        try { keyName = ((ConsoleKey)args.VirtualKeyCode).ToString(); }
        catch { keyName = $"0x{args.VirtualKeyCode:X2}"; }
        Debug.WriteLine($"[MMM][KEY-HOOK] {keyName} {(args.IsKeyDown ? "DOWN" : "UP")}  vk=0x{args.VirtualKeyCode:X2}  enabled={_enabled} dialogOpen={_dialogOpen} recording={_recordCallback != null}");

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
                Debug.WriteLine($"[MMM][KEY-HOOK] Resolved deviceId='{deviceId}' from hDevice={hDevice}");
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
                Debug.WriteLine($"[MMM][RECORD] ✓ Captured key: {inputKey} on device {captureDeviceId}");
                cb.Invoke(inputKey, captureDeviceId);
            }, null);

            return true;
        }

        // Check device filter
        bool fromTarget = _selectedDevicePath == null || _rawInputManager!.IsFromTargetDevice();

        string action = args.IsKeyDown ? "DOWN" : "UP";
        string targetTag = fromTarget ? "" : " [other device]";
        Debug.WriteLine($"[MMM][KEY]   {keyName,-12} {action,-6}  vk=0x{args.VirtualKeyCode:X2}  scan=0x{args.ScanCode:X4}{targetTag}");

        if (!fromTarget) return false;

        var inputEvent = new InputEvent
        {
            Type = InputType.KeyPress,
            State = args.IsKeyDown ? PressState.Down : PressState.Up,
            VirtualKeyCode = args.VirtualKeyCode,
            Timestamp = args.Timestamp,
            DeviceId = deviceId
        };

        return _gestureEngine?.ProcessInput(inputEvent) ?? false;
    }

    private void OnGestureRecognized(GestureDefinition gesture)
    {
        Logger.Instance.Info($"Gesture recognized: {gesture.Name} [{gesture.Type}]");
        Debug.WriteLine($"[MMM][GESTURE] ★ RECOGNIZED: {gesture.Name} [{gesture.Type}]");
        Debug.WriteLine($"[MMM][GESTURE]   Action type: {gesture.Action.Type}");
        Debug.WriteLine($"[MMM][GESTURE]   Keystroke: {gesture.Action.Keystroke}");

        try
        {
            var action = ActionFactory.Create(gesture.Action);
            Debug.WriteLine($"[MMM][ACTION] Created: {action.GetType().Name}");

            // SendInput MUST run on a thread attached to a desktop.
            // Use SynchronizationContext captured during init to dispatch on UI thread.
            if (_syncContext != null)
            {
                _syncContext.Post(async _ =>
                {
                    try
                    {
                        Debug.WriteLine($"[MMM][ACTION] Executing {action.GetType().Name} on UI thread...");
                        await action.ExecuteAsync();
                        Debug.WriteLine($"[MMM][ACTION] Execution complete.");
                    }
                    catch (Exception ex)
                    {
                        Logger.Instance.Error($"Action execution failed for gesture '{gesture.Name}'", ex);
                        Debug.WriteLine($"[MMM][ACTION] EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                    }
                }, null);
            }
            else
            {
                Debug.WriteLine($"[MMM][ACTION] WARNING: No SyncContext, running on Task.Run");
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
            Debug.WriteLine($"[MMM][ACTION] FAILED to create: {ex.Message}");
        }
    }

    private void ToggleEnabled(ToolStripMenuItem menuItem)
    {
        _enabled = !_enabled;
        menuItem.Text = _enabled ? "✓ Enabled" : "  Disabled";
        Logger.Instance.Info($"Interceptor {(_enabled ? "enabled" : "disabled")}.");
        Debug.WriteLine($"[MMM] Interceptor {(_enabled ? "enabled" : "disabled")}.");

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
            // Reload gestures into the running engine
            _config = ConfigManager.Load();
            _gestureEngine?.Dispose();
            _gestureEngine = new GestureEngine(_config.Gestures);
            _gestureEngine.OnGestureRecognized += OnGestureRecognized;
            Debug.WriteLine("[MMM] Configuration reloaded from settings dialog.");
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
        
        if (_activeDialog != null)
        {
            if (_activeDialog.WindowState == FormWindowState.Minimized) _activeDialog.WindowState = FormWindowState.Normal;
            _activeDialog.Activate();
            return;
        }

        // Pause gesture/keystroke processing while dialog is open
        _dialogOpen = true;
        Debug.WriteLine("[MMM][PICKER] Dialog opening — gesture processing PAUSED");

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
            Debug.WriteLine("[MMM][PICKER] Dialog closed — gesture processing RESUMED");
        }
    }

    private void SelectDevice(IntPtr? handle, string? devicePath)
    {
        _selectedDevicePath = devicePath;

        if (handle == null || devicePath == null)
        {
            _rawInputManager?.SetTargetDevice(IntPtr.Zero);
            Debug.WriteLine("[MMM][DEVICE] Filter cleared — accepting all mice");
        }
        else
        {
            // Use path-based matching — raw input handles from WM_INPUT don't
            // always match enumeration handles, so match by device path instead.
            if (!(_rawInputManager?.SetTargetDeviceByPath(devicePath) ?? false))
            {
                // Fallback to direct handle if path match fails
                _rawInputManager?.SetTargetDevice(handle.Value);
                Debug.WriteLine($"[MMM][DEVICE] Path match failed, using handle: {handle.Value}");
            }
            Debug.WriteLine($"[MMM][DEVICE] Now targeting: {devicePath}");
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
                key.SetValue("MightyMiniMouse", $"\"{exePath}\"");
                Logger.Instance.Info("Registered for Windows startup.");
            }
        }
        catch (Exception ex)
        {
            Logger.Instance.Error("Failed to register startup", ex);
        }
    }

    private static void UnregisterStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            key?.DeleteValue("MightyMiniMouse", throwOnMissingValue: false);
            Logger.Instance.Info("Unregistered from Windows startup.");
        }
        catch (Exception ex)
        {
            Logger.Instance.Error("Failed to unregister startup", ex);
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
        Debug.WriteLine($"[MMM] Start with Windows: {item.Checked}");
    }

    private void ExitApplication()
    {
        Logger.Instance.Info("=== Mighty Mini Mouse shutting down ===");
        Debug.WriteLine("[MMM] Shutting down.");
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
