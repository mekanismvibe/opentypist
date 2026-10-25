import AppKit

/// Fixes misspellings as the user types, like AutoHotkey's AutoCorrect.ahk:
///
/// 1. **Lists** (instant): when a word ends, look the recently typed text up
///    in the misspelling lists and, on a hit, swallow the ending key, delete
///    the typo, type the fix and send the ending key again.
/// 2. **Language model** (a moment later): for typos the lists can't settle
///    (several possible fixes, a "typo" that is also a real word, or a word
///    the macOS dictionary doesn't know), score each candidate in context and
///    fix the word only if one is clearly more likely than what was typed.
///
/// Pressing Delete right after a fix undoes it and stops that word from being
/// fixed again this session. Main thread only (runs from the event tap).
final class Autocorrector {
    private let engine: EngineService
    private let settings = Settings.shared
    private let stats = Stats.shared
    private let status = AppStatus.shared

    /// Tells the completion controller that we changed the text.
    var onTextChanged: (() -> Void)?
    /// The focused field asks for an email, username or code (see
    /// `FieldPolicy`): nothing gets corrected there or learned from it.
    var inSignInField = false

    /// Text typed since the caret last jumped (click, arrows, shortcut).
    private var typed = ""
    private static let maxTyped = 80

    /// The last fix, while Delete can still undo it.
    private var lastFix: (original: String, replacement: String, ending: String)?
    /// Learns from finished words; also holds the never-correct list.
    private let personal = Personalization.shared
    private var currentApp: (bundleID: String?, name: String) = (nil, "")

    /// A language-model check in flight for the word before `ending`.
    private struct PendingCheck {
        let id: Int
        let word: String
        let ending: String
        /// Typed after the ending while the check runs.
        var typedSince = ""
    }
    private var pending: PendingCheck?
    private var nextCheckID = 0

    /// How much more likely (in log-probability) a correction must be than
    /// the typed word before it replaces it.
    private enum Margin {
        static let knownMisspelling = 0.0
        static let unknownWord = 2.5
        static let realWord = 1.5
    }

    /// Weight of the keyboard typo cost against the model's log-probability.
    /// Without it the model happily turns "recipt" into "recipe".
    private static let typoCostWeight = 2.0

    init(engine: EngineService) {
        self.engine = engine
        reloadRules()
    }

    // MARK: - Lists

