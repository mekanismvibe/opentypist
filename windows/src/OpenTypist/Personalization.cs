using System.Text.Json;

namespace OpenTypist;

/// One saved version of the profile.
public sealed record ProfileVersion(Guid Id, DateTime Date, string Text, ProfileVersion.Origin Source, string Note)
{
    public enum Origin { Initial, You, OpenTypist, Restored }
}

public enum ProfileUpdateMode { Automatic, AskFirst }

/// The profile ("system prompt"): notes about the user that go at the top of
/// every prompt.
public sealed class ProfileState
{
    public string Text { get; set; } = "";
    public bool Locked { get; set; }
    public ProfileUpdateMode Mode { get; set; } = ProfileUpdateMode.Automatic;
    public List<ProfileVersion> History { get; set; } = [];
    /// OpenTypist's learned lines, by topic, exactly as last written. A line
    /// that no longer appears unchanged was edited or deleted by the user,
    /// and its topic becomes theirs.
    public Dictionary<string, string> GeneratedLines { get; set; } = [];
    public HashSet<string> UserOwnedTopics { get; set; } = [];
    public string? Proposal { get; set; }
    public string? ProposalNote { get; set; }
    public Dictionary<string, string>? ProposalLines { get; set; }
    public string? DismissedProposal { get; set; }

    public ProfileState Copy() => JsonSerializer.Deserialize<ProfileState>(JsonSerializer.Serialize(this))!;
}

/// What OpenTypist has noticed about how the user types. Only ever fed from
/// the user's own keystrokes, never from text on screen.
public sealed class LearnedData
{
    public sealed class AppStyle
    {
        public string Name { get; set; } = "";
        public int LowercaseStarts { get; set; }
        public int UppercaseStarts { get; set; }
    }

    /// Capitalized words used mid-sentence (people, places, products).
    public Dictionary<string, int> Names { get; set; } = [];
    /// Words the dictionary doesn't know, lowercased → count / usual spelling.
    public Dictionary<string, int> Words { get; set; } = [];
    public Dictionary<string, string> WordForms { get; set; } = [];
    public int AmericanSpellings { get; set; }
    public int BritishSpellings { get; set; }
    public Dictionary<string, int> Greetings { get; set; } = [];
    public Dictionary<string, int> SignOffs { get; set; } = [];
    public Dictionary<string, AppStyle> AppStyles { get; set; } = [];
    /// Words autocorrect must leave alone (undone fixes, added by hand).
    public HashSet<string> NeverCorrect { get; set; } = [];
}

/// The profile and what was learned from typing, a port of
/// Personalization.swift. Learning is called from the keyboard hook thread,
/// everything else from the UI thread; the state is locked, and saving and
/// profile updates run on timers. Encrypted at rest (`SecureStore`).
public sealed class Personalization
{
    public const string DefaultProfile = "I write clearly and naturally, in my own voice. In chats I keep things short and friendly; in emails I'm polite and to the point.";
    public const int MaxProfileLength = 1000;
    /// Times a name or word must be typed before it counts as learned.
    public const int LearnThreshold = 3;

    private readonly SecureStore store;
    private readonly AppSettings settings;
    private readonly object gate = new();
    private readonly SynchronizationContext? context = SynchronizationContext.Current;
    private ProfileState profile;
    private LearnedData learned;
    private readonly Timer saveTimer;
    private readonly Timer updateTimer;
    private bool updateScheduled;
    private DateTime lastUpdate = DateTime.MinValue;

    /// Raised on the creating thread's context when the profile or learned data change.
    public event Action? Changed;

    public Personalization(AppSettings settings, SecureStore? store = null)
    {
        this.settings = settings;
        this.store = store ?? SecureStore.Shared;
        saveTimer = new Timer(_ => SaveLearned(), null, Timeout.Infinite, Timeout.Infinite);
        updateTimer = new Timer(_ =>
        {
            lock (gate) updateScheduled = false;
            UpdateProfile();
        }, null, Timeout.Infinite, Timeout.Infinite);
        learned = this.store.Load<LearnedData>("learned.bin") ?? new LearnedData();
        // Phase 2 kept the never-correct words in a file of their own.
        if (this.store.Load<List<string>>("never-correct.bin") is { } older)
        {
            foreach (var word in older) learned.NeverCorrect.Add(word.ToLowerInvariant());
            this.store.Save("learned.bin", learned);
            this.store.Delete("never-correct.bin");
        }
        profile = this.store.Load<ProfileState>("profile.bin") ?? new ProfileState
        {
            Text = DefaultProfile,
            History = [new ProfileVersion(Guid.NewGuid(), DateTime.Now, DefaultProfile, ProfileVersion.Origin.Initial, "Starting profile")],
        };
    }

