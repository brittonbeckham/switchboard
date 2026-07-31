using System.Runtime.InteropServices;
using Switchboard.Core;
using Switchboard.Util;
using Microsoft.Win32;

namespace Switchboard.UI;

internal sealed class SettingsForm : Form
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Switchboard";

    // Kept as aliases so the rest of this large file (written before the dark
    // reskin) doesn't need every single reference renamed — new code should
    // reach for Theme.* directly.
    private static readonly Color Accent = Theme.Accent;
    private static readonly Color SubtleText = Theme.Subtle;

    // Keycap palette: one visual language for every assignment surface.
    private static readonly Color CapUnassignedFill = Theme.Panel;
    private static readonly Color CapUnassignedBorder = Theme.Line;
    private static readonly Color CapUnassignedText = Theme.Faint;
    private static readonly Color CapAssignedFill = Theme.AccentSoft;
    private static readonly Color CapAssignedBorder = Color.FromArgb(58, 84, 116);
    private static readonly Color CapAssignedText = Theme.Accent;
    private static readonly Color CapPendingFill = Theme.PendingFill;
    private static readonly Color CapPendingBorder = Theme.PendingBorder;
    private static readonly Color CapPendingText = Theme.PendingText;
    private static readonly Color DragMoveBorder = Theme.DragMoveBorder;
    private static readonly Color DragSwapBorder = Theme.DragSwapBorder;

    private readonly AppSettings _settings;
    private readonly TrayContext _tray;
    private readonly FlowLayoutPanel _nav;
    private readonly List<Button> _navItems = [];
    private readonly Panel _pageHost;
    private readonly Dictionary<string, Panel> _pages = [];
    private string _currentPage = "";

    private ToggleSwitch _startupCheck = null!;
    private ToggleSwitch _numpadCheck = null!;
    private ToggleSwitch _calculatorCheck = null!;
    private ToggleSwitch _hudCheck = null!;
    private ToggleSwitch _focusModeCheck = null!;
    private ToggleSwitch _blurCheck = null!;
    private ToggleSwitch _peekCheck = null!;
    private Slider _dimTrack = null!;
    private Label _dimLabel = null!;
    private Label _statusLabel = null!;
    private Button _detectorButton = null!;
    private TextBox _logBox = null!;
    private bool _loading = true;

    public SettingsForm(AppSettings settings, TrayContext tray)
    {
        _settings = settings;
        _tray = tray;

        Text = "Switchboard";
        Icon = AppIcon.Create();
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        ClientSize = new Size(1060, 700);
        BackColor = Theme.Bg;
        // Reopen where the user last put it, if that spot still exists on some screen.
        if (settings.SettingsWindowX is int x && settings.SettingsWindowY is int y &&
            Screen.AllScreens.Any(s => s.WorkingArea.Contains(new Point(x + 60, y + 30))))
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(x, y);
        }
        else
        {
            StartPosition = FormStartPosition.CenterScreen;
        }
        Font = Theme.Body;

        var titleBar = BuildTitleBar();

        var navPanel = new Panel { Dock = DockStyle.Left, Width = Theme.RailWidth, BackColor = Theme.Rail };
        var navHeader = new Label
        {
            Text = "Switchboard",
            Font = Theme.Title,
            ForeColor = Theme.Ink,
            Dock = DockStyle.Top,
            Height = 30,
            Padding = new Padding(18, 4, 0, 0),
            BackColor = Theme.Rail,
        };
        var navSubtitle = new Label
        {
            Text = "by Britton Beckham",
            Font = Theme.Caption,
            ForeColor = Theme.Subtle,
            Dock = DockStyle.Top,
            Height = 22,
            Padding = new Padding(19, 0, 0, 0),
            BackColor = Theme.Rail,
        };
        _nav = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Padding = new Padding(8, 12, 8, 0),
            BackColor = Theme.Rail,
        };
        navPanel.Controls.Add(_nav);
        navPanel.Controls.Add(navSubtitle);
        navPanel.Controls.Add(navHeader);

        _pageHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(28, 20, 28, 20), BackColor = Theme.Bg };
        var body = new Panel { Dock = DockStyle.Fill };
        body.Controls.Add(_pageHost);
        body.Controls.Add(navPanel);
        Controls.Add(body);
        Controls.Add(titleBar);

        AddPage("Megalodon Pad", BuildMegalodonPage());
        AddPage("Focus Mode", BuildFocusModePage());
        AddPage("Extras", BuildExtrasPage());
        AddPage("Diagnostics", BuildDiagnosticsPage());
        // Selecting the pad page triggers a BeginInvoke(ReadPad) — defer past
        // the constructor since the window handle doesn't exist yet.
        Load += (_, _) => ShowPage("Megalodon Pad");

        LoadState();
        _loading = false;

        Log.LineAdded += OnLogLine;
        _tray.StatusChanged += OnStatusChanged;
        _tray.BusyChanged += OnBusyChanged;
        FormClosed += (_, _) =>
        {
            Log.LineAdded -= OnLogLine;
            _tray.StatusChanged -= OnStatusChanged;
            _tray.BusyChanged -= OnBusyChanged;
            if (WindowState == FormWindowState.Normal)
            {
                _settings.SettingsWindowX = Location.X;
                _settings.SettingsWindowY = Location.Y;
                _settings.Save();
            }
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= CsDropShadow;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var pref = DwmwcpRound;
        try { DwmSetWindowAttribute(Handle, DwmwaWindowCornerPreference, ref pref, sizeof(int)); }
        catch { /* older Windows builds without this attribute just keep square corners */ }
    }

    /// <summary>Custom chrome: app mark + title, drag-to-move, minimize/close —
    /// replaces the native Windows title bar entirely.</summary>
    private Panel BuildTitleBar()
    {
        var bar = new Panel { Dock = DockStyle.Top, Height = Theme.TitleBarHeight, BackColor = Theme.Panel };

        var mark = new Panel { Location = new Point(16, 9), Size = new Size(20, 20), BackColor = Theme.Panel };
        mark.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = RoundedRect(new Rectangle(0, 0, 19, 19), 6);
            using var fill = new SolidBrush(Theme.Accent);
            g.FillPath(fill, path);
            using var dot = new SolidBrush(Color.White);
            const int s = 7;
            g.FillRectangle(dot, 3, 3, s, s);
            g.FillRectangle(dot, 10, 3, s, s);
            g.FillRectangle(dot, 3, 10, s, s);
            g.FillRectangle(dot, 10, 10, s, s);
        };
        var nameLbl = new Label
        {
            Text = "Switchboard",
            Location = new Point(44, 0),
            Size = new Size(200, Theme.TitleBarHeight),
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.BodySemibold,
            ForeColor = Theme.Ink,
            BackColor = Color.Transparent,
        };
        bar.Controls.Add(mark);
        bar.Controls.Add(nameLbl);

        void StartDrag(object? s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, WmNcLButtonDown, HtCaption, IntPtr.Zero);
        }
        bar.MouseDown += StartDrag;
        nameLbl.MouseDown += StartDrag;

        var closeBtn = MakeWinButton("✕", isClose: true);
        closeBtn.Click += (_, _) => Close();
        var minBtn = MakeWinButton("─", isClose: false);
        minBtn.Click += (_, _) => WindowState = FormWindowState.Minimized;
        closeBtn.Dock = DockStyle.Right;
        minBtn.Dock = DockStyle.Right;
        bar.Controls.Add(closeBtn);
        bar.Controls.Add(minBtn);

        return bar;
    }

    private static Button MakeWinButton(string glyph, bool isClose)
    {
        var btn = new Button
        {
            Text = glyph,
            Width = 42,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Theme.Subtle,
            BackColor = Theme.Panel,
            Font = new Font("Segoe UI", 10f),
            Cursor = Cursors.Hand,
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = isClose ? Theme.Danger : Theme.Line;
        return btn;
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private const int CsDropShadow = 0x00020000;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;
    private const int WmNcLButtonDown = 0xA1;
    private const int HtCaption = 2;

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern int SendMessage(IntPtr hWnd, int msg, int wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private void AddPage(string title, Panel page)
    {
        page.Dock = DockStyle.Fill;
        page.Visible = false;
        _pages[title] = page;
        _pageHost.Controls.Add(page);

        var item = new Button
        {
            Text = title,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 0, 0),
            Size = new Size(Theme.RailWidth - 16, 36),
            FlatStyle = FlatStyle.Flat,
            Font = Theme.BodySemibold,
            ForeColor = Theme.Subtle,
            BackColor = Theme.Rail,
            Margin = new Padding(0, 0, 0, 2),
            Cursor = Cursors.Hand,
            TabStop = false,
        };
        item.FlatAppearance.BorderSize = 0;
        item.FlatAppearance.MouseOverBackColor = Theme.Line;
        item.Click += (_, _) => ShowPage(title);
        _navItems.Add(item);
        _nav.Controls.Add(item);
    }

    public void SelectPage(string title)
    {
        var match = _pages.Keys.FirstOrDefault(k => string.Equals(k, title, StringComparison.OrdinalIgnoreCase))
                    ?? _pages.Keys.FirstOrDefault(k => k.StartsWith(title, StringComparison.OrdinalIgnoreCase));
        if (match != null) ShowPage(match);
    }

    private void ShowPage(string title)
    {
        _currentPage = title;
        foreach (var (name, page) in _pages) page.Visible = name == title;
        foreach (var item in _navItems)
        {
            var on = item.Text == title;
            item.BackColor = on ? Theme.AccentSoft : Theme.Rail;
            item.ForeColor = on ? Theme.Accent : Theme.Subtle;
            item.FlatAppearance.MouseOverBackColor = on ? Theme.AccentSoft : Theme.Line;
        }
        // The pad page always shows the live truth: re-read on every visit.
        if (title == "Megalodon Pad") BeginInvoke(ReadPad);
    }

    private static Panel PageShell(string title, string subtitle, Control content, Control? headerRight = null)
    {
        var page = new Panel { BackColor = Theme.Bg };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Bg };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var titleRow = new Panel { Height = 32, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 2), Width = 600, BackColor = Theme.Bg };
        titleRow.Controls.Add(new Label
        {
            Text = title,
            Font = Theme.Display,
            ForeColor = Theme.Ink,
            AutoSize = true,
            Location = new Point(0, 0),
            BackColor = Theme.Bg,
        });
        if (headerRight != null)
        {
            headerRight.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            headerRight.Location = new Point(titleRow.Width - headerRight.Width, 2);
            titleRow.Controls.Add(headerRight);
        }
        layout.Controls.Add(titleRow, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = subtitle,
            ForeColor = Theme.Subtle,
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            Margin = new Padding(0, 0, 0, 14),
            BackColor = Theme.Bg,
        }, 0, 1);
        content.Dock = DockStyle.Fill;
        layout.Controls.Add(content, 0, 2);
        page.Controls.Add(layout);
        return page;
    }

    // ---- Megalodon pad ----

    // Layers are swapped panels (all rendered up front, one visible at a time)
    // — navigation lives in the LayerLcd widget between the knobs, mirroring
    // the pad's own onboard OLED, instead of a row of tabs above the grid.
    private readonly List<Panel> _layerPages = [];
    private readonly List<LayerLcd> _layerLcds = [];
    private Panel _layerPageHost = null!;
    private int _selectedLayer;
    private MegalodonPad.PadSnapshot? _padSnapshot;
    private readonly Dictionary<string, PendingChange> _pendingChanges = [];
    private Panel _pendingBar = null!;
    private Label _pendingLabel = null!;
    private Button _pendingWrite = null!;

    private Panel BuildMegalodonPage()
    {
        var headerButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        var refresh = new Button
        {
            Text = "⟳  Refresh", AutoSize = true, Margin = new Padding(0, 0, 8, 0),
            FlatStyle = FlatStyle.Flat, BackColor = Theme.PanelAlt, ForeColor = Theme.Ink,
        };
        refresh.FlatAppearance.BorderColor = Theme.Line;
        refresh.Click += (_, _) => ReadPad();

        var moreMenu = new ContextMenuStrip();
        var restoreItem = new ToolStripMenuItem("Restore…");
        restoreItem.Click += (_, _) => RestorePadBackup();
        var backupItem = new ToolStripMenuItem("Backup Now");
        backupItem.Click += (_, _) => BackupPadNow();
        moreMenu.Items.Add(restoreItem);
        moreMenu.Items.Add(backupItem);
        Theme.ApplyDarkMenu(moreMenu);

        var more = new Button
        {
            Text = "⋯", AutoSize = true, Padding = new Padding(6, 0, 6, 0),
            FlatStyle = FlatStyle.Flat, BackColor = Theme.PanelAlt, ForeColor = Theme.Ink,
        };
        more.FlatAppearance.BorderColor = Theme.Line;
        more.Click += (_, _) => moreMenu.Show(more, new Point(0, more.Height));

        headerButtons.Controls.Add(refresh);
        headerButtons.Controls.Add(more);
        headerButtons.Size = headerButtons.PreferredSize;

        _layerPageHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };

        // Footer bar for staged (unwritten) changes.
        _pendingBar = new Panel { Dock = DockStyle.Bottom, Height = 52, Visible = false, BackColor = Theme.Bg };
        _pendingBar.Paint += (_, e) =>
            e.Graphics.DrawLine(new Pen(Theme.Line), 0, 0, _pendingBar.Width, 0);
        _pendingLabel = new Label
        {
            AutoSize = true,
            Location = new Point(2, 18),
            ForeColor = CapPendingText,
            Font = Theme.BodySemibold,
            BackColor = Theme.Bg,
        };
        var discard = new Button
        {
            Text = "Discard", Size = new Size(90, 32), Anchor = AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat, BackColor = Theme.PanelAlt, ForeColor = Theme.Ink,
        };
        discard.FlatAppearance.BorderColor = Theme.Line;
        var write = new Button
        {
            Text = "Write to Pad",
            Size = new Size(140, 32),
            Anchor = AnchorStyles.Right,
            BackColor = Accent,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
        };
        write.FlatAppearance.BorderSize = 0;
        _pendingWrite = write;
        discard.Click += (_, _) => DiscardPending();
        write.Click += (_, _) => WritePending();
        _pendingBar.Controls.Add(_pendingLabel);
        _pendingBar.Controls.Add(discard);
        _pendingBar.Controls.Add(write);
        _pendingBar.Resize += (_, _) =>
        {
            write.Location = new Point(_pendingBar.Width - write.Width, 10);
            discard.Location = new Point(write.Left - discard.Width - 8, 10);
        };

        // Clear Layer sits in the pad area's corner (not the header, not per-layer
        // page — a sibling of _layerPageHost so it survives ReadPad rebuilding the
        // pages, positioned off _layerPageHost's actual bounds so it never overlaps
        // the pending-changes bar when that's showing).
        var clearLayerBtn = new Button
        {
            Text = "Clear Layer",
            AutoSize = true,
            Padding = new Padding(10, 4, 10, 4),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Danger,
            ForeColor = Color.White,
        };
        clearLayerBtn.FlatAppearance.BorderSize = 0;
        clearLayerBtn.Click += (_, _) => ClearLayer();

        var container = new Panel { Dock = DockStyle.Fill };
        container.Controls.Add(_layerPageHost);
        container.Controls.Add(_pendingBar);
        container.Controls.Add(clearLayerBtn);
        clearLayerBtn.BringToFront();

        void PositionClearLayerBtn() => clearLayerBtn.Location = new Point(
            _layerPageHost.Right - clearLayerBtn.Width - 16,
            _layerPageHost.Bottom - clearLayerBtn.Height - 16);
        _layerPageHost.Resize += (_, _) => PositionClearLayerBtn();
        PositionClearLayerBtn();

        return PageShell("Megalodon Pad",
            "Your DOIO KB16's live configuration. Click any key or knob zone to stage an assignment; " +
            "staged changes glow amber until you press Write to Pad. Right-click a key to mute its pop-up.",
            container, headerButtons);
    }

    private void DiscardPending()
    {
        _pendingChanges.Clear();
        UpdatePendingBar();
        RenderAllLayers();
    }

    private void RenderAllLayers()
    {
        if (_padSnapshot == null) return;
        for (var i = 0; i < _layerPages.Count && i < _padSnapshot.LayerCount; i++)
            RenderPadInto(_layerPages[i], i);
    }

    /// <summary>Stages a clear (KC_NO) for every key, knob turn, and knob press on
    /// the current layer in one shot — same staged-pending flow as clearing one
    /// position by hand, just for all of them. Still requires Write to Pad.</summary>
    private void ClearLayer()
    {
        if (_padSnapshot == null) return;
        var layer = _selectedLayer;
        var confirm = MessageBox.Show(this,
            $"Clear every key and knob zone on Layer {layer}? This stages the clear — " +
            "you'll still need to Write to Pad to commit it.",
            "Clear Layer", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        for (var row = 0; row < 4; row++)
        {
            for (var col = 0; col < 4; col++)
            {
                var labelKey = $"L{layer}K{row},{col}";
                var oldCode = _padSnapshot.KeyCodes[layer][row, col];
                var target = new PadTarget(layer, false, row, col, 0, false,
                    $"Layer {layer} · Key R{row + 1}C{col + 1}", labelKey);
                _pendingChanges[labelKey] = new PendingChange(target, KeycodeCatalog.KC_NO, oldCode, null, null, null, true);
            }
        }

        for (var enc = 0; enc < 3; enc++)
        {
            var name = MegalodonPad.PadSnapshot.EncoderLabels[enc];
            var (ccwOld, cwOld) = _padSnapshot.EncoderCodes[layer][enc];
            var pressOld = _padSnapshot.KeyCodes[layer][enc, 4];

            var ccwKey = $"L{layer}E{enc}:ccw";
            var ccwTarget = new PadTarget(layer, true, 0, 0, enc, false, $"Layer {layer} · {name} Turn Left", ccwKey);
            _pendingChanges[ccwKey] = new PendingChange(ccwTarget, KeycodeCatalog.KC_NO, ccwOld, null, null, null, true);

            var cwKey = $"L{layer}E{enc}:cw";
            var cwTarget = new PadTarget(layer, true, 0, 0, enc, true, $"Layer {layer} · {name} Turn Right", cwKey);
            _pendingChanges[cwKey] = new PendingChange(cwTarget, KeycodeCatalog.KC_NO, cwOld, null, null, null, true);

            var pressKey = $"L{layer}E{enc}:press";
            var pressTarget = new PadTarget(layer, false, enc, 4, 0, false, $"Layer {layer} · {name} Press", pressKey);
            _pendingChanges[pressKey] = new PendingChange(pressTarget, KeycodeCatalog.KC_NO, pressOld, null, null, null, true);
        }

        UpdatePendingBar();
        RenderPadLayer();
    }

    private void UpdatePendingBar()
    {
        var count = _pendingChanges.Count;
        _pendingBar.Visible = count > 0;
        _pendingLabel.Text = count == 1 ? "1 unwritten change" : $"{count} unwritten changes";
    }

    private void WritePending()
    {
        if (_padSnapshot == null || _pendingChanges.Count == 0) return;
        var changes = _pendingChanges.Values.ToList();
        _pendingWrite.Enabled = false;
        _pendingWrite.Text = "Writing…";
        var snapshot = _padSnapshot;

        Task.Run(() =>
        {
            var failures = new List<string>();
            try
            {
                var path = MegalodonPad.SaveBackupIfChanged(snapshot, MegalodonPad.ReadLighting());
                if (path != null) Log.Info($"Pad backup saved: {Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                Log.Info($"Backup failed: {ex.Message}");
            }

            foreach (var change in changes)
            {
                try
                {
                    var t = change.Target;
                    var ok = t.IsEncoder
                        ? MegalodonPad.WriteEncoder(t.Layer, t.Encoder, t.Clockwise, change.Code)
                        : MegalodonPad.WriteKey(t.Layer, t.Row, t.Col, change.Code);
                    if (!ok) failures.Add(t.DisplayName);
                }
                catch (Exception ex)
                {
                    failures.Add($"{change.Target.DisplayName} ({ex.Message})");
                }
            }

            if (IsDisposed) return;
            BeginInvoke(() =>
            {
                // Apply labels and action bindings for every change that was written.
                foreach (var change in changes)
                {
                    if (failures.Contains(change.Target.DisplayName)) continue;
                    if (change.Label == null)
                        _settings.PadLabels.Remove(change.Target.LabelKey);
                    else
                        _settings.PadLabels[change.Target.LabelKey] = change.Label;
                    if (change.ActionId != null && change.ActionKeySpec != null)
                        _settings.FunctionKeyActions[change.ActionKeySpec] = change.ActionId;
                    if (change.ReleaseOldMapping &&
                        KeycodeCatalog.IsGhostKey(change.OldCode, out var oldFn, out var oldModBits) &&
                        change.OldCode != change.Code)
                        _settings.FunctionKeyActions.Remove(HotkeyService.FormatFunctionKey(oldFn, oldModBits));
                }
                _settings.Save();
                _tray.ApplyHotkeySetting();
                _tray.NotifyStatusChanged();
                _tray.RefreshKeyHud();

                _pendingChanges.Clear();
                UpdatePendingBar();
                _pendingWrite.Enabled = true;
                _pendingWrite.Text = "Write to Pad";

                if (failures.Count > 0)
                    MessageBox.Show(this,
                        $"{failures.Count} position(s) didn't verify — VIA may be open. Close VIA and retry.\n\n" +
                        string.Join("\n", failures),
                        "Write incomplete", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else
                    Log.Info($"Wrote {changes.Count} change(s) to the pad ✓");

                ReadPad(); // reflect the pad's actual truth
            });
        });
    }

    /// <summary>Forces an immediate backup of the pad's current live state, independent
    /// of the auto-backup-on-read/write that normally handles it.</summary>
    private void BackupPadNow()
    {
        Task.Run(() =>
        {
            try
            {
                var snapshot = MegalodonPad.Read();
                var path = MegalodonPad.SaveBackupIfChanged(snapshot, MegalodonPad.ReadLighting());
                if (IsDisposed) return;
                BeginInvoke(() =>
                {
                    if (path != null)
                    {
                        Log.Info($"Pad backup saved: {Path.GetFileName(path)}");
                        MessageBox.Show(this, $"Backup saved: {Path.GetFileName(path)}",
                            "Backup", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        MessageBox.Show(this, "No new backup needed — identical to the most recent one.",
                            "Backup", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                });
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                BeginInvoke(() => MessageBox.Show(this, $"Backup failed: {ex.Message}",
                    "Backup", MessageBoxButtons.OK, MessageBoxIcon.Error));
            }
        });
    }

    private void RestorePadBackup()
    {
        if (!Directory.Exists(MegalodonPad.BackupDirectory))
        {
            MessageBox.Show(this, "No backups yet — one is saved automatically before the first write of a session.",
                "Restore", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var picker = new OpenFileDialog
        {
            InitialDirectory = MegalodonPad.BackupDirectory,
            Filter = "Pad backups (*.json)|*.json",
            Title = "Restore pad backup",
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        var path = picker.FileName;
        Task.Run(() =>
        {
            string message;
            try
            {
                var mismatches = MegalodonPad.RestoreBackup(path);
                message = mismatches == 0
                    ? "Backup restored and verified."
                    : $"Backup restored with {mismatches} mismatched position(s).";
            }
            catch (Exception ex)
            {
                message = $"Restore failed: {ex.Message}";
            }
            Log.Info(message);
            if (!IsDisposed) BeginInvoke(ReadPad);
        });
    }

    /// <summary>Removes FunctionKeyActions entries for a ghost key (F13–F24, optionally
    /// modifier-wrapped) that no longer exists anywhere on the pad — e.g. the position
    /// holding it got dragged/reassigned elsewhere without going through the "release
    /// mapping" checkbox. An orphaned entry can never fire (nothing produces that
    /// keystroke anymore) but still blocks that slot from future allocation, and
    /// silently breaks whatever key the user expects to trigger it. F1–F12 mappings
    /// from the Key Mapping hub are untouched — those aren't tied to the pad at all.</summary>
    private void PruneOrphanedActionMappings(MegalodonPad.PadSnapshot snapshot)
    {
        var present = new HashSet<ushort>();
        for (var l = 0; l < snapshot.LayerCount; l++)
        {
            foreach (var code in snapshot.KeyCodes[l]) present.Add(code);
            foreach (var (ccw, cw) in snapshot.EncoderCodes[l])
            {
                present.Add(ccw);
                present.Add(cw);
            }
        }

        var orphaned = _settings.FunctionKeyActions.Keys
            .Where(spec => HotkeyService.TryParseFunctionKey(spec, out var fn, out var modBits) &&
                           fn is >= 13 and <= 24 &&
                           !present.Contains(KeycodeCatalog.Chord(modBits, (ushort)(0x68 + fn - 13))))
            .ToList();
        if (orphaned.Count == 0) return;

        foreach (var spec in orphaned) _settings.FunctionKeyActions.Remove(spec);
        _settings.Save();
        _tray.ApplyHotkeySetting();
        Log.Info($"Cleaned up {orphaned.Count} stale action mapping(s) no longer on the pad: {string.Join(", ", orphaned)}");
    }

    private int _padReadBusy;

    /// <summary>Reads the pad on a worker thread (a few hundred HID round-trips),
    /// then updates the tabs on the UI thread — never blocks rendering.</summary>
    private void ReadPad()
    {
        if (Interlocked.Exchange(ref _padReadBusy, 1) == 1) return;
        Task.Run(() =>
        {
            MegalodonPad.PadSnapshot? snapshot = null;
            string? error = null;
            try
            {
                snapshot = MegalodonPad.Read();
                // Auto-backup on every read so VIA-made changes are captured too
                // (deduped + rolling, so identical reads don't pile up).
                var backup = MegalodonPad.SaveBackupIfChanged(snapshot, MegalodonPad.ReadLighting());
                if (backup != null) Log.Info($"Pad backup saved: {Path.GetFileName(backup)}");
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            finally
            {
                Interlocked.Exchange(ref _padReadBusy, 0);
            }

            if (IsDisposed) return;
            BeginInvoke(() =>
            {
                if (snapshot != null)
                {
                    _padSnapshot = snapshot;
                    PruneOrphanedActionMappings(snapshot);
                    var selected = Math.Max(0, _selectedLayer);
                    ClearLayerPages();
                    // Render every layer up front: switching layers is then pure
                    // visibility toggling — no rebuild, no flicker.
                    for (var i = 0; i < snapshot.LayerCount; i++)
                    {
                        var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false };
                        AddLayerPage(page);
                        RenderPadInto(page, i);
                    }
                    SelectLayer(Math.Min(selected, snapshot.LayerCount - 1));
                }
                else
                {
                    _padSnapshot = null;
                    ClearLayerPages();
                    var errorPage = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false };
                    errorPage.Controls.Add(new Label
                    {
                        Text = error,
                        AutoSize = true,
                        ForeColor = Theme.Danger,
                        BackColor = Theme.Bg,
                        Location = new Point(10, 10),
                    });
                    AddLayerPage(errorPage);
                    SelectLayer(0);
                }
            });
        });
    }

    private void RenderPadLayer()
    {
        if (_selectedLayer >= 0 && _selectedLayer < _layerPages.Count)
            RenderPadInto(_layerPages[_selectedLayer], _selectedLayer);
    }

    private void ClearLayerPages()
    {
        _layerPageHost.Controls.Clear();
        _layerPages.Clear();
        _layerLcds.Clear();
        _selectedLayer = 0;
    }

    private void AddLayerPage(Panel page)
    {
        _layerPages.Add(page);
        _layerPageHost.Controls.Add(page);
    }

    private void SelectLayer(int index)
    {
        if (_layerPages.Count == 0) return;
        index = Math.Clamp(index, 0, _layerPages.Count - 1);
        _selectedLayer = index;
        for (var i = 0; i < _layerPages.Count; i++) _layerPages[i].Visible = i == index;
        foreach (var lcd in _layerLcds) lcd.CurrentLayer = index;
    }

    private void RenderPadInto(Panel page, int layer)
    {
        if (_padSnapshot == null) return;
        page.SuspendLayout();
        page.Controls.Clear();

        // Mirror the physical device: 4×4 keycap grid on the left, the knob
        // cluster on the right (two small knobs over the big one), the whole
        // assembly centered in the tab.
        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = Theme.Bg };
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var keys = _padSnapshot.KeyNames[layer];
        // The physical grid is 4 rows x 4 cols; column 4 of the raw matrix holds
        // the knob presses (rows 0-2) and row 4 is unused padding — neither is
        // part of the grid. Always render all 4 real rows regardless of content
        // (an empty layer should still show every clickable key, not vanish).
        const int gridRows = 4, gridCols = 4;
        var grid = new TableLayoutPanel { AutoSize = true, Margin = new Padding(0), BackColor = Theme.Bg };
        grid.ColumnCount = gridCols;
        for (var row = 0; row < gridRows; row++)
        {
            for (var col = 0; col < gridCols; col++)
            {
                var labelKey = $"L{layer}K{row},{col}";
                var code = _padSnapshot.KeyCodes[layer][row, col];
                var target = new PadTarget(layer, false, row, col, 0, false,
                    $"Layer {layer} · Key R{row + 1}C{col + 1}", labelKey);
                var cell = MakeCellFor(labelKey, keys[row, col], target, code, new Size(80, 80));
                grid.Controls.Add(cell, col, row);
            }
        }

        // Knob cluster, positioned to real proportions: small knob diameter = 1 key
        // width (80), big knob diameter = 2 key widths (160). Small knobs sit just
        // below row 0's vertical center; the big knob's center lands exactly on the
        // boundary between the bottom two rows, horizontally centered between the
        // two small knobs — matching the physical pad. The OLED between them is
        // cosmetic (shows the active layer, like the real device).
        const int smallD = 80, bigD = 160;
        var knobPanel = new Panel { Size = new Size(192, 352), Margin = new Padding(24, 0, 0, 0), BackColor = Theme.Bg };
        var sidePanel = new Panel { Size = new Size(190, 352), Margin = new Padding(28, 0, 0, 0), BackColor = Theme.Bg };

        var knobL = BuildKnobDial(smallD, 0, layer, knobPanel, sidePanel);
        knobL.Location = new Point(0, 12);
        var knobR = BuildKnobDial(smallD, 1, layer, knobPanel, sidePanel);
        knobR.Location = new Point(96, 12);
        var knobBig = BuildKnobDial(bigD, 2, layer, knobPanel, sidePanel);
        knobBig.Location = new Point(8, 184);
        var lcd = new LayerLcd
        {
            Location = new Point(13, 122),
            Size = new Size(150, 32),
            LayerCount = _padSnapshot.LayerCount,
            CurrentLayer = _selectedLayer,
        };
        lcd.LayerRequested += SelectLayer;
        if (_layerLcds.Count <= layer) _layerLcds.Add(lcd); else _layerLcds[layer] = lcd;
        knobPanel.Controls.Add(knobL);
        knobPanel.Controls.Add(knobR);
        knobPanel.Controls.Add(knobBig);
        knobPanel.Controls.Add(lcd);
        PopulateKnobSidePanel(sidePanel, layer, _selectedKnobIndex);

        var assembly = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Anchor = AnchorStyles.None,
            BackColor = Theme.Bg,
        };
        assembly.Controls.Add(grid);
        assembly.Controls.Add(knobPanel);
        assembly.Controls.Add(sidePanel);
        outer.Controls.Add(assembly, 0, 0);

        page.Controls.Add(outer);
        page.ResumeLayout();
    }

    private int _selectedKnobIndex = 2; // default to the Big Knob

    /// <summary>Builds one knob dial — clicking it selects that knob and refreshes
    /// the side panel to show its Turn Left / Turn Right / Press zones.</summary>
    private Control BuildKnobDial(int diameter, int enc, int layer, Panel knobPanel, Panel sidePanel)
    {
        var dial = new KnobDial(diameter, enc) { IsSelected = enc == _selectedKnobIndex };
        dial.OnActivate = () =>
        {
            _selectedKnobIndex = enc;
            foreach (Control c in knobPanel.Controls)
                if (c is KnobDial kd) { kd.IsSelected = kd.EncoderIndex == enc; kd.Invalidate(); }
            PopulateKnobSidePanel(sidePanel, layer, enc);
        };
        return dial;
    }

    /// <summary>Fills the side panel with the 3 assignable zones (Turn Left, Turn
    /// Right, Press) for one knob — reuses the exact same staged-assignment cell
    /// (MakeCellFor) the key grid uses, just laid out as rows instead of a square.</summary>
    private void PopulateKnobSidePanel(Panel host, int layer, int enc)
    {
        host.SuspendLayout();
        host.Controls.Clear();
        if (_padSnapshot == null) { host.ResumeLayout(); return; }

        var name = MegalodonPad.PadSnapshot.EncoderLabels[enc];
        var pressCode = _padSnapshot.KeyCodes[layer][enc, 4];
        var pressName = _padSnapshot.KeyNames[layer][enc, 4];
        var pressTarget = new PadTarget(layer, false, enc, 4, 0, false,
            $"Layer {layer} · {name} Press", $"L{layer}E{enc}:press");
        var (ccwName, cwName) = _padSnapshot.EncoderNames[layer][enc];
        var (ccwCode, cwCode) = _padSnapshot.EncoderCodes[layer][enc];
        var ccwTarget = new PadTarget(layer, true, 0, 0, enc, false,
            $"Layer {layer} · {name} Turn Left", $"L{layer}E{enc}:ccw");
        var cwTarget = new PadTarget(layer, true, 0, 0, enc, true,
            $"Layer {layer} · {name} Turn Right", $"L{layer}E{enc}:cw");

        var title = new Label
        {
            Text = name, AutoSize = true, Font = Theme.BodySemibold, ForeColor = Theme.Ink,
            Location = new Point(0, 0), BackColor = Theme.Bg,
        };
        host.Controls.Add(title);

        var y = 26;
        Control Row(string caption, string labelKey, string liveName, PadTarget target, ushort liveCode)
        {
            var wrap = new Panel { Size = new Size(190, 58), Location = new Point(0, y), BackColor = Theme.Bg };
            var cap = new Label
            {
                Text = caption, AutoSize = true, Font = Theme.CaptionSemibold, ForeColor = Theme.Subtle,
                Location = new Point(2, 0), BackColor = Theme.Bg,
            };
            var cell = MakeCellFor(labelKey, liveName, target, liveCode, new Size(190, 42));
            cell.Location = new Point(0, 16);
            wrap.Controls.Add(cap);
            wrap.Controls.Add(cell);
            y += 66;
            return wrap;
        }
        host.Controls.Add(Row("TURN LEFT", $"L{layer}E{enc}:ccw", ccwName, ccwTarget, ccwCode));
        host.Controls.Add(Row("TURN RIGHT", $"L{layer}E{enc}:cw", cwName, cwTarget, cwCode));
        host.Controls.Add(Row("PRESS", $"L{layer}E{enc}:press", pressName, pressTarget, pressCode));
        host.ResumeLayout();
    }

    /// <summary>A plain drawn knob dial — click to select, no inline text (the
    /// selected knob's zones show in the side panel instead).</summary>
    private sealed class KnobDial : Control
    {
        public int EncoderIndex { get; }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool IsSelected { get; set; }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Action? OnActivate { get; set; }

        public KnobDial(int diameter, int encoderIndex)
        {
            EncoderIndex = encoderIndex;
            Size = new Size(diameter, diameter);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            OnActivate?.Invoke();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rect = new Rectangle(2, 2, Width - 5, Height - 5);
            using (var fill = new SolidBrush(Color.FromArgb(58, 60, 66)))
                g.FillEllipse(fill, rect);
            using (var rim = new Pen(IsSelected ? Theme.Accent : Color.FromArgb(120, 124, 132), IsSelected ? 3f : 2f))
                g.DrawEllipse(rim, rect);
            using var notch = new Pen(Color.FromArgb(150, 154, 162), 2f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
            };
            var cx = Width / 2;
            g.DrawLine(notch, cx, rect.Top + 6, cx, rect.Top + rect.Height / 3);
        }
    }

    /// <summary>Panel with double buffering — custom-drawn content paints without flicker.</summary>
    private sealed class BufferedPanel : Panel
    {
        public BufferedPanel()
        {
            DoubleBuffered = true;
        }
    }

    /// <summary>
    /// A keycap: rounded corners, themed fill/border, padded centered text with
    /// wrapping and ellipsis, accent border on hover when interactive.
    /// </summary>
    private sealed class KeycapLabel : Control
    {
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Color Fill { get; set; }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Color BorderColor { get; set; }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Interactive { get; set; }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int BorderWidth { get; set; } = 1;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Muted { get; set; }

        /// <summary>Which pad position this cell represents, for drag/drop and click-to-open.</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public PadTarget? Target { get; set; }

        /// <summary>What would actually be written here (staged pending code, else the live pad code).</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public ushort EffectiveCode { get; set; }

        /// <summary>What would actually be written as this position's custom label.</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public string? EffectiveLabel { get; set; }

        /// <summary>The underlying keystroke/chord text — shown instead of Text while
        /// hovering, so a labeled cell reveals what it actually sends. Null when Text
        /// already IS the raw key (nothing to swap to).</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public string? RawKeyText { get; set; }

        /// <summary>Invoked on a plain click (no drag). Set by the caller instead of subscribing to Click,
        /// so drag gestures can be told apart from clicks.</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Action? OnActivate { get; set; }

        public enum DragVisual { None, Lifted, DropMove, DropSwap }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public DragVisual DragState { get; set; }

        private bool _hover;

        public KeycapLabel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            if (!Interactive) return;
            _hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = RoundedPath(rect, 7);

            var fill = _hover ? ControlPaint.Light(Fill, 0.3f) : Fill;
            var borderColor = _hover ? Accent : BorderColor;
            var borderWidth = BorderWidth;
            if (DragState == DragVisual.Lifted) { fill = ControlPaint.Light(Fill, 0.55f); borderWidth = 2; }
            else if (DragState == DragVisual.DropMove) { borderColor = DragMoveBorder; borderWidth = 3; }
            else if (DragState == DragVisual.DropSwap) { borderColor = DragSwapBorder; borderWidth = 3; }

            using (var fillBrush = new SolidBrush(fill))
                g.FillPath(fillBrush, path);
            using (var border = new Pen(borderColor, borderWidth)
                   {
                       DashStyle = DragState == DragVisual.Lifted
                           ? System.Drawing.Drawing2D.DashStyle.Dash
                           : System.Drawing.Drawing2D.DashStyle.Solid,
                   })
                g.DrawPath(border, path);
            var displayText = _hover && RawKeyText != null ? RawKeyText : Text;
            TextRenderer.DrawText(g, displayText, Font, Rectangle.Inflate(rect, -7, -5), ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            // Muted marker: a small crossed-out circle in the top-right corner.
            if (Muted)
            {
                var cx = Width - 12;
                var cy = 10;
                using var pen = new Pen(Color.FromArgb(220, 140, 90, 90), 1.6f);
                g.DrawEllipse(pen, cx - 5, cy - 5, 10, 10);
                g.DrawLine(pen, cx - 4, cy + 4, cx + 4, cy - 4);
            }

            // Drop-target glyph, top-left (doesn't collide with the muted marker's top-right spot).
            if (DragState == DragVisual.DropSwap)
                TextRenderer.DrawText(g, "⇄", new Font("Segoe UI", 11f, FontStyle.Bold),
                    new Point(4, 2), DragSwapBorder);
            else if (DragState == DragVisual.DropMove)
                TextRenderer.DrawText(g, "↓", new Font("Segoe UI", 11f, FontStyle.Bold),
                    new Point(4, 2), DragMoveBorder);
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundedPath(Rectangle rect, int radius)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            var d = radius * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private static string TitleCase(string text) =>
        System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text);

    /// <summary>One assignment box, styled identically everywhere (grid keys and knob zones):
    /// unassigned = washed out, assigned = tinted blue, custom-labeled = tinted green + bold.</summary>
    /// <summary>Builds a cell for a pad position, showing the staged (pending) value if one exists.</summary>
    private Control MakeCellFor(string labelKey, string liveName, PadTarget target, ushort liveCode, Size size)
    {
        if (_pendingChanges.TryGetValue(labelKey, out var pending))
            return MakeAssignmentCell(labelKey, pending.DisplayName, null, size, target, pending.Code, pending.Label,
                () => OpenAssignment(target, liveCode), pendingStyle: true);

        var custom = _settings.PadLabels.GetValueOrDefault(labelKey);
        return MakeAssignmentCell(labelKey, liveName, custom, size, target, liveCode, custom,
            () => OpenAssignment(target, liveCode));
    }

    private KeycapLabel MakeAssignmentCell(string labelKey, string keyName, string? custom, Size size,
        PadTarget target, ushort effectiveCode, string? effectiveLabel,
        Action? onClick = null, bool pendingStyle = false)
    {
        var unassigned = keyName == "—";

        // Knob zones carry a glyph prefix (⊙/⟲/⟳); strip it for chord lookup.
        var prefix = "";
        var core = keyName;
        foreach (var glyph in new[] { "⊙ ", "⟲ ", "⟳ " })
        {
            if (core.StartsWith(glyph, StringComparison.Ordinal))
            {
                prefix = glyph;
                core = core[glyph.Length..];
                break;
            }
        }

        // Auto-label from the known-chords library (user labels always win).
        // An authoritative match (e.g. Win+D -> "Show desktop") replaces the
        // chord entirely, same as a custom label — so it also needs the hover
        // reveal. A guessed one shows "(Meaning)" over the chord instead, so
        // the raw keys are already visible and there's nothing to reveal.
        var text = keyName;
        var hidesRawKey = false;
        if (custom == null && KnownChords.TryGet(core, out var known, out var authoritative))
        {
            if (authoritative) { text = $"{prefix}{known}"; hidesRawKey = true; }
            else text = $"({known})\n{prefix}{core}";
        }

        var cell = new KeycapLabel
        {
            // A custom label replaces the raw keystroke entirely — no point
            // showing both once the key's been given a real name.
            // Title-case generated names only — user labels stay exactly as typed.
            Text = custom != null ? custom : TitleCase(text),
            Size = size,
            Fill = pendingStyle ? CapPendingFill : unassigned ? CapUnassignedFill : CapAssignedFill,
            BorderColor = pendingStyle ? CapPendingBorder : unassigned ? CapUnassignedBorder : CapAssignedBorder,
            ForeColor = pendingStyle ? CapPendingText : unassigned ? CapUnassignedText : CapAssignedText,
            BorderWidth = pendingStyle ? 2 : 1,
            Muted = _settings.MutedHudKeys.Contains(labelKey),
            Margin = new Padding(4),
            Cursor = onClick != null ? Cursors.Hand : Cursors.Default,
            Interactive = onClick != null,
            Target = target,
            EffectiveCode = effectiveCode,
            EffectiveLabel = effectiveLabel,
            RawKeyText = custom != null || hidesRawKey ? TitleCase(keyName) : null,
            OnActivate = onClick,
        };
        // Long unbreakable names (chords have no spaces) shrink instead of clipping —
        // both the label and the raw key it can swap to on hover must fit.
        var longestWord = new[] { cell.Text, cell.RawKeyText ?? "" }
            .SelectMany(t => t.Split(' ', '\n')).Max(w => w.Length);
        var fontSize = custom != null ? 8f : 8.5f;
        if (size.Width < 130 && longestWord > 12) fontSize = 7.25f;
        else if (size.Width < 130 && longestWord > 9) fontSize = 7.75f;
        cell.Font = new Font("Segoe UI", fontSize);
        if (onClick != null)
        {
            WireDragHandlers(cell);
            AttachMuteMenu(cell, labelKey);
        }
        return cell;
    }

    // ---- Drag to move / swap assignments ----
    //
    // Drag a cell onto a blank one -> the assignment moves (source clears).
    // Drag a cell onto an occupied one -> the two swap. Both are staged as
    // ordinary pending changes, so "Write to Pad" commits them the same way
    // a manual edit would. Highlighting updates live as the cursor crosses
    // cells; nothing is written until drop.

    private KeycapLabel? _dragSource;
    private KeycapLabel? _dragHoverTarget;
    private Point _dragStartScreen;
    private bool _dragActive;
    private List<KeycapLabel> _draggableCells = [];

    private void WireDragHandlers(KeycapLabel cell)
    {
        cell.MouseDown += (_, e) => BeginPotentialDrag(cell, e);
        cell.MouseMove += (_, _) => ContinueDrag();
        cell.MouseUp += (_, e) => EndDrag(e);
    }

    private void BeginPotentialDrag(KeycapLabel cell, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _dragSource = cell;
        _dragStartScreen = Cursor.Position;
        _dragActive = false;
        cell.Capture = true;
    }

    private void ContinueDrag()
    {
        if (_dragSource == null) return;
        if (!_dragActive)
        {
            var dx = Cursor.Position.X - _dragStartScreen.X;
            var dy = Cursor.Position.Y - _dragStartScreen.Y;
            if (dx * dx + dy * dy < 36) return; // ~6px threshold before it counts as a drag
            if (_dragSource.EffectiveCode == KeycodeCatalog.KC_NO)
            {
                // Nothing assigned here — there's nothing to pick up.
                _dragSource.Capture = false;
                _dragSource = null;
                return;
            }
            _dragActive = true;
            _draggableCells = CollectDraggableCells(_layerPages[_selectedLayer]);
            _dragSource.DragState = KeycapLabel.DragVisual.Lifted;
            _dragSource.Cursor = Cursors.SizeAll;
            _dragSource.Invalidate();
        }
        UpdateDragHoverTarget();
    }

    private void UpdateDragHoverTarget()
    {
        var screenPos = Cursor.Position;
        KeycapLabel? found = null;
        foreach (var candidate in _draggableCells)
        {
            if (candidate == _dragSource) continue;
            if (candidate.RectangleToScreen(candidate.ClientRectangle).Contains(screenPos))
            {
                found = candidate;
                break;
            }
        }
        if (found == _dragHoverTarget) return;
        if (_dragHoverTarget != null)
        {
            _dragHoverTarget.DragState = KeycapLabel.DragVisual.None;
            _dragHoverTarget.Invalidate();
        }
        _dragHoverTarget = found;
        if (_dragHoverTarget != null)
        {
            _dragHoverTarget.DragState = _dragHoverTarget.EffectiveCode == KeycodeCatalog.KC_NO
                ? KeycapLabel.DragVisual.DropMove
                : KeycapLabel.DragVisual.DropSwap;
            _dragHoverTarget.Invalidate();
        }
    }

    private void EndDrag(MouseEventArgs e)
    {
        if (_dragSource == null || e.Button != MouseButtons.Left) return;
        var source = _dragSource;
        var target = _dragHoverTarget;
        var wasDragging = _dragActive;

        source.Capture = false;
        source.Cursor = Cursors.Hand;
        _dragSource = null;
        _dragHoverTarget = null;
        _dragActive = false;
        _draggableCells = [];

        if (!wasDragging)
        {
            source.OnActivate?.Invoke();
            return;
        }
        if (target != null)
            CommitDragDrop(source, target);
        else
        {
            source.DragState = KeycapLabel.DragVisual.None;
            source.Invalidate();
        }
    }

    private void CommitDragDrop(KeycapLabel source, KeycapLabel target)
    {
        if (source.Target == null || target.Target == null) return;
        var sourceTarget = source.Target;
        var destTarget = target.Target;
        var sourceCode = source.EffectiveCode;
        var sourceLabel = source.EffectiveLabel;
        var destCode = target.EffectiveCode;
        var destLabel = target.EffectiveLabel;
        var isSwap = destCode != KeycodeCatalog.KC_NO;

        _pendingChanges[destTarget.LabelKey] = new PendingChange(
            destTarget, sourceCode, destCode, sourceLabel, null, null, false);

        var clearedCode = sourceTarget.Layer > 0 ? KeycodeCatalog.KC_TRNS : KeycodeCatalog.KC_NO;
        _pendingChanges[sourceTarget.LabelKey] = new PendingChange(
            sourceTarget, isSwap ? destCode : clearedCode, sourceCode, isSwap ? destLabel : null, null, null, false);

        UpdatePendingBar();
        RenderAllLayers();
    }

    private static List<KeycapLabel> CollectDraggableCells(Control root)
    {
        var list = new List<KeycapLabel>();
        void Walk(Control c)
        {
            if (c is KeycapLabel { Target: not null } kc) list.Add(kc);
            foreach (Control child in c.Controls) Walk(child);
        }
        Walk(root);
        return list;
    }

    /// <summary>Right-click any pad cell to silence (or restore) its key HUD pop-up.</summary>
    private void AttachMuteMenu(KeycapLabel cell, string labelKey)
    {
        var menu = new ContextMenuStrip();
        var item = new ToolStripMenuItem("Mute pop-up notification")
        {
            CheckOnClick = true,
            Checked = _settings.MutedHudKeys.Contains(labelKey),
        };
        item.Click += (_, _) =>
        {
            if (item.Checked)
            {
                if (!_settings.MutedHudKeys.Contains(labelKey)) _settings.MutedHudKeys.Add(labelKey);
            }
            else
            {
                _settings.MutedHudKeys.Remove(labelKey);
            }
            _settings.Save();
            _tray.RefreshKeyHud();
            cell.Muted = item.Checked;
            cell.Invalidate();
        };
        menu.Items.Add(item);
        cell.ContextMenuStrip = menu;
    }

    private void OpenAssignment(PadTarget target, ushort currentCode)
    {
        if (_padSnapshot == null) return;
        // A staged change on this position supersedes the pad's current code.
        var startCode = _pendingChanges.TryGetValue(target.LabelKey, out var existing)
            ? existing.Code : currentCode;
        // Ghost codes already staged for OTHER positions this session — the
        // allocator can't see those on the pad/settings yet since nothing's
        // written until "Write to Pad", so they'd otherwise look free twice.
        var reserved = _pendingChanges
            .Where(kv => kv.Key != target.LabelKey)
            .Select(kv => kv.Value.Code)
            .ToList();
        using var dialog = new AssignmentDialog(target, startCode, _settings, _padSnapshot, reserved);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result == null) return;

        // Staging only — no write yet. If it matches the pad, it's not a change.
        if (dialog.Result.Code == currentCode && dialog.Result.Label ==
            _settings.PadLabels.GetValueOrDefault(target.LabelKey) && dialog.Result.ActionId == null)
            _pendingChanges.Remove(target.LabelKey);
        else
            _pendingChanges[target.LabelKey] = dialog.Result;
        UpdatePendingBar();
        RenderPadLayer();
    }

    // ---- Card-based settings rows, shared by Focus Mode / Extras ----

    private const int CardWidth = 560;

    /// <summary>A rounded dark card holding one or more toggle rows, each with a
    /// title, an optional description, and a switch anchored to the right —
    /// dividers appear between rows automatically.</summary>
    private Panel MakeCard(params (string Title, string Desc, ToggleSwitch Toggle)[] rows)
    {
        var card = new Panel { Width = CardWidth, BackColor = Theme.PanelAlt, Margin = new Padding(0, 0, 0, 14) };
        var y = 0;
        for (var i = 0; i < rows.Length; i++)
        {
            var (title, desc, toggle) = rows[i];
            if (i > 0)
            {
                card.Controls.Add(new Panel { Location = new Point(18, y), Size = new Size(CardWidth - 36, 1), BackColor = Theme.Line });
                y += 1;
            }
            var rowTop = y + 14;
            var titleLbl = new Label
            {
                Text = title, Font = Theme.BodySemibold, ForeColor = Theme.Ink, AutoSize = true,
                Location = new Point(18, rowTop), BackColor = Theme.PanelAlt,
            };
            card.Controls.Add(titleLbl);
            var bottom = titleLbl.Bottom;
            if (!string.IsNullOrEmpty(desc))
            {
                var descLbl = new Label
                {
                    Text = desc, Font = Theme.Caption, ForeColor = Theme.Subtle, AutoSize = true,
                    MaximumSize = new Size(380, 0), Location = new Point(18, titleLbl.Bottom + 2), BackColor = Theme.PanelAlt,
                };
                card.Controls.Add(descLbl);
                bottom = descLbl.Bottom;
            }
            toggle.Location = new Point(CardWidth - toggle.Width - 18, rowTop + (titleLbl.Height - toggle.Height) / 2);
            card.Controls.Add(toggle);
            y = Math.Max(bottom, toggle.Bottom) + 14;
        }
        card.Height = y;
        return card;
    }

    private Panel MakeSliderCard(Label captionLabel, Slider slider)
    {
        var card = new Panel { Width = CardWidth, Height = 74, BackColor = Theme.PanelAlt, Margin = new Padding(0, 0, 0, 14) };
        captionLabel.Location = new Point(18, 14);
        captionLabel.ForeColor = Theme.Subtle;
        captionLabel.Font = Theme.Caption;
        captionLabel.BackColor = Theme.PanelAlt;
        slider.Location = new Point(18, 40);
        slider.Width = CardWidth - 36;
        card.Controls.Add(captionLabel);
        card.Controls.Add(slider);
        return card;
    }

    // ---- Focus mode ----

    private Panel BuildFocusModePage()
    {
        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Theme.Bg };

        _focusModeCheck = new ToggleSwitch();
        _focusModeCheck.CheckedChanged += (_, _) => OnFocusModeChanged();
        _blurCheck = new ToggleSwitch();
        _blurCheck.CheckedChanged += (_, _) => OnFocusModeChanged();
        _peekCheck = new ToggleSwitch();
        _peekCheck.CheckedChanged += (_, _) => OnFocusModeChanged();
        stack.Controls.Add(MakeCard(
            ("Enable focus mode", "Veil everything behind the active window", _focusModeCheck),
            ("Blur background windows", "Live Gaussian blur instead of only dimming", _blurCheck),
            ("Peek", "Hovering a background window lifts the veil off it", _peekCheck)));

        _dimLabel = new Label { AutoSize = true };
        _dimTrack = new Slider { Minimum = 5, Maximum = 90 };
        _dimTrack.ValueChanged += (_, _) => OnFocusModeChanged();
        stack.Controls.Add(MakeSliderCard(_dimLabel, _dimTrack));

        return PageShell("Focus Mode",
            "Dims or blurs every window except the one you're working in. Also toggleable from the tray menu " +
            "or a mapped key.", stack);
    }

    private void OnFocusModeChanged()
    {
        UpdateFocusModeSubSettingsEnabled();
        if (_loading) return;
        _dimLabel.Text = $"Dim / tint strength: {_dimTrack.Value}%";
        _settings.FocusModeEnabled = _focusModeCheck.Checked;
        _settings.FocusModeDimPercent = _dimTrack.Value;
        _settings.FocusModeBlurEnabled = _blurCheck.Checked;
        _settings.FocusModePeekEnabled = _peekCheck.Checked;
        _settings.Save();
        _tray.ApplyFocusModeSetting();
    }

    /// <summary>Blur, peek, and the dim slider only matter once focus mode itself
    /// is on — gray them out rather than let them silently do nothing.</summary>
    private void UpdateFocusModeSubSettingsEnabled()
    {
        var on = _focusModeCheck.Checked;
        _blurCheck.Enabled = on;
        _peekCheck.Enabled = on;
        _dimTrack.Enabled = on;
    }

    // ---- Extras (legacy shortcuts + startup) ----

    private Panel BuildExtrasPage()
    {
        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Theme.Bg };

        _hudCheck = new ToggleSwitch();
        _hudCheck.CheckedChanged += (_, _) => OnExtrasChanged();
        _numpadCheck = new ToggleSwitch();
        _numpadCheck.CheckedChanged += (_, _) => OnExtrasChanged();
        _calculatorCheck = new ToggleSwitch();
        _calculatorCheck.CheckedChanged += (_, _) => OnExtrasChanged();
        stack.Controls.Add(MakeCard(
            ("Key HUD pop-ups", "Show a popup when I press a macropad key (with its label)", _hudCheck),
            ("Numpad desktop jumps", "Ctrl+Win+Numpad 1-9 jumps to that virtual desktop (NumLock on)", _numpadCheck),
            ("Calculator key fix", "Calculator key launches or focuses Calculator", _calculatorCheck)));

        _startupCheck = new ToggleSwitch();
        _startupCheck.CheckedChanged += (_, _) => OnStartupChanged();
        stack.Controls.Add(MakeCard(("Start with Windows", "Launches Switchboard automatically at sign-in", _startupCheck)));

        return PageShell("Extras",
            "Standalone shortcuts that predate key mapping, plus app startup. Desktop jumps and the calculator " +
            "fix are also available as key-mapping actions.", stack);
    }

    private void OnExtrasChanged()
    {
        if (_loading) return;
        _settings.KeyHudEnabled = _hudCheck.Checked;
        _settings.NumpadHotkeysEnabled = _numpadCheck.Checked;
        _settings.CalculatorFocusFixEnabled = _calculatorCheck.Checked;
        _settings.Save();
        _tray.ApplyHotkeySetting();
        _tray.ApplyKeyHudSetting();
    }

    private void OnStartupChanged()
    {
        if (_loading) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (_startupCheck.Checked)
            key.SetValue(RunValue, $"\"{Application.ExecutablePath}\"");
        else
            key.DeleteValue(RunValue, throwOnMissingValue: false);
        _settings.RunAtStartup = _startupCheck.Checked;
        _settings.Save();
    }

    // ---- Diagnostics ----

    private Panel BuildDiagnosticsPage()
    {
        var layout = new TableLayoutPanel { ColumnCount = 1, RowCount = 3, BackColor = Theme.Bg };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _statusLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(540, 0),
            ForeColor = Theme.Subtle,
            BackColor = Theme.Bg,
            Margin = new Padding(0, 0, 0, 8),
        };
        _detectorButton = new Button
        {
            Text = "Start key detector", AutoSize = true, Margin = new Padding(0, 0, 0, 10),
            FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Color.White, Font = Theme.BodySemibold,
        };
        _detectorButton.FlatAppearance.BorderSize = 0;
        _detectorButton.Click += (_, _) => _tray.ToggleDetector();
        _logBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.LogBg,
            ForeColor = Color.FromArgb(183, 190, 200),
            Font = Theme.Mono,
        };

        layout.Controls.Add(_statusLabel, 0, 0);
        layout.Controls.Add(_detectorButton, 0, 1);
        layout.Controls.Add(_logBox, 0, 2);
        return PageShell("Diagnostics",
            "Live activity log and the HID++ key detector for exploring Logitech devices.", layout);
    }

    // ---- State ----

    private void LoadState()
    {
        _hudCheck.Checked = _settings.KeyHudEnabled;
        _numpadCheck.Checked = _settings.NumpadHotkeysEnabled;
        _calculatorCheck.Checked = _settings.CalculatorFocusFixEnabled;
        _focusModeCheck.Checked = _settings.FocusModeEnabled;
        _blurCheck.Checked = _settings.FocusModeBlurEnabled;
        _peekCheck.Checked = _settings.FocusModePeekEnabled;
        _dimTrack.Value = Math.Clamp(_settings.FocusModeDimPercent, _dimTrack.Minimum, _dimTrack.Maximum);
        _dimLabel.Text = $"Dim / tint strength: {_dimTrack.Value}%";
        UpdateFocusModeSubSettingsEnabled();
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        _startupCheck.Checked = key?.GetValue(RunValue) != null;
        _detectorButton.Text = _tray.DetectorRunning ? "Stop key detector" : "Start key detector";
        _statusLabel.Text = _tray.CurrentStatus;
        _logBox.Text = Log.Snapshot();
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
    }

    private void OnLogLine(string line)
    {
        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            _logBox.AppendText(line + Environment.NewLine);
            _statusLabel.Text = _tray.CurrentStatus;
        });
    }

    private void OnStatusChanged()
    {
        if (IsDisposed) return;
        BeginInvoke(() => _statusLabel.Text = _tray.CurrentStatus);
    }

    private void OnBusyChanged(bool busy)
    {
        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            _detectorButton.Enabled = !busy;
            _detectorButton.Text = busy ? "Working…"
                : _tray.DetectorRunning ? "Stop key detector" : "Start key detector";
        });
    }
}
