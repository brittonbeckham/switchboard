namespace Switchboard.Core;

/// <summary>
/// The pickable QMK keycodes for the assignment editor, grouped for display.
/// Display names come from MegalodonPad.KeycodeName so the picker and the pad
/// view can never disagree about what a code is called.
/// </summary>
public static class KeycodeCatalog
{
    public const ushort KC_NO = 0x0000;
    public const ushort KC_TRNS = 0x0001;

    public sealed record Entry(string Name, ushort Code);
    public sealed record Group(string Title, Entry[] Entries);

    public static readonly Group[] Groups = BuildGroups();

    /// <summary>Composes a modifier chord: mods bits (Ctrl 1, Shift 2, Alt 4, Win 8) around a basic key.</summary>
    public static ushort Chord(int modBits, ushort basicCode) =>
        (ushort)(((modBits & 0x1F) << 8) | (basicCode & 0xFF));

    public static ushort GoToLayer(int layer) => (ushort)(0x5200 + layer);
    public static ushort LayerWhileHeld(int layer) => (ushort)(0x5220 + layer);
    public static ushort ToggleLayer(int layer) => (ushort)(0x5260 + layer);

    /// <summary>F13–F24 codes, in order — the ghost keys.</summary>
    public static readonly ushort[] GhostKeys =
        Enumerable.Range(0, 12).Select(i => (ushort)(0x68 + i)).ToArray();

    public static bool IsGhostKey(ushort code, out int functionKeyNumber)
    {
        functionKeyNumber = 0;
        if (code is < 0x68 or > 0x73) return false;
        functionKeyNumber = code - 0x68 + 13;
        return true;
    }

    private static Group[] BuildGroups()
    {
        static Entry E(ushort code) => new(MegalodonPad.KeycodeName(code), code);
        static Entry[] Range(int from, int to) =>
            Enumerable.Range(from, to - from + 1).Select(c => E((ushort)c)).ToArray();

        return
        [
            new Group("Letters", Range(0x04, 0x1D)),
            new Group("Digits", Range(0x1E, 0x27)),
            new Group("Function", Range(0x3A, 0x45)),
            new Group("Ghost (F13–F24)", Range(0x68, 0x73)),
            new Group("Navigation",
            [
                E(0x29), E(0x2B), E(0x28), E(0x2C), E(0x2A), // Esc Tab Enter Space Backspace
                E(0x4F), E(0x50), E(0x51), E(0x52),          // arrows
                E(0x4A), E(0x4D), E(0x4B), E(0x4E),          // Home End PgUp PgDn
                E(0x49), E(0x4C), E(0x46), E(0x65),          // Ins Del PrtSc Menu
            ]),
            new Group("Media",
            [
                E(0xA8), E(0xA9), E(0xAA), E(0xAB), E(0xAC),
                E(0xAE), E(0xAD), E(0xB1), E(0xB2),
            ]),
            new Group("Punctuation",
            [
                E(0x2D), E(0x2E), E(0x2F), E(0x30), E(0x31),
                E(0x33), E(0x34), E(0x35), E(0x36), E(0x37), E(0x38),
            ]),
            new Group("Numpad", Range(0x54, 0x63)),
        ];
    }
}
