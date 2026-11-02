#!/bin/bash
set -euo pipefail

# -- circuitRF Linux installer builder ----------------------------------------
#
#   packaging/linux/build-linux.sh
#
# With no arguments it builds EVERYTHING this platform ships - both architectures, all three package
# kinds:
#
#   dist/circuitRF-<version>-x64.deb              /opt/circuitrf, needs root    notify-only
#   dist/circuitRF-<version>-arm64.deb
#   dist/circuitRF-<version>-linux-x64.tar.gz     ~/.local, no root             updates itself
#   dist/circuitRF-<version>-linux-arm64.tar.gz
#   dist/circuitRF-<version>-linux-x64.AppImage   one file, runs anywhere       offers a Download
#   dist/circuitRF-<version>-linux-arm64.AppImage
#
# Narrow it only when you mean to:
#
#   packaging/linux/build-linux.sh x64             all kinds, x64 only
#   packaging/linux/build-linux.sh both tarball    both architectures, tarballs only
#   packaging/linux/build-linux.sh arm64 deb
#   packaging/linux/build-linux.sh both appimage
#
# WHY THE DEFAULT IS EVERYTHING. The .deb and the tarball used to be two scripts, each defaulting to
# one architecture, so a complete Linux release was four invocations and looked like one. A release
# cut that way ships the notify-only .deb files and silently omits the tarball - which is both the
# only self-updating Linux install AND the update payload, so nobody on Linux is offered the next
# version, and, because UpdateSelector needs a matching asset before it will even post the
# notify-only line, nobody is TOLD about it either. That happened (1.0.0-beta.2). A release script
# whose obvious invocation produces an incomplete release is the script's bug, not the operator's.
#
# THE KINDS, AND WHY THERE IS MORE THAN ONE. The .deb installs to /opt/circuitrf, which only root can
# write, so it is the managed, machine-wide story and it is NOTIFY-ONLY: circuitRF checks for
# updates, posts one Message Panel line with a link, and writes nothing. A silent background update
# requires an install location the running USER can write (docs/design/auto-update.md section 1), so
# the channel that actually auto-updates is the tarball - ~/.local/share/circuitRF, no root at any
# point, versioned directories behind a `current` symlink so an update is a pointer flip. The
# AppImage is one executable file holding the publish tree in a compressed read-only image: no install
# step, no root, keep it anywhere. It cannot update itself (it runs from a read-only mount), so when a
# newer release exists its update line carries a Download button that saves the new AppImage beside
# the running one; nothing is replaced (docs/design/auto-update.md section 2.2).
#
# THE TARBALL'S AND THE APPIMAGE'S NAMES ARE A CONTRACT. src/Ui/Updates/UpdateAssetNames.cs parses
# exactly these spellings and tests/Ui.Tests/ assert them. Rename it and updates stop - with no error anywhere and no user
# report, because a user who is not being offered an update has nothing to notice.
#
# ONE PUBLISH SERVES EVERY KIND: they differ in where the files go, never in the files themselves.
#
# Requires: .NET 10 SDK, and fpm (https://fpm.readthedocs.io) only if you are building .deb files:
#     sudo apt-get install ruby-dev build-essential && sudo gem install fpm
# fpm, not dotnet-deb: that tool targets .NET 9 and does not work with .NET 10.
# The AppImage needs curl or wget once, to fetch the tools packaging/linux/appimage.env pins, and no
# FUSE: the packing tool runs with APPIMAGE_EXTRACT_AND_RUN=1, so a container or a CI runner can pack.
# It also needs zsyncmake (sudo apt-get install zsync): every AppImage carries update information for
# AppImageUpdate and ships with its .zsync file beside it,
#
#   dist/circuitRF-<version>-linux-x64.AppImage.zsync     what AppImageUpdate reads; upload with the release
#   dist/circuitRF-<version>-linux-arm64.AppImage.zsync
#
# and an AppImage whose .zsync is missing from the release is one AppImageUpdate cannot update, with no
# error anywhere. So a missing zsyncmake is a refusal before anything is built; CRF_ALLOW_NO_ZSYNC=1
# packs AppImages with no update information instead, knowingly.
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
    *) echo "Usage: $0 [x64|arm64|both] [deb|tarball|appimage|all]"; exit 1 ;;
esac

case "$KIND_ARG" in
    both|all) KINDS="deb tarball appimage" ;;
    deb)      KINDS="deb"                  ;;
    tarball)  KINDS="tarball"              ;;
    appimage) KINDS="appimage"             ;;
    *) echo "Usage: $0 [x64|arm64|both] [deb|tarball|appimage|all]"; exit 1 ;;
esac

# Only a .deb needs fpm, so a tarball-only run works on a machine that has never seen ruby.
case " $KINDS " in
    *" deb "*) command -v fpm >/dev/null || { echo "fpm not found - see the header of this script."; exit 1; } ;;
esac

