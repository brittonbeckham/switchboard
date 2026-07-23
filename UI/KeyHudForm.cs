using System.Runtime.InteropServices;

namespace Switchboard.UI;

/// <summary>
/// A small dark card that pops into the corner of the active screen when a
/// macropad key is pressed, showing the key's label and what it does, then
/// fades away. Never takes focus; click-through.
/// </summary>
internal sealed class KeyHudForm : Form
{
    private static readonly Color Card = Color.FromArgb(38, 38, 44);
    private static readonly Color CardBorder = Color.FromArgb(70, 72, 80);
    private static readonly Color CapFill = Color.FromArgb(0, 103, 192);
    private static readonly Color Primary = Color.White;
    private static readonly Color Secondary = Color.FromArgb(168, 172, 180);

    private readonly System.Windows.Forms.Timer _hold = new() { Interval = 1400 };
    private readonly System.Windows.Forms.Timer _fade = new() { Interval = 16 };

    private string _cap = "";
    private string _title = "";
    private string _subtitle = "";

    public KeyHudForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(300, 88);
        BackColor = Color.Black;
        TransparencyKey = Color.Black;
        Opacity = 0;
        DoubleBuffered = true;

        _hold.Tick += (_, _) => { _hold.Stop(); _fade.Start(); };
        _fade.Tick += (_, _) =>
        {
            Opacity -= 0.08;
            if (Opacity <= 0.01) { _fade.Stop(); Hide(); }
        };
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

    /// <summary>Shows the HUD for one keypress on the screen with the active window.</summary>
    public void Flash(string cap, string title, string subtitle)
    {
        _cap = cap;
        _title = title;
        _subtitle = subtitle;

        var screen = Screen.FromHandle(GetForegroundWindow());
        var area = screen.WorkingArea;
        Location = new Point(area.Right - Width - 20, area.Bottom - Height - 20);

        _hold.Stop();
        _fade.Stop();
        Opacity = 0.97;
        Invalidate();
        if (!Visible) Show();
        _hold.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var card = new Rectangle(2, 2, Width - 5, Height - 5);
        using (var path = Rounded(card, 12))
        {
            using var fill = new SolidBrush(Card);
            using var border = new Pen(CardBorder);
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }

        // Keycap on the left.
        var capRect = new Rectangle(16, 20, 48, 48);
        using (var path = Rounded(capRect, 8))
        {
            using var fill = new SolidBrush(CapFill);
            g.FillPath(fill, path);
        }
        using (var capFont = new Font("Segoe UI Semibold", _cap.Length > 2 ? 10f : 15f))
            TextRenderer.DrawText(g, _cap, capFont, capRect, Primary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        var textLeft = 80;
        var textWidth = Width - textLeft - 16;
        using (var titleFont = new Font("Segoe UI Semibold", 12f))
            TextRenderer.DrawText(g, _title, titleFont,
                new Rectangle(textLeft, 22, textWidth, 26), Primary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        using (var subFont = new Font("Segoe UI", 9f))
            TextRenderer.DrawText(g, _subtitle, subFont,
                new Rectangle(textLeft, 48, textWidth, 22), Secondary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private static System.Drawing.Drawing2D.GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hold.Dispose();
            _fade.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
