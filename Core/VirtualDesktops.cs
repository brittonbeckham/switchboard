using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Switchboard.Core;

/// <summary>
/// Switches Windows virtual desktops by index. Reads the desktop list and current
/// desktop from the registry (stable across Windows 10/11 builds, unlike the
/// undocumented COM interfaces), then replays Ctrl+Win+Left/Right the right
/// number of times.
/// </summary>
public static class VirtualDesktops
{
    private const string VdKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

    public static int DesktopCount() => GetDesktopIds().Count;

    /// <summary>Switches to the given 1-based desktop. No-op if already there or out of range.</summary>
    public static void SwitchTo(int desktopNumber)
    {
        var ids = GetDesktopIds();
        if (desktopNumber < 1 || desktopNumber > ids.Count)
            throw new InvalidOperationException($"Desktop {desktopNumber} doesn't exist (you have {ids.Count}).");

        var current = GetCurrentDesktopId();
        var currentIndex = current.HasValue ? ids.IndexOf(current.Value) : -1;
        if (currentIndex < 0)
            throw new InvalidOperationException("Couldn't determine the current desktop.");

        var delta = desktopNumber - 1 - currentIndex;
        if (delta == 0) return;
        SendDesktopArrows(delta);
    }

    private static List<Guid> GetDesktopIds()
    {
        using var key = Registry.CurrentUser.OpenSubKey(VdKey);
        if (key?.GetValue("VirtualDesktopIDs") is not byte[] blob || blob.Length < 16)
            return [];
        var ids = new List<Guid>(blob.Length / 16);
        for (var offset = 0; offset + 16 <= blob.Length; offset += 16)
            ids.Add(new Guid(blob.AsSpan(offset, 16)));
        return ids;
    }

    private static Guid? GetCurrentDesktopId()
    {
        // Windows 11 keeps it on the main key; some Windows 10 builds keep it per-session.
        using (var key = Registry.CurrentUser.OpenSubKey(VdKey))
        {
            if (key?.GetValue("CurrentVirtualDesktop") is byte[] blob && blob.Length >= 16)
                return new Guid(blob.AsSpan(0, 16));
        }
        var sessionId = Process.GetCurrentProcess().SessionId;
        using (var key = Registry.CurrentUser.OpenSubKey(
            $@"Software\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo\{sessionId}\VirtualDesktops"))
        {
            if (key?.GetValue("CurrentVirtualDesktop") is byte[] blob && blob.Length >= 16)
                return new Guid(blob.AsSpan(0, 16));
        }
        return null;
    }

    private static void SendDesktopArrows(int delta)
    {
        var arrow = delta > 0 ? VK_RIGHT : VK_LEFT;
        var steps = Math.Abs(delta);

        KeyEvent(VK_LWIN, down: true);
        KeyEvent(VK_LCONTROL, down: true);
        for (var i = 0; i < steps; i++)
        {
            KeyEvent(arrow, down: true);
            KeyEvent(arrow, down: false);
            if (i < steps - 1) Thread.Sleep(60); // let the switch animation register each step
        }
        KeyEvent(VK_LCONTROL, down: false);
        KeyEvent(VK_LWIN, down: false);
    }

    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_LCONTROL = 0xA2;
    private const ushort VK_LEFT = 0x25;
    private const ushort VK_RIGHT = 0x27;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private static void KeyEvent(ushort vk, bool down)
    {
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT { wVk = vk, dwFlags = down ? 0 : KEYEVENTF_KEYUP },
            },
        };
        if (SendInput(1, [input], Marshal.SizeOf<INPUT>()) != 1)
            throw new InvalidOperationException("SendInput was blocked.");
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
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
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}
