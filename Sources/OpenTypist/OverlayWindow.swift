import AppKit

/// Floating, click-through panel that draws the suggestion as ghost text just
/// after the caret, followed by a small Tab hint. It has no background so the
/// text reads as part of the field: it uses the field's own font and a faded
/// version of the field's text color when the app reports them.
final class OverlayWindow {
    private let panel: NSPanel
    private let label = NSTextField(labelWithString: "")
    private let hint = NSTextField(labelWithString: "tab")

    private(set) var isVisible = false

    /// Used when the app doesn't report its text color.
    private static let fallbackColor = NSColor(white: 0.5, alpha: 0.9)
    /// How much of the real text color the ghost text keeps.
    private static let ghostAlpha: CGFloat = 0.45

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

        label.lineBreakMode = .byClipping
        label.drawsBackground = false
        hint.wantsLayer = true
        hint.layer?.cornerRadius = 3
        hint.layer?.borderWidth = 1
        hint.alignment = .center
        hint.drawsBackground = false

        let content = NSView()
        content.addSubview(label)
        content.addSubview(hint)
        panel.contentView = content
    }

    func show(_ text: String, at caret: CGRect, style: TextStyle) {
        let font = style.font ?? .systemFont(ofSize: min(28, max(11, caret.height * 0.78)))
        let color = style.color.map { Self.ghostColor(from: $0) } ?? Self.fallbackColor

        label.font = font
        label.textColor = color
        label.stringValue = text
        hint.font = .systemFont(ofSize: max(8, font.pointSize * 0.55), weight: .medium)
        hint.textColor = color
        hint.layer?.borderColor = color.cgColor

        let textSize = label.fittingSize
        let hintSize = NSSize(width: hint.fittingSize.width + 6, height: hint.fittingSize.height)
        let gap: CGFloat = 6
        let height = max(textSize.height, caret.height)
        let width = textSize.width + gap + hintSize.width + 2

        label.frame = NSRect(x: 0, y: (height - textSize.height) / 2, width: textSize.width, height: textSize.height)
        hint.frame = NSRect(x: label.frame.maxX + gap, y: (height - hintSize.height) / 2, width: hintSize.width, height: hintSize.height)

        // NSTextField labels have a 2pt inset; shift left so the ghost text
        // starts right at the caret.
        var origin = NSPoint(x: caret.maxX - 2, y: caret.midY - height / 2)
        if let screen = NSScreen.screens.first(where: { $0.frame.contains(caret.origin) }) {
            origin.x = min(origin.x, screen.visibleFrame.maxX - width)
        }
        panel.setFrame(NSRect(origin: origin, size: NSSize(width: width, height: height)), display: true)
        panel.orderFrontRegardless()
        isVisible = true
    }

    func hide() {
        guard isVisible else { return }
        panel.orderOut(nil)
        isVisible = false
    }

    /// The field's text color, faded: white text gets light grey ghost text,
    /// black text gets mid grey.
    private static func ghostColor(from color: NSColor) -> NSColor {
        guard let rgb = color.usingColorSpace(.sRGB) else { return fallbackColor }
        return rgb.withAlphaComponent(rgb.alphaComponent * ghostAlpha)
    }
}
