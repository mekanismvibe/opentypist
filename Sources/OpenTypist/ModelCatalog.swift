import Foundation

/// A downloadable GGUF model. All entries are base (pretrained, not chat)
/// models, because autocomplete is plain text continuation. Each one is
/// pinned to a Hugging Face revision and checked against its SHA-256.
struct ModelInfo: Identifiable, Equatable {
    let id: String
    let name: String
    let family: String
    let summary: String
    let repo: String
    let revision: String
    let fileName: String
    let size: Int64
    let sha256: String
    /// Memory needed to run it comfortably next to other apps.
    let minMemoryGB: Int
    let speed: String
    let quality: String

    var url: URL {
        URL(string: "https://huggingface.co/\(repo)/resolve/\(revision)/\(fileName)")!
    }

    var sizeText: String { ByteCountFormatter.string(fromByteCount: size, countStyle: .file) }
}

enum ModelCatalog {
    static let defaultID = "gemma-4-e2b"

    static let all: [ModelInfo] = [
        ModelInfo(
            id: "qwen3-1.7b", name: "Qwen 3 1.7B", family: "Alibaba Qwen 3",
            summary: "Smallest and fastest. Good for 8 GB Macs or when battery life matters most.",
            repo: "mradermacher/Qwen3-1.7B-Base-i1-GGUF", revision: "c11e7b148b8e7ee59a63cc8faa90b842e25e8942",
            fileName: "Qwen3-1.7B-Base.i1-Q4_K_M.gguf", size: 1_107_409_248,
            sha256: "b3aac3227132b11e0ea8c818569f27c053ebde24d0b8530f3bd476e7ff33ee20",
            minMemoryGB: 8, speed: "Fastest", quality: "Good"
        ),
        ModelInfo(
            id: "gemma-4-e2b", name: "Gemma 4 E2B", family: "Google Gemma 4",
            summary: "The default. Fast, natural suggestions on most Apple silicon Macs.",
            repo: "mradermacher/gemma-4-E2B-i1-GGUF", revision: "a9bf638e53783fc93778f357cc5c672eab5393b1",
            fileName: "gemma-4-E2B.i1-Q4_K_M.gguf", size: 3_427_862_240,
            sha256: "3cc0e9c2b4bbffea7f3f6cfe1ffd1591f4dafe02c43f78952f1142a24172df65",
            minMemoryGB: 8, speed: "Fast", quality: "Better"
        ),
        ModelInfo(
            id: "gemma-4-e4b", name: "Gemma 4 E4B", family: "Google Gemma 4",
            summary: "Noticeably smarter than E2B, a little slower. Great with 24 GB or more.",
            repo: "mradermacher/gemma-4-E4B-GGUF", revision: "712b177e561b537a217d1f73799a15e7bb99eb66",
            fileName: "gemma-4-E4B.Q4_K_M.gguf", size: 5_335_273_984,
            sha256: "17434a48d7b466313d2f7249500448074138856faecf1c03cd83ad13ca63aa52",
            minMemoryGB: 16, speed: "Balanced", quality: "Great"
        ),
        ModelInfo(
            id: "qwen3-8b", name: "Qwen 3 8B", family: "Alibaba Qwen 3",
            summary: "Dense 8B model with strong general knowledge. Slower per keystroke.",
            repo: "mradermacher/Qwen3-8B-Base-i1-GGUF", revision: "19a5b1a1ff4a5a411338e83f419043c992ee5984",
            fileName: "Qwen3-8B-Base.i1-Q4_K_M.gguf", size: 5_027_784_192,
            sha256: "983b5fac39c91e8167c150c2878a011878a3281c5412610342b33cb07a13e834",
            minMemoryGB: 16, speed: "Slower", quality: "Great"
        ),
        ModelInfo(
            id: "gemma-4-26b-a4b", name: "Gemma 4 26B A4B", family: "Google Gemma 4",
            summary: "Large mixture-of-experts model: only 4B parameters active, so still quick on big Macs.",
            repo: "mradermacher/gemma-4-26B-A4B-i1-GGUF", revision: "99feb69dd911417841d1bd94bc9a4b02953d8d3a",
            fileName: "gemma-4-26B-A4B.i1-Q4_K_M.gguf", size: 16_795_999_488,
            sha256: "24558f62309e85974a782837b67de9024ec8b6bedc9356c4cd5da410a8777a38",
            minMemoryGB: 32, speed: "Balanced", quality: "Best"
        ),
        ModelInfo(
            id: "qwen3-30b-a3b", name: "Qwen 3 30B A3B", family: "Alibaba Qwen 3",
            summary: "Largest in the catalog, 3B active parameters. For Macs with 48 GB or more.",
            repo: "mradermacher/Qwen3-30B-A3B-Base-i1-GGUF", revision: "c2c7cdc0632e91e6680bce976cedbe322ea71ea2",
            fileName: "Qwen3-30B-A3B-Base.i1-Q4_K_M.gguf", size: 18_556_686_560,
            sha256: "dfbe19e0e942ab9d18995b38e91b07dbe7088fdb44322080e2bdf16de7f8b9e1",
            minMemoryGB: 48, speed: "Balanced", quality: "Best"
        ),
    ]

    static func model(id: String?) -> ModelInfo? {
        all.first { $0.id == id }
    }

    /// Picks the model with the best quality that still keeps per-keystroke
    /// latency low and leaves memory for other apps.
    static func recommended(for hardware: HardwareInfo = .current) -> ModelInfo {
        let memory = hardware.memoryGB
        let id: String
        switch memory {
        case ..<12: id = "qwen3-1.7b"
        case ..<24: id = "gemma-4-e2b"
        case ..<64: id = hardware.isBaseChip && memory < 32 ? "gemma-4-e2b" : "gemma-4-e4b"
        default: id = "gemma-4-26b-a4b"
        }
        return model(id: id) ?? all[1]
    }
}

struct HardwareInfo {
    let chip: String
    let memoryGB: Int
    let performanceCores: Int

    static let current: HardwareInfo = {
        let memory = Int((Double(ProcessInfo.processInfo.physicalMemory) / 1_073_741_824).rounded())
        return HardwareInfo(
            chip: sysctlString("machdep.cpu.brand_string") ?? "Unknown chip",
            memoryGB: memory,
            performanceCores: sysctlInt("hw.perflevel0.physicalcpu") ?? ProcessInfo.processInfo.activeProcessorCount
        )
    }()

    var isAppleSilicon: Bool { chip.hasPrefix("Apple") }

    /// "Apple M2" rather than "Apple M2 Pro/Max/Ultra": fewer GPU cores.
    var isBaseChip: Bool {
        !(chip.contains("Pro") || chip.contains("Max") || chip.contains("Ultra"))
    }

    var summary: String { "\(chip) · \(memoryGB) GB memory" }

    func fits(_ model: ModelInfo) -> Bool { memoryGB >= model.minMemoryGB }

    private static func sysctlString(_ name: String) -> String? {
        var size = 0
        guard sysctlbyname(name, nil, &size, nil, 0) == 0, size > 0 else { return nil }
        var buffer = [CChar](repeating: 0, count: size)
        guard sysctlbyname(name, &buffer, &size, nil, 0) == 0 else { return nil }
        return String(cString: buffer)
    }

    private static func sysctlInt(_ name: String) -> Int? {
        var value: Int32 = 0
        var size = MemoryLayout<Int32>.size
        guard sysctlbyname(name, &value, &size, nil, 0) == 0 else { return nil }
        return Int(value)
    }
}
