#!/bin/zsh
# Build the Debug app and open it. Usage: Scripts/run-debug.sh [--no-open]
set -e
cd "$(dirname "$0")/.."
xcodegen generate -q
xcodebuild -project Hearsay.xcodeproj -scheme Hearsay \
  -destination 'platform=macOS,arch=arm64' -configuration Debug \
  -derivedDataPath .build/derived \
  -skipPackagePluginValidation -skipMacroValidation build \
  | grep -E "BUILD|error:" || true
APP=.build/derived/Build/Products/Debug/Hearsay.app
echo "Built: $APP"
[[ "$1" == "--no-open" ]] || open "$APP"
