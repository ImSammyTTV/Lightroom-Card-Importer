#!/bin/bash
# Builds CardImporter.app (a menu bar app) from the Swift package. Needs Xcode or the Command Line Tools.
set -euo pipefail
cd "$(dirname "$0")"

# Keep in step with AppVersion in windows/CardImporter.cs. A release tag must match (the release workflow checks).
VERSION="1.1.2"

swift build -c release
BIN="$(swift build -c release --show-bin-path)/CardImporter"

APP="CardImporter.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/CardImporter"

# App icon from Resources/icon.png
ICONSET="$(mktemp -d)/AppIcon.iconset"
mkdir -p "$ICONSET"
for s in 16 32 128 256 512; do
  sips -z $s $s Resources/icon.png --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
  sips -z $((s*2)) $((s*2)) Resources/icon.png --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Card Importer</string>
  <key>CFBundleDisplayName</key><string>Card Importer</string>
  <key>CFBundleIdentifier</key><string>com.imsammyttv.cardimporter</string>
  <key>CFBundleExecutable</key><string>CardImporter</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>${VERSION}</string>
  <key>CFBundleVersion</key><string>${VERSION}</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>LSUIElement</key><true/>
  <key>NSRemovableVolumesUsageDescription</key>
  <string>Card Importer reads photos and videos from memory cards and cameras to copy them into your photo library.</string>
</dict>
</plist>
PLIST

# Ad-hoc signature so it launches on Apple silicon without a developer account
codesign --force --deep --sign - "$APP"
echo "Built: $(pwd)/$APP"
