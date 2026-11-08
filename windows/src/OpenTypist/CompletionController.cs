using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using static OpenTypist.Win32;

namespace OpenTypist;

/// Ties keystrokes, the focused text field, the model and the overlay
/// together. A port of CompletionController.swift. Everything here runs on
/// the UI thread except `TryAcceptOnHookThread` and `PostKeyDown`, which only
/// read `visible` and post to the UI thread.
public sealed class CompletionController
{
    private readonly EngineService engine;
    private readonly FocusReader reader;
    private readonly AppSettings settings;
    private readonly Personalization personal;
    private readonly WritingSamples samples;
    private readonly ScreenContext screen;
    private readonly Stats stats;
    private readonly OverlayWindow overlay = new();
    private readonly Dispatcher dispatcher;

    /// What the hook thread may act on: the text on screen and its keys.
    /// `Source`: the suggestion it is part of (`suggestion` can already be a
    /// newer one while a show waits for the obstacle probe).
    private sealed record Visible(string Text, AcceptKeys Keys, Suggestion? Source);

    private Suggestion? suggestion;
    /// The part of `suggestion` currently drawn on screen.
    private volatile Visible? visible;
    private readonly DispatcherTimer refreshTimer;
    private int refreshGeneration;
    /// After accepting, the target app may not have applied our keystrokes yet
    /// when we read it back; wait until the text ends with `ExpectedTail`.
    private (string ExpectedTail, int Retries)? pendingAccept;
    /// Set by Esc; cleared by the next typing key.
    private bool suppressed;
    private FocusContext? lastContext;
    /// When the oldest key not yet followed by a read of the field came in
    /// (a Stopwatch timestamp).
    private long? unreadSince;
    /// A click, another app or a Tab may have moved the focus since the last
    /// read: the next key checks the field at once.
    private bool focusMayHaveMoved = true;
    /// Typing faster than the refresh debounce still gets the field checked
    /// this often.
    private const int MaxUnreadMs = 150;

    /// After accepting, the overlay is down until the rest of the suggestion
    /// (or the next one) is up again. An accept key pressed in that gap would
    /// reach the app (a tab, or the focus moving on), so it is swallowed; it
    /// carries on accepting if the rest of the same suggestion comes back.
    /// Written by the hook thread too, so under `gapGate`.
    private static readonly TimeSpan AcceptGapLength = TimeSpan.FromSeconds(1.2);
    private readonly object gapGate = new();
    private (DateTime Until, AcceptKeys Keys, AcceptKind Held)? acceptGap;
    private enum AcceptKind { None, Word, All }

    /// Called with text inserted by accepting a suggestion.
    public event Action<string>? Accepted;
    /// Called each time a field is read: whether it is a sign-in or
    /// verification field (no autocorrect or learning there either).
    public event Action<bool>? SignInFieldChanged;

    public CompletionController(EngineService engine, FocusReader reader, AppSettings settings,
        Personalization personal, WritingSamples samples, ScreenContext screen, Stats stats)
    {
        this.engine = engine;
        this.reader = reader;
        this.settings = settings;
        this.personal = personal;
        this.samples = samples;
        this.screen = screen;
        this.stats = stats;
        dispatcher = Dispatcher.CurrentDispatcher;
        refreshTimer = new DispatcherTimer(DispatcherPriority.Input, dispatcher);
        refreshTimer.Tick += (_, _) =>
        {
            refreshTimer.Stop();
            _ = Refresh();
        };
        overlay.Prepare();
    }

    // Events

