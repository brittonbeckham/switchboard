using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace Switchboard.UI;

/// <summary>
/// Finds the on-screen text insertion point (caret) where Wispr will paste.
/// Win32 caret for classic apps; FlaUI/UIA3 for Chromium, Electron, Terminal, etc.
/// </summary>
internal static class TextCaretLocator
{
    // Reused — creating UIA3Automation every frame is expensive.
    private static readonly UIA3Automation Automation = new();

    private static uint _targetThread;
    private static IntPtr _targetHwnd;
    private static long _lastGoodMs;

    /// <summary>
    /// Screen coordinates of the caret tip and its height in pixels.
    /// <paramref name="stale"/> is reserved for callers that want soft-hold behavior.
    /// </summary>
    public static bool TryGet(out int screenX, out int screenY, out int height, out bool stale)
    {
        stale = false;
        var fg = GetForegroundWindow();
        var fgIsWispr = IsWisprWindow(fg);

        if (!fgIsWispr && fg != IntPtr.Zero)
        {
            _targetHwnd = fg;
            _targetThread = GetWindowThreadProcessId(fg, out _);
        }

        if (TryWin32Thread(GetWindowThreadProcessId(fg, out _), out screenX, out screenY, out height)
            || TryAutomation(out screenX, out screenY, out height))
        {
            _lastGoodMs = Environment.TickCount64;
            return true;
        }

        // Wispr overlay stole focus — keep reading the last real target.
        if (_targetThread != 0
            && TryWin32Thread(_targetThread, out screenX, out screenY, out height))
        {
            _lastGoodMs = Environment.TickCount64;
            stale = fgIsWispr;
            return true;
        }

        if (_targetHwnd != IntPtr.Zero
            && TryAutomationFromHwnd(_targetHwnd, out screenX, out screenY, out height))
        {
            _lastGoodMs = Environment.TickCount64;
            stale = fgIsWispr;
            return true;
        }

        screenX = screenY = height = 0;
        return false;
    }

    public static bool ForegroundIsWispr() => IsWisprWindow(GetForegroundWindow());

    /// <summary>
    /// Text of the field under the caret (same target the ring follows). Does not
    /// fall back to "first edit in the window" — that breaks when you bounce
    /// between boxes during dictation.
    /// </summary>
    public static bool TryReadFocusedText(out string text)
    {
        text = "";
        try
        {
            // Prefer the live caret point — matches where Wispr will paste.
            if (TryGet(out var x, out var y, out _, out _))
            {
                if (TryReadTextAtScreenPoint(x, y, out text))
                    return true;
            }

            var focused = Automation.FocusedElement();
            if (focused != null
                && !IsWisprElement(focused)
                && TryReadElementText(focused, out text))
                return true;
        }
        catch
        {
            // UIA is flaky across apps; caller treats false as "unknown."
        }
        return false;
    }

