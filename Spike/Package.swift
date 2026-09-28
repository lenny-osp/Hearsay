// swift-tools-version: 5.9
// Phase 0 spike: link MLXAudioSTT, load a Whisper model, transcribe a WAV, time it.
// Build with xcodebuild (Metal shaders); `swift build` cannot compile MLX's kernels.
import PackageDescription

let package = Package(
    name: "hearsay-spike",
    platforms: [.macOS(.v14)],
    dependencies: [
        .package(url: "https://github.com/Blaizzy/mlx-audio-swift.git",
                 revision: "01dec7c9bdce3088a6b6b7ab9f2e403458195efb"),
    ],
    targets: [
        .executableTarget(
            name: "hearsay-spike",
            dependencies: [
                .product(name: "MLXAudioCore", package: "mlx-audio-swift"),
                .product(name: "MLXAudioSTT", package: "mlx-audio-swift"),
            ]
        ),
    ]
)
