using Switchboard.Core;

namespace Switchboard.UI;

/// <summary>One position on the pad that can be written.</summary>
internal sealed record PadTarget(
    int Layer, bool IsEncoder, int Row, int Col, int Encoder, bool Clockwise,
    string DisplayName, string LabelKey);

/// <summary>A staged (not-yet-written) assignment for one pad position.</summary>
internal sealed record PendingChange(
    PadTarget Target, ushort Code, ushort OldCode, string? Label,
    string? ActionId, string? ActionKeySpec, bool ReleaseOldMapping)
{
    /// <summary>What the cell should read while pending.</summary>
    public string DisplayName => MegalodonPad.KeycodeName(Code);
}

/// <summary>
/// The assignment editor: a universal search box on top (chords, actions, keys,
/// layers — everything, by name) plus the underlying Key / Chord / Action / Layer /
/// Clear modes for precise manual control. A search pick auto-selects the right
/// mode, fills its controls, and sets the label — the manual modes are still there
/// for anything not in the index. Staging only — it returns a PendingChange; the
/// page commits pending changes to the pad in a batch.
/// </summary>
internal sealed class AssignmentDialog : Form
{
    private static readonly Color Accent = Theme.Accent;

    private readonly PadTarget _target;
    private readonly ushort _currentCode;
    private readonly AppSettings _settings;
    private readonly MegalodonPad.PadSnapshot _snapshot;
    private readonly IReadOnlyCollection<ushort> _reservedCodes;

    private readonly CheckBox[] _modeButtons = new CheckBox[5];
    private readonly Panel[] _modePanels = new Panel[5];
    private static readonly string[] ModeNames = ["Key", "Chord", "Action", "Layer", "Clear"];

    private KeyPickerControl _keyPicker = null!;
    private KeyPickerControl _chordKeyPicker = null!;
    private readonly CheckBox[] _modToggles = new CheckBox[4];

    // Action mode: a 2-step inline flow (pick action, confirm placement) —
    // not the shared mode-panel machinery the other modes use.
    private Panel _actionStep1 = null!, _actionStep2 = null!;
    private TextBox _actionSearch = null!;
    private FlowLayoutPanel _actionCards = null!;
    private Button _actionNextBtn = null!, _actionBackBtn = null!;
    private MiniPadIndicator _actionMiniPad = null!;
    private Label _actionSummary = null!;
    private TextBox _actionSteps = null!;
    private ActionCatalog.ActionInfo? _actionSelected;

    private RadioButton _layerGo = null!, _layerHold = null!, _layerToggle = null!;
    private NumericUpDown _layerNumber = null!;
    private RadioButton _clearTransparent = null!, _clearNothing = null!;
    private TextBox _labelBox = null!;
    private Label _preview = null!;
    private Label _errorBanner = null!;
    private CheckBox _releaseMapping = null!;
    private Button _writeButton = null!;

    private TextBox _searchBox = null!;
    private ListBox _searchResults = null!;
    private List<SearchResult> _searchIndex = null!;
    private List<SearchResult> _currentMatches = [];

    private int _mode;

    /// <summary>One thing the search box can find — a chord, an action, a key, or a layer switch.</summary>
    private sealed record SearchResult(string Category, string Label, string SearchText, Action Apply);

    public AssignmentDialog(PadTarget target, ushort currentCode, AppSettings settings,
        MegalodonPad.PadSnapshot snapshot, IReadOnlyCollection<ushort> reservedCodes)
    {
        _target = target;
        _currentCode = currentCode;
        _settings = settings;
        _snapshot = snapshot;
        _reservedCodes = reservedCodes;

        Text = target.DisplayName;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(600, 628);
        BackColor = Theme.Panel;
        ForeColor = Theme.Ink;
        Font = new Font("Segoe UI", 9.75f);

        BuildUi();
        PreselectFromCurrent();
        UpdatePreview();
    }

