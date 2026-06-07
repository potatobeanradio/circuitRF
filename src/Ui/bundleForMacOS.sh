#!/bin/bash

# ── circuitRF macOS App Bundle Script ─────────────────────────────────────────
# Adapted from splotRF/src/bundleForMacOS.sh. Run from src/Ui/:
#   chmod +x bundleForMacOS.sh && ./bundleForMacOS.sh

APP_NAME="circuitRF"
EXECUTABLE_NAME="CircuitRF.Ui"        # binary produced by dotnet publish
VERSION="0.1.0"
BUNDLE_ID="com.circuitRF.circuitRF"
TARGET_FRAMEWORK="net10.0"
RID="osx-arm64"                        # change to osx-x64 for Intel Macs
CUSTOM_PLIST="./Assets/macOS/Info.plist"
ENTITLEMENTS="./Assets/macOS/Entitlements.plist"

PUBLISH_DIR="./bin/Release/${TARGET_FRAMEWORK}/${RID}/publish"
BUNDLE_DIR="./bin/Release/${TARGET_FRAMEWORK}/${RID}/${APP_NAME}.app"
CONTENTS_DIR="${BUNDLE_DIR}/Contents"
MAC_OS_DIR="${CONTENTS_DIR}/MacOS"
RESOURCES_DIR="${CONTENTS_DIR}/Resources"

echo "🚀 Starting macOS bundling for circuitRF ${VERSION}..."

if [ ! -f "$CUSTOM_PLIST" ]; then
    echo "❌ Info.plist not found at: $CUSTOM_PLIST"
    exit 1
fi

if [ ! -f "$ENTITLEMENTS" ]; then
    echo "❌ Entitlements.plist not found at: $ENTITLEMENTS"
    exit 1
fi

echo "📦 Publishing .NET application..."
dotnet publish -r $RID -c Release --self-contained
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

if [ -f "./Assets/circuitRFIcon.icns" ]; then
    echo "🎨 Copying icon..."
    cp "./Assets/circuitRFIcon.icns" "$RESOURCES_DIR/"
else
    echo "⚠️  circuitRFIcon.icns not found — application will use default icon."
fi

echo "🔐 Code signing (ad-hoc)..."
codesign --force --deep --sign "-" --entitlements "$ENTITLEMENTS" --timestamp=none "$BUNDLE_DIR"
if [ $? -ne 0 ]; then echo "❌ Code signing failed."; exit 1; fi

echo ""
echo "✅ Bundle created: ${BUNDLE_DIR}"
echo "   To distribute: replace '-' with your Developer ID Application certificate."
