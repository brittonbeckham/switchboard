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
        public required HudPress Press;   // which physical control was pressed
        public required string[] Mods;    // modifier abbreviations, e.g. ["CTRL","WIN"]
        public required string BaseKey;   // the base key (accent pill), may be ""
        public required string Title;     // what it's called
        public required string? Tag;      // small trailing note ("ghost", "key unknown")
        public long StartMs;
        public float CurrentY;
        public bool Placed;
    }

    private static readonly Color PillFill = Color.FromArgb(72, 74, 84);
    private static readonly Color PillText = Color.FromArgb(214, 217, 224);

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
    public void ShowKey(HudPress press, string[] mods, string baseKey, string title, string? tag)
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
            Press = press,
            Mods = mods,
            BaseKey = baseKey,
            Title = title,
            Tag = tag,
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

        // Blue box: mini icon of the physical control that was pressed.
        var box = new Rectangle(x + 14, y + 13, 50, 50);
        DrawControlBox(g, box, t.Press, alpha);

        var textX = x + 78;
        var textW = CardWidth - 78 - 12;
        using (var titleFont = new Font("Segoe UI Semibold", 12f))
        using (var titleBrush = new SolidBrush(Color.FromArgb(a, Primary)))
        using (var fmt = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            g.DrawString(t.Title, titleFont, titleBrush, new RectangleF(textX, y + 13, textW, 26), fmt);

        DrawChord(g, textX, y + 42, t, alpha);
    }

    /// <summary>Draws the mini 4×4 pad or 3-knob icon with the pressed control lit, plus a layer badge.</summary>
    private static void DrawControlBox(Graphics g, Rectangle box, HudPress press, float alpha)
    {
        var a = (int)(alpha * 255);
        using (var path = Rounded(box, 11))
        using (var fill = new SolidBrush(Color.FromArgb(a, CapFill)))
            g.FillPath(fill, path);

        using var dim = new SolidBrush(Color.FromArgb((int)(alpha * 90), 255, 255, 255));
        using var lit = new SolidBrush(Color.FromArgb(a, 255, 255, 255));

        if (press.Kind == HudControlKind.Knob)
        {
            // Two small knobs on top, one big below — the KB16 cluster.
            void Knob(int cx, int cy, int r, bool on) =>
                g.FillEllipse(on ? lit : dim, cx - r, cy - r, r * 2, r * 2);
            Knob(box.X + 16, box.Y + 16, 5, press.Enc == 0);
            Knob(box.X + 34, box.Y + 16, 5, press.Enc == 1);
            Knob(box.X + 25, box.Y + 35, 7, press.Enc == 2);
        }
        else
        {
            // 4×4 grid; light the pressed cell (unless unknown).
            const int cell = 7, gap = 3;
            var gridSize = 4 * cell + 3 * gap;
            var gx = box.X + (box.Width - gridSize) / 2;
            var gy = box.Y + (box.Height - gridSize) / 2;
            var faded = press.Kind == HudControlKind.Unknown;
            for (var r = 0; r < 4; r++)
            {
                for (var c = 0; c < 4; c++)
                {
                    var on = !faded && r == press.Row && c == press.Col;
                    var rect = new Rectangle(gx + c * (cell + gap), gy + r * (cell + gap), cell, cell);
                    using var path = Rounded(rect, 2);
                    g.FillPath(on ? lit : dim, path);
                }
            }
        }

        // Layer badge — nudged clear of the top-right cell.
        if (press.Layer is int layer && layer > 0)
        {
            var d = 19;
            var bx = box.Right - 5;
            var by = box.Y - 5;
            var badge = new Rectangle(bx, by, d, d);
            using var bg = new SolidBrush(Color.FromArgb(a, 27, 27, 32));
            using var ring = new Pen(Color.FromArgb(a, Card), 2f);
            g.FillEllipse(bg, badge);
            g.DrawEllipse(ring, badge);
            using var badgeFont = new Font("Segoe UI Semibold", 8.5f);
            using var badgeText = new SolidBrush(Color.FromArgb(a, 255, 255, 255));
            using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(layer.ToString(), badgeFont, badgeText, badge, center);
        }
    }

    /// <summary>Draws the sent keystroke: modifier pills (gray) + base-key pill (accent) + optional tag.</summary>
    private static void DrawChord(Graphics g, float x, float y, Toast t, float alpha)
    {
        var a = (int)(alpha * 255);
        using var pillFont = new Font("Segoe UI Semibold", 7.25f);
        using var modFill = new SolidBrush(Color.FromArgb((int)(alpha * 235), PillFill));
        using var keyFill = new SolidBrush(Color.FromArgb(a, CapFill));
        using var pillText = new SolidBrush(Color.FromArgb(a, PillText));
        using var keyText = new SolidBrush(Color.FromArgb(a, 255, 255, 255));
        using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

        void Pill(string text, Brush fill, Brush textBrush)
        {
            var w = (int)g.MeasureString(text, pillFont).Width + 12;
            var pill = new Rectangle((int)x, (int)y, w, 17);
            using (var path = Rounded(pill, 6)) g.FillPath(fill, path);
            g.DrawString(text, pillFont, textBrush, pill, center);
            x += w + 5;
        }

        foreach (var mod in t.Mods) Pill(mod, modFill, pillText);
        if (t.BaseKey.Length > 0) Pill(t.BaseKey, keyFill, keyText);

        if (!string.IsNullOrEmpty(t.Tag))
        {
            using var tagFont = new Font("Segoe UI", 8.5f);
            using var tagBrush = new SolidBrush(Color.FromArgb((int)(alpha * 190), Secondary));
            g.DrawString(t.Tag, tagFont, tagBrush, new PointF(x + 1, y + 1));
        }
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

/// <summary>Which physical control the HUD should draw, and where it lit up.</summary>
internal enum HudControlKind { KeyGrid, Knob, Unknown }

/// <summary>The pressed control: a grid cell (Row/Col 0–3), a knob (Enc 0–2), or unknown; plus its layer.</summary>
internal readonly record struct HudPress(HudControlKind Kind, int Row, int Col, int Enc, int? Layer);

[ComImport]
[Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVirtualDesktopManager
{
    [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr hwnd, out int onCurrentDesktop);
    [PreserveSig] int GetWindowDesktopId(IntPtr hwnd, out Guid desktopId);
    [PreserveSig] int MoveWindowToDesktop(IntPtr hwnd, ref Guid desktopId);
}
