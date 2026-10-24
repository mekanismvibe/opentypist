#!/bin/bash
# Packs build/OpenTypist.app into a drag-to-Applications disk image:
#   ./scripts/make-dmg.sh  →  build/OpenTypist-<version>.dmg (+ .sha256)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APP="$ROOT/build/OpenTypist.app"
[ -d "$APP" ] || { echo "Build the app first: ./scripts/build-app.sh" >&2; exit 1; }
VERSION="$(plutil -extract CFBundleShortVersionString raw "$APP/Contents/Info.plist")"
DMG="$ROOT/build/OpenTypist-$VERSION.dmg"
STAGING="$(mktemp -d)"
trap 'rm -rf "$STAGING"' EXIT

ditto "$APP" "$STAGING/OpenTypist.app"
ln -s /Applications "$STAGING/Applications"
rm -f "$DMG"
hdiutil create -volname "OpenTypist $VERSION" -srcfolder "$STAGING" -fs HFS+ -format UDZO -ov "$DMG" >/dev/null
if [ -n "${CODESIGN_IDENTITY:-}" ] && [ "$CODESIGN_IDENTITY" != "-" ]; then
    codesign --force --timestamp --sign "$CODESIGN_IDENTITY" "$DMG"
fi
(cd "$(dirname "$DMG")" && shasum -a 256 "$(basename "$DMG")" > "$(basename "$DMG").sha256")
echo "Built $DMG"
