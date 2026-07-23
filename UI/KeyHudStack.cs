using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Switchboard.UI;

/// <summary>
/// A stack of macropad-key notification cards in the corner of the active
/// screen. Newest drops in at the bottom, older cards push up, each fades on
/// its own timeline. One click-through, no-activate layered window renders the
/// whole stack with per-card alpha (via UpdateLayeredWindow), and re-shows
/// itself on the current virtual desktop so it stays visible across switches.
/// </summary>
internal sealed class KeyHudStack : Form
{
    private const int WinWidth = 320;
    private const int WinHeight = 540;
    private const int CardWidth = 300;
    private const int CardHeight = 74;
    private const int CardGap = 10;
    private const int MarginRight = 16;
    private const int MarginBottom = 16;

    private const int FadeInMs = 140;
    private const int HoldMs = 1500;
    private const int FadeOutMs = 420;

    private static readonly Color Card = Color.FromArgb(38, 38, 44);
    private static readonly Color CardBorder = Color.FromArgb(78, 80, 90);
    private static readonly Color CapFill = Color.FromArgb(0, 103, 192);
    private static readonly Color Primary = Color.White;
    private static readonly Color Secondary = Color.FromArgb(172, 176, 184);

    private sealed class Toast
    {
        public required string Cap;
        public required string Title;
        public required string Subtitle;
        public long StartMs;
        public float CurrentY;
        public bool Placed;
    }

    private readonly List<Toast> _toasts = [];
    private readonly System.Windows.Forms.Timer _anim = new() { Interval = 16 };
    private readonly IVirtualDesktopManager? _vdm;
    private long _lastFrame;

