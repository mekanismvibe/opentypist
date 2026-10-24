import AppKit
import Combine

/// A user snippet: typing `shortcut` and then a space or punctuation inserts
/// `expansion`.
struct Snippet: Codable, Identifiable, Equatable {
    var id = UUID()
    var shortcut: String
    var expansion: String
}

/// Text replacements that work in every app, including browsers and
/// Electron apps where macOS's own Text Replacements don't: the user's macOS
/// list (System Settings → Keyboard → Text Replacements) plus their own
/// snippets. Snippets are stored encrypted, like the other personal data.
final class TextReplacements: ObservableObject {
    static let shared = TextReplacements(store: .shared)

    struct SystemReplacement: Identifiable, Equatable {
        var id: String { shortcut }
        let shortcut: String
        let phrase: String
    }

    @Published private(set) var snippets: [Snippet]
    @Published private(set) var systemReplacements: [SystemReplacement] = []
    private(set) var rules = AutocorrectRules()

    private let store: SecureStore
    private static let fileName = "snippets.bin"

    init(store: SecureStore) {
        self.store = store
        snippets = store.load([Snippet].self, from: Self.fileName) ?? []
        reloadSystem()
    }

    /// Reads the macOS list again (it can change in System Settings or sync
    /// from other devices).
    func reloadSystem() {
        systemReplacements = NSSpellChecker.shared.userReplacementsDictionary
            // Entries that only change capitalization would just fight the user.
            .filter { !$0.key.isEmpty && $0.key.lowercased() != $0.value.lowercased() }
            .map { SystemReplacement(shortcut: $0.key, phrase: $0.value) }
            .sorted { $0.shortcut.lowercased() < $1.shortcut.lowercased() }
        rebuild()
    }

    /// A replacement for what was just typed, if its shortcut ends there.
    func match(_ typed: String) -> AutocorrectMatch? {
        guard Settings.shared.textReplacementsEnabled else { return nil }
        return rules.matchAtEnd(of: typed)
    }

    /// Returns a message if the snippet can't be added.
    @discardableResult
    func add(shortcut: String, expansion: String) -> String? {
        let shortcut = shortcut.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !shortcut.isEmpty, !expansion.isEmpty else { return "Enter a shortcut and the text it should become." }
        guard !shortcut.contains(where: \.isWhitespace) else { return "Shortcuts can't contain spaces." }
        guard let last = shortcut.last, !AutocorrectRules.isEndCharacter(last) else {
            return "Shortcuts can't end with punctuation; that's what triggers them."
        }
        guard !snippets.contains(where: { $0.shortcut.lowercased() == shortcut.lowercased() }) else {
            return "There's already a snippet for \"\(shortcut)\"."
        }
        snippets.append(Snippet(shortcut: shortcut, expansion: expansion))
        save()
        return nil
    }

    func remove(_ snippet: Snippet) {
        snippets.removeAll { $0.id == snippet.id }
        save()
    }

    private func save() {
        store.save(snippets, to: Self.fileName)
        rebuild()
    }

    private func rebuild() {
        let fresh = AutocorrectRules()
        func add(_ shortcut: String, _ expansion: String) {
            fresh.add(AutocorrectRule(trigger: shortcut.lowercased(), replacement: expansion, caseSensitive: false,
                                      immediate: false, insideWords: false, exact: true))
        }
        if Settings.shared.useSystemReplacements {
            for item in systemReplacements { add(item.shortcut, item.phrase) }
        }
        // Your own snippets win over the macOS list.
        for snippet in snippets { add(snippet.shortcut, snippet.expansion) }
        rules = fresh
    }

    /// Call after toggling the macOS list on or off.
    func settingsChanged() {
        rebuild()
    }
}
