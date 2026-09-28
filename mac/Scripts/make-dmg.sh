#!/bin/zsh
# Builds the drag-to-install disk image for a release.
# Usage: Scripts/make-dmg.sh <path/to/Hearsay.app> <version> <out.dmg>
#
# The image (compressed, UDZO) is named "Hearsay <version>" and holds
# Hearsay.app next to an Applications link, so users drag the app onto it.
# Only hdiutil and ditto; no third-party tool. An existing <out.dmg> is
# replaced.
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "Usage: $0 <path/to/Hearsay.app> <version> <out.dmg>" >&2
  exit 2
fi
APP=$1
VERSION=$2
OUT=$3

[[ -d $APP && -f $APP/Contents/Info.plist ]] || { echo "make-dmg: $APP is not an app bundle" >&2; exit 1; }
[[ -n $VERSION ]] || { echo "make-dmg: the version is empty" >&2; exit 1; }
[[ $OUT == *.dmg ]] || { echo "make-dmg: $OUT must end in .dmg" >&2; exit 1; }

STAGING=$(mktemp -d "${TMPDIR:-/tmp}/hearsay-dmg.XXXXXX")
trap 'rm -rf "$STAGING"' EXIT

# ditto keeps the code signature, extended attributes, and symlinks intact.
ditto "$APP" "$STAGING/Hearsay.app"
ln -s /Applications "$STAGING/Applications"

mkdir -p "$(dirname "$OUT")"
hdiutil create -volname "Hearsay $VERSION" -srcfolder "$STAGING" -ov -format UDZO "$OUT" >/dev/null
hdiutil verify "$OUT" >/dev/null
echo "Wrote $OUT"
