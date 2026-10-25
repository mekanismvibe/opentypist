import AppKit
import CryptoKit
import SwiftUI

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
        print("Loaded \(engine.modelDescription) in \(ms(since: start)) ms")
        // What "unload when idle" costs: reloading while macOS still caches the file.
        for attempt in 1...2 {
            engine.unload()
            start = Date()
            try? engine.load(path: path)
            print("Reload \(attempt): \(ms(since: start)) ms")
        }
        print("")

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
            let output = engine.complete(prompt: prompt.text, maxTokens: 20, requiredPrefix: prompt.wordPrefix,
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
        personalizationChecks(engine: engine)
        offlineChecks()
        // ggml's Metal backend asserts if the process exits with a model loaded.
        engine.unload()
        exit(0)
    }

    /// `OpenTypist --selftest-offline`: the checks that need no model (CI).
    static func runOffline(arguments: [String]) -> Never {
        autocorrectChecks(engine: nil, arguments: arguments)
        personalizationChecks(engine: nil)
        offlineChecks()
        exit(0)
    }

    private static func offlineChecks() {
        let font = NSFont.systemFont(ofSize: 13)
        let wrapped = OverlayWindow.wrap("I'll be there in about 10 minutes.", font: font, firstWidth: 70, otherWidth: 120)
        print("\nWrap (70pt, then 120pt lines): \(wrapped)")
        precondition(wrapped.count > 1 && wrapped.dropFirst().allSatisfy { $0.first?.isWhitespace != true })
        print("Wrap with no room after the caret: \(OverlayWindow.wrap(" be there soon", font: font, firstWidth: 5, otherWidth: 300))")

        // Confidence cut-off: stop at the first unsure token, and drop a
        // half-finished word when the unsure token would have continued it.
        func token(_ text: String, _ p: Float) -> LlamaEngine.GeneratedToken { .init(bytes: Array(text.utf8), probability: p) }
        let tokens = [token(" I", 0.9), token("'ll", 0.8), token(" be", 0.7), token(" th", 0.6), token("ere", 0.1)]
        precondition(PromptBuilder.confidentText(tokens, hitTokenLimit: true, minProbability: 0.05).text == " I'll be there")
        precondition(PromptBuilder.confidentText(tokens, hitTokenLimit: true, minProbability: 0.25).text == " I'll be")
        precondition(PromptBuilder.confidentText(tokens + [token(" soon", 0.05)], hitTokenLimit: false, minProbability: 0.08).text == " I'll be there")
        precondition(PromptBuilder.confidentText([token("ing", 0.9), token(" late", 0.1)], hitTokenLimit: false, minProbability: 0.25).text == "ing")
        precondition(PromptBuilder.confidentText([token(" maybe", 0.1)], hitTokenLimit: false, minProbability: 0.25).text.isEmpty)

        // Finishing a half-typed word uses its own, looser bar.
        var finishing = [token("terested", 0.3), token(" in", 0.2), token(" the", 0.9)]
        finishing[0].finishesWord = true
        precondition(PromptBuilder.confidentText(finishing, hitTokenLimit: false, minProbability: 0.45).text.isEmpty)
        precondition(PromptBuilder.confidentText(finishing, hitTokenLimit: false, minProbability: 0.45, wordMinProbability: 0.15).text == "terested")
        precondition(PromptBuilder.confidentText(finishing, hitTokenLimit: false, minProbability: 0.15, wordMinProbability: 0.1).text == "terested in the")

        // Streaming shows complete words only, cleaned like a final suggestion.
        let spaced = PromptBuilder.build(textBefore: "so ", appName: nil, windowTitle: nil)
        precondition(PromptBuilder.streamingText(" I'll be th", prompt: spaced, maxWords: 7, textAfter: "") == "I'll be")
        precondition(PromptBuilder.streamingText(" I'll", prompt: spaced, maxWords: 7, textAfter: "") == nil)
        precondition(PromptBuilder.streamingText(" I'll be there.", prompt: spaced, maxWords: 7, textAfter: "") == "I'll be there.")
        let midWord = PromptBuilder.build(textBefore: "very in", appName: nil, windowTitle: nil)
        precondition(PromptBuilder.streamingText("terested in th", prompt: midWord, maxWords: 7, textAfter: "") == "terested in")
        precondition(PromptBuilder.streamingText("terested in this", prompt: midWord, maxWords: 7, textAfter: " this job") == nil
                     || PromptBuilder.streamingText("terested in this job ", prompt: midWord, maxWords: 7, textAfter: " this job") == "terested in")

        // A half-typed word leaves the prompt, with the space before it, and
        // the model's output has to start with it.
        let splitWord = PromptBuilder.build(textBefore: "that is very in", appName: nil, windowTitle: nil)
        precondition(splitWord.text == "that is very" && splitWord.wordPrefix == " in")
        let lineStart = PromptBuilder.build(textBefore: "Hi,\nTha", appName: nil, windowTitle: nil)
        precondition(lineStart.text == "Hi,\n" && lineStart.wordPrefix == "Tha")
        precondition(PromptBuilder.build(textBefore: "see you ", appName: nil, windowTitle: nil).wordPrefix.isEmpty)
        precondition(PromptBuilder.build(textBefore: "so quickly. I\u{2019}m", appName: nil, windowTitle: nil).wordPrefix == " I\u{2019}m")

        // Smart quotes don't break following a suggestion as it's typed.
        let curly = Suggestion(textBefore: "Thanks so", text: " much. I'm free", pid: 1)
        precondition(curly.remaining(textBefore: "Thanks so much. I\u{2019}m") == " free")

        // Finishing a half-typed word has to make a real word.
        let known: (String) -> Bool = { ["tomorrow", "appreciate"].contains($0) }
        precondition(PromptBuilder.makesRealWord(textBefore: "see you tomor", suggestion: "row at", isKnown: known))
        precondition(!PromptBuilder.makesRealWord(textBefore: "see you tomor", suggestion: "ow at", isKnown: known))
        precondition(PromptBuilder.makesRealWord(textBefore: "see you ", suggestion: "tomorow", isKnown: known))
        precondition(PromptBuilder.makesRealWord(textBefore: "ask Prox", suggestion: "mox", isKnown: known))

        // Junk the model can be sure of.
        precondition(PromptBuilder.junkReason(" Hi S. Hi S. Hi S.", textBefore: "Hello") == "repeats itself")
        precondition(PromptBuilder.junkReason("o much. Hi Sarah, thanks for s", textBefore: "Hi Sarah, thanks for s") == "repeats what was just typed")
        precondition(PromptBuilder.junkReason(" [User 0001] hello", textBefore: "ok") != nil)
        precondition(PromptBuilder.junkReason(" <br> see you", textBefore: "ok") == "contains HTML")
        precondition(PromptBuilder.junkReason(" for the <b> tag", textBefore: "use <b>") == nil)
        precondition(PromptBuilder.junkReason(" so much for helping me move", textBefore: "thanks") == nil)
        precondition(PromptBuilder.junkReason(" had had enough", textBefore: "she") == nil)

        // Sign-in fields and Mail's address fields are left alone.
        func signIn(_ labels: [String], _ identifiers: [String] = [], value: String = "", role: String = "AXTextField") -> Bool {
            FieldPolicy.isSignInField(role: role, labels: labels, identifiers: identifiers, value: value)
        }
        precondition(signIn(["Email or phone"]) && signIn(["Enter PIN"]) && signIn(["Your e-mail"]) && signIn(["Verification code"]))
        precondition(signIn([], ["identifierId"]) && signIn([], ["otp-input"]) && signIn([], ["user_email"]) && signIn([], ["txtPassword"]))
        precondition(signIn([], value: "alice@exa") && signIn([], value: "alice@example.com"))
        precondition(!signIn(["Email subject"]) && !signIn(["Search mail"], ["emailSearch"]) && !signIn(["Promo code"]))
        precondition(!signIn(["Shipping address"], ["spinner-box"], value: "medium.com/@alice"))
        precondition(!signIn(["Email"], role: "AXTextArea"))
        precondition(FieldPolicy.isMailHeaderField(bundleID: "com.apple.mail", role: "AXTextField"))
        precondition(!FieldPolicy.isMailHeaderField(bundleID: "com.apple.mail", role: "AXTextArea"))
        precondition(!FieldPolicy.isMailHeaderField(bundleID: "com.apple.TextEdit", role: "AXTextField"))

        // Gemma 4 E2B moved to Q4_0; the earlier Q4_K_M file still counts.
        let gemma = ModelCatalog.model(id: "gemma-4-e2b")!
        precondition(gemma.fileName.contains("Q4_0") && gemma.legacyFileNames.contains("gemma-4-E2B.i1-Q4_K_M.gguf"))

        // Mid-line: lead into the text after the caret, don't repeat it.
        precondition(PromptBuilder.fitBefore(" this job.", suggestion: "terested in this job") == "terested in")
        precondition(PromptBuilder.fitBefore(" tomorrow", suggestion: " see you tomorrow") == " see you")
        precondition(PromptBuilder.fitBefore(", thanks", suggestion: " soon.") == " soon")
        precondition(PromptBuilder.fitBefore(" the report", suggestion: " the report") == nil)
        print("Mid-line fit: \"terested in this job\" before \" this job.\" → \(PromptBuilder.fitBefore(" this job.", suggestion: "terested in this job") ?? "nil")")

        // Accept keys.
        let tabDown = CGEvent(keyboardEventSource: nil, virtualKey: 48, keyDown: true)!
        tabDown.flags = []
        precondition(KeyCombo.tab.matches(tabDown) && !KeyCombo.shiftTab.matches(tabDown))
        tabDown.flags = .maskShift
        precondition(KeyCombo.shiftTab.matches(tabDown) && !KeyCombo.tab.matches(tabDown))
        let arrow = CGEvent(keyboardEventSource: nil, virtualKey: 124, keyDown: true)!
        arrow.flags = [.maskSecondaryFn, .maskNumericPad] // arrows carry these; they must not matter
        precondition(KeyCombo.rightArrow.matches(arrow))
        precondition(KeyCombo(keyCode: 0, character: "A").problem != nil && KeyCombo(keyCode: 49).problem != nil)
        precondition(KeyCombo(keyCode: 0, control: true, character: "A").problem == nil && KeyCombo.rightArrow.problem == nil)
        precondition(KeyCombo.tab.hintLabel == "tab" && KeyCombo.optionRightArrow.displayName == "⌥→")

        // Text replacements insert exactly what was written.
        let replacements = AutocorrectRules()
        replacements.add(AutocorrectRule(trigger: "omw", replacement: "On my way!", caseSensitive: false, immediate: false, insideWords: false, exact: true))
        replacements.add(AutocorrectRule(trigger: "@@", replacement: "me@example.com", caseSensitive: false, immediate: false, insideWords: false, exact: true))
        replacements.add(AutocorrectRule(trigger: ";addr", replacement: "1 Main St\nSpringfield", caseSensitive: false, immediate: false, insideWords: false, exact: true))
        precondition(replacements.matchAtEnd(of: "ok OMW") == .fix(replacement: "On my way!", length: 3))
        precondition(replacements.matchAtEnd(of: "email: @@") == .fix(replacement: "me@example.com", length: 2))
        precondition(replacements.matchAtEnd(of: "send to ;addr") == .fix(replacement: "1 Main St\nSpringfield", length: 5))
        precondition(replacements.matchAtEnd(of: "x;addr") == nil)

        precondition(Updater.isNewer("0.10.0", than: "0.9.2") && Updater.isNewer("0.2.0", than: "0.1.0"))
        precondition(!Updater.isNewer("0.2.0", than: "0.2.0") && !Updater.isNewer("0.1.9", than: "0.2"))

        // Suggestion bookkeeping.
        let s = Suggestion(textBefore: "so ", text: "I'll be there soon", pid: 1)
        precondition(Suggestion.nextWord(of: s.text, includeTrailingSpace: true) == "I'll ")
        precondition(s.remaining(textBefore: "late, so I'll ") == "be there soon")
        precondition(s.remaining(textBefore: "late, so I'll b") == "e there soon")
        precondition(s.remaining(textBefore: "late, so I'd") == nil)
        print("\nSuggestion checks passed")
    }

    /// `OPENTYPIST_TEST_STORE=1 OpenTypist --snapshot-personal out.png`:
    /// renders Settings → Personal with sample data, for checking the layout.
    static func snapshotPersonal(arguments: [String]) -> Never {
        guard ProcessInfo.processInfo.environment["OPENTYPIST_TEST_STORE"] != nil,
              let index = arguments.firstIndex(of: "--snapshot-personal"), index + 1 < arguments.count else {
            print("Usage: OPENTYPIST_TEST_STORE=1 OpenTypist --snapshot-personal out.png")
            exit(1)
        }
        let personal = Personalization.shared
        for _ in 0..<4 {
            personal.observeWord("Sarah", startsSentence: false, bundleID: "com.apple.mail", appName: "Mail")
            personal.observeWord("Proxmox", startsSentence: false, bundleID: "com.apple.mail", appName: "Mail")
            personal.observeWord("homelab", startsSentence: false, bundleID: "com.apple.mail", appName: "Mail")
            personal.observeLine("Hi Sarah,")
            personal.observeLine("Cheers,")
        }
        for _ in 0..<16 { personal.observeWord("lol", startsSentence: true, bundleID: "com.apple.MobileSMS", appName: "Messages") }
        personal.addNeverCorrect("teh")
        personal.addNeverCorrect("gonna")
        personal.setMode(.askFirst)
        personal.updateProfile()
        Settings.shared.keepWritingSamples = true
        WritingSamples.shared.record(WritingSample(date: Date(), bundleID: "com.apple.MobileSMS", appName: "Messages",
                                                   windowTitle: "Sam", text: "haha yeah that works for me, see you at 7 then! bring snacks"))

        // Any other tab: --snapshot-personal out.png --tab general|replacements|stats|apps
        var tab = "personal"
        if let t = arguments.firstIndex(of: "--tab"), t + 1 < arguments.count { tab = arguments[t + 1] }
        TextReplacements.shared.add(shortcut: "@@", expansion: "alex@example.com")
        TextReplacements.shared.add(shortcut: ";addr", expansion: "1 Main Street\nSpringfield")
        var days: [String: Int] = [:]
        for (offset, day) in Stats.shared.recentDays(30).enumerated() {
            days[ISO8601DateFormatter.string(from: day.date, timeZone: .current, formatOptions: [.withFullDate])] = [0, 40, 85, 120, 60, 0, 30][offset % 7] + offset * 3
        }
        Stats.shared.setDailyWordsForPreview(days)
        Settings.shared.appAcceptKeys = ["com.tinyspeck.slackmacgap": AcceptKeys(word: .rightArrow, all: .optionRightArrow)]
        ViewState.shared.settingsTab = tab
        let height: CGFloat = tab == "personal" ? 1700 : 1500
        let root: AnyView = tab == "personal" ? AnyView(PersonalSettings()) : AnyView(SettingsView(height: height))
        let view = NSHostingView(rootView: root.frame(width: 620, height: height))
        view.frame = NSRect(x: 0, y: 0, width: 620, height: height)
        let window = NSWindow(contentRect: view.frame, styleMask: [.borderless], backing: .buffered, defer: false)
        window.appearance = NSAppearance(named: .darkAqua)
        window.contentView = view
        view.layoutSubtreeIfNeeded()
        RunLoop.main.run(until: Date().addingTimeInterval(0.5))
        guard let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { exit(1) }
        view.cacheDisplay(in: view.bounds, to: rep)
        try? rep.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: arguments[index + 1]))
        Settings.shared.keepWritingSamples = false
        print("Saved \(arguments[index + 1])")
        exit(0)
    }

    /// `OpenTypist --calibrate`: replays real-looking messages word by word
    /// (and mid-word) and measures, for each confidence threshold, how often a
    /// suggestion is shown and how often its first word is what came next.
    static func calibrate(arguments: [String]) -> Never {
        let engine = LlamaEngine()
        var path = ModelManager.shared.activeModelURL?.path
        if let i = arguments.firstIndex(of: "--model"), i + 1 < arguments.count { path = arguments[i + 1] }
        guard let path, (try? engine.load(path: path)) != nil else {
            print("No model")
            exit(1)
        }
        print("model: \(URL(fileURLWithPath: path).lastPathComponent) (\(engine.modelDescription))")
        let corpus: [(app: String, text: String)] = [
            ("Mail", "Hi Sarah, thanks for getting back to me so quickly. I'm running a few minutes late, so please start the meeting without me. I will join as soon as I can and catch up on the notes afterwards."),
            ("Mail", "Dear hiring manager, I am writing to express my interest in the software engineer position at your company. I have five years of experience building web applications and I would love the opportunity to talk with you."),
            ("Messages", "hey are you still coming to the party tonight? i was thinking we could get dinner first if you want. let me know what time works for you"),
            ("Slack", "Quick update on the release: the build is green and QA signed off this morning. I will tag the new version after lunch, so let me know if anything else needs to go in before then."),
            ("Messages", "thanks so much for helping me move today, i really appreciate it. let me know when you want to grab a beer and i will pay"),
            ("Notes", "Things to do this weekend: clean the garage, call mom about the trip, pick up groceries for the week and finish reading the book for book club."),
            ("Mail", "Thank you for your order. Your package has been shipped and should arrive within three to five business days. If you have any questions, please contact our support team."),
        ]
        let thresholds: [Float] = [0, 0.05, 0.08, 0.12, 0.16, 0.2, 0.25, 0.3, 0.4, 0.5]
        var shown = [Int](repeating: 0, count: thresholds.count)
        var firstRight = [Int](repeating: 0, count: thresholds.count)
        var wordsRight = [Int](repeating: 0, count: thresholds.count)
        var wordsShown = [Int](repeating: 0, count: thresholds.count)
        var positions = 0

        func words(_ s: String) -> [String] {
            s.lowercased().split(whereSeparator: { $0.isWhitespace }).map { $0.trimmingCharacters(in: .punctuationCharacters) }
        }

        for (app, text) in corpus {
            let allWords = text.split(separator: " ").map(String.init)
            for i in 2..<allWords.count {
                // At a word boundary, and (for longer words) two letters in.
                var cases = [(allWords[..<i].joined(separator: " ") + " ", allWords[i...].joined(separator: " "))]
                if allWords[i].count >= 5 {
                    let typed = String(allWords[i].prefix(2))
                    cases.append((allWords[..<i].joined(separator: " ") + " " + typed,
                                  String(allWords[i].dropFirst(2)) + " " + allWords[(i + 1)...].joined(separator: " ")))
                }
                for (before, truth) in cases {
                    positions += 1
                    let prompt = PromptBuilder.build(textBefore: before, appName: app, windowTitle: nil)
                    guard let output = engine.complete(prompt: prompt.text, maxTokens: 20, requiredPrefix: prompt.wordPrefix,
                                                       shouldContinue: { true }, stop: { PromptBuilder.shouldStop($0, maxWords: 7) }) else { continue }
                    let midWord = !before.hasSuffix(" ")
                    let truthWords = words(truth)
                    for (t, threshold) in thresholds.enumerated() {
                        let confident = PromptBuilder.confidentText(output.tokens, hitTokenLimit: output.hitTokenLimit, minProbability: threshold)
                        guard let suggestion = PromptBuilder.clean(confident.text, prompt: prompt, maxWords: 7, hitTokenLimit: confident.hitTokenLimit) else { continue }
                        // Mid-word, the first suggested "word" finishes the typed one.
                        let suggested = words(midWord ? suggestion.drop(while: { $0.isWhitespace }).description : suggestion)
                        guard !suggested.isEmpty else { continue }
                        shown[t] += 1
                        wordsShown[t] += suggested.count
                        if suggested.first == truthWords.first { firstRight[t] += 1 }
                        wordsRight[t] += zip(suggested, truthWords).prefix { $0 == $1 }.count
                    }
                }
            }
        }
        print("positions: \(positions)")
        print("threshold  shown   first word right   correct words / shown words")
        for (t, threshold) in thresholds.enumerated() {
            let coverage = Double(shown[t]) / Double(positions) * 100
            let precision = shown[t] > 0 ? Double(firstRight[t]) / Double(shown[t]) * 100 : 0
            let wordPrecision = wordsShown[t] > 0 ? Double(wordsRight[t]) / Double(wordsShown[t]) * 100 : 0
            print(String(format: "  %.2f     %5.1f%%   %5.1f%%             %5.1f%%", threshold, coverage, precision, wordPrecision))
        }

        // Finishing a half-typed word: 1–3 letters into longer words, scored
        // on whether the completed word shows up and is the right word.
        var wordCases: [(before: String, prompt: PromptBuilder.Prompt, truth: String, output: LlamaEngine.Output)] = []
        for (app, text) in corpus {
            let allWords = text.split(separator: " ").map(String.init)
            for i in 2..<allWords.count {
                let target = allWords[i].trimmingCharacters(in: .punctuationCharacters).lowercased()
                for typedCount in 1...3 where target.count >= typedCount + 3 && target.allSatisfy(\.isLetter) {
                    let before = allWords[..<i].joined(separator: " ") + " " + String(allWords[i].prefix(typedCount))
                    let prompt = PromptBuilder.build(textBefore: before, appName: app, windowTitle: nil)
                    guard !prompt.wordPrefix.isEmpty, let output = engine.complete(
                        prompt: prompt.text, maxTokens: 20, requiredPrefix: prompt.wordPrefix,
                        shouldContinue: { true }, stop: { PromptBuilder.shouldStop($0, maxWords: 7) }) else { continue }
                    wordCases.append((before, prompt, String(target.dropFirst(typedCount)), output))
                }
            }
        }
        print("\nFinishing the current word (\(wordCases.count) half-typed words)")
        print("level      word threshold  completion shown  right word  dropped as misspelled")
        for level in ConfidenceLevel.allCases {
            for wordThreshold: Float in [level.minProbability, 0.15, 0.1, 0.06, 0.04, 0.02] where wordThreshold <= level.minProbability {
                var shownWords = 0, rightWords = 0, misspelled = 0
                for item in wordCases {
                    let confident = PromptBuilder.confidentText(item.output.tokens, hitTokenLimit: item.output.hitTokenLimit,
                                                                minProbability: level.minProbability, wordMinProbability: wordThreshold)
                    guard let suggestion = PromptBuilder.clean(confident.text, prompt: item.prompt, maxWords: 7, hitTokenLimit: confident.hitTokenLimit),
                          let completion = words(suggestion).first else { continue }
                    guard PromptBuilder.makesRealWord(textBefore: item.before, suggestion: suggestion, isKnown: SpellCheck.isValidWord) else {
                        misspelled += 1
                        continue
                    }
                    shownWords += 1
                    if completion == item.truth { rightWords += 1 }
                }
                print(String(format: "%-10@ %.2f            %5.1f%%            %5.1f%%      %d", level.rawValue as NSString, wordThreshold,
                             Double(shownWords) / Double(wordCases.count) * 100,
                             shownWords > 0 ? Double(rightWords) / Double(shownWords) * 100 : 0, misspelled))
            }
        }

        // Latency as the app runs it: medium length, early stop at each level.
        // "first word" is when the first complete word exists, which is the
        // earliest a streaming display could show something.
        print("\nLatency per keystroke (medium length, app settings)")
        print("level      tokens/keystroke  total median  total p90  first word median  first word p90")
        for level in ConfidenceLevel.allCases {
            var totals: [Double] = [], firsts: [Double] = [], tokenCounts: [Int] = []
            for (app, text) in corpus {
                let allWords = text.split(separator: " ").map(String.init)
                for i in 2..<allWords.count {
                    var befores = [allWords[..<i].joined(separator: " ") + " "]
                    if allWords[i].count >= 5 { befores.append(allWords[..<i].joined(separator: " ") + " " + allWords[i].prefix(2)) }
                    for before in befores {
                        let prompt = PromptBuilder.build(textBefore: before, appName: app, windowTitle: nil)
                        let start = Date()
                        var firstWord: Double?
                        let output = engine.complete(
                            prompt: prompt.text, maxTokens: CompletionLength.medium.maxTokens, requiredPrefix: prompt.wordPrefix,
                            minProbability: level.minProbability, wordMinProbability: level.wordMinProbability,
                            shouldContinue: { true },
                            stop: { text in
                                if firstWord == nil, PromptBuilder.firstCompleteWord(in: text, prompt: prompt) != nil {
                                    firstWord = Date().timeIntervalSince(start) * 1000
                                }
                                return PromptBuilder.shouldStop(text, maxWords: CompletionLength.medium.maxWords)
                            })
                        let total = Date().timeIntervalSince(start) * 1000
                        guard let output else { continue }
                        totals.append(total)
                        firsts.append(firstWord ?? total)
                        tokenCounts.append(output.tokens.count)
                    }
                }
            }
            totals.sort(); firsts.sort()
            let meanTokens = Double(tokenCounts.reduce(0, +)) / Double(max(tokenCounts.count, 1))
            print(String(format: "%-10@ %5.1f             %6.1f ms     %6.1f ms   %6.1f ms          %6.1f ms",
                         level.rawValue as NSString, meanTokens, totals[totals.count / 2], totals[totals.count * 9 / 10],
                         firsts[firsts.count / 2], firsts[firsts.count * 9 / 10]))
        }
        engine.unload()
        exit(0)
    }

    /// `OpenTypist --update-test <folder>`: runs the real updater against the
    /// latest GitHub release, but swaps a throwaway copy of this app inside
    /// <folder> instead of the installed one.
    static func updateTest(arguments: [String]) -> Never {
        guard let index = arguments.firstIndex(of: "--update-test"), index + 1 < arguments.count,
              Bundle.main.bundleURL.pathExtension == "app" else {
            print("Usage: /path/to/OpenTypist.app/Contents/MacOS/OpenTypist --update-test <folder>")
            exit(1)
        }
        let folder = URL(fileURLWithPath: arguments[index + 1])
        let target = folder.appendingPathComponent("OpenTypist.app")
        let done = DispatchSemaphore(value: 0)
        var failed = false
        Task {
            do {
                try? FileManager.default.removeItem(at: target)
                try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
                try FileManager.default.copyItem(at: Bundle.main.bundleURL, to: target)
                let before = Bundle(url: target)?.infoDictionary?["CFBundleShortVersionString"] as? String ?? "?"
                guard let release = try await Updater.shared.latestRelease() else {
                    print("No published release with a DMG and checksum")
                    failed = true
                    done.signal()
                    return
                }
                print("Latest release: \(release.version) (\(release.page.absoluteString)); this copy: \(before)")
                let staged = try await Updater.shared.prepare(release)
                print("Downloaded, checksum verified, app verified: \(staged.path)")
                try Updater.shared.replace(appAt: target, with: staged)
                // Bundle caches Info.plist per path, so read the new file directly.
                let plist = NSDictionary(contentsOf: target.appendingPathComponent("Contents/Info.plist"))
                let after = plist?["CFBundleShortVersionString"] as? String ?? "?"
                print("Replaced \(target.path): \(before) → \(after)")
            } catch {
                print("Update test failed: \(error.localizedDescription)")
                failed = true
            }
            done.signal()
        }
        while done.wait(timeout: .now()) == .timedOut { RunLoop.main.run(until: Date().addingTimeInterval(0.1)) }
        exit(failed ? 1 : 0)
    }

    /// `OpenTypist --complete a.txt b.txt …`: greedy continuation of each raw
    /// prompt file, for tuning prompt formats.
    static func completeFiles(arguments: [String]) -> Never {
        let engine = LlamaEngine()
        guard let path = ModelManager.shared.activeModelURL?.path, (try? engine.load(path: path)) != nil else {
            print("No model")
            exit(1)
        }
        let files = arguments.drop { $0 != "--complete" }.dropFirst()
        for file in files {
            guard let prompt = try? String(contentsOfFile: file, encoding: .utf8) else { continue }
            let output = engine.complete(prompt: prompt, maxTokens: 8,
                                         shouldContinue: { true }, stop: { $0.contains("\n") && $0.count > 1 })
            print("\(URL(fileURLWithPath: file).lastPathComponent): \(output.map { $0.text.debugDescription } ?? "nil")")
        }
        engine.unload()
        exit(0)
    }

    /// Learning, profile updates, lock, history, encryption and samples,
    /// against a throwaway store (never the Keychain or the user's data).
    private static func personalizationChecks(engine: LlamaEngine?) {
        print("\n== Personalization")
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("opentypist-selftest-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: directory) }
        let store = SecureStore(directory: directory, key: SymmetricKey(size: .bits256))
        let personal = Personalization(store: store)

        for _ in 0..<3 {
            personal.observeWord("Sarah", startsSentence: false, bundleID: "com.apple.mail", appName: "Mail")
            personal.observeWord("Proxmox", startsSentence: false, bundleID: "com.apple.mail", appName: "Mail")
            personal.observeWord("homelab", startsSentence: false, bundleID: "com.apple.mail", appName: "Mail")
            personal.observeLine("Hi Sarah,")
            personal.observeLine("Cheers,")
        }
        for word in ["colour", "organise", "favourite", "realised", "travelled", "color"] {
            personal.observeWord(word, startsSentence: false, bundleID: nil, appName: "")
        }
        for _ in 0..<16 { personal.observeWord("lol", startsSentence: true, bundleID: "com.apple.MobileSMS", appName: "Messages") }
        for line in personal.generatedLines() { print("  [\(line.topic)] \(line.line)") }

        print("  auto update: \(personal.updateProfile() ?? "no change")")
        precondition(personal.profile.text.hasPrefix(Personalization.defaultProfile) && personal.profile.text.contains("Proxmox"))
        precondition(personal.isNeverCorrect("homelab") && !personal.isNeverCorrect("sarah"))

        // The user rewrites the names line: that topic is theirs from now on.
        let namesLine = personal.profile.text.components(separatedBy: "\n").first { $0.hasPrefix("People and things I often mention:") } ?? ""
        let edited = personal.profile.text.replacingOccurrences(of: namesLine, with: "I work with Sarah on our Proxmox cluster.")
        personal.saveProfileText(edited)
        for _ in 0..<3 { personal.observeWord("Jaxon", startsSentence: false, bundleID: nil, appName: "") }
        print("  after edit + new name: \(personal.updateProfile() ?? "no change")")
        precondition(personal.profile.text.contains("I work with Sarah") && !personal.profile.text.contains("Jaxon"))
        precondition(personal.profile.userOwnedTopics.contains("names"))

        personal.setLocked(true)
        personal.observeWord("Toronto", startsSentence: false, bundleID: nil, appName: "")
        precondition(personal.updateProfile() == nil)
        personal.setLocked(false)
        personal.setMode(.askFirst)
        for _ in 0..<3 { personal.observeWord("kubectl", startsSentence: false, bundleID: nil, appName: "") }
        print("  ask-first proposal: \(personal.updateProfile() ?? "none")")
        precondition(personal.profile.proposal != nil && !personal.profile.text.contains("kubectl"))
        personal.applyProposal()
        precondition(personal.profile.text.contains("kubectl"))
        personal.addNeverCorrect("Teh")
        precondition(personal.isNeverCorrect("teh"))
        print("  history: \(personal.profile.history.map(\.note))")

        personal.saveLearned()
        let reloaded = Personalization(store: store)
        precondition(reloaded.profile == personal.profile && reloaded.learned == personal.learned)
        let raw = (try? Data(contentsOf: directory.appendingPathComponent("profile.bin"))) ?? Data()
        precondition(!String(decoding: raw, as: UTF8.self).contains("Proxmox"), "profile must be encrypted")
        print("  encrypted round-trip OK (\(raw.count) bytes on disk)")

        // Data saved under another key is set aside, never overwritten.
        let otherKeyStore = SecureStore(directory: directory, key: SymmetricKey(size: .bits256))
        precondition(otherKeyStore.load(ProfileState.self, from: "profile.bin") == nil)
        precondition(FileManager.default.fileExists(atPath: directory.appendingPathComponent("profile.bin.unreadable").path))
        precondition(!FileManager.default.fileExists(atPath: directory.appendingPathComponent("profile.bin").path))

        // A long unbroken token stands in for an API key (a realistic-looking
        // fake key would trip secret scanners).
        let cleaned = WritingSamples.clean("My card is 4111 1111 1111 1111 and the token is abcdefghij0123456789abcdefghij0123, thanks so much for helping") ?? ""
        print("  clean: \(cleaned)")
        precondition(!cleaned.contains("4111") && !cleaned.contains("abcdefghij0123"))

        // Does the profile change what the model suggests?
        let text = "Hi Sarah,\n\nThanks again for your help with the move today, I really appreciate it.\n\nCheers,\n"
        let profiles: [String?] = [nil, "I'm Alex Chen, a nurse in Toronto. I sign my emails \"Cheers, Alex\"."]
        for profile in profiles {
            let prompt = PromptBuilder.build(textBefore: text, appName: "Mail", windowTitle: nil, profile: profile)
            if let profile { precondition(prompt.text.hasPrefix("About me:\n\(profile)"), "profile missing from prompt") }
            guard let engine else { continue }
            let output = engine.complete(prompt: prompt.text, maxTokens: 6,
                                         shouldContinue: { true }, stop: { $0.contains("\n") })
            print("  profile \(profile == nil ? "off" : "on "): \"…Cheers,\\n\" → \(output.map { $0.text.debugDescription } ?? "nil")")
        }

        // Writing samples reach the prompt too, after the profile.
        let samples = WritingSamples(store: store)
        let wasEnabled = Settings.shared.keepWritingSamples
        Settings.shared.keepWritingSamples = true
        defer { Settings.shared.keepWritingSamples = wasEnabled }
        for message in ["haha yeah that works for me, see you at 7 then!", "omg yes!! bring the dog too, she'll love it",
                        "ok sounds good, i'll grab snacks on the way over"] {
            samples.record(WritingSample(date: Date(), bundleID: "com.apple.MobileSMS", appName: "Messages", windowTitle: "Sam", text: message + " " + message))
        }
        let examples = samples.examples(bundleID: "com.apple.MobileSMS", windowTitle: "Sam")
        let header = PromptBuilder.header(appName: "Messages", windowTitle: "Sam", screenText: nil, profile: "I'm Alex.", examples: examples)
        precondition(examples.count == 3 && header.hasPrefix("About me:\nI'm Alex.\n\nExamples of things I've written in Messages:\n\""))
        precondition(header.hasSuffix("Application: Messages\nWindow: Sam\nText being written:\n"))
        print("  samples: \(samples.samples.count) stored, \(examples.count) used in the prompt")
        guard let engine else { return }
        for chat in [false, true] {
            let prompt = PromptBuilder.build(textBefore: "hey are you free tonight? i was thinking we could", appName: "Messages", windowTitle: "Sam",
                                             profile: "I'm Alex.", examples: chat ? examples : [])
            let output = engine.complete(prompt: prompt.text, maxTokens: 12, requiredPrefix: prompt.wordPrefix,
                                         shouldContinue: { true }, stop: { PromptBuilder.shouldStop($0, maxWords: 7) })
            let text = output.flatMap { PromptBuilder.clean($0.text, prompt: prompt, maxWords: 7, hitTokenLimit: $0.hitTokenLimit) }
            print("  samples \(chat ? "on " : "off"): \"…i was thinking we could\" → \(text.debugDescription)")
        }
    }

    /// List matching (optionally with `--ahk file.ahk`) and model choices.
    private static func autocorrectChecks(engine: LlamaEngine?, arguments: [String]) {
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
        precondition(rules.matchAtEnd(of: "I think teh") == .fix(replacement: "the", length: 3))
        precondition(rules.matchAtEnd(of: "TEH") == .fix(replacement: "THE", length: 3))
        precondition(rules.matchAtEnd(of: "We need to redesign") == nil)
        precondition(SpellCheck.editDistance("teh", "the") == 1 && SpellCheck.adjacentKeys("e", "r") && !SpellCheck.adjacentKeys("t", "e"))

        guard let engine else { return }
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
            requiredPrefix: prompt.wordPrefix,
            shouldContinue: { true },
            stop: { PromptBuilder.shouldStop($0, maxWords: length.maxWords) }
        ) else { return nil }
        return PromptBuilder.clean(output.text, prompt: prompt, maxWords: length.maxWords, hitTokenLimit: output.hitTokenLimit)
    }

    private static func ms(since date: Date) -> Int { Int(Date().timeIntervalSince(date) * 1000) }
}
