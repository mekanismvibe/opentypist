import Accelerate
import CLlama
import Foundation

enum EngineError: LocalizedError {
    case modelLoadFailed(String)
    case contextFailed

    var errorDescription: String? {
        switch self {
        case .modelLoadFailed(let path): return "Could not load model at \(path)"
        case .contextFailed: return "Could not create llama.cpp context"
        }
    }
}

/// Thin wrapper around the llama.cpp C API. Not thread-safe: use it from one
/// queue only (see `EngineService`).
///
/// Every request is a plain text continuation of a base model. The KV cache is
/// reused across requests by keeping the longest common token prefix, so a new
/// keystroke usually only costs a couple of tokens of prompt processing.
final class LlamaEngine {
    struct Output {
        var text: String
        var hitTokenLimit: Bool
    }

    private var model: OpaquePointer?
    private var ctx: OpaquePointer?
    private var vocab: OpaquePointer?
    private var nVocab = 0
    /// Byte pieces for every token id, used for token healing.
    private var pieces: [[UInt8]] = []
    /// Tokens currently held in the KV cache (sequence 0), in order.
    private var cached: [llama_token] = []

    let contextSize: Int32 = 4096
    private(set) var modelDescription = ""

    private static var backendsLoaded = false

    static func loadBackends() {
        guard !backendsLoaded else { return }
        backendsLoaded = true
        if ProcessInfo.processInfo.environment["OPENTYPIST_DEBUG"] == nil {
            llama_log_set({ _, _, _ in }, nil)
        }
        // Inside the .app the ggml backends (Metal, CPU, BLAS) are bundled in
        // Contents/Frameworks; otherwise fall back to the Homebrew install.
        if let frameworks = Bundle.main.privateFrameworksPath,
           FileManager.default.fileExists(atPath: frameworks + "/libggml-metal.so") {
            ggml_backend_load_all_from_path(frameworks)
        } else {
            ggml_backend_load_all()
        }
        llama_backend_init()
    }

    var isLoaded: Bool { ctx != nil }

    func load(path: String) throws {
        Self.loadBackends()
        unload()

        var modelParams = llama_model_default_params()
        modelParams.n_gpu_layers = 999
        guard let model = llama_model_load_from_file(path, modelParams) else {
            throw EngineError.modelLoadFailed(path)
        }

        var ctxParams = llama_context_default_params()
        ctxParams.n_ctx = UInt32(contextSize)
        ctxParams.n_batch = 512
        ctxParams.n_ubatch = 512
        ctxParams.n_seq_max = 1
        let threads = Int32(max(2, min(8, ProcessInfo.processInfo.activeProcessorCount - 2)))
        ctxParams.n_threads = threads
        ctxParams.n_threads_batch = threads
        // Gemma uses sliding-window attention; a full-size SWA cache lets us
        // trim the cache to an arbitrary prefix when the text changes.
        ctxParams.swa_full = true
        ctxParams.no_perf = true

        guard let ctx = llama_init_from_model(model, ctxParams) else {
            llama_model_free(model)
            throw EngineError.contextFailed
        }

        self.model = model
        self.ctx = ctx
        self.vocab = llama_model_get_vocab(model)
        self.nVocab = Int(llama_vocab_n_tokens(vocab))
        self.cached = []
        self.pieces = (0..<nVocab).map { piece(llama_token($0)) }

        var buf = [CChar](repeating: 0, count: 256)
        let n = llama_model_desc(model, &buf, buf.count)
        modelDescription = n > 0 ? String(cString: buf) : URL(fileURLWithPath: path).lastPathComponent
    }

    func unload() {
        if let ctx { llama_free(ctx) }
        if let model { llama_model_free(model) }
        ctx = nil
        model = nil
        vocab = nil
        cached = []
        pieces = []
    }

    deinit { unload() }

    // MARK: - Tokens

    func tokenize(_ text: String, addBOS: Bool) -> [llama_token] {
        let byteCount = Int32(text.utf8.count)
        var tokens = [llama_token](repeating: 0, count: Int(byteCount) + 8)
        // parse_special = false: text typed by the user must never turn into control tokens.
        var n = llama_tokenize(vocab, text, byteCount, &tokens, Int32(tokens.count), addBOS, false)
        if n < 0 {
            tokens = [llama_token](repeating: 0, count: Int(-n))
            n = llama_tokenize(vocab, text, byteCount, &tokens, Int32(tokens.count), addBOS, false)
        }
        return Array(tokens.prefix(Int(max(0, n))))
    }

