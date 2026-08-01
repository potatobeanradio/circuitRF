#!/bin/sh
# Keeps senior_worker up to date as part of an ordinary `dotnet build` / `dotnet run`.
#
# THIS SCRIPT MUST NEVER FAIL A BUILD.
# ------------------------------------
# The worker is what evaluates compiled device models. It is optional: circuitRF builds, runs, and
# does everything else without it — a design with no such devices never notices it is missing. So
# every failure here is reported and swallowed (no cross-compiler, no network for a first image
# pull, a compile error) and the exit status stays 0 unless --strict is passed, which CI and release
# builds use, where silence would be wrong.
#
# Mirrors tools/macos-vmhost/ensure-built.sh deliberately: one shape for "a helper circuitRF ships",
# so neither has to be learned separately.
set -u

here=$(cd "$(dirname "$0")" && pwd)
build_dir=$here/build
binary=$build_dir/senior_worker

dest=""
strict=0

while [ $# -gt 0 ]; do
    case "$1" in
        --dest)   dest=$2; shift 2 ;;
        --strict) strict=1; shift ;;
        *) shift ;;
    esac
done

say()  { echo "senior-worker: $*"; }
skip() { say "$*"; [ "$strict" = 1 ] && exit 1; exit 0; }

# "Newer than the output" is the whole staleness rule — cheap, and editing the source is enough to
# get a rebuild without anyone remembering to ask for one.
if [ -f "$binary" ] && [ ! "$here/senior_worker.c" -nt "$binary" ] \
                    && [ ! "$here/build.sh"        -nt "$binary" ]; then
    :
else
    have=0
    for t in zig docker podman; do command -v "$t" >/dev/null 2>&1 && have=1; done
    [ "$(uname -s)" = Linux ] && [ "$(uname -m)" = x86_64 ] && have=1

    [ "$have" = 1 ] || skip "no x86-64 Linux compiler found (zig, docker or podman); skipping the device worker. circuitRF runs normally; compiled device models need it."

    say "building senior_worker"
    if ! "$here/build.sh" >/tmp/crf-senior-worker-build.log 2>&1; then
        say "senior_worker did not build; see /tmp/crf-senior-worker-build.log"
        say "circuitRF will run normally, but compiled device models will not be available."
        [ "$strict" = 1 ] && exit 1
        exit 0
    fi
fi

# ── Publish beside the app ────────────────────────────────────────────────────
# Next to the assemblies, because that is where DeviceWorkerManifest.ToolsDirectory looks — and on
# macOS it is the directory the VM host shares in, so the guest finds it at /mnt/crfw/senior_worker.
if [ -n "$dest" ] && [ -f "$binary" ]; then
    mkdir -p "$dest"
    cp -p "$binary" "$dest/" 2>/dev/null

    # The alias map travels WITH the worker. It names internal nodes a compiled model never drives,
    # and without it those become unknowns nobody wrote an equation for — a bias ramp that stalls
    # and grinds rather than an error. It is data, not a build product, so it is copied from source.
    [ -f "$here/alias-map.json" ] && cp -p "$here/alias-map.json" "$dest/" 2>/dev/null
fi

exit 0
