using System.Text;

namespace OpenTypist;

/// A completion shown to the user, anchored to the text that was before the
/// caret when it was generated. A port of `Suggestion` in Suggestion.swift.
public sealed class Suggestion
{
    /// Tail of the text before the caret at generation time.
    public string AnchorTail { get; }
    /// Full suggested continuation.
    public string Text { get; }
    public int ProcessId { get; }

    public Suggestion(string textBefore, string text, int processId)
    {
        AnchorTail = TextUtil.Suffix(textBefore, 48);
        Text = text;
        ProcessId = processId;
    }

    /// How many characters of the suggestion the user has already typed (or
    /// accepted), given the new text before the caret. null when the text no
    /// longer matches, e.g. after a typo or a caret move.
    public int? ConsumedCount(string textBefore)
    {
        for (int k = Text.Length; k >= 0; k--)
        {
            if (textBefore.EndsWith(AnchorTail + Text[..k], StringComparison.Ordinal)) return k;
        }
        return null;
    }

    public string? Remaining(string textBefore)
    {
        if (ConsumedCount(textBefore) is not int k) return null;
        var rest = Text[k..];
        return rest.Trim(' ', '\t').Length == 0 ? null : rest;
    }

    /// The next word of `remaining`, including its leading whitespace and,
    /// optionally, the single space after it.
    public static string NextWord(string remaining, bool includeTrailingSpace)
    {
        var result = new StringBuilder();
        bool sawWord = false;
        foreach (var c in remaining)
        {
            if (char.IsWhiteSpace(c))
            {
                if (sawWord)
                {
                    if (includeTrailingSpace && c == ' ') result.Append(c);
                    break;
                }
                result.Append(c);
            }
            else
            {
                sawWord = true;
                result.Append(c);
            }
        }
        return result.ToString();
    }
}
