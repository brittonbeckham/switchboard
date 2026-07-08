using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using WinRT;

namespace Switchboard.Core.Blur;

/// <summary>
/// Live Windows.Graphics.Capture session for one monitor, feeding each frame
/// through a BlurRenderer into that monitor's composition swapchain.
/// </summary>
public sealed class MonitorBlurCapture : IDisposable
{
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;
    private readonly BlurRenderer _renderer;
    private volatile bool _disposed;

    public Rectangle MonitorBounds { get; }
    public IntPtr SwapChainPointer => _renderer.SwapChainPointer;
    public BlurRenderer Renderer => _renderer;

    public MonitorBlurCapture(BlurDevice device, IntPtr monitorHandle, Rectangle monitorBounds)
    {
        MonitorBounds = monitorBounds;
        _renderer = new BlurRenderer(device, monitorBounds.Width, monitorBounds.Height);

        var item = CreateItemForMonitor(monitorHandle);
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2,
            new SizeInt32(monitorBounds.Width, monitorBounds.Height));
        _framePool.FrameArrived += OnFrameArrived;
        _session = _framePool.CreateCaptureSession(item);
        TrySet(() => _session.IsCursorCaptureEnabled = false);
        TrySet(() => _session.IsBorderRequired = false);
        _session.StartCapture();
    }

    private static void TrySet(Action set)
    {
        try { set(); } catch { /* older builds; cosmetic only */ }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_disposed) return;
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame == null) return;
            var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
            var iid = typeof(Vortice.Direct3D11.ID3D11Texture2D).GUID;
            var texPtr = access.GetInterface(ref iid);
            using var texture = new Vortice.Direct3D11.ID3D11Texture2D(texPtr);
            _renderer.Render(texture);
        }
        catch
        {
            // Transient capture errors (resolution change, session teardown) are non-fatal.
        }
    }

    private static GraphicsCaptureItem CreateItemForMonitor(IntPtr monitorHandle)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760"); // IGraphicsCaptureItem
        var itemPtr = interop.CreateForMonitor(monitorHandle, ref iid);
        try
        {
            return MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
        }
        finally
        {
            Marshal.Release(itemPtr);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _session.Dispose(); } catch { }
        try { _framePool.Dispose(); } catch { }
        _renderer.Dispose();
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, ref Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface(ref Guid iid);
    }
}
