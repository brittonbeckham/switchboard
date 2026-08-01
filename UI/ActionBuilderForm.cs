using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Switchboard.Core;
using Switchboard.Core.CustomActions;

namespace Switchboard.UI;

/// <summary>
/// Builds a custom action visually — no block naming, no JSON, no generic
/// "type the value" text boxes. Every step kind gets its own purpose-built
/// input (a live key-combo recorder, a running-window picker, a numeric
/// stepper, a verb dropdown, ...), and a live "what this does" summary sits in
/// a persistent right-hand column instead of scrolling off screen. Saves as a
/// single-block StoredAction to CustomActionStore; the Action wizard picks it
/// up immediately since its list is read fresh from disk every time.
/// </summary>
internal sealed class ActionBuilderForm : Form
{
    private TextBox _nameBox = null!;
    private FlowLayoutPanel _stepsPanel = null!;
    private TextBox _summaryBox = null!;
    private readonly List<StepEditor> _steps = [];
    private KeyRecorderControl? _recordingControl;

    public ActionBuilderForm()
    {
        Text = "Create New Action";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        KeyPreview = true;
        ClientSize = new Size(980, 680);
        BackColor = Theme.Panel;
        ForeColor = Theme.Ink;
        Font = new Font("Segoe UI", 9.75f);

        BuildUi();
        AddStep();
        UpdateSummary();
    }

    private void BuildUi()
    {
        var nameCaption = new Label
        {
            Text = "Name", Location = new Point(20, 16), AutoSize = true,
            Font = Theme.CaptionSemibold, ForeColor = Theme.Subtle, BackColor = Theme.Panel,
        };
        _nameBox = new TextBox
        {
            Location = new Point(20, 36), Width = 596,
            PlaceholderText = "e.g. Step Away, Pin Window, Begin App Selection →",
            BackColor = Theme.PanelAlt, ForeColor = Theme.Ink, BorderStyle = BorderStyle.FixedSingle,
        };
        var nameHint = new Label
        {
            Text = "Shows in the action picker and search. What appears on the pad key itself is\n" +
                   "set (optionally) when you assign it — no separate short label here.",
            Location = new Point(20, 62), Size = new Size(596, 32),
            Font = Theme.Caption, ForeColor = Theme.Faint, BackColor = Theme.Panel,
        };

        var stepsCaption = new Label
        {
            Text = "Steps", Location = new Point(20, 104), AutoSize = true,
            Font = Theme.CaptionSemibold, ForeColor = Theme.Subtle, BackColor = Theme.Panel,
        };

        _stepsPanel = new FlowLayoutPanel
        {
            Location = new Point(20, 126), Size = new Size(596, 470),
            AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            BackColor = Theme.Panel,
        };

        var addStepBtn = new Button
        {
            Text = "+ Add Step", Location = new Point(20, 604), Size = new Size(596, 32),
            FlatStyle = FlatStyle.Flat, BackColor = Theme.PanelAlt, ForeColor = Theme.Subtle,
        };
        addStepBtn.FlatAppearance.BorderColor = Theme.Line;
        addStepBtn.FlatAppearance.BorderSize = 1;
        addStepBtn.Click += (_, _) => AddStep();

        // Right column: persistent live summary, never scrolled off screen.
        var summaryPanel = new Panel
        {
            Location = new Point(632, 16), Size = new Size(328, 620), BackColor = Theme.PanelAlt,
        };
        var summaryHeader = new Label
        {
            Text = "What this does", Location = new Point(14, 12), AutoSize = true,
            Font = Theme.CaptionSemibold, ForeColor = Theme.Subtle, BackColor = Theme.PanelAlt,
        };
        var summarySub = new Label
        {
            Text = "Updates live as you build — in order.", Location = new Point(14, 32), AutoSize = true,
            Font = Theme.Caption, ForeColor = Theme.Faint, BackColor = Theme.PanelAlt,
        };
        _summaryBox = new TextBox
        {
            Location = new Point(12, 56), Size = new Size(304, 550),
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.None, BackColor = Theme.PanelAlt,
            ForeColor = Color.FromArgb(196, 202, 210), TabStop = false,
        };
        summaryPanel.Controls.AddRange([summaryHeader, summarySub, _summaryBox]);

        var save = new Button
        {
            Text = "Save Action", Size = new Size(120, 32), Location = new Point(840, 644),
            BackColor = Theme.Accent, ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
        };
        save.FlatAppearance.BorderSize = 0;
        save.Click += (_, _) => SaveAndClose();
        var cancel = new Button
        {
            Text = "Cancel", Size = new Size(90, 32), Location = new Point(740, 644),
            DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat,
            BackColor = Theme.PanelAlt, ForeColor = Theme.Ink,
        };
        cancel.FlatAppearance.BorderColor = Theme.Line;

        Controls.AddRange([nameCaption, _nameBox, nameHint, stepsCaption, _stepsPanel, addStepBtn,
            summaryPanel, save, cancel]);
        CancelButton = cancel;
    }

