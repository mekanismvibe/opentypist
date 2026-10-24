import AppKit

/// `OpenTypist --selftest [--model path.gguf]` runs the engine on sample text
/// without any UI or permissions, printing suggestions and latency.
enum SelfTest {
    static func run(arguments: [String]) -> Never {
        var path = ModelManager.shared.activeModelURL?.path
        if let i = arguments.firstIndex(of: "--model"), i + 1 < arguments.count { path = arguments[i + 1] }
        guard let path else {
            print("No model found. Pass --model /path/to/model.gguf")
            exit(1)
        }

        let engine = LlamaEngine()
        var start = Date()
        do {
            try engine.load(path: path)
        } catch {
            print("Load failed: \(error.localizedDescription)")
            exit(1)
        }
        print("Loaded \(engine.modelDescription) in \(ms(since: start)) ms\n")

        let cases: [(String, String?, String?)] = [
            ("Hi Sarah, thanks for getting back to me so quickly. I'm running a few minutes late, so ", "Mail", "Re: Lunch tomorrow"),
            ("Hi Sarah, thanks for getting back to me so quickly. I'm running a few minutes late, so I'll be th", "Mail", "Re: Lunch tomorrow"),
            ("The quarterly report shows that revenue grew by", "Pages", "Q3 Report"),
            ("hey are you still coming to the party tonight? i was thinking we could", "Messages", "Alex"),
            ("Dear hiring manager,\n\nI am writing to express my interest in the", "Mail", "Application"),
            ("Thanks for the update! Let me know if you need anything el", "Slack", "#design"),
            ("Thanks for the update! Let me know if you need anything el", nil, nil),
        ]

        for length in [CompletionLength.short, .medium] {
            print("== \(length.title)")
            for (text, app, title) in cases {
                start = Date()
                let result = suggest(engine, text: text, app: app, title: title, length: length)
                print("[\(ms(since: start)) ms] \(app ?? "-") | …\(text.suffix(40).debugDescription) → \(result.debugDescription)")
            }
            print("")
        }

        // Simulate typing one character at a time to measure per-keystroke latency.
        let sentence = "I wanted to follow up on our conversation from last week about the new project timeline"
        var latencies: [Int] = []
        var typed = ""
        print("== Typing simulation")
        for c in sentence {
            typed.append(c)
            start = Date()
            let result = suggest(engine, text: typed, app: "Mail", title: "Follow up", length: .medium)
            latencies.append(ms(since: start))
            if c == " " { print("…\(typed.suffix(30).debugDescription) → \(result.debugDescription)") }
        }
        let sorted = latencies.sorted()
        print("per-keystroke latency: median \(sorted[sorted.count / 2]) ms, p90 \(sorted[sorted.count * 9 / 10]) ms, max \(sorted.last!) ms")

        // On-screen context: the first prompt in a window pays for ~200 extra
        // tokens, later keystrokes reuse the cached prefix.
        let thread = """
        From: Sarah Chen
        Subject: Lunch tomorrow?
        Hey! Are we still on for lunch tomorrow at 12:30? I booked a table at the Thai place on 5th street. \
        Let me know if you'd rather go somewhere else. Also, can you bring the slides from Tuesday's meeting? \
        I want to go over the budget numbers with you before Friday.
        """
        print("\n== On-screen context")
        var reply = "Hi Sarah, "
        for addition in ["", "sounds ", "good, ", "see you "] {
            reply += addition
            start = Date()
            let prompt = PromptBuilder.build(textBefore: reply, appName: "Mail", windowTitle: "Re: Lunch tomorrow?", screenText: thread)
            let output = engine.complete(prompt: prompt.text, maxTokens: 20, healLastToken: prompt.healLastToken,
                                         shouldContinue: { true }, stop: { PromptBuilder.shouldStop($0, maxWords: 7) })
            let text = output.flatMap { PromptBuilder.clean($0.text, prompt: prompt, maxWords: 7, hitTokenLimit: $0.hitTokenLimit) }
            print("[\(ms(since: start)) ms] …\(reply.suffix(30).debugDescription) → \(text.debugDescription)")
        }

        let hardware = HardwareInfo.current
        print("\nThis Mac: \(hardware.summary) → recommended \(ModelCatalog.recommended(for: hardware).name)")
        for memory in [8, 16, 24, 36, 64, 128] {
            let fake = HardwareInfo(chip: "Apple M4", memoryGB: memory, performanceCores: 4)
            print("  \(memory) GB → \(ModelCatalog.recommended(for: fake).name)")
        }

        autocorrectChecks(engine: engine, arguments: arguments)

        let font = NSFont.systemFont(ofSize: 13)
        let wrapped = OverlayWindow.wrap("I'll be there in about 10 minutes.", font: font, firstWidth: 70, otherWidth: 120)
        print("\nWrap (70pt, then 120pt lines): \(wrapped)")
        precondition(wrapped.count > 1 && wrapped.dropFirst().allSatisfy { $0.first?.isWhitespace != true })
        print("Wrap with no room after the caret: \(OverlayWindow.wrap(" be there soon", font: font, firstWidth: 5, otherWidth: 300))")

        // Suggestion bookkeeping.
        let s = Suggestion(textBefore: "so ", text: "I'll be there soon", pid: 1)
        precondition(Suggestion.nextWord(of: s.text, includeTrailingSpace: true) == "I'll ")
        precondition(s.remaining(textBefore: "late, so I'll ") == "be there soon")
        precondition(s.remaining(textBefore: "late, so I'll b") == "e there soon")
        precondition(s.remaining(textBefore: "late, so I'd") == nil)
        print("\nSuggestion checks passed")
        // ggml's Metal backend asserts if the process exits with a model loaded.
        engine.unload()
        exit(0)
    }

