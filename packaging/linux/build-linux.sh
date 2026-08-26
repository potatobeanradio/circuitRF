#!/bin/bash
set -euo pipefail

# -- circuitRF Linux installer builder ----------------------------------------
#
#   packaging/linux/build-linux.sh
#
# With no arguments it builds EVERYTHING this platform ships - both architectures, both package
# kinds:
#
#   dist/circuitRF-<version>-x64.deb            /opt/circuitrf, needs root      notify-only
#   dist/circuitRF-<version>-arm64.deb
#   dist/circuitRF-<version>-linux-x64.tar.gz   ~/.local, no root               updates itself
#   dist/circuitRF-<version>-linux-arm64.tar.gz
#
# Narrow it only when you mean to:
#
#   packaging/linux/build-linux.sh x64            both kinds, x64 only
#   packaging/linux/build-linux.sh both tarball   both architectures, no .deb
#   packaging/linux/build-linux.sh arm64 deb
#
# WHY THE DEFAULT IS EVERYTHING. The .deb and the tarball used to be two scripts, each defaulting to
# one architecture, so a complete Linux release was four invocations and looked like one. A release
# cut that way ships the notify-only .deb files and silently omits the tarball - which is both the
# only self-updating Linux install AND the update payload, so nobody on Linux is offered the next
# version, and, because UpdateSelector needs a matching asset before it will even post the
# notify-only line, nobody is TOLD about it either. That happened (1.0.0-beta.2). A release script
# whose obvious invocation produces an incomplete release is the script's bug, not the operator's.
#
# THE TWO KINDS, AND WHY THERE ARE TWO. The .deb installs to /opt/circuitrf, which only root can
# write, so it is the managed, machine-wide story and it is NOTIFY-ONLY: circuitRF checks for
# updates, posts one Message Panel line with a link, and writes nothing. A silent background update
# requires an install location the running USER can write (docs/design/auto-update.md section 1), so
# the channel that actually auto-updates is the tarball - ~/.local/share/circuitRF, no root at any
# point, versioned directories behind a `current` symlink so an update is a pointer flip.
#
# THE TARBALL'S NAME IS A CONTRACT. src/Ui/Updates/UpdateAssetNames.cs parses exactly this spelling
# and tests/Ui.Tests/ assert it. Rename it and updates stop - with no error anywhere and no user
# report, because a user who is not being offered an update has nothing to notice.
#
# ONE PUBLISH SERVES BOTH KINDS: they differ in where the files go, never in the files themselves.
#
# Requires: .NET 10 SDK, and fpm (https://fpm.readthedocs.io) only if you are building .deb files:
#     sudo apt-get install ruby-dev build-essential && sudo gem install fpm
# fpm, not dotnet-deb: that tool targets .NET 9 and does not work with .NET 10.
# Cross-building is fine - an arm64 package can be produced on an x64 machine.

ARCH_ARG="${1:-both}"
KIND_ARG="${2:-both}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
HERE="${ROOT}/packaging/linux"

# One source for the version (repo-root VERSION); CRF_DEB_VERSION is dpkg's spelling of it, and the
# tarball carries CRF_VERSION verbatim - the ~ spelling is the .deb's alone.
source "${ROOT}/packaging/version.sh"

case "$ARCH_ARG" in
    both|all) ARCHES="x64 arm64" ;;
    x64)      ARCHES="x64"       ;;
    arm64)    ARCHES="arm64"     ;;
    *) echo "Usage: $0 [x64|arm64|both] [deb|tarball|both]"; exit 1 ;;
esac

case "$KIND_ARG" in
    both|all) KINDS="deb tarball" ;;
    deb)      KINDS="deb"         ;;
    tarball)  KINDS="tarball"     ;;
    *) echo "Usage: $0 [x64|arm64|both] [deb|tarball|both]"; exit 1 ;;
esac

# Only a .deb needs fpm, so a tarball-only run works on a machine that has never seen ruby.
case " $KINDS " in
    *" deb "*) command -v fpm >/dev/null || { echo "fpm not found - see the header of this script."; exit 1; } ;;
esac

