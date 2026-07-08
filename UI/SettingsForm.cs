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
    private readonly Label _statusLabel;
    private readonly ComboBox[] _keyCombos = new ComboBox[3];
    private readonly CheckBox _startupCheck;
    private readonly TextBox _logBox;
    private readonly Button _detectorButton;
    private readonly Button _rescanButton;
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
        ClientSize = new Size(460, 480);
        Font = new Font("Segoe UI", 9f);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 2,
            RowCount = 7,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(layout);

        _statusLabel = new Label { AutoSize = true, MaximumSize = new Size(420, 0), ForeColor = Color.DimGray };
        layout.Controls.Add(_statusLabel, 0, 0);
        layout.SetColumnSpan(_statusLabel, 2);

        for (var i = 0; i < 3; i++)
        {
            var label = new Label { Text = $"Easy-Switch key {i + 1}:", AutoSize = true, Anchor = AnchorStyles.Left };
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
            combo.Items.Add("Do nothing");
            for (var d = 1; d <= 9; d++) combo.Items.Add($"Switch to desktop {d}");
            var keyIndex = i;
            combo.SelectedIndexChanged += (_, _) => OnMappingChanged(keyIndex, combo.SelectedIndex);
            _keyCombos[i] = combo;
            layout.Controls.Add(label, 0, i + 1);
            layout.Controls.Add(combo, 1, i + 1);
        }

        _startupCheck = new CheckBox { Text = "Start Switchboard when Windows starts", AutoSize = true };
        _startupCheck.CheckedChanged += (_, _) => OnStartupChanged();
        layout.Controls.Add(_startupCheck, 0, 4);
        layout.SetColumnSpan(_startupCheck, 2);

        var buttonRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        _rescanButton = new Button { Text = "Rescan devices", AutoSize = true };
        _rescanButton.Click += (_, _) => _tray.Rescan();
        _detectorButton = new Button { Text = "Start key detector", AutoSize = true };
        _detectorButton.Click += (_, _) => _tray.ToggleDetector();
        buttonRow.Controls.Add(_rescanButton);
        buttonRow.Controls.Add(_detectorButton);
        layout.Controls.Add(buttonRow, 0, 5);
        layout.SetColumnSpan(buttonRow, 2);

        _logBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BackColor = Color.White,
        };
        layout.Controls.Add(_logBox, 0, 6);
        layout.SetColumnSpan(_logBox, 2);
        layout.RowStyles.Clear();
        for (var r = 0; r < 6; r++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        LoadState();
        _loading = false;

        Log.LineAdded += OnLogLine;
        _tray.StatusChanged += OnServiceStatusChanged;
        _tray.BusyChanged += OnBusyChanged;
        FormClosed += (_, _) =>
        {
            Log.LineAdded -= OnLogLine;
            _tray.StatusChanged -= OnServiceStatusChanged;
            _tray.BusyChanged -= OnBusyChanged;
        };
    }

    private void LoadState()
    {
        for (var i = 0; i < 3; i++)
        {
            var desktop = _settings.EasySwitchDesktops[i];
            _keyCombos[i].SelectedIndex = desktop is >= 1 and <= 9 ? desktop : 0;
        }
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        _startupCheck.Checked = key?.GetValue(RunValue) != null;
        _detectorButton.Text = _tray.DetectorRunning ? "Stop key detector" : "Start key detector";
        _statusLabel.Text = _tray.CurrentStatus;
        _logBox.Text = Log.Snapshot();
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
    }

    private void OnMappingChanged(int keyIndex, int selectedIndex)
    {
        if (_loading) return;
        _settings.EasySwitchDesktops[keyIndex] = selectedIndex; // 0 = do nothing, N = desktop N
        _settings.Save();
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

    private void OnServiceStatusChanged()
    {
        if (IsDisposed) return;
        BeginInvoke(() => _statusLabel.Text = _tray.CurrentStatus);
    }

    private void OnBusyChanged(bool busy)
    {
        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            _rescanButton.Enabled = !busy;
            _detectorButton.Enabled = !busy;
            _detectorButton.Text = busy ? "Working…"
                : _tray.DetectorRunning ? "Stop key detector" : "Start key detector";
        });
    }
}
