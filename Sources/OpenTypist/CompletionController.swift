import AppKit

/// Ties keystrokes, the focused text field, the model and the overlay together.
/// Main thread only.
final class CompletionController {
    private let engine: EngineService
    private let overlay = OverlayWindow()
    private let settings = Settings.shared
    private let stats = Stats.shared
    private let status = AppStatus.shared

    private var suggestion: Suggestion?
    /// The part of `suggestion` currently drawn on screen.
    private var visibleText: String?
    private var refreshWork: DispatchWorkItem?
    /// After accepting, the target app may not have applied our keystrokes yet
    /// when we read it back; wait until at least `minConsumed` characters show up.
    private var pendingAccept: (minConsumed: Int, retries: Int)?
    /// Set by Escape; cleared by the next typing key.
    private var suppressed = false

    init(engine: EngineService) {
        self.engine = engine
    }

    // MARK: - Events

    /// Returns true to swallow the key.
    func handleKeyDown(_ event: CGEvent) -> Bool {
        guard settings.enabled else {
            dismiss()
            return false
        }
        guard engine.isReady else {
            status.note("Typing seen, but the model isn't loaded yet")
            dismiss()
            return false
        }
        let keyCode = event.getIntegerValueField(.keyboardEventKeycode)
        let flags = event.flags
        let commandLike = flags.contains(.maskCommand) || flags.contains(.maskControl)

        if overlay.isVisible, let visible = visibleText, !commandLike {
            if keyCode == KeyCode.tab, !flags.contains(.maskAlternate) {
                if flags.contains(.maskShift) {
                    accept(visible)
                } else {
                    accept(Suggestion.nextWord(of: visible, includeTrailingSpace: settings.acceptTrailingSpace))
                }
                return true
            }
            if keyCode == KeyCode.escape {
                dismiss()
                suppressed = true
                return true
            }
        }

        if commandLike || keyCode == KeyCode.escape {
            dismiss()
            return false
        }
        suppressed = false
        engine.cancelAll()

        // Keep the ghost text while the user types exactly what it suggests:
        // the typed character lands on top of its ghost twin, and the next
        // refresh moves the rest behind the caret.
        let typed = event.typedCharacters
        if let visible = visibleText, !typed.isEmpty, visible.hasPrefix(typed), visible.count > typed.count {
            visibleText = String(visible.dropFirst(typed.count))
        } else {
            hideOverlay()
        }
        scheduleRefresh(after: 0.06)
        return false
    }

    /// A click may move the caret, so drop the suggestion. A new one comes
    /// with the next keystroke, never from a click alone.
    func handleMouseDown() {
        dismiss()
    }

    func dismiss() {
        refreshWork?.cancel()
        engine.cancelAll()
        suggestion = nil
        pendingAccept = nil
        hideOverlay()
    }

    // MARK: - Accepting

    private func accept(_ chunk: String) {
        guard !chunk.isEmpty, let suggestion, let visible = visibleText else { return }
        let consumedBefore = suggestion.text.count - visible.count
        hideOverlay()
        TextInserter.type(chunk)
        stats.recordAccepted(chunk)
        pendingAccept = (consumedBefore + chunk.count, 8)
        scheduleRefresh(after: 0.04)
    }

    // MARK: - Refreshing

    private func scheduleRefresh(after delay: TimeInterval) {
        refreshWork?.cancel()
        let work = DispatchWorkItem { [weak self] in self?.refresh() }
        refreshWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + delay, execute: work)
    }

    private func skip(_ reason: String) {
        status.note(reason)
        dismiss()
    }

    private func refresh() {
        guard settings.enabled, engine.isReady, !suppressed else { return dismiss() }

        let ctx: FocusContext
        switch FocusReader.read() {
        case .success(let value): ctx = value
        case .failure(let failure): return skip(failure.description)
        }
        if settings.isDisabled(bundleID: ctx.bundleID) { return skip("\(ctx.appName): turned off for this app") }
        guard let caret = ctx.caretRect else { return skip("\(ctx.appName): the app doesn't report where the caret is") }

        let before = ctx.textBefore
        // Only complete at the end of a line, with some text to go on.
        guard ctx.textAfterOnLine.trimmingCharacters(in: .whitespaces).isEmpty else {
            return skip("\(ctx.appName): caret is in the middle of a line")
        }
        guard let last = before.last, !last.isNewline,
              before.trimmingCharacters(in: .whitespacesAndNewlines).count >= 3 else {
            return skip("\(ctx.appName): not enough text before the caret yet")
        }

        if let current = suggestion, current.pid == ctx.pid {
            if let pending = pendingAccept {
                let consumed = current.consumedCount(textBefore: before) ?? -1
                if consumed < pending.minConsumed, pending.retries > 0 {
                    pendingAccept = (pending.minConsumed, pending.retries - 1)
                    scheduleRefresh(after: 0.04)
                    return
                }
                pendingAccept = nil
            }
            if let rest = current.remaining(textBefore: before) {
                show(rest, at: caret, style: ctx.style)
                return
            }
        }

        suggestion = nil
        pendingAccept = nil
        hideOverlay()

        let request = EngineService.Request(
            textBefore: before,
            appName: settings.useAppContext ? ctx.appName : nil,
            windowTitle: settings.useAppContext ? ctx.windowTitle : nil,
            screenText: ScreenContext.shared.text(for: ctx),
            length: settings.length
        )
        let started = Date()
        engine.complete(request) { [weak self] text in
            guard let self, self.settings.enabled else { return }
            let ms = Int(Date().timeIntervalSince(started) * 1000)
            guard let text else {
                self.status.note("\(ctx.appName): the model had no good suggestion (\(ms) ms)")
                return
            }
            self.suggestion = Suggestion(textBefore: before, text: text, pid: ctx.pid)
            self.show(text, at: caret, style: ctx.style)
            self.stats.recordShown()
            self.status.note("\(ctx.appName): showing a suggestion (\(ms) ms)")
        }
    }

    // MARK: - Overlay

    private func show(_ text: String, at caret: CGRect, style: TextStyle) {
        visibleText = text
        overlay.show(text, at: caret, style: style)
    }

    private func hideOverlay() {
        visibleText = nil
        overlay.hide()
    }
}
