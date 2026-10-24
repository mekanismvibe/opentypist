import AppKit
import Combine

/// One saved version of the profile.
struct ProfileVersion: Codable, Identifiable, Equatable {
    enum Source: String, Codable {
        case initial, you, openTypist, restored
    }

    var id = UUID()
    let date: Date
    let text: String
    let source: Source
    let note: String
}

enum ProfileUpdateMode: String, Codable, CaseIterable, Identifiable {
    case automatic, askFirst
    var id: String { rawValue }
    var title: String { self == .automatic ? "Update it automatically" : "Ask me first" }
}

/// The profile ("system prompt"): notes about the user that go at the top of
/// every prompt.
struct ProfileState: Codable, Equatable {
    var text: String
    var locked = false
    var mode: ProfileUpdateMode = .automatic
    var history: [ProfileVersion] = []
    /// OpenTypist's learned lines, by topic, exactly as last written. A line
    /// that no longer appears unchanged was edited or deleted by the user,
    /// and its topic becomes theirs.
    var generatedLines: [String: String] = [:]
    var userOwnedTopics: Set<String> = []
    var proposal: String?
    var proposalNote: String?
    var proposalLines: [String: String]?
    var dismissedProposal: String?
}

/// What OpenTypist has noticed about how the user types. Only ever fed from
/// the user's own keystrokes, never from text on screen.
struct LearnedData: Codable, Equatable {
    struct AppStyle: Codable, Equatable {
        var name: String
        var lowercaseStarts = 0
        var uppercaseStarts = 0
    }

    /// Capitalized words used mid-sentence (people, places, products).
    var names: [String: Int] = [:]
    /// Words the dictionary doesn't know, lowercased → count / usual spelling.
    var words: [String: Int] = [:]
    var wordForms: [String: String] = [:]
    var americanSpellings = 0
    var britishSpellings = 0
    var greetings: [String: Int] = [:]
    var signOffs: [String: Int] = [:]
    var appStyles: [String: AppStyle] = [:]
    /// Words autocorrect must leave alone (undone fixes, added by hand).
    var neverCorrect: Set<String> = []
}

final class Personalization: ObservableObject {
    static let shared = Personalization(store: .shared)

    static let defaultProfile = "I write clearly and naturally, in my own voice. In chats I keep things short and friendly; in emails I'm polite and to the point."
    static let maxProfileLength = 1000
    /// Times a name or word must be typed before it counts as learned.
    static let learnThreshold = 3

    @Published private(set) var profile: ProfileState
    @Published private(set) var learned: LearnedData

    private let store: SecureStore
    private var saveLearnedWork: DispatchWorkItem?
    private var updateWork: DispatchWorkItem?
    private var lastUpdate = Date.distantPast

    init(store: SecureStore) {
        self.store = store
        learned = store.load(LearnedData.self, from: "learned.bin") ?? LearnedData()
        if let saved = store.load(ProfileState.self, from: "profile.bin") {
            profile = saved
        } else {
            profile = ProfileState(text: Self.defaultProfile)
            profile.history = [ProfileVersion(date: Date(), text: Self.defaultProfile, source: .initial, note: "Starting profile")]
        }
    }

    /// The profile text for prompts, if the user wants it used.
    var promptProfile: String? {
        let text = profile.text.trimmingCharacters(in: .whitespacesAndNewlines)
        return Settings.shared.useProfile && !text.isEmpty ? text : nil
    }

    // MARK: - Learning

    private static let nameStoplist: Set<String> = [
        "I", "I'm", "I'll", "I've", "I'd", "OK", "Ok", "Mr", "Mrs", "Ms", "Dr", "Monday", "Tuesday", "Wednesday",
        "Thursday", "Friday", "Saturday", "Sunday", "January", "February", "March", "April", "May", "June", "July",
        "August", "September", "October", "November", "December", "English", "God",
    ]

