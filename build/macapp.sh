#!/usr/bin/env bash
#
# Wraps a published macOS build into a .app bundle.
#
# A bare executable gets the generic "exec" icon in the Dock and no name;
# Avalonia's window icon does not apply on macOS at all. The Dock icon,
# the name and the About panel all come from the bundle's Info.plist.
#
# Usage:  build/macapp.sh <published-dir> <out-dir> [version]
#   published-dir  output of `dotnet publish -r osx-*` (must contain BmwebFlasher)
#   out-dir        where BmwebFlasher.app is created (replaced if present)

set -euo pipefail

SRC="$1"; OUT="$2"; VERSION="${3:-0.0.0}"
HERE="$(cd "$(dirname "$0")/.." && pwd)"
APP="$OUT/BmwebFlasher.app"

[ -x "$SRC/BmwebFlasher" ] || { echo "no BmwebFlasher executable in $SRC" >&2; exit 1; }

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$SRC"/. "$APP/Contents/MacOS/"
cp "$HERE/src/BmwebFlasher/Assets/icon.icns" "$APP/Contents/Resources/icon.icns"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>            <string>BMWeb Flasher</string>
  <key>CFBundleDisplayName</key>     <string>BMWeb Flasher</string>
  <key>CFBundleIdentifier</key>      <string>ink.danner.bmweb.flasher</string>
  <key>CFBundleVersion</key>         <string>$VERSION</string>
  <key>CFBundleShortVersionString</key> <string>$VERSION</string>
  <key>CFBundlePackageType</key>     <string>APPL</string>
  <key>CFBundleExecutable</key>      <string>BmwebFlasher</string>
  <key>CFBundleIconFile</key>        <string>icon</string>
  <key>LSMinimumSystemVersion</key>  <string>11.0</string>
  <key>NSHighResolutionCapable</key> <true/>
  <key>NSHumanReadableCopyright</key><string>BMWeb Flasher</string>
</dict>
</plist>
PLIST

# Ad-hoc signature so Gatekeeper treats the bundle as a unit; users still
# clear quarantine on first run, as before.
codesign --force --deep --sign - "$APP" >/dev/null 2>&1 || true
echo "$APP"
