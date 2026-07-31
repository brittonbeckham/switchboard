using Switchboard.Util;

namespace Switchboard.Core.CustomActions;

/// <summary>
/// A custom action defined as data — an ordered list of JSON action blocks,
/// each run by <see cref="ActionStepRunner"/> — rather than a hand-written
/// method. This is the model the future "Create New Action" builder targets: a
/// user assembles blocks/steps in the UI instead of asking for custom code
/// each time. The whole foreground window is captured once before running and
/// restored once after, regardless of how many blocks ran or were skipped.
/// </summary>
public sealed class ScriptedAction(
    string id, string displayName, string shortLabel, IReadOnlyList<string> blockJsons) : ICustomAction
{
    public string Id => id;
    public string DisplayName => displayName;
    public string ShortLabel => shortLabel;

    public string Summary => string.Join("\n\n",
        blockJsons.Select((json, i) => $"{i + 1}. {ActionStepRunner.DescribeActionBlock(json)}"));

    public Task RunAsync()
    {
        // Only focus-window actually steals focus away from wherever the user
        // is; an action that just holds/releases/sends keys (e.g. a knob-driven
        // Alt-Tab switcher) has nothing to hand back, and forcing focus back
        // would instantly dismiss whatever system UI those keys just opened.
        var stealsFocus = blockJsons.Any(json => ActionStepRunner.BlockHasStep(json, "focus-window"));
        var previous = stealsFocus ? ForegroundStealer.Current : IntPtr.Zero;
        var results = new List<string>();
        foreach (var json in blockJsons)
        {
            var (name, ranFully) = ActionStepRunner.RunActionBlock(json);
            results.Add($"{name}: {(ranFully ? "done" : "skipped")}");
        }
        if (previous != IntPtr.Zero) ForegroundStealer.Focus(previous);
        Log.Info($"{displayName}: {string.Join(", ", results)}");
        return Task.CompletedTask;
    }
}
