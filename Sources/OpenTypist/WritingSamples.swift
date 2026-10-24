import AppKit
import Combine

struct WritingSample: Codable, Identifiable, Equatable {
    var id = UUID()
    var date: Date
    var bundleID: String?
    var appName: String
    var windowTitle: String?
    var text: String
}

/// Opt-in samples of what the user wrote, used as style examples in prompts.
///
/// A sample is taken when the user leaves a text field, or when the field is
/// cleared (a chat message was sent). Only text from fields OpenTypist was
/// already reading is kept; disabled apps and password fields never are.
/// Stored encrypted on this Mac (see `SecureStore`), at most 400 samples.
final class WritingSamples: ObservableObject {
    static let shared = WritingSamples(store: .shared)

    static let maxSamples = 400
    static let minLength = 40
    static let maxLength = 600
    /// Characters of examples to put in a prompt.
    static let promptBudget = 600

    @Published private(set) var samples: [WritingSample]

    private let store: SecureStore
    private static let fileName = "samples.bin"

    /// The field being typed in and its latest text.
    private var current: (key: String, sample: WritingSample)?
    private var exampleCache: (key: String, examples: [String])?

    init(store: SecureStore) {
        self.store = store
        samples = store.load([WritingSample].self, from: Self.fileName) ?? []
    }

    var isEnabled: Bool { Settings.shared.keepWritingSamples }

    // MARK: - Capture

    /// Called whenever OpenTypist reads the focused field.
    func observe(_ context: FocusContext) {
        guard isEnabled else { current = nil; return }
        let key = "\(context.pid):\(CFHash(context.element))"
        let sample = WritingSample(date: Date(), bundleID: context.bundleID, appName: context.appName,
                                   windowTitle: context.windowTitle, text: context.textBefore)
        if let current, current.key == key {
            // Field cleared or replaced, e.g. a chat message was sent.
            let old = current.sample.text
            if sample.text.count < old.count / 2, !old.hasPrefix(sample.text) { commit(current.sample) }
        } else if let current {
            commit(current.sample)
        }
        current = (key, sample)
    }

    /// Keeps what was typed in the current field (on app switch or quit).
    func flush() {
        if let current { commit(current.sample) }
        current = nil
    }

    private func commit(_ sample: WritingSample) {
        guard isEnabled, let text = Self.clean(sample.text) else { return }
        var cleaned = sample
        cleaned.text = text
        // A draft continued later replaces its earlier, shorter sample.
        if let last = samples.last, last.bundleID == sample.bundleID, last.windowTitle == sample.windowTitle,
           text.hasPrefix(String(last.text.prefix(Self.minLength))) {
            samples[samples.count - 1] = cleaned
        } else if samples.contains(where: { $0.text == text }) {
            return
        } else {
            samples.append(cleaned)
        }
        if samples.count > Self.maxSamples { samples.removeFirst(samples.count - Self.maxSamples) }
        save()
    }

    /// Adds a sample directly (used by the self-test).
    func record(_ sample: WritingSample) {
        commit(sample)
    }

    /// Drops things that look like secrets or code, trims to the most recent text.
    static func clean(_ text: String) -> String? {
        var text = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard text.count >= minLength else { return nil }
        // Card-like digit runs and long unbroken tokens (keys, URLs) are removed.
        text = text.replacingOccurrences(of: #"\b(?:\d[ -]?){13,19}\b"#, with: "…", options: .regularExpression)
        text = text.replacingOccurrences(of: #"\S{30,}"#, with: "…", options: .regularExpression)
        let letters = text.filter(\.isLetter).count
        guard Double(letters) / Double(max(text.count, 1)) >= 0.5 else { return nil }
        if text.count > maxLength {
            text = String(text.suffix(maxLength))
            if let space = text.firstIndex(of: " ") { text = String(text[text.index(after: space)...]) }
        }
        return text
    }

    // MARK: - Use

    /// A few samples in the user's voice for this app, most relevant first.
    /// Stable for the same app and window, so the prompt prefix stays cached.
    func examples(bundleID: String?, windowTitle: String?) -> [String] {
        guard isEnabled, !samples.isEmpty else { return [] }
        let key = "\(bundleID ?? ""):\(windowTitle ?? ""):\(samples.count)"
        if let exampleCache, exampleCache.key == key { return exampleCache.examples }

        let sameWindow = samples.reversed().filter { $0.bundleID == bundleID && $0.windowTitle == windowTitle && windowTitle != nil }
        let sameApp = samples.reversed().filter { $0.bundleID == bundleID && !sameWindow.contains($0) }
        var pool = sameWindow + sameApp
        if pool.isEmpty { pool = Array(samples.reversed().prefix(2)) }

        var result: [String] = []
        var used = 0
        for sample in pool.prefix(3) {
            var text = sample.text
            if text.count > 220 {
                text = String(text.prefix(220))
                if let space = text.lastIndex(of: " ") { text = String(text[..<space]) }
                text += "…"
            }
            if used + text.count > Self.promptBudget { break }
            result.append(text)
            used += text.count
        }
        exampleCache = (key, result)
        return result
    }

    func delete(_ sample: WritingSample) {
        samples.removeAll { $0.id == sample.id }
        save()
    }

    func deleteAll() {
        samples = []
        current = nil
        exampleCache = nil
        store.delete(Self.fileName)
    }

    private func save() {
        exampleCache = nil
        store.save(samples, to: Self.fileName)
    }
}
