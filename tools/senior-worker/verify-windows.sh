#!/bin/sh
# verify-windows.sh — exercise the Windows worker by actually loading something.
#
# WHY THIS EXISTS. Most of the Windows path cannot be tested from C# at all: staging a file under a
# name read out of a model's import table, binding that model's import to an already-loaded module,
# and stdio mode are all properties of a running process, not of a function. Without this they were
# covered "by construction", which is the weakest kind of covered.
#
# WHAT IT RUNS ON. Wine, inside a container, so it works from macOS and Linux as well as Windows CI.
#
#   *** WINE IS NOT WINDOWS. *** See the caveats printed at the end before treating a PASS here as
#   proof. It exercises the mechanism; it does not prove a compiled model library loads.
#
# Three files, on purpose: this one orchestrates on the host, verify-windows-inner.sh runs inside
# the container, and verify-windows-drive.py holds the checks. Nesting them in one file means
# nesting quotes, which is how the first version of this broke on an apostrophe.
#
# Needs docker (or podman). Deliberately NOT wired into `dotnet build` — a verification script must
# never be able to fail an application build. Run it by hand, or from a CI job.
set -eu

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/.." && pwd)

engine=""
for e in docker podman; do command -v "$e" >/dev/null 2>&1 && { engine=$e; break; }; done
[ -n "$engine" ] || { echo "verify-windows: needs docker or podman."; exit 1; }

say() { echo "verify-windows: $*"; }

# ── the pieces under test, built if absent ───────────────────────────────────
[ -f "$here/build/senior_worker.exe" ] && [ -f "$here/build/crf-model-host.dll" ] || {
    say "building the Windows worker"; "$here/build.sh" windows; }
[ -f "$root/fake-model-lib/build/fake_model.dll" ] || {
    say "building the fake model library"; "$root/fake-model-lib/build.sh" windows; }

say "running under Wine (the first run pulls an image and takes a while)"

status=0
"$engine" run --rm --platform linux/amd64 -v "$root:/w" -w /w \
    debian:bookworm-slim /w/senior-worker/verify-windows-inner.sh || status=$?

cat <<'CAVEAT'

verify-windows: WHAT A PASS HERE DOES AND DOES NOT MEAN

  It DOES mean the mechanism works: the host module name is read out of a real model's real import
  table, the shim is staged under that name from a read-only install by an unprivileged user, the
  model's import binds to the already-loaded staged module, the PE export walk finds the family, and
  raw doubles survive stdio -- proven by the control, which fails without _setmode.

  It does NOT mean a compiled model library loads. Wine is a reimplementation and the fixture is ours.
  Still open, and only a real Windows machine with a kit can close it:
    - whether the 15 symbols are SUFFICIENT (they are demonstrably necessary);
    - a CRT mismatch against a UCRT-built library;
    - whether the kit's own an extra export export wants anything at load time;
    - the vectored exception handler under a real access violation.
CAVEAT

exit $status
