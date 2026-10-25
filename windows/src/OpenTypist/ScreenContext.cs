using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Threading;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace OpenTypist;

/// Reads the visible text of the window being typed in, so prompts can
/// include the email thread or chat messages around the text field. The
/// Windows side of ScreenContext.swift: the window is captured with
/// PrintWindow and read with Windows' own OCR (Windows.Media.Ocr, in the
/// user's languages) in the background, once per window and again after a
/// while; prompts use the last result, so a keystroke never waits for it.
/// Images and text are never written to disk. Off unless turned on (Windows
/// has no permission prompt for this, unlike macOS).
public sealed class ScreenContext
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(90);

    private readonly AppSettings settings;
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private (string Key, string Text, DateTime Date)? entry;
    private string? inFlightKey;

    public ScreenContext(AppSettings settings)
    {
        this.settings = settings;
    }

    public static bool IsAvailable => OcrEngine.TryCreateFromUserProfileLanguages() is not null;

    /// UI thread. Visible text of the window around the caret, or null while
    /// it isn't available yet (or the feature is off).
    public string? TextFor(FocusContext context)
    {
        if (!settings.UseScreenContext || context.Window == IntPtr.Zero) return null;
        var key = $"{context.ProcessId}:{context.Window}:{context.WindowTitle}";
        if (entry is { } current && current.Key == key)
        {
            if (DateTime.Now - current.Date > MaxAge) Capture(key, context);
            return current.Text.Length == 0 ? null : current.Text;
        }
        Capture(key, context);
        return null;
    }

    private void Capture(string key, FocusContext context)
    {
        if (inFlightKey == key) return;
        inFlightKey = key;
        double? caretY = context.Caret is { } caret ? (caret.Top + caret.Bottom) / 2 : null;
        // The whole line around the caret counts as typed.
        var typed = context.TextBefore + context.TextAfterOnLine;
        ScreenText.Area? field = context.ElementFrame is { } frame ? new(frame.Left, frame.Top, frame.Right, frame.Bottom) : null;
        var window = context.Window;
        Task.Run(async () =>
        {
            string text;
            try { text = await Recognize(window, caretY, typed, field); }
            catch (Exception e)
            {
                AppStatus.Log($"screen context: {e.Message}");
                text = "";
            }
            await dispatcher.BeginInvoke(() =>
            {
                if (inFlightKey == key) inFlightKey = null;
                entry = (key, text, DateTime.Now);
                AppStatus.Note($"Read {text.Length} characters of on-screen context");
            });
        });
    }

    private static async Task<string> Recognize(IntPtr window, double? caretY, string typed, ScreenText.Area? field)
    {
        if (!GetWindowRect(window, out var rect) || rect.Right - rect.Left < 80 || rect.Bottom - rect.Top < 80) return "";
        using var bitmap = CaptureWindow(window, rect.Right - rect.Left, rect.Bottom - rect.Top);
        if (bitmap is null) return "";
        var lines = await RecognizeLines(bitmap);
        return ScreenText.Select(lines.Select(l => l with { X = l.X + rect.Left, Y = l.Y + rect.Top }), caretY, typed, field: field);
    }

    /// The window's own pixels (not what covers it); PW_RENDERFULLCONTENT
    /// includes GPU-drawn content such as Chromium's.
    private static Bitmap? CaptureWindow(IntPtr window, int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        var hdc = g.GetHdc();
        bool ok = PrintWindow(window, hdc, PW_RENDERFULLCONTENT);
        g.ReleaseHdc(hdc);
        if (ok) return bitmap;
        bitmap.Dispose();
        return null;
    }

    /// OCR of a bitmap: one entry per line, at its left edge and vertical middle.
    public static async Task<List<ScreenText.Line>> RecognizeLines(Bitmap bitmap)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null) return [];
        int width = bitmap.Width, height = bitmap.Height;
        // Very large captures are scaled to the OCR engine's limit.
        double scale = Math.Min(1, OcrEngine.MaxImageDimension / (double)Math.Max(width, height));
        using var source = scale < 1 ? new Bitmap(bitmap, (int)(width * scale), (int)(height * scale)) : (Bitmap)bitmap.Clone();
        var data = source.LockBits(new Rectangle(0, 0, source.Width, source.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] pixels;
        try
        {
            pixels = new byte[data.Stride * source.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        }
        finally { source.UnlockBits(data); }
        using var softwareBitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, source.Width, source.Height, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(softwareBitmap);
        var lines = new List<ScreenText.Line>();
        foreach (var line in result.Lines)
        {
            if (line.Words.Count == 0) continue;
            double left = line.Words.Min(w => w.BoundingRect.X);
            double top = line.Words.Min(w => w.BoundingRect.Y);
            double bottom = line.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
            lines.Add(new ScreenText.Line(line.Text, left / scale, (top + bottom) / 2 / scale));
        }
        return lines;
    }

    private const uint PW_RENDERFULLCONTENT = 2;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Win32.RECT rect);
}