    /// Copies for display and tests.
    public ProfileState Profile { get { lock (gate) return profile.Copy(); } }
    public LearnedData Learned { get { lock (gate) return JsonSerializer.Deserialize<LearnedData>(JsonSerializer.Serialize(learned))!; } }

    /// The profile text for prompts, if the user wants it used.
    public string? PromptProfile
    {
        get
        {
            if (!settings.UseProfile) return null;
            lock (gate)
            {
                var text = profile.Text.Trim();
                return text.Length > 0 ? text : null;
            }
        }
    }

    // Learning

    private static readonly HashSet<string> NameStoplist =
    [
        "I", "I'm", "I'll", "I've", "I'd", "OK", "Ok", "Mr", "Mrs", "Ms", "Dr", "Monday", "Tuesday", "Wednesday",
        "Thursday", "Friday", "Saturday", "Sunday", "January", "February", "March", "April", "May", "June", "July",
        "August", "September", "October", "November", "December", "English", "God",
    ];

    /// American → British pairs with a clear preference signal.
    private static readonly (string American, string British)[] SpellingPairs =
    [
        ("color", "colour"), ("colors", "colours"), ("favorite", "favourite"), ("favorites", "favourites"),
        ("honor", "honour"), ("labor", "labour"), ("neighbor", "neighbour"), ("neighbors", "neighbours"),
        ("behavior", "behaviour"), ("flavor", "flavour"), ("humor", "humour"), ("favor", "favour"),
        ("rumor", "rumour"), ("center", "centre"), ("theater", "theatre"), ("organize", "organise"),
        ("organized", "organised"), ("organization", "organisation"), ("realize", "realise"),
        ("realized", "realised"), ("recognize", "recognise"), ("apologize", "apologise"), ("analyze", "analyse"),
        ("traveled", "travelled"), ("traveling", "travelling"), ("canceled", "cancelled"),
        ("canceling", "cancelling"), ("gray", "grey"), ("defense", "defence"), ("catalog", "catalogue"),
        ("jewelry", "jewellery"), ("aluminum", "aluminium"), ("pajamas", "pyjamas"), ("fulfill", "fulfil"),
        ("enrollment", "enrolment"), ("customize", "customise"), ("prioritize", "prioritise"),
    ];

    /// true = British.
    private static readonly Dictionary<string, bool> SpellingLookup =
        SpellingPairs.SelectMany(p => new[] { (p.American, false), (p.British, true) }).ToDictionary(p => p.Item1, p => p.Item2);

    private static readonly string[] GreetingWords = ["good morning", "good afternoon", "good evening", "hi", "hey", "hello", "dear", "morning", "hiya", "yo"];
    private static readonly HashSet<string> SignOffPhrases =
    [
        "thanks", "thank you", "thanks so much", "many thanks", "thanks again", "cheers", "best", "best regards",
        "kind regards", "warm regards", "regards", "sincerely", "talk soon", "all the best", "take care", "thx",
        "ty", "love", "speak soon", "have a good one",
    ];

    /// A word the user finished typing (from keystrokes).
    public void ObserveWord(string word, bool startsSentence, string? appId, string appName)
    {
        if (!settings.LearnHabits) return;
        var letters = word.Where(char.IsLetter).ToList();
        if (letters.Count == 0) return;
        var lower = word.ToLowerInvariant();
        bool changed = false;
        // Outside the lock: about half a millisecond.
        bool? valid = null;
        bool candidate = word.Length >= 3 && word.All(c => char.IsLetter(c) || c == '\'' || c == '-');
        bool allCaps = letters.Count > 1 && letters.All(char.IsUpper);
        bool isName = candidate && !startsSentence && char.IsUpper(word[0]) && !allCaps && !NameStoplist.Contains(word);
        if (candidate && !isName) valid = SpellCheck.IsValidWord(word);

        lock (gate)
        {
            if (SpellingLookup.TryGetValue(lower, out bool british))
            {
                if (british) learned.BritishSpellings++; else learned.AmericanSpellings++;
                changed = true;
            }
            if (appId is not null && startsSentence && !NameStoplist.Contains(word))
            {
                if (!learned.AppStyles.TryGetValue(appId, out var style)) learned.AppStyles[appId] = style = new LearnedData.AppStyle();
                style.Name = appName;
                if (char.IsLower(letters[0])) style.LowercaseStarts++; else style.UppercaseStarts++;
                changed = true;
            }
            if (isName)
            {
                learned.Names[word] = learned.Names.GetValueOrDefault(word) + 1;
                changed = true;
            }
            else if (valid == false)
            {
                learned.Words[lower] = learned.Words.GetValueOrDefault(lower) + 1;
                learned.WordForms[lower] = word;
                changed = true;
            }
        }
        if (changed) LearnedChanged();
    }

