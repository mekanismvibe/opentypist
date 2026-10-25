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
    /// when we read it back; wait until the text ends with `expectedTail`.
    private var pendingAccept: (expectedTail: String, retries: Int)?
    /// Where the first non-text element (a button) sits on the caret's line,
    /// so we don't probe the screen on every keystroke.
    private var obstacleCache: (key: String, x: CGFloat?, checkedTo: CGFloat)?
    /// Set by Escape; cleared by the next typing key.
    private var suppressed = false

    /// Called with text inserted by accepting a suggestion.
    var onAccept: ((String) -> Void)?

    init(engine: EngineService) {
        self.engine = engine
    }

    // MARK: - Events

    /// Tab, Shift-Tab and Esc while a suggestion is showing. Returns true if
    /// the key was used (and must be swallowed).
    func handleSuggestionKey(_ event: CGEvent) -> Bool {
        guard overlay.isVisible, let visible = visibleText else { return false }
        let keys = settings.acceptKeys(for: NSWorkspace.shared.frontmostApplication?.bundleIdentifier)
        if keys.all.matches(event) {
            accept(visible)
            return true
        }
        if keys.word.matches(event) {
            accept(Suggestion.nextWord(of: visible, includeTrailingSpace: settings.acceptTrailingSpace))
            return true
        }
        let flags = event.flags
        if event.getIntegerValueField(.keyboardEventKeycode) == KeyCode.escape,
           !flags.contains(.maskCommand), !flags.contains(.maskControl) {
            dismiss()
            suppressed = true
            return true
        }
        return false
    }

    /// Any other key: update or drop the suggestion and schedule a refresh.
    func handleKeyDown(_ event: CGEvent) {
        guard settings.enabled else { return dismiss() }
        guard engine.isReady else {
            status.note("Typing seen, but the model isn't loaded yet")
            return dismiss()
        }
        let keyCode = event.getIntegerValueField(.keyboardEventKeycode)
        let flags = event.flags
        if flags.contains(.maskCommand) || flags.contains(.maskControl) || keyCode == KeyCode.escape {
            return dismiss()
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
    }

    /// Autocorrect replaced text with keystrokes we don't see; re-read the
    /// field once the app has applied them.
    func textChangedExternally() {
        engine.cancelAll()
        suggestion = nil
        pendingAccept = nil
        hideOverlay()
        guard settings.enabled, engine.isReady else { return }
        scheduleRefresh(after: 0.12)
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
        // Accepting what's visible ends a suggestion that's still streaming.
        engine.cancelAll()
        let consumedBefore = suggestion.text.count - visible.count
        var inserted = chunk
        // Finishing the suggestion: optionally add a space so typing can go on.
        if chunk.count >= visible.count, settings.acceptTrailingSpace, inserted.last?.isWhitespace == false {
            inserted += " "
        }
        hideOverlay()
        TextInserter.type(inserted)
        onAccept?(inserted)
        stats.recordAccepted(inserted)
        let tail = suggestion.anchorTail + String(suggestion.text.prefix(consumedBefore)) + inserted
        pendingAccept = (String(tail.suffix(40)), 8)
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
        WritingSamples.shared.observe(ctx)
        guard let caret = ctx.caretRect else { return skip("\(ctx.appName): the app doesn't report where the caret is") }

        let before = ctx.textBefore
        // In the middle of a line, suggest short insertions that lead into
        // the text after the caret (never inside a word).
        let midLine = !ctx.textAfterOnLine.trimmingCharacters(in: .whitespaces).isEmpty
        if midLine {
            guard settings.midLineSuggestions else { return skip("\(ctx.appName): caret is in the middle of a line") }
            if let next = ctx.textAfterOnLine.first, next.isLetter || next.isNumber {
                return skip("\(ctx.appName): caret is inside a word")
            }
        }
        let textAfter = midLine ? ctx.textAfterOnLine : ""
        guard let last = before.last, !last.isNewline,
              before.trimmingCharacters(in: .whitespacesAndNewlines).count >= 3 else {
            return skip("\(ctx.appName): not enough text before the caret yet")
        }

        if let pending = pendingAccept {
            if !before.hasSuffix(pending.expectedTail), pending.retries > 0 {
                pendingAccept = (pending.expectedTail, pending.retries - 1)
                scheduleRefresh(after: 0.04)
                return
            }
            pendingAccept = nil
        }
        if let current = suggestion, current.pid == ctx.pid, let rest = current.remaining(textBefore: before) {
            show(rest, context: ctx, caret: caret)
            return
        }

        suggestion = nil
        pendingAccept = nil
        hideOverlay()

        let request = EngineService.Request(
            textBefore: before,
            appName: settings.useAppContext ? ctx.appName : nil,
            windowTitle: settings.useAppContext ? ctx.windowTitle : nil,
            screenText: ScreenContext.shared.text(for: ctx),
            profile: Personalization.shared.promptProfile,
            examples: WritingSamples.shared.examples(bundleID: ctx.bundleID, windowTitle: ctx.windowTitle),
            length: midLine ? .short : settings.length,
            minProbability: settings.confidence.minProbability,
            wordMinProbability: settings.confidence.wordMinProbability,
            textAfter: textAfter
        )
        let started = Date()
        var firstShownMs: Int?
        // Show each complete word as soon as it exists, then the final text.
        // Returns whether it was shown.
        let present: (String) -> Bool = { [weak self] text in
            guard let self, self.settings.enabled else { return false }
            guard PromptBuilder.makesRealWord(textBefore: before, suggestion: text, isKnown: {
                SpellCheck.isValidWord($0) || Personalization.shared.isNeverCorrect($0)
            }) else {
                self.suggestion = nil
                self.hideOverlay()
                self.status.note("\(ctx.appName): dropped a suggestion that misspells the word being typed")
                return false
            }
            self.suggestion = Suggestion(textBefore: before, text: text, pid: ctx.pid)
            self.show(text, context: ctx, caret: caret)
            if firstShownMs == nil {
                firstShownMs = Int(Date().timeIntervalSince(started) * 1000)
                self.stats.recordShown()
            }
            return true
        }
        engine.complete(request, onPartial: { _ = present($0) }) { [weak self] text in
            guard let self else { return }
            let ms = Int(Date().timeIntervalSince(started) * 1000)
            if let text {
                if present(text) {
                    self.status.note("\(ctx.appName): suggestion shown after \(firstShownMs ?? ms) ms, complete after \(ms) ms")
                }
            } else if firstShownMs == nil {
                self.status.note("\(ctx.appName): the model had no good suggestion (\(ms) ms)")
            }
        }
    }

    // MARK: - Overlay

    private func show(_ text: String, context: FocusContext, caret: CGRect) {
        visibleText = text
        let keys = settings.acceptKeys(for: context.bundleID)
        var shown = text
        var anchor = caret
        // Mid-line, ghost text would sit on top of the text after the caret,
        // so the suggestion goes in a small bubble just below the line.
        var bubble = !context.textAfterOnLine.trimmingCharacters(in: .whitespaces).isEmpty
        // macOS draws its own prediction right after the caret. When ours
        // carries on from it, draw only the rest, after its gray text; when
        // they disagree, keep out of its way in the bubble.
        let prediction = context.hostPrediction
        if !bubble, !prediction.isEmpty {
            if text.hasPrefix(prediction) {
                shown = String(text.dropFirst(prediction.count))
                // Nothing beyond what macOS already shows: leave it to macOS.
                guard shown.contains(where: { !$0.isWhitespace }) else { return hideOverlay() }
                // Ghost text starts at the caret's right edge; macOS's starts at its left.
                let font = OverlayWindow.font(for: context.style, caret: caret)
                anchor.origin.x += (prediction as NSString).size(withAttributes: [.font: font]).width - caret.width
            } else {
                bubble = true
            }
        }
        if bubble {
            overlay.show(shown, at: anchor, style: context.style, wrap: nil, hint: keys.word.hintLabel, bubble: true)
        } else {
            overlay.show(shown, at: anchor, style: context.style, wrap: wrapGuide(for: shown, context: context, caret: anchor),
                         hint: keys.word.hintLabel)
        }
    }

    /// Where multi-line fields wrap: the field's right edge (less the same
    /// padding it has on the left), or earlier if a button sits on the line.
    /// Single-line fields scroll instead of wrapping, so they get none.
    private func wrapGuide(for text: String, context ctx: FocusContext, caret: CGRect) -> WrapGuide? {
        guard let frame = ctx.elementFrame,
              !["AXTextField", "AXComboBox", "AXSearchField"].contains(ctx.role) else { return nil }
        let lineStart = ctx.lineStartX ?? frame.minX + 4
        let padding = max(2, min(40, lineStart - frame.minX))
        var right = frame.maxX - padding
        if let screen = NSScreen.screens.first(where: { $0.frame.contains(caret.origin) }) {
            right = min(right, screen.visibleFrame.maxX - 4)
        }

        let hint = settings.acceptKeys(for: ctx.bundleID).word.hintLabel
        let needed = min(right, caret.maxX + overlay.width(of: text, style: ctx.style, caret: caret, hint: hint))
        let key = "\(ctx.pid):\(CFHash(ctx.element)):\(Int(caret.midY))"
        var obstacle: CGFloat?
        if let cache = obstacleCache, cache.key == key, cache.x != nil || cache.checkedTo >= needed {
            obstacle = cache.x
        } else {
            let from = obstacleCache?.key == key ? max(caret.maxX, obstacleCache!.checkedTo) : caret.maxX
            obstacle = FocusReader.obstacleX(in: ctx, from: from, to: needed)
            obstacleCache = (key, obstacle, max(needed, obstacleCache?.key == key ? obstacleCache!.checkedTo : 0))
            if obstacle != nil { status.note("\(ctx.appName): wrapping the suggestion before a button") }
        }
        if let obstacle { right = min(right, obstacle - 6) }
        guard right - lineStart > 40 else { return nil }
        return WrapGuide(lineStartX: lineStart, rightEdge: right)
    }

    private func hideOverlay() {
        visibleText = nil
        overlay.hide()
    }
}
