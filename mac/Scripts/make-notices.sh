#!/bin/bash
# Generate mac/THIRD_PARTY_NOTICES.md from the LICENSE and
# NOTICE files in the resolved package checkouts and HearsayWhisper/LICENSES.
#
# Usage, from mac/: Scripts/make-notices.sh
#
# Needs the package checkouts in .build/derived/SourcePackages/checkouts
# (run Scripts/run-debug.sh once first). Versions come from
# HearsayWhisper/Package.resolved; the versions of the code vendored inside
# mlx-swift come from its own headers. The script stops if a file it expects
# is missing or if an Apache-2.0 license differs from the canonical text in
# anything but its copyright line and the Swift runtime library exception.
#
# Which packages are listed is decided by what the Release app links (see the
# BUNDLED lists below). Re-check after a dependency change with
#   nm -arch arm64 .build/derived/Build/Products/Release/Hearsay.app/Contents/MacOS/Hearsay
set -euo pipefail
cd "$(dirname "$0")/.."

CHECKOUTS=.build/derived/SourcePackages/checkouts
RESOLVED=HearsayWhisper/Package.resolved
LICENSES=HearsayWhisper/LICENSES
OUT=THIRD_PARTY_NOTICES.md
CMLX=$CHECKOUTS/mlx-swift/Source/Cmlx

die() { echo "make-notices: $*" >&2; exit 1; }
need() { [[ -f $1 ]] || die "missing $1"; }

[[ -d $CHECKOUTS ]] || die "no package checkouts in $CHECKOUTS; build the app first"
need "$RESOLVED"

# Version of a package identity in Package.resolved.
resolved_version() {
  local identity=$1 i=0 id
  while id=$(plutil -extract "pins.$i.identity" raw -o - "$RESOLVED" 2>/dev/null); do
    if [[ $id == "$identity" ]]; then
      plutil -extract "pins.$i.state.version" raw -o - "$RESOLVED"
      return
    fi
    i=$((i + 1))
  done
  die "$identity is not in $RESOLVED"
}

# Value of `#define NAME value` in a header.
define_value() {
  need "$2"
  awk -v n="$1" '$1 == "#define" && $2 == n { print $3; exit }' "$2"
}

# First real copyright line of a file (not the Apache appendix template).
copyright_line() {
  need "$1"
  local line
  line=$(grep -m1 -E '^[[:space:]]*(//[[:space:]]*)?Copyright ' "$1" | grep -v '\[yyyy\]' || true)
  [[ -n $line ]] || die "no copyright line in $1"
  echo "$line" | sed -E 's#^[[:space:]]*(//[[:space:]]*)?##; s#[[:space:]]+$##'
}

# The leading /* ... */ license comment of a C or C++ source file.
leading_comment() {
  need "$1"
  awk 'NR == 1 && $0 != "/*" { exit 1 } NR == 1 { next } $0 == "*/" { exit } { print }' "$1" \
    || die "$1 does not start with a license comment"
}

fence() {
  echo '```text'
  # Strip leading blank lines and trailing whitespace; keep the text verbatim otherwise.
  sed -e '/./,$!d' -e 's/[[:space:]]*$//' "$1"
  echo '```'
}

fence_stdin() {
  echo '```text'
  sed -e 's/[[:space:]]*$//'
  echo '```'
}

# Canonical Apache-2.0 text: swift-crypto ships it unmodified (template
# appendix, no exception), so it is the reference the others are checked against.
APACHE=$CHECKOUTS/swift-crypto/LICENSE.txt
need "$APACHE"

apache_body() {
  # The license with the appendix copyright line and any runtime library
  # exception removed, whitespace-normalized, for comparison.
  sed -E '/^[[:space:]]*Copyright /d; /Runtime Library Exception/,$d' "$1" \
    | tr -s ' \t\n' ' '
}

check_apache() {
  need "$1"
  [[ $(apache_body "$1") == $(apache_body "$APACHE") ]] \
    || die "$1 is not the plain Apache-2.0 text; review it by hand"
}

MLX_SWIFT_VERSION=$(resolved_version mlx-swift)
MLX_VERSION="$(define_value MLX_VERSION_MAJOR "$CMLX/mlx/mlx/version.h").$(define_value MLX_VERSION_MINOR "$CMLX/mlx/mlx/version.h").$(define_value MLX_VERSION_PATCH "$CMLX/mlx/mlx/version.h")"
need "$CMLX/mlx-c/CMakeLists.txt"
MLXC_VERSION=$(sed -nE 's/^[[:space:]]*set\(MLX_C_VERSION ([0-9.]+)\)/\1/p' "$CMLX/mlx-c/CMakeLists.txt" | head -1)
JSON_H=$CMLX/json/single_include/nlohmann/json.hpp
JSON_VERSION="$(define_value NLOHMANN_JSON_VERSION_MAJOR "$JSON_H").$(define_value NLOHMANN_JSON_VERSION_MINOR "$JSON_H").$(define_value NLOHMANN_JSON_VERSION_PATCH "$JSON_H")"
FMT_RAW=$(define_value FMT_VERSION "$CMLX/fmt/include/fmt/base.h")
FMT_VERSION="$((FMT_RAW / 10000)).$((FMT_RAW / 100 % 100)).$((FMT_RAW % 100))"
need "$CMLX/vendor-README.md"
METALCPP_VERSION=$(grep -oE 'metal-cpp_[A-Za-z0-9_.-]+\.zip' "$CMLX/vendor-README.md" | head -1 | sed 's/\.zip$//')
[[ -n $MLXC_VERSION && -n $METALCPP_VERSION ]] || die "could not read mlx-c or metal-cpp version"

