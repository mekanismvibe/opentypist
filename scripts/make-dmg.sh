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
# On CI runners hdiutil sometimes fails with "Resource busy" while something
# else still has the new image open; a retry a few seconds later works.
for attempt in 1 2 3 4 5; do
    if output="$(hdiutil create -volname "OpenTypist $VERSION" -srcfolder "$STAGING" -fs HFS+ -format UDZO -ov "$DMG" 2>&1)"; then
        break
    fi
    if [[ "$output" != *"Resource busy"* ]] || [ "$attempt" = 5 ]; then
        echo "$output" >&2
        exit 1
    fi
    echo "hdiutil: resource busy, retrying ($attempt/5)" >&2
    sleep $((attempt * 5))
done
if [ -n "${CODESIGN_IDENTITY:-}" ] && [ "$CODESIGN_IDENTITY" != "-" ]; then
    codesign --force --timestamp --sign "$CODESIGN_IDENTITY" "$DMG"
fi
(cd "$(dirname "$DMG")" && shasum -a 256 "$(basename "$DMG")" > "$(basename "$DMG").sha256")
echo "Built $DMG"