    private static bool IsWisprElement(AutomationElement el)
    {
        try
        {
            if (el.Properties.ProcessId.TryGetValue(out var pid) && pid != 0)
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                return p.ProcessName.Contains("Wispr", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { /* ignore */ }
        return false;
    }

    private static bool TryReadTextAtScreenPoint(int screenX, int screenY, out string text)
    {
        text = "";
        try
        {
            var el = Automation.FromPoint(new System.Drawing.Point(screenX, screenY));
            for (var depth = 0; el != null && depth < 10; depth++)
            {
                if (!IsWisprElement(el) && TryReadElementText(el, out text))
                    return true;
                try { el = el.Parent; }
                catch { break; }
            }
        }
        catch
        {
            return false;
        }
        return false;
    }

    private static bool TryReadElementText(AutomationElement el, out string text)
    {
        text = "";
        try
        {
            if (el.Patterns.Value.IsSupported)
            {
                var v = el.Patterns.Value.Pattern.Value;
                if (v != null) { text = v; return true; }
            }
        }
        catch { /* continue */ }

        try
        {
            if (el.Patterns.Text.IsSupported)
            {
                var doc = el.Patterns.Text.Pattern.DocumentRange;
                if (doc != null)
                {
                    // Cap — huge docs are slow and we only need to see the paste suffix.
                    text = doc.GetText(8_000) ?? "";
                    return true;
                }
            }
        }
        catch { /* continue */ }

        return false;
    }

    /// <summary>True when <paramref name="field"/> appears to already contain the pasted clipboard text.</summary>
    public static bool FieldContainsPaste(string field, string paste)
    {
        if (string.IsNullOrEmpty(paste)) return false;
        if (string.IsNullOrEmpty(field)) return false;

        static string Norm(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n');
        var f = Norm(field);
        var p = Norm(paste);
        if (f.Contains(p, StringComparison.Ordinal)) return true;

        var ft = f.TrimEnd();
        var pt = p.TrimEnd();
        if (pt.Length > 0 && ft.EndsWith(pt, StringComparison.Ordinal)) return true;

        // UIA often truncates long documents — match on a distinctive tail.
        if (pt.Length > 64)
        {
            var tail = pt[^Math.Min(96, pt.Length)..];
            if (f.Contains(tail, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// Paste landed only when the field changed from the pre-dictation snapshot
    /// and the new clipboard text is present. Pre-existing text alone is not enough.
    /// </summary>
    public static bool PasteAppearedInField(string? fieldBefore, string fieldNow, string paste)
    {
        if (!FieldContainsPaste(fieldNow, paste)) return false;

        // Must differ from the snapshot taken when dictation started.
        if (fieldBefore != null
            && string.Equals(Norm(fieldBefore), Norm(fieldNow), StringComparison.Ordinal))
            return false;

        // If the paste string was already in the field before this dictation,
        // require a real change (length/content) so we don't treat old text as new.
        if (fieldBefore != null && FieldContainsPaste(fieldBefore, paste))
        {
            var before = Norm(fieldBefore);
            var now = Norm(fieldNow);
            if (now.Length <= before.Length) return false;
        }

        return true;

        static string Norm(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    private static bool IsWisprWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return false;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName.Contains("Wispr", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryWin32Thread(uint threadId, out int screenX, out int screenY, out int height)
    {
        screenX = screenY = height = 0;
        if (threadId == 0) return false;

        var info = new GuiThreadInfo { cbSize = Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(threadId, ref info)) return false;
        if (info.hwndCaret == IntPtr.Zero) return false;

        var rc = info.rcCaret;
        var h = rc.Bottom - rc.Top;
        if (h < 0) return false;
        if (h == 0 && rc.Right <= rc.Left) h = 16;

        var pt = new POINT { X = rc.Left, Y = rc.Top };
        if (!ClientToScreen(info.hwndCaret, ref pt)) return false;

        screenX = pt.X;
        screenY = pt.Y + Math.Max(h, 1) / 2;
        height = Math.Max(h, 12);
        return true;
    }

    private static bool TryAutomation(out int screenX, out int screenY, out int height)
    {
        screenX = screenY = height = 0;
        try
        {
            var focused = Automation.FocusedElement();
            if (focused == null || IsWisprElement(focused)) return false;
            // Real caret/selection only — no "bounding box of whatever is focused"
            // fallback (that pinned the ring to Cursor's sidebar after alt-tab).
            return TryElementCaret(focused, out screenX, out screenY, out height);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryAutomationFromHwnd(IntPtr hwnd, out int screenX, out int screenY, out int height)
    {
        screenX = screenY = height = 0;
        try
        {
            var root = Automation.FromHandle(hwnd);
            if (root == null) return false;

            try
            {
                var focused = Automation.FocusedElement();
                if (focused != null
                    && !IsWisprElement(focused)
                    && focused.Properties.ProcessId.TryGetValue(out var fp)
                    && root.Properties.ProcessId.TryGetValue(out var rp)
                    && fp == rp
                    && TryElementCaret(focused, out screenX, out screenY, out height))
                    return true;
            }
            catch { /* fall through */ }

            // Do not FindFirstDescendant(Edit) — wrong box in multi-field apps.
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryElementCaret(AutomationElement el, out int screenX, out int screenY, out int height)
    {
        screenX = screenY = height = 0;

        // TextPattern2 caret range — best signal when the app exposes it.
        try
        {
            if (el.Patterns.Text2.IsSupported)
            {
                var text2 = el.Patterns.Text2.Pattern;
                var caret = text2.GetCaretRange(out _);
                if (caret != null && TryRangeRect(caret, out screenX, out screenY, out height))
                    return true;
            }
        }
        catch { /* continue */ }

        // TextPattern selection (degenerate range = caret).
        try
        {
            if (el.Patterns.Text.IsSupported)
            {
                var text = el.Patterns.Text.Pattern;
                var selection = text.GetSelection();
                if (selection is { Length: > 0 }
                    && TryRangeRect(selection[0], out screenX, out screenY, out height))
                    return true;
            }
        }
        catch { /* continue */ }

        // Edit/Document only: use the control box if there is no caret pattern yet
        // (focus just landed). Never use Custom — Electron sidebars are Custom and
        // were stealing the ring.
        try
        {
            var ct = el.ControlType;
            if (ct is ControlType.Edit or ControlType.Document)
            {
                if (el.Properties.BoundingRectangle.TryGetValue(out var r)
                    && r.Width > 0 && r.Height > 0
                    && r.Width < 2000 && r.Height < 400
                    && !double.IsInfinity(r.X) && !double.IsNaN(r.X))
                {
                    screenX = (int)(r.X + Math.Min(12, r.Width / 2));
                    screenY = (int)(r.Y + r.Height / 2);
                    height = Math.Max(12, (int)Math.Min(r.Height, 48));
                    return true;
                }
            }
        }
        catch { /* ignore */ }

        return false;
    }

    private static bool TryRangeRect(FlaUI.Core.ITextRange range,
        out int screenX, out int screenY, out int height)
    {
        screenX = screenY = height = 0;
        try
        {
            var rects = range.GetBoundingRectangles();
            if (rects == null || rects.Length == 0) return false;
            var r = rects[0];
            if (double.IsInfinity(r.X) || double.IsNaN(r.X)) return false;
            // Chromium sometimes returns an empty placeholder.
            if (r.X == 0 && r.Y == 0 && r.Width == 0 && r.Height == 0) return false;

            screenX = (int)r.X;
            screenY = (int)(r.Y + Math.Max(r.Height, 1) / 2);
            height = Math.Max(12, (int)(r.Height > 0 ? r.Height : 16));
            return true;
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GuiThreadInfo pgui);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
}
