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

    /// <summary>Moves the active window to the next virtual desktop (wrapping past
    /// the last back to the first) and follows it there, via the documented
    /// IVirtualDesktopManager COM API — no drag, no Win+Tab.</summary>
    public static void MoveActiveWindowToNextDesktop()
    {
        var hwnd = ForegroundStealer.Current;
        if (hwnd == IntPtr.Zero) return;

        var ids = GetDesktopIds();
        if (ids.Count == 0) return;

        var manager = (IVirtualDesktopManager)Activator.CreateInstance(
            Type.GetTypeFromCLSID(ClsidVirtualDesktopManager)!)!;
        if (manager.GetWindowDesktopId(hwnd, out var currentId) != 0) return;

        var currentIndex = ids.IndexOf(currentId);
        if (currentIndex < 0) return;

        var nextIndex = (currentIndex + 1) % ids.Count;
        var nextId = ids[nextIndex];
        if (manager.MoveWindowToDesktop(hwnd, ref nextId) != 0) return;
        SwitchTo(nextIndex + 1); // follow the window so it stays in view
    }

    private static readonly Guid ClsidVirtualDesktopManager = new("aa509086-5ca9-4c25-8f95-589d3c07b48a");

    [ComImport]
    [Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrentDesktop);
        [PreserveSig] int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
        [PreserveSig] int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
    }

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

        // The user may be physically holding Ctrl/Win (numpad hotkey). Synthesizing
        // an up for a held modifier would strip it mid-chord and break the next
        // hotkey press, so only touch modifiers that aren't already down.
        var winHeld = IsDown(VK_LWIN) || IsDown(VK_RWIN);
        var ctrlHeld = IsDown(VK_CONTROL);

        if (!winHeld) KeyEvent(VK_LWIN, down: true);
        if (!ctrlHeld) KeyEvent(VK_LCONTROL, down: true);
        for (var i = 0; i < steps; i++)
        {
            KeyEvent(arrow, down: true);
            KeyEvent(arrow, down: false);
            if (i < steps - 1) Thread.Sleep(60); // let the switch animation register each step
        }
        if (!ctrlHeld) KeyEvent(VK_LCONTROL, down: false);
        if (!winHeld) KeyEvent(VK_LWIN, down: false);
    }

    private static bool IsDown(ushort vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_RWIN = 0x5C;
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

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

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
