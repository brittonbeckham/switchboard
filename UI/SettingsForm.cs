using Switchboard.Core;
using Switchboard.Util;
using Microsoft.Win32;

namespace Switchboard.UI;

internal sealed class SettingsForm : Form
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Switchboard";

    private static readonly Color NavBack = Color.FromArgb(238, 241, 245);
    private static readonly Color Accent = Color.FromArgb(0, 103, 192);
    private static readonly Color SubtleText = Color.FromArgb(96, 102, 110);

    // Keycap palette: one visual language for every assignment surface.
    private static readonly Color CapUnassignedFill = Color.FromArgb(247, 248, 250);
    private static readonly Color CapUnassignedBorder = Color.FromArgb(228, 231, 235);
    private static readonly Color CapUnassignedText = Color.FromArgb(182, 188, 197);
    private static readonly Color CapAssignedFill = Color.FromArgb(232, 240, 252);
    private static readonly Color CapAssignedBorder = Color.FromArgb(183, 207, 234);
    private static readonly Color CapAssignedText = Color.FromArgb(31, 58, 92);
    private static readonly Color CapCustomFill = Color.FromArgb(228, 242, 228);
    private static readonly Color CapCustomBorder = Color.FromArgb(180, 214, 180);
    private static readonly Color CapCustomText = Color.FromArgb(30, 70, 32);

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

        AddPage("Key Mapping", BuildKeyMappingPage());
        AddPage("Megalodon Pad", BuildMegalodonPage());
        AddPage("Focus Mode", BuildFocusModePage());
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
        for (var i = 0; i < _nav.Items.Count; i++)
        {
            if (string.Equals((string)_nav.Items[i]!, title, StringComparison.OrdinalIgnoreCase))
            {
                _nav.SelectedIndex = i;
                return;
            }
        }
    }

    private void ShowPage(string title)
    {
        foreach (var (name, page) in _pages) page.Visible = name == title;
        // The pad page always shows the live truth: re-read on every visit.
        if (title == "Megalodon Pad") BeginInvoke(ReadPad);
    }

    private static Panel PageShell(string title, string subtitle, Control content, Control? headerRight = null)
    {
        var page = new Panel();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var titleRow = new Panel { Height = 38, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 2), Width = 600 };
        titleRow.Controls.Add(new Label
        {
            Text = title,
            Font = new Font("Segoe UI Semibold", 15f),
            AutoSize = true,
            Location = new Point(0, 0),
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
        return PageShell("Key Mapping",
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
        var headerButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        var restore = new Button { Text = "Restore…", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
        restore.Click += (_, _) => RestorePadBackup();
        var refresh = new Button { Text = "⟳  Refresh", AutoSize = true };
        refresh.Click += (_, _) => ReadPad();
        headerButtons.Controls.Add(restore);
        headerButtons.Controls.Add(refresh);
        headerButtons.Size = headerButtons.PreferredSize;

        _padTabs = new TabControl { Dock = DockStyle.Fill };

        return PageShell("Megalodon Pad",
            "Your DOIO KB16's live configuration. Click any key or knob zone to change its assignment " +
            "or give it a label — changes are written straight to the pad and verified.",
            _padTabs, headerButtons);
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
                    var selected = Math.Max(0, _padTabs.SelectedIndex);
                    _padTabs.TabPages.Clear();
                    // Render every layer up front: switching tabs is then pure
                    // native paint — no rebuild, no flicker.
                    for (var i = 0; i < snapshot.LayerCount; i++)
                    {
                        var tabPage = new TabPage($"  Layer {i}  ") { BackColor = Color.White };
                        _padTabs.TabPages.Add(tabPage);
                        RenderPadInto(tabPage, i);
                    }
                    _padTabs.SelectedIndex = Math.Min(selected, snapshot.LayerCount - 1);
                }
                else
                {
                    _padSnapshot = null;
                    _padTabs.TabPages.Clear();
                    var errorPage = new TabPage("  Pad  ");
                    errorPage.Controls.Add(new Label
                    {
                        Text = error,
                        AutoSize = true,
                        ForeColor = Color.Firebrick,
                        Location = new Point(10, 10),
                    });
                    _padTabs.TabPages.Add(errorPage);
                }
            });
        });
    }

    private void RenderPadLayer()
    {
        if (_padTabs.SelectedTab != null && _padTabs.SelectedIndex >= 0)
            RenderPadInto(_padTabs.SelectedTab, _padTabs.SelectedIndex);
    }

    private void RenderPadInto(TabPage page, int layer)
    {
        if (_padSnapshot == null) return;
        page.SuspendLayout();
        page.Controls.Clear();

        // Mirror the physical device: 4×4 keycap grid on the left, the knob
        // cluster on the right (two small knobs over the big one), the whole
        // assembly centered in the tab.
        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var keys = _padSnapshot.KeyNames[layer];
        // Column 4 of the matrix holds the knob presses (rows 0-2), not grid keys.
        const int gridCols = 4;
        var grid = new TableLayoutPanel { AutoSize = true, Margin = new Padding(0) };
        grid.ColumnCount = gridCols;
        for (var row = 0; row < keys.GetLength(0); row++)
        {
            var rowHasContent = false;
            for (var col = 0; col < gridCols; col++)
                if (keys[row, col] != "—") rowHasContent = true;
            if (!rowHasContent) continue;

            for (var col = 0; col < gridCols; col++)
            {
                var labelKey = $"L{layer}K{row},{col}";
                var custom = _settings.PadLabels.GetValueOrDefault(labelKey);
                var code = _padSnapshot.KeyCodes[layer][row, col];
                var target = new PadTarget(layer, false, row, col, 0, false,
                    $"Layer {layer} · Key R{row + 1}C{col + 1}", labelKey);
                var cell = MakeAssignmentCell(labelKey, keys[row, col], custom, new Size(80, 80),
                    () => OpenAssignment(target, code));
                grid.Controls.Add(cell, col, row);
            }
        }

        var knobColumn = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(18, 0, 0, 0),
        };
        var smallKnobs = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        smallKnobs.Controls.Add(BuildKnobView(layer, 0, _padSnapshot.EncoderNames[layer][0].Ccw,
            _padSnapshot.EncoderNames[layer][0].Cw, keys[0, 4], big: false));
        smallKnobs.Controls.Add(BuildKnobView(layer, 1, _padSnapshot.EncoderNames[layer][1].Ccw,
            _padSnapshot.EncoderNames[layer][1].Cw, keys[1, 4], big: false));
        knobColumn.Controls.Add(smallKnobs);
        var bigKnob = BuildKnobView(layer, 2, _padSnapshot.EncoderNames[layer][2].Ccw,
            _padSnapshot.EncoderNames[layer][2].Cw, keys[2, 4], big: true);
        bigKnob.Margin = new Padding(0, 14, 0, 0);
        knobColumn.Controls.Add(bigKnob);

        var assembly = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Anchor = AnchorStyles.None,
        };
        assembly.Controls.Add(grid);
        assembly.Controls.Add(knobColumn);
        outer.Controls.Add(assembly, 0, 0);

        page.Controls.Add(outer);
        page.ResumeLayout();
    }

    /// <summary>
    /// One knob: drawn dial with curved turn arrows either side, the rotation
    /// keys labeled beside the arrows, and the press slot on the dial itself.
    /// All three text zones are clickable for custom labels.
    /// </summary>
    private Control BuildKnobView(int layer, int enc, string ccwName, string cwName, string pressName, bool big)
    {
        // Vertical tile: press cell, dial with turn arrows, turn cells, name.
        var width = big ? 228 : 111;
        var panel = new BufferedPanel { Size = new Size(width, big ? 176 : 196), Margin = new Padding(2, 0, 2, 0) };
        var radius = big ? 25 : 17;
        var center = new Point(width / 2, big ? 66 : 72);

        panel.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var knobFill = new SolidBrush(Color.FromArgb(58, 60, 66));
            using var knobRim = new Pen(Color.FromArgb(120, 124, 132), 2.5f);
            using var arrow = new Pen(Accent, 2.2f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.ArrowAnchor,
            };
            g.FillEllipse(knobFill, center.X - radius, center.Y - radius, radius * 2, radius * 2);
            g.DrawEllipse(knobRim, center.X - radius, center.Y - radius, radius * 2, radius * 2);

            var arcRect = new Rectangle(center.X - radius - 8, center.Y - radius - 8,
                (radius + 8) * 2, (radius + 8) * 2);
            g.DrawArc(arrow, arcRect, 250, -140); // counter-clockwise arrow
            g.DrawArc(arrow, arcRect, 290, 140);  // clockwise arrow
        };

        var name = MegalodonPad.PadSnapshot.EncoderLabels[enc];
        var cellWidth = width - 10;

        // Assignment targets: the press lives in the key matrix (column 4,
        // row = encoder index); turns are true encoder positions.
        var pressCode = _padSnapshot!.KeyCodes[layer][enc, 4];
        var pressTarget = new PadTarget(layer, false, enc, 4, 0, false,
            $"Layer {layer} · {name} Press", $"L{layer}E{enc}:press");
        var ccwCode = _padSnapshot.EncoderCodes[layer][enc].Ccw;
        var ccwTarget = new PadTarget(layer, true, 0, 0, enc, false,
            $"Layer {layer} · {name} Turn Left", $"L{layer}E{enc}:ccw");
        var cwCode = _padSnapshot.EncoderCodes[layer][enc].Cw;
        var cwTarget = new PadTarget(layer, true, 0, 0, enc, true,
            $"Layer {layer} · {name} Turn Right", $"L{layer}E{enc}:cw");

        // The dial's drawn arrows carry direction; the cells carry only names.
        // Narrow tiles get two-line cells so chords stay readable.
        var pressCell = MakeAssignmentCell($"L{layer}E{enc}:press", pressName,
            _settings.PadLabels.GetValueOrDefault($"L{layer}E{enc}:press"),
            new Size(cellWidth, big ? 26 : 38), () => OpenAssignment(pressTarget, pressCode));
        pressCell.Location = new Point(5, 2);

        Control ccwCell, cwCell;
        if (big)
        {
            // Wide tile: turn cells side by side beneath their arrows.
            ccwCell = MakeAssignmentCell($"L{layer}E{enc}:ccw", ccwName,
                _settings.PadLabels.GetValueOrDefault($"L{layer}E{enc}:ccw"), new Size(107, 40),
                () => OpenAssignment(ccwTarget, ccwCode));
            ccwCell.Location = new Point(5, 98);
            cwCell = MakeAssignmentCell($"L{layer}E{enc}:cw", cwName,
                _settings.PadLabels.GetValueOrDefault($"L{layer}E{enc}:cw"), new Size(107, 40),
                () => OpenAssignment(cwTarget, cwCode));
            cwCell.Location = new Point(116, 98);
        }
        else
        {
            // Narrow tile: turn cells stacked, left-turn first.
            ccwCell = MakeAssignmentCell($"L{layer}E{enc}:ccw", ccwName,
                _settings.PadLabels.GetValueOrDefault($"L{layer}E{enc}:ccw"), new Size(cellWidth, 32),
                () => OpenAssignment(ccwTarget, ccwCode));
            ccwCell.Location = new Point(5, 106);
            cwCell = MakeAssignmentCell($"L{layer}E{enc}:cw", cwName,
                _settings.PadLabels.GetValueOrDefault($"L{layer}E{enc}:cw"), new Size(cellWidth, 32),
                () => OpenAssignment(cwTarget, cwCode));
            cwCell.Location = new Point(5, 142);
        }

        var title = new Label
        {
            Text = name,
            Bounds = new Rectangle(0, panel.Height - 18, width, 16),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = SubtleText,
            Font = new Font("Segoe UI", 8f),
        };

        panel.Controls.Add(pressCell);
        panel.Controls.Add(ccwCell);
        panel.Controls.Add(cwCell);
        panel.Controls.Add(title);
        return panel;
    }

    /// <summary>Panel with double buffering — custom-drawn content (knob dials) paints without flicker.</summary>
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

        private bool _hover;

        public KeycapLabel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
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
            using (var fill = new SolidBrush(_hover ? ControlPaint.Light(Fill, 0.3f) : Fill))
                g.FillPath(fill, path);
            using (var border = new Pen(_hover ? Accent : BorderColor))
                g.DrawPath(border, path);
            TextRenderer.DrawText(g, Text, Font, Rectangle.Inflate(rect, -7, -5), ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
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
    private Control MakeAssignmentCell(string labelKey, string keyName, string? custom, Size size,
        Action? onClick = null)
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
        // Certain chords show the meaning alone; guessed ones show "(Meaning)"
        // over the chord — label on top, chord beneath, everywhere.
        var text = keyName;
        if (custom == null && KnownChords.TryGet(core, out var known, out var authoritative))
            text = authoritative ? $"{prefix}{known}" : $"({known})\n{prefix}{core}";

        var cell = new KeycapLabel
        {
            // Title-case generated names only — user labels stay exactly as typed.
            Text = custom != null ? $"{custom}\n({TitleCase(keyName)})" : TitleCase(text),
            Size = size,
            Fill = unassigned ? CapUnassignedFill : custom != null ? CapCustomFill : CapAssignedFill,
            BorderColor = unassigned ? CapUnassignedBorder : custom != null ? CapCustomBorder : CapAssignedBorder,
            ForeColor = unassigned ? CapUnassignedText : custom != null ? CapCustomText : CapAssignedText,
            Margin = new Padding(4),
            Cursor = onClick != null ? Cursors.Hand : Cursors.Default,
            Interactive = onClick != null,
        };
        // Long unbreakable names (chords have no spaces) shrink instead of clipping.
        var longestWord = cell.Text.Split(' ', '\n').Max(w => w.Length);
        var fontSize = custom != null ? 8f : 8.5f;
        if (size.Width < 130 && longestWord > 12) fontSize = 7.25f;
        else if (size.Width < 130 && longestWord > 9) fontSize = 7.75f;
        cell.Font = new Font("Segoe UI", fontSize, custom != null ? FontStyle.Bold : FontStyle.Regular);
        if (onClick != null)
            cell.Click += (_, _) => onClick();
        return cell;
    }

    private void OpenAssignment(PadTarget target, ushort currentCode)
    {
        if (_padSnapshot == null) return;
        using var dialog = new AssignmentDialog(target, currentCode, _settings, _padSnapshot, () =>
        {
            _tray.ApplyHotkeySetting();
            _tray.NotifyStatusChanged();
        });
        if (dialog.ShowDialog(this) == DialogResult.OK)
            ReadPad(); // re-read so every cell reflects the pad's actual truth
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
        return PageShell("Focus Mode",
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
