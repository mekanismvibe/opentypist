#!/bin/bash
# Builds build/OpenTypist.app: compiles in release mode, then copies the
# Homebrew llama.cpp/ggml libraries (and their ggml backends) into
# Contents/Frameworks so the app runs without Homebrew paths.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BREW="${HOMEBREW_PREFIX:-/opt/homebrew}"
APP="$ROOT/build/OpenTypist.app"
FW="$APP/Contents/Frameworks"
SEARCH_DIRS=("$BREW/opt/llama.cpp/lib" "$BREW/opt/ggml/lib" "$BREW/opt/libomp/lib")

for dir in "$BREW/opt/llama.cpp/lib" "$BREW/opt/ggml/libexec"; do
    [ -d "$dir" ] || { echo "Missing $dir. Install llama.cpp first: brew install llama.cpp" >&2; exit 1; }
done

cd "$ROOT"
swift build -c release

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$FW" "$APP/Contents/Resources"
cp .build/release/OpenTypist "$APP/Contents/MacOS/"
cp Resources/Info.plist "$APP/Contents/"
[ -f Resources/AppIcon.icns ] || swift scripts/make-icon.swift Resources/AppIcon.icns
cp Resources/AppIcon.icns "$APP/Contents/Resources/"

# ggml loads its compute backends (Metal, CPU variants, BLAS) with dlopen.
cp "$BREW"/opt/ggml/libexec/libggml-*.so "$FW/"

resolve() {
    local name="$1"
    for dir in "${SEARCH_DIRS[@]}"; do
        [ -f "$dir/$name" ] && { echo "$dir/$name"; return 0; }
    done
    return 1
}

# Copy every Homebrew dylib that $1 links against into Frameworks, recursively,
# and rewrite the references to @rpath/<name>.
bundle_deps() {
    local file="$1"
    local dep name src
    for dep in $(otool -L "$file" | tail -n +2 | awk '{print $1}'); do
        name="$(basename "$dep")"
        case "$dep" in
            "$BREW"/*) src="$dep" ;;
            @rpath/*) src="$(resolve "$name")" || continue ;;
            *) continue ;;
        esac
        # A library's first entry is its own install name.
        [ "$name" = "$(basename "$file")" ] && continue
        if [ ! -f "$FW/$name" ]; then
            cp -L "$src" "$FW/$name"
            chmod u+w "$FW/$name"
            install_name_tool -id "@rpath/$name" "$FW/$name" 2>/dev/null
            install_name_tool -add_rpath "@loader_path" "$FW/$name" 2>/dev/null || true
            bundle_deps "$FW/$name"
        fi
        [ "$dep" != "@rpath/$name" ] && install_name_tool -change "$dep" "@rpath/$name" "$file" 2>/dev/null
    done
}

bundle_deps "$APP/Contents/MacOS/OpenTypist"
for backend in "$FW"/libggml-*.so; do
    chmod u+w "$backend"
    install_name_tool -add_rpath "@loader_path" "$backend" 2>/dev/null || true
    bundle_deps "$backend"
done

# Fail loudly if anything still points into Homebrew.
if otool -L "$APP/Contents/MacOS/OpenTypist" "$FW"/* | grep -q "$BREW/"; then
    echo "Unbundled Homebrew references remain:" >&2
    otool -L "$APP/Contents/MacOS/OpenTypist" "$FW"/* | grep "$BREW/" >&2
    exit 1
fi

# Sign. Without CODESIGN_IDENTITY the app is ad-hoc signed with a designated
# requirement of just its bundle identifier. macOS ties Accessibility and
# Screen Recording grants to that requirement, so they survive rebuilds
# (a plain ad-hoc signature is tied to the exact binary and is lost on every
# rebuild). Set CODESIGN_IDENTITY to a real certificate for distribution.
SIGN_ID="${CODESIGN_IDENTITY:--}"
for lib in "$FW"/*; do codesign --force --sign "$SIGN_ID" "$lib" >/dev/null; done
if [ "$SIGN_ID" = "-" ]; then
    codesign --force --sign - -r='designated => identifier "org.opentypist.OpenTypist"' "$APP" >/dev/null
else
    codesign --force --sign "$SIGN_ID" "$APP" >/dev/null
fi

echo "Built $APP"
