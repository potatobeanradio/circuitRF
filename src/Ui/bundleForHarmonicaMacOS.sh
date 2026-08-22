#!/bin/bash

# ── harmonicaRF macOS App Bundle Script ───────────────────────────────────────
#
# The standalone harmonicaRF binary (docs/design/harmonicarf.md §3.1). It is the SAME assembly as
# circuitRF built with a different Main — `-p:CrfApp=harmonica` selects the StartupObject and the
# macOS bundle identity — so this script is bundleForMacOS.sh with a different app name, plist,
# icon and bundle id, and deliberately nothing else.
#
# THE PUBLISH COMMAND IS RECORDED HERE, IN THE REPOSITORY, NOT IN A SHELL HISTORY (M3's own gate).
# Run from src/Ui/:
#   chmod +x bundleForHarmonicaMacOS.sh && ./bundleForHarmonicaMacOS.sh
#
# For a non-macOS RID the publish line alone is the whole of it:
#   dotnet publish -c Release -r win-x64   --self-contained -p:CrfApp=harmonica
#   dotnet publish -c Release -r linux-x64 --self-contained -p:CrfApp=harmonica

APP_NAME="harmonicaRF"
EXECUTABLE_NAME="harmonicaRF"         # the renamed host - see CircuitRF.Ui.csproj, CrfRenameApphost
# The version comes from the repo-root VERSION file — the one place it is written. It is
# STAMPED into the bundled Info.plist below, so the .app can never report a version the
# release does not carry (the plist's own value is only a placeholder).
source "$(dirname "${BASH_SOURCE[0]}")/../../packaging/version.sh"
VERSION="$CRF_VERSION"
BUNDLE_ID="com.circuitRF.harmonicaRF"
TARGET_FRAMEWORK="net10.0"
# ── Which Mac this bundle is for ──────────────────────────────────────────────
#
# Apple Silicon (osx-arm64) or Intel (osx-x64). CRF_RID names it; the default is THE MACHINE DOING
# THE BUILDING, and that default is a statement about what can work rather than a convenience.
#
# The .NET side of this bundle cross-publishes to either RID happily. Everything else in it does
# not: the helper programs that land beside the assemblies (crf-vmhost, osdi-worker) are compiled
# by their own native toolchains for the host's own architecture, and the Linux VM image is built
# for the guest THIS host's Virtualization.framework can run — an aarch64 guest on Apple Silicon,
# an x86-64 one on Intel. So a cross-architecture bundle is a working application with a set of
# helpers that cannot load a single compiled device model. Build the Intel .dmg on an Intel Mac.
#
# packaging/macos/build-dmg.sh sets CRF_RID and checks the result; setting it by hand here is for
# producing a .NET-only bundle deliberately.
if [ -z "${CRF_RID:-}" ]; then
    case "$(uname -m)" in
        arm64)  CRF_RID="osx-arm64" ;;
        x86_64) CRF_RID="osx-x64"   ;;
        *) echo "❌ Unsupported macOS architecture: $(uname -m)"; exit 1 ;;
    esac
fi
RID="$CRF_RID"
CUSTOM_PLIST="./Assets/macOS/Harmonica-Info.plist"
ENTITLEMENTS="./Assets/macOS/Entitlements.plist"
ICON_SVG="./Assets/artwork/harmonicaRF-app-icon.svg"
ICON_ICNS="./Assets/harmonicaRFIcon.icns"

PUBLISH_DIR="./bin/Release/${TARGET_FRAMEWORK}/${RID}/publish"
BUNDLE_DIR="./bin/Release/${TARGET_FRAMEWORK}/${RID}/${APP_NAME}.app"
CONTENTS_DIR="${BUNDLE_DIR}/Contents"
MAC_OS_DIR="${CONTENTS_DIR}/MacOS"
RESOURCES_DIR="${CONTENTS_DIR}/Resources"

echo "🚀 Starting macOS bundling for harmonicaRF ${VERSION}..."

if [ ! -f "$CUSTOM_PLIST" ]; then
    echo "❌ Info.plist not found at: $CUSTOM_PLIST"
    exit 1
fi

if [ ! -f "$ENTITLEMENTS" ]; then
    echo "❌ Entitlements.plist not found at: $ENTITLEMENTS"
    exit 1
fi

# R-h8-9's three-place trap: CFBundleIdentifier lives in the plist, BUNDLE_ID lives here, and
# codesign is given the entitlements separately. Nothing derives one from another, so this is the
# one place the first two can be compared — and a mismatch is checked rather than described.
PLIST_ID=$(/usr/libexec/PlistBuddy -c "Print :CFBundleIdentifier" "$CUSTOM_PLIST" 2>/dev/null)
if [ "$PLIST_ID" != "$BUNDLE_ID" ]; then
    echo "❌ Bundle id mismatch: this script says '${BUNDLE_ID}', ${CUSTOM_PLIST} says '${PLIST_ID}'."
    echo "   They must agree — macOS keys Launch Services, the Dock and quarantine off the plist's."
    exit 1
fi

# A second guard for the failure that actually costs something: two bundles claiming ONE identifier.
CIRCUITRF_ID=$(/usr/libexec/PlistBuddy -c "Print :CFBundleIdentifier" "./Assets/macOS/Info.plist" 2>/dev/null)
if [ "$PLIST_ID" == "$CIRCUITRF_ID" ]; then
    echo "❌ harmonicaRF and circuitRF both claim '${PLIST_ID}'. macOS will treat them as one app."
    exit 1
