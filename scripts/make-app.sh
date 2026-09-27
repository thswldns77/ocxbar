#!/bin/bash
# Build OCXBar.app (menu-bar only, ad-hoc signed) with plain SwiftPM - no Xcode project needed.
#   scripts/make-app.sh            -> build/OCXBar.app
#   scripts/make-app.sh --install  -> also copies it to ~/Applications and launches it
set -euo pipefail
cd "$(dirname "$0")/.."

swift build -c release
BIN="$(swift build -c release --show-bin-path)/OCXBar"

APP="build/OCXBar.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
cp "$BIN" "$APP/Contents/MacOS/OCXBar"
cp Resources/Info.plist "$APP/Contents/Info.plist"
codesign --force --sign - "$APP" >/dev/null
echo "Built $(pwd)/$APP"

if [[ "${1:-}" == "--install" ]]; then
    DEST="$HOME/Applications"
    mkdir -p "$DEST"
    pkill -x OCXBar 2>/dev/null || true
    rm -rf "$DEST/OCXBar.app"
    cp -R "$APP" "$DEST/"
    open "$DEST/OCXBar.app"
    echo "Installed and launched $DEST/OCXBar.app"
fi

