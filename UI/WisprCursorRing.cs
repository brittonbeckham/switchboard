using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Switchboard.UI;

/// <summary>
/// Click-through layered ring centered on the text caret, with an Auto submit
/// checkbox beside it. Pulses and color-waves while Wispr Flow is dictating.
/// The checkbox is the only hit-tested region; everything else passes clicks through.
/// </summary>
internal sealed class WisprCursorRing : Form
{
    private const int BreathPeriodMs = 1600;
    private const int WavePeriodMs = 2400;
    private const int MinDiameter = 48;
    private const int MaxDiameter = 160;
    private const float RingThickness = 5.5f;
    private const int ArcSegments = 64;
    private const string StatusText = "Auto submit";
    private const int LabelGap = 10;
    private const int WmNchittest = 0x0084;
    private static readonly IntPtr HtTransparent = new(-1);
    private static readonly IntPtr HtClient = new(1);

    private readonly System.Windows.Forms.Timer _anim = new() { Interval = 16 };
    private readonly IVirtualDesktopManager? _vdm;
    private int _diameter;
    private int _labelWidth;
    private bool _labelOnLeft = true;
    private bool _active;
    private int _lastX, _lastY;
    private bool _havePoint;
    private int _misses;
    private IntPtr _lastFg;
    private bool _autoSubmit;
    private RectangleF _checkboxHit = RectangleF.Empty;

    public WisprCursorRing()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        _diameter = Scale(72);
        _labelWidth = MeasureLabelWidth();
        Size = new Size(ContentWidth, _diameter);

