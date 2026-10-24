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
        /// Text of the tokens that were kept.
        var text: String
        var hitTokenLimit: Bool
        /// Every token considered, with the model's probability for it. When
        /// generation stopped for low confidence, the last one is the token
        /// that was rejected (it is not part of `text`).
        var tokens: [GeneratedToken] = []
        var stoppedForConfidence = false
    }

    struct GeneratedToken {
        var bytes: [UInt8]
        var probability: Float
        /// Still finishing the word the user was in the middle of typing.
        var finishesWord = false

        /// Whether this piece starts a new word (space, newline or punctuation first).
        var startsNewWord: Bool {
            guard let first = bytes.first else { return false }
            return first == 0x20 || first == 0x0A || first == 0x09 || (0x21...0x2F).contains(first) || (0x3A...0x40).contains(first)
        }
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
        // The static build registers its backends (Metal, CPU, BLAS) itself;
        // only a dynamic-backend build would need to load them from disk.
        if ggml_backend_reg_count() == 0 {
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
    /// returns true to end generation. Generation also stops at the first
    /// token whose probability is below `minProbability` (or, while still
    /// finishing a half-typed word, below `wordMinProbability`): past that point the
    /// model is guessing. Returns nil if cancelled.
    func complete(
        prompt: String,
        maxTokens: Int,
        healLastToken: Bool,
        minProbability: Float = 0,
        wordMinProbability: Float? = nil,
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
        guard evaluate(tokens, shouldContinue: shouldContinue) else { return nil }

        var bytes: [UInt8] = []
        var text = ""
        var hitLimit = true
        var generated: [GeneratedToken] = []
        var stoppedForConfidence = false
        // Mid-word (token healing), the first pieces finish the typed word.
        var finishingWord = !healPrefix.isEmpty

        for step in 0..<maxTokens {
            guard shouldContinue() else { return nil }
            guard let logits = llama_get_logits_ith(ctx, -1) else { hitLimit = false; break }

            var next: llama_token
            let probability: Float
            if step == 0, !healPrefix.isEmpty {
                // Confidence among the tokens that can finish the half-typed word.
                guard let healed = bestToken(startingWith: healPrefix, logits: logits) else {
                    hitLimit = false
                    break
                }
                next = healed.token
                probability = healed.probability
            } else {
                var maxValue: Float = 0
                var maxIndex: vDSP_Length = 0
                vDSP_maxvi(logits, 1, &maxValue, &maxIndex, vDSP_Length(nVocab))
                next = llama_token(maxIndex)
                probability = exp(maxValue - logSumExp(logits))
            }

            if llama_vocab_is_eog(vocab, next) || llama_vocab_is_control(vocab, next) {
                hitLimit = false
                break
            }

            var piece = pieces[Int(next)]
            if step == 0, !healPrefix.isEmpty {
                piece.removeFirst(min(healPrefix.count, piece.count))
            }
            var token = GeneratedToken(bytes: piece, probability: probability)
            if finishingWord, token.startsNewWord { finishingWord = false }
            token.finishesWord = finishingWord
            generated.append(token)
            if probability < (finishingWord ? (wordMinProbability ?? minProbability) : minProbability) {
                hitLimit = false
                stoppedForConfidence = true
                break
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

        return Output(text: text, hitTokenLimit: hitLimit, tokens: generated, stoppedForConfidence: stoppedForConfidence)
    }

    /// Brings the KV cache to exactly `tokens`, reusing the longest cached
    /// prefix, so the logits for the last token are available.
    private func evaluate(_ tokens: [llama_token], shouldContinue: () -> Bool) -> Bool {
        guard let ctx, !tokens.isEmpty else { return false }
        // At least one token must be decoded to get logits.
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
            guard shouldContinue() else { return false }
            let count = min(512, tokens.count - index)
            var chunk = Array(tokens[index..<(index + count)])
            let rc = chunk.withUnsafeMutableBufferPointer {
                llama_decode(ctx, llama_batch_get_one($0.baseAddress, Int32(count)))
            }
            if rc != 0 {
                llama_memory_clear(memory, true)
                cached = []
                return false
            }
            cached.append(contentsOf: chunk)
            index += count
        }
        return true
    }

    // MARK: - Scoring

    /// Log-probability of each continuation following `prompt`, e.g. how
    /// likely " the" vs " teh" is after "I saw". Used to pick spelling fixes.
    func score(prompt: String, continuations: [String]) -> [Double]? {
        guard let ctx, vocab != nil else { return nil }
        var base = tokenize(prompt, addBOS: true)
        let limit = Int(contextSize) - 32
        if base.count > limit { base = [base[0]] + base.suffix(limit - 1) }
        guard evaluate(base, shouldContinue: { true }), let last = llama_get_logits_ith(ctx, -1) else { return nil }

        // The next decode overwrites the logits, so keep the prompt's.
        let baseLogits = Array(UnsafeBufferPointer(start: last, count: nVocab))
        let baseNorm = baseLogits.withUnsafeBufferPointer { logSumExp($0.baseAddress!) }
        let memory = llama_get_memory(ctx)

        var results: [Double] = []
        for text in continuations {
            let tokens = tokenize(text, addBOS: false)
            guard let first = tokens.first else { results.append(-.infinity); continue }
            var logProb = Double(baseLogits[Int(first)] - baseNorm)

            if tokens.count > 1 {
                // Decode all but the last token in one batch, with logits at every position.
                let n = tokens.count - 1
                var batch = llama_batch_init(Int32(n), 0, 1)
                for i in 0..<n {
                    batch.token[i] = tokens[i]
                    batch.pos[i] = llama_pos(base.count + i)
                    batch.n_seq_id[i] = 1
                    batch.seq_id[i]![0] = 0
                    batch.logits[i] = 1
                }
                batch.n_tokens = Int32(n)
                let rc = llama_decode(ctx, batch)
                llama_batch_free(batch)
                if rc != 0 {
                    llama_memory_clear(memory, true)
                    cached = []
                    return nil
                }
                for i in 0..<n {
                    guard let logits = llama_get_logits_ith(ctx, Int32(i)) else { return nil }
                    logProb += Double(logits[Int(tokens[i + 1])] - logSumExp(logits))
                }
                // Roll the cache back to the prompt.
                _ = llama_memory_seq_rm(memory, 0, llama_pos(base.count), -1)
            }
            results.append(logProb)
        }
        cached = base
        return results
    }

    private var expScratch: [Float] = []

    private func logSumExp(_ logits: UnsafePointer<Float>) -> Float {
        let n = vDSP_Length(nVocab)
        if expScratch.count != nVocab { expScratch = [Float](repeating: 0, count: nVocab) }
        var maxValue: Float = 0
        vDSP_maxv(logits, 1, &maxValue, n)
        var shift = -maxValue
        var count = Int32(nVocab)
        var sum: Float = 0
        expScratch.withUnsafeMutableBufferPointer { buffer in
            vDSP_vsadd(logits, 1, &shift, buffer.baseAddress!, 1, n)
            vvexpf(buffer.baseAddress!, buffer.baseAddress!, &count)
            vDSP_sve(buffer.baseAddress!, 1, &sum, n)
        }
        return maxValue + log(sum)
    }

    /// Most likely token that starts with `prefix`, and its probability among
    /// all such tokens.
    private func bestToken(startingWith prefix: [UInt8], logits: UnsafeMutablePointer<Float>) -> (token: llama_token, probability: Float)? {
        let first = prefix[0]
        var best: llama_token?
        var bestValue = -Float.infinity
        var candidateLogits: [Float] = []
        for id in 0..<nVocab {
            let candidate = pieces[id]
            guard candidate.count >= prefix.count, candidate.first == first, candidate.starts(with: prefix),
                  !llama_vocab_is_control(vocab, llama_token(id)) else { continue }
            candidateLogits.append(logits[id])
            if logits[id] > bestValue {
                best = llama_token(id)
                bestValue = logits[id]
            }
        }
        guard let best else { return nil }
        let total = candidateLogits.reduce(Float(0)) { $0 + exp($1 - bestValue) }
        return (best, 1 / total)
    }
}
