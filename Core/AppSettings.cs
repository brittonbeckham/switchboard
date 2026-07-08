using System.Text.Json;
using System.Text.Json.Serialization;

namespace Switchboard.Core;

public sealed class AppSettings
{
    public bool RunAtStartup { get; set; }

    /// <summary>Ctrl+Win+Numpad1..9 jumps directly to that virtual desktop.</summary>
    public bool NumpadHotkeysEnabled { get; set; } = true;

    /// <summary>Intercept the keyboard's Calculator key: launch Calculator or focus the existing window.</summary>
    public bool CalculatorFocusFixEnabled { get; set; } = true;

    /// <summary>Focus mode: dim everything behind the active window.</summary>
    public bool FocusModeEnabled { get; set; }

    /// <summary>How strongly focus mode dims background windows (5-90%).</summary>
    public int FocusModeDimPercent { get; set; } = 35;

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
