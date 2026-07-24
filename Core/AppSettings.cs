using System.Text.Json;
using System.Text.Json.Serialization;

namespace Switchboard.Core;

public sealed class AppSettings
{
    public bool RunAtStartup { get; set; }

    /// <summary>Last screen position of the settings window (restored on reopen).</summary>
    public int? SettingsWindowX { get; set; }
    public int? SettingsWindowY { get; set; }

    /// <summary>Mapping of function keys ("F1".."F24") to ActionCatalog action ids.</summary>
    public Dictionary<string, string> FunctionKeyActions { get; set; } = [];

    /// <summary>User notes for Megalodon pad keys/knobs, keyed "L{layer}K{row},{col}" / "L{layer}E{encoder}".</summary>
    public Dictionary<string, string> PadLabels { get; set; } = [];

    /// <summary>Ctrl+Win+Numpad1..9 jumps directly to that virtual desktop.</summary>
    public bool NumpadHotkeysEnabled { get; set; }

    /// <summary>Intercept the keyboard's Calculator key: launch Calculator or focus the existing window.</summary>
    public bool CalculatorFocusFixEnabled { get; set; }

    /// <summary>Focus mode: dim everything behind the active window.</summary>
    public bool FocusModeEnabled { get; set; }

    /// <summary>How strongly focus mode dims background windows (5-90%).</summary>
    public int FocusModeDimPercent { get; set; } = 35;

    /// <summary>Focus mode blurs background windows (acrylic) instead of only dimming.</summary>
    public bool FocusModeBlurEnabled { get; set; }

    /// <summary>Hovering the mouse over a background window temporarily lifts the veil off it.</summary>
    public bool FocusModePeekEnabled { get; set; } = true;

    /// <summary>Show an on-screen popup when a macropad key is pressed.</summary>
    public bool KeyHudEnabled { get; set; }

    /// <summary>Pad positions (label keys) whose HUD pop-up is silenced.</summary>
    public List<string> MutedHudKeys { get; set; } = [];

    [JsonIgnore]
    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Switchboard");

    private static string FilePath => Path.Combine(Directory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // Corrupt settings fall back to defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
