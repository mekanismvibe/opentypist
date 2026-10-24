import Foundation

/// One misspelling → correction rule, with AutoHotkey hotstring semantics.
struct AutocorrectRule {
    /// Lowercased unless `caseSensitive`.
    let trigger: String
    /// Empty replacement = exception: the trigger is a real word that other
    /// rules must leave alone (AHK uses `::design::` to protect it from `:?:ign::ing`).
    let replacement: String
    let caseSensitive: Bool
    /// AHK `*`: fires as soon as the trigger is typed, without an ending character.
    let immediate: Bool
    /// AHK `?`: may match the end of a longer word.
    let insideWords: Bool
}

/// What to do when the user finishes typing some text.
enum AutocorrectMatch: Equatable {
    /// Replace the last `length` typed characters with `replacement`.
    case fix(replacement: String, length: Int)
    /// Several plausible corrections (or the "misspelling" is also a real
    /// word): let the language model pick using the surrounding text.
    case ambiguous(word: String, candidates: [String])
}

/// All loaded autocorrect rules, and matching against recently typed text.
final class AutocorrectRules {
    private var wordRules: [String: AutocorrectRule] = [:]
    private var caseSensitiveWordRules: [String: AutocorrectRule] = [:]
    /// Rules whose trigger contains spaces or punctuation, or that match inside words.
    private var otherRules: [AutocorrectRule] = []
    private var immediateRules: [AutocorrectRule] = []
    private var exceptions: Set<String> = []
    private var ambiguous: [String: [String]] = [:]

    private(set) var ruleCount = 0

    /// Characters that end a word and trigger a check (AutoHotkey's defaults,
    /// minus the apostrophe so contractions stay one word).
    static let endCharacters: Set<Character> = Set(" \t\n\r-()[]{}:;\"/\\,.?!")

    static func isEndCharacter(_ c: Character) -> Bool { endCharacters.contains(c) }

    // MARK: - Loading

    func add(_ rule: AutocorrectRule) {
        guard !rule.trigger.isEmpty else { return }
        if rule.replacement.isEmpty {
            exceptions.insert(rule.trigger.lowercased())
            return
        }
        ruleCount += 1
        if rule.immediate {
            immediateRules.append(rule)
        } else if rule.insideWords || rule.trigger.contains(where: { Self.isEndCharacter($0) }) {
            otherRules.append(rule)
        } else if rule.caseSensitive {
            caseSensitiveWordRules[rule.trigger] = rule
        } else {
            wordRules[rule.trigger] = rule
        }
    }

    /// `misspelling->correction` or `misspelling->option1, option2` per line;
    /// `#` starts a comment.
    @discardableResult
    func loadArrowList(_ text: String) -> Int {
        var added = 0
        for rawLine in text.split(whereSeparator: \.isNewline) {
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            guard !line.hasPrefix("#"), let arrow = line.range(of: "->") else { continue }
            let typo = line[..<arrow.lowerBound].trimmingCharacters(in: .whitespaces).lowercased()
            let options = line[arrow.upperBound...].split(separator: ",")
                .map { $0.trimmingCharacters(in: .whitespaces) }
                .filter { !$0.isEmpty }
            guard !typo.isEmpty, !options.isEmpty else { continue }
            if options.count == 1 {
                add(AutocorrectRule(trigger: typo, replacement: options[0], caseSensitive: false, immediate: false, insideWords: false))
            } else {
                ambiguous[typo] = options
                ruleCount += 1
            }
            added += 1
        }
        return added
    }

    /// AutoHotkey hotstrings: `:options:trigger::replacement`. Supports the
    /// `*`, `?`, `C` and `B0` options, `#Hotstring` defaults, `;` comments and
    /// `/* */` blocks. Lines that are code rather than hotstrings are skipped.
    @discardableResult
    func loadAutoHotkey(_ text: String) -> Int {
        var added = 0
        var inBlockComment = false
        var defaultNoBackspace = false
        for rawLine in text.components(separatedBy: .newlines) {
            let line = rawLine.trimmingCharacters(in: .whitespaces)
            if inBlockComment {
                if line.hasPrefix("*/") { inBlockComment = false }
                continue
            }
            if line.hasPrefix("/*") { inBlockComment = true; continue }
            if line.lowercased().hasPrefix("#hotstring") {
                let options = line.dropFirst("#hotstring".count).uppercased()
                if options.contains("B0") { defaultNoBackspace = true } else if options.contains("B") { defaultNoBackspace = false }
                continue
            }
            guard line.hasPrefix(":") else { continue }
            let afterColon = line.dropFirst()
            guard let optionsEnd = afterColon.firstIndex(of: ":") else { continue }
            let options = String(afterColon[..<optionsEnd])
            guard options.allSatisfy({ "*?CcBbOoRr0123456789".contains($0) }) else { continue }
            let body = afterColon[afterColon.index(after: optionsEnd)...]
            guard let separator = body.range(of: "::") else { continue }
            let trigger = String(body[..<separator.lowerBound])
            var replacement = String(body[separator.upperBound...])
            // Comments need whitespace before the semicolon.
            if let comment = replacement.range(of: " ;") ?? replacement.range(of: "\t;") {
                replacement = String(replacement[..<comment.lowerBound])
            }
            replacement = replacement.trimmingCharacters(in: CharacterSet(charactersIn: "\t"))
            while replacement.hasSuffix(" ") && !trigger.hasSuffix(" ") { replacement.removeLast() }
            // Special-key and escape syntax is out of scope.
            if replacement.contains("{") || replacement.contains("`") { continue }

            let upper = options.uppercased()
            let noBackspace = upper.contains("B0") || (defaultNoBackspace && !upper.contains("B"))
            if noBackspace && !replacement.isEmpty { continue }
            let caseSensitive = options.contains("C") && !upper.contains("C0") && !upper.contains("C1")
            add(AutocorrectRule(
                trigger: caseSensitive ? trigger : trigger.lowercased(),
                replacement: replacement,
                caseSensitive: caseSensitive,
                immediate: options.contains("*"),
                insideWords: options.contains("?")
            ))
            added += 1
        }
        return added
    }

