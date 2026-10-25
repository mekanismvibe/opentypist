using System.Globalization;
using System.Text;

namespace OpenTypist;

/// One misspelling → correction rule, with AutoHotkey hotstring semantics.
public sealed record AutocorrectRule(
    /// Lowercased unless `CaseSensitive`.
    string Trigger,
    /// Empty replacement = exception: the trigger is a real word that other
    /// rules must leave alone (AHK uses `::design::` to protect it from `:?:ign::ing`).
    string Replacement,
    bool CaseSensitive = false,
    /// AHK `*`: fires as soon as the trigger is typed, without an ending character.
    bool Immediate = false,
    /// AHK `?`: may match the end of a longer word.
    bool InsideWords = false,
    /// Insert the replacement exactly as written (snippets), instead of
    /// matching the capitalization that was typed.
    bool Exact = false);

/// What to do when the user finishes typing some text.
public abstract record AutocorrectMatch
{
    /// Replace the last `Length` typed characters (UTF-16 units) with `Replacement`.
    public sealed record Fix(string Replacement, int Length) : AutocorrectMatch;
    /// Several plausible corrections (or the "misspelling" is also a real
    /// word): let the language model pick using the surrounding text.
    public sealed record Ambiguous(string Word, IReadOnlyList<string> Candidates) : AutocorrectMatch
    {
        public bool Equals(Ambiguous? other) => other is not null && Word == other.Word && Candidates.SequenceEqual(other.Candidates);
        public override int GetHashCode() => Word.GetHashCode();
    }
}

/// All loaded autocorrect rules, and matching against recently typed text.
/// A port of AutocorrectRules.swift.
public sealed class AutocorrectRules
{
    private readonly Dictionary<string, AutocorrectRule> wordRules = [];
    private readonly Dictionary<string, AutocorrectRule> caseSensitiveWordRules = [];
    /// Rules whose trigger contains spaces or punctuation, or that match inside
    /// words; longest first, so more specific triggers win.
    private readonly List<AutocorrectRule> otherRules = [];
    private readonly List<AutocorrectRule> immediateRules = [];
    private readonly HashSet<string> exceptions = [];
    private readonly Dictionary<string, List<string>> ambiguous = [];

    public int RuleCount { get; private set; }

    /// Characters that end a word and trigger a check (AutoHotkey's defaults,
    /// minus the apostrophe so contractions stay one word).
    public const string EndCharacters = " \t\n\r-()[]{}:;\"/\\,.?!";

    public static bool IsEndCharacter(char c) => EndCharacters.Contains(c);

    // Loading

    public void Add(AutocorrectRule rule)
    {
        if (rule.Trigger.Length == 0) return;
        if (rule.Replacement.Length == 0)
        {
            exceptions.Add(rule.Trigger.ToLowerInvariant());
            return;
        }
        RuleCount++;
        if (rule.Immediate)
        {
            immediateRules.Add(rule);
        }
        else if (rule.InsideWords || rule.Trigger.Any(IsEndCharacter))
        {
            int index = otherRules.FindIndex(r => r.Trigger.Length < rule.Trigger.Length);
            otherRules.Insert(index < 0 ? otherRules.Count : index, rule);
        }
        else if (rule.CaseSensitive)
        {
            caseSensitiveWordRules[rule.Trigger] = rule;
        }
        else
        {
            wordRules[rule.Trigger] = rule;
        }
    }

    /// `misspelling->correction` or `misspelling->option1, option2` per line;
    /// `#` starts a comment.
    public int LoadArrowList(string text)
    {
        int added = 0;
        foreach (var rawLine in text.Split('\n', '\r'))
        {
            var line = rawLine.Trim(' ', '\t');
            if (line.StartsWith('#')) continue;
            int arrow = line.IndexOf("->", StringComparison.Ordinal);
            if (arrow < 0) continue;
            var typo = line[..arrow].Trim(' ', '\t').ToLowerInvariant();
            var options = line[(arrow + 2)..].Split(',').Select(o => o.Trim(' ', '\t')).Where(o => o.Length > 0).ToList();
            if (typo.Length == 0 || options.Count == 0) continue;
            if (options.Count == 1)
            {
                Add(new AutocorrectRule(typo, options[0]));
            }
            else
            {
                ambiguous[typo] = options;
                RuleCount++;
            }
            added++;
        }
        return added;
    }

