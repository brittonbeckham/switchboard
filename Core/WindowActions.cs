using System.Runtime.InteropServices;

namespace Switchboard.Core;

/// <summary>
/// The "window" action step: a small vocabulary of things to do to the current
/// foreground window — pin/unpin on top, maximize/minimize/restore/close, set
/// opacity, or move to the next monitor. Value is "verb" or "verb:arg", e.g.
/// "opacity:60" or "monitor:next".
/// </summary>
public static class WindowActions
{
    public static bool Apply(string command)
    {
        var hwnd = ForegroundStealer.Current;
        if (hwnd == IntPtr.Zero) return false;

        var parts = command.Split(':', 2);
        var verb = parts[0].Trim();
        var arg = parts.Length > 1 ? parts[1].Trim() : "";

        return verb switch
        {
            "pin" => SetTopmost(hwnd, true),
            "unpin" => SetTopmost(hwnd, false),
            "toggle-topmost" => SetTopmost(hwnd, !IsTopmost(hwnd)),
            "maximize" => ShowWindow(hwnd, SwMaximize),
            "minimize" => ShowWindow(hwnd, SwMinimize),
            "restore" => ShowWindow(hwnd, SwRestore),
            "close" => PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero),
            "opacity" => SetOpacity(hwnd, arg),
            "monitor" when arg == "next" => MoveToNextMonitor(hwnd),
            _ => false,
        };
    }

    private static bool IsTopmost(IntPtr hwnd) => (GetWindowLong(hwnd, GwlExStyle) & WsExTopmost) != 0;

    private static bool SetTopmost(IntPtr hwnd, bool topmost) =>
        SetWindowPos(hwnd, topmost ? HwndTopmost : HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize);

    private static bool SetOpacity(IntPtr hwnd, string percentText)
    {
        if (!int.TryParse(percentText, out var percent)) return false;
        percent = Math.Clamp(percent, 0, 100);
        var exStyle = GetWindowLong(hwnd, GwlExStyle);
        if ((exStyle & WsExLayered) == 0) SetWindowLong(hwnd, GwlExStyle, exStyle | WsExLayered);
        return SetLayeredWindowAttributes(hwnd, 0, (byte)Math.Round(percent * 255.0 / 100), LwaAlpha);
    }

    /// <summary>Moves the window to the next monitor (wrapping), keeping its size
    /// and centering it on the new screen's working area.</summary>
    private static bool MoveToNextMonitor(IntPtr hwnd)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (screens.Length < 2) return false;
        if (!GetWindowRect(hwnd, out var rect)) return false;

        var current = System.Windows.Forms.Screen.FromHandle(hwnd);
        var currentIndex = Array.IndexOf(screens, current);
        var next = screens[(currentIndex + 1) % screens.Length];

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var x = next.WorkingArea.Left + (next.WorkingArea.Width - width) / 2;
        var y = next.WorkingArea.Top + (next.WorkingArea.Height - height) / 2;
        return SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, SwpNoZOrder);
    }

    private const int SwMaximize = 3;
    private const int SwMinimize = 6;
    private const int SwRestore = 9;
    private const uint WmClose = 0x0010;
    private const int GwlExStyle = -20;
    private const int WsExTopmost = 0x00000008;
    private const int WsExLayered = 0x00080000;
    private const uint LwaAlpha = 0x2;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNoTopmost = new(-2);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
