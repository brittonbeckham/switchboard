using Switchboard.Util;

namespace Switchboard.Core;

/// <summary>
/// The catalog of OS-level actions a mapped key can trigger — things plain
/// keystrokes can't do. One source of truth: the settings UI lists these and
/// the hotkey engine executes them by id.
/// </summary>
public static class ActionCatalog
{
    public const string None = "none";

    public sealed record ActionInfo(string Id, string DisplayName);

    public static readonly IReadOnlyList<ActionInfo> All =
    [
        new(None, "— Not mapped —"),
        new("mic", "Mute / unmute microphone (all mics, system-wide)"),
        new("calc", "Launch or focus Calculator"),
        new("focus", "Toggle focus mode"),
        new("settings", "Open Switchboard settings"),
        new("desk1", "Switch to desktop 1"),
        new("desk2", "Switch to desktop 2"),
        new("desk3", "Switch to desktop 3"),
        new("desk4", "Switch to desktop 4"),
        new("desk5", "Switch to desktop 5"),
        new("desk6", "Switch to desktop 6"),
        new("desk7", "Switch to desktop 7"),
        new("desk8", "Switch to desktop 8"),
        new("desk9", "Switch to desktop 9"),
    ];

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
