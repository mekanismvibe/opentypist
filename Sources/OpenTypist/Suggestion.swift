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

    static func build(textBefore: String, appName: String?, windowTitle: String?, screenText: String? = nil) -> Prompt {
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

        // Context goes first so it stays a stable, cached prefix while the user types.
        var header = ""
        if let appName, !appName.isEmpty {
            header = "Application: \(appName)\n"
            if let windowTitle, !windowTitle.isEmpty, windowTitle != appName {
                header += "Window: \(String(windowTitle.prefix(120)))\n"
            }
        }
        if let screenText, !screenText.isEmpty {
            header += "Visible on screen:\n\(screenText)\n\n"
        }
        if !header.isEmpty { header += "Text being written:\n" }
        return Prompt(text: header + body, strippedTrailingSpace: stripped, healLastToken: heal)
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
