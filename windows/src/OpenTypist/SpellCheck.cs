using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32;

namespace OpenTypist;

/// The Windows spell checker (Spell Checking API, the one Windows' own text
/// controls use), limited to the user's Windows display languages. The
/// Windows side of `SpellCheck` in Autocorrector.swift; the keyboard typo
/// costs are in `Typos`. Checkers are created per thread on first use.
public static class SpellCheck
{
    [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellCheckerFactory
    {
        IEnumString SupportedLanguages { get; }
        [return: MarshalAs(UnmanagedType.Bool)]
        bool IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
        ISpellChecker CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
    }

    [ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellChecker
    {
        string LanguageTag { [return: MarshalAs(UnmanagedType.LPWStr)] get; }
        IEnumSpellingError Check([MarshalAs(UnmanagedType.LPWStr)] string text);
        IEnumString Suggest([MarshalAs(UnmanagedType.LPWStr)] string word);
        // Add, Ignore, AutoCorrect, GetOptionValue, ... follow; not used.
    }

    [ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumSpellingError
    {
        /// S_OK with an error, S_FALSE when there are no more.
        [PreserveSig]
        int Next(out IntPtr error);
    }

    [ComImport, Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC")]
    private class SpellCheckerFactory { }

    [ThreadStatic] private static List<ISpellChecker>? checkers;

    /// BCP-47 tags of the user's Windows languages that have a spell checker.
    public static IReadOnlyList<string> Languages { get; } = DetectLanguages();

    private static List<string> DetectLanguages()
    {
        var preferred = new List<string>();
        using (var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\International\User Profile"))
        {
            if (key?.GetValue("Languages") is string[] tags) preferred.AddRange(tags);
        }
        preferred.Add(System.Globalization.CultureInfo.CurrentUICulture.Name);
        var result = new List<string>();
        try
        {
            var factory = (ISpellCheckerFactory)new SpellCheckerFactory();
            foreach (var tag in preferred.Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (factory.IsSupported(tag)) result.Add(tag);
            }
            if (result.Count == 0 && factory.IsSupported("en-US")) result.Add("en-US");
        }
        catch (COMException e)
        {
            AppStatus.Log($"spell checker unavailable: {e.Message}");
        }
        return result;
    }

    private static List<ISpellChecker> Checkers()
    {
        if (checkers is not null) return checkers;
        checkers = [];
        try
        {
            var factory = (ISpellCheckerFactory)new SpellCheckerFactory();
            foreach (var tag in Languages) checkers.Add(factory.CreateSpellChecker(tag));
        }
        catch (COMException e)
        {
            AppStatus.Log($"spell checker unavailable: {e.Message}");
        }
        return checkers;
    }

    /// Whether any of the user's languages knows `word` (also true when no
    /// spell checker is available, so nothing is "fixed" blindly).
    public static bool IsValidWord(string word)
    {
        var list = Checkers();
        if (list.Count == 0) return true;
        foreach (var checker in list)
        {
            try
            {
                var errors = checker.Check(word);
                int hr = errors.Next(out var error);
                if (error != IntPtr.Zero) Marshal.Release(error);
                Marshal.ReleaseComObject(errors);
                if (hr != 0) return true;
            }
            catch (COMException) { return true; }
        }
        return false;
    }

    /// Up to four single-word suggestions close to `word`.
    public static List<string> Guesses(string word)
    {
        var list = Checkers();
        var options = new List<string>();
        if (list.Count == 0) return options;
        try
        {
            var suggestions = list[0].Suggest(word);
            var buffer = new string[1];
            while (options.Count < 12 && suggestions.Next(1, buffer, IntPtr.Zero) == 0) options.Add(buffer[0]);
            Marshal.ReleaseComObject(suggestions);
        }
        catch (COMException) { }
        var seen = new HashSet<string>();
        return options
            .Where(o => !o.Contains(' ') && Typos.EditDistance(o.ToLowerInvariant(), word.ToLowerInvariant()) <= 2 && seen.Add(o))
            .Take(4)
            .Select(o => AutocorrectRules.ConformCase(word, o))
            .ToList();
    }
}
