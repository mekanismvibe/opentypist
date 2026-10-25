using System.Globalization;
using System.Text.Json;

namespace OpenTypist;

/// Local usage counts (never text), saved as JSON. A port of `Stats` in
/// Settings.swift.
public sealed class Stats
{
    public sealed class Data
    {
        public int WordsAccepted { get; set; }
        public int CharactersInserted { get; set; }
        public int AcceptKeyPresses { get; set; }
        public int SuggestionsShown { get; set; }
        public int WordsToday { get; set; }
        public int CorrectionsMade { get; set; }
        /// Words accepted per day ("yyyy-MM-dd" → count), last 120 days.
        public Dictionary<string, int> DailyWords { get; set; } = [];
        public string Day { get; set; } = Today();
    }

    private readonly string path;
    private readonly object gate = new();
    private readonly object fileGate = new();
    private Data data;
    private readonly Func<DateTime> now;
    /// Writes a couple of seconds after the last change, off the keyboard
    /// hook thread.
    private readonly Timer saveTimer;

    /// Raised on the thread that recorded the change.
    public event Action? Changed;

    public Stats(string path, Func<DateTime>? now = null)
    {
        this.path = path;
        this.now = now ?? (() => DateTime.Now);
        saveTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        try
        {
            data = File.Exists(path) ? JsonSerializer.Deserialize<Data>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            data = new();
        }
    }

    public int WordsAccepted { get { lock (gate) return data.WordsAccepted; } }
    public int CharactersInserted { get { lock (gate) return data.CharactersInserted; } }
    public int AcceptKeyPresses { get { lock (gate) return data.AcceptKeyPresses; } }
    public int SuggestionsShown { get { lock (gate) return data.SuggestionsShown; } }
    public int CorrectionsMade { get { lock (gate) return data.CorrectionsMade; } }

    /// Typing the accepted text by hand would have cost one key per character.
    public int KeystrokesSaved { get { lock (gate) return Math.Max(0, data.CharactersInserted - data.AcceptKeyPresses); } }

    public int WordsAcceptedToday { get { lock (gate) return data.Day == DayKey(now()) ? data.WordsToday : 0; } }

    /// Accept key presses per suggestion shown, or null before any.
    public double? AcceptRate
    {
        get
        {
            lock (gate) return data.SuggestionsShown > 0 ? Math.Min(1, (double)data.AcceptKeyPresses / data.SuggestionsShown) : null;
        }
    }

    public void RecordShown()
    {
        lock (gate) data.SuggestionsShown++;
        Save();
    }

    public void RecordAccepted(string text)
    {
        lock (gate)
        {
            RollDay();
            int words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            data.WordsAccepted += words;
            data.WordsToday += words;
            data.DailyWords[data.Day] = data.DailyWords.GetValueOrDefault(data.Day) + words;
            while (data.DailyWords.Count > 120) data.DailyWords.Remove(data.DailyWords.Keys.Min()!);
            data.CharactersInserted += text.Length;
            data.AcceptKeyPresses++;
        }
        Save();
    }

    public void RecordCorrection()
    {
        lock (gate) data.CorrectionsMade++;
        Save();
    }

    /// The last `days` days, oldest first, with zero for days without typing.
    public List<(DateTime Date, int Words)> RecentDays(int days)
    {
        lock (gate)
        {
            var today = now().Date;
            return Enumerable.Range(0, days).Reverse()
                .Select(offset => today.AddDays(-offset))
                .Select(date => (date, data.DailyWords.GetValueOrDefault(DayKey(date))))
                .ToList();
        }
    }

    public void Reset()
    {
        lock (gate) data = new Data { Day = DayKey(now()) };
        Save();
    }

    /// For screenshots and tests only.
    public void SetDailyWordsForPreview(Dictionary<string, int> days)
    {
        lock (gate) data.DailyWords = days;
    }

    private void RollDay()
    {
        var today = DayKey(now());
        if (today == data.Day) return;
        data.Day = today;
        data.WordsToday = 0;
    }

    private void Save()
    {
        saveTimer.Change(2000, Timeout.Infinite);
        Changed?.Invoke();
    }

    /// Writes the counts now (also on quit).
    public void Flush()
    {
        lock (fileGate)
        {
            string json;
            lock (gate) json = JsonSerializer.Serialize(data);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path + ".tmp", json);
                File.Move(path + ".tmp", path, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string Today() => DayKey(DateTime.Now);

    public static string DayKey(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
