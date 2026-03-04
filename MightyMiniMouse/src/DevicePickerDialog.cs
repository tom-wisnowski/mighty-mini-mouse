using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using MightyMiniMouse.Hooks;
using MightyMiniMouse.Logging;

namespace MightyMiniMouse;

/// <summary>
/// Dialog for selecting a target mouse device. Shows a live activity indicator
/// (orange dot) next to each mouse that has recent input.
/// </summary>
public class DevicePickerDialog : Form
{
    private readonly RawInputManager _rawInputManager;
    private string? _currentDevicePath;
    private readonly MightyMiniMouse.Services.DeviceManager _deviceManager;
    private readonly Action<IntPtr?, string?>? _onDeviceSelected;

    private ListView _deviceList = null!;
    private Button _refreshBtn = null!;
    private Button _selectBtn = null!;
    private Button _cancelBtn = null!;
    private Button _deleteBtn = null!;
    private Label _statusLabel = null!;
    private TextBox _nicknameBox = null!;
    private Button _saveNicknameBtn = null!;
    private System.Windows.Forms.Timer? _activityTimer;

    // Full (non-deduped) mouse list for handle → VID/PID lookup
    private List<DeviceInfo> _allMice = new();

    // Track which item indices currently have an active dot
    private readonly HashSet<int> _activeItems = new();
    // Map list item index → VID/PID for quick lookup
    private readonly Dictionary<int, string> _itemVidPid = new();
    // Track last-seen timestamp per VID/PID  
    private readonly Dictionary<string, DateTime> _lastActivity = new(StringComparer.OrdinalIgnoreCase);

    // Result
    public DeviceInfo? SelectedDevice { get; private set; }
    public bool AllDevicesSelected { get; private set; }

    // ── Dark theme colors ──
    private static readonly Color BgDark = Color.FromArgb(30, 30, 30);
    private static readonly Color BgPanel = Color.FromArgb(40, 40, 40);
    private static readonly Color BgListItem = Color.FromArgb(50, 50, 50);
    private static readonly Color BgSelected = Color.FromArgb(0, 120, 212);
    private static readonly Color FgPrimary = Color.FromArgb(230, 230, 230);
    private static readonly Color FgSecondary = Color.FromArgb(160, 160, 160);
    private static readonly Color AccentGreen = Color.FromArgb(16, 185, 129);
    private static readonly Color AccentOrange = Color.FromArgb(255, 160, 30);
    private static readonly Color BorderColor = Color.FromArgb(60, 60, 60);

    private const double ACTIVITY_DURATION_SECS = 2.0;

    public DevicePickerDialog(RawInputManager rawInputManager, string? currentDevicePath, MightyMiniMouse.Services.DeviceManager deviceManager, Action<IntPtr?, string?>? onDeviceSelected = null)
    {
        _rawInputManager = rawInputManager;
        _currentDevicePath = currentDevicePath;
        _deviceManager = deviceManager;
        _onDeviceSelected = onDeviceSelected;

        // Enable double-buffering to eliminate flicker
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

        InitializeComponents();
        PopulateDevices();
        StartActivityMonitor();
    }

    private void InitializeComponents()
    {
        Text = "Mighty Mini Mouse — Device Settings";
        Size = new Size(600, 560);
        MinimumSize = new Size(500, 480);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        BackColor = BgDark;
        ForeColor = FgPrimary;
        Font = new Font("Segoe UI", 9.5f);

        // ── Header ──
        var headerLabel = new Label
        {
            Text = "🖱️  Choose which mouse to listen to",
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            ForeColor = FgPrimary,
            Dock = DockStyle.Top,
            Padding = new Padding(16, 12, 16, 4),
            AutoSize = true
        };

        var subtitleLabel = new Label
        {
            Text = "Move each mouse to see which one lights up, then select it.",
            ForeColor = FgSecondary,
            Dock = DockStyle.Top,
            Padding = new Padding(16, 0, 16, 8),
            AutoSize = true
        };

        // ── Device List ──
        _deviceList = new DoubleBufferedListView
        {
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            BackColor = BgPanel,
            ForeColor = FgPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 10f),
            OwnerDraw = true,
        };
        _deviceList.Columns.Add("", 36);           // Activity dot column
        _deviceList.Columns.Add("Device", 330);
        _deviceList.Columns.Add("Type", 90);
        _deviceList.Columns.Add("Status", 80);
        _deviceList.DrawColumnHeader += DeviceList_DrawColumnHeader;
        _deviceList.DrawItem += DeviceList_DrawItem;
        _deviceList.DrawSubItem += DeviceList_DrawSubItem;
        _deviceList.SelectedIndexChanged += (_, _) => UpdateButtons();
        _deviceList.DoubleClick += (_, _) => { if (_selectBtn.Enabled) ConfirmSelection(); };

