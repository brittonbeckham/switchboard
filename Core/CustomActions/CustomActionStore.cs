using System.Text.Json;

namespace Switchboard.Core.CustomActions;

public sealed record StoredStep(string Kind, string Value);
public sealed record StoredBlock(string Name, List<StoredStep> Steps);
public sealed record StoredAction(string Id, string DisplayName, string ShortLabel, List<StoredBlock> Blocks);

/// <summary>
/// Persists user-created custom actions (built via the in-app action builder)
/// to their own JSON file — separate from settings.json so a growing library
/// of action definitions doesn't bloat the main config.
/// </summary>
public static class CustomActionStore
{
    private static string FilePath => Path.Combine(AppSettings.Directory, "custom-actions.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static List<StoredAction> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<List<StoredAction>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch
        {
            // Corrupt file falls back to empty rather than crashing the action list.
        }
        return [];
    }

    public static void Save(List<StoredAction> actions)
    {
        Directory.CreateDirectory(AppSettings.Directory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(actions, JsonOptions));
    }

    /// <summary>Adds or replaces (by Id) one stored action and saves immediately.</summary>
    public static void Add(StoredAction action)
    {
        var all = Load();
        all.RemoveAll(a => a.Id == action.Id);
        all.Add(action);
        Save(all);
    }

    public static void Remove(string id)
    {
        var all = Load();
        if (all.RemoveAll(a => a.Id == id) > 0) Save(all);
    }

    /// <summary>Renders one stored block into the single-property-per-step JSON
    /// ActionStepRunner expects, e.g. {"name":"Slack","steps":[{"focus-window":"slack"}]}.</summary>
    public static string ToBlockJson(StoredBlock block)
    {
        var steps = block.Steps.Select(s => (object)new Dictionary<string, string> { [s.Kind] = s.Value }).ToList();
        return JsonSerializer.Serialize(new { name = block.Name, steps });
    }
}
