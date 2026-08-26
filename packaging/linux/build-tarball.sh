#!/bin/bash
set -euo pipefail

# ── circuitRF user-local Linux channel ────────────────────────────────────────
#
#   packaging/linux/build-tarball.sh x64     → dist/circuitRF-<version>-linux-x64.tar.gz
#   packaging/linux/build-tarball.sh arm64   → dist/circuitRF-<version>-linux-arm64.tar.gz
#
# WHAT THIS IS FOR, and why it exists beside build-deb.sh rather than replacing it.
#
# The .deb installs to /opt/circuitrf, which only root can write. That is the right shape for a
# machine-wide, centrally-managed install and it is NOTIFY-ONLY: circuitRF checks for updates, posts
# one Message Panel line with a link, and writes nothing. A silent background update requires an
# install location the running USER can write (docs/design/auto-update.md §1), so the channel that
# actually auto-updates is this one — ~/.local/share/circuitRF, needing no root at any point.
#
# The layout install.sh lays down is versioned directories behind a stable launch path:
#
#     ~/.local/share/circuitRF/
#         current -> app-<version>        symlink, re-pointed atomically via rename(2)
#         app-<version>/                  the publish tree
#         staging/                        the updater's partial downloads; never executed from
#     ~/.local/bin/circuitrf              -> ../share/circuitRF/current/circuitRF
#     ~/.local/share/applications/circuitrf.desktop
#
# so an update re-registers nothing: the launcher and the .desktop entry both point at the stable
# `current/` path, and the update is a symlink flip.
#
# THE TARBALL'S NAME IS A CONTRACT. src/Ui/Updates/UpdateAssetNames.cs parses exactly this spelling
# and tests/Ui.Tests/PackagingScriptTests.cs asserts it. Rename it and updates stop — with no error
# anywhere and no user report, because a user who is not being offered an update has nothing to
# notice.
#
# Requires: .NET 10 SDK. No fpm, no root, and it cross-builds like build-deb.sh does.

ARCH="${1:-x64}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
HERE="${ROOT}/packaging/linux"

# One source for the version (repo-root VERSION). The tarball carries CRF_VERSION verbatim — NOT
# dpkg's ~ spelling, which is the .deb's alone.
source "${ROOT}/packaging/version.sh"

case "$ARCH" in
    x64)   RID="linux-x64"   ;;
    arm64) RID="linux-arm64" ;;
    *) echo "Usage: $0 [x64|arm64]"; exit 1 ;;
esac

echo "Building icons..."
dotnet run --project "${ROOT}/tools/IconGen" -- circuitrf

PUBLISH="${ROOT}/publish/${RID}"
echo "Publishing ${RID}..."
rm -rf "$PUBLISH"
dotnet publish "${ROOT}/src/Ui/CircuitRF.Ui.csproj" -c Release -r "$RID" --self-contained true -o "$PUBLISH"

# The device worker, checked exactly as build-deb.sh checks it: an archive missing it installs an
# application that reads a kit, describes it correctly, then refuses at Run naming a program the
# user never installed. Set CRF_ALLOW_NO_DEVICE_WORKER=1 to build without it on purpose.
if [ ! -f "${PUBLISH}/senior_worker" ] && [ "${CRF_ALLOW_NO_DEVICE_WORKER:-}" != "1" ]; then
    echo "The device worker (senior_worker) is missing from the publish tree."
    echo "Install zig, or a C toolchain, and publish again — or set CRF_ALLOW_NO_DEVICE_WORKER=1."
    exit 1
fi

# ── Stage ─────────────────────────────────────────────────────────────────────
#
# app-<version>/ inside the archive, so the tarball's own shape IS the installed shape and
# install.sh only has to move it and re-point one symlink.

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

APPDIR="${STAGE}/circuitRF-${CRF_VERSION}/app-${CRF_VERSION}"
mkdir -p "$APPDIR"
cp -a "${PUBLISH}/." "$APPDIR/"
chmod +x "${APPDIR}/circuitRF" 2>/dev/null || true
[ -f "${APPDIR}/senior_worker" ] && chmod +x "${APPDIR}/senior_worker"

cp "${HERE}/install.sh"        "${STAGE}/circuitRF-${CRF_VERSION}/install.sh"
cp "${HERE}/circuitrf-mime.xml" "${STAGE}/circuitRF-${CRF_VERSION}/circuitrf-mime.xml"
cp "${HERE}/icons/circuitrf.png" "${STAGE}/circuitRF-${CRF_VERSION}/circuitrf.png"
chmod +x "${STAGE}/circuitRF-${CRF_VERSION}/install.sh"

# The version the installer lays into `current`, so install.sh never has to parse a directory name.
echo -n "app-${CRF_VERSION}" > "${STAGE}/circuitRF-${CRF_VERSION}/current"

mkdir -p "${ROOT}/dist"
OUT="${ROOT}/dist/circuitRF-${CRF_VERSION}-linux-${ARCH}.tar.gz"
rm -f "$OUT"

echo "Packing ${OUT}..."
tar -C "$STAGE" -czf "$OUT" "circuitRF-${CRF_VERSION}"

echo ""
echo "OK  ${OUT}"
echo "    Install with:  tar xzf $(basename "$OUT") && ./circuitRF-${CRF_VERSION}/install.sh"
echo "    This archive is ALSO the update payload; its name is parsed by UpdateAssetNames.cs."
