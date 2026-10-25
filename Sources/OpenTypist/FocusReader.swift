import AppKit
import ApplicationServices
import Carbon

/// Font and color of the text just before the caret, so the ghost text can
/// match what the user is typing.
struct TextStyle {
    var font: NSFont?
    var color: NSColor?
}

/// What the user is typing into right now, read through the Accessibility API.
struct FocusContext {
    let pid: pid_t
    let bundleID: String?
    let appName: String
    let windowTitle: String?
    let role: String
    /// Text before the caret (at most `FocusReader.maxCharacters`).
    let textBefore: String
    /// Text after the caret up to the end of the line.
    let textAfterOnLine: String
    /// macOS's own inline prediction, the gray text it shows right after the
    /// caret. Not part of `textAfterOnLine`: it isn't the user's text.
    let hostPrediction: String
    /// Caret rectangle in Cocoa screen coordinates (origin bottom-left).
    let caretRect: CGRect?
    let style: TextStyle
    let element: AXUIElement
    let appElement: AXUIElement
    /// The text field's frame, in Cocoa screen coordinates.
    let elementFrame: CGRect?
    /// Screen x where lines of text start in this field (for wrapping).
    let lineStartX: CGFloat?
}

enum FocusFailure: Error, CustomStringConvertible {
    case noFrontApp
    case noFocusedElement(app: String)
    case secureField(app: String)
    case noTextSelection(app: String, role: String)
    case textSelected(app: String)
    case unreadableText(app: String, role: String)
    case composing(app: String)

    var description: String {
        switch self {
        case .noFrontApp: return "No active app"
        case .noFocusedElement(let app): return "\(app): no focused text field found"
        case .secureField(let app): return "\(app): password field, skipped"
        case .noTextSelection(let app, let role): return "\(app): focused element (\(role)) doesn't report a caret"
        case .textSelected(let app): return "\(app): text is selected"
        case .unreadableText(let app, let role): return "\(app): couldn't read the text of \(role)"
        case .composing(let app): return "\(app): waiting for the input method to finish"
        }
    }
}

enum FocusReader {
    static let maxCharacters = 2000

    private static let systemWide: AXUIElement = {
        let element = AXUIElementCreateSystemWide()
        AXUIElementSetMessagingTimeout(element, 0.15)
        return element
    }()

    private static var enabledManualAccessibility = Set<pid_t>()

    static var isTrusted: Bool { AXIsProcessTrusted() }

    static func promptForTrust() {
        let key = kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String
        _ = AXIsProcessTrustedWithOptions([key: true] as CFDictionary)
    }

