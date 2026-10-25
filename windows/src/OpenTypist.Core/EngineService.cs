using System.Collections.Concurrent;
using System.Diagnostics;

namespace OpenTypist;

/// Runs `LlamaEngine` on its own thread. Each request gets an id; a newer
/// request cancels older ones between tokens. A port of EngineService.swift.
/// Callbacks run on the SynchronizationContext the service was created on
/// (the UI thread in the app).
public sealed class EngineService : IDisposable
{
    public enum Status { Idle, Loading, Ready, Failed }

    public sealed record State(Status Status, string Detail = "")
    {
        public static readonly State Idle = new(Status.Idle);
    }

    public sealed class Request
    {
        public required string TextBefore { get; init; }
        public string? AppName { get; init; }
        public string? WindowTitle { get; init; }
        public string? ScreenText { get; init; }
        public string? Profile { get; init; }
        public IReadOnlyList<string> Examples { get; init; } = [];
        public CompletionLength Length { get; init; } = CompletionLength.Medium;
        public float MinProbability { get; init; }
        public float? WordMinProbability { get; init; }
        /// Text after the caret on the same line, for mid-line suggestions.
        public string TextAfter { get; init; } = "";
    }

    private readonly BlockingCollection<Action> queue = new();
    private readonly Thread thread;
    private readonly LlamaEngine engine = new();
    private readonly SynchronizationContext? context;
    private int latestId;

    /// UI thread only.
    public State CurrentState { get; private set; } = State.Idle;
    public event Action<State>? StateChanged;
    public double LastLoadSeconds { get; private set; }
    public bool UsesGpu => engine.UsesGpu;

    public bool IsReady => CurrentState.Status == Status.Ready;

    public EngineService()
    {
        context = SynchronizationContext.Current;
        thread = new Thread(() =>
        {
            foreach (var work in queue.GetConsumingEnumerable()) work();
        })
        { IsBackground = true, Name = "OpenTypist engine", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    private void Post(Action action)
    {
        if (context is null) action();
        else context.Post(_ => action(), null);
    }

    public void Load(string path, bool useGpu = true)
    {
        CancelAll();
        SetState(new State(Status.Loading, Path.GetFileName(path)));
        var started = Stopwatch.StartNew();
        queue.Add(() =>
        {
            try
            {
                engine.Load(path, useGpu);
                engine.WarmUp();
                var name = engine.ModelDescription;
                Post(() =>
                {
                    LastLoadSeconds = started.Elapsed.TotalSeconds;
                    SetState(new State(Status.Ready, name));
                });
            }
            catch (Exception e)
            {
                Post(() => SetState(new State(Status.Failed, e.Message)));
            }
        });
    }

    public void Unload()
    {
        CancelAll();
        queue.Add(engine.Unload);
        SetState(State.Idle);
    }

    /// Frees the model before the process exits; ggml's GPU backends can
    /// assert if they are torn down while a model is still loaded.
    public void Shutdown()
    {
        CancelAll();
        using var done = new ManualResetEventSlim();
        try
        {
            queue.Add(() => { engine.Unload(); done.Set(); });
            done.Wait(TimeSpan.FromSeconds(10));
        }
        catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        Shutdown();
        queue.CompleteAdding();
    }

    public int CancelAll() => Interlocked.Increment(ref latestId);

    private bool IsLatest(int id) => Volatile.Read(ref latestId) == id;

    /// Calls `completion` with the cleaned suggestion, or null if there is
    /// nothing worth showing. Not called if superseded. `onPartial` streams
    /// the suggestion as it grows, one complete word at a time; `completion`
    /// then delivers the final text.
    public void Complete(Request request, Action<string?> completion, Action<string>? onPartial = null)
    {
        int id = CancelAll();
        queue.Add(() =>
        {
            if (!IsLatest(id) || !engine.IsLoaded) return;
            Action<string>? partial = onPartial is null ? null : text => Post(() =>
            {
                if (IsLatest(id)) onPartial(text);
            });
            var text = Run(engine, request, () => IsLatest(id), out bool cancelled, partial);
            if (cancelled) return;
            Post(() =>
            {
                if (IsLatest(id)) completion(text);
            });
        });
    }

    /// The whole suggestion pipeline for one request: prompt, generation,
    /// confidence cut, clean-up and the mid-line fit. Shared with --selftest.
    /// `onPartial` gets each new streaming text on the engine thread.
    public static string? Run(LlamaEngine engine, Request request, Func<bool> shouldContinue, out bool cancelled,
                              Action<string>? onPartial = null)
    {
        var prompt = PromptBuilder.Build(request.TextBefore, request.AppName, request.WindowTitle, request.ScreenText,
                                         request.Profile, request.Examples);
        int maxWords = request.Length.MaxWords();
        string? lastPartial = null;
        var output = engine.Complete(prompt.Text, request.Length.MaxTokens(), prompt.WordPrefix, shouldContinue, text =>
        {
            if (onPartial is not null && PromptBuilder.StreamingText(text, prompt, maxWords, request.TextAfter) is string partial
                && partial != lastPartial)
            {
                lastPartial = partial;
                onPartial(partial);
            }
            return PromptBuilder.ShouldStop(text, maxWords);
        }, request.MinProbability, request.WordMinProbability);
        cancelled = output is null;
        if (output is null) return null;
        var confident = PromptBuilder.ConfidentText(output.Tokens, output.HitTokenLimit, request.MinProbability, request.WordMinProbability);
        var text = PromptBuilder.Clean(confident.Text, prompt, maxWords, confident.HitTokenLimit);
        if (text is not null && request.TextAfter.Length > 0)
        {
            text = PromptBuilder.FitBefore(request.TextAfter, text);
        }
        return text;
    }

    public sealed class ScoreRequest
    {
        public required string Context { get; init; }
        public string? AppName { get; init; }
        public string? WindowTitle { get; init; }
        public string? ScreenText { get; init; }
        public string? Profile { get; init; }
        public IReadOnlyList<string> Examples { get; init; } = [];
        public required IReadOnlyList<string> Candidates { get; init; }
    }

    /// Log-probability of each candidate word following the context (null if
    /// the model isn't available). Not cancelled by typing.
    public void Score(ScoreRequest request, Action<double[]?> completion)
    {
        queue.Add(() =>
        {
            double[]? result = null;
            if (engine.IsLoaded)
            {
                var prompt = PromptBuilder.ScoringPrompt(request.Context, request.AppName, request.WindowTitle, request.ScreenText,
                                                         request.Profile, request.Examples);
                result = engine.Score(prompt.Text, request.Candidates.Select(c => prompt.WordPrefix + c).ToList());
            }
            Post(() => completion(result));
        });
    }

    private void SetState(State state)
    {
        CurrentState = state;
        StateChanged?.Invoke(state);
    }
}
