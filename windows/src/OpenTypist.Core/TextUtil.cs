using System.Text;

namespace OpenTypist;

/// Character tests that match Swift's `Character` properties closely enough
/// for the ported logic, working on UTF-16 strings without splitting
/// surrogate pairs.
public static class TextUtil
{
    public static bool IsNewline(char c) =>
        c == '\n' || c == '\r' || c == '\u000B' || c == '\u000C' || c == '\u0085' || c == LineSeparator || c == ParagraphSeparator;

    private const char LineSeparator = (char)0x2028;
    private const char ParagraphSeparator = (char)0x2029;

    /// Spaces and tabs only, like Swift's `.whitespaces` character set.
    public static bool IsHorizontalSpace(char c) => char.IsWhiteSpace(c) && !IsNewline(c);

    public static bool IsLetterOrNumber(Rune r) => Rune.IsLetter(r) || Rune.IsNumber(r);

    public static bool ContainsLetterOrNumber(string s)
    {
        foreach (var r in s.EnumerateRunes())
        {
            if (IsLetterOrNumber(r)) return true;
        }
        return false;
    }

    public static bool EndsWithLetter(string s) =>
        s.Length > 0 && Rune.DecodeLastFromUtf16(s, out var r, out _) == System.Buffers.OperationStatus.Done && Rune.IsLetter(r);

    /// The last `count` UTF-16 units, never starting with half a surrogate pair.
    public static string Suffix(string s, int count)
    {
        if (s.Length <= count) return s;
        int start = s.Length - count;
        if (char.IsLowSurrogate(s[start])) start++;
        return s[start..];
    }

    /// The first `count` UTF-16 units, never ending with half a surrogate pair.
    public static string Prefix(string s, int count)
    {
        if (s.Length <= count) return s;
        int end = count;
        if (end > 0 && char.IsHighSurrogate(s[end - 1])) end--;
        return s[..end];
    }

    public static string TrimEndWhitespace(string s) => s.TrimEnd();

    public static string TrimPunctuation(string s)
    {
        int start = 0, end = s.Length;
        while (start < end && char.IsPunctuation(s[start])) start++;
        while (end > start && char.IsPunctuation(s[end - 1])) end--;
        return s[start..end];
    }

    /// Line endings as "\n" only. Windows controls report "\r\n" or "\r".
    public static string NormalizeNewlines(string s) =>
        s.IndexOf('\r') < 0 ? s : s.Replace("\r\n", "\n").Replace('\r', '\n');
}
