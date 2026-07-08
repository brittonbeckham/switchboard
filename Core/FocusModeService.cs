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

    private OverlayForm _overlay = null!;

    private void RecreateOverlay(bool composition)
    {
        var old = _overlay;
        _overlay = new OverlayForm(composition) { Bounds = SystemInformation.VirtualScreen };
        _overlay.Show();
        old?.Dispose();
    }
    private readonly System.Windows.Forms.Timer _fadeTimer;
    private readonly WinEventDelegate _eventProc; // field keeps the native callback alive
    private readonly List<IntPtr> _hooks = [];
    private readonly AppSettings _settings;
    private double _targetOpacity;
    private double _maxOpacity;

    public FocusModeService(AppSettings settings)
    {
        _settings = settings;
        RecreateOverlay(composition: false);

        _fadeTimer = new System.Windows.Forms.Timer { Interval = 15 };
        _fadeTimer.Tick += (_, _) => StepFade();

        _eventProc = OnWinEvent;
        foreach (var evt in new[] { EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND })
            _hooks.Add(SetWinEventHook(evt, evt, IntPtr.Zero, _eventProc, 0, 0, WINEVENT_OUTOFCONTEXT));

        ApplySettings();
        Log.Info($"Focus mode on ({settings.FocusModeDimPercent}% dim{(settings.FocusModeBlurEnabled ? " + blur" : "")}).");
    }

    private Blur.BlurVeil? _blurVeil;
    private bool _veilVisible;

    /// <summary>Re-reads dim/blur settings and applies them to the live overlay.</summary>
    public void ApplySettings()
    {
        if (_settings.FocusModeBlurEnabled && _blurVeil == null)
        {
            try
            {
                RecreateOverlay(composition: true);
                _blurVeil = new Blur.BlurVeil(_overlay.Handle, SystemInformation.VirtualScreen,
                    _settings.FocusModeDimPercent);
            }
            catch (Exception ex)
            {
                Log.Info($"Blur veil failed; using dim only. {ex}");
                _blurVeil = null;
                RecreateOverlay(composition: false);
            }
        }
        else if (!_settings.FocusModeBlurEnabled && _blurVeil != null)
        {
            _blurVeil.Dispose();
            _blurVeil = null;
            RecreateOverlay(composition: false);
        }
        _blurVeil?.SetTintPercent(_settings.FocusModeDimPercent);

        _maxOpacity = ToOpacity(_settings.FocusModeDimPercent);
        _veilVisible = false; // force re-evaluation
        Reposition();
    }

    /// <summary>
    /// Turns acrylic blur-behind on or off via the undocumented
    /// SetWindowCompositionAttribute accent-policy API. Returns false if the
    /// call is unavailable/rejected so the caller can fall back to plain dim.
    /// </summary>
    private bool ApplyAccent(bool blur, int tintPercent)
    {
        var accent = new AccentPolicy
        {
            AccentState = blur ? ACCENT_ENABLE_BLURBEHIND : ACCENT_DISABLED,
            AccentFlags = 2,
            GradientColor = 0,
        };
        var size = Marshal.SizeOf<AccentPolicy>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, buffer, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = buffer,
                SizeOfData = size,
            };
            var ok = SetWindowCompositionAttribute(_overlay.Handle, ref data) != 0;
            if (blur && !ok) Log.Info("Acrylic blur unavailable on this system; falling back to dim.");
            return ok;
        }
        catch (Exception ex)
        {
            if (blur) Log.Info($"Acrylic blur failed ({ex.Message}); falling back to dim.");
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
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
            SetVeilVisible(false);
            return;
        }

        // Slot the veil directly below the focused window.
        SetWindowPos(_overlay.Handle, foreground, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        SetVeilVisible(true);
    }

    private void SetVeilVisible(bool visible)
    {
        if (_blurVeil != null)
        {
            if (_veilVisible != visible)
            {
                _veilVisible = visible;
                _blurVeil.SetVisible(visible);
            }
            return;
        }
        FadeTo(visible ? _maxOpacity : 0);
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

    /// <summary>
    /// Borderless, click-through, never-activated veil. In composition (blur)
    /// mode the window has no GDI surface at all (WS_EX_NOREDIRECTIONBITMAP):
    /// only the composition visual tree renders, so visibility is controlled
    /// purely by visual opacity. In GDI (dim) mode it's a classic layered
    /// window faded via LWA alpha.
    /// </summary>
    private sealed class OverlayForm : Form
    {
        private readonly bool _composition;

        public OverlayForm(bool composition)
        {
            _composition = composition;
            Text = "Switchboard Focus Overlay";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;
            if (!composition) Opacity = 0;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // WS_EX_LAYERED | WS_EX_TRANSPARENT: click-through only works with
                // BOTH present. WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW (no Alt-Tab),
                // plus WS_EX_NOREDIRECTIONBITMAP in composition mode (no GDI surface).
                cp.ExStyle |= 0x80000 | 0x20 | 0x8000000 | 0x80;
                if (_composition) cp.ExStyle |= 0x00200000;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // A layered window renders nothing until its attributes are set; in
            // composition mode pin it fully opaque (visuals control visibility).
            if (_composition) SetLayeredWindowAttributes(Handle, 0, 255, 0x2 /* LWA_ALPHA */);
        }

        [DllImport("user32.dll")]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);
    }

    private const int ACCENT_DISABLED = 0;
    private const int ACCENT_ENABLE_BLURBEHIND = 3;
    private const int WCA_ACCENT_POLICY = 19;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

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
