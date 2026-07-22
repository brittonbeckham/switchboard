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

        using var mutex = new Mutex(initiallyOwned: true, "Switchboard_SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Switchboard is already running — look for it in the taskbar tray.",
                "Switchboard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.Run(new TrayContext(startInDetectorMode: args.Contains("--detector")));
    }
}

internal sealed class TrayContext : ApplicationContext, IActionHost
{
    private readonly NotifyIcon _trayIcon;
    private readonly AppSettings _settings;
    private DetectorService? _detector;
    private HotkeyService? _hotkeys;
    private FocusModeService? _focusMode;
    private SettingsForm? _settingsForm;
    private bool _micMuted;

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
            _trayIcon.Icon = CreateTrayIcon(_micMuted);
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

    public TrayContext(bool startInDetectorMode = false)
    {
        _settings = AppSettings.Load();
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
            ApplyFocusModeSetting();
        };
        _startupTimer.Start();

        _ = menu.Handle; // force handle creation so actions can BeginInvoke onto the UI thread
        _trayIcon = new NotifyIcon
        {
            Icon = CreateTrayIcon(),
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
        var detector = _detector;
        _detector = null;
        // Undiverting keys can stall if the keyboard is away; don't hang exit on it.
        if (detector != null)
            Task.Run(detector.Dispose).Wait(TimeSpan.FromSeconds(5));
        Application.Exit();
    }

    /// <summary>Draws the tray icon: three dots (the Easy-Switch keys), first one lit.</summary>
    private static Icon CreateTrayIcon(bool micMuted = false)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            if (micMuted)
            {
                // Solid red disc with a white slash: the mic is dead.
                using var red = new SolidBrush(Color.FromArgb(220, 40, 40));
                using var white = new Pen(Color.White, 4);
                g.FillEllipse(red, 2, 2, 28, 28);
                g.DrawLine(white, 8, 24, 24, 8);
                return Icon.FromHandle(bitmap.GetHicon());
            }
            using var accent = new SolidBrush(Color.FromArgb(0, 120, 212));
            using var dim = new SolidBrush(Color.FromArgb(140, 140, 140));
            g.FillEllipse(accent, 2, 11, 8, 8);
            g.FillEllipse(dim, 12, 11, 8, 8);
            g.FillEllipse(dim, 22, 11, 8, 8);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }
}
