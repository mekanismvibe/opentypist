using System.Text;
using System.Text.RegularExpressions;

namespace OpenTypist;

/// Builds the model prompt and cleans up raw model output. A port of
/// `PromptBuilder` in Sources/OpenTypist/Suggestion.swift.
public static class PromptBuilder
{
    /// `WordPrefix`: the half-typed word at the caret, with the space before
    /// it, kept out of `Text`; the model's output must start with it (see
    /// `LlamaEngine.Complete`). Empty when the caret isn't mid-word.
    public sealed record Prompt(string Text, bool StrippedTrailingSpace, string WordPrefix);

    public const int MaxContextCharacters = 1200;

    public static Prompt Build(string textBefore, string? appName, string? windowTitle, string? screenText = null,
                               string? profile = null, IReadOnlyList<string>? examples = null)
    {
        var body = StartAtWordBoundary(textBefore);

        bool stripped = false;
        while (body.Length > 0 && (body[^1] == ' ' || body[^1] == '\t'))
        {
            body = body[..^1];
            stripped = true;
        }
        var wordPrefix = "";
        if (!stripped && TextUtil.EndsWithLetter(body))
        {
            int start = WordStart(body);
            wordPrefix = body[start..];
            body = body[..start];
            // The space before a word belongs to the word's token.
            if (body.EndsWith(' '))
            {
                body = body[..^1];
                wordPrefix = " " + wordPrefix;
            }
        }

        var header = Header(appName, windowTitle, screenText, profile, examples);
        return new Prompt(header + body, stripped, wordPrefix);
    }

    private static bool IsApostrophe(char c) => c is '\'' or '’';

    /// Where the word ending `text` starts: letters, with apostrophes between
    /// letters counting as part of it ("I’m", "don't").
    private static int WordStart(string text)
    {
        int i = text.Length;
        while (i > 0)
        {
            char c = text[i - 1];
            if (char.IsLetter(c) || (IsApostrophe(c) && i >= 2 && char.IsLetter(text[i - 2]) && i < text.Length && char.IsLetter(text[i]))) i--;
            else break;
        }
        return i;
    }

    /// The last `MaxContextCharacters` of the text, not starting mid-word.
    private static string StartAtWordBoundary(string text)
    {
        var body = TextUtil.Suffix(text, MaxContextCharacters);
        if (body.Length < text.Length)
        {
            int space = -1;
            for (int i = 0; i < body.Length; i++)
            {
                if (char.IsWhiteSpace(body[i])) { space = i; break; }
            }
            if (space >= 0) body = body[(space + 1)..];
        }
        return body;
    }

    /// Context goes first so it stays a stable, cached prefix while the user
    /// types, ordered from least to most often changing: the profile, then
    /// writing examples for this app, then the app, window and screen.
    public static string Header(string? appName, string? windowTitle, string? screenText,
                                string? profile = null, IReadOnlyList<string>? examples = null)
    {
        var header = new StringBuilder();
        if (!string.IsNullOrEmpty(profile))
        {
            header.Append("About me:\n").Append(profile).Append("\n\n");
        }
        if (examples is { Count: > 0 })
        {
            var place = appName is null ? "" : $" in {appName}";
            header.Append($"Examples of things I've written{place}:\n");
            header.Append(string.Join("\n", examples.Select(e => $"\"{e}\""))).Append("\n\n");
        }
        if (!string.IsNullOrEmpty(appName))
        {
            header.Append($"Application: {appName}\n");
            if (!string.IsNullOrEmpty(windowTitle) && windowTitle != appName)
            {
                header.Append($"Window: {TextUtil.Prefix(windowTitle, 120)}\n");
            }
        }
        if (!string.IsNullOrEmpty(screenText))
        {
            header.Append("Visible on screen:\n").Append(screenText).Append("\n\n");
        }
        if (header.Length > 0) header.Append("Text being written:\n");
        return header.ToString();
    }

    /// Prompt for scoring candidate words that come right after `context`.
    /// Returns the prompt and the prefix to put before each candidate: the
    /// space before a word belongs to the word's token, so it moves over.
    public static (string Text, string WordPrefix) ScoringPrompt(string context, string? appName, string? windowTitle, string? screenText,
                                                                 string? profile = null, IReadOnlyList<string>? examples = null)
    {
        var body = StartAtWordBoundary(context);
        var prefix = "";
        while (body.Length > 0 && (body[^1] == ' ' || body[^1] == '\t'))
        {
            body = body[..^1];
            prefix = " ";
        }
        return (Header(appName, windowTitle, screenText, profile, examples) + body, prefix);
    }

