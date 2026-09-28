#!/bin/sh
# Regenerates Hearsay.xcodeproj from project.yml (XcodeGen).
set -eu
cd "$(dirname "$0")/.."
if ! command -v xcodegen >/dev/null 2>&1; then
    echo "xcodegen not found. Install it with: brew install xcodegen" >&2
    exit 1
fi
xcodegen generate "$@"
