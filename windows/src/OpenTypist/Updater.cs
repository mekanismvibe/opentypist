using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace OpenTypist;

/// Updates from GitHub Releases, like Updater.swift: checks the latest
/// release of the repository at most once a day (or when asked), downloads
/// its `OpenTypist-<version>-win-x64.zip`, verifies it against the published
/// `.zip.sha256`, checks the version of the app inside, and swaps the app
/// folder once this process has exited:
///
/// 1. The new exe (still in the temp folder) runs `--finish-update <pid>
///    <app folder>`, waits for the old process to exit, moves the old folder
///    aside, copies the new one in its place (or over it, if the folder is
///    in use) and starts it with `--after-update`.
/// 2. The new app removes the old folder and the download.
///
/// The only data sent is a normal HTTPS request to api.github.com (and the
/// download from GitHub).
public sealed class Updater
{
    public const string Repository = "mekanismvibe/opentypist";

    public sealed record Release(string Version, string Notes, string Page, string Zip, string Checksum);

    public enum Phase { Idle, Checking, UpToDate, Available, Installing, Failed }

    private readonly AppSettings settings;
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private DispatcherTimer? timer;

    public Phase State { get; private set; } = Phase.Idle;
    public string Message { get; private set; } = "";
    public Release? Available { get; private set; }
    /// Raised on the UI thread.
    public event Action? Changed;

    public Updater(AppSettings settings)
    {
        this.settings = settings;
    }

