namespace Switchboard.Core;

/// <summary>
/// Library of well-known Windows key chords, keyed by the exact decoded chord
/// text KeycodeName produces (modifier order: Ctrl, Shift, Alt, Win).
/// Authoritative = a Windows system chord whose meaning is certain; the label
/// replaces the chord in the UI. Non-authoritative = strong convention (apps
/// may differ); the label is shown alongside the chord.
/// </summary>
public static class KnownChords
{
    /// <summary>Every known chord, flattened for search indexing.</summary>
    public static IEnumerable<(string Chord, string Label, bool Authoritative)> AllEntries()
    {
        foreach (var kv in System) yield return (kv.Key, kv.Value, true);
        foreach (var kv in Conventional) yield return (kv.Key, kv.Value, false);
    }

    public static bool TryGet(string chord, out string label, out bool authoritative)
    {
        if (System.TryGetValue(chord, out label!))
        {
            authoritative = true;
            return true;
        }
        if (Conventional.TryGetValue(chord, out label!))
        {
            authoritative = false;
            return true;
        }
        authoritative = false;
        return false;
    }

    private static readonly Dictionary<string, string> System = new()
    {
        ["Win+D"] = "Show desktop",
        ["Win+M"] = "Minimize all",
        ["Shift+Win+M"] = "Restore minimized",
        ["Win+E"] = "File Explorer",
        ["Win+L"] = "Lock PC",
        ["Win+R"] = "Run dialog",
        ["Win+I"] = "Windows Settings",
        ["Win+S"] = "Search",
        ["Win+A"] = "Quick settings",
        ["Win+N"] = "Notifications",
        ["Win+X"] = "Quick link menu",
        ["Win+Tab"] = "Task view",
        ["Win+P"] = "Project / displays",
        ["Win+K"] = "Cast devices",
        ["Win+U"] = "Accessibility",
        ["Win+G"] = "Game Bar",
        ["Win+H"] = "Voice typing",
        ["Win+."] = "Emoji panel",
        ["Win+V"] = "Clipboard history",
        ["Shift+Win+S"] = "Screen snip",
        ["Win+Print Screen"] = "Screenshot to file",
        ["Win+←"] = "Snap window left",
        ["Win+→"] = "Snap window right",
        ["Win+↑"] = "Maximize window",
        ["Win+↓"] = "Minimize / restore",
        ["Ctrl+Win+←"] = "Previous desktop",
        ["Ctrl+Win+→"] = "Next desktop",
        ["Ctrl+Win+D"] = "New desktop",
        ["Ctrl+Win+F4"] = "Close desktop",
        ["Alt+Tab"] = "App switcher",
        ["Alt+F4"] = "Close app",
        ["Ctrl+Shift+Esc"] = "Task Manager",
        ["Win+Home"] = "Minimize others",
        ["Ctrl+Shift+Win+B"] = "Restart GPU driver",
    };

    private static readonly Dictionary<string, string> Conventional = new()
    {
        ["Ctrl+C"] = "Copy",
        ["Ctrl+V"] = "Paste",
        ["Ctrl+X"] = "Cut",
        ["Ctrl+A"] = "Select all",
        ["Ctrl+Z"] = "Undo",
        ["Ctrl+Y"] = "Redo",
        ["Ctrl+S"] = "Save",
        ["Ctrl+P"] = "Print",
        ["Ctrl+F"] = "Find",
        ["Ctrl+N"] = "New",
        ["Ctrl+O"] = "Open",
        ["Ctrl+W"] = "Close tab",
        ["Ctrl+T"] = "New tab",
        ["Ctrl+Shift+T"] = "Reopen closed tab",
        ["Ctrl+Tab"] = "Next tab",
        ["Ctrl+Shift+Tab"] = "Previous tab",
        ["Ctrl+B"] = "Bold",
        ["Ctrl+I"] = "Italic",
        ["Ctrl+U"] = "Underline",
        ["Ctrl+Shift+M"] = "Toggle mic (apps)",
        ["Ctrl+Shift+N"] = "New window (private)",
        ["F2"] = "Rename",
        ["F5"] = "Refresh",
        ["F11"] = "Full screen",
        ["Alt+Enter"] = "Properties",
        ["Ctrl+Home"] = "Jump to start",
        ["Ctrl+End"] = "Jump to end",
    };
}