# MIT and other permissive (non-Apache) components linked into Hearsay.app.
# name|version|url|license|license file
MIT_BUNDLED=(
  "mlx-swift|$MLX_SWIFT_VERSION|https://github.com/ml-explore/mlx-swift|MIT|$CHECKOUTS/mlx-swift/LICENSE"
  "MLX (vendored in mlx-swift)|$MLX_VERSION|https://github.com/ml-explore/mlx|MIT|$CMLX/mlx/LICENSE"
  "mlx-c (vendored in mlx-swift)|$MLXC_VERSION|https://github.com/ml-explore/mlx-c|MIT|$CMLX/mlx-c/LICENSE"
  "JSON for Modern C++ (nlohmann/json, vendored in mlx-swift)|$JSON_VERSION|https://github.com/nlohmann/json|MIT|$CMLX/json/LICENSE.MIT"
  "{fmt} (vendored in mlx-swift)|$FMT_VERSION|https://github.com/fmtlib/fmt|MIT with optional binary exception|$CMLX/fmt/LICENSE"
  "yyjson|$(resolved_version yyjson)|https://github.com/ibireme/yyjson|MIT|$CHECKOUTS/yyjson/LICENSE"
  "EventSource|$(resolved_version eventsource)|https://github.com/mattt/EventSource|MIT|$CHECKOUTS/EventSource/LICENSE.md"
)

# Apache-2.0 components linked into Hearsay.app.
# name|version|url|license file|file holding the copyright line|NOTICE file or -
APACHE_BUNDLED=(
  "swift-transformers|$(resolved_version swift-transformers)|https://github.com/huggingface/swift-transformers|$CHECKOUTS/swift-transformers/LICENSE|$CHECKOUTS/swift-transformers/LICENSE|-"
  "swift-huggingface|$(resolved_version swift-huggingface)|https://github.com/huggingface/swift-huggingface|$CHECKOUTS/swift-huggingface/LICENSE|$CHECKOUTS/swift-huggingface/LICENSE|-"
  "swift-jinja|$(resolved_version swift-jinja)|https://github.com/huggingface/swift-jinja|$CHECKOUTS/swift-jinja/LICENSE|$CHECKOUTS/swift-jinja/LICENSE|-"
  "swift-collections|$(resolved_version swift-collections)|https://github.com/apple/swift-collections|$CHECKOUTS/swift-collections/LICENSE.txt|$CHECKOUTS/swift-collections/Sources/OrderedCollections/OrderedSet/OrderedSet.swift|-"
  "swift-numerics|$(resolved_version swift-numerics)|https://github.com/apple/swift-numerics|$CHECKOUTS/swift-numerics/LICENSE.txt|$CHECKOUTS/swift-numerics/Sources/ComplexModule/Complex.swift|-"
  "swift-crypto|$(resolved_version swift-crypto)|https://github.com/apple/swift-crypto|$CHECKOUTS/swift-crypto/LICENSE.txt|$CHECKOUTS/swift-crypto/Sources/Crypto/Digests/Digests.swift|$CHECKOUTS/swift-crypto/NOTICE.txt"
  "metal-cpp (vendored in mlx-swift)|$METALCPP_VERSION|https://developer.apple.com/metal/cpp/|$CMLX/metal-cpp/LICENSE.txt|$CMLX/metal-cpp/LICENSE.txt|-"
)

POCKETFFT=$CMLX/mlx/mlx/3rdparty/pocketfft.h
need "$POCKETFFT"

need ../LICENSE
for f in mlx-audio-swift.txt mlx-whisper.txt openai-whisper.txt; do need "$LICENSES/$f"; done

TMP=$(mktemp)
trap 'rm -f "$TMP"' EXIT

