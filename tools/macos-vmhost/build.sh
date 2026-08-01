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
out=${OUT:-"$here/build"}

case "$(uname -s)" in
  Darwin) ;;
  *) echo "crf-vmhost is macOS-only — nothing to build on $(uname -s)." >&2; exit 0 ;;
esac

echo "==> building ($config)"
swift build --package-path "$here" -c "$config"

binary=$(swift build --package-path "$here" -c "$config" --show-bin-path)/crf-vmhost

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
echo "    tools/macos-vmimage/build-image.sh --out $out"
