// swift-tools-version:5.9
import PackageDescription

// llama.cpp is vendored as a git submodule (vendor/llama.cpp) and built into
// static libraries by scripts/build-llama.sh before `swift build`; the app
// has no runtime dependencies beyond macOS 14.
let llama = Context.packageDirectory + "/build/llama-install"

let package = Package(
    name: "OpenTypist",
    platforms: [.macOS(.v14)],
    targets: [
        .systemLibrary(name: "CLlama", path: "Sources/CLlama"),
        .executableTarget(
            name: "OpenTypist",
            dependencies: ["CLlama"],
            path: "Sources/OpenTypist",
            swiftSettings: [
                .unsafeFlags(["-Xcc", "-I\(llama)/include"]),
            ],
            linkerSettings: [
                .unsafeFlags([
                    "-L\(llama)/lib",
                    "-lggml-cpu", "-lggml-metal", "-lggml-blas",
                ]),
                .linkedLibrary("c++"),
                .linkedFramework("Accelerate"),
                .linkedFramework("Foundation"),
                .linkedFramework("Metal"),
                .linkedFramework("MetalKit"),
            ]
        ),
    ]
)
