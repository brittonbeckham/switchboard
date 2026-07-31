namespace Switchboard.UI;

/// <summary>
/// The single dark palette/typography/metric system every Switchboard window
/// draws from. Change a color here, it changes everywhere — no page should
/// declare its own one-off colors. See docs/style-guide.md for the full guide.
/// </summary>
internal static class Theme
{
    // Backgrounds
    public static readonly Color Bg = Color.FromArgb(23, 25, 29);          // outer window background
    public static readonly Color Panel = Color.FromArgb(30, 33, 38);       // window chrome, dialog body
    public static readonly Color PanelAlt = Color.FromArgb(36, 39, 45);    // cards, input fields
    public static readonly Color Rail = Color.FromArgb(26, 28, 32);        // nav rail background
    public static readonly Color LogBg = Color.FromArgb(15, 17, 20);       // log/terminal-style boxes

    // Text
    public static readonly Color Ink = Color.FromArgb(237, 239, 242);      // primary text
    public static readonly Color Subtle = Color.FromArgb(154, 161, 172);   // secondary/caption text
    public static readonly Color Faint = Color.FromArgb(91, 98, 112);      // timestamps, disabled text

    // Lines
    public static readonly Color Line = Color.FromArgb(44, 47, 53);        // borders, dividers

    // Accent
    public static readonly Color Accent = Color.FromArgb(76, 154, 255);
    public static readonly Color AccentSoft = Color.FromArgb(27, 42, 62);  // accent-tinted fill (selected nav, selected card)
    public static readonly Color AccentText = Color.White;                 // text/icon color ON a solid accent fill

    // Semantic (pending/drag states, reused from the pad editor)
    public static readonly Color PendingFill = Color.FromArgb(58, 46, 20);
    public static readonly Color PendingBorder = Color.FromArgb(214, 148, 46);
    public static readonly Color PendingText = Color.FromArgb(240, 190, 110);
    public static readonly Color DragMoveBorder = Color.FromArgb(52, 199, 89);
    public static readonly Color DragSwapBorder = Color.FromArgb(185, 140, 255);
    public static readonly Color Danger = Color.FromArgb(232, 17, 35);

    // Type
    public static Font Display => new("Segoe UI Variable", 14f, FontStyle.Bold);
    public static Font Title => new("Segoe UI Variable", 12.5f, FontStyle.Bold);
    public static Font Body => new("Segoe UI", 9.75f);
    public static Font BodySemibold => new("Segoe UI Semibold", 9.75f);
    public static Font Caption => new("Segoe UI", 8.5f);
    public static Font CaptionSemibold => new("Segoe UI Semibold", 8f);
    public static Font Mono => new("Cascadia Mono", 8.75f);

    // Metrics
    public const int RadiusWindow = 12;
    public const int RadiusCard = 10;
    public const int RadiusControl = 7;
    public const int TitleBarHeight = 40;
    public const int RailWidth = 200;

    /// <summary>Dark-themes a ContextMenuStrip and every item already added to it —
    /// call after populating Items, since color alone (without a renderer) leaves
    /// the native light selection highlight/border.</summary>
    public static void ApplyDarkMenu(ContextMenuStrip menu)
    {
        menu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());
        menu.BackColor = Panel;
        menu.ForeColor = Ink;
        menu.Font = Body;
        foreach (ToolStripItem item in menu.Items)
        {
            item.ForeColor = Ink;
            if (item is ToolStripMenuItem sub) ApplyDarkMenuItems(sub.DropDownItems);
        }
    }

    private static void ApplyDarkMenuItems(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items) item.ForeColor = Ink;
    }

    private sealed class DarkMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Panel;
        public override Color ImageMarginGradientBegin => Panel;
        public override Color ImageMarginGradientMiddle => Panel;
        public override Color ImageMarginGradientEnd => Panel;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Accent;
        public override Color MenuItemSelected => AccentSoft;
        public override Color MenuItemSelectedGradientBegin => AccentSoft;
        public override Color MenuItemSelectedGradientEnd => AccentSoft;
        public override Color MenuItemPressedGradientBegin => AccentSoft;
        public override Color MenuItemPressedGradientEnd => AccentSoft;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Line;
    }
}
