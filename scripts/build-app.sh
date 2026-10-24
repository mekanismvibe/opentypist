#!/bin/bash
# Builds build/OpenTypist.app: builds the vendored llama.cpp (once), compiles
# the app in release mode, assembles the bundle and signs it.
#
#   VERSION=0.2.0 BUILD_NUMBER=42 ./scripts/build-app.sh
#
# Without VERSION the version in Resources/Info.plist is used.
# CODESIGN_IDENTITY selects a signing certificate (default: ad-hoc).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP="$ROOT/build/OpenTypist.app"
cd "$ROOT"

./scripts/build-llama.sh
swift build -c release --arch arm64

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp .build/release/OpenTypist "$APP/Contents/MacOS/"
cp Resources/Info.plist "$APP/Contents/"
[ -f Resources/AppIcon.icns ] || swift scripts/make-icon.swift Resources/AppIcon.icns
cp Resources/AppIcon.icns "$APP/Contents/Resources/"
cp -R Resources/autocorrect "$APP/Contents/Resources/"

if [ -n "${VERSION:-}" ]; then
    plutil -replace CFBundleShortVersionString -string "${VERSION#v}" "$APP/Contents/Info.plist"
fi
if [ -n "${BUILD_NUMBER:-}" ]; then
    plutil -replace CFBundleVersion -string "$BUILD_NUMBER" "$APP/Contents/Info.plist"
fi

# Everything is linked statically: only system libraries may remain.
if otool -L "$APP/Contents/MacOS/OpenTypist" | tail -n +2 | grep -v -E "^\s*(/usr/lib/|/System/)"; then
    echo "Unexpected non-system library dependency (above)" >&2
    exit 1
fi

# Without CODESIGN_IDENTITY the app is ad-hoc signed with a designated
# requirement of just its bundle identifier. macOS ties Accessibility and
# Screen Recording grants to that requirement, so they survive rebuilds and
# updates (a plain ad-hoc signature is tied to the exact binary).
SIGN_ID="${CODESIGN_IDENTITY:--}"
if [ "$SIGN_ID" = "-" ]; then
    codesign --force --sign - -r='designated => identifier "org.opentypist.OpenTypist"' "$APP" >/dev/null
else
    codesign --force --options runtime --timestamp --sign "$SIGN_ID" "$APP" >/dev/null
fi

echo "Built $APP ($(plutil -extract CFBundleShortVersionString raw "$APP/Contents/Info.plist"))"