    public KeyHudStack()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(WinWidth, WinHeight);
        _anim.Tick += (_, _) => Frame();

        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a"));
            if (type != null) _vdm = (IVirtualDesktopManager?)Activator.CreateInstance(type);
            Util.Log.Info($"Key HUD: virtual-desktop follow {(_vdm != null ? "available" : "unavailable")}.");
        }
        catch (Exception ex)
        {
            _vdm = null; // desktop-following unavailable; stack still works
            Util.Log.Info($"Key HUD: virtual-desktop follow unavailable ({ex.Message}).");
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // WS_EX_TOPMOST | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            cp.ExStyle |= 0x8 | 0x80000 | 0x20 | 0x8000000 | 0x80;
            return cp;
        }
    }

    private static long NowMs => Environment.TickCount64;

    /// <summary>Adds a new card to the bottom of the stack.</summary>
    public void ShowKey(string cap, string title, string subtitle)
    {
        // Reposition to the active screen only when starting a fresh stack.
        if (_toasts.Count == 0)
        {
            var area = Screen.FromHandle(GetForegroundWindow()).WorkingArea;
            Location = new Point(area.Right - WinWidth - MarginRight + 8,
                                 area.Bottom - WinHeight - MarginBottom + 8);
            if (!Visible) Show();
        }

        _toasts.Add(new Toast
        {
            Cap = cap,
            Title = title,
            Subtitle = subtitle,
            StartMs = NowMs,
            CurrentY = WinHeight, // slides up from below its slot
        });
        _lastFrame = NowMs;
        if (!_anim.Enabled) _anim.Start();
        Frame();
    }

    private void Frame()
    {
        var now = NowMs;
        _lastFrame = now;

        _toasts.RemoveAll(t => now - t.StartMs > FadeInMs + HoldMs + FadeOutMs);
        if (_toasts.Count == 0)
        {
            _anim.Stop();
            if (Visible) Hide();
            return;
        }

        FollowActiveDesktop();

        // Layout: newest at the bottom, older stacked above.
        var count = _toasts.Count;
        for (var i = 0; i < count; i++)
        {
            var fromBottom = count - 1 - i;
            var targetY = WinHeight - CardHeight - fromBottom * (CardHeight + CardGap) - 2;
            var toast = _toasts[i];
            if (!toast.Placed)
            {
                toast.CurrentY = targetY + 34;
                toast.Placed = true;
            }
            toast.CurrentY += (targetY - toast.CurrentY) * 0.30f;
        }

        Render(now);
    }

    private void FollowActiveDesktop()
    {
        // Re-assert topmost so Show Desktop / app overlays can't bury us.
        SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        if (_vdm == null) return;
        try
        {
            // Move the HUD onto whatever desktop the foreground window is on, so a
            // desktop switch (Prev/Next Desktop keys) doesn't leave it behind.
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == Handle) return;
            if (_vdm.GetWindowDesktopId(fg, out var target) != 0 || target == Guid.Empty) return;
            if (_vdm.GetWindowDesktopId(Handle, out var mine) == 0 && mine == target) return;
            _vdm.MoveWindowToDesktop(Handle, ref target);
            SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        }
        catch
        {
            // Ignore transient COM failures during a switch.
        }
    }

    private void Render(long now)
    {
        using var bmp = new Bitmap(WinWidth, WinHeight, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            foreach (var toast in _toasts)
                DrawCard(g, toast, Alpha(now, toast));
        }
        Premultiply(bmp);
        PushLayered(bmp);
    }

    private static float Alpha(long now, Toast t)
    {
        var age = now - t.StartMs;
        if (age < FadeInMs) return age / (float)FadeInMs;
        if (age < FadeInMs + HoldMs) return 1f;
        return Math.Max(0f, 1f - (age - FadeInMs - HoldMs) / (float)FadeOutMs);
    }

    private static void DrawCard(Graphics g, Toast t, float alpha)
    {
        if (alpha <= 0.01f) return;
        var a = (int)(alpha * 255);
        var x = WinWidth - CardWidth - 6;
        var y = (int)t.CurrentY;
        var card = new Rectangle(x, y, CardWidth, CardHeight);

        using (var path = Rounded(card, 12))
        {
            using var fill = new SolidBrush(Color.FromArgb((int)(alpha * 244), Card));
            using var border = new Pen(Color.FromArgb(a, CardBorder));
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }

        var capRect = new Rectangle(x + 14, y + 14, 46, 46);
        using (var path = Rounded(capRect, 8))
        using (var fill = new SolidBrush(Color.FromArgb(a, CapFill)))
            g.FillPath(fill, path);
        using (var capFont = new Font("Segoe UI Semibold", t.Cap.Length > 2 ? 10f : 15f))
        using (var capBrush = new SolidBrush(Color.FromArgb(a, Primary)))
        using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString(t.Cap, capFont, capBrush, capRect, fmt);

        var textX = x + 74;
        var textW = CardWidth - 74 - 12;
        using (var titleFont = new Font("Segoe UI Semibold", 12f))
        using (var titleBrush = new SolidBrush(Color.FromArgb(a, Primary)))
        using (var fmt = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString(t.Title, titleFont, titleBrush, new RectangleF(textX, y + 12, textW, 28), fmt);
        using (var subFont = new Font("Segoe UI", 9f))
        using (var subBrush = new SolidBrush(Color.FromArgb((int)(alpha * 220), Secondary)))
        using (var fmt = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString(t.Subtitle, subFont, subBrush, new RectangleF(textX, y + 40, textW, 24), fmt);
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>UpdateLayeredWindow needs premultiplied alpha; GDI+ produces straight alpha.</summary>
    private static unsafe void Premultiply(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var p = (byte*)data.Scan0;
            var total = data.Height * data.Stride;
            for (var i = 0; i < total; i += 4)
            {
                var a = p[i + 3];
                p[i] = (byte)(p[i] * a / 255);
                p[i + 1] = (byte)(p[i + 1] * a / 255);
                p[i + 2] = (byte)(p[i + 2] * a / 255);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private void PushLayered(Bitmap bmp)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(memDc, hBitmap);
        try
        {
            var size = new Size(bmp.Width, bmp.Height);
            var src = new Point(0, 0);
            var dst = new Point(Left, Top);
            var blend = new BlendFunction
            {
                BlendOp = 0, // AC_SRC_OVER
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = 1, // AC_SRC_ALPHA
            };
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 0x02);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _anim.Dispose();
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, ref Point dst, ref Size size,
        IntPtr srcDc, ref Point src, int colorKey, ref BlendFunction blend, int flags);
}

[ComImport]
[Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVirtualDesktopManager
{
    [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr hwnd, out int onCurrentDesktop);
    [PreserveSig] int GetWindowDesktopId(IntPtr hwnd, out Guid desktopId);
    [PreserveSig] int MoveWindowToDesktop(IntPtr hwnd, ref Guid desktopId);
}
