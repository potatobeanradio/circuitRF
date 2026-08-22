#!/bin/sh
# Keeps the macOS VM host up to date as part of an ordinary `dotnet build` / `dotnet run`.
#
# THIS SCRIPT MUST NEVER FAIL A BUILD.
# ------------------------------------
# The VM host is what lets Linux-only device models run on macOS. It is optional: circuitRF builds,
# runs, and does everything else without it. So every failure here is reported and swallowed — a
# missing Swift toolchain, no network, a checksum mismatch — and the exit status stays 0 unless
# --strict is passed (which CI and release builds use, where silence would be wrong).
#
# THE 330 MB PROBLEM, and the split it forces
# -------------------------------------------
# Two artifacts, with very different costs:
#
#   crf-vmhost         a few seconds, no network        -> always built when stale
#   the Linux image    ~330 MB of downloads             -> only when already cached, or asked for
#
# Downloading a third of a gigabyte because somebody typed `dotnet run` is not something to do
# quietly. So the image is built automatically only when its downloads are ALREADY cached (the
# common case after the first time, and on any machine where a colleague primed the cache); when
# they are not, this prints the one command to run and carries on. Pass --with-image, or set
# CRF_BUILD_VM_IMAGE=1, to allow the download.
set -u

here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
image_dir=$repo/tools/macos-vmimage

say()  { echo "crf-vm: $*"; }
skip() { say "$*"; [ "$strict" = 1 ] && exit 1; exit 0; }

dest=""
strict=0
with_image=${CRF_BUILD_VM_IMAGE:-0}

# --arch names the Mac these artifacts are FOR, defaulting to the one running this. It is what makes
# `dotnet publish -r osx-x64` on an Apple Silicon machine produce an x86-64 VM host and an x86-64
# guest image rather than this machine's — the .csproj derives it from the RuntimeIdentifier and
# passes it straight through. Everything below is per-architecture because of it, including the
# build directory: two crf-vmhosts of the same name differing only in a way no `ls` shows is exactly
# what ships wrong once and is never noticed.
arch=""

while [ $# -gt 0 ]; do
    case "$1" in
        --dest)       dest=$2; shift 2 ;;
        --arch)       arch=$2; shift 2 ;;
        --strict)     strict=1; shift ;;
        --with-image) with_image=1; shift ;;
        *) shift ;;
    esac
done

[ -n "$arch" ] || arch=$(uname -m)
case "$arch" in
    arm64|aarch64) arch=arm64;  guest_arch=aarch64 ;;
    x86_64|x64)    arch=x86_64; guest_arch=x86_64  ;;
    *) skip "unsupported architecture '$arch'; skipping the macOS VM host" ;;
esac

[ "$(uname -s)" = Darwin ] || exit 0

build_dir=$here/build/$arch

# ── Is anything to do? ────────────────────────────────────────────────────────
# "Newer than the output" is the whole staleness rule. Cheap, and it means editing guest-init or the
# Swift source is enough to get a rebuild without anyone remembering to ask for one.
newer_than() {
    target=$1; shift
    [ -f "$target" ] || return 0
    for src in "$@"; do
        [ -e "$src" ] && [ "$src" -nt "$target" ] && return 0
    done
    return 1
}

host_binary=$build_dir/crf-vmhost
kernel=$build_dir/crf-linux-kernel
initramfs=$build_dir/crf-linux-initramfs.cpio.gz

# ── The host binary ───────────────────────────────────────────────────────────
if newer_than "$host_binary" "$here/Sources/crf-vmhost/main.swift" "$here/Package.swift" \
                             "$here/crf-vmhost.entitlements"; then
    command -v swift >/dev/null 2>&1 \
        || skip "no Swift toolchain found; skipping the macOS VM host (install Xcode command line tools to enable Linux device models)"

    say "building crf-vmhost"
    if ! "$here/build.sh" --arch "$arch" >/tmp/crf-vmhost-build.log 2>&1; then
        say "crf-vmhost did not build; see /tmp/crf-vmhost-build.log"
        say "circuitRF will run normally, but Linux-only device models will not be available."
        [ "$strict" = 1 ] && exit 1
        exit 0
    fi
fi

# ── The Linux image ───────────────────────────────────────────────────────────
if newer_than "$initramfs" "$image_dir/guest-init" "$image_dir/build-image.sh" \
                           "$image_dir/sources.lock"; then

    # Cached means every pinned download is already on disk, so rebuilding costs seconds of unpack
    # rather than a third of a gigabyte of network.
    # Only the downloads THIS architecture actually needs. Checking every URL in sources.lock would
    # count the other architecture's Alpine tarball — which is never fetched here — and so would
    # report "not cached" forever on a machine that has everything it needs.
    #
    # The Ubuntu glibc runtime is on BOTH lists, because the guest is musl on both and everything
    # circuitRF runs in it is glibc-linked. Listing it for arm64 only made an Intel machine look
    # cached when it was one download short.
    . "$image_dir/sources.lock"
    case $guest_arch in
        aarch64) needed="$ALPINE_NETBOOT_AARCH64_URL $UBUNTU_BASE_AMD64_URL" ;;
        x86_64)  needed="$ALPINE_NETBOOT_X86_64_URL $UBUNTU_BASE_AMD64_URL" ;;
        *)       needed="" ;;
    esac

    cached=1
    for url in $needed; do
        [ -n "$url" ] && [ -f "$image_dir/.work/$(basename "$url")" ] || cached=0
    done

    if [ "$cached" = 0 ] && [ "$with_image" != 1 ]; then
        say "the Linux VM image is missing or out of date, and building it downloads ~330 MB."
        say "circuitRF will run normally; Linux-only device models need this image."
        say "to build it:  tools/macos-vmimage/build-image.sh --arch $guest_arch --out $build_dir"
        exit 0
    fi

    say "building the Linux VM image"
    if ! "$image_dir/build-image.sh" --arch "$guest_arch" --out "$build_dir" \
            >/tmp/crf-vmimage-build.log 2>&1; then
        say "the Linux VM image did not build; see /tmp/crf-vmimage-build.log"
        say "circuitRF will run normally, but Linux-only device models will not be available."
        [ "$strict" = 1 ] && exit 1
        exit 0
    fi
fi

# ── Publish beside the app ────────────────────────────────────────────────────
# All three land in the same directory because crf-vmhost looks for its kernel and initramfs beside
# itself, and circuitRF looks for crf-vmhost beside its own assemblies.
if [ -n "$dest" ] && [ -f "$host_binary" ]; then
    mkdir -p "$dest"
    for artifact in "$host_binary" "$kernel" "$initramfs"; do
        [ -f "$artifact" ] && cp -p "$artifact" "$dest/" 2>/dev/null
    done
fi

exit 0