    /// A line the user finished with Enter: look for greetings and sign-offs.
    public void ObserveLine(string line)
    {
        if (!settings.LearnHabits) return;
        var trimmed = line.Trim(' ', '\t');
        if (trimmed.Length == 0 || trimmed.Length > 40) return;
        var bare = trimmed.ToLowerInvariant().Trim(',', '.', '!');

        if (SignOffPhrases.Contains(bare))
        {
            lock (gate) learned.SignOffs[trimmed] = learned.SignOffs.GetValueOrDefault(trimmed) + 1;
            LearnedChanged();
            return;
        }
        foreach (var greeting in GreetingWords)
        {
            if (bare != greeting && !bare.StartsWith(greeting + " ", StringComparison.Ordinal) && !bare.StartsWith(greeting + ",", StringComparison.Ordinal)) continue;
            // "Hi Sarah," → "Hi"; keep the user's capitalization.
            var typed = trimmed[..greeting.Length];
            if (bare.Split(' ').Length <= 4)
            {
                lock (gate) learned.Greetings[typed] = learned.Greetings.GetValueOrDefault(typed) + 1;
                LearnedChanged();
            }
            return;
        }
    }

    /// The typed word turned out to be a typo (the model fixed it).
    public void ForgetObservation(string word)
    {
        var lower = word.ToLowerInvariant();
        bool changed = false;
        lock (gate)
        {
            if (learned.Words.TryGetValue(lower, out int count))
            {
                if (count > 1) learned.Words[lower] = count - 1; else learned.Words.Remove(lower);
                changed = true;
            }
            if (learned.Names.TryGetValue(word, out int names))
            {
                if (names > 1) learned.Names[word] = names - 1; else learned.Names.Remove(word);
                changed = true;
            }
        }
        if (changed) LearnedChanged();
    }

    // Never correct

    public bool IsNeverCorrect(string word)
    {
        var lower = word.ToLowerInvariant();
        lock (gate) return learned.NeverCorrect.Contains(lower) || learned.Words.GetValueOrDefault(lower) >= LearnThreshold;
    }

    /// Words added by hand or by undoing a fix.
    public List<string> NeverCorrectWords
    {
        get { lock (gate) return [.. learned.NeverCorrect.Order(StringComparer.Ordinal)]; }
    }

    /// Safe on the keyboard hook thread: the file is written on a timer.
    public void AddNeverCorrect(string word)
    {
        var lower = word.Trim().ToLowerInvariant();
        if (lower.Length == 0) return;
        lock (gate) learned.NeverCorrect.Add(lower);
        LearnedChanged(saveNow: true);
    }

    public void RemoveNeverCorrect(string word)
    {
        lock (gate)
        {
            learned.NeverCorrect.Remove(word.ToLowerInvariant());
            learned.Words.Remove(word.ToLowerInvariant());
        }
        LearnedChanged(saveNow: true);
    }

    /// Words the dictionary doesn't know that are typed often enough to keep.
    public List<string> LearnedWords
    {
        get { lock (gate) return Ranked(learned.Words).Select(w => learned.WordForms.GetValueOrDefault(w) ?? w).ToList(); }
    }

    public List<string> LearnedNames
    {
        get { lock (gate) return Ranked(learned.Names); }
    }

