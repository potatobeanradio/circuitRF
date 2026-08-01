#!/bin/sh
# Builds the test-only fake model library.
#
#   build.sh            the host's own natural target
#   build.sh linux      fake_model.so    (x86-64 Linux)
#   build.sh windows    fake_model.dll   (x86-64 Windows)
#
# NOT part of the application. It is a FIXTURE: something for the device worker to load in a test
# run, on a machine that has no vendor kit on it. See README.md for what it serves and why it
# deliberately references nothing else in this repository.
#
# It is not built by `dotnet build` — a fixture that fails to compile must never be able to fail an
# application build. Run it by hand, or from a CI step that wants worker coverage.
set -u

here=$(cd "$(dirname "$0")" && pwd)
out=$here/build
src=$here/fake_model.c
def=$here/crf_test_host.def

mkdir -p "$out"

target=${1:-}
if [ -z "$target" ]; then
    case "$(uname -s 2>/dev/null || echo unknown)" in
        MINGW*|MSYS*|CYGWIN*) target=windows ;;
        *)                    target=linux ;;
    esac
fi

say()   { echo "fake-model-lib: $*"; }
CFLAGS='-O1 -std=gnu11 -Wall -Wextra -fPIC'

if [ "$target" = linux ]; then
    # No -l for the callbacks: on Linux they stay UNDEFINED and resolve against whatever process
    # loads this — which is the whole reason the Linux worker needs -rdynamic. -Wl,--allow-shlib-undefined
    # is not needed; undefined symbols in a shared object are the default and the point.
    if command -v zig >/dev/null 2>&1; then
        say "building with zig cc (x86_64-linux-gnu)"
        # shellcheck disable=SC2086
        exec zig cc -target x86_64-linux-gnu $CFLAGS -shared "$src" -o "$out/fake_model.so"
    fi
    for engine in docker podman; do
        command -v "$engine" >/dev/null 2>&1 || continue
        say "building with $engine (gcc:13 on linux/amd64)"
        exec "$engine" run --rm --platform linux/amd64 -v "$here:/w" -w /w gcc:13 \
            sh -c "mkdir -p build && gcc $CFLAGS -shared fake_model.c -o build/fake_model.so"
    done
    if [ "$(uname -s)" = Linux ] && [ "$(uname -m)" = x86_64 ] && command -v cc >/dev/null 2>&1; then
        say "building with the host cc"
        # shellcheck disable=SC2086
        exec cc $CFLAGS -shared "$src" -o "$out/fake_model.so"
    fi
    say "no way to build for x86-64 Linux was found (looked for zig, docker, podman)."
    exit 1
fi

if [ "$target" = windows ]; then
    # Two steps, and the first is the point of the fixture: build an IMPORT LIBRARY for a module
    # that does not exist, so the resulting DLL genuinely imports its callbacks BY NAME FROM A NAMED
    # MODULE. Supplying that module at load time is the worker's job, and this is what tests it.
    for engine in docker podman; do
        command -v "$engine" >/dev/null 2>&1 || continue
        say "building with $engine (mingw-w64 on linux/amd64)"
        exec "$engine" run --rm --platform linux/amd64 -v "$here:/w" -w /w \
            docker.io/library/debian:bookworm-slim sh -c '
                set -e
                apt-get update -qq >/dev/null
                apt-get install -y -qq gcc-mingw-w64-x86-64 binutils-mingw-w64-x86-64 >/dev/null
                mkdir -p build
                x86_64-w64-mingw32-dlltool -d crf_test_host.def \
                    -D crf_test_host.dll -l build/libcrf_test_host.a
                x86_64-w64-mingw32-gcc -O1 -std=gnu11 -Wall -Wextra -shared \
                    fake_model.c -o build/fake_model.dll -Lbuild -lcrf_test_host
            '
    done

    if command -v x86_64-w64-mingw32-gcc >/dev/null 2>&1; then
        say "building with the host mingw-w64 cross compiler"
        x86_64-w64-mingw32-dlltool -d "$def" -D crf_test_host.dll -l "$out/libcrf_test_host.a" || exit 1
        # shellcheck disable=SC2086
        x86_64-w64-mingw32-gcc $CFLAGS -shared "$src" -o "$out/fake_model.dll" \
            -L"$out" -lcrf_test_host || exit 1
        exit 0
    fi

    if command -v dlltool >/dev/null 2>&1 && command -v gcc >/dev/null 2>&1 && [ "${OS:-}" = "Windows_NT" ]; then
        say "building with the host gcc (MSYS2/MinGW)"
        dlltool -d "$def" -D crf_test_host.dll -l "$out/libcrf_test_host.a" || exit 1
        # shellcheck disable=SC2086
        gcc $CFLAGS -shared "$src" -o "$out/fake_model.dll" -L"$out" -lcrf_test_host || exit 1
        exit 0
    fi

    say "no way to build for x86-64 Windows was found (looked for docker, podman, mingw-w64)."
    exit 1
fi

say "unknown target '$target' (expected 'linux' or 'windows')."
exit 1
