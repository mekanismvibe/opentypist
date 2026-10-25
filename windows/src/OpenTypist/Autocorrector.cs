using System.Globalization;
using System.Windows.Threading;
using static OpenTypist.Win32;

namespace OpenTypist;

/// Fixes misspellings as the user types, like AutoHotkey's AutoCorrect.ahk.
/// A port of Autocorrector.swift:
///
/// 1. **Lists** (instant): when a word ends, look the recently typed text up
///    in the misspelling lists and snippets and, on a hit, swallow the ending
///    key, delete the typo, type the fix and send the ending key again, all in
///    one `SendInput` batch from the keyboard hook, so it stays in order with
///    what the user types.
/// 2. **Language model** (a moment later, on the UI thread): for typos the
///    lists can't settle (several possible fixes, a "typo" that is also a real
///    word, or a word the spell checker doesn't know), score each candidate in
///    context and fix the word only if one is clearly more likely than what
///    was typed.
///
/// Backspace right after a fix undoes it and stops that word from being fixed
/// again. `HandleKeyDown` runs on the hook thread; the state is locked.
public sealed class Autocorrector
{
    private readonly EngineService engine;
    private readonly FocusReader reader;
    private readonly AppSettings settings;
    private readonly TextReplacements replacements;
    private readonly Personalization personal;
    private readonly WritingSamples samples;
    private readonly ScreenContext screen;
    private readonly Stats stats;
    private readonly Dispatcher dispatcher;
    private readonly object gate = new();

    /// Raised on the UI thread after we changed the text.
    public event Action? TextChanged;

    /// Text typed since the caret last jumped (click, arrows, shortcut).
    private string typed = "";
    private const int MaxTyped = 80;
    /// The key after a dead key types a combined character we can't see.
    private bool afterDeadKey;
    /// The foreground app, for learning per-app style.
    private (string Id, string Name)? currentApp;

    /// The last fix, while Backspace can still undo it.
    private (string Original, string Replacement, string Ending)? lastFix;

    /// A language-model check in flight for the word before `Ending`.
    private sealed class PendingCheck(int id, string word, string ending)
    {
        public int Id { get; } = id;
        public string Word { get; } = word;
        public string Ending { get; } = ending;
        /// Typed after the ending while the check runs.
        public string TypedSince { get; set; } = "";
    }
    private PendingCheck? pending;
    private int nextCheckId;

    /// How much more likely (in log-probability) a correction must be than
    /// the typed word before it replaces it.
    private static class Margin
    {
        public const double KnownMisspelling = 0.0;
        public const double UnknownWord = 2.5;
        public const double RealWord = 1.5;
    }

    /// Weight of the keyboard typo cost against the model's log-probability.
    /// Without it the model happily turns "recipt" into "recipe".
    private const double TypoCostWeight = 2.0;

    public Autocorrector(EngineService engine, FocusReader reader, AppSettings settings, TextReplacements replacements,
        Personalization personal, WritingSamples samples, ScreenContext screen, Stats stats)
    {
        this.engine = engine;
        this.reader = reader;
        this.settings = settings;
        this.replacements = replacements;
        this.personal = personal;
        this.samples = samples;
        this.screen = screen;
        this.stats = stats;
        dispatcher = Dispatcher.CurrentDispatcher;
        ReloadRules();
    }

    // Lists

    public static string UserListsDirectory => Path.Combine(AppSettings.DataDirectory, "Autocorrect");

