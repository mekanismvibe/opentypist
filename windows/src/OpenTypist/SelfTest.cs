using System.Diagnostics;
using System.Text;
using static OpenTypist.Win32;

namespace OpenTypist;

/// `OpenTypist.exe --selftest [--model path.gguf] [--cpu]` runs the engine on
/// sample text without any UI, printing suggestions and per-keystroke
/// latency, then checks the pure logic. A port of SelfTest.swift; the sample
/// texts are the same so the numbers compare with the Mac app.
/// `--selftest-offline` runs only the checks that need no model (CI).
/// `--download <model id>` downloads and verifies a catalog model.
public static class SelfTest
{
    private static int failures;

    public static int Run(string[] args)
    {
        var settings = AppSettings.Load();
        var models = new ModelManager();
        string? path = Argument(args, "--model") ?? models.ActiveModelPath(settings.SelectedModelId, settings.CustomModelPath);
        if (path is null)
        {
            Console.WriteLine("No model found. Pass --model C:\\path\\to\\model.gguf, or download one with --download gemma-4-e2b");
            return 1;
        }
        bool useGpu = !args.Contains("--cpu");

        Console.WriteLine("== Devices");
        foreach (var device in LlamaEngine.Devices())
        {
            Console.WriteLine($"  {device.Name}: {device.Description} ({device.Type}, {device.TotalMiB} MiB)");
        }

        using var engine = new LlamaEngine();
        var start = Stopwatch.StartNew();
        try
        {
            engine.Load(path, useGpu);
        }
        catch (EngineException e)
        {
            Console.WriteLine($"Load failed: {e.Message}");
            return 1;
        }
        Console.WriteLine($"Loaded {engine.ModelDescription} in {start.ElapsedMilliseconds} ms on the {(engine.UsesGpu ? "GPU" : "CPU")}");
        // What "unload when idle" costs: reloading while Windows still caches the file.
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            engine.Unload();
            start.Restart();
            engine.Load(path, useGpu);
            Console.WriteLine($"Reload {attempt}: {start.ElapsedMilliseconds} ms");
        }
        // The app warms up before it reports Ready (GPU pipelines; slow only
        // the first time on a PC).
        start.Restart();
        engine.WarmUp();
        Console.WriteLine($"Warm-up: {start.ElapsedMilliseconds} ms");
        Console.WriteLine();

        (string Text, string? App, string? Title)[] cases =
        [
            ("Hi Sarah, thanks for getting back to me so quickly. I'm running a few minutes late, so ", "Mail", "Re: Lunch tomorrow"),
            ("Hi Sarah, thanks for getting back to me so quickly. I'm running a few minutes late, so I'll be th", "Mail", "Re: Lunch tomorrow"),
            ("The quarterly report shows that revenue grew by", "Pages", "Q3 Report"),
            ("hey are you still coming to the party tonight? i was thinking we could", "Messages", "Alex"),
            ("Dear hiring manager,\n\nI am writing to express my interest in the", "Mail", "Application"),
            ("Thanks for the update! Let me know if you need anything el", "Slack", "#design"),
            ("Thanks for the update! Let me know if you need anything el", null, null),
            ("I am very in", "Notepad", "Untitled - Notepad"),
        ];

        foreach (var length in new[] { CompletionLength.Short, CompletionLength.Medium })
        {
            Console.WriteLine($"== {length.Title()}");
            foreach (var (text, app, title) in cases)
            {
                start.Restart();
                var result = Suggest(engine, text, app, title, length);
                Console.WriteLine($"[{start.ElapsedMilliseconds} ms] {app ?? "-"} | ...{Quote(TextUtil.Suffix(text, 40))} -> {Quote(result)}");
            }
            Console.WriteLine();
        }

        Console.WriteLine("== With the Balanced confidence filter (what the app shows)");
        foreach (var (text, app, title) in cases)
        {
            var result = Suggest(engine, text, app, title, CompletionLength.Medium, ConfidenceLevel.Balanced);
            Console.WriteLine($"  ...{Quote(TextUtil.Suffix(text, 40))} -> {Quote(result)}");
        }
        Console.WriteLine();

        // Simulate typing one character at a time to measure per-keystroke latency.
        foreach (var level in new ConfidenceLevel?[] { null, ConfidenceLevel.Balanced })
        {
            const string sentence = "I wanted to follow up on our conversation from last week about the new project timeline";
            var latencies = new List<long>();
            // Streaming: when the first complete word could be on screen.
            var firstShown = new List<long>();
            var typed = new StringBuilder();
            Console.WriteLine(level is null ? "== Typing simulation" : $"== Typing simulation, {level.Value.Title()} filter");
            foreach (var c in sentence)
            {
                typed.Append(c);
                start.Restart();
                long? first = null;
                var result = Suggest(engine, typed.ToString(), "Mail", "Follow up", CompletionLength.Medium, level, _ => first ??= start.ElapsedMilliseconds);
                latencies.Add(start.ElapsedMilliseconds);
                if (result is not null) firstShown.Add(first ?? start.ElapsedMilliseconds);
                if (c == ' ' && level is null) Console.WriteLine($"...{Quote(TextUtil.Suffix(typed.ToString(), 30))} -> {Quote(result)}");
            }
            latencies.Sort();
            firstShown.Sort();
            Console.WriteLine($"per-keystroke latency: median {latencies[latencies.Count / 2]} ms, p90 {latencies[latencies.Count * 9 / 10]} ms, max {latencies[^1]} ms");
            if (firstShown.Count > 0)
            {
                Console.WriteLine($"streaming, first word on screen ({firstShown.Count} suggestions): median {firstShown[firstShown.Count / 2]} ms, p90 {firstShown[firstShown.Count * 9 / 10]} ms");
            }
            Console.WriteLine();
        }

        // On-screen context: the first prompt in a window pays for ~200 extra
        // tokens, later keystrokes reuse the cached prefix.
        const string thread = "From: Sarah Chen\nSubject: Lunch tomorrow?\nHey! Are we still on for lunch tomorrow at 12:30? I booked a table at the Thai place on 5th street. " +
                              "Let me know if you'd rather go somewhere else. Also, can you bring the slides from Tuesday's meeting? " +
                              "I want to go over the budget numbers with you before Friday.";
        Console.WriteLine("== On-screen context");
        var reply = "Hi Sarah, ";
        foreach (var addition in new[] { "", "sounds ", "good, ", "see you " })
        {
            reply += addition;
            start.Restart();
            var prompt = PromptBuilder.Build(reply, "Mail", "Re: Lunch tomorrow?", thread);
            var output = engine.Complete(prompt.Text, 20, prompt.HealLastToken, () => true, t => PromptBuilder.ShouldStop(t, 7));
            var text = output is null ? null : PromptBuilder.Clean(output.Text, prompt, 7, output.HitTokenLimit);
            Console.WriteLine($"[{start.ElapsedMilliseconds} ms] ...{Quote(TextUtil.Suffix(reply, 30))} -> {Quote(text)}");
        }

        // Does the profile (and do writing samples) change what the model suggests?
        PersonalizationChecks(engine);

        var hardware = Hardware.Current;
        Console.WriteLine();
        Console.WriteLine($"This PC: {hardware.Summary.Replace(" · ", ", ")} -> recommended {ModelCatalog.Recommended(hardware).Name}");

