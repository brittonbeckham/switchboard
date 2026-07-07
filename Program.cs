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
        Application.Run(new TrayContext());
    }
}

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly AppSettings _settings;
    private readonly EasySwitchService _service;
    private SettingsForm? _settingsForm;

    public TrayContext()
    {
        _settings = AppSettings.Load();
        _service = new EasySwitchService(_settings);
        Log.Info("Switchboard started.");

        var menu = new ContextMenuStrip();
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add("Rescan devices", null, (_, _) => _service.RescanNow());
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
        _settingsForm = new SettingsForm(_settings, _service);
        _settingsForm.Show();
    }

    private void ExitApp()
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _service.Dispose();
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