    static func read() -> Result<FocusContext, FocusFailure> {
        guard let app = NSWorkspace.shared.frontmostApplication else { return .failure(.noFrontApp) }
        let pid = app.processIdentifier
        let appName = app.localizedName ?? app.bundleIdentifier ?? "App"
        let appElement = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(appElement, 0.15)
        exposeAccessibilityTree(appElement, pid: pid)

        // Ask the app itself first; the system-wide query fails for some apps.
        guard let element: AXUIElement = attribute(appElement, kAXFocusedUIElementAttribute)
            ?? attribute(systemWide, kAXFocusedUIElementAttribute) else {
            return .failure(.noFocusedElement(app: appName))
        }
        AXUIElementSetMessagingTimeout(element, 0.15)

        let role: String = attribute(element, kAXRoleAttribute) ?? "unknown"
        // Never read or complete password fields.
        let subrole: String? = attribute(element, kAXSubroleAttribute)
        if subrole == (kAXSecureTextFieldSubrole as String) || role == "AXSecureTextField" {
            return .failure(.secureField(app: appName))
        }

        guard let rangeValue: AXValue = attribute(element, kAXSelectedTextRangeAttribute) else {
            return .failure(.noTextSelection(app: appName, role: role))
        }
        var selection = CFRange()
        guard AXValueGetValue(rangeValue, .cfRange, &selection), selection.location >= 0 else {
            return .failure(.noTextSelection(app: appName, role: role))
        }
        guard selection.length == 0 else { return .failure(.textSelected(app: appName)) }
        let caret = selection.location

        let total: Int = attribute(element, kAXNumberOfCharactersAttribute) ?? -1
        var before: String
        var after: String
        if total <= 50_000, let value: String = attribute(element, kAXValueAttribute) {
            let ns = value as NSString
            guard caret <= ns.length else { return .failure(.unreadableText(app: appName, role: role)) }
            let start = max(0, caret - maxCharacters)
            let beforeRange = ns.rangeOfComposedCharacterSequences(for: NSRange(location: start, length: caret - start))
            before = ns.substring(with: beforeRange)
            after = ns.substring(with: NSRange(location: caret, length: min(200, ns.length - caret)))
        } else {
            // Large documents: only fetch the part around the caret.
            let start = max(0, caret - maxCharacters)
            guard let b = string(element, location: start, length: caret - start) else {
                return .failure(.unreadableText(app: appName, role: role))
            }
            before = b
            after = string(element, location: caret, length: total >= 0 ? min(200, total - caret) : 200) ?? ""
        }
        // Marked text is either an input method still composing, or macOS's
        // inline prediction: the word being typed is marked, and the part
        // after the caret is the gray text it suggests ("apprec|iate"). That
        // part isn't the user's text.
        var hostPrediction = ""
        if let markedValue: AXValue = attribute(element, "AXTextInputMarkedRange") {
            var marked = CFRange()
            if AXValueGetValue(markedValue, .cfRange, &marked), marked.length > 0 {
                guard !inputMethodActive() else { return .failure(.composing(app: appName)) }
                let ns = after as NSString
                let end = min(marked.location + marked.length - caret, ns.length)
                if marked.location <= caret, end > 0 {
                    let range = NSRange(location: 0, length: end)
                    hostPrediction = ns.substring(with: range)
                    after = ns.substring(from: end)
                }
            }
        }
        if let newline = after.firstIndex(where: { $0.isNewline }) {
            after = String(after[..<newline])
        }
        // Some apps report a non-breaking space for the trailing space.
        before = before.replacingOccurrences(of: "\u{00A0}", with: " ")

        var title: String?
        if let window: AXUIElement = attribute(element, kAXWindowAttribute) {
            title = attribute(window, kAXTitleAttribute)
        }

        let previousIsNewline = before.last?.isNewline ?? false
        // Index of the first character of the current hard line, if known.
        let charactersOnLine = before.reversed().prefix(while: { !$0.isNewline })
        let hardLineStart = charactersOnLine.count < before.count || before.utf16.count == caret
            ? caret - String(charactersOnLine).utf16.count : nil
        return .success(FocusContext(
            pid: pid,
            bundleID: app.bundleIdentifier,
            appName: appName,
            windowTitle: title,
            role: role,
            textBefore: before,
            textAfterOnLine: after,
            hostPrediction: hostPrediction,
            caretRect: caretRect(element, caret: caret, previousIsNewline: previousIsNewline),
            style: textStyle(element, caret: caret),
            element: element,
            appElement: appElement,
            elementFrame: frame(of: element),
            lineStartX: lineStartX(element, caret: caret, hardLineStart: hardLineStart)
        ))
    }

    // MARK: - Layout

    private static func frame(of element: AXUIElement) -> CGRect? {
        guard let positionValue: AXValue = attribute(element, kAXPositionAttribute),
              let sizeValue: AXValue = attribute(element, kAXSizeAttribute) else { return nil }
        var position = CGPoint.zero
        var size = CGSize.zero
        guard AXValueGetValue(positionValue, .cgPoint, &position), AXValueGetValue(sizeValue, .cgSize, &size),
              size.width > 0, size.height > 0 else { return nil }
        return toCocoa(CGRect(origin: position, size: size))
    }

