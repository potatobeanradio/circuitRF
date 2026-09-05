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
# --arch <arm64|x86_64> and --os <darwin|linux>: build for a machine other than this one. The
# .csproj derives BOTH from the RuntimeIdentifier, so `dotnet publish -r osx-x64` and
# `-r linux-arm64` each get it right with nothing to remember.
#
#   macOS   Apple's clang targets both slices from either kind of Mac, so one run of
#           packaging/macos/build-macos.sh produces both disk images and an Intel Mac gets a real
#           x86_64 worker.
#   Linux   the host's own architecture is built by whatever cc is here; the OTHER one needs zig or
#           a container engine. A Linux target from a Mac needs zig either way.
#
# THE FLAGS WERE IGNORED OFF macOS UNTIL 2026-09, and that was not cosmetic. `dotnet publish -r
# linux-arm64` on an x86-64 box compiled an x86-64 worker, and `-r linux-x64` on a Mac compiled a
# Mach-O one; each landed in that RID's publish tree under the bare name the packaging step copies
# verbatim. Nothing reads an ELF header at run time — VerilogAFileResolver's architecture guard reads
# a PE header, so a POSIX worker is taken as-is by design — so the first symptom is an exec failure
# on a user's machine, for a file the build was certain it had produced correctly.
DEST=""
ARCH=""
TARGET_OS=""
while [ $# -gt 0 ]; do
  case "$1" in
    --dest) DEST="${2:-}"; shift 2 ;;
    --arch) ARCH="${2:-}"; shift 2 ;;
    --os)   TARGET_OS="${2:-}"; shift 2 ;;
    *) shift ;;
  esac
done

# Every spelling of the two architectures this worker is ever built for, reduced to one. Empty in,
# empty out, so "no --arch was given" stays distinguishable from "an --arch we do not recognise".
norm_arch() {
  case "${1:-}" in
    arm64|aarch64)     echo arm64  ;;
    x86_64|x64|amd64)  echo x86_64 ;;
    "")                echo ""     ;;
    *)                 echo "?"    ;;
  esac
}

norm_os() {
  case "${1:-}" in
    Darwin|darwin|osx|macos|mac)  echo darwin ;;
    Linux|linux)                  echo linux  ;;
    "")                           echo ""     ;;
    *)                            echo "?"    ;;
  esac
}

HOST_OS=$(norm_os "$(uname -s)")
HOST_ARCH=$(norm_arch "$(uname -m)")

ARCH=$(norm_arch "$ARCH")
TARGET_OS=$(norm_os "$TARGET_OS")

if [ "$ARCH" = "?" ]; then
  echo "osdi-worker: unsupported architecture - skipping."; exit 0
fi
if [ "$TARGET_OS" = "?" ]; then
  echo "osdi-worker: unsupported target OS - skipping."; exit 0
fi

[ -n "$TARGET_OS" ] || TARGET_OS="$HOST_OS"
[ -n "$ARCH" ]      || ARCH="$HOST_ARCH"

# Neither host nor target is one this script knows how to drive (a MinGW shell, say, where build.cmd
# is the right script). Saying so and succeeding is the contract.
if [ "$HOST_OS" = "?" ] || [ -z "$TARGET_OS" ] || [ -z "$ARCH" ]; then
  echo "osdi-worker: not a platform this script builds for - skipping."; exit 0
fi

ARCHFLAGS=""
LDLIBS=""
ZIG_TARGET=""
DOCKER_PLATFORM=""
OUT_DIR="."

case "$TARGET_OS" in
  darwin)
    ARCHFLAGS="-arch $ARCH"
    # Per-architecture, so two binaries of the same name that differ only in a way no `ls` shows can
    # never be mistaken for each other. The host build is ALSO copied to ./osdi-worker at the end,
    # because that flat path is what tools/osdi-worker/verify.py and the README already name.
    OUT_DIR="build/$ARCH"
    ;;
  linux)
    # -ldl for dlopen/dlsym (folded into libc on glibc 2.34+, still needed on anything older) and
    # -lm because a compiled model's own maths resolves through the process that loaded it. Neither
    # flag exists on macOS, which is why this is set per target rather than once.
    LDLIBS="-ldl -lm"
    case "$ARCH" in
      arm64)  ZIG_TARGET="aarch64-linux-gnu"; DOCKER_PLATFORM="linux/arm64" ;;
      x86_64) ZIG_TARGET="x86_64-linux-gnu";  DOCKER_PLATFORM="linux/amd64" ;;
    esac
    # OS-qualified, unlike macOS's: this directory can hold a Mach-O and an ELF of the same
    # architecture at once on a Mac that has published for both.
    OUT_DIR="build/linux-$ARCH"
    ;;
esac

mkdir -p "$OUT_DIR"
BIN="$OUT_DIR/osdi-worker"

CC_BIN="${CC:-cc}"
# A zig that is not on PATH can be named outright — an unpacked tarball, or one installed into a
# shell that had already started. Without this, "installed but not on PATH" is indistinguishable
# from "not installed", and the message the user gets is one they know to be false.
ZIG="${CRF_ZIG:-zig}"
CFLAGS="-O2 -Wall -Wextra"
LOG="${TMPDIR:-/tmp}/crf-osdi-worker-build.log"