case "$(uname -m)" in
    x86_64)        HOST_ARCH=x64   ;;
    aarch64|arm64) HOST_ARCH=arm64 ;;
    *)             HOST_ARCH="$(uname -m)" ;;
esac

# -- The AppImage tools (brief APPIMG R-appimg-2) -----------------------------
#
# appimagetool for THIS machine and the type-2 static runtime for each TARGET architecture, every one
# pinned in packaging/linux/appimage.env by a fixed release's URL and its SHA-256. Cached per user
# beside the kernel caches; the hash is re-checked on every use and a mismatch is a refusal. Fetched
# here, before anything is published, so a missing tool costs seconds rather than a publish.
APPIMAGE_ENV="${HERE}/appimage.env"
APPIMAGE_CACHE="${CRF_APPIMAGE_CACHE:-$HOME/.circuitRF-build/appimage}"
appimage_val() { sed -n "s/^$1=//p" "$APPIMAGE_ENV"; }
sha256_of() {
    if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | awk '{print $1}'
    else shasum -a 256 "$1" | awk '{print $1}'; fi
}
# Prints the cached path of <KEY>_URL after checking it against <KEY>_SHA256; fails otherwise.
appimage_pinned() {
    local key="$1" url sha file got
    url="$(appimage_val "${key}_URL")"
    sha="$(appimage_val "${key}_SHA256")"
    if [ -z "$url" ] || [ -z "$sha" ]; then
        echo "ERROR: ${APPIMAGE_ENV} has no ${key}_URL or no ${key}_SHA256." >&2; return 1
    fi
    file="${APPIMAGE_CACHE}/${sha}/$(basename "$url")"
    if [ ! -f "$file" ]; then
        mkdir -p "$(dirname "$file")"
        echo "Fetching ${url} ..." >&2
        if command -v curl >/dev/null 2>&1; then curl -fL --retry 3 -o "${file}.part" "$url" >&2 || return 1
        else wget -O "${file}.part" "$url" >&2 || return 1; fi
        mv "${file}.part" "$file"
    fi
    got="$(sha256_of "$file")"
    if [ "$got" != "$sha" ]; then
        echo "ERROR: ${file} has SHA-256 ${got}, and ${APPIMAGE_ENV} pins ${sha} for ${key}." >&2
        echo "       Refusing to pack with it. Delete the file to fetch it again, or correct the pin." >&2
        return 1
    fi
    echo "$file"
}

