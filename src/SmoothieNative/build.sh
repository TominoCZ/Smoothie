#!/usr/bin/env bash
# Builds the native depth pyramid: SmoothieNative.dll for the game (mingw-w64) and
# libSmoothieNative.so for the self-check on Linux, into bin/ next to this script.
# x86-64 baseline only (SSE2): no -march, so no instruction a 64-bit CPU might lack.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT="$HERE/bin"
mkdir -p "$OUT"
FLAGS=(-O2 -std=c11 -ffp-contract=off -fno-strict-aliasing -Wall -Wextra -Werror)
x86_64-w64-mingw32-gcc "${FLAGS[@]}" -shared -static-libgcc -o "$OUT/SmoothieNative.dll" "$HERE/pyramid.c" "$HERE/animpause.c" -Wl,--no-insert-timestamp
x86_64-w64-mingw32-strip "$OUT/SmoothieNative.dll"
gcc "${FLAGS[@]}" -shared -fPIC -fvisibility=hidden -o "$OUT/libSmoothieNative.so" "$HERE/pyramid.c" "$HERE/animpause.c" -lm
