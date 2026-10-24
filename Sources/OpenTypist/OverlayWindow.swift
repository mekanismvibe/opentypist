import AppKit

/// Where ghost text may go: from the caret to `rightEdge` on the first line,
/// then from `lineStartX` to `rightEdge` on the lines below.
struct WrapGuide {
    var lineStartX: CGFloat
    var rightEdge: CGFloat
}

/// Floating, click-through panel that draws the suggestion as ghost text just
/// after the caret, followed by a small Tab hint. It has no background so the
/// text reads as part of the field: it uses the field's own font and a faded
/// version of the field's text color when the app reports them. Text that
/// doesn't fit before the field's edge (or a button inside it) wraps onto the
/// next lines, like the app's own text would.
final class OverlayWindow {
    private let panel: NSPanel
    private let view = GhostTextView()

    private(set) var isVisible = false

    /// Used when the app doesn't report its text color.
    private static let fallbackColor = NSColor(white: 0.5, alpha: 0.9)
    /// How much of the real text color the ghost text keeps.
    private static let ghostAlpha: CGFloat = 0.45
    private static let maxLines = 4

    init() {
        panel = NSPanel(
            contentRect: NSRect(x: 0, y: 0, width: 10, height: 10),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.ignoresMouseEvents = true
        panel.hidesOnDeactivate = false
        panel.isReleasedWhenClosed = false
        panel.animationBehavior = .none
        panel.level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.popUpMenuWindow)))
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        panel.contentView = view
    }

    static func font(for style: TextStyle, caret: CGRect) -> NSFont {
        style.font ?? .systemFont(ofSize: min(28, max(11, caret.height * 0.78)))
    }

    /// Width of `text` on one line, including the accept-key hint.
    func width(of text: String, style: TextStyle, caret: CGRect, hint: String = "tab") -> CGFloat {
        let font = Self.font(for: style, caret: caret)
        return GhostTextView.measure(text, font: font) + GhostTextView.hintWidth(hint, font: font) + GhostTextView.hintGap
    }

    /// `hint` names the accept key. With `bubble`, the suggestion is drawn on
    /// a small card just below the caret's line instead of inline (used in
    /// the middle of a line, where inline text would cover what follows).
    func show(_ text: String, at caret: CGRect, style: TextStyle, wrap: WrapGuide?, hint: String = "tab", bubble: Bool = false) {
        if bubble { return showBubble(text, at: caret, style: style, hint: hint) }
        let font = Self.font(for: style, caret: caret)
        let color = style.color.map { Self.ghostColor(from: $0) } ?? Self.fallbackColor
        let lineHeight = max(caret.height, NSLayoutManager().defaultLineHeight(for: font))
        let hintWidth = GhostTextView.hintWidth(hint, font: font) + GhostTextView.hintGap

        // Lay out the words: first line from the caret, the rest from the line start.
        var lines: [String] = []
        if let wrap, wrap.rightEdge - wrap.lineStartX > 40 {
            lines = Self.wrap(text, font: font, firstWidth: wrap.rightEdge - caret.maxX,
                              otherWidth: wrap.rightEdge - wrap.lineStartX)
        } else {
            lines = [text]
        }
        if lines.count > Self.maxLines { lines = Array(lines.prefix(Self.maxLines)) }

        let left = wrap.map { min($0.lineStartX, caret.maxX) } ?? caret.maxX
        var placed: [(text: String, x: CGFloat)] = []
        var maxX = caret.maxX
        for (index, line) in lines.enumerated() {
            let x = index == 0 ? caret.maxX : (wrap?.lineStartX ?? caret.maxX)
            placed.append((line, x - left))
            maxX = max(maxX, x + GhostTextView.measure(line, font: font))
        }
        // The hint goes after the last word if it fits.
        let lastEnd = (placed.last.map { $0.x + left + GhostTextView.measure($0.text, font: font) }) ?? caret.maxX
        let showHint = wrap.map { lastEnd + hintWidth <= $0.rightEdge + 2 } ?? true
        if showHint { maxX = max(maxX, lastEnd + hintWidth) }

        let height = lineHeight * CGFloat(placed.count)
        var frame = NSRect(x: left, y: caret.maxY - height, width: maxX - left + 2, height: height)
        if let screen = NSScreen.screens.first(where: { $0.frame.contains(caret.origin) }), wrap == nil {
            frame.origin.x = min(frame.origin.x, screen.visibleFrame.maxX - frame.width)
        }

        view.configure(lines: placed, font: font, color: color, lineHeight: lineHeight, hint: showHint ? hint : nil)
        panel.setFrame(frame, display: false)
        view.needsDisplay = true
        panel.orderFrontRegardless()
        isVisible = true
    }

    private func showBubble(_ text: String, at caret: CGRect, style: TextStyle, hint: String) {
        let font = Self.font(for: style, caret: caret)
        let padding = GhostTextView.bubblePadding
        let lineHeight = NSLayoutManager().defaultLineHeight(for: font) + 4
        let trimmed = text.drop(while: \.isWhitespace)
        let width = GhostTextView.measure(String(trimmed), font: font) + GhostTextView.hintGap
            + GhostTextView.hintWidth(hint, font: font) + padding * 2
        var frame = NSRect(x: caret.maxX - padding, y: caret.minY - lineHeight - 3, width: width, height: lineHeight)
        if let screen = NSScreen.screens.first(where: { $0.frame.contains(caret.origin) }) {
            frame.origin.x = min(frame.origin.x, screen.visibleFrame.maxX - width)
        }
        view.configure(lines: [(String(trimmed), padding)], font: font, color: .secondaryLabelColor,
                       lineHeight: lineHeight, hint: hint, bubble: true)
        panel.setFrame(frame, display: false)
        view.needsDisplay = true
        panel.orderFrontRegardless()
        isVisible = true
    }

    func hide() {
        guard isVisible else { return }
        panel.orderOut(nil)
        isVisible = false
    }

    /// Greedy word wrap. The first line may stay empty when not even one word
    /// fits after the caret; continuation lines drop their leading space.
    static func wrap(_ text: String, font: NSFont, firstWidth: CGFloat, otherWidth: CGFloat) -> [String] {
        var lines: [String] = []
        var current = ""
        var available = firstWidth
        for word in PromptBuilder.splitWords(text) {
            let candidate = current + word
            if GhostTextView.measure(candidate, font: font) <= available || (current.isEmpty && !lines.isEmpty) {
                current = current.isEmpty && !lines.isEmpty ? String(word.drop(while: \.isWhitespace)) : candidate
            } else {
                lines.append(current)
                current = String(word.drop(while: \.isWhitespace))
                available = otherWidth
            }
        }
        lines.append(current)
        return lines
    }

    /// The field's text color, faded: white text gets light grey ghost text,
    /// black text gets mid grey.
    private static func ghostColor(from color: NSColor) -> NSColor {
        guard let rgb = color.usingColorSpace(.sRGB) else { return fallbackColor }
        return rgb.withAlphaComponent(rgb.alphaComponent * ghostAlpha)
    }
}