    /// Hook thread: the accept keys and Esc while a suggestion is showing.
    /// Returns true if the key was used (and must be swallowed). Accepting
    /// wins over autocorrect, which wins over normal typing (`PostKeyDown`).
    public bool TryAcceptOnHookThread(KeyEvent e)
    {
        var current = visible;
        if (current is null)
        {
            if (!HoldInGap(e)) return false;
            if ((e.Alt && !e.AltGr) || e.Win) new InputBatch().MenuMask().SendFromHook();
            AppStatus.Log("accept key held: the rest of the suggestion isn't on screen yet");
            return true;
        }
        // Both actions on the same key: the whole suggestion wins.
        bool all = current.Keys.All.Matches(e);
        if (all || current.Keys.Word.Matches(e))
        {
            // Swallowing the key of Alt+→ would leave a bare Alt tap, which
            // opens the app's menu (and the accepted text would pick items).
            if ((e.Alt && !e.AltGr) || e.Win) new InputBatch().MenuMask().SendFromHook();
            dispatcher.BeginInvoke(() => AcceptFromHook(current, all));
            return true;
        }
        if (e.Vk == VK_ESCAPE && !e.IsShortcut)
        {
            dispatcher.BeginInvoke(() =>
            {
                Dismiss();
                suppressed = true;
            });
            return true;
        }
        return false;
    }

    /// An accept key pressed in the gap after accepting: remembered, and kept
    /// from the app. Either thread.
    private bool HoldInGap(KeyEvent e)
    {
        lock (gapGate)
        {
            if (acceptGap is not { } gap) return false;
            if (DateTime.UtcNow > gap.Until)
            {
                acceptGap = null;
                return false;
            }
            var kind = gap.Keys.All.Matches(e) ? AcceptKind.All : gap.Keys.Word.Matches(e) ? AcceptKind.Word : AcceptKind.None;
            if (kind == AcceptKind.None) return false;
            acceptGap = gap with { Held = kind };
            return true;
        }
    }

    private void ClearGap()
    {
        lock (gapGate) acceptGap = null;
    }

    /// Hook thread: a key that reaches the app.
    public void PostKeyDown(KeyEvent e) => dispatcher.BeginInvoke(() => HandleKeyDown(e));

    /// Autocorrect replaced text with keystrokes we don't see; re-read the
    /// field once the app has applied them.
    public void TextChangedExternally()
    {
        engine.CancelAll();
        suggestion = null;
        pendingAccept = null;
        HideOverlay();
        if (!settings.Enabled || !engine.IsReady) return;
        ScheduleRefresh(120);
    }

    private void AcceptFromHook(Visible shown, bool all)
    {
        // The suggestion changed between the key press and now. Right after
        // accepting (a quick Tab, Tab), the key waits for the rest; otherwise
        // it goes back to the app rather than inserting stale text.
        if (!ReferenceEquals(shown, visible))
        {
            bool held;
            lock (gapGate)
            {
                held = acceptGap is { } gap && DateTime.UtcNow <= gap.Until;
                if (held) acceptGap = acceptGap!.Value with { Held = all ? AcceptKind.All : AcceptKind.Word };
            }
            AppStatus.Log($"accept key arrived after the suggestion changed: {(held ? "held for the rest" : "given back to the app")}");
            if (!held) TextInserter.Press(all ? shown.Keys.All.Vk : shown.Keys.Word.Vk);
            return;
        }
        Accept(all ? shown.Text : Suggestion.NextWord(shown.Text, settings.AcceptTrailingSpace));
    }

    /// Any other key: update or drop the suggestion and schedule a refresh.
    private void HandleKeyDown(KeyEvent e)
    {
        if (!settings.Enabled) { Dismiss(); return; }
        if (!engine.IsReady)
        {
            AppStatus.Note("Typing seen, but the model isn't loaded yet");
            DropSuggestion();
            ClearGap();
            // The field is still read, so autocorrect and learning keep out of
            // sign-in and password fields while there are no suggestions.
            ScheduleKeyRefresh(e.Vk);
            return;
        }
        // An IME is composing: stay out of the way.
        if (e.Vk == VK_PROCESSKEY) { Dismiss(); return; }
        if (e.IsShortcut || e.Vk == VK_ESCAPE) { Dismiss(); return; }
        suppressed = false;
        ClearGap();
        engine.CancelAll();

        // Keep the ghost text while the user types exactly what it suggests:
        // the typed character lands on top of its ghost twin, and the next
        // refresh moves the rest behind the caret.
        var typed = e.Text;
        var current = visible;
        if (current is not null && typed.Length > 0 && current.Text.StartsWith(typed, StringComparison.Ordinal) && current.Text.Length > typed.Length)
        {
            visible = current with { Text = current.Text[typed.Length..] };
            // A show still waiting for the obstacle probe would put back text
            // that starts with the character just typed.
            showSequence++;
        }
        else
        {
            HideOverlay();
        }
        ScheduleKeyRefresh(e.Vk);
    }

