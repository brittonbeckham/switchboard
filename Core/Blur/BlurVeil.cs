using System.Runtime.InteropServices;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using Switchboard.Util;

namespace Switchboard.Core.Blur;

/// <summary>
/// The live-blur veil: attaches a composition tree to the overlay window with
/// one blurred-monitor visual per screen plus a tint layer. The overlay window
/// is excluded from capture so the veil never blurs itself.
/// UI thread only (composition); capture frames arrive on worker threads.
/// </summary>
public sealed class BlurVeil : IDisposable
{
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    private readonly BlurDevice _device;
    private readonly DesktopWindowTarget _target;
    private readonly ContainerVisual _root;
    private readonly SpriteVisual _tint;
    private readonly List<MonitorBlurCapture> _captures = [];
    private readonly Compositor _compositor;
    private readonly System.Windows.Forms.Timer _mixTimer;
    private float _mix = 1f;

    public BlurVeil(IntPtr overlayHandle, Rectangle virtualScreen, int tintPercent)
    {
        if (!SetWindowDisplayAffinity(overlayHandle, WDA_EXCLUDEFROMCAPTURE))
            Log.Info("Warning: couldn't exclude the veil from capture — expect feedback artifacts.");

        // Order matters: all composition interop must be resolved before the
        // first Vortice (D3D) type loads — see CompositionHost.Compositor.
        _compositor = CompositionHost.Compositor;
        _target = CompositionHost.CreateWindowTarget(overlayHandle);
        _device = new BlurDevice();
        _root = _compositor.CreateContainerVisual();
        _root.RelativeSizeAdjustment = new System.Numerics.Vector2(1f, 1f);
        _root.Opacity = 0f;
        _target.Root = _root;

        foreach (var screen in Screen.AllScreens)
        {
            var monitorHandle = MonitorFromPoint(
                new Point(screen.Bounds.Left + screen.Bounds.Width / 2,
                          screen.Bounds.Top + screen.Bounds.Height / 2), 2 /* MONITOR_DEFAULTTONEAREST */);
            var capture = new MonitorBlurCapture(_device, monitorHandle, screen.Bounds);
            _captures.Add(capture);

            var visual = _compositor.CreateSpriteVisual();
            visual.Offset = new System.Numerics.Vector3(
                screen.Bounds.X - virtualScreen.X, screen.Bounds.Y - virtualScreen.Y, 0);
            visual.Size = new System.Numerics.Vector2(screen.Bounds.Width, screen.Bounds.Height);
            var surface = CompositionHost.CreateSurfaceForSwapChain(capture.SwapChainPointer);
            var brush = _compositor.CreateSurfaceBrush(surface);
            brush.Stretch = CompositionStretch.Fill;
            visual.Brush = brush;
            _root.Children.InsertAtTop(visual);
        }

        _tint = _compositor.CreateSpriteVisual();
        _tint.RelativeSizeAdjustment = new System.Numerics.Vector2(1f, 1f);
        _root.Children.InsertAtTop(_tint);
        SetTintPercent(tintPercent);

        _mixTimer = new System.Windows.Forms.Timer { Interval = 15 };
        _mixTimer.Tick += (_, _) => StepMix();

        Log.Info($"Blur veil active: {_captures.Count} monitor(s) captured.");
    }

    /// <summary>Focus-pull: ramps the blur strength from sharp to blurred (~350 ms).</summary>
    public void PulseBlurIn()
    {
        _mix = 0f;
        PushMix();
        _mixTimer.Start();
    }

    private void StepMix()
    {
        _mix += (1f - _mix) * 0.14f;
        if (_mix > 0.99f)
        {
            _mix = 1f;
            _mixTimer.Stop();
        }
        PushMix();
    }

    private void PushMix()
    {
        foreach (var capture in _captures) capture.Renderer.BlurMix = _mix;
    }

    public void SetTintPercent(int percent)
    {
        var alpha = (byte)Math.Clamp(percent * 255 / 100, 0, 230);
        _tint.Brush = _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(alpha, 0, 0, 0));
    }

    /// <summary>Fades the whole veil (blur + tint) in or out, ~200 ms ease.</summary>
    public void SetVisible(bool visible)
    {
        var animation = _compositor.CreateScalarKeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(200);
        animation.InsertKeyFrame(1f, visible ? 1f : 0f);
        _root.StartAnimation("Opacity", animation);
    }

    public void Dispose()
    {
        _mixTimer.Dispose();
        foreach (var capture in _captures) capture.Dispose();
        _captures.Clear();
        _target.Root = null;
        _target.Dispose();
        _device.Dispose();
        Log.Info("Blur veil disposed.");
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point pt, uint flags);
}
