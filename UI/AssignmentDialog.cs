using Switchboard.Core;

namespace Switchboard.UI;

/// <summary>One position on the pad that can be written.</summary>
internal sealed record PadTarget(
    int Layer, bool IsEncoder, int Row, int Col, int Encoder, bool Clockwise,
    string DisplayName, string LabelKey);

/// <summary>A staged (not-yet-written) assignment for one pad position.</summary>
internal sealed record PendingChange(
    PadTarget Target, ushort Code, ushort OldCode, string? Label,
    string? ActionId, int GhostFn, bool ReleaseOldMapping)
{
    /// <summary>What the cell should read while pending.</summary>
    public string DisplayName => MegalodonPad.KeycodeName(Code);
}

/// <summary>
/// The assignment editor: Key / Chord / Action / Layer / Clear modes, a label
/// field, and a live preview. Staging only — it returns a PendingChange; the
/// page commits pending changes to the pad in a batch.
/// </summary>
internal sealed class AssignmentDialog : Form
{
    private readonly PadTarget _target;
    private readonly ushort _currentCode;
    private readonly AppSettings _settings;
    private readonly MegalodonPad.PadSnapshot _snapshot;

    private readonly CheckBox[] _modeButtons = new CheckBox[5];
    private readonly Panel[] _modePanels = new Panel[5];
    private static readonly string[] ModeNames = ["Key", "Chord", "Action", "Layer", "Clear"];

    private KeyPickerControl _keyPicker = null!;
    private KeyPickerControl _chordKeyPicker = null!;
    private readonly CheckBox[] _modToggles = new CheckBox[4];
    private ListBox _actionList = null!;
    private RadioButton _layerGo = null!, _layerHold = null!, _layerToggle = null!;
    private NumericUpDown _layerNumber = null!;
    private RadioButton _clearTransparent = null!, _clearNothing = null!;
    private TextBox _labelBox = null!;
    private Label _preview = null!;
    private Label _errorBanner = null!;
    private CheckBox _releaseMapping = null!;
    private Button _writeButton = null!;

    private int _mode;

    public AssignmentDialog(PadTarget target, ushort currentCode, AppSettings settings,
        MegalodonPad.PadSnapshot snapshot)
    {
        _target = target;
        _currentCode = currentCode;
        _settings = settings;
        _snapshot = snapshot;

        Text = target.DisplayName;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(600, 584);
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9.75f);

