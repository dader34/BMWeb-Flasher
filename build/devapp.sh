#!/usr/bin/env bash
#
# Runs the Debug build as a real .app so the Dock shows the icon and name.
# A bare executable launched from a terminal gets the generic "exec" icon
# whatever the window says. The bundle's executable is a stub that sets the
# runtime roll-forward (open(1) does not pass environment through) and then
# runs the framework-dependent build in place.
#
# Usage:  build/devapp.sh        (builds Debug, wraps it, opens it)

set -euo pipefail
cd "$(dirname "$0")/.."

dotnet build src/BmwebFlasher -c Debug -v q --nologo >/dev/null
BIN="$PWD/src/BmwebFlasher/bin/Debug/net8.0"
APP="/tmp/BmwebFlasher-dev.app"

pkill -f "BmwebFlasher" 2>/dev/null || true
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp src/BmwebFlasher/Assets/icon.icns "$APP/Contents/Resources/icon.icns"

cat > "$APP/Contents/MacOS/BmwebFlasher" <<STUB
#!/bin/sh
export DOTNET_ROLL_FORWARD=LatestMajor
exec "$BIN/BmwebFlasher" "\$@"
STUB
chmod +x "$APP/Contents/MacOS/BmwebFlasher"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>BMWeb Flasher (dev)</string>
  <key>CFBundleDisplayName</key><string>BMWeb Flasher (dev)</string>
  <key>CFBundleIdentifier</key><string>ink.danner.bmweb.flasher.dev</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleExecutable</key><string>BmwebFlasher</string>
  <key>CFBundleIconFile</key><string>icon</string>
  <key>NSHighResolutionCapable</key><true/>
</dict></plist>
PLIST

open "$APP"
echo "$APP"