    /// The Wikipedia list shared with the Mac app (Resources/autocorrect),
    /// copied next to the app; when running from a build folder, the
    /// repository's copy.
    public static string? BuiltInListPath
    {
        get
        {
            const string name = "wikipedia-common-misspellings.txt";
            var local = Path.Combine(AppContext.BaseDirectory, "autocorrect", name);
            if (File.Exists(local)) return local;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var repo = Path.Combine(dir.FullName, "Resources", "autocorrect", name);
                if (File.Exists(repo)) return repo;
            }
            return null;
        }
    }

    private volatile AutocorrectRules rules = new();
    public AutocorrectRules Rules => rules;
    public int BuiltInCount { get; private set; }
    public List<(string Name, int Rules)> UserListCounts { get; private set; } = [];

    public void ReloadRules()
    {
        var fresh = new AutocorrectRules();
        BuiltInCount = BuiltInListPath is string builtIn ? fresh.LoadFile(builtIn) : 0;
        var files = Directory.Exists(UserListsDirectory)
            ? Directory.GetFiles(UserListsDirectory).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".ahk" or ".txt").Order()
            : Enumerable.Empty<string>();
        UserListCounts = files.Select(f => (Path.GetFileName(f), fresh.LoadFile(f))).ToList();
        rules = fresh;
    }

    /// Copies a `.ahk` or `misspelling->correction` list into the user lists
    /// folder and reloads. Returns how many entries it had.
    public int ImportList(string path)
    {
        Directory.CreateDirectory(UserListsDirectory);
        var destination = Path.Combine(UserListsDirectory, Path.GetFileName(path));
        File.Copy(path, destination, overwrite: true);
        ReloadRules();
        return UserListCounts.FirstOrDefault(l => l.Name == Path.GetFileName(path)).Rules;
    }

    // Keystrokes

    public void Reset()
    {
        lock (gate) ResetLocked();
    }

    private void ResetLocked()
    {
        typed = "";
        lastFix = null;
        pending = null;
        afterDeadKey = false;
    }

    /// The app that came to the foreground (UI thread).
    public void SetCurrentApp(string id, string name)
    {
        lock (gate) currentApp = (id, name);
    }

    /// The completion controller inserted accepted suggestion text.
    public void NoteInserted(string text)
    {
        lock (gate)
        {
            lastFix = null;
            pending = null;
            Append(text);
        }
    }

    /// Hook thread. Returns true to swallow the key (we replaced the text
    /// ourselves). Runs even with autocorrect off, so typing habits are
    /// still learned.
    public bool HandleKeyDown(KeyEvent e, string? appId)
    {
        lock (gate)
        {
            if (!settings.Enabled || settings.IsDisabled(appId) || e.IsShortcut || (e.Alt && !e.AltGr))
            {
                ResetLocked();
                return false;
            }
            if (e.IsDeadKey)
            {
                ResetLocked();
                afterDeadKey = true;
                return false;
            }
            if (afterDeadKey)
            {
                ResetLocked();
                return false;
            }

            switch (e.Vk)
            {
                case VK_BACK:
                    if (lastFix is { } fix)
                    {
                        Undo(fix);
                        return true;
                    }
                    if (typed.Length > 0) typed = RemoveLastCharacter(typed);
                    pending = null;
                    return false;
                case VK_RETURN:
                    return HandleCharacter('\n');
                case VK_TAB:
                    return HandleCharacter('\t');
                case VK_ESCAPE or VK_DELETE or >= 0x21 and <= 0x28: // Esc, Delete, Page Up/Down, End, Home, arrows
                    ResetLocked();
                    return false;
                default:
                    if (e.Text.Length != 1 || char.IsControl(e.Text[0]))
                    {
                        ResetLocked();
                        return false;
                    }
                    return HandleCharacter(e.Text[0]);
            }
        }
    }

    private bool HandleCharacter(char c)
    {
        lastFix = null;
        if (pending is not null) pending.TypedSince += c;

        if (AutocorrectRules.IsEndCharacter(c))
        {
            bool startsSentence = StartsSentence(typed[..^AutocorrectRules.LastWord(typed).Length]);
            bool swallowed = WordEnded(c);
            if (!swallowed) Append(c.ToString());
            LearnFromEnding(c, startsSentence);
            return swallowed;
        }

        Append(c.ToString());
        if (settings.AutocorrectEnabled && rules.MatchImmediate(typed) is AutocorrectMatch.Fix fix
            && !personal.IsNeverCorrect(AutocorrectRules.LastWord(typed)))
        {
            // The app hasn't received `c` yet: delete the rest of the trigger.
            new InputBatch().Backspace(AutocorrectRules.Backspaces(typed[^fix.Length..]) - 1).Text(fix.Replacement).SendFromHook();
            ReplaceTypedSuffix(fix.Length, fix.Replacement);
            RecordFix(byModel: false);
            return true;
        }
        return false;
    }

    /// Feeds the word (as it ended up, after any instant fix) and, at the end
    /// of a line, the line to the learner. Only typed keys are ever used.
    private void LearnFromEnding(char ending, bool startsSentence)
    {
        var before = typed.Length > 0 ? typed[..^1] : "";
        var word = AutocorrectRules.LastWord(before);
        var appId = Win32.ForegroundAppId();
        var appName = currentApp is { } app && app.Id == appId ? app.Name : appId ?? "";
        if (word.Length > 0) personal.ObserveWord(word, startsSentence, appId, appName);
        if (ending == '\n') personal.ObserveLine(before[(before.LastIndexOf('\n') + 1)..]);
    }

    /// Returns true if the ending key was swallowed and replayed after a fix.
    private bool WordEnded(char ending)
    {
        // Snippets come first.
        if (replacements.Match(typed) is AutocorrectMatch.Fix expansion)
        {
            ApplyFix(expansion.Length, expansion.Replacement, ending);
            Note("Expanded a snippet");
            return true;
        }

        var word = AutocorrectRules.LastWord(typed);
        if (word.Length == 0 || personal.IsNeverCorrect(word)) return false;

        var match = settings.AutocorrectEnabled ? rules.MatchAtEnd(typed) : null;
        switch (match)
        {
            case AutocorrectMatch.Fix fix:
                var trigger = typed[^fix.Length..];
                // A list "typo" that is a real word in the user's languages
                // ("loosing", "payed") is only fixed if the model agrees.
                if (trigger == word && SpellCheck.IsValidWord(word))
                {
                    Note("Autocorrect: list entry is also a real word, asking the model");
                    StartModelCheck(word, [fix.Replacement], Margin.RealWord, ending);
                    return false;
                }
                ApplyFix(fix.Length, fix.Replacement, ending);
                RecordFix(byModel: false);
                return true;
            case AutocorrectMatch.Ambiguous ambiguous:
                StartModelCheck(ambiguous.Word, [.. ambiguous.Candidates], Margin.KnownMisspelling, ending);
                break;
            case null:
                // Check the dictionary now (about a millisecond) so correctly spelled
                // words don't replace a check still running for an earlier typo.
                if (settings.AiTypoFix && word.Length >= 3 && !SpellCheck.IsValidWord(word))
                {
                    StartModelCheck(word, null, Margin.UnknownWord, ending);
                }
                break;
        }
        return false;
    }

    /// Replaces the last `length` typed characters, then replays the ending
    /// key that was swallowed. Backspace right afterwards undoes it.
    private void ApplyFix(int length, string replacement, char ending)
    {
        var trigger = typed[^length..];
        new InputBatch().Backspace(AutocorrectRules.Backspaces(trigger)).Text(replacement).Replay(ending).SendFromHook();
        ReplaceTypedSuffix(length, replacement);
        Append(ending.ToString());
        lastFix = (trigger, replacement, ending.ToString());
        RaiseTextChanged();
    }

    private void Undo((string Original, string Replacement, string Ending) fix)
    {
        lastFix = null;
        var applied = fix.Replacement + fix.Ending;
        new InputBatch().Backspace(AutocorrectRules.Backspaces(applied)).Text(fix.Original).SendFromHook();
        ReplaceTypedSuffix(applied.Length, fix.Original);
        var word = AutocorrectRules.LastWord(fix.Original);
        if (word.Length > 0) personal.AddNeverCorrect(word);
        Note("Undid a correction; that word won't be corrected again");
        RaiseTextChanged();
    }

    // Language-model check

    private void StartModelCheck(string word, List<string>? candidates, double margin, char ending)
    {
        if (!settings.AiTypoFix || !engine.IsReady || word.Length < 3 || personal.IsNeverCorrect(word)
            || !word.All(c => char.IsLetter(c) || c == '\'')) return;
        // Leave alone ALL-CAPS words and, for dictionary checks, names.
        var letters = word.Where(char.IsLetter).ToList();
        if (letters.Count > 1 && letters.All(char.IsUpper)) return;
        if (candidates is null && char.IsUpper(word[0]) && !StartsSentence(typed[..^word.Length])) return;

        var check = new PendingCheck(++nextCheckId, word, ending.ToString());
        pending = check;
        // Run once the app has received the ending key.
        dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(60);
            await RunModelCheck(check, candidates, margin);
        });
    }

    private bool IsPending(PendingCheck check)
    {
        lock (gate) return ReferenceEquals(pending, check);
    }

    private void ClearPending(PendingCheck check)
    {
        lock (gate)
        {
            if (ReferenceEquals(pending, check)) pending = null;
        }
    }

    /// UI thread.
    private async Task RunModelCheck(PendingCheck check, List<string>? candidates, double margin)
    {
        if (!IsPending(check)) return;
        var options = candidates ?? SpellCheck.Guesses(check.Word);
        options.RemoveAll(o => o == check.Word);
        var result = await reader.ReadAsync();
        if (options.Count == 0 || result.Context is not FocusContext ctx)
        {
            ClearPending(check);
            return;
        }
        string tail;
        lock (gate) tail = check.Word + check.Ending + check.TypedSince;
        if (!ctx.TextBefore.EndsWith(tail, StringComparison.Ordinal))
        {
            AppStatus.Note("Autocorrect: the field changed before the model could check the word");
            ClearPending(check);
            return;
        }
        var request = new EngineService.ScoreRequest
        {
            Context = ctx.TextBefore[..^tail.Length],
            AppName = settings.UseAppContext ? ctx.AppName : null,
            WindowTitle = settings.UseAppContext ? ctx.WindowTitle : null,
            ScreenText = screen.TextFor(ctx),
            Profile = personal.PromptProfile,
            Examples = samples.Examples(ctx.AppId, ctx.WindowTitle),
            Candidates = [check.Word, .. options],
        };
        // List entries that are real words are known mistakes; for everything
        // else, weigh how plausible each typo is.
        bool weighTypos = margin != Margin.RealWord;
        engine.Score(request, scores =>
        {
            if (scores is null || scores.Length != options.Count + 1) return;
            double original = scores[0];
            int best = -1;
            double bestScore = double.NegativeInfinity;
            for (int i = 0; i < options.Count; i++)
            {
                double adjusted = scores[i + 1] - (weighTypos ? TypoCostWeight * Typos.TypoCost(check.Word, options[i]) : 0);
                if (adjusted > bestScore) { bestScore = adjusted; best = i; }
            }
            if (best < 0 || bestScore - original <= margin)
            {
                AppStatus.Note("Autocorrect: the model kept the typed word");
                ClearPending(check);
                return;
            }
            _ = ApplyLateFix(check, options[best]);
        });
    }

    /// Replaces a word the user has already moved past, re-typing whatever
    /// they typed after it. UI thread.
    private async Task ApplyLateFix(PendingCheck check, string replacement)
    {
        string tail;
        lock (gate)
        {
            if (!ReferenceEquals(pending, check)) return;
            pending = null;
            tail = check.Ending + check.TypedSince;
        }
        // Make sure the field still holds exactly what we expect.
        var result = await reader.ReadAsync();
        if (result.Context is not FocusContext ctx || !ctx.TextBefore.EndsWith(check.Word + tail, StringComparison.Ordinal))
        {
            AppStatus.Note("Autocorrect: the field changed before the fix could be applied");
            return;
        }
        lock (gate)
        {
            new InputBatch().Backspace(AutocorrectRules.Backspaces(check.Word + tail)).Text(replacement + tail).SendFromHook();
            ReplaceTypedSuffix(check.Word.Length + tail.Length, replacement + tail);
            if (check.TypedSince.Length == 0) lastFix = (check.Word, replacement, check.Ending);
        }
        // It was a typo, not one of the user's own words.
        personal.ForgetObservation(check.Word);
        RecordFix(byModel: true);
    }

    // Helpers

    private void RecordFix(bool byModel)
    {
        stats.RecordCorrection();
        Note(byModel ? "Fixed a typo using the language model" : "Fixed a misspelling");
        RaiseTextChanged();
    }

    private void Note(string message) => dispatcher.BeginInvoke(() => AppStatus.Note(message));

    private void RaiseTextChanged() => dispatcher.BeginInvoke(() => TextChanged?.Invoke());

    private void Append(string text)
    {
        typed += text;
        if (typed.Length > MaxTyped) typed = TextUtil.Suffix(typed, MaxTyped);
    }

    private void ReplaceTypedSuffix(int count, string text)
    {
        typed = typed[..Math.Max(0, typed.Length - count)] + text;
    }

    private static string RemoveLastCharacter(string s)
    {
        var info = new StringInfo(s);
        return info.LengthInTextElements <= 1 ? "" : info.SubstringByTextElements(0, info.LengthInTextElements - 1);
    }

    private static bool StartsSentence(string before)
    {
        var trimmed = before.TrimEnd(' ', '\t');
        return trimmed.Length == 0 || ".!?\n".Contains(trimmed[^1]);
    }
}
