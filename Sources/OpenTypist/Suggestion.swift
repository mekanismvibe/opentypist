import Foundation

/// A completion shown to the user, anchored to the text that was before the
/// caret when it was generated.
struct Suggestion {
    /// Tail of the text before the caret at generation time.
    let anchorTail: String
    /// Full suggested continuation.
    let text: String
    let pid: pid_t

    init(textBefore: String, text: String, pid: pid_t) {
        self.anchorTail = String(textBefore.suffix(48))
        self.text = text
        self.pid = pid
    }

    /// How many characters of the suggestion the user has already typed (or
    /// accepted), given the new text before the caret. nil when the text no
    /// longer matches, e.g. after a typo or a caret move.
    func consumedCount(textBefore: String) -> Int? {
        let chars = Array(Self.normalizingQuotes(text))
        let before = Self.normalizingQuotes(textBefore)
        let anchor = Self.normalizingQuotes(anchorTail)
        for k in stride(from: chars.count, through: 0, by: -1) {
            if before.hasSuffix(anchor + String(chars[0..<k])) { return k }
        }
        return nil
    }

    /// Curly quotes as straight ones: apps with smart quotes turn the
    /// "I'm" we typed into "I’m".
    static func normalizingQuotes(_ text: String) -> String {
        String(text.map { c -> Character in
            switch c {
            case "\u{2018}", "\u{2019}": return "'"
            case "\u{201C}", "\u{201D}": return "\""
            default: return c
            }
        })
    }

    func remaining(textBefore: String) -> String? {
        guard let k = consumedCount(textBefore: textBefore) else { return nil }
        let rest = String(text.dropFirst(k))
        return rest.trimmingCharacters(in: .whitespaces).isEmpty ? nil : rest
    }

    /// The next word of `remaining`, including its leading whitespace and,
    /// optionally, the single space after it.
    static func nextWord(of remaining: String, includeTrailingSpace: Bool) -> String {
        var result = ""
        var iterator = remaining.makeIterator()
        var sawWord = false
        while let c = iterator.next() {
            if c.isWhitespace {
                if sawWord {
                    if includeTrailingSpace, c == " " { result.append(c) }
                    break
                }
                result.append(c)
            } else {
                sawWord = true
                result.append(c)
            }
        }
        return result
    }
}

/// Builds the model prompt and cleans up raw model output.
enum PromptBuilder {
    struct Prompt {
        var text: String
        /// The user's trailing space was removed from the prompt; the model
        /// output must then start with a space, which is dropped again.
        var strippedTrailingSpace: Bool
        /// The half-typed word at the caret, with the space before it, kept
        /// out of `text`: the model's output must start with it (see
        /// `LlamaEngine.complete`). Empty when the caret isn't mid-word.
        var wordPrefix: String
    }

    static let maxContextCharacters = 1200

    static func build(textBefore: String, appName: String?, windowTitle: String?, screenText: String? = nil,
                      profile: String? = nil, examples: [String] = []) -> Prompt {
        var body = String(textBefore.suffix(maxContextCharacters))
        // Don't start the prompt in the middle of a word.
        if body.count < textBefore.count, let space = body.firstIndex(where: { $0.isWhitespace }) {
            body = String(body[body.index(after: space)...])
        }

        var stripped = false
        while let last = body.last, last == " " || last == "\t" {
            body.removeLast()
            stripped = true
        }
        var wordPrefix = ""
        if !stripped, body.last?.isLetter == true {
            // An apostrophe is inside a word: "I’m" is whole, not "I’" + "m".
            let wordStart = body.lastIndex(where: { !$0.isLetter && $0 != "'" && $0 != "\u{2019}" })
                .map { body.index(after: $0) } ?? body.startIndex
            wordPrefix = String(body[wordStart...])
            body = String(body[..<wordStart])
            // The space before a word belongs to the word's token.
            if body.last == " " {
                body.removeLast()
                wordPrefix = " " + wordPrefix
            }
        }

        let header = header(appName: appName, windowTitle: windowTitle, screenText: screenText, profile: profile, examples: examples)
        return Prompt(text: header + body, strippedTrailingSpace: stripped, wordPrefix: wordPrefix)
    }

    /// Context goes first so it stays a stable, cached prefix while the user
    /// types, ordered from least to most often changing: the profile, then
    /// writing examples for this app, then the app, window and screen.
    static func header(appName: String?, windowTitle: String?, screenText: String?,
                       profile: String? = nil, examples: [String] = []) -> String {
        var header = ""
        if let profile, !profile.isEmpty {
            header += "About me:\n\(profile)\n\n"
        }
        if !examples.isEmpty {
            let place = appName.map { " in \($0)" } ?? ""
            header += "Examples of things I've written\(place):\n"
            header += examples.map { "\"\($0)\"" }.joined(separator: "\n") + "\n\n"
        }
        if let appName, !appName.isEmpty {
            header += "Application: \(appName)\n"
            if let windowTitle, !windowTitle.isEmpty, windowTitle != appName {
                header += "Window: \(String(windowTitle.prefix(120)))\n"
            }
        }
        if let screenText, !screenText.isEmpty {
            header += "Visible on screen:\n\(screenText)\n\n"
        }
        if !header.isEmpty { header += "Text being written:\n" }
        return header
    }

