using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MightyMiniMouse.Actions;
using MightyMiniMouse.Config;
using MightyMiniMouse.Gestures;

namespace MightyMiniMouse;

/// <summary>
/// Settings dialog for visually recording mouse/keyboard events
/// and mapping them to keystroke actions. Supports multiple modes.
/// Saves directly to config.json.
/// </summary>
public class SettingsDialog : Form
{
    private readonly TrayApplication _app;
    private readonly AppConfig _config;
    private readonly MightyMiniMouse.Services.DeviceManager _deviceManager;

    // Mode management
    private readonly List<ModeDefinition> _modes;
    private ModeDefinition _selectedMode;

    private const int Pad = 16; // uniform padding

    // Controls — Mode bar
    private ComboBox _modeCombo = null!;
    private Button _newModeBtn = null!;
    private Button _copyModeBtn = null!;
    private Button _renameModeBtn = null!;
    private Button _deleteModeBtn = null!;

    // Controls — Mapping
    private ListView _mappingList = null!;
    private Button _recordBtn = null!;
    private TextBox _inputKeyBox = null!;
    private TextBox _keystrokeBox = null!;
    private TextBox _nameBox = null!;
    private TextBox _deviceBox = null!;
    private Button _addBtn = null!;
    private Button _removeBtn = null!;
    private Button _saveBtn = null!;
    private Button _cancelBtn = null!;
    private Label _statusLabel = null!;
    
    private string _recordedDeviceId = "";

    public SettingsDialog(TrayApplication app, AppConfig config, MightyMiniMouse.Services.DeviceManager deviceManager)
    {
        _app = app;
        _config = config;
        _deviceManager = deviceManager;

        // Deep-copy modes so edits are non-destructive until Save
        _modes = config.Modes.Select(m => new ModeDefinition
        {
            Id = m.Id,
            Name = m.Name,
            Gestures = m.Gestures.Select(g => new GestureDefinition
            {
                Id = g.Id,
                Name = g.Name,
                Type = g.Type,
                TargetDeviceId = g.TargetDeviceId,
                InputKeys = new List<string>(g.InputKeys),
                PressCount = g.PressCount,
                TimeWindowMs = g.TimeWindowMs,
                Action = new ActionConfig
                {
                    Type = g.Action.Type,
                    Path = g.Action.Path,
                    Arguments = g.Action.Arguments,
                    Url = g.Action.Url,
                    HttpMethod = g.Action.HttpMethod,
                    Body = g.Action.Body,
                    Keystroke = g.Action.Keystroke,
                    Message = g.Action.Message
                },
                SuppressInput = g.SuppressInput
            }).ToList()
        }).ToList();

        // Resolve selected mode (match active mode, or first)
        _selectedMode = _modes.FirstOrDefault(m => m.Id == config.ActiveModeId) ?? _modes[0];

        InitializeForm();
        PopulateModeCombo();
        PopulateList();
    }