        var listPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 4, 16, 8)
        };
        listPanel.Controls.Add(_deviceList);

        // ── Refresh button ──
        _refreshBtn = new Button
        {
            Text = "🔄  Refresh",
            Size = new Size(120, 34),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(55, 55, 55),
            ForeColor = FgPrimary,
            Font = new Font("Segoe UI", 9f),
            Cursor = Cursors.Hand,
        };
        _refreshBtn.FlatAppearance.BorderSize = 1;
        _refreshBtn.FlatAppearance.BorderColor = BorderColor;
        _refreshBtn.Click += (_, _) =>
        {
            PopulateDevices();
            _statusLabel.Text = "Device list refreshed.";
            _statusLabel.ForeColor = FgSecondary;
            Debug.WriteLine("[MMM][PICKER] Device list refreshed manually");
        };

        // ── Delete button ──
        _deleteBtn = new Button
        {
            Text = "🗑  Delete",
            Size = new Size(120, 34),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(130, 40, 40),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9f),
            Cursor = Cursors.Hand,
            Enabled = false,
        };
        _deleteBtn.FlatAppearance.BorderSize = 0;
        _deleteBtn.Click += (_, _) => DeleteSelectedDevices();

        // ── Select / Cancel buttons ──
        _selectBtn = new Button
        {
            Text = "Set Active",
            Size = new Size(120, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = AccentGreen,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Enabled = false,
        };
        _selectBtn.FlatAppearance.BorderSize = 0;
        _selectBtn.Click += (_, _) => ConfirmSelection();

        _cancelBtn = new Button
        {
            Text = "OK",
            Size = new Size(120, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(60, 60, 60),
            ForeColor = FgPrimary,
            Cursor = Cursors.Hand,
        };
        _cancelBtn.FlatAppearance.BorderSize = 1;
        _cancelBtn.FlatAppearance.BorderColor = BorderColor;
        _cancelBtn.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        // ── Nickname Editing ──
        _nicknameBox = new TextBox
        {
            Anchor = AnchorStyles.Right,
            BackColor = BgPanel,
            ForeColor = FgPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Enabled = false,
            Font = new Font("Segoe UI", 10f),
            AutoSize = false,
            Size = new Size(120, 32)
        };
        
        _saveNicknameBtn = new Button
        {
            Text = "Save Nickname",
            Size = new Size(120, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(55, 55, 55),
            ForeColor = FgPrimary,
            Cursor = Cursors.Hand,
            Enabled = false
        };
        _saveNicknameBtn.FlatAppearance.BorderSize = 1;
        _saveNicknameBtn.FlatAppearance.BorderColor = BorderColor;
        _saveNicknameBtn.Click += (_, _) => SaveNickname();

        // ── Status label ──
        _statusLabel = new Label
        {
            Text = "Move a mouse to see its activity light up.",
            ForeColor = FgSecondary,
            AutoSize = true,
            Padding = new Padding(0, 6, 0, 0),
        };

        // ── Bottom panel layout ──
        var bottomPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 140,
            Padding = new Padding(16, 8, 16, 12),
            ColumnCount = 4,
            RowCount = 3,
            BackColor = BgDark,
        };
        bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); // pushes elements right
        bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottomPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        
        bottomPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        bottomPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        bottomPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // Row 0 — Refresh | (spacer) | Delete | Set Active
        bottomPanel.Controls.Add(_refreshBtn, 0, 0);
        _deleteBtn.Anchor = AnchorStyles.Right;
        bottomPanel.Controls.Add(_deleteBtn, 2, 0);
        _selectBtn.Anchor = AnchorStyles.Right;
        bottomPanel.Controls.Add(_selectBtn, 3, 0);
        
        // Row 1
        var nicknameLabel = new Label { Text = "Nickname:", AutoSize = true, ForeColor = FgSecondary, Anchor = AnchorStyles.Right | AnchorStyles.Top, Padding = new Padding(0, 6, 4, 0) };
        bottomPanel.Controls.Add(nicknameLabel, 1, 1);
        
        _nicknameBox.Anchor = AnchorStyles.Right;
        bottomPanel.Controls.Add(_nicknameBox, 2, 1);
        
        _saveNicknameBtn.Anchor = AnchorStyles.Right;
        bottomPanel.Controls.Add(_saveNicknameBtn, 3, 1);

        // Row 2
        bottomPanel.Controls.Add(_statusLabel, 0, 2);
        bottomPanel.SetColumnSpan(_statusLabel, 3);
        
        _cancelBtn.Anchor = AnchorStyles.Right;
        bottomPanel.Controls.Add(_cancelBtn, 3, 2);

        // ── Assemble ──
        Controls.Add(listPanel);
        Controls.Add(bottomPanel);
        Controls.Add(subtitleLabel);
        Controls.Add(headerLabel);
    }

    private void PopulateDevices()
    {
        _deviceList.BeginUpdate();
        _deviceList.Items.Clear();
        _itemVidPid.Clear();
        _activeItems.Clear();

        // "All Mice" option
        var allItem = new ListViewItem(new[] { "", "✦  All Mice (no filter)", "", "" });
        allItem.Tag = (DeviceInfo?)null;
        if (_currentDevicePath == null)
            allItem.SubItems[3].Text = "● Active";
        _deviceList.Items.Add(allItem);

        // Enumerate mice
        // Enumerate mice, keyboards, and precision touchpads
        var allDevices = _rawInputManager.EnumerateDevices();
        _allMice = allDevices.Where(d => 
            d.Type == 0 || 
            d.Type == 1 || 
            (d.Type == 2 && d.DevicePath.Contains("000D_0005"))
        ).ToList();

        Debug.WriteLine($"[MMM][PICKER] Enumerated {allDevices.Count} total devices, {_allMice.Count} mice/keyboards/touchpads");
        foreach (var m in _allMice)
            Debug.WriteLine($"[MMM][PICKER]   Device: handle={m.Handle}, type={m.Type}, name={m.FriendlyName}, path={m.DevicePath}");

        // Deduplicate by VID+PID for display
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int mouseIndex = 1;

        foreach (var mouse in _allMice)
        {
            string vidPid = RawInputManager.ExtractVidPid(mouse.DevicePath) ?? mouse.DevicePath;
            if (!seen.Add(vidPid)) continue;

            string connection = mouse.IsBluetooth ? "Bluetooth" : "USB";
            string status = "";
            if (_currentDevicePath != null &&
                mouse.DevicePath.Contains(_currentDevicePath, StringComparison.OrdinalIgnoreCase))
            {
                status = "● Active";
            }

            string displayName = _deviceManager.GetDisplayName(vidPid);
            
            if (displayName == vidPid && displayName != mouse.FriendlyName)
            {
                // Unaliased device ID, show hardware name
                displayName = mouse.FriendlyName; 
            }

            string label = $"  {mouseIndex}. {displayName}";
            var item = new ListViewItem(new[] { "", label, connection, status });
            item.Tag = mouse;

            int itemIndex = _deviceList.Items.Count;
            _itemVidPid[itemIndex] = vidPid;

            Debug.WriteLine($"[MMM][PICKER]   Display row {itemIndex}: {displayName} vidpid={vidPid}");

            _deviceList.Items.Add(item);
            mouseIndex++;
        }

        if (_allMice.Count == 0)
        {
            var emptyItem = new ListViewItem(new[] { "", "  (no mice found)", "", "" });
            emptyItem.ForeColor = FgSecondary;
            _deviceList.Items.Add(emptyItem);
        }

        _deviceList.EndUpdate();
    }



    // Live activity monitoring
    private int _tickCount;
    // Cache: raw input handle → VID/PID (looked up via native API)
    private readonly Dictionary<IntPtr, string?> _handleToVidPid = new();
    
    // Accumulate all handles that sent input since the last tick
    private readonly HashSet<IntPtr> _handlesActiveSinceLastTick = new();

    private void StartActivityMonitor()
    {
        _rawInputManager.OnDeviceActivity += RawInputManager_OnDeviceActivity;

        _activityTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _activityTimer.Tick += ActivityTimer_Tick;
        _activityTimer.Start();
        Debug.WriteLine($"[MMM][PICKER] Activity monitor started. _itemVidPid entries:");
        foreach (var kvp in _itemVidPid)
            Debug.WriteLine($"[MMM][PICKER]   row[{kvp.Key}] = {kvp.Value}");
    }

    private void RawInputManager_OnDeviceActivity(IntPtr handle)
    {
        lock (_handlesActiveSinceLastTick)
        {
            _handlesActiveSinceLastTick.Add(handle);
        }
    }

    private void ActivityTimer_Tick(object? sender, EventArgs e)
    {
        _tickCount++;
        var now = DateTime.UtcNow;
        bool verbose = (_tickCount % 10 == 0);

        List<IntPtr> recentHandles;
        lock (_handlesActiveSinceLastTick)
        {
            recentHandles = _handlesActiveSinceLastTick.ToList();
            _handlesActiveSinceLastTick.Clear();
        }

        if (recentHandles.Count > 0)
        {
            foreach (var handle in recentHandles)
            {
                if (handle == IntPtr.Zero)
                {
                    if (verbose) Debug.WriteLine($"[MMM][PICKER] Tick #{_tickCount}: handle=0 (synthetic input, ignoring)");
                    continue;
                }

                // Look up VID/PID for this handle (cached after first lookup)
                if (!_handleToVidPid.TryGetValue(handle, out var vidPid))
                {
                    // First time seeing this handle — look up device path via native API
                    string devicePath = RawInputManager.GetDeviceName(handle);
                    vidPid = RawInputManager.ExtractVidPid(devicePath) ?? devicePath;
                    _handleToVidPid[handle] = vidPid;
                    Debug.WriteLine($"[MMM][PICKER] New handle {handle} → path={devicePath}");
                    Debug.WriteLine($"[MMM][PICKER]   → Resolved VID/PID: {vidPid}");
                }

                if (verbose)
                    Debug.WriteLine($"[MMM][PICKER] Tick #{_tickCount}: Last handle={handle}, maps to VID/PID={vidPid}");

                if (!string.IsNullOrWhiteSpace(vidPid))
                {
                    bool isNew = !_lastActivity.ContainsKey(vidPid) ||
                                 (now - _lastActivity[vidPid]).TotalMilliseconds > 500;
                    _lastActivity[vidPid] = now;

                    if (isNew)
                    {
                        // Clear activity from all OTHER devices so their dots
                        // disappear instantly when switching mice
                        var otherKeys = _lastActivity.Keys
                            .Where(k => !k.Equals(vidPid, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        foreach (var key in otherKeys)
                            _lastActivity.Remove(key);

                        Debug.WriteLine($"[MMM][PICKER] ★ Activity changed: vidpid={vidPid} (handle={handle})");
                    }
                }
                else if (verbose)
                {
                    Debug.WriteLine($"[MMM][PICKER] Tick #{_tickCount}: handle={handle} has no VID/PID");
                }
            }
        }
        else if (verbose)
        {
            Debug.WriteLine($"[MMM][PICKER] Tick #{_tickCount}: no raw input handles active in last 200ms");
        }

        // 2. Compute which items should currently show an active dot
        var newActiveItems = new HashSet<int>();
        foreach (var kvp in _itemVidPid)
        {
            int itemIdx = kvp.Key;
            string vidPid = kvp.Value;
            if (_lastActivity.ContainsKey(vidPid))
            {
                newActiveItems.Add(itemIdx);
            }
        }

        if (verbose)
            Debug.WriteLine($"[MMM][PICKER] Active items: [{string.Join(", ", newActiveItems)}] (was: [{string.Join(", ", _activeItems)}])");

        // 3. Only redraw if the set of active items changed
        if (!newActiveItems.SetEquals(_activeItems))
        {
            var added = newActiveItems.Except(_activeItems).ToList();
            var removed = _activeItems.Except(newActiveItems).ToList();
            foreach (var idx in added)
                Debug.WriteLine($"[MMM][PICKER] ● DOT ON  row {idx} (vidpid={_itemVidPid.GetValueOrDefault(idx, "?")})");
            foreach (var idx in removed)
                Debug.WriteLine($"[MMM][PICKER] ○ DOT OFF row {idx}");

            _activeItems.Clear();
            foreach (var idx in newActiveItems)
                _activeItems.Add(idx);

            // Invalidate only the dot column
            if (_deviceList.Items.Count > 0)
            {
                var firstBounds = _deviceList.Items[0].SubItems[0].Bounds;
                var lastBounds = _deviceList.Items[_deviceList.Items.Count - 1].SubItems[0].Bounds;
                var dotColumnRect = new Rectangle(
                    firstBounds.X, firstBounds.Y,
                    firstBounds.Width,
                    lastBounds.Bottom - firstBounds.Top);
                _deviceList.Invalidate(dotColumnRect);
                Debug.WriteLine($"[MMM][PICKER] Invalidated dot column rect: {dotColumnRect}");
            }
        }
    }

    // ── Selection ──

    private void UpdateButtons()
    {
        bool hasSelection = _deviceList.SelectedItems.Count > 0;
        _selectBtn.Enabled = hasSelection;
        _nicknameBox.Enabled = hasSelection;
        _saveNicknameBtn.Enabled = hasSelection;

        // Delete is enabled only when at least one real device row (not "All Mice") is selected
        bool hasRealDeviceSelected = _deviceList.SelectedItems.Cast<ListViewItem>()
            .Any(i => i.Tag is DeviceInfo);
        _deleteBtn.Enabled = hasRealDeviceSelected;

        if (hasSelection)
        {
            var selected = _deviceList.SelectedItems[0];
            if (selected.Tag is DeviceInfo device)
            {
                string vidPid = RawInputManager.ExtractVidPid(device.DevicePath) ?? device.DevicePath;
                var knownDevices = _deviceManager.GetKnownAndConnectedDevices();
                var devInfo = knownDevices.FirstOrDefault(d => d.DeviceId == vidPid);
                _nicknameBox.Text = devInfo?.Nickname ?? "";
            }
        }
        else
        {
            _nicknameBox.Text = "";
        }
    }

    private void DeleteSelectedDevices()
    {
        var toDelete = _deviceList.SelectedItems.Cast<ListViewItem>()
            .Where(i => i.Tag is DeviceInfo)
            .Select(i => (DeviceInfo)i.Tag!)
            .ToList();

        if (toDelete.Count == 0) return;

        int deletedCount = 0;
        foreach (var device in toDelete)
        {
            string vidPid = RawInputManager.ExtractVidPid(device.DevicePath) ?? device.DevicePath;
            bool removed = _deviceManager.RemoveDevice(vidPid);

            // If this was the active target, clear it so we fall back to "All Mice"
            if (_currentDevicePath != null &&
                device.DevicePath.Contains(_currentDevicePath, StringComparison.OrdinalIgnoreCase))
            {
                _currentDevicePath = null;
                AllDevicesSelected = true;
                SelectedDevice = null;
                _onDeviceSelected?.Invoke(null, null);
                Debug.WriteLine($"[MMM][PICKER] Deleted active device — falling back to All Mice");
            }

            if (removed) deletedCount++;
            Debug.WriteLine($"[MMM][PICKER] Deleted device: {vidPid} (removed from config: {removed})");
        }

        PopulateDevices();

        if (deletedCount > 0)
        {
            _statusLabel.Text = deletedCount == 1
                ? "Device removed from saved list."
                : $"{deletedCount} devices removed from saved list.";
            _statusLabel.ForeColor = AccentOrange;
        }
        else
        {
            // Device was connected but had no saved entry — still show feedback
            _statusLabel.Text = "Selected device(s) had no saved entry to remove.";
            _statusLabel.ForeColor = FgSecondary;
        }
    }

    private void SaveNickname()
    {
        if (_deviceList.SelectedItems.Count == 0 || string.IsNullOrWhiteSpace(_nicknameBox.Text)) return;

        var selected = _deviceList.SelectedItems[0];
        if (selected.Tag is DeviceInfo device)
        {
            string vidPid = RawInputManager.ExtractVidPid(device.DevicePath) ?? device.DevicePath;
            _deviceManager.SaveNickname(vidPid, _nicknameBox.Text);
            
            _statusLabel.Text = $"Saved nickname for {device.FriendlyName}.";
            _statusLabel.ForeColor = AccentGreen;
            
            // Re-render the list items in place
            PopulateDevices();
        }
    }

    private void SetActive()
    {
        if (_deviceList.SelectedItems.Count == 0) return;

        var selected = _deviceList.SelectedItems[0];
        if (selected.Tag is DeviceInfo device)
        {
            SelectedDevice = device;
            AllDevicesSelected = false;
            Debug.WriteLine($"[MMM][PICKER] Selected: {device.FriendlyName} ({device.DevicePath})");
            _onDeviceSelected?.Invoke(device.Handle, device.DevicePath);
            PopulateDevices(); // Redraw the active dot
        }
        else if (selected.Text.Contains("All Mice"))
        {
            SelectedDevice = null;
            AllDevicesSelected = true;
            Debug.WriteLine("[MMM][PICKER] Selected ALL MICE");
            _onDeviceSelected?.Invoke(null, null);
            PopulateDevices();
        }
    }

    private void ConfirmSelection()
    {
        SetActive();
        DialogResult = DialogResult.OK;
        Close();
    }

    // ── Owner-draw for dark theme ──

    private void DeviceList_DrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using var bgBrush = new SolidBrush(Color.FromArgb(35, 35, 35));
        e.Graphics.FillRectangle(bgBrush, e.Bounds);

        using var pen = new Pen(BorderColor);
        e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1,
                           e.Bounds.Right, e.Bounds.Bottom - 1);

        if (e.ColumnIndex > 0)
        {
            var textBounds = new Rectangle(e.Bounds.X + 6, e.Bounds.Y,
                                           e.Bounds.Width - 12, e.Bounds.Height);
            using var font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", font, textBounds,
                FgSecondary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }
    }

    private void DeviceList_DrawItem(object? sender, DrawListViewItemEventArgs e)
    {
        if (e.Item == null) return;

        bool isSelected = e.Item.Selected;
        var bgColor = isSelected ? BgSelected
                    : (e.ItemIndex % 2 == 0 ? BgPanel : BgListItem);
        using var brush = new SolidBrush(bgColor);
        e.Graphics.FillRectangle(brush, e.Bounds);
    }

    private void DeviceList_DrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item == null || e.SubItem == null) return;

        bool isSelected = e.Item.Selected;
        var bgColor = isSelected ? BgSelected
                    : (e.ItemIndex % 2 == 0 ? BgPanel : BgListItem);
        using var brush = new SolidBrush(bgColor);
        e.Graphics.FillRectangle(brush, e.Bounds);

        // Column 0 = Activity dot
        if (e.ColumnIndex == 0)
        {
            if (_activeItems.Contains(e.ItemIndex))
            {
                int dotSize = 10;
                int x = e.Bounds.X + (e.Bounds.Width - dotSize) / 2;
                int y = e.Bounds.Y + (e.Bounds.Height - dotSize) / 2;
                using var dotBrush = new SolidBrush(AccentOrange);
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                e.Graphics.FillEllipse(dotBrush, x, y, dotSize, dotSize);
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
            }
            return;
        }

        // Determine text color for other columns
        var textColor = FgPrimary;
        string text = e.SubItem.Text ?? "";
        if (e.ColumnIndex == 3 && text.Contains("Active"))
            textColor = AccentGreen;
        else if (e.ColumnIndex == 2)
            textColor = isSelected ? Color.White : FgSecondary;

        var textBounds = new Rectangle(e.Bounds.X + 4, e.Bounds.Y,
                                       e.Bounds.Width - 8, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, text,
            _deviceList.Font, textBounds, textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_rawInputManager != null)
        {
            _rawInputManager.OnDeviceActivity -= RawInputManager_OnDeviceActivity;
        }

        _activityTimer?.Stop();
        _activityTimer?.Dispose();
        _activityTimer = null;
        Debug.WriteLine("[MMM][PICKER] Dialog closing, activity monitor stopped");
        base.OnFormClosing(e);
    }
}

/// <summary>
/// ListView subclass with double-buffering enabled to eliminate flicker.
/// </summary>
internal class DoubleBufferedListView : ListView
{
    public DoubleBufferedListView()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }
}
