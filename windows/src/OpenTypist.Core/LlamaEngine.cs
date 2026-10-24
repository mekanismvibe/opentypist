using System.Text;
using System.Text.Unicode;

namespace OpenTypist;

public sealed class EngineException(string message) : Exception(message);

/// One generated piece of text and the model's probability for it.
public sealed class GeneratedToken(byte[] bytes, float probability)
{
    public byte[] Bytes { get; } = bytes;
    public float Probability { get; } = probability;
    /// Still finishing the word the user was in the middle of typing.
    public bool FinishesWord { get; set; }

    /// Whether this piece starts a new word (space, newline or punctuation first).
    public bool StartsNewWord
    {
        get
        {
            if (Bytes.Length == 0) return false;
            byte first = Bytes[0];
            return first == 0x20 || first == 0x0A || first == 0x09 || (first >= 0x21 && first <= 0x2F) || (first >= 0x3A && first <= 0x40);
        }
    }

    public static GeneratedToken Of(string text, float probability, bool finishesWord = false) =>
        new(Encoding.UTF8.GetBytes(text), probability) { FinishesWord = finishesWord };
}

/// A ggml compute device ("Vulkan0", "CPU").
public sealed record ComputeDevice(string Name, string Description, string Type, long FreeMiB, long TotalMiB)
{
    public bool IsGpu => Type == "gpu";
}

/// Thin wrapper around the llama.cpp C API, a port of LlamaEngine.swift. Not
/// thread-safe: use it from one thread only (see `EngineService`).
///
/// Every request is a plain text continuation of a base model. The KV cache is
/// reused across requests by keeping the longest common token prefix, so a new
/// keystroke usually only costs a couple of tokens of prompt processing.
public sealed unsafe class LlamaEngine : IDisposable
{
    public sealed class Output
    {
        /// Text of the tokens that were kept.
        public string Text { get; init; } = "";
        public bool HitTokenLimit { get; init; }
        /// Every token considered, with the model's probability for it. When
        /// generation stopped for low confidence, the last one is the token
        /// that was rejected (it is not part of `Text`).
        public List<GeneratedToken> Tokens { get; init; } = [];
        public bool StoppedForConfidence { get; init; }
    }

    private IntPtr model;
    private IntPtr ctx;
    private IntPtr vocab;
    private int nVocab;
    /// Byte pieces for every token id, used for token healing.
    private byte[][] pieces = [];
    private bool[] isControl = [];
    /// Token ids by the first byte of their piece, for token healing.
    private List<int>[] byFirstByte = [];
    /// Tokens currently held in the KV cache (sequence 0), in order.
    private readonly List<int> cached = [];

    public const int ContextSize = 4096;
    public string ModelDescription { get; private set; } = "";
    public bool UsesGpu { get; private set; }

    private static readonly object InitLock = new();
    private static bool backendsLoaded;

    /// Loads ggml's backends (the best CPU variant, and Vulkan or CUDA when the
    /// PC supports them) from the folder this app runs from.
    public static void LoadBackends()
    {
        lock (InitLock)
        {
            if (backendsLoaded) return;
            backendsLoaded = true;
            bool quiet = Environment.GetEnvironmentVariable("OPENTYPIST_DEBUG") is null;
            Native.ot_init(AppContext.BaseDirectory.TrimEnd('\\'), quiet ? 1 : 0);
        }
    }

    public static List<ComputeDevice> Devices()
    {
        LoadBackends();
        var devices = new List<ComputeDevice>();
        var buf = new byte[512];
        for (int i = 0; ; i++)
        {
            int n;
            fixed (byte* p = buf) n = Native.ot_device_info(i, p, buf.Length);
            if (n < 0) break;
            var parts = Encoding.UTF8.GetString(buf, 0, Math.Min(n, buf.Length - 1)).Split('|');
            if (parts.Length < 5) continue;
            devices.Add(new ComputeDevice(parts[0], parts[1], parts[2], long.Parse(parts[3]), long.Parse(parts[4])));
        }
        return devices;
    }

    public bool IsLoaded => ctx != IntPtr.Zero;

