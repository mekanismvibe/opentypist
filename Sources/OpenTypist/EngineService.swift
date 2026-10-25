import Foundation

/// Runs `LlamaEngine` on its own serial queue. Each request gets an id; a
/// newer request cancels older ones between tokens.
final class EngineService {
    enum State: Equatable {
        case idle
        case loading
        case ready(String)
        case failed(String)
    }

    struct Request {
        var textBefore: String
        var appName: String?
        var windowTitle: String?
        var screenText: String?
        var profile: String?
        var examples: [String] = []
        var length: CompletionLength
        var minProbability: Float = 0
        var wordMinProbability: Float? = nil
        /// Text after the caret on the same line, for mid-line suggestions.
        var textAfter: String = ""
    }

    private let queue = DispatchQueue(label: "opentypist.engine", qos: .userInitiated)
    private let engine = LlamaEngine()
    private let lock = NSLock()
    private var latestID = 0

    /// Main thread only.
    private(set) var state: State = .idle
    var onStateChange: ((State) -> Void)?

    var isReady: Bool {
        if case .ready = state { return true }
        return false
    }

    func load(path: String) {
        cancelAll()
        setState(.loading)
        let started = Date()
        queue.async { [engine] in
            do {
                try engine.load(path: path)
                let name = engine.modelDescription
                DispatchQueue.main.async {
                    AppStatus.shared.lastModelLoadSeconds = Date().timeIntervalSince(started)
                    self.setState(.ready(name))
                }
            } catch {
                DispatchQueue.main.async { self.setState(.failed(error.localizedDescription)) }
            }
        }
    }

    func unload() {
        cancelAll()
        queue.async { [engine] in engine.unload() }
        setState(.idle)
    }

    /// Frees the model before the process exits; ggml's Metal backend asserts
    /// if it is torn down while a model is still loaded.
    func shutdown() {
        cancelAll()
        queue.sync { engine.unload() }
    }

    @discardableResult
    func cancelAll() -> Int {
        lock.lock()
        defer { lock.unlock() }
        latestID += 1
        return latestID
    }

    private func isLatest(_ id: Int) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        return id == latestID
    }

    /// Calls `completion` on the main thread with the cleaned suggestion, or
    /// nil if there is nothing worth showing. Not called if superseded.
    /// `onPartial` streams the suggestion as it grows, one complete word at a
    /// time (on the main thread); `completion` then delivers the final text.
    func complete(_ request: Request, onPartial: ((String) -> Void)? = nil, completion: @escaping (String?) -> Void) {
        let id = cancelAll()
        queue.async { [engine] in
            guard self.isLatest(id), engine.isLoaded else { return }
            let prompt = PromptBuilder.build(
                textBefore: request.textBefore,
                appName: request.appName,
                windowTitle: request.windowTitle,
                screenText: request.screenText,
                profile: request.profile,
                examples: request.examples
            )
            let maxWords = request.length.maxWords
            var lastPartial: String?
            guard let output = engine.complete(
                prompt: prompt.text,
                maxTokens: request.length.maxTokens,
                requiredPrefix: prompt.wordPrefix,
                minProbability: request.minProbability,
                wordMinProbability: request.wordMinProbability,
                shouldContinue: { self.isLatest(id) },
                stop: { text in
                    if let onPartial,
                       let partial = PromptBuilder.streamingText(text, prompt: prompt, maxWords: maxWords, textAfter: request.textAfter),
                       partial != lastPartial {
                        lastPartial = partial
                        DispatchQueue.main.async { if self.isLatest(id) { onPartial(partial) } }
                    }
                    return PromptBuilder.shouldStop(text, maxWords: maxWords)
                }
            ) else { return }
            let confident = PromptBuilder.confidentText(output.tokens, hitTokenLimit: output.hitTokenLimit,
                                                        minProbability: request.minProbability, wordMinProbability: request.wordMinProbability)
            var text = PromptBuilder.clean(confident.text, prompt: prompt, maxWords: maxWords, hitTokenLimit: confident.hitTokenLimit)
            if let suggestion = text, !request.textAfter.isEmpty {
                text = PromptBuilder.fitBefore(request.textAfter, suggestion: suggestion)
            }
            DispatchQueue.main.async {
                guard self.isLatest(id) else { return }
                completion(text)
            }
        }
    }

    struct ScoreRequest {
        var context: String
        var appName: String?
        var windowTitle: String?
        var screenText: String?
        var profile: String?
        var examples: [String] = []
        var candidates: [String]
    }

    /// Log-probability of each candidate word following the context, on the
    /// main thread (nil if the model isn't available). Not cancelled by typing.
    func score(_ request: ScoreRequest, completion: @escaping ([Double]?) -> Void) {
        queue.async { [engine] in
            var result: [Double]?
            if engine.isLoaded {
                let prompt = PromptBuilder.scoringPrompt(
                    context: request.context,
                    appName: request.appName,
                    windowTitle: request.windowTitle,
                    screenText: request.screenText,
                    profile: request.profile,
                    examples: request.examples
                )
                result = engine.score(prompt: prompt.text, continuations: request.candidates.map { prompt.wordPrefix + $0 })
            }
            DispatchQueue.main.async { completion(result) }
        }
    }

    private func setState(_ newState: State) {
        state = newState
        onStateChange?(newState)
    }
}
