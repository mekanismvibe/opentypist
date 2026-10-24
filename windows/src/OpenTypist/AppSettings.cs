using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace OpenTypist;

/// User settings, saved as JSON in %APPDATA%\OpenTypist\settings.json. The
/// Windows side of Settings.swift (phase 1 subset).
public sealed class AppSettings
{
    /// Apps where Tab or autocomplete matter more than prose suggestions, by
    /// executable name: terminals, code editors and password managers.
    public static readonly string[] DefaultDisabledApps =
    [
        "windowsterminal.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "conhost.exe", "openconsole.exe",
        "wezterm-gui.exe", "alacritty.exe", "mintty.exe", "putty.exe",
        "code.exe", "code - insiders.exe", "cursor.exe", "devenv.exe", "idea64.exe", "rider64.exe", "pycharm64.exe",
        "webstorm64.exe", "clion64.exe", "goland64.exe", "zed.exe", "sublime_text.exe", "notepad++.exe",
        "1password.exe", "bitwarden.exe", "keepass.exe", "keepassxc.exe", "dashlane.exe", "enpass.exe",
    ];

    public bool Enabled { get; set; } = true;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CompletionLength Length { get; set; } = CompletionLength.Medium;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ConfidenceLevel Confidence { get; set; } = ConfidenceLevel.Balanced;
    /// Add a space after an accepted word or a finished suggestion.
    public bool AcceptTrailingSpace { get; set; } = true;
    /// Put the app and window name in the prompt.
    public bool UseAppContext { get; set; } = true;
    /// Run the model on the graphics card when there is one.
    public bool UseGpu { get; set; } = true;
    public string? SelectedModelId { get; set; }
    /// A user-chosen .gguf; null means the downloaded catalog model.
    public string? CustomModelPath { get; set; }
    public List<string> DisabledApps { get; set; } = [.. DefaultDisabledApps];
    /// Keys that accept the next word / the whole suggestion.
    public AcceptKeys AcceptKeys { get; set; } = AcceptKeys.Standard;

    [JsonIgnore]
    public string Path { get; private set; } = DefaultPath;

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenTypist", "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public event Action? Changed;

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        AppSettings settings;
        try
        {
            settings = File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            AppStatus.Log($"settings unreadable, using defaults: {e.Message}");
            settings = new();
        }
        settings.Path = path;
        return settings;
    }

    /// Saves and tells listeners. UI thread only.
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var temp = Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, Path, overwrite: true);
        }
        catch (IOException e)
        {
            AppStatus.Log($"couldn't save settings: {e.Message}");
        }
        Changed?.Invoke();
    }

    public bool IsDisabled(string? appId) =>
        appId is not null && DisabledApps.Contains(appId, StringComparer.OrdinalIgnoreCase);

    public void SetDisabled(string appId, bool disabled)
    {
        DisabledApps.RemoveAll(a => string.Equals(a, appId, StringComparison.OrdinalIgnoreCase));
        if (disabled) DisabledApps.Add(appId);
        Save();
    }

    // Launch at login: HKCU\Software\Microsoft\Windows\CurrentVersion\Run

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "OpenTypist";

    public static bool LaunchAtLogin
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }
}
