using System.Runtime.InteropServices;

// The app folder (where llama.dll and its ggml DLLs sit next to each other)
// and System32, never the current directory.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]

namespace OpenTypist;

/// P/Invoke declarations for llama.dll (functions with plain arguments,
/// straight from vendor/llama.cpp/include/llama.h) and opentypist_native.dll
/// (windows/native/opentypist_native.c, for struct-by-value calls). C `bool`
/// is one byte, hence the U1 marshalling.
internal static unsafe class Native
{
    private const string Llama = "llama";
    private const string Shim = "opentypist_native";

    // opentypist_native.dll

    [DllImport(Shim, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ot_init([MarshalAs(UnmanagedType.LPUTF8Str)] string backendDir, int quiet);

    [DllImport(Shim, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ot_device_info(int index, byte* buf, int bufSize);

    [DllImport(Shim, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ot_model_load([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int useGpu);

    [DllImport(Shim, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr ot_context_new(IntPtr model, uint nCtx, uint nBatch, int nThreads, int swaFull, int useGpu);

    [DllImport(Shim, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ot_decode(IntPtr ctx, int* tokens, int n);

    [DllImport(Shim, CallingConvention = CallingConvention.Cdecl)]
    public static extern int ot_decode_all_logits(IntPtr ctx, int* tokens, int n, int pos0);

    [DllImport(Shim, CallingConvention = CallingConvention.Cdecl)]
    public static extern float ot_max_index(float* logits, int n, out int index);

    [DllImport(Shim, CallingConvention = CallingConvention.Cdecl)]
    public static extern float ot_log_sum_exp(float* logits, int n);

    // llama.dll

    [DllImport(Llama, CallingConvention = CallingConvention.Cdecl)]
    public static extern void llama_model_free(IntPtr model);

    [DllImport(Llama, CallingConvention = CallingConvention.Cdecl)]
    public static extern void llama_free(IntPtr ctx);

    [DllImport(Llama, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr llama_model_get_vocab(IntPtr model);

    [DllImport(Llama, CallingConvention = CallingConvention.Cdecl)]
    public static extern int llama_vocab_n_tokens(IntPtr vocab);

    [DllImport(Llama, CallingConvention = CallingConvention.Cdecl)]
    public static extern int llama_model_desc(IntPtr model, byte* buf, nuint bufSize);

    [DllImport(Llama, CallingConvention = CallingConvention.Cdecl)]
    public static extern int llama_tokenize(IntPtr vocab, byte* text, int textLen, int* tokens, int nTokensMax,
                                            [MarshalAs(UnmanagedType.U1)] bool addSpecial,
                                            [MarshalAs(UnmanagedType.U1)] bool parseSpecial);

    [DllImport(Llama, CallingConvention = CallingConvention.Cdecl)]
    public static extern int llama_token_to_piece(IntPtr vocab, int token, byte* buf, int length, int lstrip,
                                                  [MarshalAs(UnmanagedType.U1)] bool special);

    [DllImport(Llama, CallingConvention = CallingConvention.Cdecl)]
    public static extern float* llama_get_logits_ith(IntPtr ctx, int i);

    [DllImport(Llama, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr llama_get_memory(IntPtr ctx);

    [DllImport(Llama, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool llama_memory_seq_rm(IntPtr memory, int seqId, int p0, int p1);

    [DllImport(Llama, CallingConvention = CallingConvention.Cdecl)]
    public static extern void llama_memory_clear(IntPtr memory, [MarshalAs(UnmanagedType.U1)] bool data);

    [DllImport(Llama, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool llama_vocab_is_eog(IntPtr vocab, int token);

    [DllImport(Llama, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool llama_vocab_is_control(IntPtr vocab, int token);
}
