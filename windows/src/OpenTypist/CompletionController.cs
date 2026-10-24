using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using static OpenTypist.Win32;

namespace OpenTypist;

/// Ties keystrokes, the focused text field, the model and the overlay
/// together. A port of CompletionController.swift. Everything here runs on
/// the UI thread except `HandleKeyOnHookThread`, which only reads `visible`.
public sealed class CompletionController
{
    private readonly EngineService engine;
    private readonly FocusReader reader;
    private readonly AppSettings settings;
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

    public CompletionController(EngineService engine, FocusReader reader, AppSettings settings)
    {
        this.engine = engine;
        this.reader = reader;
        this.settings = settings;
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

    /// Hook thread: Tab, Shift+Tab and Esc while a suggestion is showing.
    /// Returns true if the key was used (and must be swallowed). Everything
    /// else is passed on to `HandleKeyDown` on the UI thread.
    public bool HandleKeyOnHookThread(KeyEvent e)
    {
        var current = visible;
        if (current is not null)
        {
            if (current.Keys.All.Matches(e))
            {
                dispatcher.BeginInvoke(() => AcceptFromHook(current, all: true));
                return true;
            }
            if (current.Keys.Word.Matches(e))
            {
                dispatcher.BeginInvoke(() => AcceptFromHook(current, all: false));
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
        }
        dispatcher.BeginInvoke(() => HandleKeyDown(e));
        return false;
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
        int consumedBefore = suggestion.Text.Length - current.Text.Length;
        var inserted = chunk;
        // Finishing the suggestion: optionally add a space so typing can go on.
        if (chunk.Length >= current.Text.Length && settings.AcceptTrailingSpace && !char.IsWhiteSpace(inserted[^1]))
        {
            inserted += " ";
        }
        HideOverlay();
        TextInserter.Type(inserted);
        Accepted?.Invoke(inserted);
        var tail = suggestion.AnchorTail + suggestion.Text[..Math.Max(0, consumedBefore)] + inserted;
        pendingAccept = (TextUtil.Suffix(tail, 40), 8);
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
        if (ctx.Caret is not Rect caret) { Skip($"{ctx.AppName}: the app doesn't report where the caret is"); return; }

        var before = ctx.TextBefore;
        // Mid-line suggestions (a small card below the line) come in phase 2;
        // until then, only suggest at the end of a line.
        bool midLine = ctx.TextAfterOnLine.Trim(' ', '\t').Length > 0;
        if (midLine)
        {
            var next = ctx.TextAfterOnLine[0];
            if (char.IsLetterOrDigit(next)) { Skip($"{ctx.AppName}: caret is inside a word"); return; }
            Skip($"{ctx.AppName}: caret is in the middle of a line");
            return;
        }
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
            Length = settings.Length,
            MinProbability = settings.Confidence.MinProbability(),
            WordMinProbability = settings.Confidence.WordMinProbability(),
        };
        long readMs = started.ElapsedMilliseconds;
        AppStatus.Log($"refresh {generation}: {before.Length} chars before the caret, asking the model");
        engine.Complete(request, text =>
        {
            if (!settings.Enabled || generation != refreshGeneration)
            {
                AppStatus.Log($"refresh {generation}: superseded after the model answered");
                return;
            }
            long ms = started.ElapsedMilliseconds;
            if (text is null)
            {
                AppStatus.Note($"{ctx.AppName}: the model had no good suggestion ({ms} ms, reading the field {readMs} ms)");
                return;
            }
            suggestion = new Suggestion(before, text, ctx.ProcessId);
            Show(text, ctx, caret);
            AppStatus.Note($"{ctx.AppName}: showing a suggestion ({ms} ms, reading the field {readMs} ms)");
        });
    }

    // Overlay

    private void Show(string text, FocusContext ctx, Rect caret)
    {
        var keys = settings.AcceptKeys;
        visible = new Visible(text, keys);
        overlay.ShowSuggestion(text, caret, ctx.Style, keys.Word.HintLabel);
        AppStatus.Log($"overlay: {text.Length} chars at caret ({caret.X:0},{caret.Y:0} h {caret.Height:0}), " +
                      $"font {ctx.Style.FontFamily ?? "?"} {ctx.Style.FontSizePx?.ToString("0.0") ?? "?"} px reported, {overlay.LastFontSizePx:0.0} px drawn");
    }

    private void HideOverlay()
    {
        visible = null;
        overlay.HideSuggestion();
    }

    /// The field read by the last refresh, for diagnostics.
    public FocusContext? LastContext => lastContext;
}
