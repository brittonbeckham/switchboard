using System.Runtime.InteropServices;

namespace Switchboard.Core.CustomActions;

/// <summary>Synthesizes keystrokes via SendInput — drives a foregrounded window
/// (typing a slash command into Teams/Slack) without touching the mouse.</summary>
internal static class KeystrokeSender
{
    // Only the ones ActionStepClearField needs directly; every other key comes
    // from a name via MegalodonPad.BasicCodeFromName + KeycodeCatalog.BasicToVk —
    // one source of truth for key names, not a second copy of constants here.
    public const ushort VK_DELETE = 0x2E;
    public const ushort VK_A = 0x41;

    /// <summary>Presses a key, optionally with Ctrl/Shift/Alt held — the general
    /// building block a step-based action chains together.</summary>
    public static void PressChord(ushort vk, bool ctrl = false, bool shift = false, bool alt = false, bool win = false)
    {
        var down = new List<INPUT>();
        var up = new List<INPUT>();
        if (ctrl) { down.Add(KeyInput(VK_CONTROL, false)); up.Insert(0, KeyInput(VK_CONTROL, true)); }
        if (shift) { down.Add(KeyInput(VK_SHIFT, false)); up.Insert(0, KeyInput(VK_SHIFT, true)); }
        if (alt) { down.Add(KeyInput(VK_ALT, false)); up.Insert(0, KeyInput(VK_ALT, true)); }
        if (win) { down.Add(KeyInput(VK_LWIN, false)); up.Insert(0, KeyInput(VK_LWIN, true)); }
        down.Add(KeyInput(vk, false));
        down.Add(KeyInput(vk, true));
        SendInput([.. down, .. up]);
    }

    public static void PressKey(ushort vk) => SendInput([KeyInput(vk, false), KeyInput(vk, true)]);

    /// <summary>Presses a key down without releasing it — for holding a modifier
    /// across multiple separate action invocations (e.g. a knob-driven Alt-Tab
    /// switcher, where Alt stays down across several turns).</summary>
    public static void KeyDown(ushort vk) => SendInput([KeyInput(vk, false)]);

    /// <summary>Releases a key previously held with <see cref="KeyDown"/>.</summary>
    public static void KeyUp(ushort vk) => SendInput([KeyInput(vk, true)]);


    /// <summary>Types literal text via Unicode packet input — works regardless of keyboard layout.</summary>
    public static void TypeText(string text)
    {
        var inputs = new List<INPUT>();
        foreach (var ch in text)
        {
            inputs.Add(UnicodeInput(ch, false));
            inputs.Add(UnicodeInput(ch, true));
        }
        SendInput(inputs.ToArray());
    }

    public const ushort VK_TAB = 0x09;
    public const ushort VK_SHIFT = 0x10;
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_ALT = 0x12;
    public const ushort VK_LWIN = 0x5B;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const int INPUT_KEYBOARD = 1;

    private static void SendInput(INPUT[] inputs) => SendInputNative((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    private static INPUT KeyInput(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } },
    };

    private static INPUT UnicodeInput(char ch, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) } },
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion u;
    }

    // Must include mi/hi, not just ki — the union's size (and therefore the whole
    // INPUT struct's size) has to match Windows' real layout, or the cbSize we
    // pass to SendInput won't match what it validates against and the call is
    // silently rejected: no exception, no error, just zero keystrokes delivered.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static extern uint SendInputNative(uint nInputs, INPUT[] pInputs, int cbSize);
}