    /// Loads a GGUF model with all layers on the GPU, unless `useGpu` is false
    /// or there is no usable GPU (then it runs on the CPU).
    public void Load(string path, bool useGpu = true)
    {
        LoadBackends();
        Unload();

        bool gpu = useGpu && Devices().Any(d => d.IsGpu || d.Type == "igpu");
        var m = Native.ot_model_load(path, gpu ? 1 : 0);
        if (m == IntPtr.Zero) throw new EngineException($"Could not load model at {path}");

        int threads = Math.Max(2, Math.Min(8, Environment.ProcessorCount - 2));
        // Gemma uses sliding-window attention; a full-size SWA cache lets us
        // trim the cache to an arbitrary prefix when the text changes.
        var c = Native.ot_context_new(m, ContextSize, 512, threads, 1, gpu ? 1 : 0);
        if (c == IntPtr.Zero)
        {
            Native.llama_model_free(m);
            throw new EngineException("Could not create llama.cpp context");
        }

        model = m;
        ctx = c;
        UsesGpu = gpu;
        vocab = Native.llama_model_get_vocab(m);
        nVocab = Native.llama_vocab_n_tokens(vocab);
        cached.Clear();
        pieces = new byte[nVocab][];
        isControl = new bool[nVocab];
        byFirstByte = new List<int>[256];
        for (int b = 0; b < 256; b++) byFirstByte[b] = [];
        for (int id = 0; id < nVocab; id++)
        {
            pieces[id] = Piece(id);
            isControl[id] = Native.llama_vocab_is_control(vocab, id);
            if (pieces[id].Length > 0 && !isControl[id]) byFirstByte[pieces[id][0]].Add(id);
        }

        var buf = new byte[256];
        int n;
        fixed (byte* p = buf) n = Native.llama_model_desc(m, p, (nuint)buf.Length);
        ModelDescription = n > 0 ? Encoding.UTF8.GetString(buf, 0, Math.Min(n, buf.Length - 1)) : Path.GetFileName(path);
    }

