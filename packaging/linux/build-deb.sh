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

# ── The device worker ─────────────────────────────────────────────────────────
#
# The program that evaluates a kit's compiled device models. The .csproj builds it (via
# tools/senior-worker/ensure-built.sh) and publishes it, but that build step is warn-only BY DESIGN
# — nobody should be unable to build circuitRF for want of a C compiler.
#
# That is right for a build and wrong for a RELEASE: a package missing it installs an application
# that reads a kit, describes it correctly, and then refuses at Run naming a program the user never
# installed and had no way to install. So packaging checks what building only warns about.
#
# Set CRF_ALLOW_NO_DEVICE_WORKER=1 to package without it on purpose.
WORKER="${PUBLISH}/senior_worker"

if [ -f "$WORKER" ]; then
    # ARCHITECTURE, from the ELF header rather than from where the file came from: bytes 18-19,
    # little-endian, 0x3E = x86-64 and 0xB7 = aarch64. A worker of the wrong architecture is not a
    # lesser version of a working one — it starts and then cannot load a single model — so it is
    # dropped rather than shipped, and the run-time message about a missing worker is the accurate
    # one to leave the user with.
    machine="$(od -An -tx1 -j18 -N2 "$WORKER" | tr -d ' ')"
    case "${RID}:${machine}" in
        linux-x64:3e00|linux-arm64:b700) ;;
        *) echo "⚠️  The device worker in the publish tree is not built for ${RID}; leaving it out."
           rm -f "$WORKER" ;;
    esac
fi

if [ ! -f "$WORKER" ]; then
    if [ "$RID" = linux-arm64 ]; then
        # Stated plainly rather than failed on: nothing in this repo cross-compiles the worker for
        # 64-bit ARM Linux today, so there is no action this message could ask for.
        echo "⚠️  Packaging without the device worker: no arm64 Linux build of it exists."
        echo "    Everything else in this package is unaffected; kits whose devices are compiled"
        echo "    models will say at Run that the worker is missing."
    elif [ "${CRF_ALLOW_NO_DEVICE_WORKER:-}" = 1 ]; then
        echo "⚠️  Packaging without the device worker on purpose. Compiled device models will not run."
    else
        echo "❌ The device worker is missing from ${PUBLISH}."
        echo ""
        echo "   circuitRF builds it during \`dotnet build\`, but only warns when no C compiler is"
        echo "   present — so this machine has none the build could use. Install one and re-run:"
        echo ""
        echo "       zig            one download, no daemon; the preferred route"
        echo "       gcc            the host compiler, on an x86-64 machine"
        echo "       docker/podman  pulls a small gcc image the first time"
        echo ""
        echo "   To package deliberately without it: CRF_ALLOW_NO_DEVICE_WORKER=1 $0 $ARCH"
        exit 1
    fi
fi

# ── Dependencies: deliberately none ───────────────────────────────────────────
#
# This package used to declare `Depends: libicu76 | libicu74 | ... | libicu67`, and that is an
# install failure waiting for the next distribution. ICU bumps its SONAME every release, so the
# package name changes with it (libicu77, libicu78, ...); an alternatives list can only name the
# versions that existed on the day it was written, and apt refuses the package outright when none of
# them is in the user's repositories — "none of the choices are installable: [no choices]", which is
# what a current-distro arm64 install reported (2026-08-21). Widening the list only moves the date.
#
# Nothing is lost by dropping it, because the version pin was never what made ICU work: the app is
# published SELF-CONTAINED, and .NET's globalization shim dlopen()s libicuuc.so.<N> across a wide
# range of N at startup, so it finds whatever ICU the machine actually has. postinst warns (without
# failing) when it finds none, and names the invariant-mode fall-back.
#
# fontconfig is a real run-time dependency of the shipped libSkiaSharp.so and is still not declared
# here on purpose — see packaging/RESOLVED.md, "Noted in passing".

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
