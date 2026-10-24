namespace OpenTypist;

/// The "Last check" line in the tray menu and Settings: why the last
/// keystroke did or didn't produce a suggestion. Never contains typed text.
/// With OPENTYPIST_LOG=1, notes also go to %LOCALAPPDATA%\OpenTypist\log.txt.
public static class AppStatus
{
    private static readonly object Gate = new();
    private static readonly string? LogPath = Environment.GetEnvironmentVariable("OPENTYPIST_LOG") is { Length: > 0 }
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenTypist", "log.txt")
        : null;

    public static string LastCheck { get; private set; } = "Nothing yet";
    public static event Action? Changed;

    /// UI thread only.
    public static void Note(string reason)
    {
        if (reason != LastCheck)
        {
            LastCheck = reason;
            Changed?.Invoke();
        }
        Log(reason);
    }

    public static void Log(string message)
    {
        if (LogPath is null) return;
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}\n");
            }
            catch (IOException) { }
        }
    }
}
