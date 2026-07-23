using System.Runtime.InteropServices;
using System.Text;

namespace Switchboard.Core;

/// <summary>
/// Watches raw keyboard input system-wide and reports only keystrokes that came
/// from a specific physical device (matched by a VID/PID fragment in the device
/// path). This is how the pad's keys are told apart from the main keyboard —
/// the normal Windows key messages carry no device identity, but WM_INPUT does.
/// Must be created on a thread with a message loop (the UI thread).
/// </summary>
public sealed class RawKeyboardMonitor : IDisposable
{
    private const int WM_INPUT = 0x00FF;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RIDEV_REMOVE = 0x00000001;

    /// <summary>(virtualKey, isKeyDown) for keystrokes from the matched device.</summary>
    public event Action<ushort, bool>? DeviceKey;

    private readonly string _match;
    private readonly Sink _sink;
    private readonly Dictionary<IntPtr, bool> _isDeviceCache = [];
    private readonly int _headerSize = Marshal.SizeOf<RawInputHeader>();
    private bool _disposed;

    public RawKeyboardMonitor(string vidPidFragment)
    {
        _match = vidPidFragment;
        _sink = new Sink(OnInput);
        var rid = new RawInputDevice
        {
            UsagePage = 0x01,
            Usage = 0x06, // keyboards
            Flags = RIDEV_INPUTSINK,
            Target = _sink.Handle,
        };
        if (!RegisterRawInputDevices([rid], 1, (uint)Marshal.SizeOf<RawInputDevice>()))
            Util.Log.Info("Raw keyboard input registration failed — key HUD won't detect the pad.");
    }

    private void OnInput(IntPtr lParam)
    {
        uint size = 0;
        GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, (uint)_headerSize);
        if (size == 0) return;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RID_INPUT, buffer, ref size, (uint)_headerSize) != size) return;
            var header = Marshal.PtrToStructure<RawInputHeader>(buffer);
            if (header.Type != 1) return; // keyboard
            if (!IsDevice(header.Device)) return;

            var kb = Marshal.PtrToStructure<RawKeyboard>(buffer + _headerSize);
            if (kb.VKey is 0 or 0xFF) return; // ignore fake/overrun keys
            var isDown = (kb.Flags & 0x01) == 0; // RI_KEY_BREAK = 1 = up
            DeviceKey?.Invoke(kb.VKey, isDown);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private bool IsDevice(IntPtr handle)
    {
        if (_isDeviceCache.TryGetValue(handle, out var cached)) return cached;
        var isDevice = false;
        try
        {
            uint pcb = 0;
            GetRawInputDeviceInfo(handle, RIDI_DEVICENAME, IntPtr.Zero, ref pcb);
            if (pcb > 0)
            {
                var sb = new StringBuilder((int)pcb + 1);
                GetRawInputDeviceInfo(handle, RIDI_DEVICENAME, sb, ref pcb);
                isDevice = sb.ToString().Contains(_match, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // Unknown device path; treat as not-ours.
        }
        _isDeviceCache[handle] = isDevice;
        return isDevice;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var rid = new RawInputDevice { UsagePage = 0x01, Usage = 0x06, Flags = RIDEV_REMOVE, Target = IntPtr.Zero };
        RegisterRawInputDevices([rid], 1, (uint)Marshal.SizeOf<RawInputDevice>());
        _sink.DestroyHandle();
    }

    private sealed class Sink : NativeWindow
    {
        private readonly Action<IntPtr> _onInput;

        public Sink(Action<IntPtr> onInput)
        {
            _onInput = onInput;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_INPUT) _onInput(m.LParam);
            base.WndProc(ref m);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawKeyboard
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, StringBuilder data, ref uint size);
}
