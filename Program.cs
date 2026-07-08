using Switchboard.Core;
using Switchboard.UI;
using Switchboard.Util;

namespace Switchboard;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, "Switchboard_SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Switchboard is already running — look for it in the taskbar tray.",
                "Switchboard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext(
            startInDetectorMode: Environment.GetCommandLineArgs().Contains("--detector")));
    }
}

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly AppSettings _settings;
    private DetectorService? _detector;
    private HotkeyService? _hotkeys;
    private SettingsForm? _settingsForm;

    /// <summary>Re-registers global hotkeys to match settings. UI thread only.</summary>
    public void ApplyHotkeySetting()
    {
        _hotkeys?.Dispose();
        _hotkeys = new HotkeyService(_settings);
    }

    private int _busy;

    public bool DetectorRunning => _detector != null;

    public string CurrentStatus => _detector?.Status ??
        (_settings.NumpadHotkeysEnabled || _settings.CalculatorFocusFixEnabled
            ? "Keyboard shortcuts active." : "All shortcuts disabled.");

    public event Action? StatusChanged;

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
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

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
        var detector = _detector;
        _detector = null;
        // Undiverting keys can stall if the keyboard is away; don't hang exit on it.
        if (detector != null)
            Task.Run(detector.Dispose).Wait(TimeSpan.FromSeconds(5));
        Application.Exit();
    }

    /// <summary>Draws the tray icon: three dots (the Easy-Switch keys), first one lit.</summary>
    private static Icon CreateTrayIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var accent = new SolidBrush(Color.FromArgb(0, 120, 212));
            using var dim = new SolidBrush(Color.FromArgb(140, 140, 140));
            g.FillEllipse(accent, 2, 11, 8, 8);
            g.FillEllipse(dim, 12, 11, 8, 8);
            g.FillEllipse(dim, 22, 11, 8, 8);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }
}
