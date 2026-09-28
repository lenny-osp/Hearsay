#!/bin/sh
# Copies the help pages from shared/help/<lang>/Help.html (the files
# translators edit) to Hearsay/Resources/<lang>.lproj/Help.html, where Xcode
# bundles them. The copies are git-ignored; never edit them.
# Run from anywhere; Scripts/run-debug.sh and Scripts/generate-project.sh
# call it first.
set -eu
cd "$(dirname "$0")/.."
SHARED=../shared/help
RESOURCES=Hearsay/Resources
status=0
found=0

for folder in "$SHARED"/*/; do
    [ -d "$folder" ] || continue
    lang=$(basename "$folder")
    source="$folder/Help.html"
    if [ ! -f "$source" ]; then
        echo "sync-shared: $SHARED/$lang has no Help.html" >&2
        status=1
        continue
    fi
    found=$((found + 1))
    target="$RESOURCES/$lang.lproj/Help.html"
    mkdir -p "$RESOURCES/$lang.lproj"
    # Copy only when the content differs, so unchanged pages keep their
    # modification time and do not trigger a rebuild.
    cmp -s "$source" "$target" 2>/dev/null || cp "$source" "$target"
    if ! cmp -s "$source" "$target"; then
        echo "sync-shared: $target does not match $source" >&2
        status=1
    fi
done

if [ "$found" -eq 0 ]; then
    echo "sync-shared: no help pages found in $SHARED" >&2
    exit 1
fi

# Every interface language the app bundles needs a help page.
for folder in "$RESOURCES"/*.lproj; do
    [ -d "$folder" ] || continue
    lang=$(basename "$folder" .lproj)
    [ "$lang" = "Base" ] && continue
    if [ ! -f "$SHARED/$lang/Help.html" ]; then
        echo "sync-shared: $folder has no shared/help/$lang/Help.html" >&2
        status=1
    fi
done

exit "$status"
