#!/bin/sh
# Builds the device worker.
#
#   build.sh            builds for the host's own natural target (see below)
#   build.sh linux      builds senior_worker            (x86-64 Linux ELF)
#   build.sh windows    builds crf-model-host.dll + senior_worker.exe   (x86-64 Windows PE)
#
# THE LINUX TARGET IS ALWAYS x86-64, and that is a statement about what can WORK, not a
# convenience: on an arm64 Mac the worker runs inside the Linux VM circuitRF ships, under Rosetta,
# so the host's own architecture is not what to build for.
#
# TWO PRODUCTS ON WINDOWS, ONE ON LINUX -- the reason is in senior_worker.c's header. In short: a
# Windows model imports its host callbacks from a NAMED MODULE, and an executable's exports are
# never consulted for that; so the callbacks (and the worker state they write) live in a DLL, and
# the executable is a launcher that stages that DLL under whatever name the model asks for.
#
# Cross-compiling C needs a toolchain. Whichever of these is present is used, in order of how
# little they cost to run:
#
#   zig cc     seconds, no daemon, no image pull      -- the nicest if installed
#   docker     pulls a small gcc/mingw image the first time
#   podman     same
#   cc         only when already on a matching host
set -u

here=$(cd "$(dirname "$0")" && pwd)
out=$here/build
src=$here/senior_worker.c
def=$here/crf-model-host.def

mkdir -p "$out"

target=${1:-}
if [ -z "$target" ]; then
    case "$(uname -s 2>/dev/null || echo unknown)" in
        MINGW*|MSYS*|CYGWIN*) target=windows ;;
        *)                    target=linux ;;
    esac
fi

say() { echo "senior-worker: $*"; }

# No optimisation surprises: the worker is a transport, and the time goes inside the model library
# it calls.
CFLAGS='-O2 -std=gnu11 -Wall -Wextra -Wno-unused-parameter'

# ─────────────────────────────────────────────────────────────────── x86-64 Linux
if [ "$target" = linux ]; then
    bin=$out/senior_worker

    # -ldl for dlopen/dlsym/dlinfo, -lm for the model's maths.
    #
    # -rdynamic IS LOAD-BEARING, not tidiness. A compiled Linux model resolves its host services --
    # error reporting, matrix stamping -- back into whatever process loaded it, so those symbols
    # must be in this executable's DYNAMIC table. Without it dlopen fails outright with "undefined
    # symbol: send_error_to_scn", naming a function that is plainly right here in the file.
    LDFLAGS='-rdynamic'
    LDLIBS='-ldl -lm'

    if command -v zig >/dev/null 2>&1; then
        say "building with zig cc (x86_64-linux-gnu)"
        # shellcheck disable=SC2086
        exec zig cc -target x86_64-linux-gnu $CFLAGS $LDFLAGS "$src" -o "$bin" $LDLIBS
    fi

    for engine in docker podman; do
        command -v "$engine" >/dev/null 2>&1 || continue
        say "building with $engine (gcc:13 on linux/amd64)"
        exec "$engine" run --rm --platform linux/amd64 \
            -v "$here:/w" -w /w gcc:13 \
            sh -c "mkdir -p build && gcc $CFLAGS $LDFLAGS senior_worker.c -o build/senior_worker $LDLIBS"
    done

    if [ "$(uname -s)" = Linux ] && [ "$(uname -m)" = x86_64 ] && command -v cc >/dev/null 2>&1; then
        say "building with the host cc"
        # shellcheck disable=SC2086
        exec cc $CFLAGS $LDFLAGS "$src" -o "$bin" $LDLIBS
    fi

    say "no way to build for x86-64 Linux was found (looked for zig, docker, podman)."
    exit 1
fi

# ─────────────────────────────────────────────────────────────────── x86-64 Windows
if [ "$target" = windows ]; then
    dll=$out/crf-model-host.dll
    exe=$out/senior_worker.exe

    # ON THE CRT, measured rather than assumed. Such a library is typically built against UCRT
    # (api-ms-win-crt-*) while a stock mingw-w64 links msvcrt, and matching them would remove the
    # mismatch outright -- but the CRT is a property of the TOOLCHAIN, not a #define: adding
    # -D_UCRT by hand sends the headers down the UCRT path while the link still resolves against
    # msvcrt import libraries, and the build fails on __intrinsic_setjmpex. (Confirmed directly on
    # gcc-mingw-w64 13, not reasoned about.) So: to get UCRT, use a UCRT-targeting toolchain --
    # MSYS2's ucrt64 gcc -- and change nothing here.
    #
    # Running against msvcrt is not a hazard on its own, because nothing heap-allocated or
    # FILE*-shaped crosses the ABI boundary in either direction -- only const char* and double.
    # DO NOT PASS OWNERSHIP OF MEMORY ACROSS IT, and this stays true.
    WCFLAGS="$CFLAGS"
    STUBLIBS='-lshell32'

    if command -v zig >/dev/null 2>&1; then
        say "building with zig cc (x86_64-windows-gnu)"
        # shellcheck disable=SC2086
        zig cc -target x86_64-windows-gnu $WCFLAGS -DCRF_HOST_DLL -shared \
            "$src" "$def" -o "$dll" -lm || exit 1
        # shellcheck disable=SC2086
        zig cc -target x86_64-windows-gnu $WCFLAGS -DCRF_HOST_STUB \
            "$src" -o "$exe" $STUBLIBS || exit 1
        say "built $(basename "$dll") and $(basename "$exe")"
        exit 0
    fi

    for engine in docker podman; do
        command -v "$engine" >/dev/null 2>&1 || continue
        say "building with $engine (mingw-w64 on linux/amd64)"
        exec "$engine" run --rm --platform linux/amd64 -v "$here:/w" -w /w \
            docker.io/library/debian:bookworm-slim sh -c "
                set -e
                apt-get update -qq >/dev/null
                apt-get install -y -qq gcc-mingw-w64-x86-64 >/dev/null
                mkdir -p build
                x86_64-w64-mingw32-gcc $WCFLAGS -DCRF_HOST_DLL -shared \
                    senior_worker.c crf-model-host.def -o build/crf-model-host.dll -lm
                x86_64-w64-mingw32-gcc $WCFLAGS -DCRF_HOST_STUB \
                    senior_worker.c -o build/senior_worker.exe $STUBLIBS
            "
    done

    if command -v x86_64-w64-mingw32-gcc >/dev/null 2>&1; then
        say "building with the host mingw-w64 cross compiler"
        # shellcheck disable=SC2086
        x86_64-w64-mingw32-gcc $WCFLAGS -DCRF_HOST_DLL -shared "$src" "$def" -o "$dll" -lm || exit 1
        # shellcheck disable=SC2086
        x86_64-w64-mingw32-gcc $WCFLAGS -DCRF_HOST_STUB "$src" -o "$exe" $STUBLIBS || exit 1
        exit 0
    fi

    if command -v gcc >/dev/null 2>&1 && [ "${OS:-}" = "Windows_NT" ]; then
        say "building with the host gcc (MSYS2/MinGW)"
        # shellcheck disable=SC2086
        gcc $WCFLAGS -DCRF_HOST_DLL -shared "$src" "$def" -o "$dll" -lm || exit 1
        # shellcheck disable=SC2086
        gcc $WCFLAGS -DCRF_HOST_STUB "$src" -o "$exe" $STUBLIBS || exit 1
        exit 0
    fi

    say "no way to build for x86-64 Windows was found (looked for zig, docker, podman, mingw-w64)."
    exit 1
fi

say "unknown target '$target' (expected 'linux' or 'windows')."
exit 1
