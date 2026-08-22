#!/bin/bash
set -euo pipefail

# ── circuitRF macOS .dmg builder ──────────────────────────────────────────────
#
#   packaging/macos/build-dmg.sh                 → dist/circuitRF-<version>-arm64.dmg
#   packaging/macos/build-dmg.sh harmonica       → dist/harmonicaRF-<version>-arm64.dmg
#   packaging/macos/build-dmg.sh wbond           → dist/wBond-<version>-arm64.dmg
#
# Apple Silicon (osx-arm64) only — that is what circuitRF ships. For an Intel build, change RID in
# the matching src/Ui/bundleFor*MacOS.sh.
#
# The .app itself is built by the bundle scripts that already live in src/Ui/ — this adds the two
# things a distributable disk image needs on top of one: the icon (rasterised from the committed
# SVG, since no icon binary is tracked) and the .dmg with its /Applications drop target.
#
# Requires: .NET 10 SDK. Everything else (hdiutil, codesign) ships with macOS.

APP="${1:-circuitrf}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

case "$APP" in
    circuitrf) NAME="circuitRF";   BUNDLE_SCRIPT="bundleForMacOS.sh" ;;
    harmonica) NAME="harmonicaRF"; BUNDLE_SCRIPT="bundleForHarmonicaMacOS.sh" ;;
    wbond)     NAME="wBond";       BUNDLE_SCRIPT="bundleForWBondMacOS.sh" ;;
    *) echo "Usage: $0 [circuitrf|harmonica|wbond]"; exit 1 ;;
esac

# One source for the version — the repo-root VERSION file. The bundle script stamps the same value
# into the .app's Info.plist, so the disk image can never be named a version the installed app does
# not report.
source "${ROOT}/packaging/version.sh"
VERSION="$CRF_VERSION"

echo "🎨 Building icons..."
dotnet run --project "${ROOT}/tools/IconGen" -- "$APP"

# THE VM IMAGE IS BUILT HERE EVEN THOUGH A PLAIN BUILD LEAVES IT ALONE. Compiled device models are
# Linux libraries — nothing on macOS can load one — so circuitRF runs the worker inside the small
# Linux VM it ships, and the kernel and initramfs are part of that. Building them from scratch pulls
# ~330 MB, which is why an ordinary `dotnet build` prints the command instead of doing it silently.
# Packaging is the one moment that download is exactly what the operator asked for.
export CrfBuildVmImage=true

echo "📦 Building ${NAME}.app..."
cd "${ROOT}/src/Ui"
bash "./${BUNDLE_SCRIPT}"

APP_BUNDLE="${ROOT}/src/Ui/bin/Release/net10.0/osx-arm64/${NAME}.app"
[ -d "$APP_BUNDLE" ] || { echo "❌ ${APP_BUNDLE} was not produced."; exit 1; }

# ── What the app needs beside its assemblies ──────────────────────────────────
#
# The .csproj builds these and its CrfPublishHelperPrograms target publishes them, but both build
# steps are warn-only BY DESIGN: nobody should be unable to build circuitRF for want of a Swift
# toolchain or a C compiler. That is right for a build and wrong for a RELEASE — a disk image
# missing them installs an application that reads a kit, describes it correctly, and then refuses at
# Run naming programs the user never installed and had no way to install.
#
# All four or none, on this platform: the worker here is a LINUX build (that is what the models
# are), so without the VM host, its kernel and its initramfs there is nothing that can run it.
#
# Set CRF_ALLOW_NO_DEVICE_WORKER=1 to package without them on purpose.
NEEDED="senior_worker crf-vmhost crf-linux-kernel crf-linux-initramfs.cpio.gz"
MISSING=""
for f in $NEEDED; do
    [ -f "${APP_BUNDLE}/Contents/MacOS/${f}" ] || MISSING="${MISSING} ${f}"
done

if [ -n "$MISSING" ]; then
    if [ "${CRF_ALLOW_NO_DEVICE_WORKER:-}" = 1 ]; then
        echo "⚠️  Packaging without:${MISSING}. Compiled device models will not run."
    else
        echo "❌ Missing from ${NAME}.app:${MISSING}"
        echo ""
        echo "   These are built during \`dotnet build\`, which only WARNS when it cannot:"
        echo ""
        echo "       senior_worker                 needs zig, docker/podman, or a cross-compiler"
        echo "       crf-vmhost + kernel/initramfs  need Xcode's Swift toolchain and a network"
        echo ""
        echo "   Build them by hand to see why:"
        echo "       tools/senior-worker/ensure-built.sh"
        echo "       tools/macos-vmhost/ensure-built.sh --with-image"
        echo ""
        echo "   To package deliberately without them: CRF_ALLOW_NO_DEVICE_WORKER=1 $0 $APP"
        exit 1
    fi
fi

DIST="${ROOT}/dist"
DMG="${DIST}/${NAME}-${VERSION}-arm64.dmg"
STAGE="$(mktemp -d)/${NAME}"
mkdir -p "$DIST" "$STAGE"

echo "💿 Staging disk image..."
cp -R "$APP_BUNDLE" "$STAGE/"
ln -s /Applications "${STAGE}/Applications"     # the drag-to-install target users expect

rm -f "$DMG"
hdiutil create -volname "$NAME" -srcfolder "$STAGE" -ov -format UDZO -quiet "$DMG"
rm -rf "$(dirname "$STAGE")"

echo ""
echo "✅ ${DMG}"
echo "   Ad-hoc signed: a first launch needs right-click → Open, or"
echo "   xattr -dr com.apple.quarantine /Applications/${NAME}.app"
echo "   For public distribution, sign with a Developer ID certificate and notarise — see BUILDING.md."
