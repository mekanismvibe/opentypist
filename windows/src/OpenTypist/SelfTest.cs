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
            var typed = new StringBuilder();
            Console.WriteLine(level is null ? "== Typing simulation" : $"== Typing simulation, {level.Value.Title()} filter");
            foreach (var c in sentence)
            {
                typed.Append(c);
                start.Restart();
                var result = Suggest(engine, typed.ToString(), "Mail", "Follow up", CompletionLength.Medium, level);
                latencies.Add(start.ElapsedMilliseconds);
                if (c == ' ' && level is null) Console.WriteLine($"...{Quote(TextUtil.Suffix(typed.ToString(), 30))} -> {Quote(result)}");
            }
            latencies.Sort();
            Console.WriteLine($"per-keystroke latency: median {latencies[latencies.Count / 2]} ms, p90 {latencies[latencies.Count * 9 / 10]} ms, max {latencies[^1]} ms");
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
        if (models.IsDownloaded(model))
        {
            Console.WriteLine($"{model.Name} is already downloaded: {models.FileFor(model)}");
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
        if (!models.IsDownloaded(model)) return 1;
        Console.WriteLine($"Done: {models.FileFor(model)} (choose it in Settings > Model, or pass --model to --selftest)");
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

    private static string? Suggest(LlamaEngine engine, string text, string? app, string? title, CompletionLength length, ConfidenceLevel? level = null)
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
        return EngineService.Run(engine, request, () => true, out _);
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