    /// The refresh 60 ms after the last key. The refresh is also what tells
    /// autocorrect whether the field is a password or sign-in field (the hook
    /// sees password keys on Windows), and keys less than 60 ms apart would
    /// keep putting it off: a fast typist could type a whole password into a
    /// field just clicked into before it is read. So the field is also checked
    /// (only for that) right away on the first key after the focus may have
    /// moved, and while keys keep coming without a read, at least every
    /// `MaxUnreadMs`.
    private void ScheduleKeyRefresh(int vk)
    {
        long now = Stopwatch.GetTimestamp();
        if (focusMayHaveMoved || (unreadSince is long since && Stopwatch.GetElapsedTime(since, now).TotalMilliseconds >= MaxUnreadMs))
        {
            unreadSince = now;
            _ = CheckField();
        }
        else if (unreadSince is null)
        {
            unreadSince = now;
        }
        // Tab and Enter can move the focus to another field.
        focusMayHaveMoved = vk is VK_TAB or VK_RETURN;
        ScheduleRefresh(60);
    }

    /// Reads the field only to tell autocorrect whether to keep out of it.
    /// One at a time: in an app slow to answer UI Automation, checks every
    /// 150 ms while typing would queue up faster than they finish, and the
    /// refresh reads (suggestions) would wait behind all of them.
    private async Task CheckField()
    {
        if (!settings.Enabled || checkingField) return;
        checkingField = true;
        try
        {
            NoteField(await reader.ReadAsync(settings.IsDisabled));
        }
        finally
        {
            checkingField = false;
        }
    }

    private bool checkingField;

    /// Password and sign-in fields: no autocorrect or learning from what is
    /// typed there. Other failures (a UI Automation timeout in a busy app,
    /// a turned-off app) say nothing about the field and leave the last
    /// answer, as on the Mac: a password field must not get a read error
    /// count as "not a sign-in field".
    private void NoteField(FocusResult result)
    {
        if (result.Context is not null || result.SignInField) SignInFieldChanged?.Invoke(result.SignInField);
    }

    /// A click may move the caret, so drop the suggestion. A new one comes
    /// with the next keystroke, never from a click alone.
    public void HandleMouseDown() => Dismiss();

    /// Drops the suggestion. Something the user did (typing, a click, Esc,
    /// another app) also ends the gap after accepting.
    public void Dismiss()
    {
        DropSuggestion();
        ClearGap();
        focusMayHaveMoved = true;
    }

    private void DropSuggestion()
    {
        refreshTimer.Stop();
        refreshGeneration++;
        engine.CancelAll();
        suggestion = null;
        pendingAccept = null;
        HideOverlay();
    }

    // Accepting

    private void Accept(string chunk)
    {
        var current = visible;
        var source = current?.Source ?? suggestion;
        if (chunk.Length == 0 || source is null || current is null)
        {
            AppStatus.Log($"accept skipped ({(chunk.Length == 0 ? "nothing to insert" : source is null ? "no suggestion" : "nothing on screen")})");
            return;
        }
        // Before the overlay goes down, so the hook thread always sees one or the other.
        lock (gapGate) acceptGap = (DateTime.UtcNow + AcceptGapLength, current.Keys, AcceptKind.None);
        // Accepting what's visible ends a suggestion that's still streaming.
        engine.CancelAll();
        int consumedBefore = source.Text.Length - current.Text.Length;
        var inserted = chunk;
        // Finishing the suggestion: optionally add a space so typing can go
        // on, unless the text after the caret already starts with one, or
        // with punctuation that closes the phrase (mid-line, the suggestion
        // leads into ", thanks": "soon , thanks" would be wrong).
        if (chunk.Length >= current.Text.Length && settings.AcceptTrailingSpace && !char.IsWhiteSpace(inserted[^1])
            && !(lastContext?.TextAfterOnLine is { Length: > 0 } after && (char.IsWhiteSpace(after[0]) || Closes(after[0], source.AnchorTail))))
        {
            inserted += " ";
        }
        HideOverlay();
        TextInserter.Type(inserted);
        AppStatus.Log($"accepted {(chunk.Length >= current.Text.Length ? "the whole suggestion" : "a word")}, {inserted.Length} chars");
        stats.RecordAccepted(inserted);
        Accepted?.Invoke(inserted);
        var tail = source.AnchorTail + source.Text[..Math.Clamp(consumedBefore, 0, source.Text.Length)] + inserted;
        // Text goes out a word at a time (see InputBatch), so allow ~0.8 s.
        pendingAccept = (TextUtil.Suffix(tail, 40), 20);
        ScheduleRefresh(40);
    }