    private func piece(_ token: llama_token) -> [UInt8] {
        var buf = [CChar](repeating: 0, count: 64)
        var n = llama_token_to_piece(vocab, token, &buf, Int32(buf.count), 0, false)
        if n < 0 {
            buf = [CChar](repeating: 0, count: Int(-n))
            n = llama_token_to_piece(vocab, token, &buf, Int32(buf.count), 0, false)
        }
        return buf.prefix(Int(max(0, n))).map { UInt8(bitPattern: $0) }
    }

    // MARK: - Completion

    /// Continues `prompt` greedily.
    ///
    /// With `healLastToken`, the last prompt token is removed and the first
    /// generated token is constrained to start with its bytes. This lets the
    /// model finish a half-typed word ("I'm runn" -> "ing late") instead of
    /// being stuck with an unnatural token split.
    ///
    /// `stop` is called with the text generated so far after every token and
    /// returns true to end generation. Returns nil if cancelled.
    func complete(
        prompt: String,
        maxTokens: Int,
        healLastToken: Bool,
        shouldContinue: () -> Bool,
        stop: (String) -> Bool
    ) -> Output? {
        guard let ctx, vocab != nil else { return nil }

        var tokens = tokenize(prompt, addBOS: true)
        var healPrefix: [UInt8] = []
        if healLastToken, tokens.count > 2, let last = tokens.last {
            let lastPiece = pieces[Int(last)]
            // Only heal plain word pieces, never control tokens or empty pieces.
            if !lastPiece.isEmpty, !llama_vocab_is_control(vocab, last) {
                healPrefix = lastPiece
                tokens.removeLast()
            }
        }

        let limit = Int(contextSize) - maxTokens - 8
        if tokens.count > limit {
            tokens = [tokens[0]] + tokens.suffix(limit - 1)
        }

        // Reuse the cached prefix; at least one token must be decoded to get logits.
        var common = 0
        while common < min(cached.count, tokens.count), cached[common] == tokens[common] { common += 1 }
        if common == tokens.count { common -= 1 }

        let memory = llama_get_memory(ctx)
        if !llama_memory_seq_rm(memory, 0, llama_pos(common), -1) {
            llama_memory_clear(memory, true)
            common = 0
        }
        cached = Array(tokens.prefix(common))

        var index = common
        while index < tokens.count {
            guard shouldContinue() else { return nil }
            let count = min(512, tokens.count - index)
            var chunk = Array(tokens[index..<(index + count)])
            let rc = chunk.withUnsafeMutableBufferPointer {
                llama_decode(ctx, llama_batch_get_one($0.baseAddress, Int32(count)))
            }
            if rc != 0 {
                llama_memory_clear(memory, true)
                cached = []
                return nil
            }
            cached.append(contentsOf: chunk)
            index += count
        }

        var bytes: [UInt8] = []
        var text = ""
        var hitLimit = true

        for step in 0..<maxTokens {
            guard shouldContinue() else { return nil }
            guard let logits = llama_get_logits_ith(ctx, -1) else { hitLimit = false; break }

            var next: llama_token
            if step == 0, !healPrefix.isEmpty {
                guard let healed = bestToken(startingWith: healPrefix, logits: logits) else {
                    hitLimit = false
                    break
                }
                next = healed
            } else {
                var maxValue: Float = 0
                var maxIndex: vDSP_Length = 0
                vDSP_maxvi(logits, 1, &maxValue, &maxIndex, vDSP_Length(nVocab))
                next = llama_token(maxIndex)
            }

            if llama_vocab_is_eog(vocab, next) || llama_vocab_is_control(vocab, next) {
                hitLimit = false
                break
            }

            var piece = pieces[Int(next)]
            if step == 0, !healPrefix.isEmpty {
                piece.removeFirst(min(healPrefix.count, piece.count))
            }
            bytes += piece
            // A token can end in the middle of a multi-byte character; wait for the rest.
            if let decoded = String(bytes: bytes, encoding: .utf8) { text = decoded }

            let rc = withUnsafeMutablePointer(to: &next) {
                llama_decode(ctx, llama_batch_get_one($0, 1))
            }
            if rc != 0 { hitLimit = false; break }
            cached.append(next)

            if stop(text) { hitLimit = false; break }
        }

        return Output(text: text, hitTokenLimit: hitLimit)
    }

    private func bestToken(startingWith prefix: [UInt8], logits: UnsafeMutablePointer<Float>) -> llama_token? {
        let first = prefix[0]
        var best: llama_token?
        var bestValue = -Float.infinity
        for id in 0..<nVocab {
            let candidate = pieces[id]
            guard candidate.count >= prefix.count, candidate.first == first,
                  logits[id] > bestValue, candidate.starts(with: prefix) else { continue }
            if llama_vocab_is_control(vocab, llama_token(id)) { continue }
            best = llama_token(id)
            bestValue = logits[id]
        }
        return best
    }
}
