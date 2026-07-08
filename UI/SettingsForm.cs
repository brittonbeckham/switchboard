using Switchboard.Core;
using Switchboard.Util;
using Microsoft.Win32;

namespace Switchboard.UI;

internal sealed class SettingsForm : Form
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Switchboard";

    private readonly AppSettings _settings;
    private readonly TrayContext _tray;
    private readonly ListBox _nav;
    private readonly Panel _pageHost;
    private readonly Dictionary<string, Panel> _pages = [];

    private CheckBox _hotkeysCheck = null!;
    private CheckBox _calculatorCheck = null!;
    private CheckBox _startupCheck = null!;
    private CheckBox _focusModeCheck = null!;
    private CheckBox _blurCheck = null!;
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

        Text = "Switchboard Settings";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(640, 440);
        Font = new Font("Segoe UI", 9f);

        _nav = new ListBox
        {
            Dock = DockStyle.Left,
            Width = 160,
            BorderStyle = BorderStyle.None,
            IntegralHeight = false,
            Font = new Font("Segoe UI", 10f),
        };
        _pageHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16) };
        Controls.Add(_pageHost);
        Controls.Add(_nav);

        AddPage("Keyboard shortcuts", BuildShortcutsPage());
        AddPage("Focus mode", BuildFocusModePage());
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
        };
    }

    private void AddPage(string title, Panel page)
    {
        page.Dock = DockStyle.Fill;
        page.Visible = false;
        _pages[title] = page;
        _pageHost.Controls.Add(page);
        _nav.Items.Add(title);
    }

    private void ShowPage(string title)
    {
        foreach (var (name, page) in _pages) page.Visible = name == title;
    }

    private Panel BuildShortcutsPage()
    {
        var page = new Panel();
        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
        };
        page.Controls.Add(stack);

        _hotkeysCheck = new CheckBox
        {
            Text = "Ctrl+Win+Numpad 1-9 jumps to that virtual desktop (NumLock on)",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 10),
        };
        _hotkeysCheck.CheckedChanged += (_, _) => OnShortcutSettingChanged();

        _calculatorCheck = new CheckBox
        {
            Text = "Calculator key launches or focuses Calculator",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 10),
        };
        _calculatorCheck.CheckedChanged += (_, _) => OnShortcutSettingChanged();

        _startupCheck = new CheckBox
        {
            Text = "Start Switchboard when Windows starts",
            AutoSize = true,
            Margin = new Padding(0, 14, 0, 0),
        };
        _startupCheck.CheckedChanged += (_, _) => OnStartupChanged();

        stack.Controls.Add(_hotkeysCheck);
        stack.Controls.Add(_calculatorCheck);
        stack.Controls.Add(_startupCheck);
        return page;
    }

    private Panel BuildFocusModePage()
    {
        var page = new Panel();
        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
        };
        page.Controls.Add(stack);

        _focusModeCheck = new CheckBox
        {
            Text = "Dim everything except the active window",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 12),
        };
        _focusModeCheck.CheckedChanged += (_, _) => OnFocusModeChanged();

        _dimLabel = new Label { AutoSize = true, Margin = new Padding(0, 0, 0, 2) };
        _dimTrack = new TrackBar
        {
            Minimum = 5,
            Maximum = 90,
            TickFrequency = 5,
            SmallChange = 5,
            LargeChange = 10,
            Width = 320,
        };
        _dimTrack.ValueChanged += (_, _) => OnFocusModeChanged();

        _blurCheck = new CheckBox
        {
            Text = "Blur background windows (acrylic) instead of only dimming",
            AutoSize = true,
            Margin = new Padding(0, 12, 0, 0),
        };
        _blurCheck.CheckedChanged += (_, _) => OnFocusModeChanged();

        stack.Controls.Add(_focusModeCheck);
        stack.Controls.Add(_dimLabel);
        stack.Controls.Add(_dimTrack);
        stack.Controls.Add(_blurCheck);
        return page;
    }

    private Panel BuildDiagnosticsPage()
    {
        var page = new Panel();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.Controls.Add(layout);

        _statusLabel = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(430, 0),
            ForeColor = Color.DimGray,
            Margin = new Padding(0, 0, 0, 8),
        };
        _detectorButton = new Button { Text = "Start key detector", AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
        _detectorButton.Click += (_, _) => _tray.ToggleDetector();
        _logBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BackColor = Color.White,
        };

        layout.Controls.Add(_statusLabel, 0, 0);
        layout.Controls.Add(_detectorButton, 0, 1);
        layout.Controls.Add(_logBox, 0, 2);
        return page;
    }

    private void LoadState()
    {
        _hotkeysCheck.Checked = _settings.NumpadHotkeysEnabled;
        _calculatorCheck.Checked = _settings.CalculatorFocusFixEnabled;
        _focusModeCheck.Checked = _settings.FocusModeEnabled;
        _blurCheck.Checked = _settings.FocusModeBlurEnabled;
        _dimTrack.Value = Math.Clamp(_settings.FocusModeDimPercent, _dimTrack.Minimum, _dimTrack.Maximum);
        _dimLabel.Text = $"Dim strength: {_dimTrack.Value}%";
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        _startupCheck.Checked = key?.GetValue(RunValue) != null;
        _detectorButton.Text = _tray.DetectorRunning ? "Stop key detector" : "Start key detector";
        _statusLabel.Text = _tray.CurrentStatus;
        _logBox.Text = Log.Snapshot();
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
    }

    private void OnShortcutSettingChanged()
    {
        if (_loading) return;
        _settings.NumpadHotkeysEnabled = _hotkeysCheck.Checked;
        _settings.CalculatorFocusFixEnabled = _calculatorCheck.Checked;
        _settings.Save();
        _tray.ApplyHotkeySetting(); // checkbox events arrive on the UI thread, as RegisterHotKey requires
    }

    private void OnFocusModeChanged()
    {
        if (_loading) return;
        _dimLabel.Text = $"Dim strength: {_dimTrack.Value}%";
        _settings.FocusModeEnabled = _focusModeCheck.Checked;
        _settings.FocusModeDimPercent = _dimTrack.Value;
        _settings.FocusModeBlurEnabled = _blurCheck.Checked;
        _settings.Save();
        _tray.ApplyFocusModeSetting();
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