    /// American → British pairs with a clear preference signal.
    private static let spellingPairs: [(String, String)] = [
        ("color", "colour"), ("colors", "colours"), ("favorite", "favourite"), ("favorites", "favourites"),
        ("honor", "honour"), ("labor", "labour"), ("neighbor", "neighbour"), ("neighbors", "neighbours"),
        ("behavior", "behaviour"), ("flavor", "flavour"), ("humor", "humour"), ("favor", "favour"),
        ("rumor", "rumour"), ("center", "centre"), ("theater", "theatre"), ("organize", "organise"),
        ("organized", "organised"), ("organization", "organisation"), ("realize", "realise"),
        ("realized", "realised"), ("recognize", "recognise"), ("apologize", "apologise"), ("analyze", "analyse"),
        ("traveled", "travelled"), ("traveling", "travelling"), ("canceled", "cancelled"),
        ("canceling", "cancelling"), ("gray", "grey"), ("defense", "defence"), ("catalog", "catalogue"),
        ("jewelry", "jewellery"), ("aluminum", "aluminium"), ("pajamas", "pyjamas"), ("fulfill", "fulfil"),
        ("enrollment", "enrolment"), ("customize", "customise"), ("prioritize", "prioritise"),
    ]
    private static let spellingLookup: [String: Bool] = {
        var lookup: [String: Bool] = [:] // true = British
        for (american, british) in spellingPairs {
            lookup[american] = false
            lookup[british] = true
        }
        return lookup
    }()

    private static let greetingWords = ["good morning", "good afternoon", "good evening", "hi", "hey", "hello", "dear", "morning", "hiya", "yo"]
    private static let signOffPhrases: Set<String> = [
        "thanks", "thank you", "thanks so much", "many thanks", "thanks again", "cheers", "best", "best regards",
        "kind regards", "warm regards", "regards", "sincerely", "talk soon", "all the best", "take care", "thx",
        "ty", "love", "speak soon", "have a good one",
    ]

    /// A word the user finished typing (from keystrokes).
    func observeWord(_ word: String, startsSentence: Bool, bundleID: String?, appName: String) {
        guard Settings.shared.learnHabits else { return }
        let letters = word.filter(\.isLetter)
        guard !letters.isEmpty else { return }
        let lower = word.lowercased()
        var changed = false

        if let british = Self.spellingLookup[lower] {
            if british { learned.britishSpellings += 1 } else { learned.americanSpellings += 1 }
            changed = true
        }

        if let bundleID, startsSentence, !Self.nameStoplist.contains(word), let first = letters.first {
            var style = learned.appStyles[bundleID] ?? LearnedData.AppStyle(name: appName)
            style.name = appName
            if first.isLowercase { style.lowercaseStarts += 1 } else { style.uppercaseStarts += 1 }
            learned.appStyles[bundleID] = style
            changed = true
        }

        if word.count >= 3, word.allSatisfy({ $0.isLetter || $0 == "'" || $0 == "-" }) {
            let allCaps = letters.count > 1 && letters.allSatisfy(\.isUppercase)
            if !startsSentence, word.first?.isUppercase == true, !allCaps, !Self.nameStoplist.contains(word) {
                learned.names[word, default: 0] += 1
                changed = true
            } else if !SpellCheck.isValidWord(word) {
                learned.words[lower, default: 0] += 1
                learned.wordForms[lower] = word
                changed = true
            }
        }
        if changed { learnedChanged() }
    }

    /// A line the user finished with Return: look for greetings and sign-offs.
    func observeLine(_ line: String) {
        guard Settings.shared.learnHabits else { return }
        let trimmed = line.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty, trimmed.count <= 40 else { return }
        let bare = trimmed.lowercased().trimmingCharacters(in: CharacterSet(charactersIn: ",.!"))

        if Self.signOffPhrases.contains(bare) {
            learned.signOffs[trimmed, default: 0] += 1
            learnedChanged()
            return
        }
        for greeting in Self.greetingWords where bare == greeting || bare.hasPrefix(greeting + " ") || bare.hasPrefix(greeting + ",") {
            // "Hi Sarah," → "Hi"; keep the user's capitalization.
            let typed = String(trimmed.prefix(greeting.count))
            if bare.split(separator: " ").count <= 4 {
                learned.greetings[typed, default: 0] += 1
                learnedChanged()
            }
            return
        }
    }

    /// The typed word turned out to be a typo (the model fixed it).
    func forgetObservation(of word: String) {
        let lower = word.lowercased()
        if let count = learned.words[lower] {
            learned.words[lower] = count > 1 ? count - 1 : nil
            learnedChanged()
        }
        if let count = learned.names[word] {
            learned.names[word] = count > 1 ? count - 1 : nil
            learnedChanged()
        }
    }

    // MARK: - Never correct

    func isNeverCorrect(_ word: String) -> Bool {
        let lower = word.lowercased()
        return learned.neverCorrect.contains(lower) || (learned.words[lower] ?? 0) >= Self.learnThreshold
    }

    func addNeverCorrect(_ word: String) {
        let lower = word.trimmingCharacters(in: .whitespaces).lowercased()
        guard !lower.isEmpty else { return }
        learned.neverCorrect.insert(lower)
        learnedChanged(saveNow: true)
    }