    static var userListsDirectory: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("OpenTypist/Autocorrect", isDirectory: true)
    }

    static var builtInListURL: URL? {
        let name = "wikipedia-common-misspellings"
        if let url = Bundle.main.url(forResource: name, withExtension: "txt", subdirectory: "autocorrect") { return url }
        // Running from .build (swift run / --selftest): use the repository copy.
        let repo = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        let url = repo.appendingPathComponent("Resources/autocorrect/\(name).txt")
        return FileManager.default.fileExists(atPath: url.path) ? url : nil
    }

    private(set) var builtInCount = 0
    private(set) var userListCounts: [(name: String, rules: Int)] = []

    func reloadRules() {
        let fresh = AutocorrectRules()
        builtInCount = Self.builtInListURL.map { fresh.load(fileAt: $0) } ?? 0
        let files = (try? FileManager.default.contentsOfDirectory(at: Self.userListsDirectory, includingPropertiesForKeys: nil)) ?? []
        userListCounts = files
            .filter { ["ahk", "txt"].contains($0.pathExtension.lowercased()) }
            .sorted { $0.lastPathComponent < $1.lastPathComponent }
            .map { ($0.lastPathComponent, fresh.load(fileAt: $0)) }
        rules = fresh
    }

    private(set) var rules = AutocorrectRules()

    @discardableResult
    func importList(from url: URL) throws -> Int {
        try FileManager.default.createDirectory(at: Self.userListsDirectory, withIntermediateDirectories: true)
        let destination = Self.userListsDirectory.appendingPathComponent(url.lastPathComponent)
        if FileManager.default.fileExists(atPath: destination.path) { try FileManager.default.removeItem(at: destination) }
        try FileManager.default.copyItem(at: url, to: destination)
        reloadRules()
        return userListCounts.first { $0.name == url.lastPathComponent }?.rules ?? 0
    }

    // MARK: - Keystrokes

    func reset() {
        typed = ""
        lastFix = nil
        pending = nil
    }

    /// The completion controller inserted accepted suggestion text.
    func noteInserted(_ text: String) {
        lastFix = nil
        pending = nil
        append(text)
    }

    /// Returns true to swallow the key (we replaced the text ourselves).
    /// Runs even with autocorrect off, so typing habits are still learned.
    func handleKeyDown(_ event: CGEvent, app: NSRunningApplication?) -> Bool {
        let bundleID = app?.bundleIdentifier
        guard settings.enabled, !settings.isDisabled(bundleID: bundleID), !inSignInField else {
            reset()
            return false
        }
        currentApp = (bundleID, app?.localizedName ?? bundleID ?? "")
        let keyCode = event.getIntegerValueField(.keyboardEventKeycode)
        let flags = event.flags
        if flags.contains(.maskCommand) || flags.contains(.maskControl) {
            reset()
            return false
        }

        switch keyCode {
        case 51: // Delete (backspace)
            if let fix = lastFix {
                undo(fix)
                return true
            }
            if !typed.isEmpty { typed.removeLast() }
            pending = nil
            return false
        case 36, 76: // Return, Enter
            return handleCharacter("\n", event: event)
        case 48: // Tab
            return handleCharacter("\t", event: event)
        case 53, 117, 115, 119, 116, 121, 123, 124, 125, 126: // Esc, forward delete, Home/End/PgUp/PgDn, arrows
            reset()
            return false
        default:
            let characters = event.typedCharacters
            guard characters.count == 1, let c = characters.first, !Self.isControl(c) else {
                reset()
                return false
            }
            return handleCharacter(c, event: event)
        }
    }

    private func handleCharacter(_ c: Character, event: CGEvent) -> Bool {
        lastFix = nil
        pending?.typedSince.append(c)

        if AutocorrectRules.isEndCharacter(c) {
            let startsSentence = Self.startsSentence(typed.dropLast(AutocorrectRules.lastWord(of: typed).count))
            let swallowed = wordEnded(with: c, event: event)
            if !swallowed { append(String(c)) }
            learnFromEnding(c, startsSentence: startsSentence)
            return swallowed
        }

        append(String(c))
        if settings.autocorrectEnabled, case .fix(let replacement, let length) = rules.matchImmediate(typed),
           !personal.isNeverCorrect(AutocorrectRules.lastWord(of: typed)) {
            // The app hasn't received `c` yet: delete the rest of the trigger.
            TextInserter.deleteBackward(length - 1)
            TextInserter.type(replacement)
            replaceTypedSuffix(length, with: replacement)
            recordFix()
            return true
        }
        return false
    }

    /// Feeds the word (as it ended up, after any instant fix) and, at the end
    /// of a line, the line to the learner. Only typed keys are ever used.
    private func learnFromEnding(_ ending: Character, startsSentence: Bool) {
        let before = String(typed.dropLast())
        let word = AutocorrectRules.lastWord(of: before)
        if !word.isEmpty {
            personal.observeWord(word, startsSentence: startsSentence, bundleID: currentApp.bundleID, appName: currentApp.name)
        }
        if ending == "\n", let line = before.split(separator: "\n", omittingEmptySubsequences: false).last {
            personal.observeLine(String(line))
        }
    }

    /// Returns true if the ending key was swallowed and replayed after a fix.
    private func wordEnded(with ending: Character, event: CGEvent) -> Bool {
        // Text replacements and snippets come first.
        if case .fix(let replacement, let length)? = TextReplacements.shared.match(typed) {
            applyFix(length: length, replacement: replacement, ending: ending, event: event)
            status.note("Expanded a text replacement")
            return true
        }

        let word = AutocorrectRules.lastWord(of: typed)
        guard !word.isEmpty, !personal.isNeverCorrect(word) else { return false }

        let match = settings.autocorrectEnabled ? rules.matchAtEnd(of: typed) : nil
        switch match {
        case .fix(let replacement, let length):
            let trigger = String(typed.suffix(length))
            // A list "typo" that is a real word in the user's languages
            // ("loosing", "payed") is only fixed if the model agrees.
            if trigger == word, SpellCheck.isValidWord(word) {
                status.note("Autocorrect: list entry is also a real word, asking the model")
                startModelCheck(word: word, candidates: [replacement], margin: Margin.realWord, ending: ending)
                return false
            }
            applyFix(length: length, replacement: replacement, ending: ending, event: event)
            recordFix()
            return true
        case .ambiguous(let word, let candidates):
            startModelCheck(word: word, candidates: candidates, margin: Margin.knownMisspelling, ending: ending)
        case nil:
            // Check the dictionary now (about a millisecond) so correctly spelled
            // words don't replace a check still running for an earlier typo.
            if settings.aiTypoFix, word.count >= 3, !SpellCheck.isValidWord(word) {
                startModelCheck(word: word, candidates: nil, margin: Margin.unknownWord, ending: ending)
            }
        }
        return false
    }

    /// Replaces the last `length` typed characters, then replays the ending
    /// key that was swallowed. Delete right afterwards undoes it.
    private func applyFix(length: Int, replacement: String, ending: Character, event: CGEvent) {
        let trigger = String(typed.suffix(length))
        TextInserter.deleteBackward(length)
        TextInserter.type(replacement)
        TextInserter.repost(event)
        replaceTypedSuffix(length, with: replacement)
        append(String(ending))
        lastFix = (trigger, replacement, String(ending))
        onTextChanged?()
    }

    private func undo(_ fix: (original: String, replacement: String, ending: String)) {
        lastFix = nil
        TextInserter.deleteBackward(fix.replacement.count + fix.ending.count)
        TextInserter.type(fix.original)
        replaceTypedSuffix(fix.replacement.count + fix.ending.count, with: fix.original)
        if let word = fix.original.split(whereSeparator: { AutocorrectRules.isEndCharacter($0) }).last {
            personal.addNeverCorrect(String(word))
        }
        status.note("Undid a correction; that word won't be corrected again")
        onTextChanged?()
    }

    // MARK: - Language-model check

    private func startModelCheck(word: String, candidates: [String]?, margin: Double, ending: Character) {
        guard settings.aiTypoFix, engine.isReady, word.count >= 3, !personal.isNeverCorrect(word),
              word.allSatisfy({ $0.isLetter || $0 == "'" }) else { return }
        // Leave alone ALL-CAPS words and, for dictionary checks, names.
        let letters = word.filter(\.isLetter)
        if letters.count > 1, letters.allSatisfy(\.isUppercase) { return }
        if candidates == nil, word.first?.isUppercase == true, !Self.startsSentence(typed.dropLast(word.count)) { return }

        nextCheckID += 1
        let check = PendingCheck(id: nextCheckID, word: word, ending: String(ending))
        pending = check

        // Run once the app has received the ending key.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.06) { [weak self] in
            self?.runModelCheck(check, candidates: candidates, margin: margin)
        }
    }

    private func runModelCheck(_ check: PendingCheck, candidates: [String]?, margin: Double) {
        guard pending?.id == check.id else { return }
        var options = candidates ?? SpellCheck.guesses(for: check.word)
        options.removeAll { $0 == check.word }
        guard !options.isEmpty, case .success(let ctx) = FocusReader.read() else { pending = nil; return }

        let tail = check.word + check.ending + (pending?.typedSince ?? "")
        guard ctx.textBefore.hasSuffix(tail) else {
            status.note("Autocorrect: the field changed before the model could check the word")
            pending = nil
            return
        }
        let request = EngineService.ScoreRequest(
            context: String(ctx.textBefore.dropLast(tail.count)),
            appName: settings.useAppContext ? ctx.appName : nil,
            windowTitle: settings.useAppContext ? ctx.windowTitle : nil,
            screenText: ScreenContext.shared.text(for: ctx),
            profile: Personalization.shared.promptProfile,
            examples: WritingSamples.shared.examples(bundleID: ctx.bundleID, windowTitle: ctx.windowTitle),
            candidates: [check.word] + options
        )
        // List entries that are real words are known mistakes; for everything
        // else, weigh how plausible each typo is.
        let weighTypos = margin != Margin.realWord
        engine.score(request) { [weak self] scores in
            guard let self, let scores, scores.count == options.count + 1 else { return }
            let original = scores[0]
            let adjusted = options.enumerated().map { index, option in
                scores[index + 1] - (weighTypos ? Self.typoCostWeight * SpellCheck.typoCost(typed: check.word, intended: option) : 0)
            }
            guard let best = adjusted.enumerated().max(by: { $0.element < $1.element }),
                  best.element - original > margin else {
                self.status.note("Autocorrect: the model kept the typed word")
                if self.pending?.id == check.id { self.pending = nil }
                return
            }
            self.applyLateFix(check.id, replacement: options[best.offset])
        }
    }

    /// Replaces a word the user has already moved past, re-typing whatever
    /// they typed after it.
    private func applyLateFix(_ id: Int, replacement: String) {
        guard let check = pending, check.id == id else { return }
        pending = nil
        let tail = check.ending + check.typedSince
        // Make sure the field still holds exactly what we expect.
        guard case .success(let ctx) = FocusReader.read(), ctx.textBefore.hasSuffix(check.word + tail) else {
            status.note("Autocorrect: the field changed before the fix could be applied")
            return
        }
        TextInserter.deleteBackward(check.word.count + tail.count)
        TextInserter.type(replacement + tail)
        replaceTypedSuffix(check.word.count + tail.count, with: replacement + tail)
        if check.typedSince.isEmpty { lastFix = (check.word, replacement, check.ending) }
        // It was a typo, not one of the user's own words.
        personal.forgetObservation(of: check.word)
        recordFix(byModel: true)
    }

    // MARK: - Helpers

    private func recordFix(byModel: Bool = false) {
        stats.recordCorrection()
        status.note(byModel ? "Fixed a typo using the language model" : "Fixed a misspelling")
        onTextChanged?()
    }

    private func append(_ text: String) {
        typed += text
        if typed.count > Self.maxTyped { typed = String(typed.suffix(Self.maxTyped)) }
    }

    private func replaceTypedSuffix(_ count: Int, with text: String) {
        typed = String(typed.dropLast(count)) + text
    }

    private static func isControl(_ c: Character) -> Bool {
        guard let scalar = c.unicodeScalars.first else { return true }
        // C0 controls and the private-use range macOS uses for function keys.
        return scalar.value < 0x20 || scalar.value == 0x7F || (0xF700...0xF8FF).contains(scalar.value)
    }

    private static func startsSentence(_ before: Substring) -> Bool {
        let trimmed = before.trimmingCharacters(in: .whitespaces)
        guard let last = trimmed.last else { return true }
        return ".!?\n".contains(last)
    }
}