case " $KINDS " in
    *" appimage "*)
        [ "$(uname -s)" = Linux ] || { echo "The AppImage is packed on Linux (appimagetool is a Linux program)."; exit 1; }
        case "$HOST_ARCH" in
            x64|arm64) ;;
            *) echo "No appimagetool is pinned for a ${HOST_ARCH} build machine."; exit 1 ;;
        esac
        HOST_KEY="$(echo "$HOST_ARCH" | tr '[:lower:]' '[:upper:]')"
        APPIMAGETOOL="$(appimage_pinned "APPIMAGETOOL_${HOST_KEY}")" || exit 1
        chmod +x "$APPIMAGETOOL"
        for ARCH in $ARCHES; do
            appimage_pinned "RUNTIME_$(echo "$ARCH" | tr '[:lower:]' '[:upper:]')" >/dev/null || exit 1
        done
        APPIMAGE_COMPRESSION="$(appimage_val APPIMAGE_COMPRESSION)"

        # -- AppImageUpdate: the update information and the tool that writes its .zsync ----------
        #
        # Checked HERE, before the publish, so a machine without zsyncmake is told in a second rather
        # than after minutes of building. appimagetool itself only warns when zsyncmake is absent and
        # then packs an image whose update information points at a file nobody wrote.
        APPIMAGE_UPDATE_GITHUB="$(appimage_val APPIMAGE_UPDATE_GITHUB)"
        case "$APPIMAGE_UPDATE_GITHUB" in
            */*) ;;
            *) echo "ERROR: ${APPIMAGE_ENV} has no APPIMAGE_UPDATE_GITHUB=owner/repository." >&2; exit 1 ;;
        esac
        # The channel follows VERSION, as packaging/sign-release.sh's prerelease flag does: GitHub's
        # `latest` never considers a prerelease, so a beta's image asks for `latest-all`.
        case "$CRF_VERSION" in
            *-*) APPIMAGE_UPDATE_TAG=latest-all ;;
            *)   APPIMAGE_UPDATE_TAG=latest     ;;
        esac
        if ZSYNCMAKE="$(command -v zsyncmake)"; then
            :
        elif [ "${CRF_ALLOW_NO_ZSYNC:-}" = 1 ]; then
            ZSYNCMAKE=""
            echo "NOTE: no zsyncmake and CRF_ALLOW_NO_ZSYNC=1 - the AppImages carry NO update information,"
            echo "      so AppImageUpdate cannot update them."
        else
            echo "ERROR: zsyncmake is not installed. Every AppImage carries update information for AppImageUpdate," >&2
            echo "       and the .zsync file it reads is written by zsyncmake. Install it (sudo apt-get install zsync)" >&2
            echo "       and run this again, or set CRF_ALLOW_NO_ZSYNC=1 to pack images with no update information." >&2
            exit 1
        fi
        ;;
esac

DIST="${ROOT}/dist"
mkdir -p "$DIST"

TMPROOT="$(mktemp -d)"
trap 'rm -rf "$TMPROOT"' EXIT

BUILT=()
# The .zsync files written beside the AppImages - counted apart from BUILT, which is the packages.
ZSYNCED=()

# Icons are architecture-independent, so this runs once rather than once per package.
echo "Building icons..."
dotnet run --project "${ROOT}/tools/IconGen" -- circuitrf

# The packaging smoke check (brief-automation-13-installed-cli.md). Built once, Release, so it
# shares the libraries the publishes build; run once per architecture below.
echo "Building the packaging smoke check..."
dotnet build "${ROOT}/tools/CliSmoke" -c Release -v quiet
SMOKE_DLL="${ROOT}/tools/CliSmoke/bin/Release/net10.0/CliSmoke.dll"
UNSMOKED=""

# -- The geometry kernel (brief-em3d-62 R-em3d62-5) ---------------------------
#
# OpenCASCADE behind tools/geometry-worker, in <publish>/geometry-kernel/. `dotnet build` only ever
# COPIES it out of the per-user cache and warns when the cache is empty - right for a build, wrong for
# a release - so this script BUILDS it per RID with --strict, and fails at the end when a RID that
# ships it (recipe.env's KERNEL_RIDS, decision D2) came out without it. The first run on a machine
# builds OCCT itself (minutes, once per architecture; every later run reuses the per-user cache and
# only recompiles the worker when it is stale, in seconds). The other architecture is cross-built with
# the distribution's cross g++, or a CMake toolchain file named by CRF_OCCT_TOOLCHAIN_FILE.
#
# Set CRF_ALLOW_NO_KERNEL=1 to package without it on purpose (nothing is fetched or built then).
KERNEL_RECIPE="${ROOT}/tools/geometry-worker/occt/recipe.env"
OCCT_VERSION=$(sed -n 's/^OCCT_VERSION=//p' "$KERNEL_RECIPE")
KERNEL_RIDS=$(sed -n 's/^KERNEL_RIDS=//p' "$KERNEL_RECIPE")
NO_KERNEL=""

# -- The gdstk worker (brief-oasis-gdstk.md 4e) --------------------------------
#
# gdstk, qhull and zlib in ONE statically linked program, <publish>/gdstk-kernel/gdstk-worker: the OASIS
# import and export and the second GDSII route. The geometry kernel's rule exactly: built per RID with
# --strict (well under a minute, once), and the run fails at the end when a RID in
# tools/gdstk-worker/recipe.env's KERNEL_RIDS came out without it. The same toolchain builds it, so the
# package check below covers both. Set CRF_ALLOW_NO_GDSTK=1 to package without it on purpose.
GDSTK_RECIPE="${ROOT}/tools/gdstk-worker/recipe.env"
GDSTK_VERSION=$(sed -n 's/^GDSTK_VERSION=//p' "$GDSTK_RECIPE")
GDSTK_RIDS=$(sed -n 's/^KERNEL_RIDS=//p' "$GDSTK_RECIPE")
NO_GDSTK=""

# -- The kernel's toolchain, checked once, and offered for install -------------
#
# What tools/geometry-worker/build.sh needs, as Debian/Ubuntu package names: CMake, a C++ compiler and
# make, a downloader, and for the architecture this machine is not, the cross g++. When any is missing
# and someone is at the keyboard, this offers to apt-get install exactly those, rather than letting the
# kernel fail per architecture minutes later. Declined, or with nobody to ask, the run carries on:
# build.sh refuses, and the summary at the end says so.
KERNEL_PKGS=""
need_pkg() { case " $KERNEL_PKGS " in *" $1 "*) ;; *) KERNEL_PKGS="${KERNEL_PKGS:+$KERNEL_PKGS }$1" ;; esac; }
if [ "${CRF_ALLOW_NO_KERNEL:-}" != 1 ] || [ "${CRF_ALLOW_NO_GDSTK:-}" != 1 ]; then
    for ARCH in $ARCHES; do
        WANTED=0
        if [ "${CRF_ALLOW_NO_KERNEL:-}" != 1 ]; then
            case " $KERNEL_RIDS " in *" linux-${ARCH} "*) WANTED=1 ;; esac
        fi
        if [ "${CRF_ALLOW_NO_GDSTK:-}" != 1 ]; then
            case " $GDSTK_RIDS " in *" linux-${ARCH} "*) WANTED=1 ;; esac
        fi
        [ "$WANTED" = 1 ] || continue
        command -v cmake >/dev/null 2>&1 || need_pkg cmake
        { command -v make >/dev/null 2>&1 && command -v g++ >/dev/null 2>&1; } || need_pkg build-essential
        command -v curl >/dev/null 2>&1 || command -v wget >/dev/null 2>&1 || need_pkg curl
        if [ "$ARCH" != "$HOST_ARCH" ] && [ -z "${CRF_OCCT_TOOLCHAIN_FILE:-}" ]; then
            case "$ARCH" in
                x64)   command -v x86_64-linux-gnu-g++  >/dev/null 2>&1 || need_pkg crossbuild-essential-amd64 ;;
                arm64) command -v aarch64-linux-gnu-g++ >/dev/null 2>&1 || need_pkg crossbuild-essential-arm64 ;;
            esac
        fi
    done
fi
if [ -n "$KERNEL_PKGS" ]; then
    echo ""
    echo "The geometry kernel and the gdstk worker need packages this machine does not have: ${KERNEL_PKGS}"
    ANSWER=n
    if [ -t 0 ] && command -v apt-get >/dev/null 2>&1; then
        read -r -p "  Install them now with apt-get (sudo)? [Y/n] " ANSWER || ANSWER=n
    fi
    case "$ANSWER" in
        ""|[Yy]*)
            # Unquoted on purpose: one argument per package.
            # shellcheck disable=SC2086
            if sudo apt-get update && sudo apt-get install -y $KERNEL_PKGS; then
                echo "The kernel toolchain is installed."
            else
                echo "WARNING: apt-get did not install them; the kernel will not build, and that is reported again at the end."
            fi ;;
        *)
            echo "  Not installing. To install them yourself (Debian/Ubuntu names):"
            echo "    sudo apt-get install ${KERNEL_PKGS}"
            echo "  Continuing; the kernel will not build, and that is reported again at the end." ;;
    esac
    echo ""
fi

for ARCH in $ARCHES; do

    echo ""
    echo "=== ${ARCH} ==================================================================="

    case "$ARCH" in
        x64)   RID="linux-x64";   DEB_ARCH="amd64" ;;
        arm64) RID="linux-arm64"; DEB_ARCH="arm64" ;;
    esac

    SHIPS_KERNEL=0
    case " $KERNEL_RIDS " in *" $RID "*) SHIPS_KERNEL=1 ;; esac
    if [ "$SHIPS_KERNEL" = 1 ] && [ "${CRF_ALLOW_NO_KERNEL:-}" != 1 ]; then
        echo "Building the geometry kernel (${RID})..."
        "${ROOT}/tools/geometry-worker/build.sh" --strict --rid "$RID" \
            || echo "WARNING: the geometry kernel did not build for ${RID} (see above); reported again at the end."
    fi

    SHIPS_GDSTK=0
    case " $GDSTK_RIDS " in *" $RID "*) SHIPS_GDSTK=1 ;; esac
    if [ "$SHIPS_GDSTK" = 1 ] && [ "${CRF_ALLOW_NO_GDSTK:-}" != 1 ]; then
        echo "Building the gdstk worker (${RID})..."
        "${ROOT}/tools/gdstk-worker/build.sh" --strict --rid "$RID" \
            || echo "WARNING: the gdstk worker did not build for ${RID} (see above); reported again at the end."
    fi

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

    # -- The OSDI worker ------------------------------------------------------
    #
    # The program that hosts a Verilog-A model the USER compiled. Same contract as the device worker
    # above, checked here for the same reason: the build step only ever warns, which is right for a
    # build and wrong for a release.
    #
    # It differs from the device worker in the one way that decides this block. That one is x86-64
    # ALWAYS - the vendor model libraries it hosts only ever ship as x64 - so on arm64 there is
    # nothing to build and the warning above says so. This one loads the user's own build output, so
    # its architecture must match THE PACKAGE's, and both are buildable:
    # tools/osdi-worker/build.sh cross-compiles either one with zig. A missing worker here is
    # therefore a machine that needs a toolchain, not a platform nobody supports.
    #
    # Set CRF_ALLOW_NO_OSDI_WORKER=1 to package without it on purpose.
    OSDI="${PUBLISH}/osdi-worker"

    if [ -f "$OSDI" ]; then
        # THE MAGIC IS READ AS WELL AS THE MACHINE, unlike the check above. Until 2026-09 the OSDI
        # build script ignored the target it was handed off macOS, so a Mac publishing for Linux
        # dropped a Mach-O binary into this tree under this exact name - and bytes 18-19 of a Mach-O
        # are not an architecture at all, so reading them alone can agree by accident. 0x7f E L F is
        # what separates "wrong architecture" from "not even an ELF".
        elf="$(od -An -tx1 -N4        "$OSDI" | tr -d ' ')"
        machine="$(od -An -tx1 -j18 -N2 "$OSDI" | tr -d ' ')"
        case "${elf}:${RID}:${machine}" in
            7f454c46:linux-x64:3e00|7f454c46:linux-arm64:b700) ;;
            *) echo "WARNING: the OSDI worker in the publish tree is not a ${RID} binary; leaving it out."
               rm -f "$OSDI" ;;
        esac
    fi

    if [ ! -f "$OSDI" ]; then
        if [ "${CRF_ALLOW_NO_OSDI_WORKER:-}" = 1 ]; then
            echo "WARNING: packaging without the OSDI worker on purpose. Compiled Verilog-A models will not run."
        else
            echo "ERROR: the OSDI worker is missing from ${PUBLISH}."
            echo ""
            echo "   circuitRF builds it during \`dotnet build\` (tools/osdi-worker/build.sh), but only"
            echo "   warns when it cannot - so this machine has no toolchain that targets ${RID}:"
            echo ""
            echo "       zig            builds either Linux architecture from either; the preferred route"
            echo "       gcc            the host compiler, when packaging natively on ${ARCH}"
            echo "       docker/podman  pulls a small gcc image the first time"
            echo ""
            echo "   A zig that is installed but not on PATH can be named: CRF_ZIG=/path/to/zig"
            echo "   To package deliberately without it: CRF_ALLOW_NO_OSDI_WORKER=1 $0 $ARCH"
            exit 1
        fi
    fi

    # -- The geometry kernel -------------------------------------------------
    #
    # Read back out of the publish tree like the workers above: an ELF of the package's architecture
    # (the cache holds one OCCT build per RID, and a stale build directory could hold the other), and
    # dropped when this RID does not ship it (D2), so a package matches the decision whatever the
    # build machine's cache happens to hold.
    KERNEL_DIR="${PUBLISH}/geometry-kernel"
    KERNEL_WORKER="${KERNEL_DIR}/geometry-worker"
    if [ -f "$KERNEL_WORKER" ]; then
        elf="$(od -An -tx1 -N4        "$KERNEL_WORKER" | tr -d ' ')"
        machine="$(od -An -tx1 -j18 -N2 "$KERNEL_WORKER" | tr -d ' ')"
        case "${elf}:${RID}:${machine}" in
            7f454c46:linux-x64:3e00|7f454c46:linux-arm64:b700) ;;
            *) echo "WARNING: the geometry worker in the publish tree is not a ${RID} binary; leaving the kernel out."
               rm -rf "$KERNEL_DIR" ;;
        esac
    fi
    if [ "$SHIPS_KERNEL" = 0 ] && [ -d "$KERNEL_DIR" ]; then
        echo "NOTE: ${RID} is not in recipe.env's KERNEL_RIDS; leaving the geometry kernel out."
        rm -rf "$KERNEL_DIR"
    fi
    if [ "$SHIPS_KERNEL" = 1 ] && [ ! -f "$KERNEL_WORKER" ]; then
        NO_KERNEL="${NO_KERNEL} ${ARCH}"
    fi
    if [ -f "$KERNEL_WORKER" ]; then
        KERNEL_SMOKE=(--kernel "$OCCT_VERSION")
    elif [ "$SHIPS_KERNEL" = 1 ]; then
        KERNEL_SMOKE=(--no-kernel "built without it; reported at the end")
    else
        KERNEL_SMOKE=(--no-kernel "not shipped on ${RID}")
    fi

    # -- The gdstk worker ----------------------------------------------------
    #
    # The same reading-back: an ELF of the package's architecture, dropped when this RID does not ship it.
    GDSTK_DIR="${PUBLISH}/gdstk-kernel"
    GDSTK_WORKER="${GDSTK_DIR}/gdstk-worker"
    if [ -f "$GDSTK_WORKER" ]; then
        elf="$(od -An -tx1 -N4        "$GDSTK_WORKER" | tr -d ' ')"
        machine="$(od -An -tx1 -j18 -N2 "$GDSTK_WORKER" | tr -d ' ')"
        case "${elf}:${RID}:${machine}" in
            7f454c46:linux-x64:3e00|7f454c46:linux-arm64:b700) ;;
            *) echo "WARNING: the gdstk worker in the publish tree is not a ${RID} binary; leaving it out."
               rm -rf "$GDSTK_DIR" ;;
        esac
    fi
    if [ "$SHIPS_GDSTK" = 0 ] && [ -d "$GDSTK_DIR" ]; then
        echo "NOTE: ${RID} is not in tools/gdstk-worker/recipe.env's KERNEL_RIDS; leaving the gdstk worker out."
        rm -rf "$GDSTK_DIR"
    fi
    if [ "$SHIPS_GDSTK" = 1 ] && [ ! -f "$GDSTK_WORKER" ]; then
        NO_GDSTK="${NO_GDSTK} ${ARCH}"
    fi
    if [ -f "$GDSTK_WORKER" ]; then
        GDSTK_SMOKE=(--gdstk "$GDSTK_VERSION")
    elif [ "$SHIPS_GDSTK" = 1 ]; then
        GDSTK_SMOKE=(--no-gdstk "built without it; reported at the end")
    else
        GDSTK_SMOKE=(--no-gdstk "not shipped on ${RID}")
    fi

    # -- The command line, run out of THIS publish tree ----------------------
    #
    # THE GATE THAT WAS MISSING FOR 32 RELEASES (brief-automation-13-installed-cli.md). Every release
    # through 1.0.0-beta.32 shipped a circuitRF with no command line at all - on Linux worse than
    # absent, since the .deb's /usr/bin/circuitrf link STARTED THE GUI - while every CLI gate was
    # green, because they all launched src/Cli/bin and never the tree that goes into a package. This
    # runs ${PUBLISH}/circuitRF, which is what both the .deb and the tarball carry, and fails the
    # build unless --version prints the VERSION file, `reference --json` parses, and `serve` answers
    # initialize and tools/list (compared with the tool CATALOGUE, not a count) and exits when stdin
    # closes.
    #
    # The other architecture runs only through qemu's binfmt handler (qemu-user-static). NOT
    # SMOKE-TESTED IS NOT PASSED: that fails the run at the end unless CRF_ALLOW_UNSMOKED=1.
    case "$ARCH" in
        x64)   QEMU_BINFMT=/proc/sys/fs/binfmt_misc/qemu-x86_64  ;;
        arm64) QEMU_BINFMT=/proc/sys/fs/binfmt_misc/qemu-aarch64 ;;
    esac

    CAN_SMOKE=0
    if [ "$ARCH" = "$HOST_ARCH" ] || [ -e "$QEMU_BINFMT" ]; then CAN_SMOKE=1; fi

    if [ "$CAN_SMOKE" = 1 ]; then
        echo "Smoke-testing the command line in ${PUBLISH} ..."
        dotnet "$SMOKE_DLL" "${PUBLISH}/circuitRF" "$CRF_VERSION" "${KERNEL_SMOKE[@]}" "${GDSTK_SMOKE[@]}" || {
            echo "ERROR: the command line in ${PUBLISH} does not answer (see above)."
            echo "       This tree must not be packaged."
            exit 1
        }
    else
        echo "WARNING: NOT SMOKE-TESTED - this ${HOST_ARCH} machine cannot execute a ${ARCH} build."
        echo "         Install qemu-user-static (binfmt) to check it, or build on ${ARCH}."
        UNSMOKED="${UNSMOKED} ${ARCH}"
        # The no-Python check reads files and runs nothing, so it holds for this tree all the same.
        dotnet "$SMOKE_DLL" --no-python "${PUBLISH}/circuitRF" || {
            echo "ERROR: ${PUBLISH} carries a Python environment (see above). This tree must not be packaged."
            exit 1
        }
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
            [ -f "${APPDIR}/osdi-worker" ]   && chmod +x "${APPDIR}/osdi-worker"
            [ -f "${APPDIR}/geometry-kernel/geometry-worker" ] && chmod +x "${APPDIR}/geometry-kernel/geometry-worker"
            [ -f "${APPDIR}/gdstk-kernel/gdstk-worker" ] && chmod +x "${APPDIR}/gdstk-kernel/gdstk-worker"
            # The uninstaller rides INSIDE the version directory too (brief-em3d-25): the updater installs
            # only app-<ver>/ out of this archive, so a copy beside it never reaches an updated install,
            # and the documented uninstall is current/install.sh --uninstall.
            cp "${HERE}/install.sh" "${APPDIR}/install.sh"
            chmod +x "${APPDIR}/install.sh"

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

        appimage)
            # -- The AppDir (brief APPIMG R-appimg-1) -------------------------
            #
            # The publish tree goes under usr/lib/circuitrf/ unchanged, so the AppImage carries exactly
            # the files the tarball does. AppRun hands EVERY argument through: the executable is the
            # CLI as well as the GUI (AUT-13), so `./circuitRF-<ver>-linux-x64.AppImage sparam pad.cnl`
            # works as it does from the tarball.
            case "$ARCH" in
                x64)   IMG_ARCH=x86_64  ;;
                arm64) IMG_ARCH=aarch64 ;;
            esac
            RUNTIME="$(appimage_pinned "RUNTIME_$(echo "$ARCH" | tr '[:lower:]' '[:upper:]')")" || exit 1

            IMG="${TMPROOT}/${ARCH}-AppDir"
            rm -rf "$IMG"
            LIB="${IMG}/usr/lib/circuitrf"
            mkdir -p "$LIB" "${IMG}/usr/share/mime/packages" "${IMG}/usr/share/icons/hicolor/512x512/apps"
            cp -a "${PUBLISH}/." "$LIB/"
            chmod +x "${LIB}/circuitRF" 2>/dev/null || true
            [ -f "${LIB}/senior_worker" ] && chmod +x "${LIB}/senior_worker"
            [ -f "${LIB}/osdi-worker" ]   && chmod +x "${LIB}/osdi-worker"
            [ -f "${LIB}/geometry-kernel/geometry-worker" ] && chmod +x "${LIB}/geometry-kernel/geometry-worker"
            [ -f "${LIB}/gdstk-kernel/gdstk-worker" ] && chmod +x "${LIB}/gdstk-kernel/gdstk-worker"

            cat > "${IMG}/AppRun" <<'APPRUN'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/lib/circuitrf/circuitRF" "$@"
APPRUN
            chmod +x "${IMG}/AppRun"

            # The desktop entry is DERIVED from the .deb's, with only Exec= rewritten (the image puts
            # nothing on PATH, so it names the program, which AppRun is). A change to the name, the
            # categories or the MIME list therefore reaches every kind; menu-integration tools read
            # this copy out of the image.
            sed 's|^Exec=.*|Exec=circuitRF %F|' "${HERE}/circuitrf.desktop" > "${IMG}/circuitrf.desktop"
            cp "${HERE}/icons/circuitrf.png" "${IMG}/circuitrf.png"
            ln -s circuitrf.png "${IMG}/.DirIcon"
            cp "${HERE}/circuitrf-mime.xml"  "${IMG}/usr/share/mime/packages/circuitrf.xml"
            cp "${HERE}/icons/circuitrf.png" "${IMG}/usr/share/icons/hicolor/512x512/apps/circuitrf.png"

            OUT="${DIST}/circuitRF-${CRF_VERSION}-linux-${ARCH}.AppImage"
            # The .zsync too: one left from an earlier run would describe a different file.
            rm -f "$OUT" "${OUT}.zsync"

            # The update information AppImageUpdate reads out of the image (AppImageSpec,
            # gh-releases-zsync): this repository's releases, the channel's tag, and the .zsync of the
            # SAME architecture by a wildcard over the version - which is how one image finds the next.
            UPDATE_ARGS=()
            if [ -n "$ZSYNCMAKE" ]; then
                UPDATE_INFO="gh-releases-zsync|${APPIMAGE_UPDATE_GITHUB%%/*}|${APPIMAGE_UPDATE_GITHUB#*/}|${APPIMAGE_UPDATE_TAG}|circuitRF-*-linux-${ARCH}.AppImage.zsync"
                UPDATE_ARGS=(-u "$UPDATE_INFO")
            fi

            # The HOST's appimagetool with the TARGET's runtime, which is what cross-packs the other
            # architecture; ARCH tells the tool which one it is packing. Extract-and-run: no FUSE.
            # Run from the scratch directory: given -u, appimagetool runs zsyncmake itself with no
            # output path, which writes a .zsync into the WORKING directory. That copy is discarded;
            # the one shipped is written below, with its path stated, after the smoke test.
            echo "Packing ${OUT}..."
            ( cd "$TMPROOT" && ARCH="$IMG_ARCH" APPIMAGE_EXTRACT_AND_RUN=1 "$APPIMAGETOOL" --no-appstream \
                ${UPDATE_ARGS[@]+"${UPDATE_ARGS[@]}"} \
                --comp "$APPIMAGE_COMPRESSION" --runtime-file "$RUNTIME" "$IMG" "$OUT" )
            rm -rf "$IMG"
            rm -f "${TMPROOT}/$(basename "$OUT").zsync"
            chmod +x "$OUT"

            # The update information must BE in the image: appimagetool refuses a malformed string but
            # says nothing of a runtime with no room for one. Read as bytes, so it works for either
            # architecture on either build machine. A COUNT, not grep -q: -q stops at the first match,
            # head then dies of SIGPIPE, and under pipefail a found string would read as missing.
            if [ -n "$ZSYNCMAKE" ] && [ "$(head -c 4194304 "$OUT" | grep -acF "$UPDATE_INFO" || true)" = 0 ]; then
                echo "ERROR: ${OUT} does not carry its update information (${UPDATE_INFO})." >&2
                rm -f "$OUT"
                exit 1
            fi

            # -- The AppImage itself, smoke-tested (R-appimg-3) ---------------
            #
            # The same five checks the publish tree passed, run against the FILE: that is what proves
            # AppRun and the argument pass-through. CliSmoke runs it with APPIMAGE_EXTRACT_AND_RUN=1 and
            # finds the workers inside the image. An architecture this machine cannot execute was
            # already counted as NOT SMOKE-TESTED above.
            if [ "$CAN_SMOKE" = 1 ]; then
                echo "Smoke-testing ${OUT} ..."
                APPIMAGE_EXTRACT_AND_RUN=1 dotnet "$SMOKE_DLL" "$OUT" "$CRF_VERSION" "${KERNEL_SMOKE[@]}" "${GDSTK_SMOKE[@]}" || {
                    echo "ERROR: the command line in ${OUT} does not answer (see above)."
                    echo "       This image must not be shipped; it has been deleted."
                    rm -f "$OUT"
                    exit 1
                }
            fi

            # -- The .zsync AppImageUpdate reads (written last, from the image that ships) --------
            #
            # -u names the AppImage relative to the .zsync, which is where a GitHub release puts both.
            if [ -n "$ZSYNCMAKE" ]; then
                "$ZSYNCMAKE" -u "$(basename "$OUT")" -o "${OUT}.zsync" "$OUT"
                echo "OK  ${OUT}.zsync"
                ZSYNCED+=("${OUT}.zsync")
            fi

            echo "OK  ${OUT}"
            echo "    Run with:  chmod +x $(basename "$OUT") && ./$(basename "$OUT")"
            BUILT+=("$OUT")
            ;;
        esac
    done
