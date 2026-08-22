#!/bin/bash
set -euo pipefail

# ── circuitRF macOS .dmg builder ──────────────────────────────────────────────
#
#   packaging/macos/build-dmg.sh                 → BOTH disk images, for circuitRF
#   packaging/macos/build-dmg.sh harmonica       → both, for harmonicaRF
#   packaging/macos/build-dmg.sh wbond           → both, for wBond
#
#   packaging/macos/build-dmg.sh circuitrf arm64 → Apple Silicon only
#   packaging/macos/build-dmg.sh circuitrf x64   → Intel only
#
# → dist/circuitRF-<version>-arm64.dmg  and  dist/circuitRF-<version>-x64.dmg
#
# BOTH ARCHITECTURES ARE BUILT FROM WHICHEVER MAC YOU ARE ON, and that is a measured claim rather
# than a hopeful one. Every piece of the bundle cross-builds:
#
#   the .NET application     `dotnet publish -r osx-x64|osx-arm64`, either way round
#   crf-vmhost               `swift build --arch` — Apple's toolchain targets both slices, and
#                            Virtualization.framework is in the SDK for both. main.swift's Rosetta
#                            block is behind `#if arch(arm64)`, a TARGET test, so the x86-64 build
#                            correctly contains no Rosetta code at all
#   osdi-worker              `cc -arch`
#   the Linux VM image       pure download-and-repack (curl, tar, cpio, gzip, python3) — no
#                            compiler is involved in producing either guest kernel
#   senior_worker            one file for both: it is an x86-64 LINUX binary either way, because
#                            that is what the vendor model libraries are
#
# What makes it safe is that nothing here is trusted to have done the right thing: before writing a
# disk image this script reads the architecture back out of the built bundle with `lipo`. A stale
# helper build directory, or a helper that quietly fell back to the host, is caught rather
# than shipped — the failure it prevents is an application that launches, reads a kit, describes it
# correctly and then cannot evaluate a single compiled device model.
#
# The .app itself is built by the bundle scripts that already live in src/Ui/ — this adds the two
# things a distributable disk image needs on top of one: the icon (rasterised from the committed
# SVG, since no icon binary is tracked) and the .dmg with its /Applications drop target.
#
# Requires: .NET 10 SDK. Everything else (hdiutil, codesign, lipo) ships with macOS.

APP="${1:-circuitrf}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

case "$APP" in
    circuitrf) NAME="circuitRF";   BUNDLE_SCRIPT="bundleForMacOS.sh" ;;
    harmonica) NAME="harmonicaRF"; BUNDLE_SCRIPT="bundleForHarmonicaMacOS.sh" ;;
    wbond)     NAME="wBond";       BUNDLE_SCRIPT="bundleForWBondMacOS.sh" ;;
    *) echo "Usage: $0 [circuitrf|harmonica|wbond] [arm64|x64|both]"; exit 1 ;;
esac

# BOTH IS THE DEFAULT. A release needs both disk images, and the failure mode of the old
# one-at-a-time default was silent: whoever cut the release shipped whichever architecture they
# happened to be sitting at, and the other one simply did not exist.
case "${2:-both}" in
    both)  ARCHES="arm64 x64" ;;
    arm64) ARCHES="arm64"     ;;
    x64)   ARCHES="x64"       ;;
    *) echo "Usage: $0 [circuitrf|harmonica|wbond] [arm64|x64|both]"; exit 1 ;;
esac

# One source for the version — the repo-root VERSION file. The bundle script stamps the same value
# into the .app's Info.plist, so a disk image can never be named a version the installed app does
# not report.
source "${ROOT}/packaging/version.sh"
VERSION="$CRF_VERSION"

echo "🎨 Building icons..."
dotnet run --project "${ROOT}/tools/IconGen" -- "$APP"

# THE VM IMAGE IS BUILT HERE EVEN THOUGH A PLAIN BUILD LEAVES IT ALONE. Compiled device models are
# Linux libraries — nothing on macOS can load one — so circuitRF runs the worker inside the small
# Linux VM it ships, and the kernel and initramfs are part of that. Building them from scratch pulls
# ~330 MB per architecture, which is why an ordinary `dotnet build` prints the command instead of
# doing it silently. Packaging is the one moment that download is exactly what the operator asked
# for.
export CrfBuildVmImage=true

DIST="${ROOT}/dist"
mkdir -p "$DIST"
BUILT=""