    /// Prompt for scoring candidate words that come right after `context`.
    /// Returns the prompt and the prefix to put before each candidate: the
    /// space before a word belongs to the word's token, so it moves over.
    static func scoringPrompt(context: String, appName: String?, windowTitle: String?, screenText: String?,
                              profile: String? = nil, examples: [String] = []) -> (text: String, wordPrefix: String) {
        var body = String(context.suffix(maxContextCharacters))
        if body.count < context.count, let space = body.firstIndex(where: { $0.isWhitespace }) {
            body = String(body[body.index(after: space)...])
        }
        var prefix = ""
        while let last = body.last, last == " " || last == "\t" {
            body.removeLast()
            prefix = " "
        }
        return (header(appName: appName, windowTitle: windowTitle, screenText: screenText, profile: profile, examples: examples) + body, prefix)
    }

    /// Turns raw model output into the suggestion to display, or nil if it is
    /// not worth showing.
    static func clean(_ raw: String, prompt: Prompt, maxWords: Int, hitTokenLimit: Bool) -> String? {
        var text = raw
        if let newline = text.firstIndex(where: { $0.isNewline }) {
            text = String(text[..<newline])
        }
        if prompt.strippedTrailingSpace {
            // The user already typed the space, so the model must start a new word.
            guard text.first == " " else { return nil }
            text.removeFirst()
            if text.first?.isWhitespace == true { return nil }
        }

        var words = splitWords(text)
        // Stopped by the token limit: the last word may be cut off.
        if hitTokenLimit, words.count > 1 { words.removeLast() }
        if words.count > maxWords { words = Array(words.prefix(maxWords)) }
        text = words.joined()
        while text.last?.isWhitespace == true { text.removeLast() }

        guard text.contains(where: { $0.isLetter || $0.isNumber }) else { return nil }
        return text
    }

    /// The part of the generated text the model was confident about: tokens
    /// up to the first one below `minProbability`. If that token would have
    /// continued the current word, the half-finished word goes too.
    /// Pieces that finish a half-typed word use the looser
    /// `wordMinProbability`, so the word gets completed even when the words
    /// after it are too uncertain to show.
    static func confidentText(_ tokens: [LlamaEngine.GeneratedToken], hitTokenLimit: Bool, minProbability: Float,
                              wordMinProbability: Float? = nil) -> (text: String, hitTokenLimit: Bool) {
        let cutIndex = tokens.firstIndex { token in
            token.probability < (token.finishesWord ? (wordMinProbability ?? minProbability) : minProbability)
        }
        guard let cut = cutIndex else {
            return (String(decoding: tokens.flatMap(\.bytes), as: UTF8.self), hitTokenLimit)
        }
        var text = String(decoding: tokens[..<cut].flatMap(\.bytes), as: UTF8.self)
        let startsNewWord = tokens[cut].bytes.isEmpty || tokens[cut].startsNewWord
        if !startsNewWord {
            if let space = text.lastIndex(where: { $0.isWhitespace }), text[..<space].contains(where: { !$0.isWhitespace }) {
                text = String(text[..<space])
            } else {
                text = ""
            }
        }
        return (text, false)
    }

    /// Mid-line, the suggestion must lead into the text after the caret, not
    /// repeat it: "terested in this job" before " this job" becomes
    /// "terested in". Returns nil if nothing is left to insert.
    static func fitBefore(_ after: String, suggestion: String) -> String? {
        func normalized(_ s: String) -> [String] {
            s.lowercased().split(whereSeparator: { $0.isWhitespace })
                .map { $0.trimmingCharacters(in: .punctuationCharacters) }
                .filter { !$0.isEmpty }
        }
        let afterWords = normalized(after)
        var result = suggestion
        if !afterWords.isEmpty {
            let probe = Array(afterWords.prefix(2))
            let words = splitWords(suggestion)
            for i in words.indices {
                let rest = normalized(words[i...].joined())
                if rest.starts(with: probe) || (probe.count == 2 && rest == [probe[0]]) {
                    result = words[..<i].joined()
                    break
                }
            }
        }
        // Punctuation right after the caret is already there.
        if let next = after.trimmingCharacters(in: .whitespaces).first, next.isPunctuation {
            while let last = result.last, last.isPunctuation { result.removeLast() }
        }
        while result.last?.isWhitespace == true { result.removeLast() }
        return result.contains(where: { $0.isLetter || $0.isNumber }) ? result : nil
    }