done

echo ""
echo "=== Done. ${#BUILT[@]} file(s) in ${DIST}"
# ${BUILT[@]} on an EMPTY array is an unbound-variable error under `set -u` on bash 3.2, which is
# still what macOS ships; the +"..." form is the portable spelling and costs nothing here.
for f in ${BUILT[@]+"${BUILT[@]}"}; do echo "    $(basename "$f")"; done
for f in ${ZSYNCED[@]+"${ZSYNCED[@]}"}; do echo "    $(basename "$f")   (upload with the release)"; done

# A full run ships six files and a release needs every one of them. Stated here rather than left to
# be noticed, because the failure this guards against is silent: a missing tarball stops Linux
# updates with no error anywhere, and a missing AppImage stops its Download line the same way.
FULL_RUN=0
case "${ARCH_ARG}:${KIND_ARG}" in both:both|both:all|all:both|all:all) FULL_RUN=1 ;; esac
if [ "$FULL_RUN" = 1 ] && [ "${#BUILT[@]}" -ne 6 ]; then
    echo ""
    echo "WARNING: expected 6 files for a full run, got ${#BUILT[@]}."
fi
# And the two .zsync files beside the AppImages, unless CRF_ALLOW_NO_ZSYNC=1 said there would be none.
if [ "$FULL_RUN" = 1 ] && [ -n "${ZSYNCMAKE:-}" ] && [ "${#ZSYNCED[@]}" -ne 2 ]; then
    echo ""
    echo "WARNING: expected 2 .zsync files for a full run, got ${#ZSYNCED[@]}."
