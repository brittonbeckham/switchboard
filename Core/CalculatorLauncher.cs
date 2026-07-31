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

        Util.Log.Info(ForegroundStealer.Focus(window)
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

    private static string GetClassNameOf(IntPtr hWnd)
    {
        var buffer = new System.Text.StringBuilder(256);
        _ = GetClassName(hWnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder buffer, int max);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}
