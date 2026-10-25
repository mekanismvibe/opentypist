namespace OpenTypist;

/// A user snippet: typing `Shortcut` and then a space or punctuation inserts
/// `Expansion`.
public sealed record Snippet(Guid Id, string Shortcut, string Expansion);

/// Snippets that work in every app. Windows has no system-wide list like
/// macOS's Text Replacements, so these are the user's own, stored encrypted
/// like the other personal data. The Windows side of TextReplacements.swift.
/// `Match` runs on the keyboard hook thread; everything else on the UI thread.
public sealed class TextReplacements
{
    private const string FileName = "snippets.bin";
    private readonly SecureStore store;
    private readonly AppSettings settings;
    private volatile AutocorrectRules rules = new();

    public List<Snippet> Snippets { get; }
    public event Action? Changed;

    public TextReplacements(AppSettings settings, SecureStore? store = null)
    {
        this.settings = settings;
        this.store = store ?? SecureStore.Shared;
        Snippets = this.store.Load<List<Snippet>>(FileName) ?? [];
        Rebuild();
    }

    /// A replacement for what was just typed, if its shortcut ends there.
    public AutocorrectMatch? Match(string typed) => settings.TextReplacementsEnabled ? rules.MatchAtEnd(typed) : null;

    /// Returns a message if the snippet can't be added.
    public string? Add(string shortcut, string expansion)
    {
        shortcut = shortcut.Trim();
        expansion = expansion.Replace("\r\n", "\n");
        if (shortcut.Length == 0 || expansion.Length == 0) return "Enter a shortcut and the text it should become.";
        if (shortcut.Any(char.IsWhiteSpace)) return "Shortcuts can't contain spaces.";
        if (AutocorrectRules.IsEndCharacter(shortcut[^1])) return "Shortcuts can't end with punctuation; that's what triggers them.";
        if (Snippets.Any(s => s.Shortcut.Equals(shortcut, StringComparison.OrdinalIgnoreCase))) return $"There's already a snippet for \"{shortcut}\".";
        Snippets.Add(new Snippet(Guid.NewGuid(), shortcut, expansion));
        Save();
        return null;
    }

    public void Remove(Snippet snippet)
    {
        Snippets.RemoveAll(s => s.Id == snippet.Id);
        Save();
    }

    /// Imports a plain `shortcut->text` file (one per line, `\n` for a line
    /// break). Returns how many were added and why others weren't.
    public (int Added, List<string> Skipped) Import(string path)
    {
        int added = 0;
        var skipped = new List<string>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int arrow = line.IndexOf("->", StringComparison.Ordinal);
            if (arrow <= 0) { skipped.Add($"{line}: no \"->\""); continue; }
            var problem = Add(line[..arrow], line[(arrow + 2)..].Trim().Replace("\\n", "\n"));
            if (problem is null) added++;
            else skipped.Add($"{line[..arrow].Trim()}: {problem}");
        }
        return (added, skipped);
    }

    private void Save()
    {
        store.Save(FileName, Snippets);
        Rebuild();
        Changed?.Invoke();
    }

    private void Rebuild()
    {
        var fresh = new AutocorrectRules();
        foreach (var snippet in Snippets)
        {
            fresh.Add(new AutocorrectRule(snippet.Shortcut.ToLowerInvariant(), snippet.Expansion, Exact: true));
        }
        rules = fresh;
    }
}
