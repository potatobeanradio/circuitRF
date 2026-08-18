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

echo "📦 Building ${NAME}.app..."
cd "${ROOT}/src/Ui"
bash "./${BUNDLE_SCRIPT}"

APP_BUNDLE="${ROOT}/src/Ui/bin/Release/net10.0/osx-arm64/${NAME}.app"
[ -d "$APP_BUNDLE" ] || { echo "❌ ${APP_BUNDLE} was not produced."; exit 1; }

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