fi

echo "📦 Publishing .NET application (CrfApp=harmonica)..."
dotnet publish -r $RID -c Release --self-contained -p:CrfApp=harmonica
if [ $? -ne 0 ]; then echo "❌ Publish failed."; exit 1; fi

if [ -d "$BUNDLE_DIR" ]; then
    echo "🧹 Removing old bundle..."
    rm -rf "$BUNDLE_DIR"
fi

echo "📁 Creating ${APP_NAME}.app structure..."
mkdir -p "$MAC_OS_DIR" "$RESOURCES_DIR"

echo "🚚 Copying published files..."
cp -R "${PUBLISH_DIR}/." "$MAC_OS_DIR/"
chmod +x "${MAC_OS_DIR}/${EXECUTABLE_NAME}"

echo "📄 Copying Info.plist..."
cp "$CUSTOM_PLIST" "${CONTENTS_DIR}/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString ${CRF_VERSION}" "${CONTENTS_DIR}/Info.plist"
# CFBundleVersion must be purely numeric, so it gets the version's numeric head.
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion ${CRF_VERSION_CORE}" "${CONTENTS_DIR}/Info.plist"

# The icon is BUILT from the committed SVG rather than committed as a binary — the repository holds
# circuitRF's brand artwork as SVG for the same reason. Warn-and-continue, exactly like circuitRF's
# own script: a missing icon must never be why somebody cannot produce a bundle.
if [ ! -f "$ICON_ICNS" ] && [ -f "$ICON_SVG" ]; then
    if command -v rsvg-convert >/dev/null 2>&1 || command -v qlmanage >/dev/null 2>&1; then
        echo "🎨 Building ${ICON_ICNS} from ${ICON_SVG}..."
        ICONSET=$(mktemp -d)/harmonicaRF.iconset
        mkdir -p "$ICONSET"
        for SZ in 16 32 64 128 256 512 1024; do
            if command -v rsvg-convert >/dev/null 2>&1; then
                rsvg-convert -w $SZ -h $SZ "$ICON_SVG" -o "${ICONSET}/icon_${SZ}x${SZ}.png" 2>/dev/null
            else
                qlmanage -t -s $SZ -o "$ICONSET" "$ICON_SVG" >/dev/null 2>&1
                mv "${ICONSET}/$(basename "$ICON_SVG").png" "${ICONSET}/icon_${SZ}x${SZ}.png" 2>/dev/null
            fi
        done
        # iconutil wants the @2x names too; the plain set is enough for a usable icon.
        iconutil -c icns "$ICONSET" -o "$ICON_ICNS" 2>/dev/null \
            || echo "⚠️  iconutil could not build the icon — continuing without one."
    else
        echo "⚠️  No SVG rasteriser (rsvg-convert / qlmanage) — continuing without an icon."
    fi
fi

if [ -f "$ICON_ICNS" ]; then
    echo "🎨 Copying icon..."
    cp "$ICON_ICNS" "${RESOURCES_DIR}/harmonicaRFIcon.icns"
else
    echo "⚠️  harmonicaRFIcon.icns not found — application will use the default icon."
fi

echo "🔐 Code signing (ad-hoc)..."
codesign --force --deep --sign "-" --entitlements "$ENTITLEMENTS" --timestamp=none "$BUNDLE_DIR"
if [ $? -ne 0 ]; then echo "❌ Code signing failed."; exit 1; fi

# ── crf-vmhost's OWN entitlement, and why this runs AFTER the bundle is signed ────────────────
#
# `--deep` re-signs every nested executable with the entitlements given HERE, and circuitRF's are
# not crf-vmhost's. So the deep pass silently replaced com.apple.security.virtualization with
# com.apple.security.files.user-selected.read-write, and the packaged VM host — correctly signed by
# tools/macos-vmhost/build.sh minutes earlier — arrived unable to create a virtual machine:
#
#     the virtual machine configuration was rejected: Invalid virtual machine configuration.
#     The process doesn't have the "com.apple.security.virtualization" entitlement.
#
# Nothing about the build said so. It only shows in a bundle that has been through this script, and
# only when a compiled device model is actually run — so `dotnet run` worked and every .dmg shipped
# a VM host that could not start. (Measured 2026-08-22 with `codesign -d --entitlements`.)
#
# Inside-out is the fix, and the order is the whole of it: sign the nested binary with its own
# entitlements, then RE-SEAL the bundle without `--deep` so the outer signature records the new
# cdhash and does not touch the inner binary again.
VMHOST="${MAC_OS_DIR}/crf-vmhost"
VMHOST_ENTITLEMENTS="../../tools/macos-vmhost/crf-vmhost.entitlements"
if [ -f "$VMHOST" ] && [ -f "$VMHOST_ENTITLEMENTS" ]; then
    echo "🔐 Re-signing crf-vmhost with its virtualization entitlement..."
    codesign --force --sign "-" --entitlements "$VMHOST_ENTITLEMENTS" \
             --options runtime --timestamp=none "$VMHOST" || {
        echo "❌ Could not sign crf-vmhost."; exit 1; }

    codesign --force --sign "-" --entitlements "$ENTITLEMENTS" --timestamp=none "$BUNDLE_DIR" || {
        echo "❌ Could not re-seal the bundle."; exit 1; }
fi

echo ""
echo "✅ Bundle created: ${BUNDLE_DIR}"
echo "   To distribute: replace '-' with your Developer ID Application certificate."
