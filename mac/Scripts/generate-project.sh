#!/bin/sh
# Regenerates Hearsay.xcodeproj from project.yml (XcodeGen).
set -eu
cd "$(dirname "$0")/.."
if ! command -v xcodegen >/dev/null 2>&1; then
    echo "xcodegen not found. Install it with: brew install xcodegen" >&2
    exit 1
fi
# Help pages live in shared/help; copy them into the .lproj folders first.
Scripts/sync-shared.sh
xcodegen generate "$@"
# Pin the generated project to the same package versions as HearsayWhisper's
# lock file, so CI and every machine resolve identical dependencies (the
# third-party notices are generated from these versions).
mkdir -p Hearsay.xcodeproj/project.xcworkspace/xcshareddata/swiftpm
cp HearsayWhisper/Package.resolved Hearsay.xcodeproj/project.xcworkspace/xcshareddata/swiftpm/Package.resolved
