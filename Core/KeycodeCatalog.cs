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

    /// <summary>True if this code is F13–F24, optionally wrapped in modifiers (e.g. Ctrl+F17).</summary>
    public static bool IsGhostKey(ushort code, out int functionKeyNumber) =>
        IsGhostKey(code, out functionKeyNumber, out _);

    public static bool IsGhostKey(ushort code, out int functionKeyNumber, out int modBits)
    {
        functionKeyNumber = 0;
        modBits = 0;
        byte baseByte;
        if (code is >= 0x0100 and <= 0x1FFF) { modBits = (code >> 8) & 0x1F; baseByte = (byte)(code & 0xFF); }
        else if (code <= 0xFF) baseByte = (byte)code;
        else return false;
        if (baseByte is < 0x68 or > 0x73) return false;
        functionKeyNumber = baseByte - 0x68 + 13;
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
        0xE0 => 0xA2,                                             // Left Ctrl
        0xE1 => 0xA0,                                             // Left Shift
        0xE2 => 0xA4,                                             // Left Alt
        0xE3 => 0x5B,                                             // Left Win
        0xE4 => 0xA3,                                             // Right Ctrl
        0xE5 => 0xA1,                                             // Right Shift
        0xE6 => 0xA5,                                             // Right Alt
        0xE7 => 0x5C,                                             // Right Win
        0x46 => 0x2C,                                              // Print Screen
        0xA6 => 0x5F,                                              // System Sleep
        0xA8 => 0xAD,                                              // Mute (speakers)
        0xA9 => 0xAF,                                              // Volume Up
        0xAA => 0xAE,                                              // Volume Down
        0xAB => 0xB0,                                              // Next Track
        0xAC => 0xB1,                                              // Previous Track
        0xAD => 0xB2,                                              // Media Stop
        0xAE => 0xB3,                                              // Play / Pause
        0xAF => 0xB5,                                              // Media Select
        // System Power/Wake and Eject/Fast-Forward/Rewind have no synthesizable
        // Windows VK — those are ACPI/driver-level events, not real keystrokes.
        _ => null,
    };

    private static readonly Lazy<Dictionary<ushort, string>> VkToBasicName = new(() =>
    {
        var map = new Dictionary<ushort, string>();
        for (var b = 0; b <= 0xFF; b++)
        {
            if (BasicToVk((byte)b) is not ushort vk) continue;
            map.TryAdd(vk, MegalodonPad.KeycodeName((ushort)b));
        }
        return map;
    });

    /// <summary>Reverse of <see cref="BasicToVk"/>: the same display name
    /// MegalodonPad/BasicCodeFromName uses for a Windows VK, if any — lets a
    /// live-captured keystroke (e.g. from a "record a key" control) round-trip
    /// back through the same name vocabulary the rest of the app already uses.</summary>
    public static string? NameForVk(ushort vk) => VkToBasicName.Value.GetValueOrDefault(vk);

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
            new Group("Modifiers (held while physically pressed)",
            [
                E(0xE0), E(0xE1), E(0xE2), E(0xE3), // Left Ctrl/Shift/Alt/Win
                E(0xE4), E(0xE5), E(0xE6), E(0xE7), // Right Ctrl/Shift/Alt/Win
            ]),
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
