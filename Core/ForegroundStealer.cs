using System.Runtime.InteropServices;

namespace Switchboard.Core;

/// <summary>
/// Reliably steals foreground focus for a window, working around Windows'
/// foreground-lock restriction via a synthetic Alt tap + thread-input attach.
/// Shared by anything that needs to drive another app's UI (Calculator focus
/// fix, custom actions that type into Teams/Slack).
/// </summary>
public static class ForegroundStealer
{
    public static IntPtr Current => GetForegroundWindow();

    /// <summary>Brings a window to the foreground, retrying briefly since other
    /// apps (shell, vendor software) can yank focus back.</summary>
    public static bool Focus(IntPtr window, int attempts = 6)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (GetForegroundWindow() != window) FocusOnce(window);
            Thread.Sleep(200);
            if (GetForegroundWindow() == window && attempt >= 2) return true;
        }
        return GetForegroundWindow() == window;
    }

    private static void FocusOnce(IntPtr window)
    {
        if (IsIconic(window)) ShowWindow(window, SW_RESTORE);
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var ourThread = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != ourThread &&
                       AttachThreadInput(ourThread, foregroundThread, true);
        try
        {
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            SetForegroundWindow(window);
            BringWindowToTop(window);
        }
        finally
        {
            if (attached) AttachThreadInput(ourThread, foregroundThread, false);
        }
    }

    private const int SW_RESTORE = 9;
    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool attaching);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);
}
