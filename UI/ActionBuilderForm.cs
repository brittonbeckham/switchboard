using Switchboard.Core.CustomActions;

namespace Switchboard.UI;

/// <summary>
/// Builds a custom action visually: name it, add one or more blocks (one per
/// target app/window), and chain steps within each block — no JSON typed by
/// hand. Saves to CustomActionStore; the Action wizard picks it up immediately
/// since its list is read fresh from disk every time.
/// </summary>
internal sealed class ActionBuilderForm : Form
{
    private TextBox _displayNameBox = null!;
    private TextBox _shortLabelBox = null!;
    private FlowLayoutPanel _blocksPanel = null!;
    private TextBox _previewBox = null!;
    private readonly List<BlockEditor> _blocks = [];

    public ActionBuilderForm()
    {
        Text = "Create New Action";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(680, 660);
        BackColor = Theme.Panel;
        ForeColor = Theme.Ink;
        Font = new Font("Segoe UI", 9.75f);

        BuildUi();
        AddBlock();
        UpdatePreview();
    }

    private static Label MakeCaption(string text, Point loc) => new()
    {
        Text = text, Location = loc, AutoSize = true, ForeColor = Theme.Ink, BackColor = Theme.Panel,
    };

    private static TextBox MakeTextBox(Point loc, int width) => new()
    {
        Location = loc, Width = width, BackColor = Theme.PanelAlt, ForeColor = Theme.Ink, BorderStyle = BorderStyle.FixedSingle,
    };

    private void BuildUi()
    {
        var nameCaption = MakeCaption("Display name (the full description):", new Point(20, 16));
        _displayNameBox = MakeTextBox(new Point(20, 36), 640);
        _displayNameBox.TextChanged += (_, _) => UpdatePreview();

        var shortCaption = MakeCaption("Short label (shown on the pad cell):", new Point(20, 66));
        _shortLabelBox = MakeTextBox(new Point(20, 86), 300);

        var blocksCaption = MakeCaption("Steps, grouped into blocks — one block per target app or window:", new Point(20, 122));

        _blocksPanel = new FlowLayoutPanel
        {
            Location = new Point(20, 144),
            Size = new Size(640, 300),
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.Panel,
        };

        var addBlockBtn = new Button
        {
            Text = "+ Add Block", Location = new Point(20, 452), Size = new Size(110, 28),
            FlatStyle = FlatStyle.Flat, BackColor = Theme.PanelAlt, ForeColor = Theme.Ink,
        };
        addBlockBtn.FlatAppearance.BorderColor = Theme.Line;
        addBlockBtn.Click += (_, _) => AddBlock();

        var previewCaption = new Label
        {
            Text = "What this does:",
            Location = new Point(20, 490),
            AutoSize = true,
            Font = Theme.CaptionSemibold,
            ForeColor = Theme.Subtle,
            BackColor = Theme.Panel,
        };
        _previewBox = new TextBox
        {
            Location = new Point(20, 510),
            Size = new Size(640, 100),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Panel,
            ForeColor = Color.FromArgb(196, 202, 210),
            TabStop = false,
        };

        var save = new Button
        {
            Text = "Save Action",
            Size = new Size(120, 32),
            Location = new Point(540, 620),
            BackColor = Theme.Accent,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
        };
        save.FlatAppearance.BorderSize = 0;
        save.Click += (_, _) => SaveAndClose();
        var cancel = new Button
        {
            Text = "Cancel", Size = new Size(90, 32), Location = new Point(440, 620), DialogResult = DialogResult.Cancel,
            FlatStyle = FlatStyle.Flat, BackColor = Theme.PanelAlt, ForeColor = Theme.Ink,
        };
        cancel.FlatAppearance.BorderColor = Theme.Line;

        Controls.AddRange([nameCaption, _displayNameBox, shortCaption, _shortLabelBox, blocksCaption,
            _blocksPanel, addBlockBtn, previewCaption, _previewBox, save, cancel]);
        CancelButton = cancel;
    }

