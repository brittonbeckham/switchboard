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
        _peekTarget = IntPtr.Zero; // fresh window has no region
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

        StartPeekTimer();
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

    private IntPtr _lastForeground;

    private void Reposition()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == _overlay.Handle || IsShellWindow(foreground))
        {
            _lastForeground = IntPtr.Zero;
            SetVeilVisible(false);
            return;
        }

        // Slot the veil directly below the focused window.
        SetWindowPos(_overlay.Handle, foreground, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        var focusChanged = foreground != _lastForeground;
        _lastForeground = foreground;
        SetVeilVisible(true);
        // Focus-pull: re-animate the blur when attention moves to a new window.
        if (focusChanged && _veilVisible) _blurVeil?.PulseBlurIn();
        if (focusChanged) ClearPeekHole();
    }

    // ---- Hover-to-peek: cut the hovered background window out of the veil ----

    private System.Windows.Forms.Timer? _peekTimer;
    private IntPtr _peekTarget;

    private void StartPeekTimer()
    {
        _peekTimer = new System.Windows.Forms.Timer { Interval = 120 };
        _peekTimer.Tick += (_, _) => UpdatePeek();
        _peekTimer.Start();
    }

    private void UpdatePeek()
    {
        if (!_settings.FocusModePeekEnabled || !_veilVisible)
        {
            ClearPeekHole();
            return;
        }

        GetCursorPos(out var cursor);
        var hit = WindowFromPoint(cursor);
        var root = hit == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hit, 2 /* GA_ROOT */);

        // Peek only applies to real background windows.
        if (root == IntPtr.Zero || root == _overlay.Handle || root == _lastForeground || IsShellWindow(root))
        {
            ClearPeekHole();
            return;
        }
        if (root == _peekTarget) return;

        _peekTarget = root;
        var rect = GetExtendedFrameBounds(root);
        ApplyPeekHole(rect);
    }

    private Rectangle GetExtendedFrameBounds(IntPtr hwnd)
    {
        // DWM's extended frame bounds hug the visible window; GetWindowRect
        // includes the invisible resize border and drop shadow.
        if (DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */,
                out var rect, Marshal.SizeOf<RECT>()) != 0)
            GetWindowRect(hwnd, out rect);
        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    private void ApplyPeekHole(Rectangle screenRect)
    {
        // Region coordinates are overlay-window-relative.
        var origin = SystemInformation.VirtualScreen.Location;
        var local = screenRect with { X = screenRect.X - origin.X, Y = screenRect.Y - origin.Y };
        var size = SystemInformation.VirtualScreen.Size;

        var full = CreateRectRgn(0, 0, size.Width, size.Height);
        var hole = CreateRectRgn(local.Left, local.Top, local.Right, local.Bottom);
        CombineRgn(full, full, hole, 4 /* RGN_DIFF */);
        DeleteObject(hole);
        SetWindowRgn(_overlay.Handle, full, true); // the system now owns `full`
    }

    private void ClearPeekHole()
    {
        if (_peekTarget == IntPtr.Zero) return;
        _peekTarget = IntPtr.Zero;
        SetWindowRgn(_overlay.Handle, IntPtr.Zero, true);
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
        _peekTimer?.Dispose();
        _fadeTimer.Dispose();
        _blurVeil?.Dispose();
        _blurVeil = null;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT rect, int size);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr dest, IntPtr src1, IntPtr src2, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

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
