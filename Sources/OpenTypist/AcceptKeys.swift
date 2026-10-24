import AppKit

/// A key plus modifiers, used to accept suggestions.
struct KeyCombo: Codable, Equatable, Hashable {
    var keyCode: Int64
    var command = false
    var option = false
    var control = false
    var shift = false
    /// What the key prints, captured when it was recorded (for letters and symbols).
    var character: String?

    static let tab = KeyCombo(keyCode: 48)
    static let shiftTab = KeyCombo(keyCode: 48, shift: true)
    static let rightArrow = KeyCombo(keyCode: 124)
    static let optionRightArrow = KeyCombo(keyCode: 124, option: true)

    init(keyCode: Int64, command: Bool = false, option: Bool = false, control: Bool = false, shift: Bool = false, character: String? = nil) {
        self.keyCode = keyCode
        self.command = command
        self.option = option
        self.control = control
        self.shift = shift
        self.character = character
    }

    init(event: NSEvent) {
        let flags = event.modifierFlags
        self.init(keyCode: Int64(event.keyCode), command: flags.contains(.command), option: flags.contains(.option),
                  control: flags.contains(.control), shift: flags.contains(.shift),
                  character: event.charactersIgnoringModifiers?.uppercased())
    }

    func matches(_ event: CGEvent) -> Bool {
        let flags = event.flags
        return event.getIntegerValueField(.keyboardEventKeycode) == keyCode
            && flags.contains(.maskCommand) == command
            && flags.contains(.maskAlternate) == option
            && flags.contains(.maskControl) == control
            && flags.contains(.maskShift) == shift
    }

    /// Non-typing keys: short hint shown after the ghost text, and the name
    /// shown in Settings.
    private static let names: [Int64: (hint: String, display: String)] = [
        48: ("tab", "Tab"), 36: ("↩", "Return"), 76: ("⌤", "Enter"), 49: ("space", "Space"),
        123: ("←", "←"), 124: ("→", "→"), 125: ("↓", "↓"), 126: ("↑", "↑"),
        115: ("home", "Home"), 119: ("end", "End"), 116: ("pgup", "Page Up"), 121: ("pgdn", "Page Down"),
        50: ("`", "`"), 53: ("esc", "Escape"), 51: ("⌫", "Delete"),
        122: ("F1", "F1"), 120: ("F2", "F2"), 99: ("F3", "F3"), 118: ("F4", "F4"), 96: ("F5", "F5"),
        97: ("F6", "F6"), 98: ("F7", "F7"), 100: ("F8", "F8"), 101: ("F9", "F9"), 109: ("F10", "F10"),
        103: ("F11", "F11"), 111: ("F12", "F12"),
    ]

    private var modifierSymbols: String {
        (control ? "⌃" : "") + (option ? "⌥" : "") + (shift ? "⇧" : "") + (command ? "⌘" : "")
    }

    /// "⇧Tab", "⌥→", "⌃J".
    var displayName: String {
        modifierSymbols + (Self.names[keyCode]?.display ?? character ?? "Key \(keyCode)")
    }

    /// Short label for the hint after the ghost text: "tab", "→", "⌥j".
    var hintLabel: String {
        modifierSymbols + (Self.names[keyCode]?.hint ?? character?.lowercased() ?? "?")
    }

    /// Keys that would get in the way of normal typing are refused: letters,
    /// digits, Space, Delete and Escape (which already dismisses) without ⌘⌃⌥.
    var problem: String? {
        if keyCode == 53 { return "Escape already dismisses suggestions." }
        if keyCode == 51 || keyCode == 117 { return "Delete keys can't accept suggestions." }
        let hasModifier = command || option || control
        if !hasModifier, Self.names[keyCode] == nil || keyCode == 49 {
            return "Pick a key you don't type with, or add ⌃, ⌥ or ⌘."
        }
        return nil
    }
}

/// Records the next key pressed in OpenTypist's own windows.
final class KeyRecorder: ObservableObject {
    static let shared = KeyRecorder()

    /// Which field is waiting for a key.
    @Published private(set) var recording: String?
    @Published var message: String?
    private var monitor: Any?

    func start(_ id: String, onKey: @escaping (KeyCombo) -> Void) {
        stop()
        recording = id
        message = nil
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            guard let self else { return event }
            let modifiers = event.modifierFlags.intersection([.command, .option, .control, .shift])
            if event.keyCode == 53, modifiers.isEmpty {
                self.stop()
                return nil
            }
            let combo = KeyCombo(event: event)
            if let problem = combo.problem {
                self.message = problem
                return nil
            }
            onKey(combo)
            self.stop()
            return nil
        }
    }

    func stop() {
        if let monitor { NSEvent.removeMonitor(monitor) }
        monitor = nil
        recording = nil
    }
}

/// The two accept keys for an app (or everywhere).
struct AcceptKeys: Codable, Equatable {
    var word: KeyCombo
    var all: KeyCombo

    static let standard = AcceptKeys(word: .tab, all: .shiftTab)
}
