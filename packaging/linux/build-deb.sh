#!/bin/bash
set -euo pipefail

# ── circuitRF Debian package builder ──────────────────────────────────────────
#
#   packaging/linux/build-deb.sh x64      → dist/circuitRF-<version>-x64.deb    (Intel/AMD)
#   packaging/linux/build-deb.sh arm64    → dist/circuitRF-<version>-arm64.deb
#
# Installs a self-contained build to /opt/circuitrf/, puts `circuitrf` on PATH, and registers the
# icon, the menu entry and the file types so a double-click in the file manager opens the app.
#
# Requires: .NET 10 SDK and fpm (https://fpm.readthedocs.io):
#     sudo apt-get install ruby-dev build-essential
#     sudo gem install fpm
#
# fpm, not dotnet-deb: that tool targets .NET 9 and does not work with .NET 10.
# Cross-building is fine — a linux-arm64 package can be produced on an x64 machine.

ARCH="${1:-x64}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
HERE="${ROOT}/packaging/linux"

# One source for the version (repo-root VERSION); CRF_DEB_VERSION is dpkg's spelling of it.
source "${ROOT}/packaging/version.sh"

case "$ARCH" in
    x64)   RID="linux-x64";   DEB_ARCH="amd64" ;;
    arm64) RID="linux-arm64"; DEB_ARCH="arm64" ;;
    *) echo "Usage: $0 [x64|arm64]"; exit 1 ;;
esac

command -v fpm >/dev/null || { echo "❌ fpm not found — see the header of this script."; exit 1; }

echo "🎨 Building icons..."
dotnet run --project "${ROOT}/tools/IconGen" -- circuitrf

echo "📦 Publishing ${RID}..."
PUBLISH="${ROOT}/publish/${RID}"
rm -rf "$PUBLISH"
dotnet publish "${ROOT}/src/Ui/CircuitRF.Ui.csproj" -c Release -r "$RID" --self-contained true -o "$PUBLISH"

DIST="${ROOT}/dist"
DEB="${DIST}/circuitRF-${CRF_VERSION}-${ARCH}.deb"
mkdir -p "$DIST"
rm -f "$DEB"

echo "🗜  Building ${DEB}..."
fpm -s dir -t deb -n circuitrf -v "$CRF_DEB_VERSION" -a "$DEB_ARCH" \
    --description "Lightweight cross-platform RF circuit simulator and electromagnetic solver" \
    --url "https://github.com/potatobeanradio/circuitRF" \
    --license MIT \
    --maintainer "potatobeanradio" \
    --category "science" \
    --depends "libicu76 | libicu74 | libicu72 | libicu71 | libicu70 | libicu67" \
    --after-install "${HERE}/postinst" \
    --after-remove  "${HERE}/postrm" \
    -p "$DEB" \
    "${PUBLISH}/=/opt/circuitrf/" \
    "${HERE}/circuitrf.desktop=/usr/share/applications/circuitrf.desktop" \
    "${HERE}/circuitrf-mime.xml=/usr/share/mime/packages/circuitrf.xml" \
    "${HERE}/icons/circuitrf.png=/usr/share/icons/hicolor/512x512/apps/circuitrf.png"

echo ""
echo "✅ ${DEB}"
echo "   Install: sudo apt install ${DEB}     Remove: sudo apt remove circuitrf"
