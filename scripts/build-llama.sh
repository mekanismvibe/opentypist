#!/bin/bash
# Builds the vendored llama.cpp (vendor/llama.cpp, a git submodule pinned to a
# release) as static libraries for Apple silicon, macOS 14 and later, into
# build/llama-install. Skips the build when that revision is already built.
# Needs CMake (`brew install cmake`); Ninja is used when available.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SRC="$ROOT/vendor/llama.cpp"
BUILD="$ROOT/build/llama"
PREFIX="$ROOT/build/llama-install"
STAMP="$PREFIX/.built-from"

if [ ! -f "$SRC/CMakeLists.txt" ]; then
    git -C "$ROOT" submodule update --init --depth 1 vendor/llama.cpp
fi
REVISION="$(git -C "$SRC" rev-parse HEAD)"
if [ -f "$STAMP" ] && [ "$(cat "$STAMP")" = "$REVISION" ]; then
    echo "llama.cpp ${REVISION:0:10} already built"
    exit 0
fi
command -v cmake >/dev/null || { echo "CMake is required: brew install cmake" >&2; exit 1; }

GENERATOR=()
command -v ninja >/dev/null && GENERATOR=(-G Ninja)

cmake -S "$SRC" -B "$BUILD" "${GENERATOR[@]}" \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_OSX_DEPLOYMENT_TARGET=14.0 \
    -DCMAKE_OSX_ARCHITECTURES=arm64 \
    -DBUILD_SHARED_LIBS=OFF \
    -DGGML_NATIVE=OFF \
    -DGGML_OPENMP=OFF \
    -DGGML_METAL=ON \
    -DGGML_METAL_EMBED_LIBRARY=ON \
    -DGGML_BLAS=ON \
    -DGGML_ACCELERATE=ON \
    -DLLAMA_BUILD_COMMON=OFF \
    -DLLAMA_BUILD_TESTS=OFF \
    -DLLAMA_BUILD_TOOLS=OFF \
    -DLLAMA_BUILD_EXAMPLES=OFF \
    -DLLAMA_BUILD_SERVER=OFF \
    -DLLAMA_BUILD_APP=OFF \
    -DLLAMA_BUILD_UI=OFF \
    -DLLAMA_BUILD_HTML=OFF \
    -DLLAMA_BUILD_MTMD=OFF \
    -DLLAMA_OPENSSL=OFF
cmake --build "$BUILD" --config Release -j "$(sysctl -n hw.ncpu)"
rm -rf "$PREFIX"
cmake --install "$BUILD" --prefix "$PREFIX" >/dev/null
echo "$REVISION" > "$STAMP"
echo "Built llama.cpp ${REVISION:0:10} into $PREFIX"