/// The macOS spell checker, limited to the user's own languages.
enum SpellCheck {
    static let languages: [String] = {
        let available = Set(NSSpellChecker.shared.availableLanguages)
        var result: [String] = []
        for identifier in Locale.preferredLanguages {
            let underscored = identifier.replacingOccurrences(of: "-", with: "_")
            let base = String(underscored.prefix(while: { $0 != "_" }))
            if let match = [underscored, base].first(where: available.contains), !result.contains(match) {
                result.append(match)
            }
        }
        return result.isEmpty ? ["en"] : result
    }()

    static func isValidWord(_ word: String) -> Bool {
        let checker = NSSpellChecker.shared
        if checker.hasLearnedWord(word) { return true }
        for language in languages {
            var count = 0
            let range = checker.checkSpelling(of: word, startingAt: 0, language: language, wrap: false,
                                              inSpellDocumentWithTag: 0, wordCount: &count)
            if range.location == NSNotFound { return true }
        }
        return false
    }

    /// Up to four single-word suggestions close to `word`.
    static func guesses(for word: String) -> [String] {
        let checker = NSSpellChecker.shared
        let range = NSRange(location: 0, length: (word as NSString).length)
        var options: [String] = []
        if let correction = checker.correction(forWordRange: range, in: word, language: languages[0], inSpellDocumentWithTag: 0) {
            options.append(correction)
        }
        options += checker.guesses(forWordRange: range, in: word, language: languages[0], inSpellDocumentWithTag: 0) ?? []
        var seen = Set<String>()
        return options.filter { option in
            !option.contains(" ") && editDistance(option.lowercased(), word.lowercased()) <= 2 && seen.insert(option).inserted
        }
        .prefix(4)
        .map { AutocorrectRules.conformCase(typed: word, replacement: $0) }
    }