{
cat <<'EOF'
# Third-Party Notices

Hearsay is MIT licensed (see `LICENSE`, Copyright (c) 2026 Chihling Wang).
It is built with the open-source software listed below. This file is
generated by `mac/Scripts/make-notices.sh` from the license files of the exact
versions the app is built with; do not edit it by hand.

1. Bundled libraries: compiled and statically linked into Hearsay.app.
2. Copied and ported code: source in this repository that comes from other projects.
3. Credited, not shipped: models and programs Hearsay uses but does not include.
4. Not used.

## 1. Bundled libraries

| Component | Version | License |
| --- | --- | --- |
EOF
for entry in "${MIT_BUNDLED[@]}"; do
  IFS='|' read -r name version url license file <<<"$entry"
  echo "| [$name]($url) | $version | $license |"
done
echo "| [PocketFFT C++ (vendored in MLX)](https://github.com/mreineck/pocketfft) | bundled with MLX $MLX_VERSION | BSD-3-Clause |"
for entry in "${APACHE_BUNDLED[@]}"; do
  IFS='|' read -r name version url file cfile notice <<<"$entry"
  echo "| [$name]($url) | $version | Apache-2.0 |"
done
cat <<'EOF'

On Apple platforms swift-crypto forwards to CryptoKit; its bundled BoringSSL
is not compiled into Hearsay.

### 1.1 MIT and BSD licensed libraries

EOF
for entry in "${MIT_BUNDLED[@]}"; do
  IFS='|' read -r name version url license file <<<"$entry"
  need "$file"
  echo "#### $name $version"
  echo
  echo "$url, $license."
  echo
  fence "$file"
  echo
done
echo "#### PocketFFT C++ (vendored in MLX $MLX_VERSION)"
echo
echo "https://github.com/mreineck/pocketfft, BSD-3-Clause. From the header comment of the vendored \`mlx/3rdparty/pocketfft.h\`."
echo
leading_comment "$POCKETFFT" | fence_stdin
echo
cat <<'EOF'
### 1.2 Apache License 2.0 libraries

Each library below is licensed under the Apache License, Version 2.0, whose
full text follows the list. Copyright lines, NOTICE files and license
additions are given per library.

EOF
for entry in "${APACHE_BUNDLED[@]}"; do
  IFS='|' read -r name version url file cfile notice <<<"$entry"
  check_apache "$file"
  echo "#### $name $version"
  echo
  echo "$url, Apache-2.0. $(copyright_line "$cfile")"
  echo
  if grep -q 'Runtime Library Exception' "$file"; then
    echo "Its LICENSE adds:"
    echo
    sed -n '/Runtime Library Exception/,$p' "$file" | fence_stdin
    echo
  fi
  if [[ $notice != "-" ]]; then
    need "$notice"
    echo "NOTICE:"
    echo
    fence "$notice"
    echo
  fi
done
echo "#### Apache License, Version 2.0"
echo
fence "$APACHE"
echo
cat <<EOF
## 2. Copied and ported code

#### mlx-audio-swift (copied)

\`mac/HearsayWhisper/Sources/HearsayWhisper/Model/\` is vendored from
https://github.com/Blaizzy/mlx-audio-swift at commit 01dec7c9, with local
changes marked "Hearsay:". MIT.

EOF
fence "$LICENSES/mlx-audio-swift.txt"
cat <<EOF

#### mlx_whisper 0.4.3 (ported)

\`mac/HearsayWhisper/Sources/HearsayWhisper/Decoder/\` is a Swift port of the
Python package \`mlx_whisper\` 0.4.3 from
https://github.com/ml-explore/mlx-examples (\`whisper/\`). MIT.

EOF
fence "$LICENSES/mlx-whisper.txt"
cat <<EOF

#### OpenAI Whisper (tables copied)

The language tables in \`WhisperLanguages.swift\` originate in
https://github.com/openai/whisper (\`whisper/tokenizer.py\`), by way of
mlx_whisper. MIT.

EOF
fence "$LICENSES/openai-whisper.txt"
cat <<'EOF'

## 3. Credited, not shipped

These are downloaded or run at the user's request and are not part of
Hearsay.app. No license text is required here; each is under its own terms.

- Whisper model weights, downloaded from Hugging Face at runtime:
  `mlx-community/whisper-*` models, Apache-2.0 per their model cards
  (https://huggingface.co/mlx-community). `mlx-community/whisper-large-v3-turbo`
  declares no license on its card; its source model
  `openai/whisper-large-v3-turbo` is MIT (https://huggingface.co/openai/whisper-large-v3-turbo).
- Whisper tokenizer files, downloaded at runtime from `openai/whisper-large-v3`,
  Apache-2.0 (https://huggingface.co/openai/whisper-large-v3).
- AI command-line tools Hearsay can run for meeting notes, if the user has
  installed them: GitHub Copilot CLI, Claude Code, OpenAI Codex CLI and
  Antigravity CLI. They are separate products under their own terms, are
  not shipped with Hearsay, and Hearsay does not include any of their code.
  The same holds for local model servers such as Ollama or LM Studio that
  Hearsay can send notes requests to.

## 4. Not used

- FFmpeg: unlike whisper-tools, Hearsay does not use or ship FFmpeg; audio
  is decoded with Apple's AVFoundation.
EOF
} > "$TMP"

mv "$TMP" "$OUT"
trap - EXIT
echo "Wrote $OUT"
