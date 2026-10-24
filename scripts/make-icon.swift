// Draws the app icon and writes an .icns file.
// Usage: swift scripts/make-icon.swift Resources/AppIcon.icns
import AppKit

let output = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "AppIcon.icns"
let fm = FileManager.default
let iconset = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("OpenTypist.iconset")
try? fm.removeItem(at: iconset)
try fm.createDirectory(at: iconset, withIntermediateDirectories: true)

func render(_ size: CGFloat) -> Data {
    let rep = NSBitmapImageRep(
        bitmapDataPlanes: nil, pixelsWide: Int(size), pixelsHigh: Int(size), bitsPerSample: 8,
        samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0
    )!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)

    let inset = size * 0.09
    let rect = NSRect(x: inset, y: inset, width: size - inset * 2, height: size - inset * 2)
    let path = NSBezierPath(roundedRect: rect, xRadius: rect.width * 0.22, yRadius: rect.width * 0.22)
    NSGradient(colors: [NSColor(calibratedRed: 0.16, green: 0.20, blue: 0.32, alpha: 1),
                        NSColor(calibratedRed: 0.08, green: 0.10, blue: 0.17, alpha: 1)])!
        .draw(in: path, angle: -90)

    // Typed text, then the ghost suggestion and a caret.
    let fontSize = rect.height * 0.30
    let font = NSFont.systemFont(ofSize: fontSize, weight: .semibold)
    let typed = NSAttributedString(string: "Hi", attributes: [.font: font, .foregroundColor: NSColor.white])
    let ghost = NSAttributedString(string: " th", attributes: [.font: font, .foregroundColor: NSColor(white: 1, alpha: 0.35)])
    let typedSize = typed.size()
    let total = typedSize.width + ghost.size().width
    let origin = NSPoint(x: rect.midX - total / 2, y: rect.midY - typedSize.height / 2)
    typed.draw(at: origin)
    ghost.draw(at: NSPoint(x: origin.x + typedSize.width, y: origin.y))

    let caretX = origin.x + typedSize.width + size * 0.004
    NSColor(calibratedRed: 0.45, green: 0.75, blue: 1, alpha: 1).setFill()
    NSBezierPath(roundedRect: NSRect(x: caretX, y: origin.y + typedSize.height * 0.12, width: max(1, size * 0.018), height: typedSize.height * 0.78),
                 xRadius: size * 0.008, yRadius: size * 0.008).fill()

    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

for base in [16, 32, 128, 256, 512] {
    for scale in [1, 2] {
        let name = scale == 1 ? "icon_\(base)x\(base).png" : "icon_\(base)x\(base)@2x.png"
        try render(CGFloat(base * scale)).write(to: iconset.appendingPathComponent(name))
    }
}

let task = Process()
task.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
task.arguments = ["-c", "icns", iconset.path, "-o", output]
try task.run()
task.waitUntilExit()
exit(task.terminationStatus)
