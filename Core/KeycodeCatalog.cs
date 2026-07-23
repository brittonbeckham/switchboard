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

    /// <summary>Windows virtual key a basic QMK keycode produces, or null if unmapped.</summary>
    public static ushort? BasicToVk(byte code) => code switch
    {
        >= 0x04 and <= 0x1D => (ushort)(0x41 + (code - 0x04)),   // A–Z
        >= 0x1E and <= 0x26 => (ushort)(0x31 + (code - 0x1E)),   // 1–9
        0x27 => 0x30,                                            // 0
        0x28 => 0x0D,                                            // Enter
        0x29 => 0x1B,                                            // Esc
        0x2A => 0x08,                                            // Backspace
        0x2B => 0x09,                                            // Tab
        0x2C => 0x20,                                            // Space
        >= 0x3A and <= 0x45 => (ushort)(0x70 + (code - 0x3A)),   // F1–F12
        >= 0x68 and <= 0x73 => (ushort)(0x7C + (code - 0x68)),   // F13–F24
        0x49 => 0x2D,                                            // Insert
        0x4A => 0x24,                                            // Home
        0x4B => 0x21,                                            // Page Up
        0x4C => 0x2E,                                            // Delete
        0x4D => 0x23,                                            // End
        0x4E => 0x22,                                            // Page Down
        0x4F => 0x27,                                            // Right
        0x50 => 0x25,                                            // Left
        0x51 => 0x28,                                            // Down
        0x52 => 0x26,                                            // Up
        0x2D => 0xBD,                                            // -
        0x2E => 0xBB,                                            // =
        0x2F => 0xDB,                                            // [
        0x30 => 0xDD,                                            // ]
        0x31 => 0xDC,                                            // \
        0x33 => 0xBA,                                            // ;
        0x34 => 0xDE,                                            // '
        0x35 => 0xC0,                                            // ` (grave)
        0x36 => 0xBC,                                            // ,
        0x37 => 0xBE,                                            // .
        0x38 => 0xBF,                                            // /
        _ => null,
    };

    /// <summary>Our modifier bits (Ctrl 1, Shift 2, Alt 4, Win 8) for a modifier VK, or 0.</summary>
    public static int ModBitForVk(ushort vk) => vk switch
    {
        0x11 or 0xA2 or 0xA3 => 1, // Ctrl / LCtrl / RCtrl
        0x10 or 0xA0 or 0xA1 => 2, // Shift / LShift / RShift
        0x12 or 0xA4 or 0xA5 => 4, // Alt / LAlt / RAlt
        0x5B or 0x5C => 8,         // LWin / RWin
        _ => 0,
    };

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
