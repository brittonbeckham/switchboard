using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Switchboard.Core;

/// <summary>
/// Launches Calculator or brings the existing window to the foreground. The
/// modern Calculator is a UWP app hosted by ApplicationFrameHost, so the frame
/// window is found by matching the hosted CoreWindow's process.
/// </summary>
public static class CalculatorLauncher
{
    public static void LaunchOrFocus()
    {
        var window = FindCalculatorWindow();
        if (window == IntPtr.Zero)
        {
            Process.Start(new ProcessStartInfo("calc.exe") { UseShellExecute = true });
            for (var i = 0; i < 40 && window == IntPtr.Zero; i++)
            {
                Thread.Sleep(100);
                window = FindCalculatorWindow();
            }
        }
        if (window == IntPtr.Zero)
        {
            Util.Log.Info("Calculator window never appeared.");
            return;
        }

        // Something else may also react (shell, vendor software) and yank the
        // foreground back — keep re-asserting briefly until our focus sticks.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (GetForegroundWindow() != window) FocusWindow(window);
            Thread.Sleep(200);
            if (GetForegroundWindow() == window && attempt >= 2)
            {
                Util.Log.Info("Calculator focused.");
                return;
            }
        }
        Util.Log.Info(GetForegroundWindow() == window
            ? "Calculator focused."
            : "Calculator focus was overridden by another window.");
    }

    private static IntPtr FindCalculatorWindow()
    {
        foreach (var name in new[] { "win32calc", "Calculator" })
        {
            var direct = Process.GetProcessesByName(name)
                .Select(p => p.MainWindowHandle)
                .FirstOrDefault(h => h != IntPtr.Zero);
            if (direct != IntPtr.Zero) return direct;
        }

        var calcPids = Process.GetProcessesByName("CalculatorApp").Select(p => (uint)p.Id).ToHashSet();
        if (calcPids.Count == 0) return IntPtr.Zero;

        var found = IntPtr.Zero;
        EnumWindows((frame, _) =>
        {
            if (GetClassNameOf(frame) != "ApplicationFrameWindow") return true;
            EnumChildWindows(frame, (child, _) =>
            {
                GetWindowThreadProcessId(child, out var pid);
                if (calcPids.Contains(pid))
                {
                    found = frame;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found == IntPtr.Zero;
        }, IntPtr.Zero);
        return found;
    }

    private static void FocusWindow(IntPtr window)
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

    private static string GetClassNameOf(IntPtr hWnd)
    {
        var buffer = new System.Text.StringBuilder(256);
        _ = GetClassName(hWnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private const int SW_RESTORE = 9;
    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder buffer, int max);

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
