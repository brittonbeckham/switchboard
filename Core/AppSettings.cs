using System.Text.Json;
using System.Text.Json.Serialization;

namespace Switchboard.Core;

public sealed class AppSettings
{
    /// <summary>Target virtual desktop (1-based) for each Easy-Switch key. 0 = do nothing.</summary>
    public int[] EasySwitchDesktops { get; set; } = [1, 2, 3];

    public bool RunAtStartup { get; set; }

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

    public int DesktopForKey(int keyNumber) =>
        keyNumber is >= 1 and <= 3 ? EasySwitchDesktops[keyNumber - 1] : 0;
}
