import AppKit
import ApplicationServices

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
    /// Caret rectangle in Cocoa screen coordinates (origin bottom-left).
    let caretRect: CGRect?
    let style: TextStyle
}

enum FocusFailure: Error, CustomStringConvertible {
    case noFrontApp
    case noFocusedElement(app: String)
    case secureField(app: String)
    case noTextSelection(app: String, role: String)
    case textSelected(app: String)
    case unreadableText(app: String, role: String)

    var description: String {
        switch self {
        case .noFrontApp: return "No active app"
        case .noFocusedElement(let app): return "\(app): no focused text field found"
        case .secureField(let app): return "\(app): password field, skipped"
        case .noTextSelection(let app, let role): return "\(app): focused element (\(role)) doesn't report a caret"
        case .textSelected(let app): return "\(app): text is selected"
        case .unreadableText(let app, let role): return "\(app): couldn't read the text of \(role)"
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
        return .success(FocusContext(
            pid: pid,
            bundleID: app.bundleIdentifier,
            appName: appName,
            windowTitle: title,
            role: role,
            textBefore: before,
            textAfterOnLine: after,
            caretRect: caretRect(element, caret: caret, previousIsNewline: previousIsNewline),
            style: textStyle(element, caret: caret)
        ))
    }

    // MARK: - Helpers

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
