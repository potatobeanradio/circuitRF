#!/usr/bin/env bash
# ── Build the circuitRF per-user launcher stub ────────────────────────────────────────────────
#
#   build-stub.sh [x64|arm64|x86] [app-name]
#
# Writes build/<app-name>-stub-<arch>.exe. Follows tools/senior-worker/build.sh: zig cc is the
# preferred route because it cross-compiles a Windows PE from any host with one download and no
# daemon, which is what lets this stub be built and checked on a machine that is not Windows.
#
# The Windows-native route is build-stub.ps1, which build-msi.ps1 calls.
set -eu

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
arch="${1:-x64}"
app="${2:-circuitRF}"
out="$here/build"
mkdir -p "$out"

case "$arch" in
    x64)   target=x86_64-windows-gnu  ;;
    arm64) target=aarch64-windows-gnu ;;
    x86)   target=x86-windows-gnu     ;;
    *) echo "unknown architecture '$arch' (expected x64, arm64 or x86)" >&2; exit 2 ;;
esac

ZIG=${CRF_ZIG:-zig}
if ! command -v "$ZIG" >/dev/null 2>&1; then
    echo "zig is not on PATH. Install it (brew install zig / winget install zig.zig), or build on" >&2
    echo "Windows with packaging/windows/stub/build-stub.ps1." >&2
    exit 1
fi

exe="$out/$app-stub-$arch.exe"

# -mwindows: a GUI subsystem binary, so launching from a shortcut opens no console window. The stub
# still writes to stderr for anyone who runs it from a terminal.
"$ZIG" cc -target "$target" -O2 -municode -mwindows \
    -DCRF_APP_NAME="\"$app\"" \
    "$here/circuitrf-stub.c" -o "$exe" -luser32

echo "built $exe"
