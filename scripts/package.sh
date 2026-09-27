#!/bin/bash
# Package OCXBar for teammates (Apple Silicon):
#   dist/OCXBar-<version>.zip         app + 설치방법.txt
#   dist/OCXBar-<version>-source.zip  buildable source
set -euo pipefail
cd "$(dirname "$0")/.."

scripts/make-app.sh
VERSION="$(/usr/libexec/PlistBuddy -c 'Print CFBundleShortVersionString' Resources/Info.plist)"
STAGE="build/package/OCXBar-$VERSION"
rm -rf build/package
mkdir -p "$STAGE" dist
cp -R build/OCXBar.app "$STAGE/"
cp docs/INSTALL.txt "$STAGE/설치방법.txt"
xattr -cr "$STAGE"

rm -f "dist/OCXBar-$VERSION.zip" "dist/OCXBar-$VERSION-source.zip"
ditto -c -k --norsrc --noextattr --keepParent "$STAGE" "dist/OCXBar-$VERSION.zip"
zip -qr "dist/OCXBar-$VERSION-source.zip" Package.swift README.md Resources Sources scripts docs .gitignore
echo "dist/OCXBar-$VERSION.zip"
echo "dist/OCXBar-$VERSION-source.zip"
