using Switchboard.Core;
using Switchboard.Util;
using Microsoft.Win32;

namespace Switchboard.UI;

internal sealed class SettingsForm : Form
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Switchboard";

    private static readonly Color NavBack = Color.FromArgb(243, 244, 246);
    private static readonly Color Accent = Color.FromArgb(0, 103, 192);
    private static readonly Color SubtleText = Color.FromArgb(96, 102, 110);

    private readonly AppSettings _settings;
    private readonly TrayContext _tray;
    private readonly ListBox _nav;
    private readonly Panel _pageHost;
    private readonly Dictionary<string, Panel> _pages = [];

    private readonly ComboBox[] _keyCombos = new ComboBox[24];
    private CheckBox _startupCheck = null!;
    private CheckBox _numpadCheck = null!;
    private CheckBox _calculatorCheck = null!;
    private CheckBox _focusModeCheck = null!;
    private CheckBox _blurCheck = null!;
    private CheckBox _peekCheck = null!;
    private TrackBar _dimTrack = null!;
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
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ClientSize = new Size(890, 700);
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
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9.75f);

        var navPanel = new Panel { Dock = DockStyle.Left, Width = 200, BackColor = NavBack };
        var navSubtitle = new Label
        {
            Text = "by Britton Beckham",
            Font = new Font("Segoe UI", 8.25f),
            ForeColor = SubtleText,
            Dock = DockStyle.Top,
            Height = 24,
            Padding = new Padding(19, 0, 0, 0),
            BackColor = NavBack,
        };
        var navHeader = new Label
        {
            Text = "Switchboard",
            Font = new Font("Segoe UI Semibold", 13f),
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(18, 14, 0, 0),
            BackColor = NavBack,
        };
        _nav = new ListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor = NavBack,
            IntegralHeight = false,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 40,
        };
        _nav.DrawItem += DrawNavItem;
        navPanel.Controls.Add(_nav);
        navPanel.Controls.Add(navSubtitle);
        navPanel.Controls.Add(navHeader);

        _pageHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(28, 20, 28, 20), BackColor = Color.White };
        Controls.Add(_pageHost);
        Controls.Add(navPanel);

        AddPage("Key mapping", BuildKeyMappingPage());
        AddPage("Megalodon pad", BuildMegalodonPage());
        AddPage("Focus mode", BuildFocusModePage());
        AddPage("Extras", BuildExtrasPage());
        AddPage("Diagnostics", BuildDiagnosticsPage());
        _nav.SelectedIndexChanged += (_, _) => ShowPage((string)_nav.SelectedItem!);
        _nav.SelectedIndex = 0;

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

    private void DrawNavItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using (var back = new SolidBrush(selected ? Color.White : NavBack))
            e.Graphics.FillRectangle(back, e.Bounds);
        if (selected)
        {
            using var accent = new SolidBrush(Accent);
            e.Graphics.FillRectangle(accent, e.Bounds.X, e.Bounds.Y + 8, 3, e.Bounds.Height - 16);
        }
        var text = (string)_nav.Items[e.Index]!;
        using var font = selected ? new Font(Font, FontStyle.Bold) : (Font)Font.Clone();
        using var brush = new SolidBrush(Color.FromArgb(32, 36, 42));
        e.Graphics.DrawString(text, font, brush, e.Bounds.X + 18, e.Bounds.Y + 10);
    }

    private void AddPage(string title, Panel page)
    {
        page.Dock = DockStyle.Fill;
        page.Visible = false;
        _pages[title] = page;
        _pageHost.Controls.Add(page);
        _nav.Items.Add(title);
    }

    public void SelectPage(string title)
    {
        var index = _nav.Items.IndexOf(title);
        if (index >= 0) _nav.SelectedIndex = index;
    }

    private void ShowPage(string title)
    {
        foreach (var (name, page) in _pages) page.Visible = name == title;
        // The pad page always shows the live truth: re-read on every visit.
        if (title == "Megalodon pad") BeginInvoke(ReadPad);
    }

    private static Panel PageShell(string title, string subtitle, Control content)
    {
        var page = new Panel();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label
        {
            Text = title,
            Font = new Font("Segoe UI Semibold", 15f),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 2),
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = subtitle,
            ForeColor = SubtleText,
            AutoSize = true,
            MaximumSize = new Size(540, 0),
            Margin = new Padding(0, 0, 0, 14),
        }, 0, 1);
        content.Dock = DockStyle.Fill;
        layout.Controls.Add(content, 0, 2);
        page.Controls.Add(layout);
        return page;
    }

    // ---- Key mapping ----

    private Panel BuildKeyMappingPage()
    {
        var scroll = new Panel { AutoScroll = true };
        var grid = new TableLayoutPanel
        {
            ColumnCount = 4,
            AutoSize = true,
            Padding = new Padding(0, 0, 16, 0),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 236));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 236));

        for (var i = 0; i < 24; i++)
        {
            var keyNumber = i + 1;
            var label = new Label
            {
                Text = $"F{keyNumber}",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Font = new Font("Segoe UI Semibold", 9.75f),
                ForeColor = keyNumber >= 13 ? Accent : Color.FromArgb(32, 36, 42),
            };
            var combo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 224,
                Margin = new Padding(0, 3, 12, 3),
            };
            foreach (var action in ActionCatalog.All) combo.Items.Add(action.DisplayName);
            combo.SelectedIndexChanged += (_, _) => OnMappingChanged(keyNumber, combo.SelectedIndex);
            _keyCombos[i] = combo;

            // Two columns: F1-F12 left, F13-F24 right (ghost keys highlighted).
            var row = i % 12;
            var col = i / 12 * 2;
            grid.Controls.Add(label, col, row);
            grid.Controls.Add(combo, col + 1, row);
        }

        scroll.Controls.Add(grid);
        return PageShell("Key mapping",
            "Map any function key to an OS action. F13–F24 (highlighted) are \"ghost keys\" — no physical " +
            "keyboard sends them, making them perfect targets for macropad keys. Mapped keys are captured " +
            "globally; unmapped keys pass through untouched.",
            scroll);
    }

    private void OnMappingChanged(int keyNumber, int actionIndex)
    {
        if (_loading) return;
        var actionId = ActionCatalog.All[Math.Max(0, actionIndex)].Id;
        if (actionId == ActionCatalog.None)
            _settings.FunctionKeyActions.Remove($"F{keyNumber}");
        else
            _settings.FunctionKeyActions[$"F{keyNumber}"] = actionId;
        _settings.Save();
        _tray.ApplyHotkeySetting();
        _tray.NotifyStatusChanged();
    }

    // ---- Megalodon pad ----

    private TabControl _padTabs = null!;
    private MegalodonPad.PadSnapshot? _padSnapshot;

    private Panel BuildMegalodonPage()
    {
        var layout = new TableLayoutPanel { ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var refresh = new Button { Text = "Read from pad", AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
        refresh.Click += (_, _) => ReadPad();

        _padTabs = new TabControl { Dock = DockStyle.Fill };
        _padTabs.SelectedIndexChanged += (_, _) => RenderPadLayer();

        layout.Controls.Add(refresh, 0, 0);
        layout.Controls.Add(_padTabs, 0, 1);
        return PageShell("Megalodon pad",
            "Live view of your DOIO KB16's actual configuration, read over its VIA channel and decoded into " +
            "plain names. Click any key to give it your own label. Click \"Read from pad\" after changing " +
            "things in VIA.", layout);
    }

    private void ReadPad()
    {
        try
        {
            _padSnapshot = MegalodonPad.Read();
            var selected = Math.Max(0, _padTabs.SelectedIndex);
            _padTabs.TabPages.Clear();
            for (var i = 0; i < _padSnapshot.LayerCount; i++)
                _padTabs.TabPages.Add(new TabPage($"  Layer {i}  ") { BackColor = Color.White });
            _padTabs.SelectedIndex = Math.Min(selected, _padSnapshot.LayerCount - 1);
            RenderPadLayer();
        }
        catch (Exception ex)
        {
            _padSnapshot = null;
            _padTabs.TabPages.Clear();
            var errorPage = new TabPage("  Pad  ");
            errorPage.Controls.Add(new Label
            {
                Text = ex.Message,
                AutoSize = true,
                ForeColor = Color.Firebrick,
                Location = new Point(10, 10),
            });
            _padTabs.TabPages.Add(errorPage);
        }
    }

    private void RenderPadLayer()
    {
        if (_padSnapshot == null || _padTabs.SelectedIndex < 0) return;
        var layer = _padTabs.SelectedIndex;
        var page = _padTabs.SelectedTab!;
        page.SuspendLayout();
        page.Controls.Clear();

        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Padding = new Padding(8),
        };

        var keys = _padSnapshot.KeyNames[layer];
        var grid = new TableLayoutPanel { AutoSize = true, Margin = new Padding(0, 6, 0, 12) };
        grid.ColumnCount = keys.GetLength(1);
        for (var row = 0; row < keys.GetLength(0); row++)
        {
            var rowHasContent = false;
            for (var col = 0; col < keys.GetLength(1); col++)
                if (keys[row, col] != "—") rowHasContent = true;
            if (!rowHasContent) continue;

            for (var col = 0; col < keys.GetLength(1); col++)
            {
                var labelKey = $"L{layer}K{row},{col}";
                var custom = _settings.PadLabels.GetValueOrDefault(labelKey);
                var keyName = keys[row, col];
                var cell = new Label
                {
                    Text = custom != null ? $"{custom}\n({keyName})" : keyName,
                    AutoSize = false,
                    Size = new Size(112, 44),
                    TextAlign = ContentAlignment.MiddleCenter,
                    BackColor = keyName == "—" ? Color.FromArgb(248, 248, 249)
                        : custom != null ? Color.FromArgb(228, 240, 228)
                        : Color.FromArgb(240, 244, 250),
                    BorderStyle = BorderStyle.FixedSingle,
                    Margin = new Padding(3),
                    Font = new Font("Segoe UI", custom != null ? 8f : 8.5f,
                        custom != null ? FontStyle.Bold : FontStyle.Regular),
                    Cursor = keyName == "—" ? Cursors.Default : Cursors.Hand,
                };
                if (keyName != "—")
                    cell.Click += (_, _) => EditPadLabel(labelKey, keyName);
                grid.Controls.Add(cell, col, row);
            }
        }
        stack.Controls.Add(grid);

        var encoderHeader = new Label
        {
            Text = "Knobs",
            Font = new Font("Segoe UI Semibold", 10.5f),
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 2),
        };
        stack.Controls.Add(encoderHeader);
        var knobRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        for (var enc = 0; enc < _padSnapshot.EncoderNames[layer].Length; enc++)
        {
            var (ccw, cw) = _padSnapshot.EncoderNames[layer][enc];
            knobRow.Controls.Add(BuildKnobView(layer, enc, ccw, cw));
        }
        stack.Controls.Add(knobRow);

        page.Controls.Add(stack);
        page.ResumeLayout();
    }

    /// <summary>
    /// One knob: drawn dial with curved turn arrows either side, the rotation
    /// keys labeled beside the arrows, and the press slot on the dial itself.
    /// All three text zones are clickable for custom labels.
    /// </summary>
    private Control BuildKnobView(int layer, int enc, string ccwName, string cwName)
    {
        var big = enc == 2;
        var panel = new Panel { Size = new Size(196, 150), Margin = new Padding(3, 0, 3, 0) };
        var radius = big ? 32 : 25;
        var center = new Point(98, 52);

        panel.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var knobFill = new SolidBrush(Color.FromArgb(58, 60, 66));
            using var knobRim = new Pen(Color.FromArgb(120, 124, 132), 2.5f);
            using var arrow = new Pen(Accent, 2.5f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.ArrowAnchor,
            };
            g.FillEllipse(knobFill, center.X - radius, center.Y - radius, radius * 2, radius * 2);
            g.DrawEllipse(knobRim, center.X - radius, center.Y - radius, radius * 2, radius * 2);

            var arcRect = new Rectangle(center.X - radius - 10, center.Y - radius - 10,
                (radius + 10) * 2, (radius + 10) * 2);
            // Left arrow: curves counter-clockwise (arrowhead ends lower-left).
            g.DrawArc(arrow, arcRect, 250, -140);
            // Right arrow: curves clockwise (arrowhead ends lower-right).
            g.DrawArc(arrow, arcRect, 290, 140);
        };

        var name = MegalodonPad.PadSnapshot.EncoderLabels[enc];
        var title = new Label
        {
            Text = name,
            Bounds = new Rectangle(0, 128, 196, 18),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = SubtleText,
            Font = new Font("Segoe UI", 8f),
        };
        var ccwLabel = MakeKnobText($"L{layer}E{enc}:ccw", $"⟲\n{ccwName}", ContentAlignment.TopCenter,
            new Rectangle(2, 96, 94, 32), $"{name} — turn left ({ccwName})");
        var cwLabel = MakeKnobText($"L{layer}E{enc}:cw", $"⟳\n{cwName}", ContentAlignment.TopCenter,
            new Rectangle(100, 96, 94, 32), $"{name} — turn right ({cwName})");
        var pressLabel = MakeKnobText($"L{layer}E{enc}:press", "press", ContentAlignment.MiddleCenter,
            new Rectangle(center.X - radius + 4, center.Y - 14, (radius - 4) * 2, 28), $"{name} — press");
        pressLabel.BackColor = Color.FromArgb(58, 60, 66);
        pressLabel.ForeColor = Color.White;

        panel.Controls.Add(ccwLabel);
        panel.Controls.Add(cwLabel);
        panel.Controls.Add(pressLabel);
        panel.Controls.Add(title);
        return panel;
    }

    private Label MakeKnobText(string labelKey, string baseText, ContentAlignment align, Rectangle bounds, string editTitle)
    {
        var custom = _settings.PadLabels.GetValueOrDefault(labelKey);
        var label = new Label
        {
            Bounds = bounds,
            TextAlign = align,
            Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 8.75f, custom != null ? FontStyle.Bold : FontStyle.Regular),
            Text = custom != null ? $"{custom}\n{baseText.Split('\n')[^1]}" : baseText,
        };
        if (custom != null && labelKey.EndsWith(":press")) label.Text = custom;
        label.Click += (_, _) => EditPadLabel(labelKey, editTitle);
        return label;
    }

    private void EditPadLabel(string labelKey, string keyName)
    {
        using var dialog = new Form
        {
            Text = $"Label for {keyName}",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(340, 108),
            MaximizeBox = false,
            MinimizeBox = false,
            Font = Font,
        };
        var box = new TextBox
        {
            Location = new Point(14, 14),
            Width = 312,
            Text = _settings.PadLabels.GetValueOrDefault(labelKey, ""),
        };
        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new Point(170, 62), Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(251, 62), Width = 75 };
        dialog.Controls.AddRange([box, ok, cancel]);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;

        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (string.IsNullOrWhiteSpace(box.Text))
            _settings.PadLabels.Remove(labelKey);
        else
            _settings.PadLabels[labelKey] = box.Text.Trim();
        _settings.Save();
        RenderPadLayer();
    }

    // ---- Focus mode ----

    private Panel BuildFocusModePage()
    {
        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false };

        _focusModeCheck = MakeCheck("Enable focus mode (veil everything behind the active window)");
        _focusModeCheck.CheckedChanged += (_, _) => OnFocusModeChanged();
        _dimLabel = new Label { AutoSize = true, Margin = new Padding(0, 10, 0, 2) };
        _dimTrack = new TrackBar
        {
            Minimum = 5,
            Maximum = 90,
            TickFrequency = 5,
            SmallChange = 5,
            LargeChange = 10,
            Width = 340,
        };
        _dimTrack.ValueChanged += (_, _) => OnFocusModeChanged();
        _blurCheck = MakeCheck("Blur background windows (live Gaussian) instead of only dimming");
        _blurCheck.CheckedChanged += (_, _) => OnFocusModeChanged();
        _peekCheck = MakeCheck("Peek: hovering a background window lifts the veil off it");
        _peekCheck.CheckedChanged += (_, _) => OnFocusModeChanged();

        stack.Controls.Add(_focusModeCheck);
        stack.Controls.Add(_dimLabel);
        stack.Controls.Add(_dimTrack);
        stack.Controls.Add(_blurCheck);
        stack.Controls.Add(_peekCheck);
        return PageShell("Focus mode",
            "Dims or blurs every window except the one you're working in. Also toggleable from the tray menu " +
            "or a mapped key.", stack);
    }

    private void OnFocusModeChanged()
    {
        if (_loading) return;
        _dimLabel.Text = $"Dim / tint strength: {_dimTrack.Value}%";
        _settings.FocusModeEnabled = _focusModeCheck.Checked;
        _settings.FocusModeDimPercent = _dimTrack.Value;
        _settings.FocusModeBlurEnabled = _blurCheck.Checked;
        _settings.FocusModePeekEnabled = _peekCheck.Checked;
        _settings.Save();
        _tray.ApplyFocusModeSetting();
    }

    // ---- Extras (legacy shortcuts + startup) ----

    private Panel BuildExtrasPage()
    {
        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false };

        _numpadCheck = MakeCheck("Ctrl+Win+Numpad 1-9 jumps to that virtual desktop (NumLock on)");
        _numpadCheck.CheckedChanged += (_, _) => OnExtrasChanged();
        _calculatorCheck = MakeCheck("Calculator key launches or focuses Calculator");
        _calculatorCheck.CheckedChanged += (_, _) => OnExtrasChanged();
        _startupCheck = MakeCheck("Start Switchboard when Windows starts");
        _startupCheck.Margin = new Padding(0, 18, 0, 0);
        _startupCheck.CheckedChanged += (_, _) => OnStartupChanged();

        stack.Controls.Add(_numpadCheck);
        stack.Controls.Add(_calculatorCheck);
        stack.Controls.Add(_startupCheck);
        return PageShell("Extras",
            "Standalone shortcuts that predate key mapping, plus app startup. Desktop jumps and the calculator " +
            "fix are also available as key-mapping actions.", stack);
    }

    private void OnExtrasChanged()
    {
        if (_loading) return;
        _settings.NumpadHotkeysEnabled = _numpadCheck.Checked;
        _settings.CalculatorFocusFixEnabled = _calculatorCheck.Checked;
        _settings.Save();
        _tray.ApplyHotkeySetting();
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
        var layout = new TableLayoutPanel { ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _statusLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(540, 0),
            ForeColor = SubtleText,
            Margin = new Padding(0, 0, 0, 8),
        };
        _detectorButton = new Button { Text = "Start key detector", AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
        _detectorButton.Click += (_, _) => _tray.ToggleDetector();
        _logBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(250, 250, 251),
            Font = new Font("Cascadia Mono", 8.75f),
        };

        layout.Controls.Add(_statusLabel, 0, 0);
        layout.Controls.Add(_detectorButton, 0, 1);
        layout.Controls.Add(_logBox, 0, 2);
        return PageShell("Diagnostics",
            "Live activity log and the HID++ key detector for exploring Logitech devices.", layout);
    }

    private static CheckBox MakeCheck(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(0, 0, 0, 10),
    };

    // ---- State ----

    private void LoadState()
    {
        for (var i = 0; i < 24; i++)
        {
            var actionId = _settings.FunctionKeyActions.GetValueOrDefault($"F{i + 1}", ActionCatalog.None);
            _keyCombos[i].SelectedIndex = ActionCatalog.IndexOf(actionId);
        }
        _numpadCheck.Checked = _settings.NumpadHotkeysEnabled;
        _calculatorCheck.Checked = _settings.CalculatorFocusFixEnabled;
        _focusModeCheck.Checked = _settings.FocusModeEnabled;
        _blurCheck.Checked = _settings.FocusModeBlurEnabled;
        _peekCheck.Checked = _settings.FocusModePeekEnabled;
        _dimTrack.Value = Math.Clamp(_settings.FocusModeDimPercent, _dimTrack.Minimum, _dimTrack.Maximum);
        _dimLabel.Text = $"Dim / tint strength: {_dimTrack.Value}%";
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