    /// Entries used often enough, most used first. Ties are alphabetical, so
    /// a line doesn't flip between orders and look like a profile change.
    private static List<string> Ranked(Dictionary<string, int> counts) =>
        counts.Where(p => p.Value >= LearnThreshold).OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key).ToList();

    public void RemoveLearnedName(string name)
    {
        lock (gate) learned.Names.Remove(name);
        LearnedChanged(saveNow: true);
    }

    public void ForgetLearned()
    {
        lock (gate) learned = new LearnedData { NeverCorrect = learned.NeverCorrect };
        LearnedChanged(saveNow: true);
    }

    private void LearnedChanged(bool saveNow = false)
    {
        saveTimer.Change(saveNow ? 0 : 10_000, Timeout.Infinite);
        ScheduleProfileUpdate();
        RaiseChanged();
    }

    /// Saves now and stops the timers (on quit).
    public void Flush()
    {
        saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        updateTimer.Change(Timeout.Infinite, Timeout.Infinite);
        SaveLearned();
    }

    public void SaveLearned()
    {
        LearnedData copy;
        lock (gate) copy = JsonSerializer.Deserialize<LearnedData>(JsonSerializer.Serialize(learned))!;
        store.Save("learned.bin", copy);
    }

    // Learned lines

    /// Facts for the profile, by topic, written the way a base model reads
    /// best: plain first-person statements, not instructions.
    public List<(string Topic, string Line)> GeneratedLines()
    {
        lock (gate) return GeneratedLinesLocked();
    }

    private List<(string Topic, string Line)> GeneratedLinesLocked()
    {
        var lines = new List<(string, string)>();
        var names = Ranked(learned.Names).Take(8).ToList();
        if (names.Count > 0) lines.Add(("names", $"People and things I often mention: {string.Join(", ", names)}."));
        var words = Ranked(learned.Words).Take(8).Select(w => learned.WordForms.GetValueOrDefault(w) ?? w).ToList();
        if (words.Count > 0) lines.Add(("words", $"Words I use that dictionaries may not know: {string.Join(", ", words)}."));
        int spellingTotal = learned.AmericanSpellings + learned.BritishSpellings;
        if (spellingTotal >= 5)
        {
            double britishShare = (double)learned.BritishSpellings / spellingTotal;
            if (britishShare >= 0.75) lines.Add(("spelling", "I use British spelling (colour, organise)."));
            else if (britishShare <= 0.25) lines.Add(("spelling", "I use American spelling (color, organize)."));
        }
        var greeting = Top(learned.Greetings);
        var signOff = Top(learned.SignOffs);
        if (greeting is not null && signOff is not null) lines.Add(("greetings", $"I usually start messages with \"{greeting}\" and sign off with \"{signOff}\"."));
        else if (greeting is not null) lines.Add(("greetings", $"I usually start messages with \"{greeting}\"."));
        else if (signOff is not null) lines.Add(("greetings", $"I usually sign off with \"{signOff}\"."));
        var casualApps = learned.AppStyles.Values
            .Where(s => s.LowercaseStarts + s.UppercaseStarts >= 15 && (double)s.LowercaseStarts / (s.LowercaseStarts + s.UppercaseStarts) >= 0.6)
            .Select(s => s.Name).Order(StringComparer.Ordinal).ToList();
        if (casualApps.Count > 0) lines.Add(("style", $"In {List(casualApps)} I write casually, mostly in lowercase."));
        return lines;
    }

    /// Most used entry if it has been seen enough and clearly dominates.
    private static string? Top(Dictionary<string, int> counts)
    {
        var best = Ranked(counts).FirstOrDefault();
        if (best is null) return null;
        int total = counts.Values.Sum();
        return (double)counts[best] / total >= 0.5 ? best : null;
    }

    private static string List(List<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };

    // Profile updates

    private void ScheduleProfileUpdate()
    {
        // At most every ten minutes while typing.
        lock (gate)
        {
            if (updateScheduled || DateTime.Now - lastUpdate < TimeSpan.FromMinutes(10)) return;
            updateScheduled = true;
        }
        updateTimer.Change(30_000, Timeout.Infinite);
    }

    /// Rewrites OpenTypist's learned lines at the end of the profile, keeping
    /// everything the user wrote. Returns a short description of what changed.
    public string? UpdateProfile()
    {
        string? note;
        lock (gate)
        {
            lastUpdate = DateTime.Now;
            if (profile.Locked) return null;

            var state = profile.Copy();
            var currentLines = state.Text.Split('\n').ToList();
            // Learned lines the user edited or deleted are theirs from now on.
            foreach (var (topic, line) in state.GeneratedLines)
            {
                if (!currentLines.Contains(line)) state.UserOwnedTopics.Add(topic);
            }
            var previous = state.GeneratedLines.Where(p => !state.UserOwnedTopics.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
            var userLines = currentLines.Where(line => !previous.ContainsValue(line)).ToList();
            while (userLines.Count > 0 && userLines[^1].Trim().Length == 0) userLines.RemoveAt(userLines.Count - 1);

            var fresh = new Dictionary<string, string>();
            var freshOrdered = new List<string>();
            foreach (var (topic, line) in GeneratedLinesLocked())
            {
                if (state.UserOwnedTopics.Contains(topic)) continue;
                fresh[topic] = line;
                freshOrdered.Add(line);
            }
            var newText = string.Join("\n", userLines.Concat(freshOrdered));
            note = DescribeChange(previous, fresh);
            if (newText == state.Text || note is null)
            {
                state.GeneratedLines = fresh;
                profile = state;
                SaveProfileLocked();
                return null;
            }

            if (state.Mode == ProfileUpdateMode.Automatic)
            {
                state.GeneratedLines = fresh;
                state.Text = newText;
                state.Proposal = null;
                AppendHistory(state, ProfileVersion.Origin.OpenTypist, note);
            }
            else
            {
                if (newText == state.DismissedProposal) return null;
                state.Proposal = newText;
                state.ProposalNote = note;
                state.ProposalLines = fresh;
            }
            profile = state;
            SaveProfileLocked();
        }
        RaiseChanged();
        return note;
    }

    private static string? DescribeChange(Dictionary<string, string> old, Dictionary<string, string> fresh)
    {
        var added = fresh.Keys.Where(k => !old.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
        var changed = fresh.Where(p => old.TryGetValue(p.Key, out var o) && o != p.Value).Select(p => p.Key).Order(StringComparer.Ordinal).ToList();
        var removed = old.Keys.Where(k => !fresh.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
        var parts = new List<string>();
        if (added.Count > 0) parts.Add("learned " + string.Join(", ", added.Select(TopicName)));
        if (changed.Count > 0) parts.Add("updated " + string.Join(", ", changed.Select(TopicName)));
        if (removed.Count > 0) parts.Add("dropped " + string.Join(", ", removed.Select(TopicName)));
        if (parts.Count == 0) return null;
        var text = string.Join("; ", parts);
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static string TopicName(string topic) => topic switch
    {
        "words" => "your words",
        "greetings" => "greetings and sign-offs",
        "style" => "chat style",
        _ => topic,
    };

    // Editing

    public void SaveProfileText(string text)
    {
        var trimmed = TextUtil.Prefix(text.Replace("\r\n", "\n"), MaxProfileLength);
        lock (gate)
        {
            if (trimmed == profile.Text) return;
            profile.Text = trimmed;
            profile.Proposal = null;
            AppendHistory(profile, ProfileVersion.Origin.You, "Edited by you");
            SaveProfileLocked();
        }
        RaiseChanged();
    }

    public void SetLocked(bool locked)
    {
        lock (gate)
        {
            profile.Locked = locked;
            if (locked) profile.Proposal = null;
            SaveProfileLocked();
        }
        RaiseChanged();
    }

    public void SetMode(ProfileUpdateMode mode)
    {
        lock (gate)
        {
            profile.Mode = mode;
            SaveProfileLocked();
        }
        RaiseChanged();
    }

    public void ApplyProposal()
    {
        lock (gate)
        {
            if (profile.Proposal is not string proposal) return;
            profile.Text = proposal;
            profile.GeneratedLines = profile.ProposalLines ?? profile.GeneratedLines;
            profile.Proposal = null;
            AppendHistory(profile, ProfileVersion.Origin.OpenTypist, profile.ProposalNote ?? "Update from OpenTypist");
            SaveProfileLocked();
        }
        RaiseChanged();
    }

    public void DismissProposal()
    {
        lock (gate)
        {
            profile.DismissedProposal = profile.Proposal;
            profile.Proposal = null;
            SaveProfileLocked();
        }
        RaiseChanged();
    }

    public void Restore(ProfileVersion version)
    {
        lock (gate)
        {
            profile.Text = version.Text;
            profile.Proposal = null;
            AppendHistory(profile, ProfileVersion.Origin.Restored, $"Restored the version from {version.Date:d MMM yyyy, HH:mm}");
            SaveProfileLocked();
        }
        RaiseChanged();
    }

    private static void AppendHistory(ProfileState state, ProfileVersion.Origin source, string note)
    {
        state.History.Add(new ProfileVersion(Guid.NewGuid(), DateTime.Now, state.Text, source, note));
        if (state.History.Count > 50) state.History.RemoveRange(0, state.History.Count - 50);
    }

    private void SaveProfileLocked() => store.Save("profile.bin", profile);

    private void RaiseChanged()
    {
        if (context is null) Changed?.Invoke();
        else context.Post(_ => Changed?.Invoke(), null);
    }
}