    /// AutoHotkey hotstrings: `:options:trigger::replacement`. Supports the
    /// `*`, `?`, `C` and `B0` options, `#Hotstring` defaults, `;` comments and
    /// `/* */` blocks. Lines that are code rather than hotstrings are skipped.
    public int LoadAutoHotkey(string text)
    {
        int added = 0;
        bool inBlockComment = false;
        bool defaultNoBackspace = false;
        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n', '\r'))
        {
            var line = rawLine.Trim(' ', '\t');
            if (inBlockComment)
            {
                if (line.StartsWith("*/", StringComparison.Ordinal)) inBlockComment = false;
                continue;
            }
            if (line.StartsWith("/*", StringComparison.Ordinal)) { inBlockComment = true; continue; }
            if (line.StartsWith("#hotstring", StringComparison.OrdinalIgnoreCase))
            {
                var hotstringOptions = line["#hotstring".Length..].ToUpperInvariant();
                if (hotstringOptions.Contains("B0")) defaultNoBackspace = true;
                else if (hotstringOptions.Contains('B')) defaultNoBackspace = false;
                continue;
            }
            if (!line.StartsWith(':')) continue;
            var afterColon = line[1..];
            int optionsEnd = afterColon.IndexOf(':');
            if (optionsEnd < 0) continue;
            var options = afterColon[..optionsEnd];
            if (!options.All(c => "*?CcBbOoRr0123456789".Contains(c))) continue;
            var body = afterColon[(optionsEnd + 1)..];
            int separator = body.IndexOf("::", StringComparison.Ordinal);
            if (separator < 0) continue;
            var trigger = body[..separator];
            var replacement = body[(separator + 2)..];
            // Comments need whitespace before the semicolon.
            int comment = replacement.IndexOf(" ;", StringComparison.Ordinal);
            if (comment < 0) comment = replacement.IndexOf("\t;", StringComparison.Ordinal);
            if (comment >= 0) replacement = replacement[..comment];
            replacement = replacement.Trim('\t');
            while (replacement.EndsWith(' ') && !trigger.EndsWith(' ')) replacement = replacement[..^1];
            // Special-key and escape syntax is out of scope.
            if (replacement.Contains('{') || replacement.Contains('`')) continue;

            var upper = options.ToUpperInvariant();
            bool noBackspace = upper.Contains("B0") || (defaultNoBackspace && !upper.Contains('B'));
            if (noBackspace && replacement.Length > 0) continue;
            bool caseSensitive = options.Contains('C') && !upper.Contains("C0") && !upper.Contains("C1");
            Add(new AutocorrectRule(caseSensitive ? trigger : trigger.ToLowerInvariant(), replacement, caseSensitive,
                                    Immediate: options.Contains('*'), InsideWords: options.Contains('?')));
            added++;
        }
        return added;
    }

