#!/usr/bin/env bash
# Builds the OSDI worker, and the test-only model it can be driven against.
#
# A MISSING COMPILER PRINTS A MESSAGE AND SUCCEEDS. Same rule as the other workers: a missing worker
# must never be the reason somebody cannot build the application.
set -uo pipefail
cd "$(dirname "$0")"

# --dest <dir>: also copy the built worker there, so the application ships it. Mirrors the other
# workers' ensure-built scripts, which is where this convention comes from.
DEST=""
while [ $# -gt 0 ]; do
  case "$1" in
    --dest) DEST="${2:-}"; shift 2 ;;
    *) shift ;;
  esac
done

CC_BIN="${CC:-cc}"
if ! command -v "$CC_BIN" >/dev/null 2>&1; then
  echo "osdi-worker: no C compiler ('$CC_BIN') — skipping. The application still builds; OSDI-backed"
  echo "             devices will report an unavailable provider until this is built."
  exit 0
fi

WARN="-Wall -Wextra"

echo "osdi-worker: building worker"
"$CC_BIN" -O2 $WARN -o osdi-worker osdi_worker.c || {
  echo "osdi-worker: worker build FAILED"; exit 1; }

# The test model is a shared library in whatever form this platform loads. The extension is
# cosmetic — the worker dlopen()s whatever path it is given and never inspects the name.
MODEL_DIR="../fake-osdi-model"
if [ -f "$MODEL_DIR/fake_osdi.c" ]; then
  echo "osdi-worker: building test-only model (not shipped)"
  "$CC_BIN" -shared -fPIC -O2 $WARN -o "$MODEL_DIR/fake_osdi.osdi" "$MODEL_DIR/fake_osdi.c" || {
    echo "osdi-worker: test model build FAILED"; exit 1; }
fi

if [ -n "$DEST" ] && [ -d "$DEST" ]; then
  cp -f osdi-worker "$DEST/" 2>/dev/null && echo "osdi-worker: copied to $DEST"
fi

echo "osdi-worker: ok"
