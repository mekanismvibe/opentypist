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

        // Does the profile reach the prompt and change what the model suggests?
        Console.WriteLine();
        Console.WriteLine("== Profile");
        const string letter = "Hi Sarah,\n\nThanks again for your help with the move today, I really appreciate it.\n\nCheers,\n";
        foreach (var profile in new[] { null, "I'm Alex Chen, a nurse in Toronto. I sign my emails \"Cheers, Alex\"." })
        {
            var prompt = PromptBuilder.Build(letter, "Mail", null, profile: profile);
            if (profile is not null) Check(prompt.Text.StartsWith($"About me:\n{profile}"), "profile missing from prompt");
            var output = engine.Complete(prompt.Text, 6, false, () => true, t => t.Contains('\n'));
            Console.WriteLine($"  profile {(profile is null ? "off" : "on ")}: \"...Cheers,\\n\" -> {Quote(output?.Text)}");
        }

        var hardware = Hardware.Current;
        Console.WriteLine();
        Console.WriteLine($"This PC: {hardware.Summary.Replace(" · ", ", ")} -> recommended {ModelCatalog.Recommended(hardware).Name}");

        OfflineChecks();
        // ggml's GPU backends can assert if the process exits with a model loaded.
        engine.Unload();
        return failures == 0 ? 0 : 1;
    }

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
        OfflineChecks();
        return failures == 0 ? 0 : 1;
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

        Console.WriteLine(failures == 0 ? "\nAll checks passed" : $"\n{failures} check(s) FAILED");
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
