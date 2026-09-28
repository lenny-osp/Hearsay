// swift-tools-version: 6.0
// HearsayCore: pure logic for the Hearsay app. No UI. Everything here is
// unit-tested with `swift test` from this directory. Targets that import MLX
// live in HearsayWhisper (added in Phase 4a) and are built with xcodebuild.
import PackageDescription

let package = Package(
    name: "HearsayCore",
    platforms: [.macOS(.v14)],
    products: [
        .library(name: "HearsayCore", targets: ["HearsayCore"]),
    ],
    targets: [
        .target(
            name: "HearsayCore",
            swiftSettings: [.enableUpcomingFeature("StrictConcurrency")]
        ),
        .testTarget(
            name: "HearsayCoreTests",
            dependencies: ["HearsayCore"]
        ),
    ]
)
