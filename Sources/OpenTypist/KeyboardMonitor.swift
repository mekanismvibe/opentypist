import AppKit
import CoreGraphics

/// Global keyboard/mouse event tap. Runs on the main run loop; handlers can
/// swallow a key (e.g. Tab while a suggestion is visible) by returning true.
final class KeyboardMonitor {
    var onKeyDown: ((CGEvent) -> Bool)?
    var onMouseDown: (() -> Void)?

    private var tap: CFMachPort?
    private var source: CFRunLoopSource?

    var isRunning: Bool { tap != nil }

    func start() -> Bool {
        guard tap == nil else { return true }
        let types: [CGEventType] = [.keyDown, .leftMouseDown, .rightMouseDown, .otherMouseDown]
        let mask = types.reduce(CGEventMask(0)) { $0 | (CGEventMask(1) << $1.rawValue) }

        let callback: CGEventTapCallBack = { _, type, event, userInfo in
            guard let userInfo else { return Unmanaged.passUnretained(event) }
            let monitor = Unmanaged<KeyboardMonitor>.fromOpaque(userInfo).takeUnretainedValue()
            return monitor.handle(type: type, event: event)
        }

        guard let tap = CGEvent.tapCreate(
            tap: .cgSessionEventTap,
            place: .headInsertEventTap,
            options: .defaultTap,
            eventsOfInterest: mask,
            callback: callback,
            userInfo: Unmanaged.passUnretained(self).toOpaque()
        ) else { return false }

        let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0)
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)
        self.tap = tap
        self.source = source
        return true
    }

    private func handle(type: CGEventType, event: CGEvent) -> Unmanaged<CGEvent>? {
        switch type {
        case .tapDisabledByTimeout, .tapDisabledByUserInput:
            if let tap { CGEvent.tapEnable(tap: tap, enable: true) }
        case .keyDown:
            // Ignore the keystrokes we synthesize ourselves.
            if event.getIntegerValueField(.eventSourceUserData) == TextInserter.marker { break }
            if onKeyDown?(event) == true { return nil }
        case .leftMouseDown, .rightMouseDown, .otherMouseDown:
            onMouseDown?()
        default:
            break
        }
        return Unmanaged.passUnretained(event)
    }
}

enum KeyCode {
    static let tab: Int64 = 48
    static let escape: Int64 = 53
}

extension CGEvent {
    var typedCharacters: String {
        var length = 0
        var buffer = [UniChar](repeating: 0, count: 8)
        keyboardGetUnicodeString(maxStringLength: buffer.count, actualStringLength: &length, unicodeString: &buffer)
        return String(utf16CodeUnits: buffer, count: length)
    }
}

/// Types text into the focused app by posting synthetic Unicode key events.
enum TextInserter {
    static let marker: Int64 = 0x0F7E_7157

    /// Presses Delete (backspace) `count` times.
    static func deleteBackward(_ count: Int) {
        guard count > 0 else { return }
        let source = CGEventSource(stateID: .privateState)
        for _ in 0..<count {
            for keyDown in [true, false] {
                guard let event = CGEvent(keyboardEventSource: source, virtualKey: 51, keyDown: keyDown) else { continue }
                event.flags = []
                event.setIntegerValueField(.eventSourceUserData, value: marker)
                event.post(tap: .cghidEventTap)
            }
        }
    }

    /// Replays a key the tap swallowed, after our own edits. Built from our
    /// own event source: a copy of the original keeps its source, which
    /// overwrites our marker when posted, so the tap would see it again.
    static func repost(_ event: CGEvent) {
        let keyCode = CGKeyCode(event.getIntegerValueField(.keyboardEventKeycode))
        guard let replay = CGEvent(keyboardEventSource: CGEventSource(stateID: .privateState), virtualKey: keyCode, keyDown: true) else { return }
        var length = 0
        var buffer = [UniChar](repeating: 0, count: 8)
        event.keyboardGetUnicodeString(maxStringLength: buffer.count, actualStringLength: &length, unicodeString: &buffer)
        replay.keyboardSetUnicodeString(stringLength: length, unicodeString: buffer)
        replay.flags = event.flags
        replay.setIntegerValueField(.eventSourceUserData, value: marker)
        replay.post(tap: .cghidEventTap)
    }

    /// Line breaks are sent as Shift-Return, which adds a new line in chat
    /// apps (where a plain Return would send the message) as well as in
    /// ordinary text fields.
    static func type(_ text: String) {
        let lines = text.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n")
        for (index, line) in lines.enumerated() {
            if index > 0 { pressShiftReturn() }
            typeLine(line)
        }
    }

    private static func pressShiftReturn() {
        let source = CGEventSource(stateID: .privateState)
        for keyDown in [true, false] {
            guard let event = CGEvent(keyboardEventSource: source, virtualKey: 36, keyDown: keyDown) else { continue }
            event.flags = .maskShift
            event.setIntegerValueField(.eventSourceUserData, value: marker)
            event.post(tap: .cghidEventTap)
        }
    }

    private static func typeLine(_ text: String) {
        let source = CGEventSource(stateID: .privateState)
        let units = Array(text.utf16)
        var index = 0
        while index < units.count {
            var end = min(index + 16, units.count)
            // Don't split a surrogate pair across two events.
            if end < units.count, UTF16.isLeadSurrogate(units[end - 1]) { end -= 1 }
            let chunk = Array(units[index..<end])
            for keyDown in [true, false] {
                guard let event = CGEvent(keyboardEventSource: source, virtualKey: 0, keyDown: keyDown) else { continue }
                event.flags = []
                chunk.withUnsafeBufferPointer {
                    event.keyboardSetUnicodeString(stringLength: chunk.count, unicodeString: $0.baseAddress)
                }
                event.setIntegerValueField(.eventSourceUserData, value: marker)
                event.post(tap: .cghidEventTap)
            }
            index = end
        }
    }
}