    private void AddBlock()
    {
        BlockEditor editor = null!;
        editor = new BlockEditor(onChanged: UpdatePreview, onRemove: () =>
        {
            _blocksPanel.Controls.Remove(editor);
            _blocks.Remove(editor);
            UpdatePreview();
        });
        _blocks.Add(editor);
        _blocksPanel.Controls.Add(editor);
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var blocks = _blocks.Select(b => b.ToBlock()).Where(b => b.Steps.Count > 0).ToList();
        if (blocks.Count == 0)
        {
            _previewBox.Text = "Add some steps below to see a preview.";
            return;
        }
        var lines = new List<string>();
        for (var i = 0; i < blocks.Count; i++)
        {
            lines.Add($"{i + 1}. {blocks[i].Name}:");
            lines.AddRange(blocks[i].Steps.Select(s => "   - " + ActionStepRunner.DescribeStep(s.Kind, s.Value)));
        }
        // WinForms' native edit control needs \r\n to actually break a line.
        _previewBox.Text = string.Join("\r\n", lines);
    }

    private void SaveAndClose()
    {
        var displayName = _displayNameBox.Text.Trim();
        var shortLabel = _shortLabelBox.Text.Trim();
        if (displayName.Length == 0 || shortLabel.Length == 0)
        {
            MessageBox.Show(this, "Give the action a display name and a short label first.",
                "Create New Action", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var blocks = _blocks.Select(b => b.ToBlock()).Where(b => b.Steps.Count > 0).ToList();
        if (blocks.Count == 0)
        {
            MessageBox.Show(this, "Add at least one step to at least one block first.",
                "Create New Action", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var id = $"custom-{Guid.NewGuid():N}";
        CustomActionStore.Add(new StoredAction(id, displayName, shortLabel, blocks));
        DialogResult = DialogResult.OK;
    }

    /// <summary>One block's editor: a name field, an ordered step list, and controls
    /// to append/remove steps. Steps are edited as (kind, value) pairs — the kind
    /// dropdown drives the value box's placeholder and whether it's even needed.</summary>
    private sealed class BlockEditor : Panel
    {
        private static readonly string[] StepKinds =
            ["focus-window", "sleep", "send-keys", "clear-field", "hold-key", "release-key",
             "run", "window", "run-action"];

        private readonly TextBox _nameBox;
        private readonly ListBox _stepsList;
        private readonly ComboBox _kindCombo;
        private readonly TextBox _valueBox;

        public BlockEditor(Action onChanged, Action onRemove)
        {
            Size = new Size(600, 224);
            BorderStyle = BorderStyle.FixedSingle;
            Margin = new Padding(4, 4, 4, 10);
            BackColor = Theme.PanelAlt;

            var nameCaption = new Label
            {
                Text = "Block name (e.g. \"Microsoft Teams\"):", Location = new Point(8, 8), AutoSize = true,
                ForeColor = Theme.Ink, BackColor = Theme.PanelAlt,
            };
            _nameBox = new TextBox
            {
                Location = new Point(8, 28), Width = 400,
                BackColor = Theme.Panel, ForeColor = Theme.Ink, BorderStyle = BorderStyle.FixedSingle,
            };
            _nameBox.TextChanged += (_, _) => onChanged();

            var removeBlock = new Button
            {
                Text = "✕ Remove Block", Location = new Point(462, 26), Size = new Size(122, 26),
                FlatStyle = FlatStyle.Flat, BackColor = Theme.Panel, ForeColor = Theme.Ink,
            };
            removeBlock.FlatAppearance.BorderColor = Theme.Line;
            removeBlock.Click += (_, _) => onRemove();

            _stepsList = new ListBox
            {
                Location = new Point(8, 58), Size = new Size(576, 90),
                BackColor = Theme.Panel, ForeColor = Theme.Ink, BorderStyle = BorderStyle.FixedSingle,
            };

            _kindCombo = new ComboBox
            {
                Location = new Point(8, 156), Width = 140, DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Theme.Panel, ForeColor = Theme.Ink, FlatStyle = FlatStyle.Flat,
            };
            _kindCombo.Items.AddRange(StepKinds);
            _kindCombo.SelectedIndex = 0;
            _kindCombo.SelectedIndexChanged += (_, _) => UpdateValueBoxState();

            _valueBox = new TextBox
            {
                Location = new Point(156, 156), Width = 290,
                BackColor = Theme.Panel, ForeColor = Theme.Ink, BorderStyle = BorderStyle.FixedSingle,
            };

            var addStep = new Button
            {
                Text = "+ Add Step", Location = new Point(454, 155), Size = new Size(100, 26),
                FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Color.White,
            };
            addStep.FlatAppearance.BorderSize = 0;
            addStep.Click += (_, _) =>
            {
                var kind = (string)_kindCombo.SelectedItem!;
                var value = _valueBox.Enabled ? _valueBox.Text.Trim() : "";
                _stepsList.Items.Add(new StepListItem(new StoredStep(kind, value)));
                _valueBox.Clear();
                onChanged();
            };

            var removeStep = new Button
            {
                Text = "✕ Remove Selected Step", Location = new Point(8, 188), Size = new Size(160, 26),
                FlatStyle = FlatStyle.Flat, BackColor = Theme.Panel, ForeColor = Theme.Ink,
            };
            removeStep.FlatAppearance.BorderColor = Theme.Line;
            removeStep.Click += (_, _) =>
            {
                if (_stepsList.SelectedIndex < 0) return;
                _stepsList.Items.RemoveAt(_stepsList.SelectedIndex);
                onChanged();
            };

            Controls.AddRange([nameCaption, _nameBox, removeBlock, _stepsList, _kindCombo, _valueBox, addStep, removeStep]);
            UpdateValueBoxState();
        }

        private void UpdateValueBoxState()
        {
            var kind = (string)_kindCombo.SelectedItem!;
            _valueBox.PlaceholderText = kind switch
            {
                "focus-window" => "Process name, e.g. ms-teams — optionally |launch-command to launch it if not running",
                "sleep" => "Milliseconds, e.g. 160",
                "send-keys" => "Key/chord (Ctrl+E, Win+Left, Enter, Esc) or literal text (/away)",
                "hold-key" => "Ctrl, Shift, Alt, or Win — optionally Name:timeoutMs, e.g. Alt:5000",
                "release-key" => "Ctrl, Shift, Alt, or Win",
                "run" => "Any command line, e.g. wt.exe -d \"D:\\code\\project\" or a URL",
                "window" => "pin, unpin, toggle-topmost, maximize, minimize, restore, close, opacity:60, monitor:next",
                "run-action" => "Another action's id, e.g. mic or step-away",
                _ => "",
            };
            _valueBox.Enabled = kind != "clear-field";
            if (!_valueBox.Enabled) _valueBox.Clear();
        }

        public StoredBlock ToBlock() => new(
            string.IsNullOrWhiteSpace(_nameBox.Text) ? "Block" : _nameBox.Text.Trim(),
            _stepsList.Items.Cast<StepListItem>().Select(i => i.Step).ToList());

        /// <summary>Wraps a StoredStep with a readable ToString() for the ListBox —
        /// terser than the full wizard-style description, since it's a compact row.</summary>
        private sealed class StepListItem(StoredStep step)
        {
            public StoredStep Step => step;

            public override string ToString() => step.Kind switch
            {
                "focus-window" => $"Focus window: {step.Value}",
                "sleep" => $"Sleep {step.Value}ms",
                "send-keys" => $"Send keys: {step.Value}",
                "clear-field" => "Clear field",
                "hold-key" => $"Hold key: {step.Value}",
                "release-key" => $"Release key: {step.Value}",
                "run" => $"Run: {step.Value}",
                "window" => $"Window: {step.Value}",
                "run-action" => $"Run action: {step.Value}",
                _ => step.Kind,
            };
        }
    }
}
