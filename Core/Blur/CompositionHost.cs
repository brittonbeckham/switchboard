using System.Runtime.InteropServices;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace Switchboard.Core.Blur;

/// <summary>
/// Shared Windows.UI.Composition plumbing: the per-thread DispatcherQueue, a
/// Compositor, and the COM interop needed to bind composition to Win32 HWNDs
/// and DXGI swapchains. UI thread only.
/// </summary>
public static class CompositionHost
{
    private static object? _dispatcherController;
    private static Compositor? _compositor;
    private static ICompositorDesktopInterop? _desktopInterop;
    private static ICompositorInterop? _interop;

    public static Compositor Compositor
    {
        get
        {
            if (_compositor == null)
            {
                EnsureDispatcherQueue();
                _compositor = new Compositor();
                // Resolve the interop interfaces immediately: other libraries
                // (Vortice) install their own COM marshalling once loaded, which
                // breaks later [ComImport] casts on WinRT objects. Grabbing the
                // references up front sidesteps the ordering hazard.
                _desktopInterop = _compositor.As<ICompositorDesktopInterop>();
                _interop = _compositor.As<ICompositorInterop>();
            }
            return _compositor;
        }
    }

    public static DesktopWindowTarget CreateWindowTarget(IntPtr hwnd)
    {
        _ = Compositor;
        _desktopInterop!.CreateDesktopWindowTarget(hwnd, true, out var targetPtr);
        try
        {
            return MarshalInterface<DesktopWindowTarget>.FromAbi(targetPtr);
        }
        finally
        {
            Marshal.Release(targetPtr);
        }
    }

    public static ICompositionSurface CreateSurfaceForSwapChain(IntPtr swapChain)
    {
        _ = Compositor;
        _interop!.CreateCompositionSurfaceForSwapChain(swapChain, out var surfacePtr);
        try
        {
            return MarshalInterface<ICompositionSurface>.FromAbi(surfacePtr);
        }
        finally
        {
            Marshal.Release(surfacePtr);
        }
    }

    private static void EnsureDispatcherQueue()
    {
        if (_dispatcherController != null) return;
        var options = new DispatcherQueueOptions
        {
            Size = Marshal.SizeOf<DispatcherQueueOptions>(),
            ThreadType = 2,    // DQTYPE_THREAD_CURRENT
            ApartmentType = 2, // DQTAT_COM_STA
        };
        var hr = CreateDispatcherQueueController(options, out var controllerPtr);
        if (hr != 0) throw new COMException("CreateDispatcherQueueController failed", hr);
        try
        {
            _dispatcherController = MarshalInterface<Windows.System.DispatcherQueueController>.FromAbi(controllerPtr);
        }
        finally
        {
            Marshal.Release(controllerPtr);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int Size;
        public int ThreadType;
        public int ApartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);

    [ComImport]
    [Guid("29E691FA-4567-4DCA-B319-D0F207EB6807")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorDesktopInterop
    {
        void CreateDesktopWindowTarget(IntPtr hwnd, bool isTopmost, out IntPtr target);
    }

    [ComImport]
    [Guid("25297D5C-3AD4-4C9C-B5CF-E36A38512330")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorInterop
    {
        void CreateCompositionSurfaceForHandle(IntPtr swapChain, out IntPtr surface);
        void CreateCompositionSurfaceForSwapChain(IntPtr swapChain, out IntPtr surface);
        void CreateGraphicsDevice(IntPtr renderingDevice, out IntPtr device);
    }
}