    /// Turns raw model output into the suggestion to display, or null if it is
    /// not worth showing.
    public static string? Clean(string raw, Prompt prompt, int maxWords, bool hitTokenLimit)
    {
        var text = raw;
        int newline = text.IndexOfAny(['\n', '\r', '\u000B', '\u000C', '\u0085', (char)0x2028, (char)0x2029]);
        if (newline >= 0) text = text[..newline];
        if (prompt.StrippedTrailingSpace)
        {
            // The user already typed the space, so the model must start a new word.
            if (text.Length == 0 || text[0] != ' ') return null;
            text = text[1..];
            if (text.Length > 0 && char.IsWhiteSpace(text[0])) return null;
        }

        var words = SplitWords(text);
        // Stopped by the token limit: the last word may be cut off.
        if (hitTokenLimit && words.Count > 1) words.RemoveAt(words.Count - 1);
        if (words.Count > maxWords) words = words.Take(maxWords).ToList();
        text = string.Concat(words).TrimEnd();

        return TextUtil.ContainsLetterOrNumber(text) ? text : null;
    }

    /// The part of the generated text the model was confident about: tokens
    /// up to the first one below `minProbability`. If that token would have
    /// continued the current word, the half-finished word goes too.
    /// Pieces that finish a half-typed word use the looser
    /// `wordMinProbability`, so the word gets completed even when the words
    /// after it are too uncertain to show.
    public static (string Text, bool HitTokenLimit) ConfidentText(IReadOnlyList<GeneratedToken> tokens, bool hitTokenLimit,
                                                                  float minProbability, float? wordMinProbability = null)
    {
        int cut = -1;
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Probability < (t.FinishesWord ? (wordMinProbability ?? minProbability) : minProbability)) { cut = i; break; }
        }
        if (cut < 0) return (Decode(tokens, tokens.Count), hitTokenLimit);

        var text = Decode(tokens, cut);
        bool startsNewWord = tokens[cut].Bytes.Length == 0 || tokens[cut].StartsNewWord;
        if (!startsNewWord)
        {
            int space = -1;
            for (int i = text.Length - 1; i >= 0; i--)
            {
                if (char.IsWhiteSpace(text[i])) { space = i; break; }
            }
            bool wordBeforeSpace = space > 0 && text[..space].Any(c => !char.IsWhiteSpace(c));
            text = wordBeforeSpace ? text[..space] : "";
        }
        return (text, false);
    }

    private static string Decode(IReadOnlyList<GeneratedToken> tokens, int count)
    {
        var bytes = new List<byte>();
        for (int i = 0; i < count; i++) bytes.AddRange(tokens[i].Bytes);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// Mid-line, the suggestion must lead into the text after the caret, not
    /// repeat it: "terested in this job" before " this job" becomes
    /// "terested in". Returns null if nothing is left to insert.
    public static string? FitBefore(string after, string suggestion)
    {
        static List<string> Normalized(string s) =>
            s.ToLowerInvariant()
             .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
             .Select(TextUtil.TrimPunctuation)
             .Where(w => w.Length > 0)
             .ToList();

        var afterWords = Normalized(after);
        var result = suggestion;
        if (afterWords.Count > 0)
        {
            var probe = afterWords.Take(2).ToList();
            var words = SplitWords(suggestion);
            for (int i = 0; i < words.Count; i++)
            {
                var rest = Normalized(string.Concat(words.Skip(i)));
                bool startsWithProbe = rest.Count >= probe.Count && rest.Take(probe.Count).SequenceEqual(probe);
                if (startsWithProbe || (probe.Count == 2 && rest.Count == 1 && rest[0] == probe[0]))
                {
                    result = string.Concat(words.Take(i));
                    break;
                }
            }
        }
        // Punctuation right after the caret is already there.
        var next = after.TrimStart(' ', '\t');
        if (next.Length > 0 && char.IsPunctuation(next[0]))
        {
            while (result.Length > 0 && char.IsPunctuation(result[^1])) result = result[..^1];
        }
        result = result.TrimEnd();
        return TextUtil.ContainsLetterOrNumber(result) ? result : null;
    }

    /// Whether a suggestion that finishes the half-typed word before the caret
    /// makes a real word ("tomor" + "row" yes, "tomor" + "ow" no). Suggestions
    /// that start a new word, short words and capitalized ones (often names
    /// the dictionary doesn't know) always pass; `isKnown` also covers the
    /// user's learned words.
    public static bool MakesRealWord(string textBefore, string suggestion, Func<string, bool> isKnown)
    {
        if (!TextUtil.EndsWithLetter(textBefore) || suggestion.Length == 0 || !char.IsLetter(suggestion[0])) return true;
        int start = textBefore.Length;
        while (start > 0 && char.IsLetter(textBefore[start - 1])) start--;
        int end = 0;
        while (end < suggestion.Length && char.IsLetter(suggestion[end])) end++;
        var word = textBefore[start..] + suggestion[..end];
        if (word.Length < 4 || !char.IsLower(word[0])) return true;
        return isKnown(word);
    }

    private static readonly Regex ChatTag = new(@"^\[/?[A-Za-z][A-Za-z0-9 _-]{0,24}\]", RegexOptions.Compiled);
    private static readonly Regex HtmlTag = new(@"</?[A-Za-z][A-Za-z0-9]*(\s[^<>]*)?>", RegexOptions.Compiled);
    // Whole words, so "has an aim" isn't caught.
    private static readonly Regex AiTalk = new(@"\bas (an ai|a language model)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// Why a suggestion the model was sure of is still not worth showing
    /// (phrased to follow "a suggestion that"), or null if it's fine.
    public static string? JunkReason(string suggestion, string textBefore)
    {
        var words = PlainWords(suggestion);
        // The model stuck in a loop: "Hi S. Hi S. Hi S."
        for (int period = 1; period <= 3; period++)
        {
            if (words.Count < period * 3) continue;
            int run = 0;
            for (int i = period; i < words.Count; i++)
            {
                run = words[i] == words[i - period] ? run + 1 : 0;
                if (run >= period * 2) return "repeats itself";
            }
        }
        // Going back over what was just typed instead of continuing it.
        var recent = PlainWords(TextUtil.Suffix(textBefore, 160));
        if (recent.Count >= 4 && words.Count >= 4)
        {
            var tail = recent.Skip(recent.Count - 4).ToList();
            for (int s = 0; s + 4 <= words.Count; s++)
            {
                if (words.Skip(s).Take(4).SequenceEqual(tail)) return "repeats what was just typed";
            }
        }
        // Leftovers from the chat logs and web pages the model learned from.
        var trimmed = suggestion.Trim(' ', '\t');
        if (ChatTag.IsMatch(trimmed)) return "starts with a chat or forum tag";
        if (!textBefore.Contains('<') && HtmlTag.IsMatch(trimmed)) return "contains HTML";
        if (AiTalk.IsMatch(trimmed)) return "talks about being an AI";
        return null;
    }

    /// Lowercased words without punctuation, for comparing text.
    private static List<string> PlainWords(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || IsApostrophe(c)) current.Append(c);
            else if (current.Length > 0) { words.Add(current.ToString()); current.Clear(); }
        }
        if (current.Length > 0) words.Add(current.ToString());
        return words;
    }

    /// Splits into words, each keeping its leading whitespace.
    public static List<string> SplitWords(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        bool inWord = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (inWord)
                {
                    words.Add(current.ToString());
                    current.Clear();
                    inWord = false;
                }
                current.Append(c);
            }
            else
            {
                inWord = true;
                current.Append(c);
            }
        }
        if (inWord) words.Add(current.ToString());
        return words;
    }

    /// The beginning of `text` up to the end of its first complete word, once
    /// that word has ended (a later word or punctuation has begun), cleaned
    /// like a final suggestion. null while the first word may still grow.
    public static string? FirstCompleteWord(string text, Prompt prompt)
    {
        var words = SplitWords(text);
        // The first word is complete once a second one has started, or once
        // the text after it has punctuation or whitespace.
        bool complete = words.Count >= 2 || (words.Count == 1 && text.Length > 0 && (char.IsWhiteSpace(text[^1]) || char.IsPunctuation(text[^1])));
        return complete ? Clean(words[0], prompt, 1, false) : null;
    }

    /// What to show while a suggestion is still being generated: only the
    /// words that are complete (a half-generated word may still change),
    /// cleaned like a final suggestion. Every token generated so far already
    /// passed the confidence filter.
    public static string? StreamingText(string text, Prompt prompt, int maxWords, string textAfter)
    {
        var words = SplitWords(text);
        if (text.Length > 0 && !char.IsWhiteSpace(text[^1]) && !char.IsPunctuation(text[^1]) && words.Count > 0) words.RemoveAt(words.Count - 1);
        if (words.Count == 0 || Clean(string.Concat(words), prompt, maxWords, false) is not string shown) return null;
        if (textAfter.Length > 0)
        {
            if (FitBefore(textAfter, shown) is not string fitted) return null;
            shown = fitted;
        }
        return shown;
    }

    /// Word count of the model output used as an early stop signal.
    public static bool ShouldStop(string text, int maxWords)
    {
        if (text.Any(TextUtil.IsNewline)) return true;
        // One extra word means the last kept word is complete.
        return SplitWords(text).Count > maxWords;
    }
}
