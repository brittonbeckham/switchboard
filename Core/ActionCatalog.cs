using Switchboard.Core.CustomActions;
using Switchboard.Util;

namespace Switchboard.Core;

/// <summary>
/// The catalog of OS-level actions a mapped key can trigger — things plain
/// keystrokes can't do. One source of truth: the settings UI lists these and
/// the hotkey engine executes them by id. Hand-coded custom actions (see
/// Core/CustomActions) are merged in automatically.
/// </summary>
public static class ActionCatalog
{
    public const string None = "none";

    /// <summary>Set once at startup by the tray context — lets a "run-action" step
    /// call back into <see cref="Run"/> without threading a host through every
    /// action/step layer.</summary>
    public static IActionHost? DefaultHost { get; set; }

    /// <summary>Summary is the numbered, human-readable steps shown in the assignment
    /// wizard's confirm step — what actually happens when this action runs.</summary>
    public sealed record ActionInfo(string Id, string DisplayName, string ShortLabel, string Summary);

    /// <summary>Recomputed on every access (not cached at startup) so an action just
    /// saved in the builder appears immediately — CustomActionCatalog.All itself
    /// reads its store fresh each time, and this has to follow suit.</summary>
    public static IReadOnlyList<ActionInfo> All => BuildAll();

    private static IReadOnlyList<ActionInfo> BuildAll()
    {
        var list = new List<ActionInfo>
        {
            new(None, "— Not mapped —", "", ""),
            new("mic", "Mute / unmute microphone (all mics, system-wide)", "Mic Mute",
                "1. Toggles mute on every active microphone endpoint via Windows Core Audio.\n" +
                "2. Updates the tray icon to reflect the new state."),
            new("calc", "Launch or focus Calculator", "Calculator",
                "1. If Calculator isn't running, launches it.\n" +
                "2. Brings its window to the front (works around Windows' foreground-lock restriction)."),
            new("focus", "Toggle focus mode", "Focus Mode",
                "1. Toggles the dim/blur veil over every window except the one you're focused on."),
            new("settings", "Open Switchboard settings", "Settings",
                "1. Opens (or focuses) the Switchboard settings window."),
            new("desk1", "Switch to desktop 1", "Desktop 1", "1. Switches to virtual desktop 1."),
            new("desk2", "Switch to desktop 2", "Desktop 2", "1. Switches to virtual desktop 2."),
            new("move-window-next-desktop", "Move active window to next desktop (wraps)", "Next Desktop →",
                "1. Moves the currently active window to the next virtual desktop, wrapping to desktop 1 after the last.\n" +
                "2. Stays on the current desktop — only the window moves."),
        };
        list.AddRange(CustomActionCatalog.All.Select(a => new ActionInfo(a.Id, a.DisplayName, a.ShortLabel, a.Summary)));
        return list;
    }

    public static int IndexOf(string id)
    {
        for (var i = 0; i < All.Count; i++)
            if (All[i].Id == id) return i;
        return 0;
    }

    /// <summary>Executes an action by id. Runs on a worker thread.</summary>
    public static void Run(string id, IActionHost host)
    {
        try
        {
            switch (id)
            {
                case "mic":
                    var (muted, devices) = AudioControl.ToggleMicrophoneMute();
                    Log.Info($"Microphone {(muted ? "MUTED" : "live")} ({devices} device(s)).");
                    host.OnMicMuteChanged(muted);
                    break;
                case "calc":
                    CalculatorLauncher.LaunchOrFocus();
                    break;
                case "focus":
                    host.ToggleFocusMode();
                    break;
                case "settings":
                    host.OpenSettings();
                    break;
                case var d when d.StartsWith("desk", StringComparison.Ordinal) &&
                                int.TryParse(d.AsSpan(4), out var desktop):
                    VirtualDesktops.SwitchTo(desktop);
                    break;
                case "move-window-next-desktop":
                    VirtualDesktops.MoveActiveWindowToNextDesktop();
                    break;
                default:
                    CustomActionCatalog.Find(id)?.RunAsync().GetAwaiter().GetResult();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Info($"Action '{id}' failed: {ex.Message}");
        }
    }
}

/// <summary>What actions need from the shell around them (tray icon, settings window).</summary>
public interface IActionHost
{
    void OnMicMuteChanged(bool muted);
    void ToggleFocusMode();
    void OpenSettings();
}
