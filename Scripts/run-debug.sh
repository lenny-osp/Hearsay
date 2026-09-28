#!/bin/zsh
# Build the app and open it.
# Usage: Scripts/run-debug.sh [--release] [--no-open]
#   --release  build -configuration Release (about 7x faster transcription)
#              into the same derived data path and open the Release app
#   --no-open  build only
set -e
cd "$(dirname "$0")/.."
CONFIGURATION=Debug
OPEN=1
for arg in "$@"; do
  case "$arg" in
    --release) CONFIGURATION=Release ;;
    --no-open) OPEN=0 ;;
    *) echo "Unknown option: $arg" >&2; echo "Usage: $0 [--release] [--no-open]" >&2; exit 2 ;;
  esac
done
mkdir -p .build
xcodegen generate -q
# Xcode does not re-copy a resource bundle whose inner folders changed (for
# example new .lproj translations), so drop the embedded copies first.
rm -rf ".build/derived/Build/Products/$CONFIGURATION/Hearsay.app/Contents/Resources/"*.bundle
xcodebuild -project Hearsay.xcodeproj -scheme Hearsay \
  -destination 'platform=macOS,arch=arm64' -configuration "$CONFIGURATION" \
  -derivedDataPath .build/derived \
  -skipPackagePluginValidation -skipMacroValidation build \
  > .build/run-debug.log 2>&1 || true
grep -E "BUILD|error:" .build/run-debug.log || true
grep -q "BUILD SUCCEEDED" .build/run-debug.log || { echo "$CONFIGURATION build failed; see .build/run-debug.log" >&2; exit 1; }
APP=.build/derived/Build/Products/$CONFIGURATION/Hearsay.app
echo "Built $CONFIGURATION: $APP"
[[ $OPEN == 1 ]] && open "$APP"
exit 0