    /// Whether a suggestion that finishes the half-typed word before the caret
    /// makes a real word ("tomor" + "row" yes, "tomor" + "ow" no). Suggestions
    /// that start a new word, short words and capitalized ones (often names
    /// the dictionary doesn't know) always pass.
    static func makesRealWord(textBefore: String, suggestion: String, isKnown: (String) -> Bool) -> Bool {
        guard textBefore.last?.isLetter == true, suggestion.first?.isLetter == true else { return true }
        let typed = String(textBefore.reversed().prefix(while: \.isLetter).reversed())
        let word = typed + String(suggestion.prefix(while: \.isLetter))
        guard word.count >= 4, word.first?.isLowercase == true else { return true }
        return isKnown(word)
    }

    /// Why a suggestion the model was sure of is still not worth showing
    /// (phrased to follow "a suggestion that"), or nil if it's fine.
    static func junkReason(_ suggestion: String, textBefore: String) -> String? {
        let words = plainWords(suggestion)
        // The model stuck in a loop: "Hi S. Hi S. Hi S."
        for period in 1...3 where words.count >= period * 3 {
            var run = 0
            for i in period..<words.count {
                run = words[i] == words[i - period] ? run + 1 : 0
                if run >= period * 2 { return "repeats itself" }
            }
        }
        // Going back over what was just typed instead of continuing it.
        let recent = plainWords(String(textBefore.suffix(160)))
        if recent.count >= 4, words.count >= 4 {
            let tail = Array(recent.suffix(4))
            if (0...(words.count - 4)).contains(where: { Array(words[$0..<($0 + 4)]) == tail }) {
                return "repeats what was just typed"
            }
        }
        // Leftovers from the chat logs and web pages the model learned from.
        let trimmed = suggestion.trimmingCharacters(in: .whitespaces)
        if trimmed.range(of: #"^\[/?[A-Za-z][A-Za-z0-9 _-]{0,24}\]"#, options: .regularExpression) != nil {
            return "starts with a chat or forum tag"
        }
        if !textBefore.contains("<"), trimmed.range(of: #"</?[A-Za-z][A-Za-z0-9]*(\s[^<>]*)?>"#, options: .regularExpression) != nil {
            return "contains HTML"
        }
        let lower = trimmed.lowercased()
        if ["as an ai", "as a language model"].contains(where: lower.contains) {
            return "talks about being an AI"
        }
        return nil
    }

    /// Lowercased words without punctuation, for comparing text.
    private static func plainWords(_ text: String) -> [String] {
        text.lowercased().split { !$0.isLetter && !$0.isNumber && $0 != "'" }.map(String.init)
    }

    /// Splits into words, each keeping its leading whitespace.
    static func splitWords(_ text: String) -> [String] {
        var words: [String] = []
        var current = ""
        var inWord = false
        for c in text {
            if c.isWhitespace {
                if inWord {
                    words.append(current)
                    current = ""
                    inWord = false
                }
                current.append(c)
            } else {
                inWord = true
                current.append(c)
            }
        }
        if inWord { words.append(current) }
        return words
    }

    /// The beginning of `text` up to the end of its first complete word, once
    /// that word has ended (a later word or punctuation has begun), cleaned
    /// like a final suggestion. nil while the first word may still grow.
    static func firstCompleteWord(in text: String, prompt: Prompt) -> String? {
        let words = splitWords(text)
        // The first word is complete once a second one has started, or once
        // the text after it has punctuation or whitespace.
        guard words.count >= 2 || (words.count == 1 && text.last.map { $0.isWhitespace || $0.isPunctuation } == true) else {
            return nil
        }
        return clean(words[0], prompt: prompt, maxWords: 1, hitTokenLimit: false)
    }

    /// What to show while a suggestion is still being generated: only the
    /// words that are complete (a half-generated word may still change),
    /// cleaned like a final suggestion. Every token generated so far already
    /// passed the confidence filter.
    static func streamingText(_ text: String, prompt: Prompt, maxWords: Int, textAfter: String) -> String? {
        var words = splitWords(text)
        if let last = text.last, !last.isWhitespace, !last.isPunctuation, !words.isEmpty { words.removeLast() }
        guard !words.isEmpty, var shown = clean(words.joined(), prompt: prompt, maxWords: maxWords, hitTokenLimit: false) else {
            return nil
        }
        if !textAfter.isEmpty {
            guard let fitted = fitBefore(textAfter, suggestion: shown) else { return nil }
            shown = fitted
        }
        return shown
    }

    /// Word count of the model output used as an early stop signal.
    static func shouldStop(_ text: String, maxWords: Int) -> Bool {
        if text.contains(where: { $0.isNewline }) { return true }
        // One extra word means the last kept word is complete.
        return splitWords(text).count > maxWords
    }
}
