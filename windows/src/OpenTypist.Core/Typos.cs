namespace OpenTypist;

/// Keyboard-aware edit distances for weighing spelling fixes, the pure part
/// of `SpellCheck` in Autocorrector.swift.
public static class Typos
{
    private static readonly string[] KeyboardRows = ["qwertyuiop", "asdfghjkl", "zxcvbnm"];

    /// Whether two letters sit next to each other on a QWERTY keyboard.
    public static bool AdjacentKeys(char a, char b)
    {
        static (int Row, int Column)? Position(char c)
        {
            for (int row = 0; row < KeyboardRows.Length; row++)
            {
                int column = KeyboardRows[row].IndexOf(c);
                if (column >= 0) return (row, column);
            }
            return null;
        }
        if (Position(a) is not (int pr, int pc) || Position(b) is not (int qr, int qc)) return false;
        if (pr == qr) return Math.Abs(pc - qc) == 1;
        // Rows are staggered: a key touches the same column and one to the side.
        if (qr == pr + 1) return qc == pc || qc == pc - 1;
        if (qr == pr - 1) return qc == pc || qc == pc + 1;
        return false;
    }

    /// How costly it is to have typed `typed` when meaning `intended`: swapped
    /// letters, missed or doubled letters and neighbouring keys are cheap,
    /// other substitutions expensive.
    public static double TypoCost(string typed, string intended)
    {
        var a = intended.ToLowerInvariant();
        var b = typed.ToLowerInvariant();
        if (a.Length == 0 || b.Length == 0) return Math.Max(a.Length, b.Length);
        var d = new double[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i * 0.8;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j * 0.8;
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                double substitution = a[i - 1] == b[j - 1] ? 0 : (AdjacentKeys(a[i - 1], b[j - 1]) ? 0.8 : 1.5);
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 0.8, d[i, j - 1] + 0.8), d[i - 1, j - 1] + substitution);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 0.6);
                }
            }
        }
        return d[a.Length, b.Length];
    }

    /// Optimal string alignment distance (transpositions count as one edit).
    public static int EditDistance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
        }
        return d[a.Length, b.Length];
    }
}