        AutocorrectChecks(engine, args);
        OfflineChecks();
        // ggml's GPU backends can assert if the process exits with a model loaded.
        engine.Unload();
        return failures == 0 ? 0 : 1;
    }

    /// List matching (optionally with `--ahk file.ahk`), the spell checker
    /// and, with a model, how the model picks between spellings.
    private static void AutocorrectChecks(LlamaEngine? engine, string[] args)
    {
        Console.WriteLine();
        Console.WriteLine("== Autocorrect lists");
        var rules = new AutocorrectRules();
        if (Autocorrector.BuiltInListPath is string builtIn) Console.WriteLine($"built-in: {rules.LoadFile(builtIn)} entries");
        else { failures++; Console.WriteLine("FAILED: built-in list not found"); }
        if (Argument(args, "--ahk") is string ahk) Console.WriteLine($"ahk: {rules.LoadFile(ahk)} hotstrings");
        string[] cases = ["I think teh", "Teh", "TEH", "becuase", "I recieved", "it was definately", "achive",
                          "We need to redesign", "a foriegn", "I am designign", "he doe snot", "it was dispell"];
        foreach (var text in cases)
        {
            var match = rules.MatchAtEnd(text) ?? rules.MatchImmediate(text);
            Console.WriteLine($"  {Quote(text)} -> {Describe(match)}");
        }
        Console.WriteLine($"  conformCase: {AutocorrectRules.ConformCase("Teh", "the")}, edit(teh, the) = {Typos.EditDistance("teh", "the")}");
        Check(rules.MatchAtEnd("I think teh") == new AutocorrectMatch.Fix("the", 3), "teh -> the");
        Check(rules.MatchAtEnd("TEH") == new AutocorrectMatch.Fix("THE", 3), "TEH -> THE");
        Check(rules.MatchAtEnd("Teh") == new AutocorrectMatch.Fix("The", 3), "Teh -> The");
        Check(rules.MatchAtEnd("We need to redesign") is null, "redesign stays");
        Check(rules.MatchAtEnd("achive") is AutocorrectMatch.Ambiguous { Candidates: ["achieve", "archive"] }, "achive is ambiguous");
        Check(Typos.EditDistance("teh", "the") == 1 && Typos.AdjacentKeys('e', 'r') && !Typos.AdjacentKeys('t', 'e'), "edit distance and keyboard");
        Check(Typos.TypoCost("recipt", "receipt") < Typos.TypoCost("recipt", "recipe"), "typo cost prefers a missed letter");

        // AutoHotkey hotstrings.
        var hotstrings = new AutocorrectRules();
        int loaded = hotstrings.LoadAutoHotkey(string.Join("\n",
            "; AutoCorrect.ahk style", "#Hotstring R", "::teh::the", ":*:btw::by the way", ":?:ign::ing", "::design::", ":C:US::United States",
            ":B0:keep::kept", "::comment::with comment ; ignored", "/*", "::inside::block", "*/", "MsgBox, not a hotstring", "::fun::{Enter}"));
        Console.WriteLine($"  ahk sample: {loaded} hotstrings");
        Check(loaded == 6, "ahk: six hotstrings (B0, block comment, code and {keys} skipped)");
        Check(hotstrings.MatchAtEnd("teh") == new AutocorrectMatch.Fix("the", 3), "ahk ::teh::the");
        Check(hotstrings.MatchImmediate("btw") == new AutocorrectMatch.Fix("by the way", 3), "ahk * fires without an ending");
        Check(hotstrings.MatchAtEnd("I am designign") == new AutocorrectMatch.Fix("ing", 3), "ahk ? fixes the end of a longer word");
        Check(hotstrings.MatchAtEnd("design") is null, "ahk exception ::design::");
        Check(hotstrings.MatchAtEnd("US") == new AutocorrectMatch.Fix("United States", 2) && hotstrings.MatchAtEnd("us") is null, "ahk C is case-sensitive");
        Check(hotstrings.MatchAtEnd("comment") == new AutocorrectMatch.Fix("with comment", 7), "ahk ; comment stripped");

        // Snippets insert exactly what was written.
        var snippets = new AutocorrectRules();
        snippets.Add(new AutocorrectRule("omw", "On my way!", Exact: true));
        snippets.Add(new AutocorrectRule("@@", "me@example.com", Exact: true));
        snippets.Add(new AutocorrectRule(";addr", "1 Main St\nSpringfield", Exact: true));
        Check(snippets.MatchAtEnd("ok OMW") == new AutocorrectMatch.Fix("On my way!", 3), "snippet omw");
        Check(snippets.MatchAtEnd("email: @@") == new AutocorrectMatch.Fix("me@example.com", 2), "snippet @@");
        Check(snippets.MatchAtEnd("send to ;addr") == new AutocorrectMatch.Fix("1 Main St\nSpringfield", 5), "snippet ;addr");
        Check(snippets.MatchAtEnd("x;addr") is null, "snippet must start a word");
        Check(AutocorrectRules.Backspaces("café") == 4 && AutocorrectRules.Backspaces("ok \U0001F44D") == 4, "backspaces per character");

        // The Windows spell checker.
        var clock = Stopwatch.StartNew();
        bool loosingValid = SpellCheck.IsValidWord("loosing");
        for (int i = 0; i < 100; i++) SpellCheck.IsValidWord(i % 2 == 0 ? "becuase" : "because");
        double perWord = clock.Elapsed.TotalMilliseconds / 101;
        Console.WriteLine($"  spell languages: {string.Join(", ", SpellCheck.Languages)}; 'loosing' valid: {loosingValid}; " +
                          $"'stoer' guesses: {string.Join(", ", SpellCheck.Guesses("stoer"))}; {perWord:0.00} ms per word");
        if (SpellCheck.Languages.Count > 0)
        {
            Check(SpellCheck.IsValidWord("because") && !SpellCheck.IsValidWord("becuase"), "spell checker knows because, not becuase");
            Check(perWord < 5, "spell check is fast enough for the keyboard hook");
        }

        if (engine is null) return;
        Console.WriteLine();
        Console.WriteLine("== Model picks (log-probabilities; first entry is what was typed)");
        (string Context, string[] Candidates)[] picks =
        [
            ("I worked hard all year to", ["achive", "achieve", "archive"]),
            ("Please remember to", ["achive", "achieve", "archive"]),
            ("I keep", ["loosing", "losing"]),
            ("The knot kept", ["loosing", "losing"]),
            ("I need to go to the", ["stoer", "store", "stoner"]),
            ("My friend", ["Jaxon", "Jason", "Saxon"]),
            ("Can you send me the", ["recipt", "receipt", "recipe"]),
        ];
        foreach (var (context, candidates) in picks)
        {
            clock.Restart();
            var prompt = PromptBuilder.ScoringPrompt(context + " ", "Messages", null, null);
            var scores = engine.Score(prompt.Text, candidates.Select(c => prompt.WordPrefix + c).ToList()) ?? [];
            var shown = candidates.Zip(scores).Select((pair, index) =>
            {
                double cost = index == 0 ? 0 : 2.0 * Typos.TypoCost(candidates[0], pair.First);
                return $"{pair.First} {pair.Second:0.0}->{pair.Second - cost:0.0}";
            });
            Console.WriteLine($"[{clock.ElapsedMilliseconds} ms] {Quote(context)} -> {string.Join(", ", shown)}");
        }
    }

    private static string Describe(AutocorrectMatch? match) => match switch
    {
        AutocorrectMatch.Fix f => $"fix({Quote(f.Replacement)}, {f.Length})",
        AutocorrectMatch.Ambiguous a => $"ambiguous({Quote(a.Word)}: {string.Join(", ", a.Candidates)})",
        _ => "nil",
    };

    /// `--calibrate [--model path.gguf] [--cpu]`: replays real-looking
    /// messages word by word (and mid-word) and measures, for each confidence
    /// threshold, how often a suggestion is shown and how often its first word
    /// is what came next, how often half-typed words are finished right, and
    /// per-keystroke latency as the app runs it. A port of `calibrate` in
    /// SelfTest.swift, with the same corpus.
    public static int Calibrate(string[] args)
    {
        var settings = AppSettings.Load();
        string? path = Argument(args, "--model") ?? new ModelManager().ActiveModelPath(settings.SelectedModelId, settings.CustomModelPath);
        using var engine = new LlamaEngine();
        try
        {
            if (path is null) throw new EngineException("No model");
            engine.Load(path, useGpu: !args.Contains("--cpu"));
        }
        catch (EngineException e)
        {
            Console.WriteLine(e.Message);
            return 1;
        }
        engine.WarmUp();
        Console.WriteLine($"model: {Path.GetFileName(path)} ({engine.ModelDescription}) on the {(engine.UsesGpu ? "GPU" : "CPU")}");
        (string App, string Text)[] corpus =
        [
            ("Mail", "Hi Sarah, thanks for getting back to me so quickly. I'm running a few minutes late, so please start the meeting without me. I will join as soon as I can and catch up on the notes afterwards."),
            ("Mail", "Dear hiring manager, I am writing to express my interest in the software engineer position at your company. I have five years of experience building web applications and I would love the opportunity to talk with you."),
            ("Messages", "hey are you still coming to the party tonight? i was thinking we could get dinner first if you want. let me know what time works for you"),
            ("Slack", "Quick update on the release: the build is green and QA signed off this morning. I will tag the new version after lunch, so let me know if anything else needs to go in before then."),
            ("Messages", "thanks so much for helping me move today, i really appreciate it. let me know when you want to grab a beer and i will pay"),
            ("Notes", "Things to do this weekend: clean the garage, call mom about the trip, pick up groceries for the week and finish reading the book for book club."),
            ("Mail", "Thank you for your order. Your package has been shipped and should arrive within three to five business days. If you have any questions, please contact our support team."),
        ];
        float[] thresholds = [0, 0.05f, 0.08f, 0.12f, 0.16f, 0.2f, 0.25f, 0.3f, 0.4f, 0.5f];
        var shown = new int[thresholds.Length];
        var firstRight = new int[thresholds.Length];
        var wordsRight = new int[thresholds.Length];
        var wordsShown = new int[thresholds.Length];
        int positions = 0;

        static List<string> Words(string s) =>
            s.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(TextUtil.TrimPunctuation).ToList();
        static string Join(IEnumerable<string> words) => string.Join(" ", words);

        foreach (var (app, text) in corpus)
        {
            var allWords = text.Split(' ');
            for (int i = 2; i < allWords.Length; i++)
            {
                // At a word boundary, and (for longer words) two letters in.
                var cases = new List<(string Before, string Truth)> { (Join(allWords[..i]) + " ", Join(allWords[i..])) };
                if (allWords[i].Length >= 5)
                {
                    cases.Add((Join(allWords[..i]) + " " + allWords[i][..2], allWords[i][2..] + " " + Join(allWords[(i + 1)..])));
                }
                foreach (var (before, truth) in cases)
                {
                    positions++;
                    var prompt = PromptBuilder.Build(before, app, null);
                    var output = engine.Complete(prompt.Text, 20, prompt.HealLastToken, () => true, t => PromptBuilder.ShouldStop(t, 7));
                    if (output is null) continue;
                    bool midWord = !before.EndsWith(' ');
                    var truthWords = Words(truth);
                    for (int t = 0; t < thresholds.Length; t++)
                    {
                        var confident = PromptBuilder.ConfidentText(output.Tokens, output.HitTokenLimit, thresholds[t]);
                        if (PromptBuilder.Clean(confident.Text, prompt, 7, confident.HitTokenLimit) is not string suggestion) continue;
                        // Mid-word, the first suggested "word" finishes the typed one.
                        var suggested = Words(midWord ? suggestion.TrimStart() : suggestion);
                        if (suggested.Count == 0) continue;
                        shown[t]++;
                        wordsShown[t] += suggested.Count;
                        if (suggested[0] == truthWords.FirstOrDefault()) firstRight[t]++;
                        wordsRight[t] += suggested.Zip(truthWords).TakeWhile(p => p.First == p.Second).Count();
                    }
                }
            }
        }
        Console.WriteLine($"positions: {positions}");
        Console.WriteLine("threshold  shown   first word right   correct words / shown words");
        for (int t = 0; t < thresholds.Length; t++)
        {
            double coverage = shown[t] * 100.0 / positions;
            double precision = shown[t] > 0 ? firstRight[t] * 100.0 / shown[t] : 0;
            double wordPrecision = wordsShown[t] > 0 ? wordsRight[t] * 100.0 / wordsShown[t] : 0;
            Console.WriteLine($"  {thresholds[t]:0.00}     {coverage,5:0.0}%   {precision,5:0.0}%             {wordPrecision,5:0.0}%");
        }

        // Finishing a half-typed word: 1-3 letters into longer words, scored
        // on whether the completed word shows up and is the right word.
        var wordCases = new List<(PromptBuilder.Prompt Prompt, string Truth, LlamaEngine.Output Output)>();
        foreach (var (app, text) in corpus)
        {
            var allWords = text.Split(' ');
            for (int i = 2; i < allWords.Length; i++)
            {
                var target = TextUtil.TrimPunctuation(allWords[i]).ToLowerInvariant();
                for (int typedCount = 1; typedCount <= 3; typedCount++)
                {
                    if (target.Length < typedCount + 3 || !target.All(char.IsLetter)) continue;
                    var before = Join(allWords[..i]) + " " + allWords[i][..typedCount];
                    var prompt = PromptBuilder.Build(before, app, null);
                    if (!prompt.HealLastToken) continue;
                    var output = engine.Complete(prompt.Text, 20, true, () => true, t => PromptBuilder.ShouldStop(t, 7));
                    if (output is not null) wordCases.Add((prompt, target[typedCount..], output));
                }
            }
        }
        Console.WriteLine($"\nFinishing the current word ({wordCases.Count} half-typed words)");
        Console.WriteLine("level      word threshold  completion shown  right word");
        foreach (var level in Enum.GetValues<ConfidenceLevel>())
        {
            foreach (var wordThreshold in new[] { level.MinProbability(), 0.15f, 0.1f, 0.06f, 0.04f, 0.02f }.Where(w => w <= level.MinProbability()))
            {
                int shownWords = 0, rightWords = 0;
                foreach (var item in wordCases)
                {
                    var confident = PromptBuilder.ConfidentText(item.Output.Tokens, item.Output.HitTokenLimit, level.MinProbability(), wordThreshold);
                    if (PromptBuilder.Clean(confident.Text, item.Prompt, 7, confident.HitTokenLimit) is not string suggestion) continue;
                    var completion = Words(suggestion).FirstOrDefault();
                    if (completion is null) continue;
                    shownWords++;
                    if (completion == item.Truth) rightWords++;
                }
                Console.WriteLine($"{level.ToString().ToLowerInvariant(),-10} {wordThreshold:0.00}            {shownWords * 100.0 / wordCases.Count,5:0.0}%            {(shownWords > 0 ? rightWords * 100.0 / shownWords : 0),5:0.0}%");
            }
        }

        // Latency as the app runs it: medium length, early stop at each level.
        // "first word" is when the first complete word exists, which is the
        // earliest a streaming display can show something.
        Console.WriteLine("\nLatency per keystroke (medium length, app settings)");
        Console.WriteLine("level      tokens/keystroke  total median  total p90  first word median  first word p90");
        foreach (var level in Enum.GetValues<ConfidenceLevel>())
        {
            var totals = new List<double>();
            var firsts = new List<double>();
            var tokenCounts = new List<int>();
            foreach (var (app, text) in corpus)
            {
                var allWords = text.Split(' ');
                for (int i = 2; i < allWords.Length; i++)
                {
                    var befores = new List<string> { Join(allWords[..i]) + " " };
                    if (allWords[i].Length >= 5) befores.Add(Join(allWords[..i]) + " " + allWords[i][..2]);
                    foreach (var before in befores)
                    {
                        var prompt = PromptBuilder.Build(before, app, null);
                        var clock = Stopwatch.StartNew();
                        double? firstWord = null;
                        var output = engine.Complete(prompt.Text, CompletionLength.Medium.MaxTokens(), prompt.HealLastToken, () => true, t =>
                        {
                            if (firstWord is null && PromptBuilder.FirstCompleteWord(t, prompt) is not null) firstWord = clock.Elapsed.TotalMilliseconds;
                            return PromptBuilder.ShouldStop(t, CompletionLength.Medium.MaxWords());
                        }, level.MinProbability(), level.WordMinProbability());
                        double total = clock.Elapsed.TotalMilliseconds;
                        if (output is null) continue;
                        totals.Add(total);
                        firsts.Add(firstWord ?? total);
                        tokenCounts.Add(output.Tokens.Count);
                    }
                }
            }
            totals.Sort();
            firsts.Sort();
            Console.WriteLine($"{level.ToString().ToLowerInvariant(),-10} {tokenCounts.Average(),5:0.0}             {totals[totals.Count / 2],6:0.0} ms     {totals[totals.Count * 9 / 10],6:0.0} ms   " +
                              $"{firsts[firsts.Count / 2],6:0.0} ms          {firsts[firsts.Count * 9 / 10],6:0.0} ms");
        }
        engine.Unload();
        return 0;
    }

    public static int RunOffline(string[] args)
    {
        AutocorrectChecks(null, args);
        PersonalizationChecks(null);
        OfflineChecks();
        return failures == 0 ? 0 : 1;
    }

    /// Learning, profile updates, lock, history, encryption and samples,
    /// against a throwaway store (never the user's data). A port of
    /// `personalizationChecks` in SelfTest.swift.
    private static void PersonalizationChecks(LlamaEngine? engine)
    {
        Console.WriteLine();
        Console.WriteLine("== Personalization");
        var folder = Path.Combine(Path.GetTempPath(), $"opentypist-selftest-{Guid.NewGuid():N}");
        try
        {
            var store = new SecureStore(folder);
            var settings = new AppSettings { KeepWritingSamples = true };
            var personal = new Personalization(settings, store);
            bool spellChecker = SpellCheck.Languages.Count > 0;

            for (int i = 0; i < 3; i++)
            {
                personal.ObserveWord("Sarah", false, "outlook.exe", "Outlook");
                personal.ObserveWord("Proxmox", false, "outlook.exe", "Outlook");
                personal.ObserveWord("homelab", false, "outlook.exe", "Outlook");
                personal.ObserveLine("Hi Sarah,");
                personal.ObserveLine("Cheers,");
            }
            foreach (var word in new[] { "colour", "organise", "favourite", "realised", "travelled", "color" }) personal.ObserveWord(word, false, null, "");
            for (int i = 0; i < 16; i++) personal.ObserveWord("lol", true, "whatsapp.exe", "WhatsApp");
            foreach (var (topic, line) in personal.GeneratedLines()) Console.WriteLine($"  [{topic}] {line}");

            Console.WriteLine($"  auto update: {personal.UpdateProfile() ?? "no change"}");
            var text = personal.Profile.Text;
            Check(text.StartsWith(Personalization.DefaultProfile, StringComparison.Ordinal) && text.Contains("Proxmox") && text.Contains("British spelling")
                  && text.Contains("\"Hi\"") && text.Contains("\"Cheers,\"") && text.Contains("In WhatsApp I write casually"), "learned lines go into the profile");
            Check(!personal.IsNeverCorrect("sarah"), "names aren't never-correct words");
            if (spellChecker) Check(personal.IsNeverCorrect("homelab") && personal.LearnedWords.Contains("homelab"), "learned words are never corrected");

            // The user rewrites the names line: that topic is theirs from now on.
            var namesLine = text.Split('\n').FirstOrDefault(l => l.StartsWith("People and things I often mention:", StringComparison.Ordinal)) ?? "";
            personal.SaveProfileText(text.Replace(namesLine, "I work with Sarah on our Proxmox cluster."));
            for (int i = 0; i < 3; i++) personal.ObserveWord("Jaxon", false, null, "");
            Console.WriteLine($"  after edit + new name: {personal.UpdateProfile() ?? "no change"}");
            text = personal.Profile.Text;
            Check(text.Contains("I work with Sarah") && !text.Contains("Jaxon") && personal.Profile.UserOwnedTopics.Contains("names"), "an edited line is the user's");

            personal.SetLocked(true);
            for (int i = 0; i < 20; i++) personal.ObserveWord("color", false, null, "");
            Check(personal.UpdateProfile() is null && !personal.Profile.Text.Contains("American"), "a locked profile stays as it is");
            personal.SetLocked(false);
            personal.SetMode(ProfileUpdateMode.AskFirst);
            Console.WriteLine($"  ask-first proposal: {personal.UpdateProfile() ?? "none"}");
            Check(personal.Profile.Proposal is { } proposal && proposal.Contains("American spelling") && !personal.Profile.Text.Contains("American"), "ask first proposes");
            personal.ApplyProposal();
            Check(personal.Profile.Text.Contains("American spelling") && personal.Profile.Proposal is null, "applying a proposal");
            personal.AddNeverCorrect("Teh");
            Check(personal.IsNeverCorrect("teh") && personal.NeverCorrectWords.Contains("teh"), "never correct");
            var first = personal.Profile.History[0];
            personal.Restore(first);
            Check(personal.Profile.Text == first.Text && personal.Profile.History[^1].Source == ProfileVersion.Origin.Restored, "restoring a version");
            Console.WriteLine($"  history: {string.Join(" | ", personal.Profile.History.Select(v => v.Note))}");

            personal.Flush();
            var reloaded = new Personalization(settings, store);
            static string Json(object o) => System.Text.Json.JsonSerializer.Serialize(o);
            Check(Json(reloaded.Profile) == Json(personal.Profile) && Json(reloaded.Learned) == Json(personal.Learned), "profile and learned data load again");
            var raw = File.ReadAllBytes(Path.Combine(folder, "profile.bin"));
            Check(!System.Text.Encoding.UTF8.GetString(raw).Contains("Proxmox"), "profile is encrypted");
            Console.WriteLine($"  encrypted round-trip OK ({raw.Length} bytes on disk)");

            // Phase 2 kept never-correct words in a file of their own.
            store.Save("never-correct.bin", new List<string> { "Gonna" });
            var migrated = new Personalization(settings, store);
            Check(migrated.IsNeverCorrect("gonna") && migrated.IsNeverCorrect("teh") && !File.Exists(Path.Combine(folder, "never-correct.bin")), "never-correct words move into the learned data");
            migrated.Flush();
            personal.ForgetLearned();
            personal.Flush();
            Check(personal.LearnedNames.Count == 0 && personal.IsNeverCorrect("teh"), "forgetting keeps never-correct words");

            // A long unbroken token stands in for an API key (a realistic-looking
            // fake key would trip secret scanners).
            var cleaned = WritingSamples.Clean("My card is 4111 1111 1111 1111 and the token is abcdefghij0123456789abcdefghij0123, thanks so much for helping") ?? "";
            Console.WriteLine($"  clean: {cleaned}");
            Check(!cleaned.Contains("4111") && !cleaned.Contains("abcdefghij0123"), "samples drop card numbers and tokens");
            Check(WritingSamples.Clean("too short") is null && WritingSamples.Clean("{\"a\": 1, \"b\": [2, 3, 4], \"c\": \"<x/>\", \"d\": 5}") is null, "short or code-like text isn't kept");

            // Samples are taken when the field is left or cleared (message sent).
            var samples = new WritingSamples(settings, store);
            FocusContext Field(string typed, string field = "1.2.3", string title = "Sam") =>
                new(42, "whatsapp.exe", "WhatsApp", title, "Edit", typed, "", null, new TextStyle(), null, false, FieldId: field);
            samples.Observe(Field("haha yeah that works"));
            samples.Observe(Field("haha yeah that works for me, see you at 7 then! bring snacks"));
            samples.Observe(Field(""));
            Check(samples.Samples.Count == 1 && samples.Samples[0].Text.EndsWith("bring snacks", StringComparison.Ordinal), "a sample when a message is sent");
            samples.Observe(Field("omg yes!! bring the dog too, she'll love it, and the frisbee"));
            samples.Observe(Field("x", field: "9.9"));
            Check(samples.Samples.Count == 2, "a sample when leaving the field");
            samples.Observe(Field("ok sounds good, i'll grab snacks on the way over, see you soon"));
            samples.Flush();
            var examples = samples.Examples("whatsapp.exe", "Sam");
            var header = PromptBuilder.Header("WhatsApp", "Sam", null, "I'm Alex.", examples);
            Check(examples.Count == 3 && header.StartsWith("About me:\nI'm Alex.\n\nExamples of things I've written in WhatsApp:\n\"", StringComparison.Ordinal)
                  && header.EndsWith("Application: WhatsApp\nWindow: Sam\nText being written:\n", StringComparison.Ordinal), "samples go into the prompt after the profile");
            Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(folder, "samples.bin"))).Contains("snacks"), "samples are encrypted");
            Check(new WritingSamples(settings, store).Samples.Count == 3, "samples load again");
            settings.KeepWritingSamples = false;
            samples.Observe(Field("this should not be kept because samples are off now, really"));
            samples.Flush();
            Check(samples.Samples.Count == 3 && samples.Examples("whatsapp.exe", "Sam").Count == 0, "samples off: nothing kept or used");
            samples.DeleteAll();
            Check(samples.Samples.Count == 0 && !File.Exists(Path.Combine(folder, "samples.bin")), "deleting all samples");
            Console.WriteLine($"  samples: {examples.Count} used in the prompt, {header.Length} characters of header");

            // Does the profile change what the model suggests?
            var letter = "Hi Sarah,\n\nThanks again for your help with the move today, I really appreciate it.\n\nCheers,\n";
            foreach (var profile in new[] { null, "I'm Alex Chen, a nurse in Toronto. I sign my emails \"Cheers, Alex\"." })
            {
                var prompt = PromptBuilder.Build(letter, "Outlook", null, profile: profile);
                if (profile is not null) Check(prompt.Text.StartsWith($"About me:\n{profile}", StringComparison.Ordinal), "profile at the top of the prompt");
                if (engine is null) continue;
                var output = engine.Complete(prompt.Text, 6, false, () => true, t => t.Contains('\n'));
                Console.WriteLine($"  profile {(profile is null ? "off" : "on ")}: \"...Cheers,\\n\" -> {Quote(output?.Text)}");
            }
            if (engine is not null)
            {
                foreach (bool chat in new[] { false, true })
                {
                    var prompt = PromptBuilder.Build("hey are you free tonight? i was thinking we could", "WhatsApp", "Sam", profile: "I'm Alex.", examples: chat ? examples : []);
                    var output = engine.Complete(prompt.Text, 12, prompt.HealLastToken, () => true, t => PromptBuilder.ShouldStop(t, 7));
                    var suggestion = output is null ? null : PromptBuilder.Clean(output.Text, prompt, 7, output.HitTokenLimit);
                    Console.WriteLine($"  samples {(chat ? "on " : "off")}: \"...i was thinking we could\" -> {Quote(suggestion)}");
                }
            }
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    /// `--download <id>`: the same download + SHA-256 check the Model tab uses.
    public static int Download(string[] args)
    {
        var id = Argument(args, "--download");
        var model = ModelCatalog.Model(id);
        if (model is null)
        {
            Console.WriteLine($"Unknown model. Choose one of: {string.Join(", ", ModelCatalog.All.Select(m => m.Id))}");
            return 1;
        }
        var models = new ModelManager();
        // The catalog's current file; an earlier format on disk (Gemma 4 E2B
        // Q4_K_M) doesn't count here, so this also fetches the new one.
        if (File.Exists(models.DownloadPathFor(model)))
        {
            Console.WriteLine($"{model.Name} is already downloaded: {models.DownloadPathFor(model)}");
            return 0;
        }
        int lastPercent = -1;
        models.DownloadChanged += () =>
        {
            var d = models.Download;
            if (d.Phase == ModelManager.Phase.Downloading && d.Percent / 5 != lastPercent / 5)
            {
                lastPercent = d.Percent;
                Console.WriteLine($"  {d.Percent}% ({d.Received / 1e6:0} of {d.Total / 1e6:0} MB)");
            }
            else if (d.Phase == ModelManager.Phase.Verifying) Console.WriteLine("  checking SHA-256...");
            else if (d.Phase == ModelManager.Phase.Failed) Console.WriteLine($"  failed: {d.Message}");
        };
        Console.WriteLine($"Downloading {model.Name} ({model.SizeText}) from {model.Url}");
        models.StartDownload(model).Wait();
        if (!File.Exists(models.DownloadPathFor(model))) return 1;
        Console.WriteLine($"Done: {models.DownloadPathFor(model)} (choose it in Settings > Model, or pass --model to --selftest)");
        return 0;
    }

    /// `--update-test <folder> [--zip <OpenTypist-x.y.z-win-x64.zip>]`: runs
    /// the real updater (download, checksum, unpack, version check, folder
    /// swap) against the latest GitHub release, or a local zip with its
    /// `.sha256` beside it, but swaps a throwaway copy of this app inside
    /// <folder> instead of the installed one.
    public static int UpdateTest(string[] args)
    {
        var folder = Argument(args, "--update-test");
        if (folder is null || folder.StartsWith("--", StringComparison.Ordinal))
        {
            Console.WriteLine("Usage: OpenTypist.exe --update-test <folder> [--zip <OpenTypist-x.y.z-win-x64.zip>]");
            return 1;
        }
        var target = Path.Combine(Path.GetFullPath(folder), "OpenTypist");
        var work = Path.Combine(Path.GetTempPath(), $"OpenTypistUpdate-{Guid.NewGuid():N}");
        try
        {
            Console.WriteLine($"Copying this app ({Updater.CurrentVersion}) to {target}");
            if (Directory.Exists(target)) Directory.Delete(target, true);
            CopyTree(Updater.AppFolder, target);

            string newApp, version;
            var zip = Argument(args, "--zip");
            if (zip is not null)
            {
                var name = Path.GetFileName(zip);
                var match = System.Text.RegularExpressions.Regex.Match(name, @"^OpenTypist-(\d+(?:\.\d+)*)-win-x64\.zip$");
                if (!match.Success)
                {
                    Console.WriteLine($"FAILED: {name} isn't named like a release ({Versions.WindowsZip("x.y.z")})");
                    return 1;
                }
                version = match.Groups[1].Value;
                Directory.CreateDirectory(work);
                Console.WriteLine($"Checking {name} against {name}.sha256 and unpacking");
                newApp = Updater.Unpack(zip, File.ReadAllText(zip + ".sha256"), version, work);
            }
            else
            {
                Console.WriteLine($"Asking GitHub for the latest release of {Updater.Repository}");
                var release = Updater.FetchLatest().GetAwaiter().GetResult();
                if (release is null)
                {
                    Console.WriteLine("The latest release has no Windows build yet, so there is nothing to install. (Pass --zip to test a local build.)");
                    return 3;
                }
                version = release.Version;
                Console.WriteLine($"Downloading {release.Zip}");
                var progress = new Progress<string>(m => { if (m.EndsWith("0%", StringComparison.Ordinal)) Console.WriteLine($"  {m}"); });
                newApp = Updater.Prepare(release, progress, work).GetAwaiter().GetResult();
            }
            Console.WriteLine($"Swapping in {version}");
            int code = Updater.FinishUpdate(["--finish-update", "0", target], relaunch: false, source: newApp);
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(target, "OpenTypist.exe"));
            bool ok = code == 0 && (info.ProductVersion ?? "").Split('+')[0] == version && Directory.Exists(target + ".old");
            Console.WriteLine(ok
                ? $"Update test passed: {target} now holds OpenTypist {info.ProductVersion}, the old copy was moved to {Path.GetFileName(target)}.old"
                : $"FAILED: exit code {code}, {target} holds {info.ProductVersion}");
            return ok ? 0 : 1;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or System.Net.Http.HttpRequestException or UnauthorizedAccessException)
        {
            Console.WriteLine($"FAILED: {e.Message}");
            return 1;
        }
        finally
        {
            foreach (var path in new[] { work, target + ".old" })
            {
                try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (IOException) { }
            }
        }
    }

    /// `--snapshot-settings <out.png> [--tab Personal]`, with
    /// OPENTYPIST_TEST_STORE set: renders a whole Settings tab with sample
    /// data, for checking the layout. Like `--snapshot-personal` on the Mac.
    public static int SnapshotSettings(string[] args)
    {
        var output = Argument(args, "--snapshot-settings");
        if (Environment.GetEnvironmentVariable("OPENTYPIST_TEST_STORE") is not { Length: > 0 } || output is null)
        {
            Console.WriteLine("Usage: set OPENTYPIST_TEST_STORE to an empty folder, then OpenTypist.exe --snapshot-settings out.png [--tab Personal|General|Stats|...] [--as-is]");
            return 1;
        }
        var tab = Argument(args, "--tab") ?? "Personal";
        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        var tray = new TrayApp(app);
        int code = 1;
        app.Startup += (_, _) =>
        {
            try
            {
                tray.StartPreview();
                // --as-is: what the test store holds, without sample data.
                if (!args.Contains("--as-is")) SeedPreview(tray);
                var window = new SettingsWindow(tray)
                {
                    WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                    Left = -20000,
                    Top = 0,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                };
                window.Show();
                window.SelectTab(tab);
                window.UpdateLayout();
                code = RenderTab(window, tab, Path.GetFullPath(output));
                window.Close();
            }
            finally
            {
                tray.Shutdown();
                app.Shutdown();
            }
        };
        app.Run();
        return code;
    }

    private static void SeedPreview(TrayApp tray)
    {
        var personal = tray.Personal;
        for (int i = 0; i < 4; i++)
        {
            personal.ObserveWord("Sarah", false, "outlook.exe", "Outlook");
            personal.ObserveWord("Proxmox", false, "outlook.exe", "Outlook");
            personal.ObserveWord("homelab", false, "outlook.exe", "Outlook");
            personal.ObserveLine("Hi Sarah,");
            personal.ObserveLine("Cheers,");
        }
        for (int i = 0; i < 16; i++) personal.ObserveWord("lol", true, "whatsapp.exe", "WhatsApp");
        personal.AddNeverCorrect("teh");
        personal.AddNeverCorrect("gonna");
        personal.SetMode(ProfileUpdateMode.AskFirst);
        personal.UpdateProfile();
        tray.Settings.KeepWritingSamples = true;
        tray.Samples.Record(new WritingSample(Guid.NewGuid(), DateTime.Now, "whatsapp.exe", "WhatsApp", "Sam", "haha yeah that works for me, see you at 7 then! bring snacks"));
        tray.Replacements?.Add("@@", "alex@example.com");
        tray.Replacements?.Add(";addr", "1 Main Street\nSpringfield");
        var days = new Dictionary<string, int>();
        var recent = tray.Stats.RecentDays(30);
        for (int offset = 0; offset < recent.Count; offset++) days[Stats.DayKey(recent[offset].Date)] = new[] { 0, 40, 85, 120, 60, 0, 30 }[offset % 7] + offset * 3;
        tray.Stats.SetDailyWordsForPreview(days);
        tray.Settings.AppAcceptKeys["slack.exe"] = new AcceptKeys(KeyCombo.RightArrow, KeyCombo.AltRightArrow);
    }

    /// Renders all of a tab, not only what fits in the window.
    private static int RenderTab(SettingsWindow window, string header, string output)
    {
        var item = window.Tabs.Items.OfType<System.Windows.Controls.TabItem>().FirstOrDefault(t => (string)t.Header == header);
        if (item is null)
        {
            Console.WriteLine($"No tab named {header}");
            return 1;
        }
        System.Windows.FrameworkElement content;
        if (item.Content is System.Windows.Controls.ScrollViewer viewer)
        {
            content = (System.Windows.FrameworkElement)viewer.Content;
            viewer.Content = null;
        }
        else
        {
            content = (System.Windows.FrameworkElement)item.Content;
            item.Content = null;
        }
        const double width = 680;
        var host = new System.Windows.Controls.Border
        {
            Background = System.Windows.SystemColors.WindowBrush,
            Child = content,
            Width = width,
        };
        // The window's font, which the detached content no longer inherits.
        System.Windows.Documents.TextElement.SetFontFamily(host, window.FontFamily);
        System.Windows.Documents.TextElement.SetFontSize(host, window.FontSize);
        host.Measure(new System.Windows.Size(width, double.PositiveInfinity));
        host.Arrange(new System.Windows.Rect(host.DesiredSize));
        host.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth), (int)Math.Ceiling(host.ActualHeight), 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(host);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var file = File.Create(output)) encoder.Save(file);
        Console.WriteLine($"Saved {output} ({bitmap.PixelWidth}x{bitmap.PixelHeight})");
        return 0;
    }

    private static void CopyTree(string source, string target)
    {
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        }
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
        }
    }

    private static void OfflineChecks()
    {
        Console.WriteLine();
        Console.WriteLine("== Logic checks");

        // Confidence cut-off: stop at the first unsure token, and drop a
        // half-finished word when the unsure token would have continued it.
        static GeneratedToken T(string text, float p, bool finishes = false) => GeneratedToken.Of(text, p, finishes);
        var tokens = new List<GeneratedToken> { T(" I", 0.9f), T("'ll", 0.8f), T(" be", 0.7f), T(" th", 0.6f), T("ere", 0.1f) };
        Check(PromptBuilder.ConfidentText(tokens, true, 0.05f).Text == " I'll be there", "confident 0.05");
        Check(PromptBuilder.ConfidentText(tokens, true, 0.25f).Text == " I'll be", "confident 0.25");
        Check(PromptBuilder.ConfidentText([.. tokens, T(" soon", 0.05f)], false, 0.08f).Text == " I'll be there", "confident 0.08");
        Check(PromptBuilder.ConfidentText([T("ing", 0.9f), T(" late", 0.1f)], false, 0.25f).Text == "ing", "confident ing");
        Check(PromptBuilder.ConfidentText([T(" maybe", 0.1f)], false, 0.25f).Text.Length == 0, "confident empty");

        // Finishing a half-typed word uses its own, looser bar.
        var finishing = new List<GeneratedToken> { T("terested", 0.3f, true), T(" in", 0.2f), T(" the", 0.9f) };
        Check(PromptBuilder.ConfidentText(finishing, false, 0.45f).Text.Length == 0, "finishing strict");
        Check(PromptBuilder.ConfidentText(finishing, false, 0.45f, 0.15f).Text == "terested", "finishing loose");
        Check(PromptBuilder.ConfidentText(finishing, false, 0.15f, 0.1f).Text == "terested in the", "finishing all");

        // Streaming shows complete words only, cleaned like a final suggestion.
        var afterSpace = PromptBuilder.Build("so ", null, null);
        Check(PromptBuilder.StreamingText(" I'll be th", afterSpace, 7, "") == "I'll be", "stream drops the growing word");
        Check(PromptBuilder.StreamingText(" I'll", afterSpace, 7, "") is null, "stream waits for the first word");
        Check(PromptBuilder.StreamingText(" I'll be there.", afterSpace, 7, "") == "I'll be there.", "stream ends at punctuation");
        var inWord = PromptBuilder.Build("very in", null, null);
        Check(PromptBuilder.StreamingText("terested in th", inWord, 7, "") == "terested in", "stream mid-word");
        Check(PromptBuilder.StreamingText("terested in this", inWord, 7, " this job") is null
              || PromptBuilder.StreamingText("terested in this job ", inWord, 7, " this job") == "terested in", "stream mid-line");
        Check(PromptBuilder.FirstCompleteWord(" I'll be", afterSpace) == "I'll" && PromptBuilder.FirstCompleteWord(" I'l", afterSpace) is null, "first complete word");

        // Gemma 4 E2B moved to Q4_0; the earlier Q4_K_M file still counts.
        var gemma = ModelCatalog.Model("gemma-4-e2b")!;
        Check(gemma.FileName.Contains("Q4_0") && gemma.FileNames.Contains("gemma-4-E2B.i1-Q4_K_M.gguf"), "E2B Q4_0 with Q4_K_M as legacy");
        var legacyDir = Path.Combine(Path.GetTempPath(), $"opentypist-selftest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(legacyDir);
        try
        {
            var manager = new ModelManager(legacyDir);
            Check(!manager.IsDownloaded(gemma), "nothing downloaded yet");
            File.WriteAllText(Path.Combine(legacyDir, "gemma-4-E2B.i1-Q4_K_M.gguf"), "");
            Check(manager.IsDownloaded(gemma) && manager.FileFor(gemma).EndsWith("Q4_K_M.gguf")
                  && manager.DownloadPathFor(gemma).EndsWith("Q4_0.gguf"), "legacy file loads, new downloads use Q4_0");
            File.WriteAllText(Path.Combine(legacyDir, gemma.FileName), "");
            Check(manager.FileFor(gemma).EndsWith("Q4_0.gguf"), "the current file wins over the legacy one");
            manager.Delete(gemma);
            Check(!manager.IsDownloaded(gemma), "delete removes both files");
        }
        finally { Directory.Delete(legacyDir, true); }

        // Mid-line: lead into the text after the caret, don't repeat it.
        Check(PromptBuilder.FitBefore(" this job.", "terested in this job") == "terested in", "fit this job");
        Check(PromptBuilder.FitBefore(" tomorrow", " see you tomorrow") == " see you", "fit tomorrow");
        Check(PromptBuilder.FitBefore(", thanks", " soon.") == " soon", "fit punctuation");
        Check(PromptBuilder.FitBefore(" the report", " the report") is null, "fit nothing left");
        Console.WriteLine($"Mid-line fit: \"terested in this job\" before \" this job.\" -> {Quote(PromptBuilder.FitBefore(" this job.", "terested in this job"))}");

        // Prompt: trailing space, token healing, header order, profile.
        var spaced = PromptBuilder.Build("I'm running late, so ", "Notepad", "notes.txt - Notepad");
        Check(spaced.StrippedTrailingSpace && !spaced.HealLastToken && spaced.Text.EndsWith("late, so"), "prompt trailing space");
        var midWord = PromptBuilder.Build("I am very in", null, null);
        Check(midWord.HealLastToken && !midWord.StrippedTrailingSpace && midWord.Text == "I am very in", "prompt healing");
        Check(PromptBuilder.Clean(" be there soon", spaced, 7, false) == "be there soon", "clean drops the space");
        Check(PromptBuilder.Clean("be there", spaced, 7, false) is null, "clean needs a space");
        Check(PromptBuilder.Clean(" one two three four", spaced, 3, true) == "one two three", "clean word limit");
        var header = PromptBuilder.Header("Messages", "Sam", null, "I'm Alex.", ["see you at 7!", "omg yes"]);
        Check(header == "About me:\nI'm Alex.\n\nExamples of things I've written in Messages:\n\"see you at 7!\"\n\"omg yes\"\n\nApplication: Messages\nWindow: Sam\nText being written:\n", "header order");
        var profiled = PromptBuilder.Build("Cheers,\n", "Mail", null, profile: "I sign emails 'Cheers, Alex'.");
        Check(profiled.Text.StartsWith("About me:\nI sign emails 'Cheers, Alex'.\n\nApplication: Mail\n"), "profile reaches the prompt");
        var longText = new string('x', 50) + " " + string.Concat(Enumerable.Repeat("word ", 300)) + "end";
        Check(PromptBuilder.Build(longText, null, null).Text.StartsWith("word "), "prompt starts at a word boundary");
        Check(TextUtil.NormalizeNewlines("a\r\nb\rc") == "a\nb\nc", "newlines");

        // Accept keys.
        KeyEvent Key(int vk, bool shift = false, bool ctrl = false, bool alt = false) => new(vk, 0, shift, ctrl, alt, false, false, "");
        Check(KeyCombo.Tab.Matches(Key(VK_TAB)) && !KeyCombo.ShiftTab.Matches(Key(VK_TAB)), "tab");
        Check(KeyCombo.ShiftTab.Matches(Key(VK_TAB, shift: true)) && !KeyCombo.Tab.Matches(Key(VK_TAB, shift: true)), "shift+tab");
        Check(!KeyCombo.Tab.Matches(Key(VK_TAB, ctrl: true)), "ctrl+tab is not tab");
        Check(KeyCombo.AltRightArrow.Matches(Key(0x27, alt: true)) && !KeyCombo.RightArrow.Matches(Key(0x27, alt: true)), "alt+right");
        Check(new KeyCombo(0x41, Character: "A").Problem is not null && new KeyCombo(VK_SPACE).Problem is not null, "letters and space refused");
        Check(new KeyCombo(0x41, Ctrl: true, Character: "A").Problem is null && KeyCombo.RightArrow.Problem is null, "ctrl+letter and arrows allowed");
        Check(KeyCombo.Tab.HintLabel == "tab" && KeyCombo.AltRightArrow.DisplayName == "Alt+→", "key names");

        // Suggestion bookkeeping.
        var s = new Suggestion("so ", "I'll be there soon", 1);
        Check(Suggestion.NextWord(s.Text, true) == "I'll ", "next word");
        Check(s.Remaining("late, so I'll ") == "be there soon", "remaining after word");
        Check(s.Remaining("late, so I'll b") == "e there soon", "remaining mid word");
        Check(s.Remaining("late, so I'd") is null, "remaining after typo");

        // Model recommendations by RAM and graphics memory.
        foreach (var (memory, gpu) in new (int, long)[] { (8, 0), (16, 0), (32, 0), (16, 8192), (32, 12288), (32, 4096), (64, 24576), (128, 0) })
        {
            var fake = new HardwareInfo("Test CPU", memory, gpu > 0 ? "Test GPU" : null, gpu);
            Console.WriteLine($"  {memory} GB RAM, {(gpu > 0 ? $"{gpu / 1024} GB graphics card" : "no graphics card")} -> {ModelCatalog.Recommended(fake).Name}");
        }
        Check(ModelCatalog.Recommended(new HardwareInfo("", 32, "GPU", 12288)).Id == "gemma-4-e4b", "recommend 32 GB + 12 GB GPU");
        Check(ModelCatalog.Recommended(new HardwareInfo("", 32, null, 0)).Id == "gemma-4-e2b", "recommend CPU only");

        // Ghost text wraps like real text: 7 px per character here.
        static double Width(string t) => t.Length * 7;
        var wrapped = OverlayWindow.Wrap("I'll be there in about 10 minutes.", Width, 70, 120);
        Console.WriteLine($"Wrap (70 px, then 120 px lines): {string.Join(" | ", wrapped.Select(l => Quote(l)))}");
        Check(wrapped.Count > 1 && wrapped.Skip(1).All(l => l.Length > 0 && !char.IsWhiteSpace(l[0])), "continuation lines start with a word");
        Check(wrapped.All(l => Width(l) <= 120), "wrapped lines fit");
        var noRoom = OverlayWindow.Wrap(" be there soon", Width, 5, 300);
        Check(noRoom.Count == 2 && noRoom[0].Length == 0 && noRoom[1] == "be there soon", "no room after the caret: start on the next line");

        // Accept keys per app.
        var keySettings = new AppSettings();
        keySettings.AppAcceptKeys["slack.exe"] = new AcceptKeys(KeyCombo.RightArrow, KeyCombo.AltRightArrow);
        Check(keySettings.AcceptKeysFor("slack.exe").Word == KeyCombo.RightArrow && keySettings.AcceptKeysFor("notepad.exe") == AcceptKeys.Standard
              && keySettings.AcceptKeysFor(null) == AcceptKeys.Standard, "per-app accept keys");
        var json = System.Text.Json.JsonSerializer.Serialize(keySettings.AppAcceptKeys);
        var back = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, AcceptKeys>>(json)!;
        Check(back["slack.exe"] == keySettings.AppAcceptKeys["slack.exe"], "accept keys survive saving");
        Check(keySettings.IsDisabled(Path.GetFileName(Environment.ProcessPath ?? "OpenTypist.exe").ToLowerInvariant()), "never in OpenTypist's own windows");

        // Personal data is encrypted at rest (DPAPI), in a throwaway folder.
        var folder = Path.Combine(Path.GetTempPath(), $"opentypist-selftest-{Guid.NewGuid():N}");
        try
        {
            var store = new SecureStore(folder);
            var replacements = new TextReplacements(new AppSettings(), store);
            Check(replacements.Add("@@", "me@example.com") is null && replacements.Add("@@", "again") is not null
                  && replacements.Add("a b", "x") is not null && replacements.Add("sig.", "x") is not null, "snippet rules");
            Check(replacements.Match("mail @@") == new AutocorrectMatch.Fix("me@example.com", 2), "snippet matches");
            var raw = File.ReadAllBytes(Path.Combine(folder, "snippets.bin"));
            Check(!System.Text.Encoding.UTF8.GetString(raw).Contains("example.com"), "snippets are encrypted");
            Check(new TextReplacements(new AppSettings(), store).Snippets.Single().Expansion == "me@example.com", "snippets load again");
            File.WriteAllBytes(Path.Combine(folder, "snippets.bin"), [1, 2, 3]);
            Check(new TextReplacements(new AppSettings(), store).Snippets.Count == 0 && File.Exists(Path.Combine(folder, "snippets.bin.unreadable")),
                  "an unreadable file is set aside, not overwritten");
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }

        UpdaterChecks();
        PowerAndStatsChecks();
        ScreenContextChecks();

        Console.WriteLine(failures == 0 ? "\nAll checks passed" : $"\n{failures} check(s) FAILED");
    }

    private static void UpdaterChecks()
    {
        Console.WriteLine();
        Console.WriteLine($"== Updates (this is OpenTypist {Updater.CurrentVersion})");
        Check(Versions.IsNewer("0.10.0", "0.9.2") && Versions.IsNewer("0.2.0", "0.1.0") && Versions.IsNewer("v1.0", "0.9"), "newer versions");
        Check(!Versions.IsNewer("0.2.0", "0.2.0") && !Versions.IsNewer("0.1.9", "0.2") && !Versions.IsNewer("0.2", "0.2.0"), "not newer");
        Check(System.Text.RegularExpressions.Regex.IsMatch(Updater.CurrentVersion, @"^\d+(\.\d+)+$"), "the app knows its version");

        static System.Text.Json.JsonElement Release(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement;
        const string mac = """{"name": "OpenTypist-0.4.0.dmg", "browser_download_url": "https://example.com/mac.dmg"}""";
        const string zip = """{"name": "OpenTypist-0.4.0-win-x64.zip", "browser_download_url": "https://example.com/win.zip"}""";
        const string sum = """{"name": "OpenTypist-0.4.0-win-x64.zip.sha256", "browser_download_url": "https://example.com/win.zip.sha256"}""";
        var both = Updater.ParseRelease(Release($$"""{"tag_name": "v0.4.0", "draft": false, "prerelease": false, "body": "Notes", "html_url": "https://example.com/r", "assets": [{{mac}}, {{zip}}, {{sum}}]}"""));
        Check(both is { Version: "0.4.0", Zip: "https://example.com/win.zip", Checksum: "https://example.com/win.zip.sha256" }, "a release with a Windows build");
        Check(Updater.ParseRelease(Release($$"""{"tag_name": "v0.4.0", "draft": false, "prerelease": false, "assets": [{{mac}}]}""")) is null, "a release with only the Mac build (Windows still building)");
        Check(Updater.ParseRelease(Release($$"""{"tag_name": "v0.4.0", "draft": false, "prerelease": true, "assets": [{{mac}}, {{zip}}, {{sum}}]}""")) is null, "pre-releases are skipped");

        // Checksum, unpack and version check on a zip of this very exe.
        var work = Path.Combine(Path.GetTempPath(), $"OpenTypistUpdate-{Guid.NewGuid():N}");
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null || !exe.EndsWith("OpenTypist.exe", StringComparison.OrdinalIgnoreCase)) return;
            var source = Path.Combine(work, "src", "OpenTypist");
            Directory.CreateDirectory(source);
            File.Copy(exe, Path.Combine(source, "OpenTypist.exe"));
            var package = Path.Combine(work, Versions.WindowsZip(Updater.CurrentVersion));
            System.IO.Compression.ZipFile.CreateFromDirectory(Path.Combine(work, "src"), package);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(package))).ToLowerInvariant();
            var folder = Updater.Unpack(package, $"{hash}  {Path.GetFileName(package)}\n", Updater.CurrentVersion, Path.Combine(work, "a"));
            Check(File.Exists(Path.Combine(folder, "OpenTypist.exe")), "a verified zip unpacks");
            bool refused;
            try { Updater.Unpack(package, new string('0', 64), Updater.CurrentVersion, Path.Combine(work, "b")); refused = false; }
            catch (InvalidDataException) { refused = true; }
            Check(refused, "a zip that doesn't match its checksum is refused");
            try { Updater.Unpack(package, hash, "99.0.0", Path.Combine(work, "c")); refused = false; }
            catch (InvalidDataException) { refused = true; }
            Check(refused, "a zip with another version inside is refused");
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (IOException) { }
        }
    }

    private static void PowerAndStatsChecks()
    {
        Console.WriteLine();
        Console.WriteLine("== Memory, battery and stats");
        Check(PowerRules.Current(false, false, PowerBehavior.Pause, PowerBehavior.Pause) == PowerBehavior.Normal, "plugged in: as usual");
        Check(PowerRules.Current(true, false, PowerBehavior.SmallerModel, PowerBehavior.Pause) == PowerBehavior.SmallerModel, "on battery");
        Check(PowerRules.Current(true, true, PowerBehavior.SmallerModel, PowerBehavior.Pause) == PowerBehavior.Pause, "battery saver wins");
        Check(PowerRules.Current(true, true, PowerBehavior.SmallerModel, PowerBehavior.Normal) == PowerBehavior.SmallerModel, "battery saver set to normal: battery rule");
        var now = new DateTime(2026, 10, 24, 12, 0, 0);
        Check(PowerRules.ShouldUnloadForIdle(true, 15, now.AddMinutes(-16), now) && !PowerRules.ShouldUnloadForIdle(true, 15, now.AddMinutes(-14), now)
              && !PowerRules.ShouldUnloadForIdle(false, 15, now.AddHours(-5), now), "idle unload");

        var folder = Path.Combine(Path.GetTempPath(), $"opentypist-selftest-{Guid.NewGuid():N}");
        try
        {
            var clock = now;
            var path = Path.Combine(folder, "stats.json");
            var stats = new Stats(path, () => clock);
            stats.RecordShown();
            stats.RecordShown();
            stats.RecordAccepted("I'll ");
            stats.RecordAccepted("be there soon ");
            stats.RecordCorrection();
            Check(stats.WordsAccepted == 4 && stats.WordsAcceptedToday == 4 && stats.CharactersInserted == 19 && stats.KeystrokesSaved == 17
                  && stats.AcceptRate == 1 && stats.CorrectionsMade == 1, "stats counts");
            clock = now.AddDays(1);
            stats.RecordAccepted("ok ");
            var days = stats.RecentDays(30);
            Check(stats.WordsAcceptedToday == 1 && days.Count == 30 && days[^1].Words == 1 && days[^2].Words == 4 && days[^1].Date == clock.Date, "words per day");
            stats.Flush();
            var reloaded = new Stats(path, () => clock);
            Check(reloaded.WordsAccepted == 5 && reloaded.RecentDays(2)[0].Words == 4, "stats load again");
            Check(!File.ReadAllText(path).Contains("there"), "stats keep counts, never text");
            reloaded.Reset();
            Check(reloaded.WordsAccepted == 0 && reloaded.AcceptRate is null, "reset");
            reloaded.Flush();
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    private static void ScreenContextChecks()
    {
        Console.WriteLine();
        Console.WriteLine("== Screen context");
        var lines = new List<ScreenText.Line>
        {
            new("Inbox", 10, 20),
            new("From: Sarah", 200, 100),
            new("Can we move the meeting to Thursday?", 200, 130),
            new("Thanks, Sarah", 200, 160),
            new("Sure, Thursday works", 200, 400),
            new("Unrelated footer text far away", 10, 900),
        };
        var picked = ScreenText.Select(lines, 400, "Hi Sarah,\nSure, Thursday works", budget: 80);
        Console.WriteLine($"  picked: {Quote(picked)}");
        Check(!picked.Contains("Sure, Thursday works") && picked.Contains("Can we move the meeting") && !picked.Contains("footer"), "typed text dropped, nearest lines kept");
        Check(picked.IndexOf("From: Sarah", StringComparison.Ordinal) < picked.IndexOf("Thanks, Sarah", StringComparison.Ordinal), "reading order");
        // Mid-line, OCR sees the whole line (and reads the caret as a letter).
        var midLine = ScreenText.Select([.. lines, new("I am very inl this job.", 210, 430)], 430, "I am very in", field: new ScreenText.Area(190, 380, 800, 460));
        Check(!midLine.Contains("this job") && !midLine.Contains("Sure, Thursday") && midLine.Contains("Thursday?"), "the text field's own text is left out");
        Check(PromptBuilder.Header(null, null, picked).StartsWith("Visible on screen:\n", StringComparison.Ordinal), "screen text goes into the prompt");

        if (!ScreenContext.IsAvailable)
        {
            Console.WriteLine("  Windows text recognition isn't installed for this user's languages; OCR check skipped");
            return;
        }
        using var bitmap = new System.Drawing.Bitmap(900, 200);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.Clear(System.Drawing.Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var font = new System.Drawing.Font("Segoe UI", 20);
            g.DrawString("Can we move the meeting to Thursday?", font, System.Drawing.Brushes.Black, 20, 30);
            g.DrawString("Thanks, Sarah", font, System.Drawing.Brushes.Black, 20, 110);
        }
        var started = System.Diagnostics.Stopwatch.StartNew();
        var recognized = ScreenContext.RecognizeLines(bitmap).GetAwaiter().GetResult();
        Console.WriteLine($"  OCR ({started.ElapsedMilliseconds} ms): {string.Join(" | ", recognized.Select(l => $"{l.Text} @{l.Y:0}"))}");
        Check(recognized.Count == 2 && recognized[0].Text.Contains("Thursday") && recognized[1].Text.Contains("Sarah") && recognized[0].Y < recognized[1].Y, "OCR reads lines in order");
    }

    private static void Check(bool condition, string name)
    {
        if (condition) return;
        failures++;
        Console.WriteLine($"FAILED: {name}");
    }

    private static string? Suggest(LlamaEngine engine, string text, string? app, string? title, CompletionLength length, ConfidenceLevel? level = null,
                                   Action<string>? onPartial = null)
    {
        var request = new EngineService.Request
        {
            TextBefore = text,
            AppName = app,
            WindowTitle = title,
            Length = length,
            MinProbability = level?.MinProbability() ?? 0,
            WordMinProbability = level?.WordMinProbability(),
        };
        return EngineService.Run(engine, request, () => true, out _, onPartial);
    }

    private static string? Argument(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// Like Swift's debugDescription: quoted, with escapes, or nil.
    private static string Quote(string? s) =>
        s is null ? "nil" : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
}
