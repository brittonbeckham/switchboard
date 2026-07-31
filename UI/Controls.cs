using System.Drawing.Drawing2D;

namespace Switchboard.UI;

/// <summary>Shared paint helpers for the custom-drawn controls below.</summary>
internal static class ControlPaint2
{
    /// <summary>Blends a color toward another by <paramref name="amount"/> (0 = no
    /// change, 1 = fully <paramref name="toward"/>) — used to render a dimmed,
    /// disabled look without a separate disabled-color palette per control.</summary>
    public static Color Blend(Color color, Color toward, double amount) => Color.FromArgb(
        color.A,
        (int)(color.R + (toward.R - color.R) * amount),
        (int)(color.G + (toward.G - color.G) * amount),
        (int)(color.B + (toward.B - color.B) * amount));
}

/// <summary>A modern on/off switch — WinForms' native CheckBox can't be restyled
/// to match the dark theme, so this is a fully custom-drawn replacement.</summary>
internal sealed class ToggleSwitch : Control
{
    private bool _checked;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
        }
    }

    public event EventHandler? CheckedChanged;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(38, 22);
        Cursor = Cursors.Hand;
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Checked = !Checked;
        CheckedChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new Rectangle(0, 0, Width - 1, Height - 1);
        var trackColor = Checked ? Theme.Accent : Theme.Line;
        var knobColor = Color.White;
        if (!Enabled)
        {
            trackColor = ControlPaint2.Blend(trackColor, Theme.Panel, 0.55);
            knobColor = ControlPaint2.Blend(knobColor, Theme.Panel, 0.4);
        }
        using (var path = Rounded(track, Height / 2))
        using (var fill = new SolidBrush(trackColor))
            g.FillPath(fill, path);

        var knobD = Height - 4;
        var knobX = Checked ? Width - knobD - 2 : 2;
        using var knobBrush = new SolidBrush(knobColor);
        g.FillEllipse(knobBrush, knobX, 2, knobD, knobD);
    }

    private static GraphicsPath Rounded(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>A modern range slider — replaces WinForms' native TrackBar, which
/// can't be restyled and always renders with the light-theme system groove.</summary>
internal sealed class Slider : Control
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Minimum { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Maximum { get; set; } = 100;

    private int _value;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(value, Minimum, Maximum);
            if (clamped == _value) return;
            _value = clamped;
            Invalidate();
        }
    }

    public event EventHandler? ValueChanged;

    public Slider()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 22;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Capture = true;
        SetFromX(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.Button == MouseButtons.Left) SetFromX(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        Capture = false;
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    private void SetFromX(int x)
    {
        var usable = Math.Max(1, Width - 16);
        var frac = Math.Clamp((x - 8) / (double)usable, 0, 1);
        var newValue = Minimum + (int)Math.Round(frac * (Maximum - Minimum));
        if (newValue == Value) return;
        Value = newValue;
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var midY = Height / 2;
        var track = new Rectangle(8, midY - 2, Math.Max(1, Width - 16), 4);
        var fillColor = Theme.Accent;
        if (!Enabled) fillColor = ControlPaint2.Blend(fillColor, Theme.Panel, 0.55);

        using (var path = Rounded(track, 2))
        using (var fill = new SolidBrush(Theme.Line))
            g.FillPath(fill, path);

        var frac = Maximum > Minimum ? (Value - Minimum) / (double)(Maximum - Minimum) : 0;
        var fillWidth = Math.Max(4, (int)(track.Width * frac));
        var fillRect = new Rectangle(track.X, track.Y, fillWidth, track.Height);
        using (var path = Rounded(fillRect, 2))
        using (var fill = new SolidBrush(fillColor))
            g.FillPath(fill, path);

        var thumbX = track.X + fillWidth;
        using var thumbBrush = new SolidBrush(fillColor);
        using var thumbRing = new Pen(Theme.Panel, 2);
        g.FillEllipse(thumbBrush, thumbX - 8, midY - 8, 16, 16);
        g.DrawEllipse(thumbRing, thumbX - 8, midY - 8, 16, 16);
    }

    private static GraphicsPath Rounded(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>The pad's layer readout, styled after its own onboard OLED: digits
/// 0..N-1 left to right on a black LCD field, the current one drawn as an
/// inverted (filled) pill, with prev/next chevrons clustered together on the
/// right — replaces a row of tabs with something that reads like the device
/// itself.</summary>
internal sealed class LayerLcd : Control
{
    private int _layerCount = 1;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int LayerCount
    {
        get => _layerCount;
        set { _layerCount = Math.Max(1, value); Invalidate(); }
    }

    private int _currentLayer;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int CurrentLayer
    {
        get => _currentLayer;
        set { _currentLayer = value; Invalidate(); }
    }

    /// <summary>Fired with the requested layer index — a digit click jumps there
    /// directly, a chevron click requests current ± 1 (wrapping).</summary>
    public event Action<int>? LayerRequested;

    private const int ChevronZoneWidth = 40;

    public LayerLcd()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(150, 32);
        Cursor = Cursors.Hand;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var bg = new SolidBrush(Color.FromArgb(6, 9, 13)))
        using (var path = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 6))
            g.FillPath(bg, path);

        var digitsWidth = Width - ChevronZoneWidth;
        var cellWidth = digitsWidth / (float)_layerCount;
        using var digitFont = new Font("Consolas", 10f, FontStyle.Bold);

        for (var i = 0; i < _layerCount; i++)
        {
            // Displayed 1-based (matches the pad's own OLED); everything else
            // (LayerCount, CurrentLayer, click mapping) stays 0-based internally.
            var label = (i + 1).ToString();
            var cellRect = new RectangleF(i * cellWidth, 0, cellWidth, Height);
            if (i == _currentLayer)
            {
                var pillRect = Rectangle.Round(RectangleF.Inflate(cellRect, -4, -5));
                using var pillPath = Rounded(pillRect, 5);
                using var pillBrush = new SolidBrush(Theme.Accent);
                g.FillPath(pillBrush, pillPath);
                TextRenderer.DrawText(g, label, digitFont, Rectangle.Round(cellRect), Color.Black,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            else
            {
                TextRenderer.DrawText(g, label, digitFont, Rectangle.Round(cellRect), Theme.Accent,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        using var chevronFont = new Font("Segoe UI", 10f, FontStyle.Bold);
        var half = ChevronZoneWidth / 2;
        var prevRect = new Rectangle((int)digitsWidth, 0, half, Height);
        var nextRect = new Rectangle((int)digitsWidth + half, 0, ChevronZoneWidth - half, Height);
        TextRenderer.DrawText(g, "‹", chevronFont, prevRect, Theme.Accent,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, "›", chevronFont, nextRect, Theme.Accent,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        var digitsWidth = Width - ChevronZoneWidth;
        if (e.X >= digitsWidth)
        {
            var half = ChevronZoneWidth / 2;
            var delta = e.X < digitsWidth + half ? -1 : 1;
            var next = ((_currentLayer + delta) % _layerCount + _layerCount) % _layerCount;
            LayerRequested?.Invoke(next);
        }
        else
        {
            var cellWidth = digitsWidth / (float)_layerCount;
            var index = Math.Clamp((int)(e.X / cellWidth), 0, _layerCount - 1);
            LayerRequested?.Invoke(index);
        }
    }

    private static GraphicsPath Rounded(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