DIST="${ROOT}/dist"
mkdir -p "$DIST"

TMPROOT="$(mktemp -d)"
trap 'rm -rf "$TMPROOT"' EXIT

BUILT=()

# Icons are architecture-independent, so this runs once rather than once per package.
echo "Building icons..."
dotnet run --project "${ROOT}/tools/IconGen" -- circuitrf

for ARCH in $ARCHES; do

    echo ""
    echo "=== ${ARCH} ==================================================================="

    case "$ARCH" in
        x64)   RID="linux-x64";   DEB_ARCH="amd64" ;;
        arm64) RID="linux-arm64"; DEB_ARCH="arm64" ;;
    esac

    PUBLISH="${ROOT}/publish/${RID}"
    echo "Publishing ${RID}..."
    rm -rf "$PUBLISH"
    dotnet publish "${ROOT}/src/Ui/CircuitRF.Ui.csproj" -c Release -r "$RID" --self-contained true -o "$PUBLISH"

    # -- The device worker ----------------------------------------------------
    #
    # The program that evaluates a kit's compiled device models. The .csproj builds it (via
    # tools/senior-worker/ensure-built.sh) and publishes it, but that build step is warn-only BY
    # DESIGN - nobody should be unable to build circuitRF for want of a C compiler.
    #
    # That is right for a build and wrong for a RELEASE: a package missing it installs an
    # application that reads a kit, describes it correctly, and then refuses at Run naming a program
    # the user never installed and had no way to install. So packaging checks what building only
    # warns about.
    #
    # Set CRF_ALLOW_NO_DEVICE_WORKER=1 to package without it on purpose.
    WORKER="${PUBLISH}/senior_worker"

    if [ -f "$WORKER" ]; then
        # ARCHITECTURE, from the ELF header rather than from where the file came from: bytes 18-19,
        # little-endian, 0x3E = x86-64 and 0xB7 = aarch64. A worker of the wrong architecture is not
        # a lesser version of a working one - it starts and then cannot load a single model - so it
        # is dropped rather than shipped, and the run-time message about a missing worker is the
        # accurate one to leave the user with.
        machine="$(od -An -tx1 -j18 -N2 "$WORKER" | tr -d ' ')"
        case "${RID}:${machine}" in
            linux-x64:3e00|linux-arm64:b700) ;;
            *) echo "WARNING: the device worker in the publish tree is not built for ${RID}; leaving it out."
               rm -f "$WORKER" ;;
        esac
    fi

    if [ ! -f "$WORKER" ]; then
        if [ "$RID" = linux-arm64 ]; then
            # Stated plainly rather than failed on: nothing in this repo cross-compiles the worker
            # for 64-bit ARM Linux today, so there is no action this message could ask for.
            echo "WARNING: packaging without the device worker: no arm64 Linux build of it exists."
            echo "         Everything else in this package is unaffected; kits whose devices are"
            echo "         compiled models will say at Run that the worker is missing."
        elif [ "${CRF_ALLOW_NO_DEVICE_WORKER:-}" = 1 ]; then
            echo "WARNING: packaging without the device worker on purpose. Compiled models will not run."
        else
            echo "ERROR: the device worker is missing from ${PUBLISH}."
            echo ""
            echo "   circuitRF builds it during \`dotnet build\`, but only warns when no C compiler is"
            echo "   present - so this machine has none the build could use. Install one and re-run:"
            echo ""
            echo "       zig            one download, no daemon; the preferred route"
            echo "       gcc            the host compiler, on an x86-64 machine"
            echo "       docker/podman  pulls a small gcc image the first time"
            echo ""
            echo "   To package deliberately without it: CRF_ALLOW_NO_DEVICE_WORKER=1 $0 $ARCH"
            exit 1
        fi
    fi

    for KIND in $KINDS; do
        case "$KIND" in

        deb)
            # -- Dependencies: deliberately none ------------------------------
            #
            # This package used to declare `Depends: libicu76 | libicu74 | ... | libicu67`, and that
            # is an install failure waiting for the next distribution. ICU bumps its SONAME every
            # release, so the package name changes with it (libicu77, libicu78, ...); an
            # alternatives list can only name the versions that existed on the day it was written,
            # and apt refuses the package outright when none of them is in the user's repositories -
            # "none of the choices are installable: [no choices]", which is what a current-distro
            # arm64 install reported (2026-08-21). Widening the list only moves the date.
            #
            # Nothing is lost by dropping it, because the version pin was never what made ICU work:
            # the app is published SELF-CONTAINED, and .NET's globalization shim dlopen()s
            # libicuuc.so.<N> across a wide range of N at startup, so it finds whatever ICU the
            # machine actually has. postinst warns (without failing) when it finds none, and names
            # the invariant-mode fall-back.
            #
            # fontconfig is a real run-time dependency of the shipped libSkiaSharp.so and is still
            # not declared here on purpose - see packaging/RESOLVED.md, "Noted in passing".

            DEB="${DIST}/circuitRF-${CRF_VERSION}-${ARCH}.deb"
            rm -f "$DEB"

            echo "Building ${DEB}..."
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

            echo "OK  ${DEB}"
            echo "    Install: sudo apt install ${DEB}     Remove: sudo apt remove circuitrf"
            BUILT+=("$DEB")
            ;;

        tarball)
            # -- Stage --------------------------------------------------------
            #
            # app-<version>/ inside the archive, so the tarball's own shape IS the installed shape
            # and install.sh only has to move it and re-point one symlink.

            STAGE="${TMPROOT}/${ARCH}"
            rm -rf "$STAGE"
            mkdir -p "$STAGE"

            APPDIR="${STAGE}/circuitRF-${CRF_VERSION}/app-${CRF_VERSION}"
            mkdir -p "$APPDIR"
            cp -a "${PUBLISH}/." "$APPDIR/"
            chmod +x "${APPDIR}/circuitRF" 2>/dev/null || true
            [ -f "${APPDIR}/senior_worker" ] && chmod +x "${APPDIR}/senior_worker"

            cp "${HERE}/install.sh"          "${STAGE}/circuitRF-${CRF_VERSION}/install.sh"
            cp "${HERE}/circuitrf-mime.xml"  "${STAGE}/circuitRF-${CRF_VERSION}/circuitrf-mime.xml"
            cp "${HERE}/icons/circuitrf.png" "${STAGE}/circuitRF-${CRF_VERSION}/circuitrf.png"
            chmod +x "${STAGE}/circuitRF-${CRF_VERSION}/install.sh"

            # The version the installer lays into `current`, so install.sh never has to parse a
            # directory name.
            echo -n "app-${CRF_VERSION}" > "${STAGE}/circuitRF-${CRF_VERSION}/current"

            OUT="${DIST}/circuitRF-${CRF_VERSION}-linux-${ARCH}.tar.gz"
            rm -f "$OUT"

            echo "Packing ${OUT}..."
            tar -C "$STAGE" -czf "$OUT" "circuitRF-${CRF_VERSION}"
            rm -rf "$STAGE"

            echo "OK  ${OUT}"
            echo "    Install with:  tar xzf $(basename "$OUT") && ./circuitRF-${CRF_VERSION}/install.sh"
            echo "    This archive is ALSO the update payload; its name is parsed by UpdateAssetNames.cs."
            BUILT+=("$OUT")
            ;;
        esac
    done
done

echo ""
echo "=== Done. ${#BUILT[@]} artifact(s) in ${DIST}"
# ${BUILT[@]} on an EMPTY array is an unbound-variable error under `set -u` on bash 3.2, which is
# still what macOS ships; the +"..." form is the portable spelling and costs nothing here.
for f in ${BUILT[@]+"${BUILT[@]}"}; do echo "    $(basename "$f")"; done

# A full run ships four files and a release needs every one of them. Stated here rather than left to
# be noticed, because the failure this guards against is silent: a missing tarball stops Linux
# updates with no error anywhere.
if [ "$ARCH_ARG" = "both" ] && [ "$KIND_ARG" = "both" ] && [ "${#BUILT[@]}" -ne 4 ]; then
    echo ""
    echo "WARNING: expected 4 artifacts for a full run, got ${#BUILT[@]}."
fi
