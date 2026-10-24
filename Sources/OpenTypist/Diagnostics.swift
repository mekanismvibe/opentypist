import Combine
import Foundation
import os

/// Observable app-wide status for the menu and the settings window.
final class AppStatus: ObservableObject {
    static let shared = AppStatus()

    @Published var engine: EngineService.State = .idle
    @Published var keyboardTapRunning = false
    /// How long the last model load took (shown next to the unload option).
    @Published var lastModelLoadSeconds: Double?
    /// For the settings window (list counts, importing).
    var autocorrector: Autocorrector?
    /// What happened on the last completion attempt, in plain words. Never
    /// contains the user's text.
    @Published private(set) var lastCheck = "Waiting for you to type"

    private let logger = Logger(subsystem: "org.opentypist.OpenTypist", category: "completion")

    func note(_ message: String) {
        guard message != lastCheck else { return }
        lastCheck = message
        logger.notice("\(message, privacy: .public)")
    }
}