    /// The Vulkan backend builds its GPU pipelines the first time it sees each
    /// batch shape: 20 s or more on a PC's first run, until the driver has
    /// cached them. Do that now, with the prompt and keystroke sizes typing
    /// produces, instead of on the user's first keystrokes.
    public void WarmUp()
    {
        if (ctx == IntPtr.Zero) return;
        var text = string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 60));
        foreach (var extra in new[] { "", " It", " It was", " It was a", " It was a sunny day, and", " It was a sunny day, and the children played outside for hours" })
        {
            Complete(text + extra, 2, false, () => true, _ => false);
        }
        Complete("Hello", 2, true, () => true, _ => false);
        Native.llama_memory_clear(Native.llama_get_memory(ctx), true);
        cached.Clear();
    }

    public void Unload()
    {
        if (ctx != IntPtr.Zero) Native.llama_free(ctx);
        if (model != IntPtr.Zero) Native.llama_model_free(model);
        ctx = IntPtr.Zero;
        model = IntPtr.Zero;
        vocab = IntPtr.Zero;
        cached.Clear();
        pieces = [];
        isControl = [];
        byFirstByte = [];
    }

    public void Dispose() => Unload();

    // Tokens

    public List<int> Tokenize(string text, bool addBos)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var tokens = new int[bytes.Length + 8];
        int n;
        // parse_special = false: text typed by the user must never turn into control tokens.
        fixed (byte* t = bytes)
        fixed (int* o = tokens)
            n = Native.llama_tokenize(vocab, t, bytes.Length, o, tokens.Length, addBos, false);
        if (n < 0)
        {
            tokens = new int[-n];
            fixed (byte* t = bytes)
            fixed (int* o = tokens)
                n = Native.llama_tokenize(vocab, t, bytes.Length, o, tokens.Length, addBos, false);
        }
        return tokens.Take(Math.Max(0, n)).ToList();
    }

    private byte[] Piece(int token)
    {
        var buf = new byte[64];
        int n;
        fixed (byte* p = buf) n = Native.llama_token_to_piece(vocab, token, p, buf.Length, 0, false);
        if (n < 0)
        {
            buf = new byte[-n];
            fixed (byte* p = buf) n = Native.llama_token_to_piece(vocab, token, p, buf.Length, 0, false);
        }
        return buf.AsSpan(0, Math.Max(0, n)).ToArray();
    }

    // Completion

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
    /// model is guessing. Returns null if cancelled.
    public Output? Complete(string prompt, int maxTokens, bool healLastToken, Func<bool> shouldContinue, Func<string, bool> stop,
                            float minProbability = 0, float? wordMinProbability = null)
    {
        if (ctx == IntPtr.Zero) return null;

        var tokens = Tokenize(prompt, addBos: true);
        byte[] healPrefix = [];
        if (healLastToken && tokens.Count > 2)
        {
            int last = tokens[^1];
            var lastPiece = pieces[last];
            // Only heal plain word pieces, never control tokens or empty pieces.
            if (lastPiece.Length > 0 && !isControl[last])
            {
                healPrefix = lastPiece;
                tokens.RemoveAt(tokens.Count - 1);
            }
        }

        int limit = ContextSize - maxTokens - 8;
        if (tokens.Count > limit) tokens = [tokens[0], .. tokens.Skip(tokens.Count - (limit - 1))];
        if (!Evaluate(tokens, shouldContinue)) return null;

        var bytes = new List<byte>();
        var text = "";
        bool hitLimit = true;
        var generated = new List<GeneratedToken>();
        bool stoppedForConfidence = false;
        // Mid-word (token healing), the first pieces finish the typed word.
        bool finishingWord = healPrefix.Length > 0;

        for (int step = 0; step < maxTokens; step++)
        {
            if (!shouldContinue()) return null;
            float* logits = Native.llama_get_logits_ith(ctx, -1);
            if (logits == null) { hitLimit = false; break; }

            int next;
            float probability;
            if (step == 0 && healPrefix.Length > 0)
            {
                // Confidence among the tokens that can finish the half-typed word.
                var healed = BestToken(healPrefix, logits);
                if (healed is null) { hitLimit = false; break; }
                next = healed.Value.Token;
                probability = healed.Value.Probability;
            }
            else
            {
                float maxValue = Native.ot_max_index(logits, nVocab, out next);
                probability = MathF.Exp(maxValue - Native.ot_log_sum_exp(logits, nVocab));
            }

            if (Native.llama_vocab_is_eog(vocab, next) || isControl[next]) { hitLimit = false; break; }

            var piece = pieces[next];
            if (step == 0 && healPrefix.Length > 0) piece = piece[Math.Min(healPrefix.Length, piece.Length)..];
            var token = new GeneratedToken(piece, probability);
            if (finishingWord && token.StartsNewWord) finishingWord = false;
            token.FinishesWord = finishingWord;
            generated.Add(token);
            if (probability < (finishingWord ? (wordMinProbability ?? minProbability) : minProbability))
            {
                hitLimit = false;
                stoppedForConfidence = true;
                break;
            }
            bytes.AddRange(piece);
            // A token can end in the middle of a multi-byte character; wait for the rest.
            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bytes);
            if (Utf8.IsValid(span)) text = Encoding.UTF8.GetString(span);

            int rc = Native.ot_decode(ctx, &next, 1);
            if (rc != 0) { hitLimit = false; break; }
            cached.Add(next);

            if (stop(text)) { hitLimit = false; break; }
        }

        return new Output { Text = text, HitTokenLimit = hitLimit, Tokens = generated, StoppedForConfidence = stoppedForConfidence };
    }

    /// Brings the KV cache to exactly `tokens`, reusing the longest cached
    /// prefix, so the logits for the last token are available.
    private bool Evaluate(List<int> tokens, Func<bool> shouldContinue)
    {
        if (ctx == IntPtr.Zero || tokens.Count == 0) return false;
        // At least one token must be decoded to get logits.
        int common = 0;
        while (common < Math.Min(cached.Count, tokens.Count) && cached[common] == tokens[common]) common++;
        if (common == tokens.Count) common--;

        var memory = Native.llama_get_memory(ctx);
        if (!Native.llama_memory_seq_rm(memory, 0, common, -1))
        {
            Native.llama_memory_clear(memory, true);
            common = 0;
        }
        cached.RemoveRange(common, cached.Count - common);

        int index = common;
        var array = tokens.ToArray();
        while (index < array.Length)
        {
            if (!shouldContinue()) return false;
            int count = Math.Min(512, array.Length - index);
            int rc;
            fixed (int* p = &array[index]) rc = Native.ot_decode(ctx, p, count);
            if (rc != 0)
            {
                Native.llama_memory_clear(memory, true);
                cached.Clear();
                return false;
            }
            cached.AddRange(array.AsSpan(index, count).ToArray());
            index += count;
        }
        return true;
    }

    // Scoring

    /// Log-probability of each continuation following `prompt`, e.g. how
    /// likely " the" vs " teh" is after "I saw". Used to pick spelling fixes.
    public double[]? Score(string prompt, IReadOnlyList<string> continuations)
    {
        if (ctx == IntPtr.Zero) return null;
        var baseTokens = Tokenize(prompt, addBos: true);
        int limit = ContextSize - 32;
        if (baseTokens.Count > limit) baseTokens = [baseTokens[0], .. baseTokens.Skip(baseTokens.Count - (limit - 1))];
        if (!Evaluate(baseTokens, () => true)) return null;
        float* last = Native.llama_get_logits_ith(ctx, -1);
        if (last == null) return null;

        // The next decode overwrites the logits, so keep the prompt's.
        var baseLogits = new float[nVocab];
        new ReadOnlySpan<float>(last, nVocab).CopyTo(baseLogits);
        float baseNorm;
        fixed (float* p = baseLogits) baseNorm = Native.ot_log_sum_exp(p, nVocab);
        var memory = Native.llama_get_memory(ctx);

        var results = new double[continuations.Count];
        for (int c = 0; c < continuations.Count; c++)
        {
            var tokens = Tokenize(continuations[c], addBos: false);
            if (tokens.Count == 0) { results[c] = double.NegativeInfinity; continue; }
            double logProb = baseLogits[tokens[0]] - baseNorm;

            if (tokens.Count > 1)
            {
                // Decode all but the last token in one batch, with logits at every position.
                int n = tokens.Count - 1;
                var batch = tokens.Take(n).ToArray();
                int rc;
                fixed (int* p = batch) rc = Native.ot_decode_all_logits(ctx, p, n, baseTokens.Count);
                if (rc != 0)
                {
                    Native.llama_memory_clear(memory, true);
                    cached.Clear();
                    return null;
                }
                for (int i = 0; i < n; i++)
                {
                    float* logits = Native.llama_get_logits_ith(ctx, i);
                    if (logits == null) return null;
                    logProb += logits[tokens[i + 1]] - Native.ot_log_sum_exp(logits, nVocab);
                }
                // Roll the cache back to the prompt.
                Native.llama_memory_seq_rm(memory, 0, baseTokens.Count, -1);
            }
            results[c] = logProb;
        }
        cached.Clear();
        cached.AddRange(baseTokens);
        return results;
    }

    /// Most likely token that starts with `prefix`, and its probability among
    /// all such tokens.
    private (int Token, float Probability)? BestToken(byte[] prefix, float* logits)
    {
        int best = -1;
        float bestValue = float.NegativeInfinity;
        var candidates = byFirstByte[prefix[0]];
        foreach (int id in candidates)
        {
            var candidate = pieces[id];
            if (candidate.Length < prefix.Length || !candidate.AsSpan(0, prefix.Length).SequenceEqual(prefix)) continue;
            if (logits[id] > bestValue)
            {
                best = id;
                bestValue = logits[id];
            }
        }
        if (best < 0) return null;
        double total = 0;
        foreach (int id in candidates)
        {
            var candidate = pieces[id];
            if (candidate.Length < prefix.Length || !candidate.AsSpan(0, prefix.Length).SequenceEqual(prefix)) continue;
            total += Math.Exp(logits[id] - bestValue);
        }
        return (best, (float)(1 / total));
    }
}
