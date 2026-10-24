// swift-tools-version:5.9
import Foundation
import PackageDescription

// llama.cpp and ggml come from Homebrew (`brew install llama.cpp`).
// scripts/build-app.sh copies the dylibs into the .app so the built app
// does not need Homebrew at runtime.
let brew = ProcessInfo.processInfo.environment["HOMEBREW_PREFIX"] ?? "/opt/homebrew"

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
                .unsafeFlags([
                    "-Xcc", "-I\(brew)/opt/llama.cpp/include",
                    "-Xcc", "-I\(brew)/opt/ggml/include",
                ]),
            ],
            linkerSettings: [
                .unsafeFlags([
                    "-L\(brew)/opt/llama.cpp/lib",
                    "-L\(brew)/opt/ggml/lib",
                    "-Xlinker", "-rpath", "-Xlinker", "@executable_path/../Frameworks",
                ]),
            ]
        ),
    ]
)