    private void BuildUi()
    {
        // Universal search, always on top. Picking a result drives the mode
        // panels below exactly as if the user had configured them by hand.
        _searchBox = new TextBox
        {
            Location = new Point(20, 12),
            Size = new Size(560, 28),
            PlaceholderText = "Search actions, chords, keys… (e.g. \"Show Desktop\")",
            Font = new Font("Segoe UI", 10f),
            BackColor = Theme.PanelAlt,
            ForeColor = Theme.Ink,
            BorderStyle = BorderStyle.FixedSingle,
        };
        _searchBox.TextChanged += (_, _) => UpdateSearchResults();
        _searchBox.KeyDown += SearchBox_KeyDown;
        Controls.Add(_searchBox);

        // Everything below the search bar lives in one panel so the search
        // overlay can sit on top of it without disturbing any coordinates.
        var body = new Panel { Location = new Point(0, 44), Size = new Size(600, 584), BackColor = Theme.Panel };
        Controls.Add(body);

        var current = new Label
        {
            Text = $"Currently: {MegalodonPad.KeycodeName(_currentCode)}",
            ForeColor = Theme.Subtle,
            BackColor = Theme.Panel,
            Location = new Point(20, 12),
            AutoSize = true,
        };
        body.Controls.Add(current);

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
                BackColor = Theme.PanelAlt,
                ForeColor = Theme.Subtle,
                Font = Theme.BodySemibold,
            };
            button.FlatAppearance.CheckedBackColor = Theme.AccentSoft;
            button.FlatAppearance.BorderColor = Theme.Line;
            button.FlatAppearance.MouseOverBackColor = Theme.Line;
            button.Click += (_, _) => SelectMode(index);
            _modeButtons[i] = button;
            body.Controls.Add(button);
        }

        // Mode panels share one region.
        var panelBounds = new Rectangle(20, 80, 560, 330);
        for (var i = 0; i < 5; i++)
        {
            _modePanels[i] = new Panel { Bounds = panelBounds, Visible = false, BackColor = Theme.Panel };
            body.Controls.Add(_modePanels[i]);
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
                BackColor = Theme.PanelAlt,
                ForeColor = Theme.Ink,
            };
            toggle.FlatAppearance.CheckedBackColor = Theme.AccentSoft;
            toggle.FlatAppearance.BorderColor = Theme.Line;
            toggle.FlatAppearance.MouseOverBackColor = Theme.Line;
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

        // Action — step 1: pick what it does.
        _actionStep1 = new Panel { Bounds = new Rectangle(0, 0, 560, 330), BackColor = Theme.Panel };
        _actionSearch = new TextBox
        {
            Bounds = new Rectangle(0, 0, 560, 26), PlaceholderText = "Search actions…",
            BackColor = Theme.PanelAlt, ForeColor = Theme.Ink, BorderStyle = BorderStyle.FixedSingle,
        };
        _actionSearch.TextChanged += (_, _) => PopulateActionCards(_actionSearch.Text);
        _actionCards = new FlowLayoutPanel
        {
            Bounds = new Rectangle(0, 32, 560, 256),
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Theme.Panel,
        };
        _actionNextBtn = new Button
        {
            Text = "Next ›",
            Size = new Size(100, 30),
            Location = new Point(460, 296),
            BackColor = Accent,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Enabled = false,
        };
        _actionNextBtn.FlatAppearance.BorderSize = 0;
        _actionNextBtn.Click += (_, _) => SelectActionStep(2);
        _actionStep1.Controls.Add(_actionCards);
        _actionStep1.Controls.Add(_actionSearch);
        _actionStep1.Controls.Add(_actionNextBtn);

        // Action — step 2: confirm where it lands, and what it actually does.
        _actionStep2 = new Panel { Bounds = new Rectangle(0, 0, 560, 330), Visible = false, BackColor = Theme.Panel };
        _actionMiniPad = new MiniPadIndicator { Location = new Point(0, 8) };
        _actionSummary = new Label
        {
            Location = new Point(210, 8),
            Size = new Size(340, 80),
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = Theme.Ink,
            BackColor = Theme.Panel,
        };
        var summaryHeader = new Label
        {
            Text = "What this does:",
            Location = new Point(0, 102),
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 8.5f),
            ForeColor = Theme.Subtle,
            BackColor = Theme.Panel,
        };
        _actionSteps = new TextBox
        {
            Location = new Point(0, 122),
            Size = new Size(540, 168),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Panel,
            ForeColor = Color.FromArgb(196, 202, 210),
            Font = new Font("Segoe UI", 9.5f),
            TabStop = false,
        };
        _actionBackBtn = new Button
        {
            Text = "‹ Back", Size = new Size(90, 30), Location = new Point(0, 296),
            FlatStyle = FlatStyle.Flat, BackColor = Theme.PanelAlt, ForeColor = Theme.Ink,
        };
        _actionBackBtn.FlatAppearance.BorderColor = Theme.Line;
        _actionBackBtn.Click += (_, _) => SelectActionStep(1);
        _actionStep2.Controls.Add(_actionMiniPad);
        _actionStep2.Controls.Add(_actionSummary);
        _actionStep2.Controls.Add(summaryHeader);
        _actionStep2.Controls.Add(_actionSteps);
        _actionStep2.Controls.Add(_actionBackBtn);

        _modePanels[2].Controls.Add(_actionStep2);
        _modePanels[2].Controls.Add(_actionStep1);
        PopulateActionCards("");

        // Layer.
        _layerGo = new RadioButton { Text = "Go To (switch and stay)", Location = new Point(0, 4), AutoSize = true, Checked = true, ForeColor = Theme.Ink, BackColor = Theme.Panel };
        _layerHold = new RadioButton { Text = "While Held (like a Shift key)", Location = new Point(0, 32), AutoSize = true, ForeColor = Theme.Ink, BackColor = Theme.Panel };
        _layerToggle = new RadioButton { Text = "Toggle (tap on, tap off)", Location = new Point(0, 60), AutoSize = true, ForeColor = Theme.Ink, BackColor = Theme.Panel };
        _layerNumber = new NumericUpDown
        {
            Minimum = 0,
            Maximum = 3,
            Location = new Point(80, 94),
            Width = 60,
            BackColor = Theme.PanelAlt,
            ForeColor = Theme.Ink,
        };
        var layerLabel = new Label { Text = "Layer:", Location = new Point(0, 96), AutoSize = true, ForeColor = Theme.Ink, BackColor = Theme.Panel };
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
            ForeColor = Theme.Ink,
            BackColor = Theme.Panel,
        };
        _clearNothing = new RadioButton
        {
            Text = "Nothing — the key does nothing",
            Location = new Point(0, 32),
            AutoSize = true,
            Checked = _target.Layer == 0,
            ForeColor = Theme.Ink,
            BackColor = Theme.Panel,
        };
        _clearTransparent.CheckedChanged += (_, _) => UpdatePreview();
        _clearNothing.CheckedChanged += (_, _) => UpdatePreview();
        _modePanels[4].Controls.AddRange([_clearTransparent, _clearNothing]);

        // Label field.
        var labelCaption = new Label { Text = "Label (optional):", Location = new Point(20, 422), AutoSize = true, ForeColor = Theme.Ink, BackColor = Theme.Panel };
        _labelBox = new TextBox
        {
            Location = new Point(140, 419),
            Width = 440,
            Text = _settings.PadLabels.GetValueOrDefault(_target.LabelKey, ""),
            BackColor = Theme.PanelAlt,
            ForeColor = Theme.Ink,
            BorderStyle = BorderStyle.FixedSingle,
        };
        body.Controls.Add(labelCaption);
        body.Controls.Add(_labelBox);

        // Release-mapping question (shown only when relevant).
        _releaseMapping = new CheckBox
        {
            Location = new Point(20, 448),
            AutoSize = true,
            Visible = false,
            Checked = true,
            ForeColor = Theme.Ink,
            BackColor = Theme.Panel,
        };
        body.Controls.Add(_releaseMapping);
        if (KeycodeCatalog.IsGhostKey(_currentCode, out var releaseFn, out var releaseModBits))
        {
            var keySpec = HotkeyService.FormatFunctionKey(releaseFn, releaseModBits);
            if (_settings.FunctionKeyActions.TryGetValue(keySpec, out var mappedAction))
            {
                var actionName = ActionCatalog.All.FirstOrDefault(a => a.Id == mappedAction)?.DisplayName ?? mappedAction;
                _releaseMapping.Text = $"Also release Switchboard mapping {keySpec} → {actionName}";
                _releaseMapping.Visible = true;
            }
        }

        // Preview + error + buttons.
        _preview = new Label
        {
            Location = new Point(20, 478),
            Size = new Size(560, 34),
            ForeColor = Theme.Ink,
            BackColor = Theme.Panel,
            Font = new Font("Segoe UI Semibold", 9.75f),
        };
        _errorBanner = new Label
        {
            Location = new Point(20, 478),
            Size = new Size(560, 34),
            ForeColor = Color.White,
            BackColor = Theme.Danger,
            TextAlign = ContentAlignment.MiddleCenter,
            Visible = false,
        };
        body.Controls.Add(_errorBanner);
        body.Controls.Add(_preview);

        _writeButton = new Button
        {
            Text = "Assign",
            Size = new Size(130, 34),
            Location = new Point(450, 528),
            BackColor = Theme.Accent,
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
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.PanelAlt,
            ForeColor = Theme.Ink,
        };
        cancel.FlatAppearance.BorderColor = Theme.Line;
        body.Controls.Add(_writeButton);
        body.Controls.Add(cancel);
        CancelButton = cancel;

        // Search results overlay: same rectangle as the mode panels, drawn on
        // top of them (added after `body`) only while there's a live query.
        _searchResults = new ListBox
        {
            Bounds = new Rectangle(20, 124, 560, 330),
            Visible = false,
            BorderStyle = BorderStyle.FixedSingle,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 38,
            IntegralHeight = false,
            BackColor = Theme.PanelAlt,
        };
        _searchResults.DrawItem += DrawSearchResultItem;
        _searchResults.Click += (_, _) => ApplySelectedSearchResult();
        Controls.Add(_searchResults);

        _searchIndex = BuildSearchIndex();
    }

    private void SelectMode(int mode)
    {
        _mode = mode;
        for (var i = 0; i < 5; i++)
        {
            _modeButtons[i].Checked = i == mode;
            _modePanels[i].Visible = i == mode;
        }
        if (mode == 2) SelectActionStep(1);
        UpdatePreview();
    }

    private void SelectActionStep(int step)
    {
        _actionStep1.Visible = step == 1;
        _actionStep2.Visible = step == 2;
        if (step == 2 && _actionSelected != null)
        {
            _actionMiniPad.Target = _target;
            _actionMiniPad.Invalidate();
            _actionSummary.Text = $"{_actionSelected.ShortLabel}\n\n" +
                $"Triggered by {_target.DisplayName}.\n" +
                "(Switchboard handles the invisible key behind the scenes.)";
            // WinForms' native edit control needs \r\n to actually break a line — a bare \n
            // just gets swallowed, which is why every step used to run together. The extra
            // blank line between steps is deliberate breathing room, not just a line break.
            _actionSteps.Text = string.IsNullOrWhiteSpace(_actionSelected.Summary)
                ? "No step-by-step summary available for this action."
                : _actionSelected.Summary.Replace("\n", "\r\n\r\n");
        }
        UpdatePreview();
    }

    private void PickAction(ActionCatalog.ActionInfo action)
    {
        _actionSelected = action;
        _actionNextBtn.Enabled = true;
        if (string.IsNullOrWhiteSpace(_labelBox.Text)) _labelBox.Text = action.ShortLabel;
        PopulateActionCards(_actionSearch.Text);
    }

    private void OpenActionBuilder()
    {
        using var builder = new ActionBuilderForm();
        if (builder.ShowDialog(this) != DialogResult.OK) return;
        // ActionCatalog.All reads the store fresh, so the new action is already
        // there — just refresh the list (and re-run search index too, since it
        // was built once at dialog-open time and won't see it otherwise).
        _searchIndex = BuildSearchIndex();
        PopulateActionCards(_actionSearch.Text);
    }

    private void PopulateActionCards(string filter)
    {
        _actionCards.SuspendLayout();
        _actionCards.Controls.Clear();
        _actionCards.Controls.Add(BuildActionCard("+ Create New Action…",
            "Build a custom scripted action", false, OpenActionBuilder, dashed: true));
        foreach (var action in ActionCatalog.All)
        {
            if (action.Id == ActionCatalog.None) continue;
            if (filter.Length > 0 &&
                !action.ShortLabel.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !action.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            var act = action;
            _actionCards.Controls.Add(BuildActionCard(act.ShortLabel, act.DisplayName,
                _actionSelected?.Id == act.Id, () => PickAction(act)));
        }
        _actionCards.ResumeLayout();
    }

    private Control BuildActionCard(string title, string subtitle, bool selected, Action onClick, bool dashed = false)
    {
        var card = new Panel
        {
            Size = new Size(540, 46),
            Margin = new Padding(0, 0, 0, 6),
            BackColor = selected ? Theme.AccentSoft : Theme.PanelAlt,
            Cursor = Cursors.Hand,
        };
        card.Paint += (_, e) =>
        {
            using var pen = new Pen(selected ? Accent : dashed ? Color.FromArgb(80, 84, 92) : Theme.Line, 1.4f)
            {
                DashStyle = dashed ? System.Drawing.Drawing2D.DashStyle.Dash : System.Drawing.Drawing2D.DashStyle.Solid,
            };
            e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
        };
        var titleLabel = new Label
        {
            Text = title,
            Location = new Point(12, 7),
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 10f),
            ForeColor = selected ? Accent : dashed ? Theme.Subtle : Theme.Ink,
            BackColor = selected ? Theme.AccentSoft : Theme.PanelAlt,
        };
        var subLabel = new Label
        {
            Text = subtitle,
            Location = new Point(12, 26),
            AutoSize = true,
            Font = new Font("Segoe UI", 8.25f),
            ForeColor = Theme.Subtle,
            BackColor = selected ? Theme.AccentSoft : Theme.PanelAlt,
        };
        card.Controls.Add(titleLabel);
        card.Controls.Add(subLabel);
        card.Click += (_, _) => onClick();
        titleLabel.Click += (_, _) => onClick();
        subLabel.Click += (_, _) => onClick();
        return card;
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
        else if (KeycodeCatalog.IsGhostKey(_currentCode, out var fn, out var modBits) &&
                 _settings.FunctionKeyActions.TryGetValue(HotkeyService.FormatFunctionKey(fn, modBits), out var actionId))
        {
            SelectMode(2);
            var preselected = ActionCatalog.All.FirstOrDefault(a => a.Id == actionId);
            if (preselected != null)
            {
                _actionSelected = preselected;
                _actionNextBtn.Enabled = true;
                PopulateActionCards("");
                SelectActionStep(2);
            }
        }
        else
        {
            SelectMode(0);
            _keyPicker.Select(_currentCode);
        }
    }

    private (ushort Code, string Description, string? ActionId, string? ActionKeySpec) ResolveSelection()
    {
        switch (_mode)
        {
            case 0:
                if (_keyPicker.SelectedCode is not ushort key) return (0, "Pick a key above.", null, null);
                return (key, $"Will write: {MegalodonPad.KeycodeName(key)}", null, null);
            case 1:
                var mods = (_modToggles[0].Checked ? 1 : 0) | (_modToggles[1].Checked ? 2 : 0) |
                           (_modToggles[2].Checked ? 4 : 0) | (_modToggles[3].Checked ? 8 : 0);
                if (_chordKeyPicker.SelectedCode is not ushort baseKey) return (0, "Pick the chord's base key.", null, null);
                if (mods == 0) return (0, "Toggle at least one modifier for a chord.", null, null);
                var chord = KeycodeCatalog.Chord(mods, baseKey);
                var chordName = MegalodonPad.KeycodeName(chord);
                var meaning = KnownChords.TryGet(chordName, out var known, out _) ? $" — ({known})" : "";
                return (chord, $"Will write: {chordName}{meaning}", null, null);
            case 2:
                if (_actionSelected == null) return (0, "Pick an action above.", null, null);
                var allocated = ActionKeyAllocator.FindFree(_snapshot, _settings, _reservedCodes);
                if (allocated == null) return (0, "No free action slots left (all F13–F24 combinations in use).", null, null);
                var (actionCode, actionKeySpec) = allocated.Value;
                return (actionCode, $"Will assign \"{_actionSelected.ShortLabel}\" to {_target.DisplayName}.",
                    _actionSelected.Id, actionKeySpec);
            case 3:
                var layer = (int)_layerNumber.Value;
                var code = _layerGo.Checked ? KeycodeCatalog.GoToLayer(layer)
                    : _layerHold.Checked ? KeycodeCatalog.LayerWhileHeld(layer)
                    : KeycodeCatalog.ToggleLayer(layer);
                return (code, $"Will write: {MegalodonPad.KeycodeName(code)}", null, null);
            default:
                var clear = _clearTransparent.Checked ? KeycodeCatalog.KC_TRNS : KeycodeCatalog.KC_NO;
                return (clear, $"Will write: {MegalodonPad.KeycodeName(clear)}", null, null);
        }
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
        var (code, _, actionId, actionKeySpec) = ResolveSelection();
        if (code == 0 && _mode != 4)
        {
            _errorBanner.Text = _preview.Text;
            _errorBanner.Visible = true;
            return;
        }

        Result = new PendingChange(
            _target, code, _currentCode,
            string.IsNullOrWhiteSpace(_labelBox.Text) ? null : _labelBox.Text.Trim(),
            actionId, actionKeySpec,
            _releaseMapping is { Visible: true, Checked: true });
        DialogResult = DialogResult.OK;
    }

    // ---- Universal search ----

    /// <summary>
    /// Flattens every known chord, Switchboard action, plain key, and layer
    /// switch into one searchable list. Picking a result drives the same mode
    /// panels a manual pick would — search is a fast path, not a separate model.
    /// </summary>
    private List<SearchResult> BuildSearchIndex()
    {
        var results = new List<SearchResult>();

        foreach (var (chordText, label, _) in KnownChords.AllEntries())
        {
            if (!TryParseChordText(chordText, out var mods, out var baseCode)) continue;
            results.Add(new SearchResult("Chord", label, $"{label} {chordText}", () =>
            {
                SelectMode(1);
                _modToggles[0].Checked = (mods & 1) != 0;
                _modToggles[1].Checked = (mods & 2) != 0;
                _modToggles[2].Checked = (mods & 4) != 0;
                _modToggles[3].Checked = (mods & 8) != 0;
                _chordKeyPicker.Select(baseCode);
                _labelBox.Text = label;
            }));
        }

        foreach (var action in ActionCatalog.All)
        {
            if (action.Id == ActionCatalog.None) continue;
            var act = action;
            results.Add(new SearchResult("Action", act.ShortLabel, $"{act.ShortLabel} {act.DisplayName}", () =>
            {
                SelectMode(2);
                PickAction(act);
                SelectActionStep(2);
            }));
        }

        foreach (var group in KeycodeCatalog.Groups)
        {
            foreach (var entry in group.Entries)
            {
                var code = entry.Code;
                var name = entry.Name;
                results.Add(new SearchResult(group.Title, name, name, () =>
                {
                    SelectMode(0);
                    _keyPicker.Select(code);
                    _labelBox.Text = name;
                }));
            }
        }

        for (var l = 0; l < _snapshot.LayerCount; l++)
        {
            var layer = l;
            results.Add(new SearchResult("Layer", $"Go to Layer {layer}", $"Go to Layer {layer} switch", () =>
            {
                SelectMode(3);
                _layerGo.Checked = true;
                _layerNumber.Value = layer;
                _labelBox.Text = $"Go to Layer {layer}";
            }));
            results.Add(new SearchResult("Layer", $"Layer {layer} While Held", $"Layer {layer} hold momentary", () =>
            {
                SelectMode(3);
                _layerHold.Checked = true;
                _layerNumber.Value = layer;
                _labelBox.Text = $"Layer {layer} While Held";
            }));
            results.Add(new SearchResult("Layer", $"Toggle Layer {layer}", $"Toggle Layer {layer}", () =>
            {
                SelectMode(3);
                _layerToggle.Checked = true;
                _layerNumber.Value = layer;
                _labelBox.Text = $"Toggle Layer {layer}";
            }));
        }

        results.Add(new SearchResult("Clear", "Clear (Nothing)", "Clear Nothing blank empty", () =>
        {
            SelectMode(4);
            _clearNothing.Checked = true;
            _labelBox.Text = "";
        }));
        if (_target.Layer > 0)
        {
            results.Add(new SearchResult("Clear", "Transparent (fall through)", "Transparent fall through clear", () =>
            {
                SelectMode(4);
                _clearTransparent.Checked = true;
                _labelBox.Text = "";
            }));
        }

        return results;
    }

    /// <summary>Parses a chord's decoded text ("Ctrl+Win+D") back into mod bits + base key code.</summary>
    private static bool TryParseChordText(string chordText, out int mods, out ushort baseCode)
    {
        mods = 0;
        baseCode = 0;
        var parts = chordText.Split('+');
        if (parts.Length < 2) return false;
        var baseByte = MegalodonPad.BasicCodeFromName(parts[^1]);
        if (baseByte is not byte b) return false;
        baseCode = b;
        foreach (var mod in parts[..^1])
        {
            mods |= mod switch { "Ctrl" => 1, "Shift" => 2, "Alt" => 4, "Win" => 8, _ => 0 };
        }
        return mods != 0;
    }

    private void UpdateSearchResults()
    {
        var q = _searchBox.Text.Trim();
        if (q.Length == 0)
        {
            _searchResults.Visible = false;
            _currentMatches = [];
            return;
        }
        _currentMatches = _searchIndex
            .Where(r => r.SearchText.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Label.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(r => r.Label.Length)
            .Take(30)
            .ToList();
        _searchResults.Items.Clear();
        if (_currentMatches.Count > 0)
        {
            _searchResults.Items.AddRange(_currentMatches.Select(m => (object)m).ToArray());
            _searchResults.SelectedIndex = 0;
        }
        _searchResults.Visible = _currentMatches.Count > 0;
        if (_searchResults.Visible) _searchResults.BringToFront();
    }

    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (!_searchResults.Visible) return;
        switch (e.KeyCode)
        {
            case Keys.Down:
                _searchResults.SelectedIndex = Math.Min(_searchResults.SelectedIndex + 1, _currentMatches.Count - 1);
                e.Handled = true;
                e.SuppressKeyPress = true;
                break;
            case Keys.Up:
                _searchResults.SelectedIndex = Math.Max(_searchResults.SelectedIndex - 1, 0);
                e.Handled = true;
                e.SuppressKeyPress = true;
                break;
            case Keys.Enter:
                ApplySelectedSearchResult();
                e.Handled = true;
                e.SuppressKeyPress = true;
                break;
            case Keys.Escape:
                _searchBox.Clear();
                _searchResults.Visible = false;
                e.Handled = true;
                e.SuppressKeyPress = true;
                break;
        }
    }

    private void ApplySelectedSearchResult()
    {
        var idx = _searchResults.SelectedIndex;
        if (idx < 0 || idx >= _currentMatches.Count) return;
        _currentMatches[idx].Apply();
        _searchBox.Clear();
        _searchResults.Visible = false;
        UpdatePreview();
    }

    private void DrawSearchResultItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _currentMatches.Count) return;
        var r = _currentMatches[e.Index];
        var selected = (e.State & DrawItemState.Selected) != 0;
        using var back = new SolidBrush(selected ? Theme.AccentSoft : Theme.PanelAlt);
        e.Graphics.FillRectangle(back, e.Bounds);
        TextRenderer.DrawText(e.Graphics, r.Label, new Font("Segoe UI Semibold", 9.5f),
            new Rectangle(e.Bounds.X + 10, e.Bounds.Y + 4, e.Bounds.Width - 20, 18),
            Theme.Ink, TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(e.Graphics, r.Category, new Font("Segoe UI", 8f),
            new Rectangle(e.Bounds.X + 10, e.Bounds.Y + 22, e.Bounds.Width - 20, 16),
            Theme.Subtle, TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        e.DrawFocusRectangle();
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
            BackColor = Theme.Panel;
            _search = new TextBox
            {
                Dock = DockStyle.Top, PlaceholderText = "Search keys…",
                BackColor = Theme.PanelAlt, ForeColor = Theme.Ink, BorderStyle = BorderStyle.FixedSingle,
            };
            _chips = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(0, 6, 0, 0),
                BackColor = Theme.Panel,
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
                    ForeColor = Theme.Subtle,
                    BackColor = Theme.Panel,
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
                        BackColor = Theme.PanelAlt,
                        ForeColor = Theme.Ink,
                    };
                    chip.FlatAppearance.BorderColor = Theme.Line;
                    if (SelectedCode == entry.Code) Highlight(chip);
                    chip.Click += (_, _) =>
                    {
                        if (_selectedChip != null)
                        {
                            _selectedChip.BackColor = Theme.PanelAlt;
                            _selectedChip.ForeColor = Theme.Ink;
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
            chip.BackColor = Theme.Accent;
            chip.ForeColor = Color.White;
        }
    }

    /// <summary>Compact, read-only pad graphic — highlights one physical position
    /// (a grid key, a knob press, or a knob turn) so the Action wizard can show
    /// exactly where it's landing without any pixel-guessing.</summary>
    private sealed class MiniPadIndicator : Control
    {
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public PadTarget? Target { get; set; }

        private static readonly Color CellFill = Theme.PanelAlt;
        private static readonly Color CellBorder = Theme.Line;
        private static readonly Color HitFill = Theme.Accent;

        public MiniPadIndicator()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Size = new Size(190, 90);
            BackColor = Theme.Panel;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            const int cell = 18, gap = 4;

            for (var r = 0; r < 4; r++)
            for (var c = 0; c < 4; c++)
            {
                var hit = Target is { IsEncoder: false } t && t.Row == r && t.Col == c;
                DrawCell(g, c * (cell + gap), r * (cell + gap), cell, cell, hit);
            }

            var knobX = 4 * (cell + gap) + 16;
            for (var k = 0; k < 2; k++)
            {
                var hitPress = Target is { IsEncoder: false } tp && tp.Row == k && tp.Col == 4;
                var hitTurn = Target is { IsEncoder: true } tt && tt.Encoder == k;
                DrawKnob(g, knobX + k * 24, 10, 9, hitPress || hitTurn);
            }
            var hitBig = (Target is { IsEncoder: false } tb && tb.Row == 2 && tb.Col == 4) ||
                         (Target is { IsEncoder: true } tt2 && tt2.Encoder == 2);
            DrawKnob(g, knobX + 12, 44, 12, hitBig);
        }

        private static void DrawCell(Graphics g, int x, int y, int w, int h, bool hit)
        {
            using var path = Rounded(new Rectangle(x, y, w, h), 4);
            using var fill = new SolidBrush(hit ? HitFill : CellFill);
            g.FillPath(fill, path);
            if (!hit)
            {
                using var pen = new Pen(CellBorder);
                g.DrawPath(pen, path);
            }
        }

        private static void DrawKnob(Graphics g, int cx, int cy, int r, bool hit)
        {
            using var fill = new SolidBrush(hit ? HitFill : Color.FromArgb(58, 60, 66));
            g.FillEllipse(fill, cx - r, cy - r, r * 2, r * 2);
        }

        private static System.Drawing.Drawing2D.GraphicsPath Rounded(Rectangle rect, int radius)
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
}