    /// Loads a `.ahk` script or an arrow list. AutoHotkey scripts are often
    /// Windows-1252, so anything that isn't valid UTF-8 is read as that.
    public int LoadFile(string path)
    {
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(data);
            if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        }
        catch (DecoderFallbackException)
        {
            text = Latin1Windows1252(data);
        }
        return Path.GetExtension(path).Equals(".ahk", StringComparison.OrdinalIgnoreCase) ? LoadAutoHotkey(text) : LoadArrowList(text);
    }

    /// Windows-1252 without the code-pages provider: Latin-1 plus the
    /// 0x80-0x9F block.
    private static string Latin1Windows1252(byte[] data)
    {
        const string high = "€\u0081‚ƒ„…†‡ˆ‰Š‹Œ\u008DŽ\u008F\u0090‘’“”•–—˜™š›œ\u009DžŸ";
        var sb = new StringBuilder(data.Length);
        foreach (var b in data) sb.Append(b >= 0x80 && b <= 0x9F ? high[b - 0x80] : (char)b);
        return sb.ToString();
    }

    // Matching

    /// Called when an ending character is typed. `typed` is the recently typed
    /// text (since the last caret jump), without that ending character.
    public AutocorrectMatch? MatchAtEnd(string typed)
    {
        var word = LastWord(typed);
        if (word.Length == 0) return null;
        var lower = word.ToLowerInvariant();
        if (exceptions.Contains(lower)) return null;

        foreach (var rule in otherRules)
        {
            if (MatchLength(rule, typed) is not int length) continue;
            return new AutocorrectMatch.Fix(ReplacementFor(rule, typed[^length..]), length);
        }
        if (caseSensitiveWordRules.TryGetValue(word, out var exact)) return new AutocorrectMatch.Fix(exact.Replacement, word.Length);
        if (wordRules.TryGetValue(lower, out var rule2)) return new AutocorrectMatch.Fix(ReplacementFor(rule2, word), word.Length);
        if (ambiguous.TryGetValue(lower, out var options))
        {
            return new AutocorrectMatch.Ambiguous(word, options.Select(o => ConformCase(word, o)).ToList());
        }
        return null;
    }

    /// Called after each word character for AHK `*` rules.
    public AutocorrectMatch? MatchImmediate(string typed)
    {
        if (immediateRules.Count == 0 || exceptions.Contains(LastWord(typed).ToLowerInvariant())) return null;
        foreach (var rule in immediateRules)
        {
            if (MatchLength(rule, typed) is not int length) continue;
            return new AutocorrectMatch.Fix(ReplacementFor(rule, typed[^length..]), length);
        }
        return null;
    }

    /// Single-word fix for `word`, if any.
    public string? PlainCorrection(string word) => wordRules.TryGetValue(word.ToLowerInvariant(), out var rule) ? rule.Replacement : null;

    private static int? MatchLength(AutocorrectRule rule, string typed)
    {
        var candidate = rule.CaseSensitive ? typed : typed.ToLowerInvariant();
        if (!candidate.EndsWith(rule.Trigger, StringComparison.Ordinal)) return null;
        int length = rule.Trigger.Length;
        if (!rule.InsideWords)
        {
            // Must start a word: preceded by an ending character or the start of what we saw.
            var before = typed[..^length];
            if (before.Length > 0 && !IsEndCharacter(before[^1])) return null;
        }
        return length;
    }

    private static string ReplacementFor(AutocorrectRule rule, string typed) =>
        rule.CaseSensitive || rule.Exact ? rule.Replacement : ConformCase(typed, rule.Replacement);

    public static string LastWord(string typed)
    {
        int start = typed.Length;
        while (start > 0 && !IsEndCharacter(typed[start - 1])) start--;
        return typed[start..];
    }

    /// AutoHotkey's case conformance: an all-caps trigger gives an all-caps
    /// replacement, a capitalized trigger a capitalized one.
    public static string ConformCase(string typed, string replacement)
    {
        var letters = typed.Where(char.IsLetter).ToList();
        if (letters.Count == 0) return replacement;
        if (letters.Count > 1 && letters.All(char.IsUpper)) return replacement.ToUpperInvariant();
        if (char.IsUpper(letters[0]) && replacement.Length > 0)
        {
            return char.ToUpperInvariant(replacement[0]) + replacement[1..];
        }
        return replacement;
    }

    /// How many Backspace presses delete `text`: one per user-perceived
    /// character, not per UTF-16 unit.
    public static int Backspaces(string text) => new StringInfo(text).LengthInTextElements;
}