        BuildUi();
        PreselectFromCurrent();
        UpdatePreview();
    }

    private void BuildUi()
    {
        var current = new Label
        {
            Text = $"Currently: {MegalodonPad.KeycodeName(_currentCode)}",
            ForeColor = Color.FromArgb(96, 102, 110),
            Location = new Point(20, 12),
            AutoSize = true,
        };
        Controls.Add(current);

        // Mode segments.
        for (var i = 0; i < 5; i++)
        {
            var index = i;
            var button = new CheckBox
            {
                Text = ModeNames[i],
                Appearance = Appearance.Button,
                TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(108, 32),
                Location = new Point(20 + i * 112, 38),
                FlatStyle = FlatStyle.Flat,
            };
            button.FlatAppearance.CheckedBackColor = Color.FromArgb(232, 240, 252);
            button.FlatAppearance.BorderColor = Color.FromArgb(183, 207, 234);
            button.Click += (_, _) => SelectMode(index);
            _modeButtons[i] = button;
            Controls.Add(button);
        }

        // Mode panels share one region.
        var panelBounds = new Rectangle(20, 80, 560, 330);
        for (var i = 0; i < 5; i++)
        {
            _modePanels[i] = new Panel { Bounds = panelBounds, Visible = false };
            Controls.Add(_modePanels[i]);
        }

        // Key.
        _keyPicker = new KeyPickerControl { Dock = DockStyle.Fill };
        _keyPicker.SelectionChanged += UpdatePreview;
        _modePanels[0].Controls.Add(_keyPicker);

        // Chord: modifier toggles + key picker.
        var modNames = new[] { "Ctrl", "Shift", "Alt", "Win" };
        for (var i = 0; i < 4; i++)
        {
            var toggle = new CheckBox
            {
                Text = modNames[i],
                Appearance = Appearance.Button,
                TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(80, 34),
                Location = new Point(i * 88, 0),
                FlatStyle = FlatStyle.Flat,
            };
            toggle.FlatAppearance.CheckedBackColor = Color.FromArgb(232, 240, 252);
            toggle.CheckedChanged += (_, _) => UpdatePreview();
            _modToggles[i] = toggle;
            _modePanels[1].Controls.Add(toggle);
        }
        _chordKeyPicker = new KeyPickerControl
        {
            Bounds = new Rectangle(0, 42, 560, 288),
        };
        _chordKeyPicker.SelectionChanged += UpdatePreview;
        _modePanels[1].Controls.Add(_chordKeyPicker);

        // Action.
        _actionList = new ListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            ItemHeight = 24,
        };
        foreach (var action in ActionCatalog.All)
        {
            if (action.Id != ActionCatalog.None) _actionList.Items.Add(action.DisplayName);
        }
        _actionList.SelectedIndexChanged += (_, _) => UpdatePreview();
        _modePanels[2].Controls.Add(_actionList);

        // Layer.
        _layerGo = new RadioButton { Text = "Go To (switch and stay)", Location = new Point(0, 4), AutoSize = true, Checked = true };
        _layerHold = new RadioButton { Text = "While Held (like a Shift key)", Location = new Point(0, 32), AutoSize = true };
        _layerToggle = new RadioButton { Text = "Toggle (tap on, tap off)", Location = new Point(0, 60), AutoSize = true };
        _layerNumber = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 3,
            Location = new Point(80, 94),
            Width = 60,
        };
        var layerLabel = new Label { Text = "Layer:", Location = new Point(0, 96), AutoSize = true };
        foreach (var radio in new[] { _layerGo, _layerHold, _layerToggle })
            radio.CheckedChanged += (_, _) => UpdatePreview();
        _layerNumber.ValueChanged += (_, _) => UpdatePreview();
        _modePanels[3].Controls.AddRange([_layerGo, _layerHold, _layerToggle, layerLabel, _layerNumber]);

        // Clear.
        _clearTransparent = new RadioButton
        {
            Text = "Transparent — fall through to Layer 0's assignment",
            Location = new Point(0, 4),
            AutoSize = true,
            Checked = _target.Layer > 0,
            Enabled = _target.Layer > 0,
        };
        _clearNothing = new RadioButton
        {
            Text = "Nothing — the key does nothing",
            Location = new Point(0, 32),
            AutoSize = true,
            Checked = _target.Layer == 0,
        };
        _clearTransparent.CheckedChanged += (_, _) => UpdatePreview();
        _clearNothing.CheckedChanged += (_, _) => UpdatePreview();
        _modePanels[4].Controls.AddRange([_clearTransparent, _clearNothing]);

        // Label field.
        var labelCaption = new Label { Text = "Label (optional):", Location = new Point(20, 422), AutoSize = true };
        _labelBox = new TextBox
        {
            Location = new Point(140, 419),
            Width = 440,
            Text = _settings.PadLabels.GetValueOrDefault(_target.LabelKey, ""),
        };
        Controls.Add(labelCaption);
        Controls.Add(_labelBox);

        // Release-mapping question (shown only when relevant).
        _releaseMapping = new CheckBox
        {
            Location = new Point(20, 448),
            AutoSize = true,
            Visible = false,
            Checked = true,
        };
        Controls.Add(_releaseMapping);
        if (KeycodeCatalog.IsGhostKey(_currentCode, out var fn) &&
            _settings.FunctionKeyActions.TryGetValue($"F{fn}", out var mappedAction))
        {
            var actionName = ActionCatalog.All.FirstOrDefault(a => a.Id == mappedAction)?.DisplayName ?? mappedAction;
            _releaseMapping.Text = $"Also release Switchboard mapping F{fn} → {actionName}";
            _releaseMapping.Visible = true;
        }

        // Preview + error + buttons.
        _preview = new Label
        {
            Location = new Point(20, 478),
            Size = new Size(560, 34),
            ForeColor = Color.FromArgb(31, 58, 92),
            Font = new Font("Segoe UI Semibold", 9.75f),
        };
        _errorBanner = new Label
        {
            Location = new Point(20, 478),
            Size = new Size(560, 34),
            ForeColor = Color.White,
            BackColor = Color.FromArgb(200, 50, 50),
            TextAlign = ContentAlignment.MiddleCenter,
            Visible = false,
        };
        Controls.Add(_errorBanner);
        Controls.Add(_preview);

        _writeButton = new Button
        {
            Text = "Assign",
            Size = new Size(130, 34),
            Location = new Point(450, 528),
            BackColor = Color.FromArgb(0, 103, 192),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
        };
        _writeButton.FlatAppearance.BorderSize = 0;
        _writeButton.Click += (_, _) => ConfirmAssignment();
        var cancel = new Button
        {
            Text = "Cancel",
            Size = new Size(90, 34),
            Location = new Point(350, 528),
            DialogResult = DialogResult.Cancel,
        };
        Controls.Add(_writeButton);
        Controls.Add(cancel);
        CancelButton = cancel;
    }

    private void SelectMode(int mode)
    {
        _mode = mode;
        for (var i = 0; i < 5; i++)
        {
            _modeButtons[i].Checked = i == mode;
            _modePanels[i].Visible = i == mode;
        }
        UpdatePreview();
    }

    private void PreselectFromCurrent()
    {
        if (_currentCode is KeycodeCatalog.KC_NO or KeycodeCatalog.KC_TRNS)
        {
            SelectMode(4);
        }
        else if (_currentCode is >= 0x0100 and <= 0x1FFF)
        {
            SelectMode(1);
            var mods = (_currentCode >> 8) & 0x1F;
            _modToggles[0].Checked = (mods & 0x01) != 0;
            _modToggles[1].Checked = (mods & 0x02) != 0;
            _modToggles[2].Checked = (mods & 0x04) != 0;
            _modToggles[3].Checked = (mods & 0x08) != 0;
            _chordKeyPicker.Select((ushort)(_currentCode & 0xFF));
        }
        else if (_currentCode is >= 0x5200 and <= 0x527F)
        {
            SelectMode(3);
            if (_currentCode <= 0x521F) { _layerGo.Checked = true; _layerNumber.Value = _currentCode - 0x5200; }
            else if (_currentCode <= 0x523F) { _layerHold.Checked = true; _layerNumber.Value = _currentCode - 0x5220; }
            else { _layerToggle.Checked = true; _layerNumber.Value = _currentCode - 0x5260; }
        }
        else if (KeycodeCatalog.IsGhostKey(_currentCode, out var fn) &&
                 _settings.FunctionKeyActions.ContainsKey($"F{fn}"))
        {
            SelectMode(2);
            var actionId = _settings.FunctionKeyActions[$"F{fn}"];
            var name = ActionCatalog.All.FirstOrDefault(a => a.Id == actionId)?.DisplayName;
            if (name != null) _actionList.SelectedItem = name;
        }
        else
        {
            SelectMode(0);
            _keyPicker.Select(_currentCode);
        }
    }

    private (ushort Code, string Description, string? ActionId, int GhostFn) ResolveSelection()
    {
        switch (_mode)
        {
            case 0:
                if (_keyPicker.SelectedCode is not ushort key) return (0, "Pick a key above.", null, 0);
                return (key, $"Will write: {MegalodonPad.KeycodeName(key)}", null, 0);
            case 1:
                var mods = (_modToggles[0].Checked ? 1 : 0) | (_modToggles[1].Checked ? 2 : 0) |
                           (_modToggles[2].Checked ? 4 : 0) | (_modToggles[3].Checked ? 8 : 0);
                if (_chordKeyPicker.SelectedCode is not ushort baseKey) return (0, "Pick the chord's base key.", null, 0);
                if (mods == 0) return (0, "Toggle at least one modifier for a chord.", null, 0);
                var chord = KeycodeCatalog.Chord(mods, baseKey);
                var chordName = MegalodonPad.KeycodeName(chord);
                var meaning = KnownChords.TryGet(chordName, out var known, out _) ? $" — ({known})" : "";
                return (chord, $"Will write: {chordName}{meaning}", null, 0);
            case 2:
                if (_actionList.SelectedIndex < 0) return (0, "Pick a Switchboard action.", null, 0);
                var action = ActionCatalog.All.First(a =>
                    a.DisplayName == (string)_actionList.SelectedItem!);
                var ghost = FindFreeGhostKey();
                if (ghost == 0) return (0, "No free ghost keys (F13–F24 all in use).", null, 0);
                KeycodeCatalog.IsGhostKey(ghost, out var fn);
                return (ghost, $"Will write F{fn} to this position and map F{fn} → {action.DisplayName}.", action.Id, fn);
            case 3:
                var layer = (int)_layerNumber.Value;
                var code = _layerGo.Checked ? KeycodeCatalog.GoToLayer(layer)
                    : _layerHold.Checked ? KeycodeCatalog.LayerWhileHeld(layer)
                    : KeycodeCatalog.ToggleLayer(layer);
                return (code, $"Will write: {MegalodonPad.KeycodeName(code)}", null, 0);
            default:
                var clear = _clearTransparent.Checked ? KeycodeCatalog.KC_TRNS : KeycodeCatalog.KC_NO;
                return (clear, $"Will write: {MegalodonPad.KeycodeName(clear)}", null, 0);
        }
    }

    private ushort FindFreeGhostKey()
    {
        var used = new HashSet<ushort>();
        for (var l = 0; l < _snapshot.LayerCount; l++)
        {
            foreach (var code in _snapshot.KeyCodes[l]) used.Add(code);
            foreach (var (ccw, cw) in _snapshot.EncoderCodes[l])
            {
                used.Add(ccw);
                used.Add(cw);
            }
        }
        foreach (var ghost in KeycodeCatalog.GhostKeys)
        {
            KeycodeCatalog.IsGhostKey(ghost, out var fn);
            if (!used.Contains(ghost) && !_settings.FunctionKeyActions.ContainsKey($"F{fn}"))
                return ghost;
        }
        return 0;
    }

    private void UpdatePreview()
    {
        var (_, description, _, _) = ResolveSelection();
        _preview.Text = description;
        _errorBanner.Visible = false;
    }

    /// <summary>The staged result of the dialog, read by the caller after DialogResult.OK.</summary>
    public PendingChange? Result { get; private set; }

    private void ConfirmAssignment()
    {
        var (code, _, actionId, ghostFn) = ResolveSelection();
        if (code == 0 && _mode != 4)
        {
            _errorBanner.Text = _preview.Text;
            _errorBanner.Visible = true;
            return;
        }

        Result = new PendingChange(
            _target, code, _currentCode,
            string.IsNullOrWhiteSpace(_labelBox.Text) ? null : _labelBox.Text.Trim(),
            actionId, ghostFn,
            _releaseMapping is { Visible: true, Checked: true });
        DialogResult = DialogResult.OK;
    }

    /// <summary>Searchable, grouped key chip picker.</summary>
    private sealed class KeyPickerControl : UserControl
    {
        private readonly TextBox _search;
        private readonly FlowLayoutPanel _chips;
        private Button? _selectedChip;

        public ushort? SelectedCode { get; private set; }
        public event Action? SelectionChanged;

        public KeyPickerControl()
        {
            _search = new TextBox { Dock = DockStyle.Top, PlaceholderText = "Search keys…" };
            _chips = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(0, 6, 0, 0),
            };
            _search.TextChanged += (_, _) => Populate(_search.Text);
            Controls.Add(_chips);
            Controls.Add(_search);
            Populate("");
        }

        public void Select(ushort code)
        {
            SelectedCode = code;
            Populate(_search.Text);
        }

        private void Populate(string filter)
        {
            _chips.SuspendLayout();
            _chips.Controls.Clear();
            _selectedChip = null;
            foreach (var group in KeycodeCatalog.Groups)
            {
                var matches = group.Entries
                    .Where(e => filter.Length == 0 ||
                                e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (matches.Length == 0) continue;

                var header = new Label
                {
                    Text = group.Title,
                    AutoSize = true,
                    Font = new Font("Segoe UI Semibold", 8.5f),
                    ForeColor = Color.FromArgb(96, 102, 110),
                    Margin = new Padding(2, 8, 0, 2),
                };
                _chips.SetFlowBreak(header, false);
                _chips.Controls.Add(header);
                _chips.SetFlowBreak(header, true);

                foreach (var entry in matches)
                {
                    var chip = new Button
                    {
                        Text = entry.Name,
                        AutoSize = true,
                        MinimumSize = new Size(52, 28),
                        FlatStyle = FlatStyle.Flat,
                        Margin = new Padding(2),
                        Tag = entry.Code,
                    };
                    chip.FlatAppearance.BorderColor = Color.FromArgb(210, 214, 220);
                    if (SelectedCode == entry.Code) Highlight(chip);
                    chip.Click += (_, _) =>
                    {
                        if (_selectedChip != null)
                        {
                            _selectedChip.BackColor = SystemColors.Control;
                            _selectedChip.ForeColor = SystemColors.ControlText;
                        }
                        SelectedCode = (ushort)chip.Tag!;
                        Highlight(chip);
                        SelectionChanged?.Invoke();
                    };
                    _chips.Controls.Add(chip);
                }
            }
            _chips.ResumeLayout();
        }

        private void Highlight(Button chip)
        {
            _selectedChip = chip;
            chip.BackColor = Color.FromArgb(0, 103, 192);
            chip.ForeColor = Color.White;
        }
    }
}