    func removeNeverCorrect(_ word: String) {
        learned.neverCorrect.remove(word.lowercased())
        learned.words[word.lowercased()] = nil
        learnedChanged(saveNow: true)
    }

    /// Words the dictionary doesn't know that are typed often enough to keep.
    var learnedWords: [String] {
        Self.ranked(learned.words).map { learned.wordForms[$0] ?? $0 }
    }

    var learnedNames: [String] {
        Self.ranked(learned.names)
    }

    /// Entries used often enough, most used first. Ties are alphabetical:
    /// dictionaries have no stable order, and a shuffled line would look
    /// like a profile change.
    private static func ranked(_ counts: [String: Int]) -> [String] {
        counts.filter { $0.value >= learnThreshold }
            .sorted { $0.value != $1.value ? $0.value > $1.value : $0.key < $1.key }
            .map(\.key)
    }

    func removeLearnedName(_ name: String) {
        learned.names[name] = nil
        learnedChanged(saveNow: true)
    }

    func forgetLearned() {
        let neverCorrect = learned.neverCorrect
        learned = LearnedData()
        learned.neverCorrect = neverCorrect
        learnedChanged(saveNow: true)
    }

    private func learnedChanged(saveNow: Bool = false) {
        saveLearnedWork?.cancel()
        let work = DispatchWorkItem { [weak self] in self?.saveLearned() }
        saveLearnedWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + (saveNow ? 0 : 10), execute: work)
        scheduleProfileUpdate()
    }

    func saveLearned() {
        saveLearnedWork?.cancel()
        store.save(learned, to: "learned.bin")
    }

    // MARK: - Learned lines

    /// Facts for the profile, by topic, written the way a base model reads
    /// best: plain first-person statements, not instructions.
    func generatedLines() -> [(topic: String, line: String)] {
        var lines: [(String, String)] = []
        let names = learnedNames.prefix(8)
        if !names.isEmpty {
            lines.append(("names", "People and things I often mention: \(names.joined(separator: ", "))."))
        }
        let words = learnedWords.prefix(8)
        if !words.isEmpty {
            lines.append(("words", "Words I use that dictionaries may not know: \(words.joined(separator: ", "))."))
        }
        let spellingTotal = learned.americanSpellings + learned.britishSpellings
        if spellingTotal >= 5 {
            let britishShare = Double(learned.britishSpellings) / Double(spellingTotal)
            if britishShare >= 0.75 {
                lines.append(("spelling", "I use British spelling (colour, organise)."))
            } else if britishShare <= 0.25 {
                lines.append(("spelling", "I use American spelling (color, organize)."))
            }
        }
        let greeting = Self.top(learned.greetings)
        let signOff = Self.top(learned.signOffs)
        switch (greeting, signOff) {
        case let (g?, s?): lines.append(("greetings", "I usually start messages with \"\(g)\" and sign off with \"\(s)\"."))
        case let (g?, nil): lines.append(("greetings", "I usually start messages with \"\(g)\"."))
        case let (nil, s?): lines.append(("greetings", "I usually sign off with \"\(s)\"."))
        default: break
        }
        let casualApps = learned.appStyles.values
            .filter { $0.lowercaseStarts + $0.uppercaseStarts >= 15 && Double($0.lowercaseStarts) / Double($0.lowercaseStarts + $0.uppercaseStarts) >= 0.6 }
            .map(\.name).sorted()
        if !casualApps.isEmpty {
            lines.append(("style", "In \(Self.list(casualApps)) I write casually, mostly in lowercase."))
        }
        return lines
    }

    /// Most used entry if it has been seen enough and clearly dominates.
    private static func top(_ counts: [String: Int]) -> String? {
        guard let best = ranked(counts).first, let count = counts[best] else { return nil }
        let total = counts.values.reduce(0, +)
        return Double(count) / Double(total) >= 0.5 ? best : nil
    }

    private static func list(_ items: [String]) -> String {
        switch items.count {
        case 0: return ""
        case 1: return items[0]
        default: return items.dropLast().joined(separator: ", ") + " and " + items.last!
        }
    }

    // MARK: - Profile updates

    private func scheduleProfileUpdate() {
        // At most every ten minutes while typing.
        guard updateWork == nil, Date().timeIntervalSince(lastUpdate) > 600 else { return }
        let work = DispatchWorkItem { [weak self] in
            self?.updateWork = nil
            self?.updateProfile()
        }
        updateWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 30, execute: work)
    }

    /// Rewrites OpenTypist's learned lines at the end of the profile, keeping
    /// everything the user wrote. Returns a short description of what changed.
    @discardableResult
    func updateProfile() -> String? {
        lastUpdate = Date()
        guard !profile.locked else { return nil }

        var state = profile
        let currentLines = state.text.components(separatedBy: "\n")
        // Learned lines the user edited or deleted are theirs from now on.
        for (topic, line) in state.generatedLines where !currentLines.contains(line) {
            state.userOwnedTopics.insert(topic)
        }
        let previous = state.generatedLines.filter { !state.userOwnedTopics.contains($0.key) }
        var userLines = currentLines.filter { line in !previous.values.contains(line) }
        while userLines.last?.trimmingCharacters(in: .whitespaces).isEmpty == true { userLines.removeLast() }

        var fresh: [String: String] = [:]
        var freshOrdered: [String] = []
        for (topic, line) in generatedLines() where !state.userOwnedTopics.contains(topic) {
            fresh[topic] = line
            freshOrdered.append(line)
        }
        let newText = (userLines + freshOrdered).joined(separator: "\n")
        let note = Self.describeChange(from: previous, to: fresh)
        guard newText != state.text, let note else {
            state.generatedLines = fresh
            if state != profile { profile = state; saveProfile() }
            return nil
        }

        switch state.mode {
        case .automatic:
            state.generatedLines = fresh
            state.text = newText
            state.proposal = nil
            appendHistory(&state, source: .openTypist, note: note)
        case .askFirst:
            guard newText != state.dismissedProposal else { return nil }
            state.proposal = newText
            state.proposalNote = note
            state.proposalLines = fresh
        }
        profile = state
        saveProfile()
        return note
    }

    private static func describeChange(from old: [String: String], to new: [String: String]) -> String? {
        let added = new.filter { old[$0.key] == nil }.keys.sorted()
        let changed = new.filter { old[$0.key] != nil && old[$0.key] != $0.value }.keys.sorted()
        let removed = old.filter { new[$0.key] == nil }.keys.sorted()
        var parts: [String] = []
        if !added.isEmpty { parts.append("learned \(added.map(topicName).joined(separator: ", "))") }
        if !changed.isEmpty { parts.append("updated \(changed.map(topicName).joined(separator: ", "))") }
        if !removed.isEmpty { parts.append("dropped \(removed.map(topicName).joined(separator: ", "))") }
        return parts.isEmpty ? nil : parts.joined(separator: "; ").capitalizingFirst
    }

    private static func topicName(_ topic: String) -> String {
        switch topic {
        case "names": return "names"
        case "words": return "your words"
        case "spelling": return "spelling"
        case "greetings": return "greetings and sign-offs"
        case "style": return "chat style"
        default: return topic
        }
    }

    // MARK: - Editing

    func saveProfileText(_ text: String) {
        let trimmed = String(text.prefix(Self.maxProfileLength))
        guard trimmed != profile.text else { return }
        var state = profile
        state.text = trimmed
        state.proposal = nil
        appendHistory(&state, source: .you, note: "Edited by you")
        profile = state
        saveProfile()
    }

    func setLocked(_ locked: Bool) {
        profile.locked = locked
        if locked { profile.proposal = nil }
        saveProfile()
    }

    func setMode(_ mode: ProfileUpdateMode) {
        profile.mode = mode
        saveProfile()
    }

    func applyProposal() {
        guard let proposal = profile.proposal else { return }
        var state = profile
        state.text = proposal
        state.generatedLines = state.proposalLines ?? state.generatedLines
        state.proposal = nil
        appendHistory(&state, source: .openTypist, note: state.proposalNote ?? "Update from OpenTypist")
        profile = state
        saveProfile()
    }

    func dismissProposal() {
        profile.dismissedProposal = profile.proposal
        profile.proposal = nil
        saveProfile()
    }

    func restore(_ version: ProfileVersion) {
        var state = profile
        state.text = version.text
        state.proposal = nil
        appendHistory(&state, source: .restored, note: "Restored the version from \(version.date.formatted(date: .abbreviated, time: .shortened))")
        profile = state
        saveProfile()
    }

    private func appendHistory(_ state: inout ProfileState, source: ProfileVersion.Source, note: String) {
        state.history.append(ProfileVersion(date: Date(), text: state.text, source: source, note: note))
        if state.history.count > 50 { state.history.removeFirst(state.history.count - 50) }
    }

    private func saveProfile() {
        store.save(profile, to: "profile.bin")
    }
}

private extension String {
    var capitalizingFirst: String { prefix(1).uppercased() + dropFirst() }
}
