#!/usr/bin/env bash
# Brief em3d-27 (F2 spike), route B: fetch wgpu-native for THIS machine into native/<rid>/.
# Never committed (native/ is git-ignored). wgpu-native is MIT OR Apache-2.0.
set -euo pipefail
VER=v29.0.1.1
cd "$(dirname "$0")"
case "$(uname -s)-$(uname -m)" in
  Darwin-arm64)  Z=macos-aarch64;  RID=osx-arm64 ;;
  Darwin-x86_64) Z=macos-x86_64;   RID=osx-x64 ;;
  Linux-x86_64)  Z=linux-x86_64;   RID=linux-x64 ;;
  Linux-aarch64) Z=linux-aarch64;  RID=linux-arm64 ;;
  *) echo "unsupported host $(uname -s)-$(uname -m)" >&2; exit 1 ;;
esac
mkdir -p "native/$RID"
curl -fsSL -o /tmp/wgpu-$$.zip "https://github.com/gfx-rs/wgpu-native/releases/download/$VER/wgpu-$Z-release.zip"
unzip -o -q /tmp/wgpu-$$.zip -d "native/$RID"
rm -f /tmp/wgpu-$$.zip native/$RID/lib/*.a
ls -l native/$RID/lib
