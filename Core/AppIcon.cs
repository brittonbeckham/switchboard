using System.Drawing.Drawing2D;

namespace Switchboard.Core;

/// <summary>
/// Draws Switchboard's app-mark icon at runtime: a 2×2 keypad, one key accent-lit
/// (or every key red with a slash when the mic is muted). The single source of
/// truth for the tray icon and every window's taskbar icon, so they can't drift
/// apart into two different-looking icons.
/// </summary>
public static class AppIcon
{
    public static Icon Create(bool micMuted = false)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var accent = new SolidBrush(Color.FromArgb(77, 163, 255));
            using var dim = new SolidBrush(Color.FromArgb(106, 106, 114));
            using var red = new SolidBrush(Color.FromArgb(224, 60, 60));

            var keys = new[]
            {
                new Rectangle(3, 3, 12, 12),
                new Rectangle(17, 3, 12, 12),
                new Rectangle(3, 17, 12, 12),
                new Rectangle(17, 17, 12, 12),
            };
            for (var i = 0; i < keys.Length; i++)
            {
                var brush = micMuted ? red : i == 0 ? accent : dim;
                FillRoundedRect(g, brush, keys[i], 3);
            }
            if (micMuted)
            {
                using var white = new Pen(Color.White, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(white, 5, 27, 27, 5);
            }
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static void FillRoundedRect(Graphics g, Brush brush, Rectangle rect, int radius)
    {
        using var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }
}