    private static let keyboardRows = ["qwertyuiop", "asdfghjkl", "zxcvbnm"].map(Array.init)

    /// Whether two letters sit next to each other on a QWERTY keyboard.
    static func adjacentKeys(_ a: Character, _ b: Character) -> Bool {
        func position(_ c: Character) -> (row: Int, column: Int)? {
            for (row, keys) in keyboardRows.enumerated() {
                if let column = keys.firstIndex(of: c) { return (row, column) }
            }
            return nil
        }
        guard let p = position(a), let q = position(b) else { return false }
        if p.row == q.row { return abs(p.column - q.column) == 1 }
        // Rows are staggered: a key touches the same column and one to the side.
        if q.row == p.row + 1 { return q.column == p.column || q.column == p.column - 1 }
        if q.row == p.row - 1 { return q.column == p.column || q.column == p.column + 1 }
        return false
    }

    /// How costly it is to have typed `typed` when meaning `intended`: swapped
    /// letters, missed or doubled letters and neighbouring keys are cheap,
    /// other substitutions expensive.
    static func typoCost(typed: String, intended: String) -> Double {
        let a = Array(intended.lowercased()), b = Array(typed.lowercased())
        if a.isEmpty || b.isEmpty { return Double(max(a.count, b.count)) }
        var d = [[Double]](repeating: [Double](repeating: 0, count: b.count + 1), count: a.count + 1)
        for i in 0...a.count { d[i][0] = Double(i) * 0.8 }
        for j in 0...b.count { d[0][j] = Double(j) * 0.8 }
        for i in 1...a.count {
            for j in 1...b.count {
                let substitution = a[i - 1] == b[j - 1] ? 0 : (adjacentKeys(a[i - 1], b[j - 1]) ? 0.8 : 1.5)
                d[i][j] = min(d[i - 1][j] + 0.8, d[i][j - 1] + 0.8, d[i - 1][j - 1] + substitution)
                if i > 1, j > 1, a[i - 1] == b[j - 2], a[i - 2] == b[j - 1] {
                    d[i][j] = min(d[i][j], d[i - 2][j - 2] + 0.6)
                }
            }
        }
        return d[a.count][b.count]
    }

    /// Optimal string alignment distance (transpositions count as one edit).
    static func editDistance(_ a: String, _ b: String) -> Int {
        let a = Array(a), b = Array(b)
        if a.isEmpty { return b.count }
        if b.isEmpty { return a.count }
        var d = [[Int]](repeating: [Int](repeating: 0, count: b.count + 1), count: a.count + 1)
        for i in 0...a.count { d[i][0] = i }
        for j in 0...b.count { d[0][j] = j }
        for i in 1...a.count {
            for j in 1...b.count {
                let cost = a[i - 1] == b[j - 1] ? 0 : 1
                d[i][j] = min(d[i - 1][j] + 1, d[i][j - 1] + 1, d[i - 1][j - 1] + cost)
                if i > 1, j > 1, a[i - 1] == b[j - 2], a[i - 2] == b[j - 1] {
                    d[i][j] = min(d[i][j], d[i - 2][j - 2] + 1)
                }
            }
        }
        return d[a.count][b.count]
    }
}
