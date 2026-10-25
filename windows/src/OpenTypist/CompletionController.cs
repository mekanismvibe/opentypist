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
    private sealed record Visible(string Text, AcceptKeys Keys);

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

    /// Called with text inserted by accepting a suggestion.
    public event Action<string>? Accepted;

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
        if (current is null) return false;
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
        // The suggestion changed between the key press and now: give the
        // key back to the app rather than inserting stale text.
        if (!ReferenceEquals(shown, visible))
        {
            TextInserter.Press(all ? shown.Keys.All.Vk : shown.Keys.Word.Vk);
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
            Dismiss();
            return;
        }
        // An IME is composing: stay out of the way.
        if (e.Vk == VK_PROCESSKEY) { Dismiss(); return; }
        if (e.IsShortcut || e.Vk == VK_ESCAPE) { Dismiss(); return; }
        suppressed = false;
        engine.CancelAll();

        // Keep the ghost text while the user types exactly what it suggests:
        // the typed character lands on top of its ghost twin, and the next
        // refresh moves the rest behind the caret.
        var typed = e.Text;
        var current = visible;
        if (current is not null && typed.Length > 0 && current.Text.StartsWith(typed, StringComparison.Ordinal) && current.Text.Length > typed.Length)
        {
            visible = current with { Text = current.Text[typed.Length..] };
        }
        else
        {
            HideOverlay();
        }
        ScheduleRefresh(60);
    }

    /// A click may move the caret, so drop the suggestion. A new one comes
    /// with the next keystroke, never from a click alone.
    public void HandleMouseDown() => Dismiss();

    public void Dismiss()
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
        if (chunk.Length == 0 || suggestion is null || current is null) return;
        // Accepting what's visible ends a suggestion that's still streaming.
        engine.CancelAll();
        int consumedBefore = suggestion.Text.Length - current.Text.Length;
        var inserted = chunk;
        // Finishing the suggestion: optionally add a space so typing can go
        // on, unless the text after the caret already starts with one.
        if (chunk.Length >= current.Text.Length && settings.AcceptTrailingSpace && !char.IsWhiteSpace(inserted[^1])
            && !(lastContext?.TextAfterOnLine is { Length: > 0 } after && char.IsWhiteSpace(after[0])))
        {
            inserted += " ";
        }
        HideOverlay();
        TextInserter.Type(inserted);
        stats.RecordAccepted(inserted);
        Accepted?.Invoke(inserted);
        var tail = suggestion.AnchorTail + suggestion.Text[..Math.Max(0, consumedBefore)] + inserted;
        // Text goes out a word at a time (see InputBatch), so allow ~0.8 s.
        pendingAccept = (TextUtil.Suffix(tail, 40), 20);
        ScheduleRefresh(40);
    }

    // Refreshing

    private void ScheduleRefresh(int milliseconds)
    {
        refreshGeneration++;
        refreshTimer.Stop();
        refreshTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        refreshTimer.Start();
    }

    private void Skip(string reason)
    {
        AppStatus.Note(reason);
        Dismiss();
    }

    private async Task Refresh()
    {
        if (!settings.Enabled || !engine.IsReady || suppressed) { Dismiss(); return; }
        int generation = refreshGeneration;
        var started = Stopwatch.StartNew();

        var result = await reader.ReadAsync();
        // A newer keystroke arrived while reading the field.
        if (generation != refreshGeneration)
        {
            AppStatus.Log($"refresh {generation}: superseded while reading the field ({started.ElapsedMilliseconds} ms)");
            return;
        }
        if (result.Context is not FocusContext ctx) { Skip(result.Failure ?? "No focused text field"); return; }
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
            if (!before.EndsWith(expectedTail, StringComparison.Ordinal) && retries > 0)
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
            Show(rest, ctx, caret);
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
        void Present(string text)
        {
            if (!settings.Enabled || generation != refreshGeneration) return;
            suggestion = new Suggestion(before, text, ctx.ProcessId);
            Show(text, ctx, caret);
            if (firstShownMs is null)
            {
                firstShownMs = started.ElapsedMilliseconds;
                stats.RecordShown();
            }
        }
        engine.Complete(request, text =>
        {
            if (!settings.Enabled || generation != refreshGeneration)
            {
                AppStatus.Log($"refresh {generation}: superseded after the model answered");
                return;
            }
            long ms = started.ElapsedMilliseconds;
            if (text is not null)
            {
                Present(text);
                AppStatus.Note($"{ctx.AppName}: suggestion shown after {firstShownMs ?? ms} ms, complete after {ms} ms (reading the field {readMs} ms)");
            }
            else if (firstShownMs is null)
            {
                AppStatus.Note($"{ctx.AppName}: the model had no good suggestion ({ms} ms, reading the field {readMs} ms)");
            }
        }, Present);
    }

    // Overlay

    /// Bumped by every show and hide, so a show that waited for the obstacle
    /// probe doesn't draw over something newer.
    private int showSequence;

    private async void Show(string text, FocusContext ctx, Rect caret)
    {
        int sequence = ++showSequence;
        var keys = settings.AcceptKeysFor(ctx.AppId);
        // Mid-line, ghost text would sit on top of the text after the caret,
        // so the suggestion goes in a small card just below the line.
        bool bubble = ctx.TextAfterOnLine.Trim(' ', '\t').Length > 0;
        var wrap = bubble ? null : await WrapGuideFor(text, ctx, caret, keys.Word.HintLabel);
        if (sequence != showSequence) return;
        visible = new Visible(text, keys);
        overlay.ShowSuggestion(text, caret, ctx.Style, keys.Word.HintLabel, wrap, bubble);
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