    /// The running app's version ("0.3.1"), without build metadata.
    public static string CurrentVersion { get; } = Clean(
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0");

    private static string Clean(string version)
    {
        int plus = version.IndexOf('+');
        return (plus >= 0 ? version[..plus] : version).TrimStart('v');
    }

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"OpenTypist/{CurrentVersion}");
        return client;
    }

    // Checking

    /// Checks now if automatic checks are on and the last one is a day old,
    /// then keeps doing so while the app runs.
    public void StartAutomaticChecks()
    {
        CheckIfDue();
        timer = new DispatcherTimer(TimeSpan.FromHours(6), DispatcherPriority.Background, (_, _) => CheckIfDue(), dispatcher);
        timer.Start();
    }

    private void CheckIfDue()
    {
        if (!settings.CheckForUpdates) return;
        if (settings.LastUpdateCheck is DateTime last && DateTime.UtcNow - last < TimeSpan.FromHours(24)) return;
        Check();
    }

    public async void Check(bool userInitiated = false)
    {
        if (State is Phase.Checking or Phase.Installing) return;
        Set(Phase.Checking, "Checking for updates...");
        try
        {
            var release = await FetchLatest();
            settings.LastUpdateCheck = DateTime.UtcNow;
            settings.Save();
            if (release is not null && Versions.IsNewer(release.Version, CurrentVersion))
            {
                Available = release;
                Set(Phase.Available, $"OpenTypist {release.Version} is available");
                AppStatus.Note($"Update available: OpenTypist {release.Version}");
            }
            else
            {
                Available = null;
                Set(Phase.UpToDate, $"OpenTypist {CurrentVersion} is up to date");
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            Set(Phase.Failed, $"Couldn't check for updates: {e.Message}");
            if (!userInitiated) AppStatus.Log(Message);
        }
    }

    /// The newest published release that has a Windows build, if any.
    public static async Task<Release?> FetchLatest()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no releases yet
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"GitHub returned {(int)response.StatusCode}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return ParseRelease(json.RootElement);
    }

    public static Release? ParseRelease(JsonElement release)
    {
        if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
        var version = Clean(release.GetProperty("tag_name").GetString() ?? "");
        var zipName = Versions.WindowsZip(version);
        string? zip = null, checksum = null;
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            var url = asset.GetProperty("browser_download_url").GetString();
            if (name == zipName) zip = url;
            else if (name == zipName + ".sha256") checksum = url;
        }
        // The macOS build is published first; the Windows one follows.
        if (zip is null || checksum is null) return null;
        var notes = release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        var page = release.TryGetProperty("html_url", out var html) ? html.GetString() ?? "" : "";
        return new Release(version, notes, page, zip, checksum);
    }

    // Installing

    public static string AppFolder => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    public async void Install()
    {
        if (Available is not { } release || State == Phase.Installing) return;
        Set(Phase.Installing, $"Downloading {release.Version}...");
        try
        {
            var progress = new Progress<string>(message => Set(Phase.Installing, message));
            var newApp = await Prepare(release, progress);
            if (!CanReplace(AppFolder))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{newApp}\"") { UseShellExecute = true });
                throw new IOException($"OpenTypist can't replace itself in {Path.GetDirectoryName(AppFolder)}. The new version is open in Explorer; quit OpenTypist and copy it over the old one.");
            }
            Set(Phase.Installing, "Installing...");
            Process.Start(new ProcessStartInfo(Path.Combine(newApp, "OpenTypist.exe"))
            {
                ArgumentList = { "--finish-update", Environment.ProcessId.ToString(), AppFolder },
                WorkingDirectory = newApp,
                UseShellExecute = false,
            });
            Application.Current.Shutdown();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Set(Phase.Failed, e.Message);
        }
    }

    /// Downloads, verifies and unpacks the update; returns the folder with
    /// the new OpenTypist.exe.
    public static async Task<string> Prepare(Release release, IProgress<string>? progress = null, string? work = null)
    {
        work ??= Path.Combine(Path.GetTempPath(), $"OpenTypistUpdate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        var zip = Path.Combine(work, Versions.WindowsZip(release.Version));
        using (var response = await Http.GetAsync(release.Zip, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? 0;
            await using var input = await response.Content.ReadAsStreamAsync();
            await using var output = File.Create(zip);
            var buffer = new byte[1 << 20];
            long received = 0;
            int lastPercent = -1;
            for (int read; (read = await input.ReadAsync(buffer)) > 0;)
            {
                await output.WriteAsync(buffer.AsMemory(0, read));
                received += read;
                int percent = total > 0 ? (int)(received * 100 / total) : 0;
                if (percent != lastPercent) progress?.Report($"Downloading {release.Version}... {percent}%");
                lastPercent = percent;
            }
        }
        var checksum = await Http.GetStringAsync(release.Checksum);
        progress?.Report("Checking the download...");
        return Unpack(zip, checksum, release.Version, work);
    }

    /// Verifies `zip` against a `sha256sum`-style line, unpacks it into
    /// `work` and checks the app inside is `version`.
    public static string Unpack(string zip, string checksum, string version, string work)
    {
        var expected = checksum.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? "";
        string actual;
        using (var stream = File.OpenRead(zip)) actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (expected.Length != 64 || actual != expected)
        {
            throw new InvalidDataException("The download didn't match its published checksum, so it wasn't installed.");
        }
        var extracted = Path.Combine(work, "app");
        // Refuses entries that would land outside the folder.
        ZipFile.ExtractToDirectory(zip, extracted);
        // The zip holds an OpenTypist folder (or the files directly).
        var folder = File.Exists(Path.Combine(extracted, "OpenTypist.exe"))
            ? extracted
            : Directory.GetDirectories(extracted).FirstOrDefault(d => File.Exists(Path.Combine(d, "OpenTypist.exe")))
              ?? throw new InvalidDataException("The download doesn't contain OpenTypist.exe.");
        var info = FileVersionInfo.GetVersionInfo(Path.Combine(folder, "OpenTypist.exe"));
        if (info.ProductName != "OpenTypist" || Clean(info.ProductVersion ?? "") != version)
        {
            throw new InvalidDataException($"The downloaded app isn't OpenTypist {version} ({info.ProductName} {info.ProductVersion}).");
        }
        return folder;
    }

    /// Whether this user can move and recreate the app folder.
    public static bool CanReplace(string folder)
    {
        var parent = Path.GetDirectoryName(folder);
        if (parent is null) return false;
        try
        {
            var probe = Path.Combine(parent, $".opentypist-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            probe = Path.Combine(folder, $".opentypist-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// `--finish-update <pid> <app folder>`, run from the new version's temp
    /// folder (`source`, the folder of the running exe unless given). Returns
    /// the exit code.
    public static int FinishUpdate(string[] args, bool relaunch = true, string? source = null)
    {
        int index = Array.IndexOf(args, "--finish-update");
        if (index < 0 || index + 2 >= args.Length || !int.TryParse(args[index + 1], out int pid)) return 2;
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[index + 2]));
        source ??= AppFolder;
        var log = new List<string>();
        try
        {
            if (pid > 0)
            {
                try { using var old = Process.GetProcessById(pid); old.WaitForExit(30_000); }
                catch (ArgumentException) { } // already gone
            }
            var backup = target + ".old";
            DeleteFolder(backup);
            bool moved = Retry(() => Directory.Move(target, backup));
            if (moved)
            {
                try { CopyFolder(source, target); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Put the old version back.
                    DeleteFolder(target);
                    Directory.Move(backup, target);
                    throw;
                }
            }
            else
            {
                // Something holds the folder open (an Explorer window, a
                // terminal): replace the files inside it instead.
                log.Add("folder in use, copying over it");
                CopyFolder(source, target);
            }
            if (relaunch)
            {
                Process.Start(new ProcessStartInfo(Path.Combine(target, "OpenTypist.exe"))
                {
                    ArgumentList = { "--after-update", moved ? backup : "", DownloadFolder(source) },
                    WorkingDirectory = target,
                    UseShellExecute = false,
                });
            }
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            log.Add(e.Message);
            if (relaunch && File.Exists(Path.Combine(target, "OpenTypist.exe")))
            {
                Process.Start(new ProcessStartInfo(Path.Combine(target, "OpenTypist.exe")) { WorkingDirectory = target, UseShellExecute = false });
            }
            return 1;
        }
        finally
        {
            try { File.WriteAllLines(Path.Combine(Path.GetTempPath(), "OpenTypist-update.log"), log); } catch (IOException) { }
        }
    }

    /// The OpenTypistUpdate-… folder `Prepare` downloaded into, if `folder`
    /// is inside one.
    private static string DownloadFolder(string folder)
    {
        for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
        {
            if (dir.Name.StartsWith("OpenTypistUpdate-", StringComparison.Ordinal)) return dir.FullName;
        }
        return "";
    }

    /// `--after-update <old folder> <download folder>`: the new version
    /// cleans up in the background (the helper may still be exiting).
    public static void CleanUpAfterUpdate(string[] args)
    {
        int index = Array.IndexOf(args, "--after-update");
        if (index < 0) return;
        var folders = args.Skip(index + 1).Take(2).Where(a => a.Length > 0 && !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        AppStatus.Note($"Updated to OpenTypist {CurrentVersion}");
        Task.Run(async () =>
        {
            foreach (var folder in folders)
            {
                // Only folders the updater made: "<app>.old" and the download.
                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
                if (!name.EndsWith(".old", StringComparison.Ordinal) && !name.StartsWith("OpenTypistUpdate-", StringComparison.Ordinal)) continue;
                for (int attempt = 0; attempt < 30 && Directory.Exists(folder); attempt++)
                {
                    DeleteFolder(folder);
                    if (Directory.Exists(folder)) await Task.Delay(1000);
                }
            }
        });
    }

    private static bool Retry(Action action)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(500);
            }
        }
        return false;
    }

    private static void CopyFolder(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        }
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            if (!Retry(() => File.Copy(file, destination, overwrite: true))) throw new IOException($"Couldn't replace {destination}");
        }
    }

    private static void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private void Set(Phase phase, string message)
    {
        State = phase;
        Message = message;
        Changed?.Invoke();
    }
}
