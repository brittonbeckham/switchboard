using System.Runtime.InteropServices;
using System.Text;
using FlaUI.UIA3;

namespace Switchboard.UI;

/// <summary>Console probe for caret APIs. Run with --carettest.</summary>
internal static class CaretProbe
{
    public static void Run()
    {
        AllocConsole();
        var logPath = Path.Combine(Path.GetTempPath(), "switchboard-caretprobe.log");
        Console.WriteLine("Caret probe — click into Notepad / Cursor / browser / Terminal. Ctrl+C to quit.");
        Console.WriteLine($"Also logging to {logPath}");
        Console.WriteLine();
        File.WriteAllText(logPath, $"started {DateTime.Now:O}{Environment.NewLine}");

        using var automation = new UIA3Automation();
        while (true)
        {
            var ok = TextCaretLocator.TryGet(out var x, out var y, out var h, out var stale);
            var fg = GetForegroundWindow();
            GetWindowThreadProcessId(fg, out var pid);
            var proc = "?";
            try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { /* */ }

            var detail = "uia=n/a";
            try
            {
                var el = automation.FocusedElement();
                if (el == null) detail = "focused=null";
                else
                {
                    var name = el.Name ?? "";
                    var ct = el.ControlType.ToString();
                    var text = el.Patterns.Text.IsSupported;
                    var text2 = el.Patterns.Text2.IsSupported;
                    detail = $"name=[{Trim(name, 28)}] ct={ct} text={text} text2={text2}";
                    if (text2)
                    {
                        try
                        {
                            var caret = el.Patterns.Text2.Pattern.GetCaretRange(out var active);
                            var rects = caret?.GetBoundingRectangles();
                            if (rects is { Length: > 0 })
                                detail += $" caret2={rects[0].X:0},{rects[0].Y:0} {rects[0].Width:0}x{rects[0].Height:0} active={active}";
                            else
                                detail += " caret2=empty";
                        }
                        catch (Exception ex) { detail += $" caret2Err={ex.Message}"; }
                    }
                    else if (text)
                    {
                        try
                        {
                            var sel = el.Patterns.Text.Pattern.GetSelection();
                            if (sel is { Length: > 0 })
                            {
                                var rects = sel[0].GetBoundingRectangles();
                                if (rects is { Length: > 0 })
                                    detail += $" sel={rects[0].X:0},{rects[0].Y:0} {rects[0].Width:0}x{rects[0].Height:0}";
                                else
                                    detail += " sel=emptyRects";
                            }
                            else detail += " sel=none";
                        }
                        catch (Exception ex) { detail += $" selErr={ex.Message}"; }
                    }

                    if (el.Properties.BoundingRectangle.TryGetValue(out var br))
                        detail += $" bounds={br.X:0},{br.Y:0} {br.Width:0}x{br.Height:0}";
                }
            }
            catch (Exception ex) { detail = "ERR " + ex.Message; }

            var line1 = $"{DateTime.Now:HH:mm:ss.fff} [{proc}] locator={(ok ? $"OK {x},{y} h={h} stale={stale}" : "miss")}";
            var line2 = $"  {detail}";
            Console.WriteLine(line1);
            Console.WriteLine(line2);
            Console.WriteLine();
            File.AppendAllText(logPath, line1 + Environment.NewLine + line2 + Environment.NewLine);
            Thread.Sleep(400);
        }
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] private static extern bool AllocConsole();
}
