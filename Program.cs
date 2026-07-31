using Switchboard.Core;
using Switchboard.UI;
using Switchboard.Util;

namespace Switchboard;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        var args = Environment.GetCommandLineArgs();
        var testIndex = Array.IndexOf(args, "--backdroptest");
        if (testIndex >= 0)
        {
            // Diagnostic side-mode: intentionally exempt from the single-instance gate.
            Core.BackdropTest.Run(testIndex + 1 < args.Length ? args[testIndex + 1] : "a");
            return;
        }

        var compIndex = Array.IndexOf(args, "--compositiontest");
        if (compIndex >= 0)
        {
            Core.CompositionTest.Run(compIndex + 1 < args.Length ? args[compIndex + 1] : "1");
            return;
        }

        if (args.Contains("--hudtest"))
        {
            var hud = new UI.KeyHudStack();
            (UI.HudPress Press, string[] Mods, string Base, string Title, string? Tag)[] samples =
            [
                (new UI.HudPress(UI.HudControlKind.KeyGrid, 1, 1, 0, 0), ["CTRL"], "A", "Select All", null),
                (new UI.HudPress(UI.HudControlKind.KeyGrid, 0, 2, 0, 2), ["CTRL", "WIN"], "M", "Mute Microphone", null),
                (new UI.HudPress(UI.HudControlKind.Knob, 0, 0, 1, 0), [], "Play", "Play / Pause", null),
                (new UI.HudPress(UI.HudControlKind.KeyGrid, 3, 3, 0, 0), [], "F24", "Mute Microphone", "ghost"),
            ];
            var i = 0;
            var feed = new System.Windows.Forms.Timer { Interval = 500 };
            feed.Tick += (_, _) =>
            {
                if (i < samples.Length) hud.ShowKey(samples[i].Press, samples[i].Mods, samples[i].Base, samples[i].Title, samples[i].Tag);
                i++;
                if (i > samples.Length + 6) Application.Exit();
            };
            feed.Start();
            Application.Run();
            return;
        }

        using var mutex = new Mutex(initiallyOwned: true, "Switchboard_SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Switchboard is already running — look for it in the taskbar tray.",
                "Switchboard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var settingsIndex = Array.IndexOf(args, "--settings");
        Application.Run(new TrayContext(
            startInDetectorMode: args.Contains("--detector"),
            openSettingsPage: settingsIndex >= 0
                ? settingsIndex + 1 < args.Length ? args[settingsIndex + 1] : ""
                : null));
    }
}

internal sealed class TrayContext : ApplicationContext, IActionHost
{
    private readonly NotifyIcon _trayIcon;
    private readonly AppSettings _settings;
    private DetectorService? _detector;
    private HotkeyService? _hotkeys;
    private FocusModeService? _focusMode;
    private RawKeyboardMonitor? _rawKeyboard;
    private KeyHudService? _keyHud;
    private SettingsForm? _settingsForm;
    private bool _micMuted;

    /// <summary>Enables or disables the macropad key HUD to match settings. UI thread only.</summary>
    public void ApplyKeyHudSetting()
    {
        if (_settings.KeyHudEnabled && _keyHud == null)
        {
            _rawKeyboard ??= new RawKeyboardMonitor("VID_D010&PID_1601");
            _keyHud = new KeyHudService(_settings, _rawKeyboard);
        }
        else if (!_settings.KeyHudEnabled && _keyHud != null)
        {
            _keyHud.Dispose();
            _keyHud = null;
            _rawKeyboard?.Dispose();
            _rawKeyboard = null;
        }
    }

    /// <summary>Called after pad edits so the HUD's label lookup stays current.</summary>
    public void RefreshKeyHud() => _keyHud?.RefreshLookup();

    /// <summary>Re-registers global hotkeys to match settings. UI thread only.</summary>
    public void ApplyHotkeySetting()
    {
        _hotkeys?.Dispose();
        _hotkeys = new HotkeyService(_settings, this);
    }

    // ---- IActionHost ----

    public void OnMicMuteChanged(bool muted)
    {
        _micMuted = muted;
        _trayIcon.ContextMenuStrip?.BeginInvoke(() =>
        {
            _trayIcon.Icon = AppIcon.Create(_micMuted);
            _trayIcon.Text = _micMuted ? "Switchboard — MIC MUTED" : "Switchboard";
        });
    }

    public void ToggleFocusMode()
    {
        _trayIcon.ContextMenuStrip?.BeginInvoke(() =>
        {
            _settings.FocusModeEnabled = !_settings.FocusModeEnabled;
            _settings.Save();
            ApplyFocusModeSetting();
        });
    }

    public void OpenSettings()
    {
        _trayIcon.ContextMenuStrip?.BeginInvoke(ShowSettings);
    }

