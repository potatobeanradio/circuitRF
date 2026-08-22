#!/usr/bin/env bash
# Builds the OSDI worker, and the test-only model it can be driven against.
#
# A MISSING COMPILER PRINTS A MESSAGE AND SUCCEEDS. Same rule as the other workers: a missing worker
# must never be the reason somebody cannot build the application.
set -uo pipefail
cd "$(dirname "$0")"

# --dest <dir>: also copy the built worker there, so the application ships it. Mirrors the other
# workers' ensure-built scripts, which is where this convention comes from.
#
# --arch <arm64|x86_64>: build for a Mac other than this one. Apple's clang targets both slices from
# either kind of Mac, so one run of packaging/macos/build-dmg.sh can produce both disk images; the
# .csproj derives this from the RuntimeIdentifier, so `dotnet publish -r osx-x64` gets it right on
# its own. Ignored off macOS, where -arch means nothing and there is only ever one target.
DEST=""
ARCH=""
while [ $# -gt 0 ]; do
  case "$1" in
    --dest) DEST="${2:-}"; shift 2 ;;
    --arch) ARCH="${2:-}"; shift 2 ;;
    *) shift ;;
  esac
done

ARCHFLAGS=""
OUT_DIR="."
if [ "$(uname -s)" = Darwin ]; then
  [ -n "$ARCH" ] || ARCH=$(uname -m)
  case "$ARCH" in
    arm64|aarch64) ARCH=arm64  ;;
    x86_64|x64)    ARCH=x86_64 ;;
    *) echo "osdi-worker: unsupported architecture '$ARCH' - skipping."; exit 0 ;;
  esac
  ARCHFLAGS="-arch $ARCH"
  # Per-architecture, so two binaries of the same name that differ only in a way no `ls` shows can
  # never be mistaken for each other. The host build is ALSO copied to ./osdi-worker at the end,
  # because that flat path is what tools/osdi-worker/verify.py and the README already name.
  OUT_DIR="build/$ARCH"
  mkdir -p "$OUT_DIR"
fi

CC_BIN="${CC:-cc}"
if ! command -v "$CC_BIN" >/dev/null 2>&1; then
  echo "osdi-worker: no C compiler ('$CC_BIN') — skipping. The application still builds; OSDI-backed"
  echo "             devices will report an unavailable provider until this is built."
  exit 0
fi

WARN="-Wall -Wextra"

echo "osdi-worker: building worker${ARCH:+ ($ARCH)}"
"$CC_BIN" -O2 $WARN $ARCHFLAGS -o "$OUT_DIR/osdi-worker" osdi_worker.c || {
  echo "osdi-worker: worker build FAILED"; exit 1; }

# The flat path the rest of the repo already names, for the host's own architecture only. A
# cross-built binary must NOT land there: verify.py would then run a program this machine cannot
# execute, and say nothing useful about why.
if [ "$OUT_DIR" != "." ] && [ "$ARCH" = "$(uname -m | sed 's/aarch64/arm64/')" ]; then
  cp -f "$OUT_DIR/osdi-worker" ./osdi-worker
fi

# The test model is a shared library in whatever form this platform loads. The extension is
# cosmetic — the worker dlopen()s whatever path it is given and never inspects the name.
MODEL_DIR="../fake-osdi-model"
if [ -f "$MODEL_DIR/fake_osdi.c" ]; then
  echo "osdi-worker: building test-only model (not shipped)"
  # The host's own architecture, always: this model exists to be dlopen()ed by a worker running
  # HERE, in a test. A cross-built one could not be loaded by anything on this machine.
  "$CC_BIN" -shared -fPIC -O2 $WARN -o "$MODEL_DIR/fake_osdi.osdi" "$MODEL_DIR/fake_osdi.c" || {
    echo "osdi-worker: test model build FAILED"; exit 1; }
fi

if [ -n "$DEST" ] && [ -d "$DEST" ]; then
  cp -f "$OUT_DIR/osdi-worker" "$DEST/" 2>/dev/null && echo "osdi-worker: copied to $DEST"
fi

echo "osdi-worker: ok"