    /// No space goes before these when a suggestion is finished mid-line.
    private const string ClosingPunctuation = ",.;:!?)]}…\"'\u201D\u2019";

    /// Whether `next` (right after the caret) closes the phrase. A straight
    /// quote after a space opens one (read |"The Guide" keeps its space);
    /// `before` is the text that was before the caret, ahead of the quote.
    private static bool Closes(char next, string before) =>
        ClosingPunctuation.Contains(next) && !(next is '"' or '\'' && (before.Length == 0 || char.IsWhiteSpace(before[^1])));

    // Refreshing

    private void ScheduleRefresh(int milliseconds)
    {
        refreshGeneration++;
        refreshTimer.Stop();
        refreshTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        refreshTimer.Start();
    }

    /// Nothing to suggest here; the gap after accepting runs its course.
    private void Skip(string reason)
    {
        AppStatus.Note(reason);
        DropSuggestion();
    }

    private async Task Refresh()
    {
        unreadSince = null;
        if (!settings.Enabled || suppressed) { Dismiss(); return; }
        int generation = refreshGeneration;
        var started = Stopwatch.StartNew();

        var result = await reader.ReadAsync(settings.IsDisabled);
        // Still the field being typed in, even when a newer key came meanwhile.
        NoteField(result);
        // A newer keystroke arrived while reading the field.
        if (generation != refreshGeneration)
        {
            AppStatus.Log($"refresh {generation}: superseded while reading the field ({started.ElapsedMilliseconds} ms)");
            return;
        }
        if (result.Context is not FocusContext ctx) { Skip(result.Failure ?? "No focused text field"); return; }
        if (!engine.IsReady) { Skip("Typing seen, but the model isn't loaded yet"); return; }
        lastContext = ctx;
        if (settings.IsDisabled(ctx.AppId)) { Skip($"{ctx.AppName}: turned off for this app"); return; }
        samples.Observe(ctx);
        if (ctx.Caret is not Rect caret) { Skip($"{ctx.AppName}: the app doesn't report where the caret is"); return; }

        var before = ctx.TextBefore;
        // In the middle of a line, suggest short insertions that lead into
        // the text after the caret (never inside a word).
        bool midLine = ctx.TextAfterOnLine.Trim(' ', '\t').Length > 0;
        if (midLine)
        {
            if (!settings.MidLineSuggestions) { Skip($"{ctx.AppName}: caret is in the middle of a line"); return; }
            if (char.IsLetterOrDigit(ctx.TextAfterOnLine[0])) { Skip($"{ctx.AppName}: caret is inside a word"); return; }
        }
        var textAfter = midLine ? ctx.TextAfterOnLine : "";
        if (before.Length == 0 || before[^1] == '\n' || before.Trim().Length < 3)
        {
            Skip($"{ctx.AppName}: not enough text before the caret yet");
            return;
        }

        if (pendingAccept is (string expectedTail, int retries))
        {
            // Word and Outlook turn the ' we typed into ’.
            if (!Suggestion.NormalizingQuotes(before).EndsWith(Suggestion.NormalizingQuotes(expectedTail), StringComparison.Ordinal) && retries > 0)
            {
                AppStatus.Log($"refresh {generation}: waiting for the app to apply the accepted text ({retries} tries left)");
                pendingAccept = (expectedTail, retries - 1);
                ScheduleRefresh(40);
                return;
            }
            pendingAccept = null;
        }
        if (suggestion is { } current && current.ProcessId == ctx.ProcessId && current.Remaining(before) is string rest)
        {
            Show(rest, ctx, caret, continuing: true);
            return;
        }

        suggestion = null;
        pendingAccept = null;
        HideOverlay();

        var request = new EngineService.Request
        {
            TextBefore = before,
            AppName = settings.UseAppContext ? ctx.AppName : null,
            WindowTitle = settings.UseAppContext ? ctx.WindowTitle : null,
            ScreenText = screen.TextFor(ctx),
            Profile = personal.PromptProfile,
            Examples = samples.Examples(ctx.AppId, ctx.WindowTitle),
            Length = midLine ? CompletionLength.Short : settings.Length,
            MinProbability = settings.Confidence.MinProbability(),
            WordMinProbability = settings.Confidence.WordMinProbability(),
            TextAfter = textAfter,
        };
        long readMs = started.ElapsedMilliseconds;
        AppStatus.Log($"refresh {generation}: {before.Length} chars before the caret, asking the model");
        long? firstShownMs = null;
        // Show each complete word as soon as it exists, then the final text.
        // Returns whether it was shown.
        bool Present(string text)
        {
            if (!settings.Enabled || generation != refreshGeneration) return false;
            if (Rejection(text, before) is string reason)
            {
                suggestion = null;
                HideOverlay();
                AppStatus.Note($"{ctx.AppName}: dropped a suggestion that {reason}");
                return false;
            }
            suggestion = new Suggestion(before, text, ctx.ProcessId);
            Show(text, ctx, caret);
            if (firstShownMs is null)
            {
                firstShownMs = started.ElapsedMilliseconds;
                stats.RecordShown();
            }
            return true;
        }
        engine.Complete(request, text =>
        {
            if (!settings.Enabled || generation != refreshGeneration)
            {
                AppStatus.Log($"refresh {generation}: superseded after the model answered");
                return;
            }
            long ms = started.ElapsedMilliseconds;
            if (text is not null && Present(text))
            {
                AppStatus.Note($"{ctx.AppName}: suggestion shown after {firstShownMs ?? ms} ms, complete after {ms} ms (reading the field {readMs} ms)");
                return;
            }
            // Nothing is coming. The gap still runs its 1.2 s: a Tab right
            // after accepting the end of a sentence mustn't type a tab.
            if (text is null && firstShownMs is null)
            {
                AppStatus.Note($"{ctx.AppName}: the model had no good suggestion ({ms} ms, reading the field {readMs} ms)");
            }
            else if (text is null)
            {
                // Streaming showed the start of it, but the finished text didn't
                // pass (cut at an unsure word piece, or not fitting the text
                // after the caret): don't leave that start up to be accepted.
                suggestion = null;
                HideOverlay();
                AppStatus.Note($"{ctx.AppName}: dropped a suggestion that didn't hold up once complete ({ms} ms)");
            }
        }, text => Present(text));
    }

