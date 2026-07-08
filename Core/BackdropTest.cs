using System.Runtime.InteropServices;

namespace Switchboard.Core;

/// <summary>
/// Hidden diagnostic (run with: Switchboard.exe --backdroptest &lt;a|b&gt;).
/// Shows a test window at a fixed rect for 8 seconds using the documented
/// Windows 11 system-backdrop API so an external script can measure whether
/// acrylic actually renders. Variant a = plain window; b = layered click-through.
/// </summary>
public static class BackdropTest
{
    public static void Run(string variant)
    {
        var form = new TestForm(layered: variant == "b", activate: variant == "c");
        form.Shown += async (_, _) =>
        {
            await Task.Delay(8000);
            form.Close();
        };
        Application.Run(form);
    }

    private sealed class TestForm : Form
    {
        private readonly bool _layered;

        private readonly bool _activate;

        public TestForm(bool layered, bool activate = false)
        {
            _layered = layered;
            _activate = activate;
            Text = "Backdrop Test";
            FormBorderStyle = activate ? FormBorderStyle.Sizable : FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(100, 100, 800, 600);
            BackColor = Color.Black;
            ShowInTaskbar = false;
            TopMost = true;
        }

        protected override bool ShowWithoutActivation => !_activate;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                if (_layered) cp.ExStyle |= 0x80000 | 0x20; // WS_EX_LAYERED | WS_EX_TRANSPARENT
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (_layered) SetLayeredWindowAttributes(Handle, 0, 255, 0x2); // LWA_ALPHA, fully opaque
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            var hrFrame = DwmExtendFrameIntoClientArea(Handle, ref margins);
            var backdrop = 3; // DWMSBT_TRANSIENTWINDOW (acrylic)
            var hrType = DwmSetWindowAttribute(Handle, 38, ref backdrop, sizeof(int));
            Util.Log.Info($"BackdropTest(layered={_layered}, activate={_activate}): frame=0x{hrFrame:X8} type=0x{hrType:X8}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left, Right, Top, Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);
}
