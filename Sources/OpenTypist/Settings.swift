import AppKit
import Combine

enum CompletionLength: String, CaseIterable, Identifiable {
    case short, medium, long

    var id: String { rawValue }

    var title: String {
        switch self {
        case .short: return "Short (up to 3 words)"
        case .medium: return "Medium (up to 7 words)"
        case .long: return "Long (up to 15 words)"
        }
    }

    var maxWords: Int {
        switch self {
        case .short: return 3
        case .medium: return 7
        case .long: return 15
        }
    }

    var maxTokens: Int {
        switch self {
        case .short: return 10
        case .medium: return 20
        case .long: return 36
        }
    }
}

final class Settings: ObservableObject {
    static let shared = Settings()

    /// Apps where Tab or autocomplete matter more than prose suggestions.
    static let defaultDisabledApps = [
        "com.apple.Terminal",
        "com.googlecode.iterm2",
        "dev.warp.Warp-Stable",
        "com.mitchellh.ghostty",
        "com.apple.dt.Xcode",
        "com.microsoft.VSCode",
        "com.todesktop.230313mzl4w4u92", // Cursor
        "com.1password.1password",
        "com.agilebits.onepassword7",
        "com.bitwarden.desktop",
        "com.apple.keychainaccess",
    ]

    private let defaults = UserDefaults.standard

    @Published var enabled: Bool { didSet { defaults.set(enabled, forKey: "enabled") } }
    @Published var length: CompletionLength { didSet { defaults.set(length.rawValue, forKey: "length") } }
    @Published var useAppContext: Bool { didSet { defaults.set(useAppContext, forKey: "useAppContext") } }
    /// Read visible window text (needs Screen Recording) and add it to the prompt.
    @Published var useScreenContext: Bool { didSet { defaults.set(useScreenContext, forKey: "useScreenContext") } }
    @Published var selectedModelID: String { didSet { defaults.set(selectedModelID, forKey: "selectedModelID") } }
    @Published var acceptTrailingSpace: Bool { didSet { defaults.set(acceptTrailingSpace, forKey: "acceptTrailingSpace") } }
    @Published var disabledApps: [String] { didSet { defaults.set(disabledApps, forKey: "disabledApps") } }
    /// A user-chosen .gguf; nil means the downloaded default model.
    @Published var customModelPath: String? { didSet { defaults.set(customModelPath, forKey: "customModelPath") } }

    private init() {
        defaults.register(defaults: [
            "enabled": true,
            "length": CompletionLength.medium.rawValue,
            "useAppContext": true,
            "useScreenContext": true,
            "selectedModelID": ModelCatalog.recommended().id,
            "acceptTrailingSpace": true,
            "disabledApps": Self.defaultDisabledApps,
        ])
        enabled = defaults.bool(forKey: "enabled")
        length = CompletionLength(rawValue: defaults.string(forKey: "length") ?? "") ?? .medium
        useAppContext = defaults.bool(forKey: "useAppContext")
        useScreenContext = defaults.bool(forKey: "useScreenContext")
        selectedModelID = defaults.string(forKey: "selectedModelID") ?? ModelCatalog.defaultID
        acceptTrailingSpace = defaults.bool(forKey: "acceptTrailingSpace")
        disabledApps = defaults.stringArray(forKey: "disabledApps") ?? Self.defaultDisabledApps
        customModelPath = defaults.string(forKey: "customModelPath")
    }

    func isDisabled(bundleID: String?) -> Bool {
        guard let bundleID else { return false }
        return bundleID == Bundle.main.bundleIdentifier || disabledApps.contains(bundleID)
    }

    func setDisabled(_ disabled: Bool, bundleID: String) {
        if disabled {
            if !disabledApps.contains(bundleID) { disabledApps.append(bundleID) }
        } else {
            disabledApps.removeAll { $0 == bundleID }
        }
    }
}

final class Stats: ObservableObject {
    static let shared = Stats()

    private let defaults = UserDefaults.standard

    @Published private(set) var wordsAccepted: Int
    @Published private(set) var charactersInserted: Int
    @Published private(set) var acceptKeyPresses: Int
    @Published private(set) var suggestionsShown: Int
    @Published private(set) var wordsToday: Int
    private var day: String

    private init() {
        wordsAccepted = defaults.integer(forKey: "stats.words")
        charactersInserted = defaults.integer(forKey: "stats.chars")
        acceptKeyPresses = defaults.integer(forKey: "stats.presses")
        suggestionsShown = defaults.integer(forKey: "stats.shown")
        wordsToday = defaults.integer(forKey: "stats.wordsToday")
        day = defaults.string(forKey: "stats.day") ?? Self.today()
    }

    /// Typing the accepted text by hand would have cost one key per character.
    var keystrokesSaved: Int { max(0, charactersInserted - acceptKeyPresses) }

    var wordsAcceptedToday: Int { day == Self.today() ? wordsToday : 0 }

    func recordShown() {
        suggestionsShown += 1
        save()
    }

    func recordAccepted(_ text: String) {
        rollDay()
        let words = text.split(whereSeparator: { $0.isWhitespace }).count
        wordsAccepted += words
        wordsToday += words
        charactersInserted += text.count
        acceptKeyPresses += 1
        save()
    }

    func reset() {
        wordsAccepted = 0
        charactersInserted = 0
        acceptKeyPresses = 0
        suggestionsShown = 0
        wordsToday = 0
        save()
    }

    private func rollDay() {
        let today = Self.today()
        if today != day {
            day = today
            wordsToday = 0
        }
    }

    private func save() {
        defaults.set(wordsAccepted, forKey: "stats.words")
        defaults.set(charactersInserted, forKey: "stats.chars")
        defaults.set(acceptKeyPresses, forKey: "stats.presses")
        defaults.set(suggestionsShown, forKey: "stats.shown")
        defaults.set(wordsToday, forKey: "stats.wordsToday")
        defaults.set(day, forKey: "stats.day")
    }

    private static func today() -> String {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd"
        return f.string(from: Date())
    }
}
