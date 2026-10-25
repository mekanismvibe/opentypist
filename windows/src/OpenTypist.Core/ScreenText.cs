namespace OpenTypist;

/// Picks what of a window's recognized text goes into the prompt, the pure
/// part of ScreenContext.swift: drop what the user typed (it is already in the
/// prompt), keep the lines nearest the caret within the budget, then put them
/// back in reading order. On Windows the text field's own area is dropped
/// too: mid-line, the text after the caret isn't "typed before" it, and OCR
/// reads the caret as a letter.
public static class ScreenText
{
    /// Characters of on-screen text to include in a prompt.
    public const int Budget = 900;

    public sealed record Line(string Text, double X, double Y);

    /// The text field, in the same (screen) coordinates as the lines.
    public readonly record struct Area(double Left, double Top, double Right, double Bottom)
    {
        public bool Contains(Line line) => line.X >= Left && line.X <= Right && line.Y >= Top && line.Y <= Bottom;
    }

    public static string Select(IEnumerable<Line> recognized, double? caretY, string typed, int budget = Budget, Area? field = null)
    {
        var typedText = Normalize(TextUtil.Suffix(typed, 2000));
        var lines = recognized.Where(l =>
        {
            if (field is { } area && area.Contains(l)) return false;
            var normalized = Normalize(l.Text);
            return normalized.Length > 0 && !(normalized.Length >= 4 && typedText.Contains(normalized, StringComparison.Ordinal));
        }).ToList();

        if (caretY is double y) lines = lines.OrderBy(l => Math.Abs(l.Y - y)).ToList();
        var kept = new List<Line>();
        int used = 0;
        foreach (var line in lines)
        {
            if (used + line.Text.Length + 1 > budget) break;
            kept.Add(line);
            used += line.Text.Length + 1;
        }
        return string.Join("\n", kept.OrderBy(l => (int)(l.Y / 6)).ThenBy(l => l.X).Select(l => l.Text));
    }

    private static string Normalize(string text) =>
        string.Join(" ", text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