for ARCH in $ARCHES; do
    case "$ARCH" in
        arm64) RID="osx-arm64"; MACHO_ARCH="arm64"  ;;
        x64)   RID="osx-x64";   MACHO_ARCH="x86_64" ;;
    esac

    echo ""
    echo "══ ${NAME} · ${ARCH} ══════════════════════════════════════════════════"

    # The bundle scripts default to the host's architecture; this is what makes the RID this script
    # names and the RID the .app is built at ONE value rather than two that happen to agree.
    export CRF_RID="$RID"

    echo "📦 Building ${NAME}.app (${RID})..."
    ( cd "${ROOT}/src/Ui" && bash "./${BUNDLE_SCRIPT}" )

    APP_BUNDLE="${ROOT}/src/Ui/bin/Release/net10.0/${RID}/${NAME}.app"
    [ -d "$APP_BUNDLE" ] || { echo "❌ ${APP_BUNDLE} was not produced."; exit 1; }

    # ── What the app needs beside its assemblies ──────────────────────────────
    #
    # The .csproj builds these and its CrfPublishHelperPrograms target publishes them, but both
    # build steps are warn-only BY DESIGN: nobody should be unable to build circuitRF for want of a
    # Swift toolchain or a C compiler. That is right for a build and wrong for a RELEASE — a disk
    # image missing them installs an application that reads a kit, describes it correctly, and then
    # refuses at Run naming programs the user never installed and had no way to install.
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
            echo "❌ Missing from ${NAME}.app (${ARCH}):${MISSING}"
            echo ""
            echo "   These are built during \`dotnet build\`, which only WARNS when it cannot:"
            echo ""
            echo "       senior_worker                 needs zig, docker/podman, or a cross-compiler"
            echo "       crf-vmhost + kernel/initramfs  need Xcode's Swift toolchain and a network"
            echo ""
            echo "   Build them by hand to see why:"
            echo "       tools/senior-worker/ensure-built.sh"
            echo "       tools/macos-vmhost/ensure-built.sh --arch ${MACHO_ARCH} --with-image"
            echo ""
            echo "   To package deliberately without them: CRF_ALLOW_NO_DEVICE_WORKER=1 $0 $APP $ARCH"
            exit 1
        fi
    fi

    # ── Architecture, measured rather than assumed ────────────────────────────
    #
    # Mirrors what build-deb.sh does with the worker's ELF header, and for the same reason: a binary
    # of the wrong architecture is not a lesser version of a working one. The app host, crf-vmhost
    # and osdi-worker are Mach-O, so `lipo -archs` reads it straight out; a wrong one here means a
    # bundle that either will not launch at all or cannot evaluate a compiled device model.
    #
    # senior_worker is deliberately NOT checked here — it is a Linux ELF, always x86-64 on purpose,
    # and lipo knows nothing about it. build-deb.sh's ELF check is the one that covers that file.
    BAD=""
    for f in "${NAME}" crf-vmhost osdi-worker; do
        path="${APP_BUNDLE}/Contents/MacOS/${f}"
        [ -f "$path" ] || continue
        archs=$(lipo -archs "$path" 2>/dev/null || echo "?")
        case " $archs " in
            *" ${MACHO_ARCH} "*) ;;
            *) BAD="${BAD}\n       ${f}: ${archs}" ;;
        esac
    done

    if [ -n "$BAD" ]; then
        echo "❌ ${NAME}.app (${ARCH}) contains Mach-O binaries that are not ${MACHO_ARCH}:"
        printf "%b\n" "$BAD"
        echo ""
        echo "   A helper fell back to this machine's own architecture instead of the one being"
        echo "   published, or a stale build directory was picked up. Delete"
        echo "   tools/macos-vmhost/build and tools/osdi-worker/build and run this again."
        exit 1
    fi

    # ── The guest kernel ──────────────────────────────────────────────────────
    #
    # The one per-architecture artifact `lipo` cannot speak for: it is a LINUX kernel, not Mach-O.
    # An aarch64 Image carries "ARM\x64" at offset 56; an x86-64 bzImage carries "HdrS" at 0x202.
    # A bundle carrying the other one starts its VM and gets "Internal Virtualization error", which
    # names nothing, so it is worth the four lines to catch here.
    KERNEL="${APP_BUNDLE}/Contents/MacOS/crf-linux-kernel"
    if [ -f "$KERNEL" ]; then
        python3 - "$KERNEL" "$MACHO_ARCH" <<'KPY' || exit 1
import sys
data = open(sys.argv[1], 'rb').read()
want = sys.argv[2]
is_arm = data[56:60] in (b'ARM\x64', b'ARMd')
is_x86 = data[0x202:0x206] == b'HdrS'
got = 'arm64' if is_arm else 'x86_64' if is_x86 else 'unrecognised'
if got != want:
    sys.exit(f"❌ crf-linux-kernel in this bundle is {got}, not {want}. The guest kernel must "
             f"match the host that boots it; delete tools/macos-vmhost/build and build again.")
KPY
    fi

    DMG="${DIST}/${NAME}-${VERSION}-${ARCH}.dmg"
    STAGE="$(mktemp -d)/${NAME}"
    mkdir -p "$STAGE"

    echo "💿 Staging disk image..."
    cp -R "$APP_BUNDLE" "$STAGE/"
    ln -s /Applications "${STAGE}/Applications"   # the drag-to-install target users expect

    rm -f "$DMG"
    hdiutil create -volname "$NAME" -srcfolder "$STAGE" -ov -format UDZO -quiet "$DMG"
    rm -rf "$(dirname "$STAGE")"

    BUILT="${BUILT}\n   ${DMG}"
done

echo ""
echo "✅ Built:"
printf "%b\n" "$BUILT"
echo ""
echo "   Ad-hoc signed: a first launch needs right-click → Open, or"
echo "   xattr -dr com.apple.quarantine /Applications/${NAME}.app"
echo "   For public distribution, sign with a Developer ID certificate and notarise — see BUILDING.md."