have() { command -v "$1" >/dev/null 2>&1; }

# Can the compiler sitting on this machine produce the requested binary? On macOS clang targets both
# slices, so any Mac can build for any Mac. Everywhere else one compiler means one target, and the
# host's own OS and architecture are the only thing it can be trusted to emit.
NATIVE_CC=0
if [ "$TARGET_OS" = "$HOST_OS" ]; then
  case "$TARGET_OS" in
    darwin) NATIVE_CC=1 ;;
    *)      [ "$ARCH" = "$HOST_ARCH" ] && NATIVE_CC=1 ;;
  esac
fi

echo "osdi-worker: building worker ($TARGET_OS/$ARCH)"
BUILT_WITH=""
ATTEMPTED=0

# ROUTES, in the order of what they cost to run: the host compiler needs nothing installed, zig is
# one download and the only thing here that cross-compiles freely, a container engine pulls an image
# the first time.
if [ "$NATIVE_CC" = 1 ] && have "$CC_BIN"; then
  ATTEMPTED=1
  # shellcheck disable=SC2086
  if "$CC_BIN" $CFLAGS $ARCHFLAGS -o "$BIN" osdi_worker.c $LDLIBS >"$LOG" 2>&1; then
    BUILT_WITH="$CC_BIN"
  fi
fi

if [ -z "$BUILT_WITH" ] && [ -n "$ZIG_TARGET" ] && have "$ZIG"; then
  ATTEMPTED=1
  # shellcheck disable=SC2086
  if "$ZIG" cc -target "$ZIG_TARGET" $CFLAGS -o "$BIN" osdi_worker.c $LDLIBS >"$LOG" 2>&1; then
    BUILT_WITH="zig cc -target $ZIG_TARGET"
  fi
fi

if [ -z "$BUILT_WITH" ] && [ -n "$DOCKER_PLATFORM" ]; then
  for engine in docker podman; do
    have "$engine" || continue
    ATTEMPTED=1
    # An arm64 image on an x86-64 host (or the reverse) needs binfmt/qemu registered. When it is
    # not, this fails like any other route and the next message covers it.
    if "$engine" run --rm --platform "$DOCKER_PLATFORM" \
         -v "$PWD:/w" -w /w gcc:13 \
         sh -c "mkdir -p '$OUT_DIR' && gcc $CFLAGS -o '$BIN' osdi_worker.c $LDLIBS" >"$LOG" 2>&1; then
      BUILT_WITH="$engine (gcc:13 on $DOCKER_PLATFORM)"
      break
    fi
  done
fi

if [ -z "$BUILT_WITH" ]; then
  if [ "$ATTEMPTED" = 0 ]; then
    # NOT A FAILURE. Nothing here can target that machine, and the build must not stop over it.
    echo "osdi-worker: no toolchain here can build for $TARGET_OS/$ARCH - skipping."
    if [ "$NATIVE_CC" = 1 ]; then
      echo "             Install a C compiler ('$CC_BIN' was not found), or zig."
    else
      echo "             Cross-building for $TARGET_OS/$ARCH needs zig (or docker/podman)."
      echo "             Name one outright with CRF_ZIG=/path/to/zig if it is installed but not on PATH."
    fi
    echo "             The application still builds; OSDI-backed devices will report an unavailable"
    echo "             provider until this is built."
    exit 0
  fi
  echo "osdi-worker: worker build FAILED; the compiler's own output is here:"
  echo "osdi-worker:   $LOG"
  exit 1
fi

echo "osdi-worker: built $BIN with $BUILT_WITH"

# The flat path the rest of the repo already names, for this machine's own OS and architecture only.
# A cross-built binary must NOT land there: verify.py would then run a program this machine cannot
# execute, and say nothing useful about why.
if [ "$OUT_DIR" != "." ] && [ "$TARGET_OS" = "$HOST_OS" ] && [ "$ARCH" = "$HOST_ARCH" ]; then
  cp -f "$BIN" ./osdi-worker
fi

# The test model is a shared library in whatever form this platform loads. The extension is
# cosmetic — the worker dlopen()s whatever path it is given and never inspects the name.
MODEL_DIR="../fake-osdi-model"
if [ -f "$MODEL_DIR/fake_osdi.c" ] && have "$CC_BIN"; then
  echo "osdi-worker: building test-only model (not shipped)"
  # The host's own OS and architecture, always: this model exists to be dlopen()ed by a worker
  # running HERE, in a test. A cross-built one could not be loaded by anything on this machine.
  # shellcheck disable=SC2086
  "$CC_BIN" -shared -fPIC $CFLAGS -o "$MODEL_DIR/fake_osdi.osdi" "$MODEL_DIR/fake_osdi.c" || {
    echo "osdi-worker: test model build FAILED"; exit 1; }
fi

if [ -n "$DEST" ] && [ -d "$DEST" ]; then
  cp -f "$BIN" "$DEST/" 2>/dev/null && echo "osdi-worker: copied to $DEST"
fi

echo "osdi-worker: ok"
