using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using static OpenTypist.Win32;

namespace OpenTypist;

/// Where ghost text may go, in physical screen pixels: from the caret to
/// `RightEdge` on the first line, then from `LineStartX` to `RightEdge` on
/// the lines below.
public sealed record WrapGuide(double LineStartX, double RightEdge);

/// Topmost, click-through, non-activating layered window that draws the
/// suggestion as ghost text just after the caret, followed by a small hint
/// with the accept key. It has no background so the text reads as part of the
/// field: the field's own font and a faded (45% alpha) version of its text
/// color when UI Automation reports them, otherwise grey. Text that doesn't
/// fit before the field's edge (or a button on the line) wraps onto the next
/// lines, like the app's own text would. In the middle of a line it draws a
/// small card below the line instead. The Windows side of OverlayWindow.swift.
/// Positions are physical pixels (per-monitor DPI v2).
public sealed class OverlayWindow : Window
{
    /// Used when the app doesn't report its text color.
    private static readonly Color FallbackColor = Color.FromArgb(230, 128, 128, 128);
    /// How much of the real text color the ghost text keeps.
    private const double GhostAlpha = 0.45;
    private const int MaxLines = 4;
    public const double HintGap = 6;
    private const double BubblePadding = 7;

    private readonly GhostTextView view = new();
    private IntPtr hwnd;
    private double lastScale;