    /// Left edge of the caret's visual line: the first character of the line
    /// (soft-wrapped or not), so wrapped ghost text lines up with real text.
    private static func lineStartX(_ element: AXUIElement, caret: Int, hardLineStart: Int?) -> CGFloat? {
        // Native text views: line number → character range → bounds.
        var index = caret
        if let lineValue = parameterized(element, kAXLineForIndexParameterizedAttribute, index as CFNumber),
           let line = lineValue as? Int,
           let rangeValue = parameterized(element, kAXRangeForLineParameterizedAttribute, line as CFNumber),
           CFGetTypeID(rangeValue) == AXValueGetTypeID() {
            var range = CFRange()
            if AXValueGetValue(rangeValue as! AXValue, .cfRange, &range), range.location < caret {
                index = range.location
            }
        } else if let hardLineStart, hardLineStart < caret {
            index = hardLineStart
        }
        if index < caret, let r = bounds(element, location: index, length: 1), r.width < 200 {
            return r.minX
        }
        // Web text: the caret's line as a text marker range.
        var selected: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, "AXSelectedTextMarkerRange" as CFString, &selected) == .success,
              let selected,
              let caretMarker = parameterized(element, "AXStartTextMarkerForTextMarkerRange", selected),
              let lineRange = parameterized(element, "AXLineTextMarkerRangeForTextMarker", caretMarker),
              let start = parameterized(element, "AXStartTextMarkerForTextMarkerRange", lineRange),
              let next = parameterized(element, "AXNextTextMarkerForTextMarker", start),
              let firstCharacter = parameterized(element, "AXTextMarkerRangeForUnorderedTextMarkers", [start, next] as CFArray),
              let value = parameterized(element, "AXBoundsForTextMarkerRange", firstCharacter),
              CFGetTypeID(value) == AXValueGetTypeID() else { return nil }
        var rect = CGRect.zero
        guard AXValueGetValue(value as! AXValue, .cgRect, &rect), rect.height > 1, rect.origin != .zero, rect.width < 200 else { return nil }
        return rect.minX
    }

    /// Screen x of the first thing to the right of the caret on its line that
    /// isn't part of the text field, such as a send or microphone button.
    static func obstacleX(in context: FocusContext, from startX: CGFloat, to endX: CGFloat) -> CGFloat? {
        guard let caret = context.caretRect, endX > startX else { return nil }
        let primaryHeight = NSScreen.screens.first?.frame.height ?? 0
        let y = Float(primaryHeight - caret.midY)
        var x = startX + 4
        while x <= endX {
            var hit: AXUIElement?
            if AXUIElementCopyElementAtPosition(context.appElement, Float(x), y, &hit) == .success,
               let hit, !isPart(hit, of: context.element) {
                return x
            }
            x += 10
        }
        return nil
    }

    /// Whether `element` is `field` or inside it (web text fields contain
    /// static-text children).
    private static func isPart(_ element: AXUIElement, of field: AXUIElement) -> Bool {
        var current: AXUIElement? = element
        for _ in 0..<6 {
            guard let node = current else { return false }
            if CFEqual(node, field) { return true }
            current = attribute(node, kAXParentAttribute)
        }
        return false
    }

    private static func toCocoa(_ axRect: CGRect) -> CGRect {
        let primaryHeight = NSScreen.screens.first?.frame.height ?? 0
        return CGRect(x: axRect.minX, y: primaryHeight - axRect.maxY, width: axRect.width, height: axRect.height)
    }

    // MARK: - Helpers

    /// Whether the keyboard input is an input method (Japanese, Chinese,
    /// Korean…) rather than a plain layout; only then is marked text a
    /// composition in progress.
    private static func inputMethodActive() -> Bool {
        guard let source = TISCopyCurrentKeyboardInputSource()?.takeRetainedValue(),
              let type = TISGetInputSourceProperty(source, kTISPropertyInputSourceType) else { return false }
        return Unmanaged<CFString>.fromOpaque(type).takeUnretainedValue() != kTISTypeKeyboardLayout
    }

    /// Chromium and Electron only build their accessibility tree on request.
    private static func exposeAccessibilityTree(_ app: AXUIElement, pid: pid_t) {
        guard !enabledManualAccessibility.contains(pid) else { return }
        enabledManualAccessibility.insert(pid)
        AXUIElementSetAttributeValue(app, "AXManualAccessibility" as CFString, kCFBooleanTrue)
    }

    private static func attribute<T>(_ element: AXUIElement, _ name: String) -> T? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, name as CFString, &value) == .success, let value else { return nil }
        if T.self == AXUIElement.self {
            guard CFGetTypeID(value) == AXUIElementGetTypeID() else { return nil }
        } else if T.self == AXValue.self {
            guard CFGetTypeID(value) == AXValueGetTypeID() else { return nil }
        }
        return value as? T
    }

    private static func parameterized(_ element: AXUIElement, _ name: String, location: Int, length: Int) -> CFTypeRef? {
        guard location >= 0, length >= 0 else { return nil }
        var range = CFRange(location: location, length: length)
        guard let param = AXValueCreate(.cfRange, &range) else { return nil }
        var value: CFTypeRef?
        guard AXUIElementCopyParameterizedAttributeValue(element, name as CFString, param, &value) == .success else {
            return nil
        }
        return value
    }

    private static func string(_ element: AXUIElement, location: Int, length: Int) -> String? {
        parameterized(element, kAXStringForRangeParameterizedAttribute, location: location, length: length) as? String
    }

    private static func bounds(_ element: AXUIElement, location: Int, length: Int) -> CGRect? {
        guard let value = parameterized(element, kAXBoundsForRangeParameterizedAttribute, location: location, length: length),
              CFGetTypeID(value) == AXValueGetTypeID() else { return nil }
        var rect = CGRect.zero
        guard AXValueGetValue(value as! AXValue, .cgRect, &rect), rect.height > 1, rect.origin != .zero else { return nil }
        return rect
    }

    private static func caretRect(_ element: AXUIElement, caret: Int, previousIsNewline: Bool) -> CGRect? {
        var axRect: CGRect?
        if caret > 0, !previousIsNewline, let r = bounds(element, location: caret - 1, length: 1), r.width < 200 {
            // Right edge of the character before the caret.
            axRect = CGRect(x: r.maxX, y: r.minY, width: 1, height: r.height)
        } else if let r = bounds(element, location: caret, length: 0) {
            axRect = CGRect(x: r.minX, y: r.minY, width: 1, height: r.height)
        } else {
            axRect = markerCaretRect(element, previousIsNewline: previousIsNewline)
        }
        guard let axRect else { return nil }
        // Accessibility uses top-left origin on the primary screen.
        let primaryHeight = NSScreen.screens.first?.frame.height ?? 0
        return CGRect(x: axRect.minX, y: primaryHeight - axRect.maxY, width: axRect.width, height: axRect.height)
    }

    // MARK: Text markers (Chromium, Electron and WebKit)

    // Browser engines often don't answer the range-based queries above for
    // web text fields, but do answer the same questions in terms of opaque
    // "text markers".

    private static func parameterized(_ element: AXUIElement, _ name: String, _ param: CFTypeRef) -> CFTypeRef? {
        var value: CFTypeRef?
        guard AXUIElementCopyParameterizedAttributeValue(element, name as CFString, param, &value) == .success else {
            return nil
        }
        return value
    }

    /// Marker range covering the character just before the caret.
    private static func previousCharacterMarkerRange(_ element: AXUIElement) -> CFTypeRef? {
        var selected: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, "AXSelectedTextMarkerRange" as CFString, &selected) == .success,
              let selected,
              let caret = parameterized(element, "AXStartTextMarkerForTextMarkerRange", selected),
              let previous = parameterized(element, "AXPreviousTextMarkerForTextMarker", caret) else { return nil }
        return parameterized(element, "AXTextMarkerRangeForUnorderedTextMarkers", [previous, caret] as CFArray)
    }

    private static func markerCaretRect(_ element: AXUIElement, previousIsNewline: Bool) -> CGRect? {
        if !previousIsNewline, let range = previousCharacterMarkerRange(element),
           let value = parameterized(element, "AXBoundsForTextMarkerRange", range),
           CFGetTypeID(value) == AXValueGetTypeID() {
            var rect = CGRect.zero
            if AXValueGetValue(value as! AXValue, .cgRect, &rect), rect.height > 1, rect.origin != .zero, rect.width < 200 {
                return CGRect(x: rect.maxX, y: rect.minY, width: 1, height: rect.height)
            }
        }
        // Collapsed selection: some engines return the caret itself.
        var selected: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, "AXSelectedTextMarkerRange" as CFString, &selected) == .success,
              let selected,
              let value = parameterized(element, "AXBoundsForTextMarkerRange", selected),
              CFGetTypeID(value) == AXValueGetTypeID() else { return nil }
        var rect = CGRect.zero
        guard AXValueGetValue(value as! AXValue, .cgRect, &rect), rect.height > 1, rect.origin != .zero else { return nil }
        return CGRect(x: rect.minX, y: rect.minY, width: 1, height: rect.height)
    }

    // MARK: Style

    private static func textStyle(_ element: AXUIElement, caret: Int) -> TextStyle {
        guard caret > 0 else { return TextStyle() }
        var value = parameterized(element, kAXAttributedStringForRangeParameterizedAttribute, location: caret - 1, length: 1)
        if (value as? NSAttributedString)?.length ?? 0 == 0, let range = previousCharacterMarkerRange(element) {
            value = parameterized(element, "AXAttributedStringForTextMarkerRange", range)
        }
        guard let attributed = value as? NSAttributedString, attributed.length > 0 else { return TextStyle() }
        let attributes = attributed.attributes(at: 0, effectiveRange: nil)
        var style = TextStyle()

        // Keys of kAXFontTextAttribute / kAXForegroundColorTextAttribute.
        if let font = attributes[NSAttributedString.Key("AXFont")] as? [String: Any],
           let size = (font["AXFontSize"] as? NSNumber).map({ CGFloat($0.doubleValue) }), size >= 6, size <= 96 {
            let name = font["AXFontName"] as? String
            style.font = name.flatMap { NSFont(name: $0, size: size) } ?? .systemFont(ofSize: size)
        }
        if let raw = attributes[NSAttributedString.Key("AXForegroundColor")] {
            let ref = raw as CFTypeRef
            if CFGetTypeID(ref) == CGColor.typeID {
                style.color = NSColor(cgColor: ref as! CGColor)
            }
        }
        return style
    }
}
