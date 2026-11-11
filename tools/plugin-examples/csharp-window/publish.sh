#!/usr/bin/env bash
# Publishes this plugin with NativeAOT into bin/<rid>/, one native program per runtime identifier.
#
#   ./publish.sh                 every RID this machine can build (this operating system's)
#   ./publish.sh osx-arm64       one RID
#
# NativeAOT compiles to machine code with the platform's own native toolchain (Xcode's command-line tools on macOS,
# clang and the zlib headers on Linux), so it cannot cross operating systems: a macOS machine builds the osx-* RIDs and
# nothing else. Asked for a RID it cannot build, this script STOPS and says so - a plugin.json that names a RID with
# no program behind it fails for whoever installs it, not for you.
set -euo pipefail
cd "$(dirname "$0")"

case "$(uname -s)" in
  Darwin) os=osx;   native=(osx-arm64 osx-x64) ;;
  Linux)  os=linux; native=(linux-x64 linux-arm64) ;;
  *) echo "publish.sh runs on macOS or Linux; on Windows run publish.ps1." >&2; exit 2 ;;
esac

rids=("$@")
[ ${#rids[@]} -eq 0 ] && rids=("${native[@]}")

for rid in "${rids[@]}"; do
  if [[ "$rid" != "$os"-* ]]; then
    echo "Cannot publish $rid here: NativeAOT builds $os-* RIDs on this machine only. Publish $rid on its own operating system." >&2
    exit 2
  fi
done

for rid in "${rids[@]}"; do
  echo "Publishing $rid ..."
  dotnet publish LineSection.csproj -c Release -r "$rid" -p:PublishAot=true -o "bin/$rid"
done
echo "Done. Ship plugin.json with the bin/ folder; each bin/<rid>/ holds the program and the native libraries beside it."