    /// Why a suggestion isn't worth showing, or null.
    private string? Rejection(string text, string before)
    {
        if (!PromptBuilder.MakesRealWord(before, text, word => SpellCheck.IsValidWord(word) || personal.IsNeverCorrect(word)))
        {
            return "misspells the word being typed";
        }
        return PromptBuilder.JunkReason(text, before);
    }

    // Overlay

    /// Bumped by every show and hide, so a show that waited for the obstacle
    /// probe doesn't draw over something newer.
    private int showSequence;

    /// `continuing`: `text` is the rest of a suggestion the user has already
    /// seen and started accepting.
    private async void Show(string text, FocusContext ctx, Rect caret, bool continuing = false)
    {
        int sequence = ++showSequence;
        var source = suggestion;
        var keys = settings.AcceptKeysFor(ctx.AppId);
        // Mid-line, ghost text would sit on top of the text after the caret,
        // so the suggestion goes in a small card just below the line.
        bool bubble = ctx.TextAfterOnLine.Trim(' ', '\t').Length > 0;
        var wrap = bubble ? null : await WrapGuideFor(text, ctx, caret, keys.Word.HintLabel);
        if (sequence != showSequence) return;
        visible = new Visible(text, keys, source);
        overlay.ShowSuggestion(text, caret, ctx.Style, keys.Word.HintLabel, wrap, bubble);
        // The gap after accepting is over. An accept key held in it carries on
        // with the suggestion being accepted; a new one has to be seen first.
        AcceptKind held;
        lock (gapGate)
        {
            held = acceptGap?.Held ?? AcceptKind.None;
            acceptGap = null;
        }
        if (held != AcceptKind.None) AppStatus.Log($"accept key held in the gap: {(continuing ? "carrying on" : "dropped, this suggestion is new")}");
        if (continuing && held == AcceptKind.All) Accept(text);
        else if (continuing && held == AcceptKind.Word) Accept(Suggestion.NextWord(text, settings.AcceptTrailingSpace));
        AppStatus.Log($"overlay: {text.Length} chars at caret ({caret.X:0},{caret.Y:0} h {caret.Height:0}), " +
                      (bubble ? "mid-line card, " : $"{overlay.LastLineCount} line(s){(wrap is null ? "" : $" wrapping at {wrap.RightEdge:0} from {wrap.LineStartX:0}")}, ") +
                      $"font {ctx.Style.FontFamily ?? "?"} {ctx.Style.FontSizePx?.ToString("0.0") ?? "?"} px reported, {overlay.LastFontSizePx:0.0} px drawn " +
                      $"(zoom sample {ctx.Style.Sample?.Length ?? 0} chars, {ctx.Style.SampleWidthPx?.ToString("0") ?? "?"} px)");
    }