    private void InitializeForm()
    {
        Text = "Mighty Mini Mouse — Mappings";
        ClientSize = new Size(740, 620);
        MinimumSize = new Size(620, 560);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        Font = new Font("Segoe UI", 9);
        BackColor = Color.FromArgb(30, 30, 30);
        KeyPreview = true; // Capture keys at form level (needed for BT HID recording)
        ForeColor = Color.White;
        Padding = new Padding(Pad);

        int y = Pad;
        int contentWidth = ClientSize.Width - (Pad * 2);

        // ── Mode Selector Bar ──
        var modeLabel = new Label
        {
            Text = "Mode:",
            Location = new Point(Pad, y + 4),
            AutoSize = true,
            ForeColor = Color.FromArgb(180, 180, 180),
            Font = new Font("Segoe UI", 9, FontStyle.Bold)
        };
        Controls.Add(modeLabel);

        _modeCombo = new ComboBox
        {
            Location = new Point(Pad + 50, y),
            Size = new Size(200, 26),
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(45, 45, 45),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _modeCombo.SelectedIndexChanged += OnModeSelectionChanged;
        Controls.Add(_modeCombo);

        int btnX = Pad + 260;
        int btnW = 70;
        int btnGap = 4;

        _newModeBtn = CreateSmallButton("New", btnX, y, btnW, Color.FromArgb(0, 120, 215));
        _newModeBtn.Click += NewMode;
        Controls.Add(_newModeBtn);
        btnX += btnW + btnGap;

        _copyModeBtn = CreateSmallButton("Copy", btnX, y, btnW, Color.FromArgb(0, 120, 215));
        _copyModeBtn.Click += CopyMode;
        Controls.Add(_copyModeBtn);
        btnX += btnW + btnGap;

        _renameModeBtn = CreateSmallButton("Rename", btnX, y, btnW, Color.FromArgb(80, 80, 80));
        _renameModeBtn.Click += RenameMode;
        Controls.Add(_renameModeBtn);
        btnX += btnW + btnGap;

        _deleteModeBtn = CreateSmallButton("Delete", btnX, y, btnW, Color.FromArgb(180, 50, 50));
        _deleteModeBtn.Click += DeleteMode;
        Controls.Add(_deleteModeBtn);

        y += 34;

        // ── Existing Mappings List ──
        var listLabel = new Label
        {
            Text = "Gesture Mappings",
            Location = new Point(Pad, y),
            AutoSize = true,
            ForeColor = Color.FromArgb(180, 180, 180),
            Font = new Font("Segoe UI", 9, FontStyle.Bold)
        };
        Controls.Add(listLabel);
        y += listLabel.Height + 4;

        _mappingList = new ListView
        {
            Location = new Point(Pad, y),
            Size = new Size(contentWidth, 170),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            BackColor = Color.FromArgb(45, 45, 45),
            ForeColor = Color.White,
            HeaderStyle = ColumnHeaderStyle.Nonclickable
        };
        _mappingList.Columns.Add("Name", 140);
        _mappingList.Columns.Add("Input", 140);
        _mappingList.Columns.Add("Action", 180);
        _mappingList.Columns.Add("Type", 80);
        _mappingList.Columns.Add("Device", 120);
        _mappingList.SelectedIndexChanged += (_, _) => UpdateButtonStates();
        Controls.Add(_mappingList);
        y += _mappingList.Height + 8;

        // ── Remove button ──
        _removeBtn = new Button
        {
            Text = "Remove Selected",
            Location = new Point(Pad, y),
            Size = new Size(130, 28),
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
            BackColor = Color.FromArgb(180, 50, 50),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Enabled = false
        };
        _removeBtn.Click += RemoveMapping;
        Controls.Add(_removeBtn);
        y += _removeBtn.Height + 16;

        // ── New Mapping Section ──
        var newLabel = new Label
        {
            Text = "Add New Mapping",
            Location = new Point(Pad, y),
            AutoSize = true,
            ForeColor = Color.FromArgb(180, 180, 180),
            Font = new Font("Segoe UI", 9, FontStyle.Bold)
        };
        Controls.Add(newLabel);
        y += newLabel.Height + 8;

        int labelWidth = 70;
        int fieldX = Pad + labelWidth + 4;
        int fieldWidth = 220;

        // Name
        Controls.Add(new Label
        {
            Text = "Name:",
            Location = new Point(Pad, y + 2),
            Size = new Size(labelWidth, 22),
            TextAlign = ContentAlignment.MiddleRight
        });
        _nameBox = new TextBox
        {
            Location = new Point(fieldX, y),
            Size = new Size(fieldWidth, 24),
            BackColor = Color.FromArgb(45, 45, 45),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        Controls.Add(_nameBox);
        y += 30;

        // Input Key (recorded)
        Controls.Add(new Label
        {
            Text = "Input:",
            Location = new Point(Pad, y + 2),
            Size = new Size(labelWidth, 22),
            TextAlign = ContentAlignment.MiddleRight
        });
        _inputKeyBox = new TextBox
        {
            Location = new Point(fieldX, y),
            Size = new Size(fieldWidth, 24),
            ReadOnly = true,
            BackColor = Color.FromArgb(55, 55, 55),
            ForeColor = Color.FromArgb(100, 200, 255),
            BorderStyle = BorderStyle.FixedSingle,
            Text = "(click Record, then use your device)"
        };
        Controls.Add(_inputKeyBox);

        _recordBtn = new Button
        {
            Text = "⏺ Record",
            Location = new Point(fieldX + fieldWidth + 8, y - 2),
            Size = new Size(100, 28),
            BackColor = Color.FromArgb(200, 60, 60),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _recordBtn.Click += ToggleRecording;
        Controls.Add(_recordBtn);
        y += 30;

        // Device
        Controls.Add(new Label
        {
            Text = "Device ID:",
            Location = new Point(Pad, y + 2),
            Size = new Size(labelWidth, 22),
            TextAlign = ContentAlignment.MiddleRight
        });
        _deviceBox = new TextBox
        {
            Location = new Point(fieldX, y),
            Size = new Size(fieldWidth, 24),
            ReadOnly = true,
            BackColor = Color.FromArgb(55, 55, 55),
            ForeColor = Color.FromArgb(180, 180, 180),
            BorderStyle = BorderStyle.FixedSingle,
            Text = "(Any Device)"
        };
        Controls.Add(_deviceBox);
        y += 30;

        // Keystroke mapping
        Controls.Add(new Label
        {
            Text = "Maps to:",
            Location = new Point(Pad, y + 2),
            Size = new Size(labelWidth, 22),
            TextAlign = ContentAlignment.MiddleRight
        });
        _keystrokeBox = new TextBox
        {
            Location = new Point(fieldX, y),
            Size = new Size(fieldWidth, 24),
            BackColor = Color.FromArgb(45, 45, 45),
            ForeColor = Color.FromArgb(100, 255, 100),
            BorderStyle = BorderStyle.FixedSingle,
            ReadOnly = true // we capture keys ourselves
        };
        Controls.Add(_keystrokeBox);

        var keystrokeHint = new Label
        {
            Text = "Click here, then press keys",
            Location = new Point(fieldX + fieldWidth + 8, y + 3),
            AutoSize = true,
            ForeColor = Color.FromArgb(120, 120, 120),
            Font = new Font("Segoe UI", 8)
        };
        Controls.Add(keystrokeHint);
        y += 30;

        // Add button
        _addBtn = new Button
        {
            Text = "Add Mapping",
            Location = new Point(fieldX, y),
            Size = new Size(130, 30),
            BackColor = Color.FromArgb(0, 120, 215),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _addBtn.Click += AddMapping;
        Controls.Add(_addBtn);
        y += 42;

        // ── Status label ──
        _statusLabel = new Label
        {
            Text = "",
            Location = new Point(Pad, y),
            Size = new Size(contentWidth - 260, 22),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            ForeColor = Color.FromArgb(100, 200, 100)
        };
        Controls.Add(_statusLabel);

        // ── Bottom buttons ──
        _saveBtn = new Button
        {
            Text = "Save && Close",
            Size = new Size(120, 32),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            BackColor = Color.FromArgb(0, 120, 215),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
            // No DialogResult here — we save first, then close manually
        };
        _saveBtn.Location = new Point(ClientSize.Width - Pad - _saveBtn.Width - 130, y);
        _saveBtn.Click += SaveAndClose;
        Controls.Add(_saveBtn);

        _cancelBtn = new Button
        {
            Text = "Cancel",
            Size = new Size(120, 32),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            BackColor = Color.FromArgb(60, 60, 60),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            DialogResult = DialogResult.Cancel
        };
        _cancelBtn.Location = new Point(ClientSize.Width - Pad - _cancelBtn.Width, y);
        Controls.Add(_cancelBtn);

        AcceptButton = _saveBtn;
        CancelButton = _cancelBtn;
    }

    private static Button CreateSmallButton(string text, int x, int y, int width, Color backColor)
    {
        return new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(width, 26),
            BackColor = backColor,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8)
        };
    }

    // ── Mode Management ──

    private void PopulateModeCombo()
    {
        _modeCombo.SelectedIndexChanged -= OnModeSelectionChanged;
        _modeCombo.Items.Clear();
        foreach (var mode in _modes)
            _modeCombo.Items.Add(mode.Name);

        int idx = _modes.IndexOf(_selectedMode);
        if (idx >= 0) _modeCombo.SelectedIndex = idx;

        _modeCombo.SelectedIndexChanged += OnModeSelectionChanged;
        UpdateModeButtonStates();
    }

    private void OnModeSelectionChanged(object? sender, EventArgs e)
    {
        if (_modeCombo.SelectedIndex < 0 || _modeCombo.SelectedIndex >= _modes.Count) return;
        _selectedMode = _modes[_modeCombo.SelectedIndex];
        PopulateList();
        UpdateModeButtonStates();
    }

    private void UpdateModeButtonStates()
    {
        _deleteModeBtn.Enabled = _modes.Count > 1;
    }

    private void NewMode(object? sender, EventArgs e)
    {
        string? name = PromptForName("New Mode", "Enter a name for the new mode:", "");
        if (string.IsNullOrWhiteSpace(name)) return;

        var newMode = new ModeDefinition { Name = name };
        _modes.Add(newMode);
        _selectedMode = newMode;
        PopulateModeCombo();
        PopulateList();

        _statusLabel.Text = $"Created mode: {name}";
        _statusLabel.ForeColor = Color.FromArgb(100, 200, 100);
    }

    private void CopyMode(object? sender, EventArgs e)
    {
        string? name = PromptForName("Copy Mode", $"Enter a name for the copy of '{_selectedMode.Name}':",
            $"{_selectedMode.Name} (Copy)");
        if (string.IsNullOrWhiteSpace(name)) return;

        var copy = new ModeDefinition
        {
            Name = name,
            Gestures = _selectedMode.Gestures.Select(g => new GestureDefinition
            {
                Id = Guid.NewGuid().ToString(),
                Name = g.Name,
                Type = g.Type,
                TargetDeviceId = g.TargetDeviceId,
                InputKeys = new List<string>(g.InputKeys),
                PressCount = g.PressCount,
                TimeWindowMs = g.TimeWindowMs,
                Action = new ActionConfig
                {
                    Type = g.Action.Type,
                    Path = g.Action.Path,
                    Arguments = g.Action.Arguments,
                    Url = g.Action.Url,
                    HttpMethod = g.Action.HttpMethod,
                    Body = g.Action.Body,
                    Keystroke = g.Action.Keystroke,
                    Message = g.Action.Message
                },
                SuppressInput = g.SuppressInput
            }).ToList()
        };
        _modes.Add(copy);
        _selectedMode = copy;
        PopulateModeCombo();
        PopulateList();

        _statusLabel.Text = $"Copied mode as: {name}";
        _statusLabel.ForeColor = Color.FromArgb(100, 200, 100);
    }

    private void RenameMode(object? sender, EventArgs e)
    {
        string? name = PromptForName("Rename Mode", $"Enter a new name for '{_selectedMode.Name}':", _selectedMode.Name);
        if (string.IsNullOrWhiteSpace(name)) return;

        _selectedMode.Name = name;
        PopulateModeCombo();

        _statusLabel.Text = $"Renamed to: {name}";
        _statusLabel.ForeColor = Color.FromArgb(100, 200, 100);
    }

    private void DeleteMode(object? sender, EventArgs e)
    {
        if (_modes.Count <= 1)
        {
            MessageBox.Show("Cannot delete the last remaining mode.", "Delete Mode",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var result = MessageBox.Show($"Delete mode '{_selectedMode.Name}' and all its mappings?",
            "Delete Mode", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (result != DialogResult.Yes) return;

        string deletedName = _selectedMode.Name;
        _modes.Remove(_selectedMode);
        _selectedMode = _modes[0];
        PopulateModeCombo();
        PopulateList();

        _statusLabel.Text = $"Deleted mode: {deletedName}";
        _statusLabel.ForeColor = Color.FromArgb(255, 200, 100);
    }

    private static string? PromptForName(string title, string prompt, string defaultValue)
    {
        using var dlg = new Form
        {
            Text = title,
            ClientSize = new Size(360, 120),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Color.FromArgb(40, 40, 40),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9)
        };

        var lbl = new Label { Text = prompt, Location = new Point(16, 12), AutoSize = true };
        var txt = new TextBox
        {
            Text = defaultValue,
            Location = new Point(16, 36),
            Size = new Size(320, 24),
            BackColor = Color.FromArgb(55, 55, 55),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(170, 72),
            Size = new Size(80, 30),
            BackColor = Color.FromArgb(0, 120, 215),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(258, 72),
            Size = new Size(80, 30),
            BackColor = Color.FromArgb(60, 60, 60),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };

        dlg.Controls.AddRange([lbl, txt, ok, cancel]);
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;

        return dlg.ShowDialog() == DialogResult.OK ? txt.Text.Trim() : null;
    }

    // ── Mapping List ──

    private void PopulateList()
    {
        _mappingList.Items.Clear();
        foreach (var g in _selectedMode.Gestures)
        {
            var item = new ListViewItem(g.Name);
            item.SubItems.Add(string.Join(" + ", g.InputKeys));
            item.SubItems.Add(FormatAction(g.Action));
            item.SubItems.Add(g.Type.ToString());

            string deviceDisplay = "Any";
            if (!string.IsNullOrEmpty(g.TargetDeviceId)) {
                deviceDisplay = _deviceManager.GetDisplayName(g.TargetDeviceId);
            }
            item.SubItems.Add(deviceDisplay);

            item.Tag = g;
            _mappingList.Items.Add(item);
        }
        UpdateButtonStates();
    }

    private static string FormatAction(ActionConfig action)
    {
        return action.Type.ToLowerInvariant() switch
        {
            "keystroke" => $"Keys: {action.Keystroke}",
            "launch" => $"Run: {action.Path}",
            "notification" => $"Notify: {action.Message}",
            "webhook" => $"HTTP: {action.Url}",
            "powershell" => $"PS: {action.Path}",
            _ => action.Type
        };
    }

    private void UpdateButtonStates()
    {
        _removeBtn.Enabled = _mappingList.SelectedItems.Count > 0;
    }

    // ── Recording ──

    private bool _isRecording;

    private void ToggleRecording(object? sender, EventArgs e)
    {
        if (_isRecording)
        {
            StopRecording();
        }
        else
        {
            _isRecording = true;
            _recordBtn.Text = "■ Stop";
            _recordBtn.BackColor = Color.FromArgb(255, 160, 0);
            _inputKeyBox.Text = "Waiting for input...";
            _statusLabel.Text = "Press a button on your mouse or device now.";
            _statusLabel.ForeColor = Color.FromArgb(255, 200, 100);

            _app.StartRecording((inputKey, deviceId) =>
            {
                if (InvokeRequired)
                    BeginInvoke(() => OnInputRecorded(inputKey, deviceId));
                else
                    OnInputRecorded(inputKey, deviceId);
            });
        }
    }

    private void OnInputRecorded(string inputKey, string deviceId)
    {
        _recordedDeviceId = deviceId;
        _inputKeyBox.Text = inputKey;

        if (!string.IsNullOrEmpty(deviceId))
        {
            _deviceBox.Text = _deviceManager.GetDisplayName(deviceId);
        }
        else
        {
            _deviceBox.Text = "(Any Device)";
        }

        string context = string.IsNullOrEmpty(deviceId) ? "" : $" on {deviceId}";
        _statusLabel.Text = $"Captured: {inputKey}{context}. Now click the 'Maps to' box and press your keys.";
        _statusLabel.ForeColor = Color.FromArgb(100, 200, 255);

        if (string.IsNullOrWhiteSpace(_nameBox.Text))
            _nameBox.Text = $"{inputKey} mapping";

        StopRecording();
        _keystrokeBox.Focus();
    }

    private void StopRecording()
    {
        _isRecording = false;
        _recordBtn.Text = "⏺ Record";
        _recordBtn.BackColor = Color.FromArgb(200, 60, 60);
        _app.StopRecording();
    }

    // ── Keystroke capture ──
    // We use the form-level PreviewKeyDown + KeyDown with KeyPreview=true
    // to capture ALL key combos including Alt+Key, Ctrl+Shift+Key, etc.
    // The keystroke box is ReadOnly — we write to it programmatically.

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // During recording mode, capture key events as input recording
        // (BT HID devices may send keys that bypass WH_KEYBOARD_LL hooks
        //  but reach WinForms key events — this is the fallback path)
        if (_isRecording)
        {
            var keyName = KeyToRecordingName(keyData);
            if (keyName != null)
            {
                string resolvedId = _app.GetLastInputDeviceId();
                Logging.DiagnosticOutput.LogDebug(Logging.DiagnosticOutput.CategoryDevice, $"Captured key via ProcessCmdKey: Key.{keyName} on device {resolvedId}");
                OnInputRecorded($"Key.{keyName}", resolvedId);
                return true;
            }
        }

        // If the keystroke box is focused, intercept ALL key combos
        // (including Alt+key, Ctrl+key, etc.) before the form processes them
        if (_keystrokeBox.Focused)
        {
            CaptureKeystroke(keyData);
            return true; // swallow — don't let Alt activate menus, etc.
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Fallback recording capture for keys that come through KeyDown
        if (_isRecording)
        {
            var keyName = KeyToRecordingName(e.KeyData);
            if (keyName != null)
            {
                string resolvedId = _app.GetLastInputDeviceId();
                Logging.DiagnosticOutput.LogDebug(Logging.DiagnosticOutput.CategoryDevice, $"Captured key via OnKeyDown: Key.{keyName} on device {resolvedId}");
                OnInputRecorded($"Key.{keyName}", resolvedId);
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
        }

        if (_keystrokeBox.Focused)
        {
            CaptureKeystroke(e.KeyData);
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        base.OnKeyDown(e);
    }

    /// <summary>
    /// Convert a Keys value to a recording-compatible name (e.g. "PageUp", "Left").
    /// Returns null for modifier-only keys (Shift, Ctrl, Alt) since those aren't
    /// standalone inputs from a finger mouse.
    /// </summary>
    private static string? KeyToRecordingName(Keys keyData)
    {
        // Strip modifiers to get the base key
        var baseKey = keyData & Keys.KeyCode;

        // Ignore modifier-only presses
        if (baseKey is Keys.ShiftKey or Keys.ControlKey or Keys.Menu
            or Keys.LShiftKey or Keys.RShiftKey
            or Keys.LControlKey or Keys.RControlKey
            or Keys.LMenu or Keys.RMenu
            or Keys.None)
            return null;

        // Map to ConsoleKey name for consistency with the hook-based recording path
        try
        {
            var consoleName = ((ConsoleKey)(int)baseKey).ToString();
            return consoleName;
        }
        catch
        {
            return baseKey.ToString();
        }
    }

    private void CaptureKeystroke(Keys keyData)
    {
        var parts = new List<string>();

        if ((keyData & Keys.Control) != 0) parts.Add("Ctrl");
        if ((keyData & Keys.Alt) != 0) parts.Add("Alt");
        if ((keyData & Keys.Shift) != 0) parts.Add("Shift");

        // Extract the base key (without modifier flags)
        var baseKey = keyData & Keys.KeyCode;

        // Skip if only a modifier was pressed (no main key yet)
        if (baseKey is Keys.ControlKey or Keys.Menu or Keys.ShiftKey
            or Keys.LControlKey or Keys.RControlKey
            or Keys.LMenu or Keys.RMenu
            or Keys.LShiftKey or Keys.RShiftKey)
        {
            // Show the actual modifier names as they're held (no ellipses)
            if (parts.Count > 0)
                _keystrokeBox.Text = string.Join("+", parts);
            return;
        }

        // Map the base key to a readable name
        string keyName = baseKey switch
        {
            Keys.Next => "PageDown",
            Keys.Prior => "PageUp",
            Keys.Back => "Backspace",
            Keys.Return => "Enter",
            Keys.Escape => "Escape",
            Keys.Capital => "CapsLock",
            Keys.OemPeriod => ".",
            Keys.Oemcomma => ",",
            Keys.OemQuestion => "/",
            Keys.OemSemicolon => ";",
            Keys.OemQuotes => "'",
            Keys.OemOpenBrackets => "[",
            Keys.OemCloseBrackets => "]",
            Keys.OemBackslash => "\\",
            Keys.OemMinus => "-",
            Keys.Oemplus => "=",
            Keys.OemPipe => "\\",
            Keys.Oemtilde => "`",
            _ => baseKey.ToString()
        };

        parts.Add(keyName);
        _keystrokeBox.Text = string.Join("+", parts);
    }

    // ── Add / Remove ──

    private void AddMapping(object? sender, EventArgs e)
    {
        var inputKey = _inputKeyBox.Text;
        var keystroke = _keystrokeBox.Text;
        var name = _nameBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(inputKey) || inputKey.StartsWith("("))
        {
            _statusLabel.Text = "Record an input event first.";
            _statusLabel.ForeColor = Color.FromArgb(255, 100, 100);
            return;
        }

        if (string.IsNullOrWhiteSpace(keystroke) || keystroke.EndsWith("..."))
        {
            _statusLabel.Text = "Enter a complete keystroke mapping (press modifier + key).";
            _statusLabel.ForeColor = Color.FromArgb(255, 100, 100);
            return;
        }

        if (string.IsNullOrWhiteSpace(name))
            name = $"{inputKey} → {keystroke}";

        var gesture = new GestureDefinition
        {
            Name = name,
            Type = GestureType.SinglePress,
            InputKeys = [inputKey],
            TargetDeviceId = string.IsNullOrEmpty(_recordedDeviceId) ? null : _recordedDeviceId,
            SuppressInput = true,
            Action = new ActionConfig
            {
                Type = "keystroke",
                Keystroke = keystroke
            }
        };

        _selectedMode.Gestures.Add(gesture);
        PopulateList();

        // Clear inputs for next mapping
        _nameBox.Text = "";
        _inputKeyBox.Text = "(click Record, then use your device)";
        _keystrokeBox.Text = "";

        _statusLabel.Text = $"Added: {name}";
        _statusLabel.ForeColor = Color.FromArgb(100, 200, 100);

        Logging.DiagnosticOutput.LogDebug(Logging.DiagnosticOutput.CategoryConfig, $"Added gesture to mode '{_selectedMode.Name}': {name} ({inputKey} → {keystroke})");
    }

    private void RemoveMapping(object? sender, EventArgs e)
    {
        if (_mappingList.SelectedItems.Count == 0) return;

        var selected = _mappingList.SelectedItems[0];
        var gesture = (GestureDefinition)selected.Tag!;
        _selectedMode.Gestures.Remove(gesture);
        PopulateList();

        _statusLabel.Text = $"Removed: {gesture.Name}";
        _statusLabel.ForeColor = Color.FromArgb(255, 200, 100);
    }

    // ── Save ──

    private void SaveAndClose(object? sender, EventArgs e)
    {
        try
        {
            _config.Modes = _modes;
            _config.ActiveModeId = _selectedMode.Id;
            // Clear legacy gestures list
            _config.Gestures = [];
            ConfigManager.Save(_config);

            Logging.DiagnosticOutput.LogInfo(Logging.DiagnosticOutput.CategoryConfig, $"Settings saved: {_modes.Count} mode(s), active='{_selectedMode.Name}'");

            // Save succeeded — now close the dialog with OK
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            Logging.DiagnosticOutput.LogError(Logging.DiagnosticOutput.CategoryConfig, "Failed to save config from settings dialog", ex);
            MessageBox.Show($"Failed to save: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            // Don't close — let user retry
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        StopRecording();
        base.OnFormClosing(e);
    }
}
