namespace Switchboard.Core.CustomActions;

/// <summary>The registry of hand-coded custom actions. Add a new one here and
/// it's immediately available everywhere ActionCatalog entries are — the
/// Action picker, the search box, ghost-key allocation — no other UI changes.</summary>
public static class CustomActionCatalog
{
    /// <summary>Foreground the app, clear whatever's in the focused field, type the
    /// slash command, run it. Chromium apps (Teams/Slack) only process input while
    /// actually focused, so each sleep gives the previous step time to actually land
    /// before the next one fires — not a guarantee, just breathing room.</summary>
    private static string TeamsBlockJson(string command) => $$"""
        {
          "name": "Microsoft Teams",
          "steps": [
            { "focus-window": "ms-teams" },
            { "sleep": "160" },
            { "send-keys": "Ctrl+E" },
            { "sleep": "160" },
            { "clear-field": "" },
            { "sleep": "160" },
            { "send-keys": "{{command}}" },
            { "sleep": "160" },
            { "send-keys": "Enter" }
          ]
        }
        """;

    private static string SlackBlockJson(string command) => $$"""
        {
          "name": "Slack",
          "steps": [
            { "focus-window": "slack" },
            { "sleep": "160" },
            { "send-keys": "Esc" },
            { "sleep": "160" },
            { "clear-field": "" },
            { "sleep": "160" },
            { "send-keys": "{{command}}" },
            { "sleep": "160" },
            { "send-keys": "Enter" }
          ]
        }
        """;

    /// <summary>A breather between the Teams and Slack blocks — otherwise the Slack
    /// focus-window fires the instant Teams' Enter keypress lands, with no gap.</summary>
    private const string BetweenAppsBlockJson = """
        {
          "name": "Between apps",
          "steps": [
            { "sleep": "500" }
          ]
        }
        """;

    private static readonly IReadOnlyList<ICustomAction> BuiltIn =
    [
        new ScriptedAction("step-away", "Step away: set Teams + Slack status to Away", "Step Away",
            [TeamsBlockJson("/away"), BetweenAppsBlockJson, SlackBlockJson("/away")]),
        new ScriptedAction("step-back", "I'm back: set Teams + Slack status to Active", "I'm Back",
            [TeamsBlockJson("/available"), BetweenAppsBlockJson, SlackBlockJson("/active")]),
    ];

    /// <summary>Built-ins plus every user-created custom action, read fresh from disk
    /// on every access — cheap at this scale, and guarantees an action just saved in
    /// the builder shows up immediately without restarting Switchboard.</summary>
    public static IReadOnlyList<ICustomAction> All
    {
        get
        {
            var list = new List<ICustomAction>(BuiltIn);
            foreach (var stored in CustomActionStore.Load())
            {
                var blockJsons = stored.Blocks.Select(CustomActionStore.ToBlockJson).ToList();
                list.Add(new ScriptedAction(stored.Id, stored.DisplayName, stored.ShortLabel, blockJsons));
            }
            return list;
        }
    }

    public static ICustomAction? Find(string id) => All.FirstOrDefault(a => a.Id == id);
}
