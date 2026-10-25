using System.Text.RegularExpressions;

namespace OpenTypist;

public sealed record WritingSample(Guid Id, DateTime Date, string? AppId, string AppName, string? WindowTitle, string Text);

/// Opt-in samples of what the user wrote, used as style examples in prompts.
/// A port of WritingSamples.swift.
///
/// A sample is taken when the user leaves a text field, or when the field is
/// cleared (a chat message was sent). Only text from fields OpenTypist was
/// already reading is kept; disabled apps and password fields never are.
/// Stored encrypted (`SecureStore`), at most 400 samples. UI thread only.
public sealed class WritingSamples
{
    public const int MaxSamples = 400;
    public const int MinLength = 40;
    public const int MaxLength = 600;
    /// Characters of examples to put in a prompt.
    public const int PromptBudget = 600;
    private const string FileName = "samples.bin";

    private readonly SecureStore store;
    private readonly AppSettings settings;
    /// The field being typed in and its latest text.
    private (string Key, WritingSample Sample)? current;
    private (string Key, List<string> Examples)? exampleCache;

    public List<WritingSample> Samples { get; private set; }
    public event Action? Changed;

    public WritingSamples(AppSettings settings, SecureStore? store = null)
    {
        this.settings = settings;
        this.store = store ?? SecureStore.Shared;
        Samples = this.store.Load<List<WritingSample>>(FileName) ?? [];
    }

    public bool IsEnabled => settings.KeepWritingSamples;

    /// Called whenever OpenTypist reads the focused field.
    public void Observe(FocusContext context)
    {
        if (!IsEnabled) { current = null; return; }
        var key = $"{context.ProcessId}:{context.FieldId}";
        var sample = new WritingSample(Guid.NewGuid(), DateTime.Now, context.AppId, context.AppName, context.WindowTitle, context.TextBefore);
        if (current is { } previous && previous.Key == key)
        {
            // Field cleared or replaced, e.g. a chat message was sent. (Not
            // when it shrank by deleting back: that's editing.) Empty is
            // everything's prefix, so it counts as cleared.
            var old = previous.Sample.Text;
            if (sample.Text.Length < old.Length / 2 && (sample.Text.Length == 0 || !old.StartsWith(sample.Text, StringComparison.Ordinal))) Commit(previous.Sample);
        }
        else if (current is { } other)
        {
            Commit(other.Sample);
        }
        current = (key, sample);
    }

    /// Keeps what was typed in the current field (on app switch or quit).
    public void Flush()
    {
        if (current is { } previous) Commit(previous.Sample);
        current = null;
    }

    private void Commit(WritingSample sample)
    {
        if (!IsEnabled || Clean(sample.Text) is not string text) return;
        var cleaned = sample with { Text = text };
        // A draft continued later replaces its earlier, shorter sample.
        if (Samples.Count > 0 && Samples[^1] is var last && last.AppId == sample.AppId && last.WindowTitle == sample.WindowTitle
            && text.StartsWith(TextUtil.Prefix(last.Text, MinLength), StringComparison.Ordinal))
        {
            Samples[^1] = cleaned;
        }
        else if (Samples.Any(s => s.Text == text))
        {
            return;
        }
        else
        {
            Samples.Add(cleaned);
        }
        if (Samples.Count > MaxSamples) Samples.RemoveRange(0, Samples.Count - MaxSamples);
        Save();
    }

    /// Adds a sample directly (used by the self-test).
    public void Record(WritingSample sample) => Commit(sample);

    /// 13 to 19 digits, maybe grouped with spaces or dashes; ends on a digit
    /// so the space after it stays.
    private static readonly Regex CardNumber = new(@"\b\d(?:[ -]?\d){12,18}\b", RegexOptions.Compiled);
    private static readonly Regex LongToken = new(@"\S{30,}", RegexOptions.Compiled);

    /// Drops things that look like secrets or code, trims to the most recent text.
    public static string? Clean(string text)
    {
        text = text.Trim();
        if (text.Length < MinLength) return null;
        // Card-like digit runs and long unbroken tokens (keys, URLs) are removed.
        text = CardNumber.Replace(text, "…");
        text = LongToken.Replace(text, "…");
        int letters = text.Count(char.IsLetter);
        if ((double)letters / Math.Max(text.Length, 1) < 0.5) return null;
        if (text.Length > MaxLength)
        {
            text = TextUtil.Suffix(text, MaxLength);
            int space = text.IndexOf(' ');
            if (space >= 0) text = text[(space + 1)..];
        }
        return text;
    }

    /// A few samples in the user's voice for this app, most relevant first.
    /// Stable for the same app and window, so the prompt prefix stays cached.
    public List<string> Examples(string? appId, string? windowTitle)
    {
        if (!IsEnabled || Samples.Count == 0) return [];
        var key = $"{appId}:{windowTitle}:{Samples.Count}";
        if (exampleCache is { } cache && cache.Key == key) return cache.Examples;

        var newestFirst = Enumerable.Reverse(Samples).ToList();
        var sameWindow = newestFirst.Where(s => s.AppId == appId && s.WindowTitle == windowTitle && windowTitle is not null).ToList();
        var sameApp = newestFirst.Where(s => s.AppId == appId && !sameWindow.Contains(s)).ToList();
        var pool = sameWindow.Concat(sameApp).ToList();
        if (pool.Count == 0) pool = newestFirst.Take(2).ToList();

        var result = new List<string>();
        int used = 0;
        foreach (var sample in pool.Take(3))
        {
            var text = sample.Text;
            if (text.Length > 220)
            {
                text = TextUtil.Prefix(text, 220);
                int space = text.LastIndexOf(' ');
                if (space > 0) text = text[..space];
                text += "…";
            }
            if (used + text.Length > PromptBudget) break;
            result.Add(text);
            used += text.Length;
        }
        exampleCache = (key, result);
        return result;
    }

    public void Delete(WritingSample sample)
    {
        Samples.RemoveAll(s => s.Id == sample.Id);
        Save();
    }

    public void DeleteAll()
    {
        Samples = [];
        current = null;
        exampleCache = null;
        store.Delete(FileName);
        Changed?.Invoke();
    }

    private void Save()
    {
        exampleCache = null;
        store.Save(FileName, Samples);
        Changed?.Invoke();
    }
}