        _anim.Tick += (_, _) => Frame();

        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a"));
            if (type != null) _vdm = (IVirtualDesktopManager?)Activator.CreateInstance(type);
        }
        catch
        {
            _vdm = null;
        }
    }

    /// <summary>Whether Enter should follow a normal finish+paste.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AutoSubmit
    {
        get => _autoSubmit;
        set
        {
            if (_autoSubmit == value) return;
            _autoSubmit = value;
            if (_active) Render();
            AutoSubmitChanged?.Invoke(value);
        }
    }

    public event Action<bool>? AutoSubmitChanged;

    private int ContentWidth => _labelWidth + LabelGap + _diameter;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // WS_EX_TOPMOST | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            // (No WS_EX_TRANSPARENT — we hit-test only the checkbox via WM_NCHITTEST.)
            cp.ExStyle |= 0x8 | 0x80000 | 0x8000000 | 0x80;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmNchittest)
        {
            var screen = new Point(m.LParam.ToInt32() & 0xFFFF, (m.LParam.ToInt32() >> 16) & 0xFFFF);
            // Handle signed coords on multi-monitor (negative X/Y).
            if ((m.LParam.ToInt32() & 0xFFFF) > 32767) screen.X -= 65536;
            if (((m.LParam.ToInt32() >> 16) & 0xFFFF) > 32767) screen.Y -= 65536;
            var client = new Point(screen.X - Left, screen.Y - Top);
            m.Result = _checkboxHit.Contains(client) ? HtClient : HtTransparent;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        if (!_checkboxHit.Contains(e.Location)) return;
        AutoSubmit = !AutoSubmit;
    }

    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        _havePoint = false;
        _misses = 0;
        if (active)
        {
            if (!Visible) Show();
            _anim.Start();
            Frame();
        }
        else
        {
            _anim.Stop();
            if (Visible) Hide();
        }
    }

    private void Frame()
    {
        if (!_active) return;

        // Alt-tab / click-back: drop the old point so we don't keep painting on a
        // stale sidebar hit while the real caret is in the chat box.
        var fg = GetForegroundWindow();
        if (fg != IntPtr.Zero && fg != Handle && fg != _lastFg)
        {
            _lastFg = fg;
            if (!TextCaretLocator.ForegroundIsWispr())
            {
                _havePoint = false;
                _misses = 0;
            }
        }

        if (TextCaretLocator.TryGet(out var x, out var y, out var caretHeight, out var stale))
        {
            // Ignore soft-hold readings from the Wispr overlay window itself.
            if (!stale || !_havePoint)
            {
                _lastX = x;
                _lastY = y;
                _havePoint = true;
                _misses = 0;
            }

            var target = Math.Clamp(caretHeight * 3, MinDiameter, MaxDiameter);
            if (Math.Abs(target - _diameter) >= 4)
            {
                _diameter = target;
                Size = new Size(ContentWidth, _diameter);
            }
        }
        else
        {
            _misses++;
            // Drop faster when Cursor/etc is focused but caret isn't readable yet.
            var limit = TextCaretLocator.ForegroundIsWispr() ? 12 : 4;
            if (_misses > limit)
                _havePoint = false;
        }

        if (!_havePoint)
        {
            if (Visible) Hide();
            return;
        }

        if (!Visible) Show();

        var half = _diameter / 2;
        // Prefer label on the left of the caret; flip right if it would hang off-screen.
        var leftEdge = _lastX - half - LabelGap - _labelWidth;
        _labelOnLeft = leftEdge >= SystemInformation.VirtualScreen.Left + 4;
        Location = _labelOnLeft
            ? new Point(_lastX - half - LabelGap - _labelWidth, _lastY - half)
            : new Point(_lastX - half, _lastY - half);

        FollowActiveDesktop();
        Render();
    }

    private void FollowActiveDesktop()
    {
        SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        if (_vdm == null) return;
        try
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == Handle) return;
            if (_vdm.GetWindowDesktopId(fg, out var target) != 0 || target == Guid.Empty) return;
            if (_vdm.GetWindowDesktopId(Handle, out var mine) == 0 && mine == target) return;
            _vdm.MoveWindowToDesktop(Handle, ref target);
            SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        }
        catch
        {
            // Transient COM failures during a desktop switch.
        }
    }

    private void Render()
    {
        var now = Environment.TickCount64;
        var breathPhase = (now % BreathPeriodMs) / (float)BreathPeriodMs;
        var breath = 0.5f + 0.5f * MathF.Sin(breathPhase * MathF.Tau);
        var wave = (now % WavePeriodMs) / (float)WavePeriodMs;
        var inset = 6 + (int)(5 * (1f - breath));

        var w = ContentWidth;
        var h = _diameter;
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            var ringOriginX = _labelOnLeft ? _labelWidth + LabelGap : 0;
            var labelOriginX = _labelOnLeft ? 0 : _diameter + LabelGap;

            var cx = ringOriginX + _diameter / 2f;
            var cy = h / 2f;
            var radius = (_diameter - inset * 2) / 2f;

            DrawWaveRing(g, cx, cy, radius + 3f, RingThickness + 5f, wave, breath, 0.28f, 0.08f);
            DrawWaveRing(g, cx, cy, radius, RingThickness, wave, breath, 0.95f, 0f);
            DrawWaveRing(g, cx, cy, radius - 5f, RingThickness * 0.55f, wave + 0.18f, breath, 0.45f, 0.2f);

            DrawAutoSubmitControl(g, labelOriginX, h);
        }

        Premultiply(bmp);
        PushLayered(bmp);
    }

    private void DrawAutoSubmitControl(Graphics g, int originX, int canvasH)
    {
        var padX = Scale(8);
        var padY = Scale(5);
        var box = Scale(13);
        var gap = Scale(6);
        using var font = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        var textSize = g.MeasureString(StatusText, font);
        var pillW = padX + box + gap + textSize.Width + padX;
        var pillH = Math.Max(box, textSize.Height) + padY * 2;
        var pillX = originX + (_labelWidth - pillW);
        if (!_labelOnLeft) pillX = originX;
        var pillY = (canvasH - pillH) / 2f;
        var pill = new RectangleF(pillX, pillY, pillW, pillH);
        _checkboxHit = Rectangle.Inflate(Rectangle.Round(pill), 2, 2);

        // Flat chrome — no rainbow / breath; white outline checkbox.
        using (var path = Rounded(Rectangle.Round(pill), Scale(6)))
        {
            using var fill = new SolidBrush(Color.FromArgb(220, 28, 30, 36));
            using var border = new Pen(Color.FromArgb(200, 220, 220, 225), 1f);
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }

        var boxX = pillX + padX;
        var boxY = pillY + (pillH - box) / 2f;
        var boxRect = new RectangleF(boxX, boxY, box, box);
        using (var boxPath = Rounded(Rectangle.Round(boxRect), Scale(2)))
        {
            if (_autoSubmit)
            {
                using var fill = new SolidBrush(Color.FromArgb(235, 245, 246, 250));
                g.FillPath(fill, boxPath);
                using var checkPen = new Pen(Color.FromArgb(240, 28, 30, 36), DpiScale(2f))
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                    LineJoin = LineJoin.Round,
                };
                g.DrawLines(checkPen, new[]
                {
                    new PointF(boxX + box * 0.22f, boxY + box * 0.52f),
                    new PointF(boxX + box * 0.42f, boxY + box * 0.72f),
                    new PointF(boxX + box * 0.78f, boxY + box * 0.28f),
                });
            }
            else
            {
                using var empty = new Pen(Color.FromArgb(220, 235, 235, 240), 1.5f);
                g.DrawPath(empty, boxPath);
            }
        }

        using var textBrush = new SolidBrush(Color.FromArgb(235, 245, 246, 250));
        g.DrawString(StatusText, font, textBrush, boxX + box + gap, pillY + (pillH - textSize.Height) / 2f);
    }

    private int MeasureLabelWidth()
    {
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        using var font = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
        var text = g.MeasureString(StatusText, font);
        var box = Scale(13);
        var gap = Scale(6);
        var padX = Scale(8);
        return (int)Math.Ceiling(padX + box + gap + text.Width + padX) + 4;
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = Math.Max(2, radius) * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void DrawWaveRing(Graphics g, float cx, float cy, float radius, float thickness,
        float wave, float breath, float alphaScale, float inwardBias)
    {
        if (radius < 4 || thickness < 0.5f) return;

        var baseAlpha = (0.40f + 0.50f * breath) * alphaScale;
        for (var i = 0; i < ArcSegments; i++)
        {
            var t0 = i / (float)ArcSegments;
            var t1 = (i + 1) / (float)ArcSegments;
            var hue = (t0 + wave + inwardBias) % 1f;
            var crest = 0.55f + 0.45f * MathF.Sin((t0 - wave) * MathF.Tau);
            var a = (int)(Math.Clamp(baseAlpha * crest, 0f, 1f) * 255);
            if (a < 8) continue;

            var color = WaveColor(hue, 0.85f + 0.15f * crest, a);
            var ang0 = t0 * 360f - 90f;
            var sweep = (t1 - t0) * 360f + 0.6f;
            var rect = new RectangleF(cx - radius, cy - radius, radius * 2, radius * 2);
            using var pen = new Pen(color, thickness)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            g.DrawArc(pen, rect, ang0, sweep);
        }
    }

    private static Color WaveColor(float hue, float sat, int alpha)
    {
        hue = ((hue % 1f) + 1f) % 1f;
        sat = Math.Clamp(sat, 0f, 1f);
        const float val = 1f;
        var i = (int)(hue * 6f);
        var f = hue * 6f - i;
        var p = val * (1f - sat);
        var q = val * (1f - f * sat);
        var t = val * (1f - (1f - f) * sat);
        float r, g, b;
        switch (i % 6)
        {
            case 0: r = val; g = t; b = p; break;
            case 1: r = q; g = val; b = p; break;
            case 2: r = p; g = val; b = t; break;
            case 3: r = p; g = q; b = val; break;
            case 4: r = t; g = p; b = val; break;
            default: r = val; g = p; b = q; break;
        }
        return Color.FromArgb(alpha, (int)(r * 255), (int)(g * 255), (int)(b * 255));
    }

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
                BlendOp = 0,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = 1,
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

    private static int Scale(int logical)
    {
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        return (int)Math.Round(logical * g.DpiX / 96f);
    }

    private static float DpiScale(float logical)
    {
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        return logical * g.DpiX / 96f;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _anim.Stop();
            _anim.Dispose();
        }
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