    public bool IsShowing { get; private set; }
    /// For diagnostics: the font size and line count of the last suggestion.
    public double LastFontSizePx { get; private set; }
    public int LastLineCount { get; private set; }

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = false;
        Width = 10;
        Height = 10;
        Left = -32000;
        Top = -32000;
        Content = view;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            long style = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            style |= WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)style);
        };
    }

    /// Creates the native window once, off screen, so the first suggestion
    /// doesn't pay for it.
    public void Prepare()
    {
        Show();
        Hide();
    }

    /// The field's font size on screen, in physical pixels. UI Automation
    /// reports the size without the app's zoom (Notepad, browsers), and the
    /// character box can be taller than the text (line spacing), so the zoom
    /// comes from the on-screen width of the text before the caret.
    public static double FontSizePx(TextStyle style, Typeface typeface, Rect caret, double scale)
    {
        if (style.FontSizePx is double px)
        {
            if (style.Sample is { Length: > 0 } sample && style.SampleWidthPx is double width)
            {
                var formatted = GhostTextView.Format(sample, typeface, px, Colors.Black, 1);
                if (formatted.Width >= 4)
                {
                    double zoom = Math.Clamp(width / formatted.Width, 0.25, 5);
                    // Right after inserting text, browsers can report boxes
                    // from a layout that is still updating. When the caret
                    // is exactly one unzoomed line tall, a small difference
                    // is that, not zoom.
                    double heightZoom = caret.Height / formatted.Height;
                    if (Math.Abs(zoom - 1) < 0.08 || (Math.Abs(heightZoom - 1) < 0.07 && Math.Abs(zoom - 1) < 0.25)) return px;
                    return px * zoom;
                }
            }
            if (px <= caret.Height * 1.05) return px;
        }
        return Math.Min(28 * scale, Math.Max(11 * scale, caret.Height * 0.78));
    }

    /// Font, size and colors for a suggestion at `caret`, measured in
    /// physical pixels.
    private sealed class Layout
    {
        public required double Scale { get; init; }
        public required Typeface Typeface { get; init; }
        /// Font size in device-independent units for this monitor.
        public required double Size { get; init; }
        public required double SizePx { get; init; }
        public required Color Color { get; init; }

        public FormattedText Format(string s) => GhostTextView.Format(s, Typeface, Size, Color, Scale);
        public double MeasurePx(string s) => Math.Ceiling(Format(s).WidthIncludingTrailingWhitespace * Scale);
        public FormattedText Hint(string hint) =>
            GhostTextView.Format(hint, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal),
                                 Math.Max(8, Size * 0.55), Color, Scale);
        /// Hint box plus the gap before it.
        public double HintWidthPx(string hint) => (Hint(hint).Width + 8 + HintGap) * Scale;
        public double LineHeightPx => Format("Xg").Height * Scale;
    }

    private static Layout LayoutFor(Rect caret, TextStyle style)
    {
        double scale = ScaleAt((int)caret.X, (int)caret.Y);
        var color = style.Color is Color c ? Color.FromArgb((byte)(255 * GhostAlpha), c.R, c.G, c.B) : FallbackColor;
        var family = new FontFamily(style.FontFamily is { Length: > 0 } f ? $"{f}, Segoe UI" : "Segoe UI");
        var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double sizePx = FontSizePx(style, typeface, caret, scale);
        return new Layout { Scale = scale, Typeface = typeface, Size = sizePx / scale, SizePx = sizePx, Color = color };
    }

    /// Width of `text` on one line including the accept-key hint, in
    /// physical pixels.
    public static double WidthPx(string text, Rect caret, TextStyle style, string hint)
    {
        var layout = LayoutFor(caret, style);
        return layout.MeasurePx(text) + layout.HintWidthPx(hint);
    }

    /// Shows `text` after `caret` (physical pixels), followed by the `hint`.
    /// With `wrap`, long text wraps like the field's own text; with `bubble`,
    /// it goes on a small card just below the caret's line (used in the
    /// middle of a line, where inline text would cover what follows).
    public void ShowSuggestion(string text, Rect caret, TextStyle style, string hint, WrapGuide? wrap = null, bool bubble = false)
    {
        var layout = LayoutFor(caret, style);
        LastFontSizePx = layout.SizePx;
        if (bubble)
        {
            ShowBubble(text, caret, layout, style, hint);
            return;
        }

        double lineHeightPx = layout.LineHeightPx;
        double hintWidthPx = layout.HintWidthPx(hint);

        // Lay out the words: first line from the caret, the rest from the line start.
        var lines = wrap is { } w && w.RightEdge - w.LineStartX > 40
            ? Wrap(text, layout.MeasurePx, w.RightEdge - caret.Right, w.RightEdge - w.LineStartX)
            : [text];
        if (lines.Count > MaxLines) lines = lines.Take(MaxLines).ToList();
        LastLineCount = lines.Count;

        double left = wrap is null ? caret.Right : Math.Min(wrap.LineStartX, caret.Right);
        var placed = new List<(string Text, double X)>();
        double maxX = caret.Right;
        for (int i = 0; i < lines.Count; i++)
        {
            double x = i == 0 ? caret.Right : wrap!.LineStartX;
            placed.Add((lines[i], x - left));
            maxX = Math.Max(maxX, x + layout.MeasurePx(lines[i]));
        }
        // The hint goes after the last word if it fits.
        double lastEnd = left + placed[^1].X + layout.MeasurePx(placed[^1].Text);
        bool showHint = wrap is null || lastEnd + hintWidthPx <= wrap.RightEdge + 2;
        if (showHint) maxX = Math.Max(maxX, lastEnd + hintWidthPx);

        // Line pitch: the caret's height where it is one line tall.
        double pitchPx = caret.Height <= lineHeightPx * 1.6 ? Math.Max(caret.Height, lineHeightPx) : lineHeightPx;
        // Apps with line spacing report a caret (character box) taller than
        // the text, with the glyphs at its bottom (Notepad, Word): align to
        // the bottom there, else center on the caret.
        double top = caret.Height > lineHeightPx ? caret.Bottom - lineHeightPx : caret.Top + (caret.Height - lineHeightPx) / 2;

        int x0 = (int)Math.Round(left);
        int y0 = (int)Math.Round(top);
        int width = (int)Math.Ceiling(maxX - left + 2);
        int height = (int)Math.Ceiling(pitchPx * (placed.Count - 1) + lineHeightPx);
        if (wrap is null)
        {
            // Keep it on the caret's monitor.
            var work = WorkAreaAt(x0, y0);
            if (x0 + width > work.Right) x0 = Math.Max(work.Left, work.Right - width);
        }

        view.Configure(placed.Select(p => (layout.Format(p.Text), p.X / layout.Scale)).ToList(), pitchPx / layout.Scale,
                       showHint ? layout.Hint(hint) : null, layout.Color, card: null);
        Place(x0, y0, width, height, layout.Scale);
    }

    private void ShowBubble(string text, Rect caret, Layout layout, TextStyle style, string hint)
    {
        LastLineCount = 1;
        var trimmed = text.TrimStart();
        double padding = BubblePadding * layout.Scale;
        double lineHeightPx = layout.LineHeightPx + 4 * layout.Scale;
        var card = CardColors.For(style);
        var cardLayout = new Layout { Scale = layout.Scale, Typeface = layout.Typeface, Size = layout.Size, SizePx = layout.SizePx, Color = card.Text };
        double width = cardLayout.MeasurePx(trimmed) + cardLayout.HintWidthPx(hint) + padding * 2;
        int x0 = (int)Math.Round(caret.Right - padding);
        int y0 = (int)Math.Round(caret.Bottom + 3 * layout.Scale);
        var work = WorkAreaAt(x0, y0);
        if (x0 + width > work.Right) x0 = (int)Math.Max(work.Left, work.Right - width);

        view.Configure([(cardLayout.Format(trimmed), BubblePadding)], lineHeightPx / layout.Scale, cardLayout.Hint(hint), card.Text, card);
        Place(x0, y0, (int)Math.Ceiling(width), (int)Math.Ceiling(lineHeightPx), layout.Scale);
    }

    private void Place(int x, int y, int w, int h, double scale)
    {
        if (!IsShowing)
        {
            Show();
            IsShowing = true;
        }
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOOWNERZORDER);
        // Moving to a monitor with another DPI makes WPF resize the window
        // for that DPI; put the size back.
        if (scale != lastScale)
        {
            lastScale = scale;
            SetWindowPos(hwnd, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }
        view.InvalidateVisual();
    }

    public void HideSuggestion()
    {
        if (!IsShowing) return;
        IsShowing = false;
        Hide();
    }

    /// Greedy word wrap. The first line may stay empty when not even one word
    /// fits after the caret; continuation lines drop their leading space.
    public static List<string> Wrap(string text, Func<string, double> measure, double firstWidth, double otherWidth)
    {
        var lines = new List<string>();
        var current = "";
        double available = firstWidth;
        foreach (var word in PromptBuilder.SplitWords(text))
        {
            var candidate = current + word;
            if (measure(candidate) <= available || (current.Length == 0 && lines.Count > 0))
            {
                current = current.Length == 0 && lines.Count > 0 ? word.TrimStart() : candidate;
            }
            else
            {
                lines.Add(current);
                current = word.TrimStart();
                available = otherWidth;
            }
        }
        lines.Add(current);
        return lines;
    }

    /// The mid-line card's colors: dark when the field's text is light (or,
    /// without a text color, when Windows apps use the dark theme).
    public sealed record CardColors(Color Background, Color Border, Color Text)
    {
        public static CardColors For(TextStyle style)
        {
            bool dark;
            if (style.Color is Color c) dark = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255 > 0.5;
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                dark = key?.GetValue("AppsUseLightTheme") is int light && light == 0;
            }
            return dark
                ? new CardColors(Color.FromArgb(247, 0x2B, 0x2B, 0x2B), Color.FromRgb(0x4A, 0x4A, 0x4A), Color.FromRgb(0xB4, 0xB4, 0xB4))
                : new CardColors(Color.FromArgb(247, 0xFB, 0xFB, 0xFB), Color.FromRgb(0xCF, 0xCF, 0xCF), Color.FromRgb(0x6E, 0x6E, 0x6E));
        }
    }

    /// Draws the laid-out ghost text lines, the accept-key hint and, for the
    /// mid-line card, its background.
    private sealed class GhostTextView : FrameworkElement
    {
        private List<(FormattedText Text, double X)> lines = [];
        private double pitch;
        private FormattedText? hint;
        private Color color;
        private CardColors? card;

        public static FormattedText Format(string s, Typeface typeface, double size, Color color, double scale) =>
            new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, new SolidColorBrush(color), scale);

        public void Configure(List<(FormattedText Text, double X)> lines, double pitch, FormattedText? hint, Color color, CardColors? card)
        {
            this.lines = lines;
            this.pitch = pitch;
            this.hint = hint;
            this.color = color;
            this.card = card;
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (lines.Count == 0) return;
            if (card is not null)
            {
                var bounds = new Rect(0.5, 0.5, Math.Max(0, ActualWidth - 1), Math.Max(0, ActualHeight - 1));
                dc.DrawRoundedRectangle(new SolidColorBrush(card.Background), new Pen(new SolidColorBrush(card.Border), 1), bounds, 5, 5);
            }
            for (int i = 0; i < lines.Count; i++)
            {
                double y = card is null ? i * pitch : (pitch - lines[i].Text.Height) / 2;
                dc.DrawText(lines[i].Text, new Point(lines[i].X, y));
            }
            if (hint is null) return;
            var last = lines[^1];
            double lineTop = card is null ? (lines.Count - 1) * pitch : 0;
            double lineHeight = card is null ? last.Text.Height : pitch;
            double x = last.X + last.Text.WidthIncludingTrailingWhitespace + HintGap;
            var box = new Rect(x, lineTop + (lineHeight - hint.Height - 2) / 2, hint.Width + 8, hint.Height + 2);
            var pen = new Pen(new SolidColorBrush(color), 1);
            dc.DrawRoundedRectangle(null, pen, new Rect(box.X + 0.5, box.Y + 0.5, box.Width - 1, box.Height - 1), 3, 3);
            dc.DrawText(hint, new Point(box.X + 4, box.Y + 1));
        }
    }
}
