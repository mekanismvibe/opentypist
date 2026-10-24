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
        let chars = Array(text)
        for k in stride(from: chars.count, through: 0, by: -1) {
            if textBefore.hasSuffix(anchorTail + String(chars[0..<k])) { return k }
        }
        return nil
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
        var healLastToken: Bool
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
        let heal = !stripped && (body.last?.isLetter ?? false)

        let header = header(appName: appName, windowTitle: windowTitle, screenText: screenText, profile: profile, examples: examples)
        return Prompt(text: header + body, strippedTrailingSpace: stripped, healLastToken: heal)
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

    /// Word count of the model output used as an early stop signal.
    static func shouldStop(_ text: String, maxWords: Int) -> Bool {
        if text.contains(where: { $0.isNewline }) { return true }
        // One extra word means the last kept word is complete.
        return splitWords(text).count > maxWords
    }
}
