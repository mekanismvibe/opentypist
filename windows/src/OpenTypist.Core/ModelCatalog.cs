namespace OpenTypist;

/// A downloadable GGUF model. All entries are base (pretrained, not chat)
/// models, because autocomplete is plain text continuation. Each one is
/// pinned to a Hugging Face revision and checked against its SHA-256. Same
/// catalog as Sources/OpenTypist/ModelCatalog.swift.
public sealed record ModelInfo(
    string Id, string Name, string Family, string Summary, string Repo, string Revision, string FileName,
    long Size, string Sha256,
    /// Memory needed to run it comfortably next to other apps.
    int MinMemoryGB, string Speed, string Quality,
    /// Earlier files of the same model that still count as downloaded.
    string[]? LegacyFileNames = null)
{
    public Uri Url => new($"https://huggingface.co/{Repo}/resolve/{Revision}/{FileName}");

    /// The current file name first, then earlier ones.
    public IEnumerable<string> FileNames => LegacyFileNames is null ? [FileName] : [FileName, .. LegacyFileNames];

    public string SizeText => Size >= 1_000_000_000 ? $"{Size / 1e9:0.0} GB" : $"{Size / 1e6:0} MB";
}

public static class ModelCatalog
{
    public const string DefaultId = "gemma-4-e2b";

    public static readonly IReadOnlyList<ModelInfo> All =
    [
        new("qwen3-1.7b", "Qwen 3 1.7B", "Alibaba Qwen 3",
            "Smallest and fastest, slightly less accurate than Gemma 4 E2B. Good for 8 GB PCs, PCs without a graphics card, or to save battery.",
            "mradermacher/Qwen3-1.7B-Base-i1-GGUF", "c11e7b148b8e7ee59a63cc8faa90b842e25e8942",
            "Qwen3-1.7B-Base.i1-Q4_K_M.gguf", 1_107_409_248,
            "b3aac3227132b11e0ea8c818569f27c053ebde24d0b8530f3bd476e7ff33ee20",
            8, "Fastest", "Good"),
        new("gemma-4-e2b", "Gemma 4 E2B", "Google Gemma 4",
            "The default. Fast, natural suggestions on most PCs.",
            // Q4_0 rather than Q4_K_M, like the Mac app: same measured
            // suggestion quality (`--calibrate`), as fast or slightly faster
            // on Vulkan and faster on the CPU, slightly smaller (windows/README.md).
            "mradermacher/gemma-4-E2B-i1-GGUF", "a9bf638e53783fc93778f357cc5c672eab5393b1",
            "gemma-4-E2B.i1-Q4_0.gguf", 3_362_502_368,
            "50dffdea1974b389a70909510c27634030c420a5a32a5a7ee620ff11aa4fd43d",
            8, "Fast", "Better",
            LegacyFileNames: ["gemma-4-E2B.i1-Q4_K_M.gguf"]),
        new("gemma-4-e4b", "Gemma 4 E4B", "Google Gemma 4",
            "Noticeably smarter than E2B, a little slower. Great with 24 GB or more, or a graphics card with 8 GB.",
            "mradermacher/gemma-4-E4B-GGUF", "712b177e561b537a217d1f73799a15e7bb99eb66",
            "gemma-4-E4B.Q4_K_M.gguf", 5_335_273_984,
            "17434a48d7b466313d2f7249500448074138856faecf1c03cd83ad13ca63aa52",
            16, "Balanced", "Great"),
        new("qwen3-8b", "Qwen 3 8B", "Alibaba Qwen 3",
            "Dense 8B model with strong general knowledge. Slower per keystroke.",
            "mradermacher/Qwen3-8B-Base-i1-GGUF", "19a5b1a1ff4a5a411338e83f419043c992ee5984",
            "Qwen3-8B-Base.i1-Q4_K_M.gguf", 5_027_784_192,
            "983b5fac39c91e8167c150c2878a011878a3281c5412610342b33cb07a13e834",
            16, "Slower", "Great"),
        new("gemma-4-26b-a4b", "Gemma 4 26B A4B", "Google Gemma 4",
            "Large mixture-of-experts model: only 4B parameters active, so still quick with lots of memory.",
            "mradermacher/gemma-4-26B-A4B-i1-GGUF", "99feb69dd911417841d1bd94bc9a4b02953d8d3a",
            "gemma-4-26B-A4B.i1-Q4_K_M.gguf", 16_795_999_488,
            "24558f62309e85974a782837b67de9024ec8b6bedc9356c4cd5da410a8777a38",
            32, "Balanced", "Best"),
        new("qwen3-30b-a3b", "Qwen 3 30B A3B", "Alibaba Qwen 3",
            "Largest in the catalog, 3B active parameters. For PCs with 48 GB or more.",
            "mradermacher/Qwen3-30B-A3B-Base-i1-GGUF", "c2c7cdc0632e91e6680bce976cedbe322ea71ea2",
            "Qwen3-30B-A3B-Base.i1-Q4_K_M.gguf", 18_556_686_560,
            "dfbe19e0e942ab9d18995b38e91b07dbe7088fdb44322080e2bdf16de7f8b9e1",
            48, "Balanced", "Best"),
    ];

    public static ModelInfo? Model(string? id) => All.FirstOrDefault(m => m.Id == id);

    /// Picks the model with the best quality that still keeps per-keystroke
    /// latency low and leaves memory for other apps: by RAM like the Mac app,
    /// then capped by the graphics card's memory (the whole model and its
    /// cache should fit there). Without a graphics card, the small models.
    public static ModelInfo Recommended(HardwareInfo hardware)
    {
        int memory = hardware.MemoryGB;
        string[] ladder = ["qwen3-1.7b", "gemma-4-e2b", "gemma-4-e4b", "gemma-4-26b-a4b"];
        int step = memory switch
        {
            < 12 => 0,
            < 24 => 1,
            < 64 => 2,
            _ => 3,
        };
        if (hardware.GpuMemoryMiB <= 0)
        {
            step = Math.Min(step, memory < 16 ? 0 : 1);
        }
        else
        {
            const long headroomMiB = 1536;
            while (step > 0 && Model(ladder[step])!.Size / (1024 * 1024) + headroomMiB > hardware.GpuMemoryMiB) step--;
        }
        return Model(ladder[step]) ?? All[1];
    }
}

public sealed record HardwareInfo(string Cpu, int MemoryGB, string? Gpu, long GpuMemoryMiB)
{
    public string Summary =>
        $"{Cpu} · {MemoryGB} GB memory" + (Gpu is null ? " · no graphics card for the model" : $" · {Gpu} ({GpuMemoryMiB / 1024.0:0.#} GB)");

    public bool Fits(ModelInfo model) => MemoryGB >= model.MinMemoryGB;
}