    private void AddStep()
    {
        StepEditor editor = null!;
        editor = new StepEditor(
            onChanged: UpdateSummary,
            onRemove: () => { _stepsPanel.Controls.Remove(editor); _steps.Remove(editor); RenumberSteps(); UpdateSummary(); },
            onRecordRequested: BeginRecording,
            onCancelRequested: CancelRecording);
        _steps.Add(editor);
        _stepsPanel.Controls.Add(editor);
        RenumberSteps();
        UpdateSummary();
    }

    private void RenumberSteps()
    {
        for (var i = 0; i < _steps.Count; i++) _steps[i].SetDisplayNumber(i + 1);
    }

    private void UpdateSummary()
    {
        if (_steps.Count == 0)
        {
            _summaryBox.Text = "Add a step to see what this action will do.";
            return;
        }
        var lines = _steps.Select((s, i) => $"{i + 1}. {s.Describe()}");
        _summaryBox.Text = string.Join("\r\n\r\n", lines);
    }

    private void SaveAndClose()
    {
        var name = _nameBox.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "Give the action a name first.",
                "Create New Action", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var stored = _steps.Select(s => s.ToStoredStep()).ToList();
        if (stored.Count == 0)
        {
            MessageBox.Show(this, "Add at least one step first.",
                "Create New Action", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var incomplete = _steps.Where(s => !s.IsComplete()).ToList();
        if (incomplete.Count > 0)
        {
            MessageBox.Show(this, "Finish setting up every step first (some are still empty).",
                "Create New Action", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var id = $"custom-{Guid.NewGuid():N}";
        var block = new StoredBlock(name, stored);
        CustomActionStore.Add(new StoredAction(id, name, name, [block]));
        DialogResult = DialogResult.OK;
    }

    // ---- Live key-combo recording (form-level so Tab/Escape/arrows/plain
    // letters are all interceptable, instead of triggering normal dialog
    // navigation) ----

    private void BeginRecording(KeyRecorderControl control)
    {
        _recordingControl?.CancelListening();
        _recordingControl = control;
        control.BeginListening();
    }

    private void CancelRecording(KeyRecorderControl control)
    {
        if (_recordingControl != control) return;
        _recordingControl = null;
        control.CancelListening();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_recordingControl != null && TryHandleRecordingKey(keyData)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_recordingControl == null) return;
        if (TryHandleRecordingKey(e.KeyData)) { e.Handled = true; e.SuppressKeyPress = true; }
    }

    private bool TryHandleRecordingKey(Keys keyData)
    {
        if (_recordingControl == null) return false;
        var baseKey = keyData & Keys.KeyCode;

        if (baseKey is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin)
        {
            var mods = string.Join(" + ", LiveModifierParts(keyData));
            _recordingControl.UpdateLiveModifiers(mods);
            return true;
        }

        var name = KeycodeCatalog.NameForVk((ushort)baseKey);
        if (name == null) return true; // unrecognized key — swallow it, keep listening

        var parts = LiveModifierParts(keyData);
        parts.Add(name);
        var control = _recordingControl;
        _recordingControl = null;
        control.CaptureChord(string.Join("+", parts));
        return true;
    }

    private static List<string> LiveModifierParts(Keys keyData)
    {
        var parts = new List<string>();
        if ((keyData & Keys.Control) != 0) parts.Add("Ctrl");
        if ((keyData & Keys.Shift) != 0) parts.Add("Shift");
        if ((keyData & Keys.Alt) != 0) parts.Add("Alt");
        if (IsWinKeyDown()) parts.Add("Win"); // the shell eats the Win keydown itself; GetAsyncKeyState still sees it held
        return parts;
    }

    private static bool IsWinKeyDown() => (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);

    // ---- Running-window enumeration for the Focus-a-Window picker ----

    internal static List<(string Title, string ProcessName)> GetRunningWindows()
    {
        var results = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var length = GetWindowTextLength(hwnd);
            if (length == 0) return true;
            var sb = new StringBuilder(length + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            var title = sb.ToString().Trim();
            if (title.Length == 0) return true;

            GetWindowThreadProcessId(hwnd, out var pid);
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                if (seen.Add(proc.ProcessName)) results.Add((title, proc.ProcessName));
            }
            catch { /* process may have exited between enumeration and lookup */ }
            return true;
        }, IntPtr.Zero);
        return results.OrderBy(r => r.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr param);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>One step's editor: a kind dropdown that swaps the whole input
    /// area below it for something purpose-built to that kind — never a bare
    /// "type the value" text box for things that aren't actually free text.</summary>
    private sealed class StepEditor : Panel
    {
        private static readonly (string Kind, string Label)[] Kinds =
        [
            ("send-key", "Send Key / Chord"),
            ("type-text", "Type Text"),
            ("sleep", "Sleep"),
            ("focus-window", "Focus a Window"),
            ("window", "Active Window Action"),
            ("hold-key", "Hold a Modifier"),
            ("release-key", "Release a Modifier"),
            ("run", "Run Command"),
            ("run-action", "Run Another Action"),
            ("clear-field", "Clear Field"),
        ];

        private static readonly (string Verb, string Label)[] WindowVerbs =
        [
            ("pin", "Pin on top"), ("unpin", "Unpin"), ("toggle-topmost", "Toggle always on top"),
            ("maximize", "Maximize"), ("minimize", "Minimize"), ("restore", "Restore"),
            ("close", "Close"), ("opacity", "Set opacity…"), ("monitor", "Move to next monitor"),
        ];

        private readonly Label _numLabel;
        private readonly ComboBox _kindCombo;
        private readonly Panel _body;
        private readonly Action _onChanged;
        private readonly Action<KeyRecorderControl> _onRecordRequested;
        private readonly Action<KeyRecorderControl> _onCancelRequested;

        private string _kind = "send-key";

        // Per-kind live state — only the fields relevant to the current kind are ever read.
        private string? _chord;
        private string _text = "";
        private int _sleepMs = 160;
        private string? _focusProcess;
        private string _focusQuery = "";
        private bool _launch;
        private string _launchCommand = "";
        private string _windowVerb = "pin";
        private int _opacity = 60;
        private string _modifier = "Alt";
        private int _holdTimeoutMs = 5000;
        private string _runCommand = "";
        private string? _runActionId;
        private string _runActionQuery = "";

        public StepEditor(Action onChanged, Action onRemove, Action<KeyRecorderControl> onRecordRequested,
            Action<KeyRecorderControl> onCancelRequested)
        {
            _onChanged = onChanged;
            _onRecordRequested = onRecordRequested;
            _onCancelRequested = onCancelRequested;
            Size = new Size(576, 160);
            Margin = new Padding(0, 0, 0, 10);
            BackColor = Theme.PanelAlt;

            _numLabel = new Label
            {
                Text = "1", Location = new Point(12, 12), Size = new Size(20, 20),
                TextAlign = ContentAlignment.MiddleCenter, Font = Theme.Mono,
                ForeColor = Theme.Faint, BackColor = Theme.Panel,
            };
            _kindCombo = new ComboBox
            {
                Location = new Point(40, 10), Size = new Size(430, 24), DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Theme.Panel, ForeColor = Theme.Ink, FlatStyle = FlatStyle.Flat,
            };
            foreach (var (kind, label) in Kinds) _kindCombo.Items.Add(label);
            _kindCombo.SelectedIndex = 0;
            _kindCombo.SelectedIndexChanged += (_, _) =>
            {
                _kind = Kinds[_kindCombo.SelectedIndex].Kind;
                ResetStateForKind();
                RebuildBody();
                _onChanged();
            };

            var removeBtn = new Button
            {
                Text = "✕", Location = new Point(540, 8), Size = new Size(26, 26),
                FlatStyle = FlatStyle.Flat, BackColor = Theme.PanelAlt, ForeColor = Theme.Faint,
            };
            removeBtn.FlatAppearance.BorderSize = 0;
            removeBtn.Click += (_, _) => onRemove();

            _body = new Panel { Location = new Point(12, 44), Size = new Size(552, 104), BackColor = Theme.PanelAlt };

            Controls.AddRange([_numLabel, _kindCombo, removeBtn, _body]);
            RebuildBody();
        }

        public void SetDisplayNumber(int n) => _numLabel.Text = n.ToString();

        private void ResetStateForKind()
        {
            _chord = null; _text = ""; _sleepMs = 160; _focusProcess = null; _focusQuery = "";
            _launch = false; _launchCommand = ""; _windowVerb = "pin"; _opacity = 60;
            _modifier = "Alt"; _holdTimeoutMs = 5000; _runCommand = ""; _runActionId = null; _runActionQuery = "";
        }

        private static Label Caption(string text, Point loc) => new()
        {
            Text = text, Location = loc, AutoSize = true, Font = Theme.Caption, ForeColor = Theme.Subtle, BackColor = Theme.PanelAlt,
        };

        private void RebuildBody()
        {
            _body.SuspendLayout();
            _body.Controls.Clear();
            Height = 160;

            switch (_kind)
            {
                case "send-key":
                {
                    // _chord is always null here: RebuildBody only runs on first
                    // construction or right after ResetStateForKind clears it.
                    var recorder = new KeyRecorderControl { Location = new Point(0, 4), Size = new Size(340, 40) };
                    recorder.RecordRequested += () => _onRecordRequested(recorder);
                    recorder.CancelRequested += () => _onCancelRequested(recorder);
                    recorder.ChordChanged += () => { _chord = recorder.Chord; _onChanged(); };
                    _body.Controls.Add(recorder);
                    Height = 100;
                    break;
                }
                case "type-text":
                {
                    var cap = Caption("Text to type", new Point(0, 0));
                    var box = new TextBox
                    {
                        Location = new Point(0, 20), Width = 500, Text = _text, PlaceholderText = "/away",
                        BackColor = Theme.Panel, ForeColor = Theme.Ink, BorderStyle = BorderStyle.FixedSingle,
                    };
                    box.TextChanged += (_, _) => { _text = box.Text; _onChanged(); };
                    _body.Controls.AddRange([cap, box]);
                    Height = 90;
                    break;
                }
                case "sleep":
                {
                    var cap = Caption("Pause for", new Point(0, 0));
                    var upDown = new NumericUpDown
                    {
                        Location = new Point(0, 20), Width = 90, Minimum = 0, Maximum = 60000, Increment = 10,
                        Value = _sleepMs, BackColor = Theme.Panel, ForeColor = Theme.Ink, BorderStyle = BorderStyle.FixedSingle,
                    };
                    upDown.ValueChanged += (_, _) => { _sleepMs = (int)upDown.Value; _onChanged(); };
                    var unit = new Label { Text = "ms", Location = new Point(96, 24), AutoSize = true, ForeColor = Theme.Faint, BackColor = Theme.PanelAlt };

                    var quickX = 150;
                    var quickButtons = new List<Button>();
                    foreach (var v in new[] { 100, 160, 250, 500, 1000 })
                    {
                        var btn = new Button
                        {
                            Text = v.ToString(), Location = new Point(quickX, 19), Size = new Size(48, 24),
                            FlatStyle = FlatStyle.Flat, BackColor = Theme.Panel, ForeColor = Theme.Subtle,
                        };
                        btn.FlatAppearance.BorderColor = Theme.Line;
                        btn.Click += (_, _) => upDown.Value = v;
                        quickButtons.Add(btn);
                        quickX += 54;
                    }
                    _body.Controls.AddRange([cap, upDown, unit, .. quickButtons]);
                    Height = 90;
                    break;
                }
                case "clear-field":
                {
                    var cap = new Label
                    {
                        Text = "No settings needed — selects all, then deletes, in whatever field is focused.",
                        Location = new Point(0, 8), Size = new Size(520, 32), Font = Theme.Caption, ForeColor = Theme.Faint, BackColor = Theme.PanelAlt,
                    };
                    _body.Controls.Add(cap);
                    Height = 70;
                    break;
                }
                case "focus-window":
                {
                    var search = new TextBox
                    {
                        Location = new Point(0, 0), Width = 520, PlaceholderText = "Search running windows…", Text = _focusQuery,
                        BackColor = Theme.Panel, ForeColor = Theme.Ink, BorderStyle = BorderStyle.FixedSingle,
                    };
                    var list = new ListBox
                    {
                        Location = new Point(0, 26), Size = new Size(520, 100),
                        BackColor = Theme.Panel, ForeColor = Theme.Ink, BorderStyle = BorderStyle.FixedSingle,
                    };
                    var all = GetRunningWindows();
                    void PopulateList()
                    {
                        list.Items.Clear();
                        var filtered = all.Where(w => w.Title.Contains(_focusQuery, StringComparison.OrdinalIgnoreCase));
                        foreach (var w in filtered) list.Items.Add(new WindowItem(w.Title, w.ProcessName));
                        if (_focusProcess != null)
                        {
                            for (var i = 0; i < list.Items.Count; i++)
                                if (((WindowItem)list.Items[i]).ProcessName == _focusProcess) { list.SelectedIndex = i; break; }
                        }
                    }
                    PopulateList();
                    search.TextChanged += (_, _) => { _focusQuery = search.Text; PopulateList(); };
                    list.SelectedIndexChanged += (_, _) =>
                    {
                        if (list.SelectedItem is WindowItem item) { _focusProcess = item.ProcessName; _onChanged(); }
                    };

                    var launchToggle = new ToggleSwitch { Location = new Point(0, 132), Checked = _launch };
                    var launchLabel = new Label
                    {
                        Text = "Launch if not already running", Location = new Point(46, 137), AutoSize = true,
                        Font = Theme.Caption, ForeColor = Theme.Subtle, BackColor = Theme.PanelAlt,
                    };
                    var launchBox = new TextBox
                    {
                        Location = new Point(0, 162), Width = 520, PlaceholderText = "Command to launch it, e.g. wt.exe",
                        Text = _launchCommand, Visible = _launch,
                        BackColor = Theme.Panel, ForeColor = Theme.Ink, BorderStyle = BorderStyle.FixedSingle,
                    };
                    launchBox.TextChanged += (_, _) => { _launchCommand = launchBox.Text; _onChanged(); };
                    launchToggle.CheckedChanged += (_, _) =>
                    {
                        _launch = launchToggle.Checked;
                        launchBox.Visible = _launch;
                        Height = _launch ? 230 : 195;
                        _onChanged();
                    };

                    _body.Controls.AddRange([search, list, launchToggle, launchLabel, launchBox]);
                    Height = _launch ? 230 : 195;
                    break;
                }
                case "window":
                {
                    var cap = Caption("On the active window…", new Point(0, 0));
                    var combo = new ComboBox
                    {
                        Location = new Point(0, 20), Width = 260, DropDownStyle = ComboBoxStyle.DropDownList,
                        BackColor = Theme.Panel, ForeColor = Theme.Ink, FlatStyle = FlatStyle.Flat,
                    };
                    foreach (var (verb, label) in WindowVerbs) combo.Items.Add(label);
                    combo.SelectedIndex = Array.FindIndex(WindowVerbs, v => v.Verb == _windowVerb);
                    if (combo.SelectedIndex < 0) combo.SelectedIndex = 0;

                    var opacityCap = Caption("Opacity", new Point(0, 56));
                    opacityCap.Visible = _windowVerb == "opacity";
                    var slider = new Slider { Location = new Point(0, 76), Width = 300, Minimum = 10, Maximum = 100, Value = _opacity, Visible = _windowVerb == "opacity" };
                    var opacityVal = new Label { Text = $"{_opacity}%", Location = new Point(308, 80), AutoSize = true, ForeColor = Theme.Ink, BackColor = Theme.PanelAlt, Visible = _windowVerb == "opacity" };

                    combo.SelectedIndexChanged += (_, _) =>
                    {
                        _windowVerb = WindowVerbs[combo.SelectedIndex].Verb;
                        var showOpacity = _windowVerb == "opacity";
                        opacityCap.Visible = slider.Visible = opacityVal.Visible = showOpacity;
                        Height = showOpacity ? 130 : 70;
                        _onChanged();
                    };
                    slider.ValueChanged += (_, _) => { _opacity = slider.Value; opacityVal.Text = $"{_opacity}%"; _onChanged(); };

                    _body.Controls.AddRange([cap, combo, opacityCap, slider, opacityVal]);
                    Height = _windowVerb == "opacity" ? 130 : 70;
                    break;
                }
                case "hold-key":
                case "release-key":
                {
                    var cap = Caption("Modifier", new Point(0, 0));
                    var modButtons = new List<Button>();
                    var x = 0;
                    foreach (var m in new[] { "Ctrl", "Shift", "Alt", "Win" })
                    {
                        var on = _modifier == m;
                        var btn = new Button
                        {
                            Text = m, Location = new Point(x, 20), Size = new Size(64, 28),
                            FlatStyle = FlatStyle.Flat,
                            BackColor = on ? Theme.AccentSoft : Theme.Panel,
                            ForeColor = on ? Theme.Accent : Theme.Subtle,
                        };
                        btn.FlatAppearance.BorderColor = on ? Theme.Accent : Theme.Line;
                        var captured = m;
                        btn.Click += (_, _) => { _modifier = captured; RebuildBody(); _onChanged(); };
                        modButtons.Add(btn);
                        x += 70;
                    }
                    _body.Controls.AddRange([cap, .. modButtons]);

                    if (_kind == "hold-key")
                    {
                        var timeoutCap = Caption("Auto-release after", new Point(0, 58));
                        var upDown = new NumericUpDown
                        {
                            Location = new Point(0, 78), Width = 90, Minimum = 100, Maximum = 60000, Increment = 500,
                            Value = _holdTimeoutMs, BackColor = Theme.Panel, ForeColor = Theme.Ink, BorderStyle = BorderStyle.FixedSingle,
                        };
                        upDown.ValueChanged += (_, _) => { _holdTimeoutMs = (int)upDown.Value; _onChanged(); };
                        var unit = new Label
                        {
                            Text = "ms, unless re-armed sooner", Location = new Point(96, 82), AutoSize = true,
                            Font = Theme.Caption, ForeColor = Theme.Faint, BackColor = Theme.PanelAlt,
                        };
                        _body.Controls.AddRange([timeoutCap, upDown, unit]);
                        Height = 140;
                    }
                    else
                    {
                        Height = 80;
                    }
                    break;
                }
                case "run":
                {
                    var cap = Caption("Command, script, file, or URL", new Point(0, 0));
                    var box = new TextBox
                    {
                        Location = new Point(0, 20), Width = 420, Text = _runCommand,
                        PlaceholderText = "wt.exe -d \"D:\\code\\project\"",
                        BackColor = Theme.Panel, ForeColor = Theme.Ink, BorderStyle = BorderStyle.FixedSingle,
                    };
                    box.TextChanged += (_, _) => { _runCommand = box.Text; _onChanged(); };
                    var browse = new Button
                    {
                        Text = "Browse…", Location = new Point(426, 19), Size = new Size(94, 26),
                        FlatStyle = FlatStyle.Flat, BackColor = Theme.Panel, ForeColor = Theme.Ink,
                    };
                    browse.FlatAppearance.BorderColor = Theme.Line;
                    browse.Click += (_, _) =>
                    {
                        using var dialog = new OpenFileDialog { CheckFileExists = true };
                        if (dialog.ShowDialog() == DialogResult.OK)
                        {
                            box.Text = dialog.FileName.Contains(' ') ? $"\"{dialog.FileName}\"" : dialog.FileName;
                        }
                    };
                    _body.Controls.AddRange([cap, box, browse]);
                    Height = 90;
                    break;
                }
                case "run-action":
                {
                    var search = new TextBox
                    {
                        Location = new Point(0, 0), Width = 520, PlaceholderText = "Search actions…", Text = _runActionQuery,
                        BackColor = Theme.Panel, ForeColor = Theme.Ink, BorderStyle = BorderStyle.FixedSingle,
                    };
                    var list = new ListBox
                    {
                        Location = new Point(0, 26), Size = new Size(520, 90),
                        BackColor = Theme.Panel, ForeColor = Theme.Ink, BorderStyle = BorderStyle.FixedSingle,
                    };
                    void PopulateActions()
                    {
                        list.Items.Clear();
                        var candidates = ActionCatalog.All.Where(a => a.Id != ActionCatalog.None &&
                            a.DisplayName.Contains(_runActionQuery, StringComparison.OrdinalIgnoreCase));
                        foreach (var a in candidates) list.Items.Add(new ActionItem(a.Id, a.DisplayName));
                        if (_runActionId != null)
                        {
                            for (var i = 0; i < list.Items.Count; i++)
                                if (((ActionItem)list.Items[i]).Id == _runActionId) { list.SelectedIndex = i; break; }
                        }
                    }
                    PopulateActions();
                    search.TextChanged += (_, _) => { _runActionQuery = search.Text; PopulateActions(); };
                    list.SelectedIndexChanged += (_, _) =>
                    {
                        if (list.SelectedItem is ActionItem item) { _runActionId = item.Id; _onChanged(); }
                    };
                    _body.Controls.AddRange([search, list]);
                    Height = 160;
                    break;
                }
            }

            _body.ResumeLayout();
        }

        public bool IsComplete() => _kind switch
        {
            "send-key" => _chord != null,
            "type-text" => _text.Length > 0,
            "focus-window" => _focusProcess != null,
            "run" => _runCommand.Trim().Length > 0,
            "run-action" => _runActionId != null,
            _ => true,
        };

        public StoredStep ToStoredStep() => _kind switch
        {
            "send-key" => new StoredStep("send-keys", _chord ?? ""),
            "type-text" => new StoredStep("send-keys", _text),
            "sleep" => new StoredStep("sleep", _sleepMs.ToString()),
            "focus-window" => new StoredStep("focus-window",
                _launch && _launchCommand.Trim().Length > 0 ? $"{_focusProcess}|{_launchCommand.Trim()}" : _focusProcess ?? ""),
            "window" => new StoredStep("window", _windowVerb == "opacity" ? $"opacity:{_opacity}" : _windowVerb),
            "hold-key" => new StoredStep("hold-key", $"{_modifier}:{_holdTimeoutMs}"),
            "release-key" => new StoredStep("release-key", _modifier),
            "run" => new StoredStep("run", _runCommand.Trim()),
            "run-action" => new StoredStep("run-action", _runActionId ?? ""),
            "clear-field" => new StoredStep("clear-field", ""),
            _ => new StoredStep("send-keys", ""),
        };

        public string Describe()
        {
            var step = ToStoredStep();
            return ActionStepRunner.DescribeStep(step.Kind, step.Value);
        }

        private sealed class WindowItem(string title, string processName)
        {
            public string ProcessName => processName;
            public override string ToString() => title;
        }

        private sealed class ActionItem(string id, string displayName)
        {
            public string Id => id;
            public override string ToString() => displayName;
        }
    }
}