    @discardableResult
    func load(fileAt url: URL) -> Int {
        guard let data = try? Data(contentsOf: url) else { return 0 }
        // AutoHotkey scripts are often Windows-1252.
        let text = String(data: data, encoding: .utf8) ?? String(data: data, encoding: .windowsCP1252) ?? ""
        return url.pathExtension.lowercased() == "ahk" ? loadAutoHotkey(text) : loadArrowList(text)
    }

    // MARK: - Matching

    /// Called when an ending character is typed. `typed` is the recently typed
    /// text (since the last caret jump), without that ending character.
    func matchAtEnd(of typed: String) -> AutocorrectMatch? {
        let word = Self.lastWord(of: typed)
        guard !word.isEmpty else { return nil }
        let lower = word.lowercased()
        if exceptions.contains(lower) { return nil }

        // Longer, more specific triggers first.
        for rule in otherRules.sorted(by: { $0.trigger.count > $1.trigger.count }) {
            guard let length = matchLength(rule, in: typed) else { continue }
            let typedTrigger = String(typed.suffix(length))
            return .fix(replacement: replacement(for: rule, typed: typedTrigger), length: length)
        }
        if let rule = caseSensitiveWordRules[word] {
            return .fix(replacement: rule.replacement, length: word.count)
        }
        if let rule = wordRules[lower] {
            return .fix(replacement: Self.conformCase(typed: word, replacement: rule.replacement), length: word.count)
        }
        if let options = ambiguous[lower] {
            return .ambiguous(word: word, candidates: options.map { Self.conformCase(typed: word, replacement: $0) })
        }
        return nil
    }

    /// Called after each word character for AHK `*` rules.
    func matchImmediate(_ typed: String) -> AutocorrectMatch? {
        guard !immediateRules.isEmpty, !exceptions.contains(Self.lastWord(of: typed).lowercased()) else { return nil }
        for rule in immediateRules {
            guard let length = matchLength(rule, in: typed) else { continue }
            let typedTrigger = String(typed.suffix(length))
            return .fix(replacement: replacement(for: rule, typed: typedTrigger), length: length)
        }
        return nil
    }

    /// Single-word fix for `word`, if any (used to recognise list entries
    /// that are also real words).
    func plainCorrection(for word: String) -> String? {
        wordRules[word.lowercased()]?.replacement
    }

    private func matchLength(_ rule: AutocorrectRule, in typed: String) -> Int? {
        let candidate = rule.caseSensitive ? typed : typed.lowercased()
        guard candidate.hasSuffix(rule.trigger) else { return nil }
        let length = rule.trigger.count
        if !rule.insideWords {
            // Must start a word: preceded by an ending character or the start of what we saw.
            let before = typed.dropLast(length)
            if let previous = before.last, !Self.isEndCharacter(previous) { return nil }
        }
        return length
    }

    private func replacement(for rule: AutocorrectRule, typed: String) -> String {
        rule.caseSensitive ? rule.replacement : Self.conformCase(typed: typed, replacement: rule.replacement)
    }

    static func lastWord(of typed: String) -> String {
        String(typed.reversed().prefix(while: { !isEndCharacter($0) }).reversed())
    }

    /// AutoHotkey's case conformance: an all-caps trigger gives an all-caps
    /// replacement, a capitalized trigger a capitalized one.
    static func conformCase(typed: String, replacement: String) -> String {
        let letters = typed.filter(\.isLetter)
        guard let first = letters.first else { return replacement }
        if letters.count > 1, letters.allSatisfy(\.isUppercase) { return replacement.uppercased() }
        if first.isUppercase, let r = replacement.first {
            return r.uppercased() + replacement.dropFirst()
        }
        return replacement
    }
}