/// Draws the laid-out ghost text lines and the accept-key hint.
private final class GhostTextView: NSView {
    static let hintGap: CGFloat = 6
    static let bubblePadding: CGFloat = 7

    private var lines: [(text: String, x: CGFloat)] = []
    private var font = NSFont.systemFont(ofSize: 13)
    private var color = NSColor.gray
    private var lineHeight: CGFloat = 16
    private var hint: String?
    private var bubble = false

    override var isFlipped: Bool { true }

    static func measure(_ text: String, font: NSFont) -> CGFloat {
        ceil((text as NSString).size(withAttributes: [.font: font]).width)
    }

    static func hintFont(for font: NSFont) -> NSFont {
        .systemFont(ofSize: max(8, font.pointSize * 0.55), weight: .medium)
    }

    static func hintWidth(_ hint: String, font: NSFont) -> CGFloat {
        measure(hint, font: hintFont(for: font)) + 8
    }

    func configure(lines: [(text: String, x: CGFloat)], font: NSFont, color: NSColor, lineHeight: CGFloat,
                   hint: String?, bubble: Bool = false) {
        self.lines = lines
        self.font = font
        self.color = color
        self.lineHeight = lineHeight
        self.hint = hint
        self.bubble = bubble
    }

    override func draw(_ dirtyRect: NSRect) {
        if bubble {
            let card = NSBezierPath(roundedRect: bounds.insetBy(dx: 0.5, dy: 0.5), xRadius: 5, yRadius: 5)
            NSColor.windowBackgroundColor.withAlphaComponent(0.97).setFill()
            card.fill()
            NSColor.separatorColor.setStroke()
            card.stroke()
        }
        let attributes: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: color]
        let textHeight = NSLayoutManager().defaultLineHeight(for: font)
        for (index, line) in lines.enumerated() {
            let y = CGFloat(index) * lineHeight + (lineHeight - textHeight) / 2
            (line.text as NSString).draw(at: NSPoint(x: line.x, y: y), withAttributes: attributes)
        }
        guard let hint, let last = lines.last else { return }

        let hintFont = Self.hintFont(for: font)
        let hintAttributes: [NSAttributedString.Key: Any] = [.font: hintFont, .foregroundColor: color]
        let hintText = hint as NSString
        let hintSize = hintText.size(withAttributes: hintAttributes)
        let lastIndex = CGFloat(lines.count - 1)
        let x = last.x + Self.measure(last.text, font: font) + Self.hintGap
        let box = NSRect(x: x, y: lastIndex * lineHeight + (lineHeight - hintSize.height - 2) / 2,
                         width: hintSize.width + 8, height: hintSize.height + 2)
        color.setStroke()
        let path = NSBezierPath(roundedRect: box.insetBy(dx: 0.5, dy: 0.5), xRadius: 3, yRadius: 3)
        path.lineWidth = 1
        path.stroke()
        hintText.draw(at: NSPoint(x: box.minX + 4, y: box.minY + 1), withAttributes: hintAttributes)
    }
}
