namespace Switchboard.Util;

/// <summary>Tiny in-memory log with a change event so the settings dialog can live-tail it.</summary>
public static class Log
{
    private const int MaxLines = 500;
    private static readonly LinkedList<string> Lines = new();
    private static readonly object Sync = new();

    public static event Action<string>? LineAdded;

    private static readonly string LogFile =
        Path.Combine(Core.AppSettings.Directory, "switchboard.log");

    public static void Info(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (Sync)
        {
            Lines.AddLast(line);
            while (Lines.Count > MaxLines) Lines.RemoveFirst();
            try
            {
                Directory.CreateDirectory(Core.AppSettings.Directory);
                File.AppendAllText(LogFile, line + Environment.NewLine);
            }
            catch
            {
                // Logging must never take the app down.
            }
        }
        LineAdded?.Invoke(line);
    }

    public static string Snapshot()
    {
        lock (Sync) return string.Join(Environment.NewLine, Lines);
    }
}