    /// List matching (optionally with `--ahk file.ahk`) and model choices.
    private static func autocorrectChecks(engine: LlamaEngine, arguments: [String]) {
        print("\n== Autocorrect lists")
        let rules = AutocorrectRules()
        if let url = Autocorrector.builtInListURL { print("built-in: \(rules.load(fileAt: url)) entries") }
        if let i = arguments.firstIndex(of: "--ahk"), i + 1 < arguments.count {
            print("ahk: \(rules.load(fileAt: URL(fileURLWithPath: arguments[i + 1]))) hotstrings")
        }
        let cases = ["I think teh", "Teh", "TEH", "becuase", "I recieved", "it was definately", "achive",
                     "We need to redesign", "a foriegn", "I am designign", "he doe snot", "it was dispell"]
        for text in cases {
            let match = rules.matchAtEnd(of: text) ?? rules.matchImmediate(text)
            print("  \(text.debugDescription) → \(String(describing: match))")
        }
        print("  conformCase: \(AutocorrectRules.conformCase(typed: "Teh", replacement: "the")), edit(teh, the) = \(SpellCheck.editDistance("teh", "the"))")
        print("  spell languages: \(SpellCheck.languages); 'loosing' valid: \(SpellCheck.isValidWord("loosing")); 'stoer' guesses: \(SpellCheck.guesses(for: "stoer"))")

        print("\n== Model picks (log-probabilities; first entry is what was typed)")
        let picks: [(String, [String])] = [
            ("I worked hard all year to", ["achive", "achieve", "archive"]),
            ("Please remember to", ["achive", "achieve", "archive"]),
            ("I keep", ["loosing", "losing"]),
            ("The knot kept", ["loosing", "losing"]),
            ("I need to go to the", ["stoer", "store", "stoner"]),
            ("My friend", ["Jaxon", "Jason", "Saxon"]),
            ("Can you send me the", ["recipt", "receipt", "recipe"]),
        ]
        for (context, candidates) in picks {
            let start = Date()
            let prompt = PromptBuilder.scoringPrompt(context: context + " ", appName: "Messages", windowTitle: nil, screenText: nil)
            let scores = engine.score(prompt: prompt.text, continuations: candidates.map { prompt.wordPrefix + $0 }) ?? []
            let shown = zip(candidates, scores).enumerated().map { index, pair in
                let cost = index == 0 ? 0 : 2.0 * SpellCheck.typoCost(typed: candidates[0], intended: pair.0)
                return "\(pair.0) \(String(format: "%.1f", pair.1))→\(String(format: "%.1f", pair.1 - cost))"
            }.joined(separator: ", ")
            print("[\(ms(since: start)) ms] \(context.debugDescription) → \(shown)")
        }
    }

    private static func suggest(_ engine: LlamaEngine, text: String, app: String?, title: String?, length: CompletionLength) -> String? {
        let prompt = PromptBuilder.build(textBefore: text, appName: app, windowTitle: title)
        guard let output = engine.complete(
            prompt: prompt.text,
            maxTokens: length.maxTokens,
            healLastToken: prompt.healLastToken,
            shouldContinue: { true },
            stop: { PromptBuilder.shouldStop($0, maxWords: length.maxWords) }
        ) else { return nil }
        return PromptBuilder.clean(output.text, prompt: prompt, maxWords: length.maxWords, hitTokenLimit: output.hitTokenLimit)
    }

    private static func ms(since date: Date) -> Int { Int(Date().timeIntervalSince(date) * 1000) }
}