    /// <summary>Creates/updates/tears down the focus-mode overlay to match settings. UI thread only.</summary>
    public void ApplyFocusModeSetting()
    {
        if (_settings.FocusModeEnabled)
        {
            if (_focusMode == null)
                _focusMode = new FocusModeService(_settings);
            else
                _focusMode.ApplySettings();
        }
        else if (_focusMode != null)
        {
            _focusMode.Dispose();
            _focusMode = null;
        }
        if (_focusMenuItem != null) _focusMenuItem.Checked = _settings.FocusModeEnabled;
    }

    private ToolStripMenuItem? _focusMenuItem;
    private System.Windows.Forms.Timer? _startupTimer;
    private readonly string? _openSettingsPageAtStart;

    private int _busy;

    public bool DetectorRunning => _detector != null;

    public string CurrentStatus
    {
        get
        {
            if (_detector != null) return _detector.Status;
            var mapped = _settings.FunctionKeyActions.Count(kv => kv.Value != ActionCatalog.None);
            return mapped > 0 ? $"{mapped} function key(s) mapped." : "No keys mapped yet.";
        }
    }

    public event Action? StatusChanged;

    public void NotifyStatusChanged() => StatusChanged?.Invoke();

    /// <summary>Raised (from a worker thread) when a scan/mode switch starts or finishes.</summary>
    public event Action<bool>? BusyChanged;

    /// <summary>Switches between normal interception and key-detector diagnostic mode.
    /// HID scans take seconds, so the work runs off the UI thread.</summary>
    public void ToggleDetector() => RunInBackground(() =>
    {
        if (_detector == null)
        {
            _detector = new DetectorService();
            _detector.Start();
        }
        else
        {
            _detector.Dispose();
            _detector = null;
        }
    });

    private void RunInBackground(Action work)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return; // one operation at a time
        BusyChanged?.Invoke(true);
        Task.Run(() =>
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                Log.Info($"Operation failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
                BusyChanged?.Invoke(false);
                StatusChanged?.Invoke();
            }
        });
    }

    public TrayContext(bool startInDetectorMode = false, string? openSettingsPage = null)
    {
        ActionCatalog.DefaultHost = this;
        _settings = AppSettings.Load();
        _openSettingsPageAtStart = openSettingsPage;
        if (openSettingsPage != null) Log.Info($"Will auto-open settings page '{openSettingsPage}'.");
        if (startInDetectorMode)
        {
            var detector = _detector = new DetectorService();
            Task.Run(detector.Start);
        }
        ApplyHotkeySetting();
        Log.Info("Switchboard started.");

        var menu = new ContextMenuStrip();
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        _focusMenuItem = new ToolStripMenuItem("Focus mode");
        _focusMenuItem.Click += (_, _) =>
        {
            _settings.FocusModeEnabled = !_settings.FocusModeEnabled;
            _settings.Save();
            ApplyFocusModeSetting();
        };
        menu.Items.Add(_focusMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        // Composition interop needs a running message loop; defer via a one-shot
        // timer (Application.Idle never fires in a message-starved tray app).
        // Field-rooted so the GC can't collect it before it ticks.
        _startupTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _startupTimer.Tick += (_, _) =>
        {
            _startupTimer!.Dispose();
            _startupTimer = null;
            Log.Info("Startup timer fired.");
            ApplyFocusModeSetting();
            ApplyKeyHudSetting();
            if (_openSettingsPageAtStart != null)
            {
                try
                {
                    ShowSettings();
                    if (_openSettingsPageAtStart.Length > 0)
                        _settingsForm?.SelectPage(_openSettingsPageAtStart);
                }
                catch (Exception ex)
                {
                    Log.Info($"Settings auto-open failed: {ex}");
                }
            }
        };
        _startupTimer.Start();

        _ = menu.Handle; // force handle creation so actions can BeginInvoke onto the UI thread
        _trayIcon = new NotifyIcon
        {
            Icon = AppIcon.Create(),
            Text = "Switchboard",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => ShowSettings();
    }

    private void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            if (_settingsForm.WindowState == FormWindowState.Minimized)
                _settingsForm.WindowState = FormWindowState.Normal;
            _settingsForm.Show();
            _settingsForm.Activate();
            return;
        }
        _settingsForm = new SettingsForm(_settings, this);
        _settingsForm.Show();
    }

    private void ExitApp()
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _hotkeys?.Dispose();
        _hotkeys = null;
        _focusMode?.Dispose();
        _focusMode = null;
        _keyHud?.Dispose();
        _keyHud = null;
        _rawKeyboard?.Dispose();
        _rawKeyboard = null;
        var detector = _detector;
        _detector = null;
        // Undiverting keys can stall if the keyboard is away; don't hang exit on it.
        if (detector != null)
            Task.Run(detector.Dispose).Wait(TimeSpan.FromSeconds(5));
        Application.Exit();
    }

}
