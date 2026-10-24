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
        queue.async { [engine] in
            do {
                try engine.load(path: path)
                let name = engine.modelDescription
                DispatchQueue.main.async { self.setState(.ready(name)) }
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
    func complete(_ request: Request, completion: @escaping (String?) -> Void) {
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
            guard let output = engine.complete(
                prompt: prompt.text,
                maxTokens: request.length.maxTokens,
                healLastToken: prompt.healLastToken,
                shouldContinue: { self.isLatest(id) },
                stop: { PromptBuilder.shouldStop($0, maxWords: maxWords) }
            ) else { return }
            let text = PromptBuilder.clean(output.text, prompt: prompt, maxWords: maxWords, hitTokenLimit: output.hitTokenLimit)
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
