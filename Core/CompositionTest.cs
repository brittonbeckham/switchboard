using System.Runtime.InteropServices;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace Switchboard.Core;

/// <summary>
/// Hidden diagnostic (run with: Switchboard.exe --compositiontest &lt;1|2&gt;).
/// Spike for the Windows.UI.Composition blur route:
///   variant 1 = SpriteVisual with a semi-transparent red ColorBrush
///               (proves the compositor → desktop-window pipeline renders),
///   variant 2 = SpriteVisual with CreateHostBackdropBrush
///               (proves the behind-the-window backdrop source works).
/// Shows a window at (100,100) 800x600 for 8 seconds so an external script
/// can measure the pixels.
/// </summary>
public static class CompositionTest
{
    // Keep composition objects rooted for the window's lifetime.
    private static object? _dispatcherController;
    private static Compositor? _compositor;
    private static DesktopWindowTarget? _target;

    public static void Run(string variant)
    {
        var form = new Form
        {
            Text = "Composition Test",
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(100, 100, 800, 600),
            BackColor = Color.Black,
            ShowInTaskbar = false,
            TopMost = true,
        };
        form.Shown += async (_, _) =>
        {
            try
            {
                Attach(form.Handle, variant);
                Util.Log.Info($"CompositionTest variant {variant}: attached OK.");
            }
            catch (Exception ex)
            {
                Util.Log.Info($"CompositionTest variant {variant} FAILED: {ex}");
            }
            await Task.Delay(8000);
            form.Close();
        };
        Application.Run(form);
    }

    private static void Attach(IntPtr hwnd, string variant)
    {
        EnsureDispatcherQueue();
        _compositor = new Compositor();

        var interop = _compositor.As<ICompositorDesktopInterop>();
        interop.CreateDesktopWindowTarget(hwnd, true, out var targetPtr);
        _target = MarshalInterface<DesktopWindowTarget>.FromAbi(targetPtr);

        var root = _compositor.CreateSpriteVisual();
        root.RelativeSizeAdjustment = new System.Numerics.Vector2(1f, 1f);
        root.Brush = variant == "2"
            ? _compositor.CreateHostBackdropBrush()
            : _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(128, 255, 0, 0));
        _target.Root = root;
    }

    /// <summary>The compositor needs a DispatcherQueue on the calling (STA/UI) thread.</summary>
    public static void EnsureDispatcherQueue()
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
        _dispatcherController = MarshalInterface<Windows.System.DispatcherQueueController>.FromAbi(controllerPtr);
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
}