fi

# Reported after the packages exist, so nothing built is thrown away - but the run still fails.
UNSMOKED_FAIL=0
if [ -n "$UNSMOKED" ]; then
    echo ""
    echo "NOT SMOKE-TESTED:${UNSMOKED}. Nothing has shown that their command line answers, which is"
    echo "  exactly what shipped broken for 32 releases. Install qemu-user-static and run again, or"
    echo "  set CRF_ALLOW_UNSMOKED=1 to accept that knowingly."
    [ "${CRF_ALLOW_UNSMOKED:-}" = 1 ] || UNSMOKED_FAIL=1
fi

NO_KERNEL_FAIL=0
if [ -n "$NO_KERNEL" ]; then
    echo ""
    echo "NO GEOMETRY KERNEL in:${NO_KERNEL}. Those packages have no booleans, fillets, chamfers or STEP"
    echo "  import and export, and a .c3d using any of them is refused on open."
    if [ "${CRF_ALLOW_NO_KERNEL:-}" = 1 ]; then
        echo "  CRF_ALLOW_NO_KERNEL=1 says that is intended."
    else
        echo "  Build it and run this again - tools/geometry-worker/build.sh --rid linux-<arch> (needs cmake,"
        echo "  make and a C++ compiler, and for the other architecture crossbuild-essential-amd64 or -arm64;"
        echo "  minutes per architecture, once) - or set CRF_ALLOW_NO_KERNEL=1 to ship without it knowingly."
        NO_KERNEL_FAIL=1
    fi
