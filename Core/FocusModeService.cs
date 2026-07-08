using System.Runtime.InteropServices;
using Switchboard.Util;

namespace Switchboard.Core;

/// <summary>
/// Focus mode: dims everything behind the active window. A single click-through
/// layered overlay spans all monitors and is kept in the Z order directly below
/// the foreground window, so only the focused window stays undimmed. Focus
/// changes arrive via WinEvent hooks. Create and use on the UI thread only.
/// </summary>
public sealed class FocusModeService : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    private const uint WINEVENT_OUTOFCONTEXT = 0;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    // Windows that mean "nothing is really focused" — fade the veil away.
    private static readonly string[] ShellClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];

    private readonly OverlayForm _overlay = new();
    private readonly System.Windows.Forms.Timer _fadeTimer;
    private readonly WinEventDelegate _eventProc; // field keeps the native callback alive
    private readonly List<IntPtr> _hooks = [];
    private double _targetOpacity;
    private double _maxOpacity;

    public FocusModeService(int dimPercent)
    {
        _maxOpacity = ToOpacity(dimPercent);
        _overlay.Bounds = SystemInformation.VirtualScreen;
        _overlay.Show();

        _fadeTimer = new System.Windows.Forms.Timer { Interval = 15 };
        _fadeTimer.Tick += (_, _) => StepFade();

        _eventProc = OnWinEvent;
        foreach (var evt in new[] { EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND })
            _hooks.Add(SetWinEventHook(evt, evt, IntPtr.Zero, _eventProc, 0, 0, WINEVENT_OUTOFCONTEXT));

        Reposition();
        Log.Info($"Focus mode on ({dimPercent}% dim).");
    }

    public void SetDimPercent(int percent)
    {
        _maxOpacity = ToOpacity(percent);
        Reposition();
    }

    private static double ToOpacity(int percent) => Math.Clamp(percent, 5, 90) / 100.0;

    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int objectId, int childId, uint thread, uint time)
    {
        try
        {
            Reposition();
        }
        catch
        {
            // Never let an exception escape a native callback.
        }
    }

    private void Reposition()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == _overlay.Handle || IsShellWindow(foreground))
        {
            FadeTo(0);
            return;
        }

        // Slot the veil directly below the focused window.
        SetWindowPos(_overlay.Handle, foreground, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        FadeTo(_maxOpacity);
    }

    private static bool IsShellWindow(IntPtr hwnd)
    {
        var buffer = new System.Text.StringBuilder(64);
        _ = GetClassName(hwnd, buffer, buffer.Capacity);
        return ShellClasses.Contains(buffer.ToString());
    }

    private void FadeTo(double target)
    {
        _targetOpacity = target;
        _fadeTimer.Start();
    }

    private void StepFade()
    {
        var current = _overlay.Opacity;
        var diff = _targetOpacity - current;
        if (Math.Abs(diff) < 0.015)
        {
            _overlay.Opacity = _targetOpacity;
            _fadeTimer.Stop();
            return;
        }
        _overlay.Opacity = current + diff * 0.25; // ease-out toward the target
    }

    public void Dispose()
    {
        foreach (var hook in _hooks) UnhookWinEvent(hook);
        _hooks.Clear();
        _fadeTimer.Dispose();
        _overlay.Dispose();
        Log.Info("Focus mode off.");
    }

    /// <summary>Borderless, layered, click-through, never-activated veil.</summary>
    private sealed class OverlayForm : Form
    {
        public OverlayForm()
        {
            Text = "Switchboard Focus Overlay";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;
            Opacity = 0;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // WS_EX_LAYERED | WS_EX_TRANSPARENT (click-through) |
                // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW (no Alt-Tab entry)
                cp.ExStyle |= 0x80000 | 0x20 | 0x8000000 | 0x80;
                return cp;
            }
        }
    }

    private delegate void WinEventDelegate(IntPtr hook, uint evt, IntPtr hwnd, int objectId, int childId, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
        WinEventDelegate proc, uint pid, uint thread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
}
