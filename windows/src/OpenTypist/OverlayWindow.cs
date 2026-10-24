using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using static OpenTypist.Win32;

namespace OpenTypist;

/// Topmost, click-through, non-activating layered window that draws the
/// suggestion as ghost text just after the caret, followed by a small hint
/// with the accept key. It has no background so the text reads as part of the
/// field: the field's own font and a faded (45% alpha) version of its text
/// color when UI Automation reports them, otherwise grey. The Windows side of
/// OverlayWindow.swift. Positions are physical pixels (per-monitor DPI v2).
public sealed class OverlayWindow : Window
{
    /// Used when the app doesn't report its text color.
    private static readonly Color FallbackColor = Color.FromArgb(230, 128, 128, 128);
    /// How much of the real text color the ghost text keeps.
    private const double GhostAlpha = 0.45;
    public const double HintGap = 6;

    private readonly GhostTextView view = new();
    private IntPtr hwnd;

    public bool IsShowing { get; private set; }
    /// For diagnostics: the font size the last suggestion was drawn with.
    public double LastFontSizePx { get; private set; }

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
    /// comes from the on-screen width of the character before the caret.
    public static double FontSizePx(TextStyle style, Typeface typeface, Rect caret, double scale)
    {
        if (style.FontSizePx is double px)
        {
            if (style.PreviousCharacter is char c && !char.IsSurrogate(c) && !char.IsControl(c)
                && style.PreviousCharacterWidthPx is double width && width >= 2)
            {
                double advance = GhostTextView.Format(c.ToString(), typeface, px, Colors.Black, 1).WidthIncludingTrailingWhitespace;
                if (advance >= 2)
                {
                    double zoom = Math.Clamp(width / advance, 0.25, 5);
                    return Math.Abs(zoom - 1) < 0.1 ? px : px * zoom;
                }
            }
            if (px <= caret.Height * 1.05) return px;
        }
        return Math.Min(28 * scale, Math.Max(11 * scale, caret.Height * 0.78));
    }

    /// Shows `text` after `caret` (physical pixels), followed by the `hint`.
    public void ShowSuggestion(string text, Rect caret, TextStyle style, string hint)
    {
        double scale = ScaleAt((int)caret.X, (int)caret.Y);
        var color = style.Color is Color c ? Color.FromArgb((byte)(255 * GhostAlpha), c.R, c.G, c.B) : FallbackColor;
        var family = new FontFamily(style.FontFamily is { Length: > 0 } f ? $"{f}, Segoe UI" : "Segoe UI");
        var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double sizePx = FontSizePx(style, typeface, caret, scale);
        LastFontSizePx = sizePx;

        // Lay out in device-independent units for this monitor.
        double size = sizePx / scale;
        var formatted = GhostTextView.Format(text, typeface, size, color, scale);
        var hintFormatted = GhostTextView.Format(hint, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal),
                                                 Math.Max(8, size * 0.55), color, scale);
        double lineHeight = formatted.Height;
        double width = formatted.WidthIncludingTrailingWhitespace + HintGap + hintFormatted.Width + 8 + 2;

        int x = (int)Math.Round(caret.Right);
        int w = (int)Math.Ceiling(width * scale);
        int h = (int)Math.Ceiling(lineHeight * scale);
        // Apps with line spacing report a caret (character box) taller than
        // the text, with the glyphs at its bottom (Notepad, Word): align to
        // the bottom there, else center on the caret.
        int y = caret.Height > h
            ? (int)Math.Round(caret.Bottom) - h
            : (int)Math.Round(caret.Top + (caret.Height - h) / 2);
        // Keep it on the caret's monitor.
        var work = WorkAreaAt(x, y);
        if (x + w > work.Right) x = Math.Max(work.Left, work.Right - w);

        view.Configure(formatted, hintFormatted, lineHeight, color);
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

    private double lastScale;

    public void HideSuggestion()
    {
        if (!IsShowing) return;
        IsShowing = false;
        Hide();
    }

    /// Draws the ghost text and the accept-key hint.
    private sealed class GhostTextView : FrameworkElement
    {
        private FormattedText? text;
        private FormattedText? hint;
        private double lineHeight;
        private Color color;

        public static FormattedText Format(string s, Typeface typeface, double size, Color color, double scale) =>
            new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, new SolidColorBrush(color), scale);

        public void Configure(FormattedText text, FormattedText hint, double lineHeight, Color color)
        {
            this.text = text;
            this.hint = hint;
            this.lineHeight = lineHeight;
            this.color = color;
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (text is null || hint is null) return;
            dc.DrawText(text, new Point(0, (lineHeight - text.Height) / 2));
            double x = text.WidthIncludingTrailingWhitespace + HintGap;
            var box = new Rect(x, (lineHeight - hint.Height - 2) / 2, hint.Width + 8, hint.Height + 2);
            var pen = new Pen(new SolidColorBrush(color), 1);
            dc.DrawRoundedRectangle(null, pen, new Rect(box.X + 0.5, box.Y + 0.5, box.Width - 1, box.Height - 1), 3, 3);
            dc.DrawText(hint, new Point(box.X + 4, box.Y + 1));
        }
    }
}