fi

NO_GDSTK_FAIL=0
if [ -n "$NO_GDSTK" ]; then
    echo ""
    echo "NO GDSTK WORKER in:${NO_GDSTK}. Those packages have no OASIS import or export and no GDSII (gdstk)"
    echo "  route; \`convert\` refuses the oasis format there."
    if [ "${CRF_ALLOW_NO_GDSTK:-}" = 1 ]; then
        echo "  CRF_ALLOW_NO_GDSTK=1 says that is intended."
    else
        echo "  Build it and run this again - tools/gdstk-worker/build.sh --rid linux-<arch> (needs cmake and a"
        echo "  C++ compiler, and for the other architecture crossbuild-essential-amd64 or -arm64; well under a"
        echo "  minute per architecture) - or set CRF_ALLOW_NO_GDSTK=1 to ship without it knowingly."
        NO_GDSTK_FAIL=1
    fi
fi

# Whether these artifacts can ever be installed as an AUTOMATIC UPDATE is decided by the release key
# compiled into the binary, not by anything this script did - so it is stated here, where someone is
# already reading the output, rather than discovered when a published release reaches nobody.
source "${ROOT}/packaging/signing-status.sh"
crf_report_release_key "${ROOT}/src/Ui/Updates/ReleaseKeys.cs"

[ "$UNSMOKED_FAIL" = 1 ] && exit 1
[ "$NO_KERNEL_FAIL" = 1 ] && exit 1
[ "$NO_GDSTK_FAIL" = 1 ] && exit 1
exit 0
