using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace OpenTypist;

/// Downloads, verifies and finds GGUF model files under
/// %LOCALAPPDATA%\OpenTypist\Models. Nothing is downloaded unless the user
/// asks for it. A port of ModelManager.swift; an interrupted download resumes.
public sealed class ModelManager
{
    public enum Phase { None, Downloading, Verifying, Failed }

    public sealed record DownloadState(Phase Phase, string ModelId = "", long Received = 0, long Total = 0, string Message = "")
    {
        public static readonly DownloadState None = new(Phase.None);
        public int Percent => Total > 0 ? (int)(Received * 100 / Total) : 0;
    }

    private readonly SynchronizationContext? context = SynchronizationContext.Current;
    private CancellationTokenSource? cancel;

    public string ModelsDirectory { get; }
    public DownloadState Download { get; private set; } = DownloadState.None;
    /// Raised (on the creating thread's context) when `Download` changes.
    public event Action? DownloadChanged;
    /// Raised when a download finished and verified.
    public event Action<ModelInfo>? Installed;

    public ModelManager(string? modelsDirectory = null)
    {
        ModelsDirectory = modelsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenTypist", "Models");
    }

    public string FileFor(ModelInfo model) => Path.Combine(ModelsDirectory, model.FileName);

    public bool IsDownloaded(ModelInfo model) => File.Exists(FileFor(model));

    public IEnumerable<ModelInfo> DownloadedModels => ModelCatalog.All.Where(IsDownloaded);

    public bool IsDownloading => Download.Phase is Phase.Downloading or Phase.Verifying;

    /// The file to load: a custom .gguf if set and present, else the selected
    /// catalog model, else any downloaded one.
    public string? ActiveModelPath(string? selectedId, string? customPath)
    {
        if (!string.IsNullOrEmpty(customPath) && File.Exists(customPath)) return customPath;
        var selected = ModelCatalog.Model(selectedId);
        if (selected is not null && IsDownloaded(selected)) return FileFor(selected);
        return DownloadedModels.Select(FileFor).FirstOrDefault();
    }

    public void Delete(ModelInfo model)
    {
        try { File.Delete(FileFor(model)); } catch (IOException) { }
        try { File.Delete(FileFor(model) + ".partial"); } catch (IOException) { }
    }

    public void CancelDownload()
    {
        cancel?.Cancel();
        cancel = null;
        SetState(DownloadState.None);
    }

    public Task StartDownload(ModelInfo model)
    {
        if (IsDownloading) return Task.CompletedTask;
        var source = new CancellationTokenSource();
        cancel = source;
        SetState(new DownloadState(Phase.Downloading, model.Id, 0, model.Size));
        return Task.Run(() => DownloadAsync(model, source.Token));
    }

    private async Task DownloadAsync(ModelInfo model, CancellationToken token)
    {
        var partial = FileFor(model) + ".partial";
        try
        {
            Directory.CreateDirectory(ModelsDirectory);
            using var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None });
            http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenTypist-Windows");
            http.Timeout = Timeout.InfiniteTimeSpan;

            long existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            if (existing > model.Size) { File.Delete(partial); existing = 0; }
            using var request = new HttpRequestMessage(HttpMethod.Get, model.Url);
            if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing == model.Size)
            {
                // Already complete; just verify.
            }
            else
            {
                if (!response.IsSuccessStatusCode) throw new IOException($"Download failed (HTTP {(int)response.StatusCode})");
                bool resumed = response.StatusCode == HttpStatusCode.PartialContent;
                if (!resumed) existing = 0;
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using var output = new FileStream(partial, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                var buffer = new byte[1 << 20];
                long received = existing;
                var report = Stopwatch.StartNew();
                int n;
                while ((n = await input.ReadAsync(buffer, token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), token);
                    received += n;
                    if (report.ElapsedMilliseconds > 250)
                    {
                        report.Restart();
                        SetState(new DownloadState(Phase.Downloading, model.Id, received, model.Size));
                    }
                }
            }

            SetState(new DownloadState(Phase.Verifying, model.Id, model.Size, model.Size));
            Verify(partial, model);
            File.Move(partial, FileFor(model), overwrite: true);
            SetState(DownloadState.None);
            Post(() => Installed?.Invoke(model));
        }
        catch (OperationCanceledException)
        {
            // Keep the partial file so the download can resume.
        }
        catch (Exception e)
        {
            SetState(new DownloadState(Phase.Failed, model.Id, 0, model.Size, e.Message));
        }
    }

    /// Checks size and SHA-256; a file that fails is deleted.
    public static void Verify(string path, ModelInfo model)
    {
        long size = new FileInfo(path).Length;
        if (size != model.Size)
        {
            throw new IOException($"Downloaded file has the wrong size ({size} bytes)");
        }
        string digest;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8 << 20))
        {
            digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        if (digest != model.Sha256)
        {
            File.Delete(path);
            throw new IOException("Downloaded file failed the checksum check");
        }
    }

    private void SetState(DownloadState state) => Post(() =>
    {
        // A cancelled download may still report progress once; ignore it.
        if (cancel is null && state.Phase == Phase.Downloading) return;
        Download = state;
        DownloadChanged?.Invoke();
    });

    private void Post(Action action)
    {
        if (context is null) action();
        else context.Post(_ => action(), null);
    }
}
