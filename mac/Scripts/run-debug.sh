#!/bin/zsh
# Build the app and open it.
# Usage: Scripts/run-debug.sh [--release] [--no-open] [--ci] [SETTING=value ...]
#   --release  build -configuration Release (about 7x faster transcription)
#              into the same derived data path and open the Release app
#   --no-open  build only
#   --ci       non-interactive (never opens the app) and prints the end of
#              the build log on failure; also on when the environment has CI
#              set (GitHub Actions sets CI=true)
#   SETTING=value  passed to xcodebuild as a build setting, for example
#              MARKETING_VERSION=0.2.0 CURRENT_PROJECT_VERSION=7
set -e
cd "$(dirname "$0")/.."
CONFIGURATION=Debug
OPEN=1
CI_MODE=0
[[ -n ${CI:-} && ${CI:-} != 0 && ${CI:-} != false ]] && CI_MODE=1
SETTINGS=()
for arg in "$@"; do
  case "$arg" in
    --release) CONFIGURATION=Release ;;
    --no-open) OPEN=0 ;;
    --ci) CI_MODE=1 ;;
    [A-Z_]*=*) SETTINGS+=("$arg") ;;
    *) echo "Unknown option: $arg" >&2; echo "Usage: $0 [--release] [--no-open] [--ci] [SETTING=value ...]" >&2; exit 2 ;;
  esac
done
[[ $CI_MODE == 1 ]] && OPEN=0
mkdir -p .build
# Help pages live in shared/help; copy them into the .lproj folders first.
Scripts/sync-shared.sh
xcodegen generate -q
# Pin the generated project to the same package versions as HearsayWhisper's
# lock file, so CI and every machine resolve identical dependencies (the
# third-party notices are generated from these versions).
mkdir -p Hearsay.xcodeproj/project.xcworkspace/xcshareddata/swiftpm
cp HearsayWhisper/Package.resolved Hearsay.xcodeproj/project.xcworkspace/xcshareddata/swiftpm/Package.resolved
# Xcode does not re-copy a resource bundle whose inner folders changed (for
# example new .lproj translations), so drop the embedded copies first.
rm -rf ".build/derived/Build/Products/$CONFIGURATION/Hearsay.app/Contents/Resources/"*.bundle(N)
xcodebuild -project Hearsay.xcodeproj -scheme Hearsay \
  -destination 'platform=macOS,arch=arm64' -configuration "$CONFIGURATION" \
  -derivedDataPath .build/derived \
  -skipPackagePluginValidation -skipMacroValidation "${SETTINGS[@]}" build \
  > .build/run-debug.log 2>&1 < /dev/null || true
grep -E "BUILD|error:" .build/run-debug.log || true
if ! grep -q "BUILD SUCCEEDED" .build/run-debug.log; then
  echo "$CONFIGURATION build failed; see .build/run-debug.log" >&2
  if [[ $CI_MODE == 1 ]]; then
    echo "----- last 200 lines of .build/run-debug.log -----" >&2
    tail -n 200 .build/run-debug.log >&2
  fi
  exit 1
fi
APP=.build/derived/Build/Products/$CONFIGURATION/Hearsay.app
# Re-sign with a stable identity when one is in the keychain, so macOS keeps
# the Microphone and Screen & System Audio Recording grants across builds.
# An ad-hoc signature changes with every build and loses them. Override the
# name with HEARSAY_SIGNING_IDENTITY; set it to "-" to keep ad-hoc signing.
IDENTITY="${HEARSAY_SIGNING_IDENTITY:-Hearsay Code Signing (self-signed)}"
if [[ $IDENTITY != "-" ]] && security find-identity -v -p codesigning 2>/dev/null | grep -q "\"$IDENTITY\""; then
  codesign --force --deep --options runtime --timestamp=none \
    --entitlements Hearsay/Hearsay.entitlements --sign "$IDENTITY" "$APP" \
    && echo "Signed with: $IDENTITY" || echo "Signing with $IDENTITY failed; the build stays ad-hoc signed" >&2
fi
echo "Built $CONFIGURATION: $APP"
[[ $OPEN == 1 ]] && open "$APP"
exit 0
