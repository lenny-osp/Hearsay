// swift-tools-version: 6.0
// HearsayWhisper: vendored MLX Whisper model (mlx-audio-swift, MIT) plus a
// timestamped decoder ported from Python mlx_whisper 0.4.3.
// Build with xcodebuild (Metal shaders); `swift build` cannot compile MLX's kernels.
import PackageDescription

let package = Package(
    name: "HearsayWhisper",
    platforms: [.macOS(.v14)],
    products: [
        .library(name: "HearsayWhisper", targets: ["HearsayWhisper"]),
        .executable(name: "hearsay-transcribe", targets: ["hearsay-transcribe"]),
    ],
    dependencies: [
        .package(url: "https://github.com/ml-explore/mlx-swift.git", from: "0.30.6"),
        .package(url: "https://github.com/huggingface/swift-transformers.git", from: "1.1.6"),
    ],
    targets: [
        // System zlib, so the compression ratio uses the same `compress()` as
        // Python's `zlib.compress` (zlib container, default level).
        .systemLibrary(name: "CZlib", path: "Sources/CZlib"),
        .target(
            name: "HearsayWhisper",
            dependencies: [
                "CZlib",
                .product(name: "MLX", package: "mlx-swift"),
                .product(name: "MLXNN", package: "mlx-swift"),
                .product(name: "MLXFast", package: "mlx-swift"),
                .product(name: "MLXFFT", package: "mlx-swift"),
                .product(name: "Transformers", package: "swift-transformers"),
            ]
        ),
        .executableTarget(
            name: "hearsay-transcribe",
            dependencies: ["HearsayWhisper"]
        ),
        .testTarget(
            name: "HearsayWhisperTests",
            dependencies: ["HearsayWhisper"]
        ),
    ]
)
