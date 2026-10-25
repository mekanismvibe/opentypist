namespace OpenTypist;

public enum CompletionLength { Short, Medium, Long }

/// How sure the model must be before a word is shown. Each generated token
/// needs at least `MinProbability`; the suggestion ends where one falls short.
public enum ConfidenceLevel { More, Balanced, Fewer }

public static class CompletionOptions
{
    public static string Title(this CompletionLength length) => length switch
    {
        CompletionLength.Short => "Short (up to 3 words)",
        CompletionLength.Medium => "Medium (up to 7 words)",
        _ => "Long (up to 15 words)",
    };

    public static int MaxWords(this CompletionLength length) => length switch
    {
        CompletionLength.Short => 3,
        CompletionLength.Medium => 7,
        _ => 15,
    };

    public static int MaxTokens(this CompletionLength length) => length switch
    {
        CompletionLength.Short => 10,
        CompletionLength.Medium => 20,
        _ => 36,
    };

    public static string Title(this ConfidenceLevel level) => level switch
    {
        ConfidenceLevel.More => "More suggestions",
        ConfidenceLevel.Balanced => "Balanced",
        _ => "Fewer, more accurate",
    };

    /// Measured on the Mac with `OpenTypist --calibrate` (283 positions in 7
    /// messages, Gemma 4 E2B): no filter shows 91% of the time with 26% of
    /// suggested words right; 0.12 → 86% / 36%; 0.25 → 74% / 50%; 0.45 → ~55% / ~73%.
    public static float MinProbability(this ConfidenceLevel level) => level switch
    {
        ConfidenceLevel.More => 0.12f,
        ConfidenceLevel.Balanced => 0.25f,
        _ => 0.45f,
    };

    /// Looser bar for finishing a half-typed word, which is right far more
    /// often than the words after it. Measured on 230 half-typed words: at
    /// "fewer", 0.45 alone hid 12.6% of completions; 0.15 shows 98.7%, 88% right.
    public static float WordMinProbability(this ConfidenceLevel level) => level switch
    {
        ConfidenceLevel.More => 0.06f,
        ConfidenceLevel.Balanced => 0.1f,
        _ => 0.15f,
    };
}
