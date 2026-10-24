import AppKit
import ScreenCaptureKit
import Vision

/// Reads the visible text of the window being typed in, so prompts can
/// include the email thread or chat messages around the text field. Needs
/// the Screen & System Audio Recording permission (only the screen is used).
///
/// The window is captured and OCR'd in the background once per window (and
/// again after a while); prompts use the last result, so a keystroke never
/// waits for it. Images and text are never written to disk.
final class ScreenContext {
    static let shared = ScreenContext()

    /// Characters of on-screen text to include in a prompt.
    static let budget = 900
    private static let maxAge: TimeInterval = 90

    private struct Entry {
        let key: String
        let text: String
        let date: Date
    }

    private var entry: Entry?
    private var inFlightKey: String?

    static var hasPermission: Bool { CGPreflightScreenCaptureAccess() }

    static func requestPermission() {
        if !CGRequestScreenCaptureAccess() { openSettings() }
    }

    static func openSettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture") {
            NSWorkspace.shared.open(url)
        }
    }

    /// Main thread. Visible text of the window around the caret, or nil while
    /// it isn't available yet (or the feature is off).
    func text(for context: FocusContext) -> String? {
        guard Settings.shared.useScreenContext, Self.hasPermission,
              let window = Self.frontWindow(pid: context.pid) else { return nil }
        let key = "\(context.pid):\(window.id):\(context.windowTitle ?? "")"
        if let entry, entry.key == key {
            if Date().timeIntervalSince(entry.date) > Self.maxAge { capture(window, key: key, context: context) }
            return entry.text.isEmpty ? nil : entry.text
        }
        capture(window, key: key, context: context)
        return nil
    }

    private func capture(_ window: (id: CGWindowID, bounds: CGRect), key: String, context: FocusContext) {
        guard inFlightKey != key else { return }
        inFlightKey = key
        let primaryHeight = NSScreen.screens.first?.frame.height ?? 0
        let caretY = context.caretRect.map { primaryHeight - $0.midY }
        let typed = Self.normalize(String(context.textBefore.suffix(2000)))

        Task.detached(priority: .utility) {
            let text = (try? await Self.recognize(window: window, caretY: caretY, typed: typed)) ?? ""
            DispatchQueue.main.async {
                if self.inFlightKey == key { self.inFlightKey = nil }
                self.entry = Entry(key: key, text: text, date: Date())
                AppStatus.shared.note("Read \(text.count) characters of on-screen context")
            }
        }
    }

    /// Frontmost normal window of the app, in global top-left coordinates.
    static func frontWindow(pid: pid_t) -> (id: CGWindowID, bounds: CGRect)? {
        guard let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID)
            as? [[String: Any]] else { return nil }
        for info in list {
            guard (info[kCGWindowOwnerPID as String] as? NSNumber)?.int32Value == pid,
                  (info[kCGWindowLayer as String] as? NSNumber)?.intValue == 0,
                  let number = (info[kCGWindowNumber as String] as? NSNumber)?.uint32Value,
                  let boundsInfo = info[kCGWindowBounds as String] as? NSDictionary,
                  let bounds = CGRect(dictionaryRepresentation: boundsInfo as CFDictionary),
                  bounds.width > 80, bounds.height > 80 else { continue }
            return (number, bounds)
        }
        return nil
    }

    private static func recognize(window: (id: CGWindowID, bounds: CGRect), caretY: CGFloat?, typed: String) async throws -> String {
        let content = try await SCShareableContent.excludingDesktopWindows(true, onScreenWindowsOnly: true)
        guard let scWindow = content.windows.first(where: { $0.windowID == window.id }) else { return "" }
        let filter = SCContentFilter(desktopIndependentWindow: scWindow)
        let config = SCStreamConfiguration()
        let scale = CGFloat(filter.pointPixelScale)
        config.width = Int(window.bounds.width * scale)
        config.height = Int(window.bounds.height * scale)
        config.showsCursor = false
        let image = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)

        let request = VNRecognizeTextRequest()
        request.recognitionLevel = .accurate
        request.usesLanguageCorrection = true
        try VNImageRequestHandler(cgImage: image).perform([request])

        let bounds = window.bounds
        var lines: [(text: String, x: CGFloat, y: CGFloat)] = (request.results ?? []).compactMap { observation in
            guard let candidate = observation.topCandidates(1).first, candidate.confidence > 0.3 else { return nil }
            let box = observation.boundingBox // normalized, origin bottom-left
            return (candidate.string, bounds.minX + box.minX * bounds.width, bounds.minY + (1 - box.midY) * bounds.height)
        }

        // Drop what the user typed themselves; it is already in the prompt.
        lines.removeAll { line in
            let normalized = normalize(line.text)
            return normalized.count >= 4 && typed.contains(normalized)
        }

        // Keep the lines nearest the caret, then restore reading order.
        if let caretY { lines.sort { abs($0.y - caretY) < abs($1.y - caretY) } }
        var kept: [(text: String, x: CGFloat, y: CGFloat)] = []
        var used = 0
        for line in lines {
            if used + line.text.count + 1 > budget { break }
            kept.append(line)
            used += line.text.count + 1
        }
        kept.sort { (Int($0.y / 6), $0.x) < (Int($1.y / 6), $1.x) }
        return kept.map(\.text).joined(separator: "\n")
    }

    private static func normalize(_ text: String) -> String {
        text.lowercased().split(whereSeparator: { $0.isWhitespace }).joined(separator: " ")
    }
}