    /// Where the first non-field element (a button) sits on the caret's
    /// line, so the screen isn't probed on every keystroke.
    private (string Key, double? X, double CheckedTo)? obstacleCache;

    /// Where multi-line fields wrap: the field's right edge (less the same
    /// padding it has on the left), or earlier if a button sits on the line.
    /// Single-line fields scroll instead of wrapping, so they get none.
    private async Task<WrapGuide?> WrapGuideFor(string text, FocusContext ctx, Rect caret, string hint)
    {
        if (ctx.SingleLine || ctx.ElementFrame is not Rect frame) return null;
        double lineStart = ctx.LineStartX ?? frame.Left + 4;
        double padding = Math.Clamp(lineStart - frame.Left, 2, 40);
        double right = Math.Min(frame.Right - padding, WorkAreaAt((int)caret.X, (int)caret.Y).Right - 4);

        double needed = Math.Min(right, caret.Right + OverlayWindow.WidthPx(text, caret, ctx.Style, hint));
        var key = $"{ctx.ProcessId}:{frame}:{(int)((caret.Top + caret.Bottom) / 2)}";
        double? obstacle;
        if (obstacleCache is { } cache && cache.Key == key && (cache.X is not null || cache.CheckedTo >= needed))
        {
            obstacle = cache.X;
        }
        else
        {
            bool sameLine = obstacleCache?.Key == key;
            double from = sameLine ? Math.Max(caret.Right, obstacleCache!.Value.CheckedTo) : caret.Right;
            double checkedBefore = sameLine ? obstacleCache!.Value.CheckedTo : 0;
            obstacle = await reader.ObstacleXAsync(ctx, from, needed);
            obstacleCache = (key, obstacle, Math.Max(needed, checkedBefore));
            if (obstacle is not null) AppStatus.Note($"{ctx.AppName}: wrapping the suggestion before a button");
        }
        if (obstacle is double x) right = Math.Min(right, x - 6);
        return right - lineStart > 40 ? new WrapGuide(lineStart, right) : null;
    }

    private void HideOverlay()
    {
        showSequence++;
        visible = null;
        overlay.HideSuggestion();
    }

    /// The field read by the last refresh, for diagnostics.
    public FocusContext? LastContext => lastContext;
}
