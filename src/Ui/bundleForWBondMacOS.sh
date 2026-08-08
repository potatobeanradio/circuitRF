#!/bin/bash

# ── wBond macOS App Bundle Script ─────────────────────────────────────────────
#
# The standalone wBond binary (docs/design/wbond.md §11, WB38). It is the SAME assembly as circuitRF
# built with a different Main — `-p:CrfApp=wbond` selects the StartupObject and the macOS bundle
# identity — so this script is bundleForHarmonicaMacOS.sh with a different app name, plist, icon and
# bundle id, and deliberately nothing else.
#
# THE PUBLISH COMMAND IS RECORDED HERE, IN THE REPOSITORY, NOT IN A SHELL HISTORY (M4's own gate).
# Run from src/Ui/:
#   chmod +x bundleForWBondMacOS.sh && ./bundleForWBondMacOS.sh
#
# For a non-macOS RID the publish line alone is the whole of it:
#   dotnet publish -c Release -r win-x64   --self-contained -p:CrfApp=wbond
#   dotnet publish -c Release -r linux-x64 --self-contained -p:CrfApp=wbond
#
# §5 question 4, answered the same way H8 answered it: a macOS bundle script plus the two publish
# one-liners above, and no installer for Windows or Linux. Building an MSI and an AppImage is its own
# piece of work with its own signing story, and nothing in this phase needs one.

APP_NAME="wBond"
EXECUTABLE_NAME="CircuitRF.Ui"        # shared assembly name — see WBond-Info.plist for why (WB40)
VERSION="0.1.0"
BUNDLE_ID="com.circuitRF.wBond"
TARGET_FRAMEWORK="net10.0"
RID="osx-arm64"                        # change to osx-x64 for Intel Macs
CUSTOM_PLIST="./Assets/macOS/WBond-Info.plist"
ENTITLEMENTS="./Assets/macOS/Entitlements.plist"
ICON_SVG="./Assets/artwork/wBond-app-icon.svg"
ICON_ICNS="./Assets/wBondIcon.icns"

PUBLISH_DIR="./bin/Release/${TARGET_FRAMEWORK}/${RID}/publish"
BUNDLE_DIR="./bin/Release/${TARGET_FRAMEWORK}/${RID}/${APP_NAME}.app"
CONTENTS_DIR="${BUNDLE_DIR}/Contents"
MAC_OS_DIR="${CONTENTS_DIR}/MacOS"
RESOURCES_DIR="${CONTENTS_DIR}/Resources"

echo "🚀 Starting macOS bundling for wBond ${VERSION}..."

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
# There are THREE applications in this assembly now, so both siblings are checked rather than one.
for SIBLING in "./Assets/macOS/Info.plist" "./Assets/macOS/Harmonica-Info.plist"; do
    SIBLING_ID=$(/usr/libexec/PlistBuddy -c "Print :CFBundleIdentifier" "$SIBLING" 2>/dev/null)
    if [ "$PLIST_ID" == "$SIBLING_ID" ]; then
        echo "❌ wBond and ${SIBLING} both claim '${PLIST_ID}'. macOS will treat them as one app."
        exit 1
    fi
done

echo "📦 Publishing .NET application (CrfApp=wbond)..."
dotnet publish -r $RID -c Release --self-contained -p:CrfApp=wbond
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

# The icon is BUILT from the committed SVG rather than committed as a binary — the repository holds
# circuitRF's brand artwork as SVG for the same reason. Warn-and-continue, exactly like circuitRF's
# own script: a missing icon must never be why somebody cannot produce a bundle.
if [ ! -f "$ICON_ICNS" ] && [ -f "$ICON_SVG" ]; then
    if command -v rsvg-convert >/dev/null 2>&1 || command -v qlmanage >/dev/null 2>&1; then
        echo "🎨 Building ${ICON_ICNS} from ${ICON_SVG}..."
        ICONSET=$(mktemp -d)/wBond.iconset
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
    cp "$ICON_ICNS" "${RESOURCES_DIR}/wBondIcon.icns"
else
    echo "⚠️  wBondIcon.icns not found — application will use the default icon."
fi

echo "🔐 Code signing (ad-hoc)..."
codesign --force --deep --sign "-" --entitlements "$ENTITLEMENTS" --timestamp=none "$BUNDLE_DIR"
if [ $? -ne 0 ]; then echo "❌ Code signing failed."; exit 1; fi

echo ""
echo "✅ Bundle created: ${BUNDLE_DIR}"
echo "   To distribute: replace '-' with your Developer ID Application certificate."
