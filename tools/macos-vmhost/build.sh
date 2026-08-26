#!/bin/sh
# Builds crf-vmhost and signs it with the entitlement that lets it create a virtual machine.
#
# The signing step is not optional and not a release-only concern: an unsigned binary is killed the
# moment it touches Virtualization.framework, so a developer build that skipped it would look like a
# code bug rather than a missing entitlement. Ad-hoc signing ("-") is enough for local use; a
# release build passes a real identity in via IDENTITY.
set -eu

here=$(cd "$(dirname "$0")" && pwd)
config=${CONFIG:-release}
identity=${IDENTITY:--}

# --arch names the Mac this binary is FOR: arm64 or x86_64, defaulting to the one it is built on.
#
# Cross-building is genuinely supported here, not a hopeful flag: Apple's toolchain targets both
# slices from either kind of Mac, and Virtualization.framework is in the SDK for both. The Rosetta
# block in main.swift is behind `#if arch(arm64)`, which is a TARGET test — so an x86_64 build
# correctly contains no Rosetta code at all (verified with `nm -u`, not assumed). That is what lets
# one run of packaging/macos/build-macos.sh produce both disk images.
#
# Each architecture gets its own build/ subdirectory. Two binaries of the same name that differ only
# in a way no `ls` shows is exactly the sort of thing that ships wrong once and is never noticed.
arch=""
while [ $# -gt 0 ]; do
    case "$1" in
        --arch) arch=$2; shift 2 ;;
        --out)  out_override=$2; shift 2 ;;
        *) shift ;;
    esac
done

[ -n "$arch" ] || arch=$(uname -m)
case "$arch" in
  arm64|aarch64) arch=arm64  ;;
  x86_64|x64)    arch=x86_64 ;;
  *) echo "unsupported architecture: $arch" >&2; exit 2 ;;
esac

out=${out_override:-${OUT:-"$here/build/$arch"}}

case "$(uname -s)" in
  Darwin) ;;
  *) echo "crf-vmhost is macOS-only — nothing to build on $(uname -s)." >&2; exit 0 ;;
esac

echo "==> building ($config, $arch)"
swift build --package-path "$here" -c "$config" --arch "$arch"

binary=$(swift build --package-path "$here" -c "$config" --arch "$arch" --show-bin-path)/crf-vmhost

echo "==> signing with entitlements (identity: $identity)"
codesign --force --sign "$identity" \
         --entitlements "$here/crf-vmhost.entitlements" \
         --options runtime \
         "$binary"

mkdir -p "$out"
cp "$binary" "$out/crf-vmhost"

echo "==> $out/crf-vmhost"
echo
echo "It needs a kernel and initramfs beside it. Build those with:"
echo "    tools/macos-vmimage/build-image.sh --arch $arch --out $out"
