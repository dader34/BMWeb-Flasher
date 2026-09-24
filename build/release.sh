#!/usr/bin/env bash
#
# Builds the release archives for every platform the app runs on.
#
# Each archive holds one self-contained binary: the .NET runtime and every
# dependency travel with it, so nothing needs installing first. That is also
# why they are large.
#
# Usage:  build/release.sh [version]
# The version defaults to whatever the project declares.

set -euo pipefail

cd "$(dirname "$0")/.."

PROJECT="src/BmwebFlasher/BmwebFlasher.csproj"
OUT="dist"

VERSION="${1:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJECT" | head -1)}"
if [ -z "$VERSION" ]; then
  echo "Could not read a version from $PROJECT, and none was given." >&2
  exit 1
fi

echo "BMWeb Flasher $VERSION"
echo

rm -rf "$OUT"
mkdir -p "$OUT"

build() {
  local rid="$1" label="$2" binary="$3"
  local staging="$OUT/$label"

  echo "building $label"
  dotnet publish "$PROJECT" \
    -c Release -r "$rid" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:DebugType=None \
    -o "$staging" \
    -v q --nologo

  # The debug symbols are not wanted in a release archive.
  rm -f "$staging"/*.pdb

  # macOS gets a real .app bundle: a bare executable shows the generic
  # "exec" icon in the Dock and has no name; the bundle's Info.plist is
  # what carries the icon and the name.
  case "$rid" in
    osx-*)
      build/macapp.sh "$staging" "$OUT/$label-app" "$VERSION" >/dev/null
      rm -rf "$staging"
      mv "$OUT/$label-app" "$staging"
      ;;
  esac

  ( cd "$OUT" && zip -qr "BmwebFlasher-$VERSION-$label.zip" "$label" )
  rm -rf "$staging"

  echo "  $OUT/BmwebFlasher-$VERSION-$label.zip"
}

build osx-arm64 macos-arm64 BmwebFlasher
build osx-x64   macos-x64   BmwebFlasher
build win-x64   windows-x64 BmwebFlasher.exe

echo
echo "done:"
ls -lh "$OUT"/*.zip | awk '{print "  " $9 "  " $5}'
echo
echo "macOS users will have to clear the quarantine flag on first run:"
echo "  xattr -d com.apple.quarantine BmwebFlasher"
