#!/usr/bin/env bash
# Tools/Packaging/build-mac-app.sh
#
# Phase X.6 / Slice 6.3 — macOS .app bundle build. Wraps the
# FracturingFog.App self-contained publish into a double-click-launchable
# .app on macOS Sonoma+ (Apple Silicon and Intel).
#
# Prereqs (install on the build host):
#   * dotnet 10 SDK    — `dotnet --version` reports 10.x
#   * plutil           — ships with macOS by default
#   * iconutil         — ships with macOS by default (used for the optional
#                        .icns conversion when Resources/macOS/icon.iconset
#                        is present)
#
# Code-signing + notarisation are NOT performed here; see the trailing
# comment for the manual steps once an Apple Developer cert is on hand.
#
# Usage:
#   Tools/Packaging/build-mac-app.sh [rid]
#
#     rid : RID to package. Default osx-arm64. Also valid: osx-x64.
#
# Output:
#   dist/FracturingFog.app/

set -euo pipefail

RID="${1:-osx-arm64}"
case "$RID" in
    osx-arm64|osx-x64) ;;
    *)
        echo "build-mac-app.sh: unsupported RID '$RID'." >&2
        echo "  expected: osx-arm64 | osx-x64" >&2
        exit 2
        ;;
esac

REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
APP_CSPROJ="$REPO_ROOT/FracturingFog.App/FracturingFog.App.csproj"
PUBLISH_DIR="$REPO_ROOT/FracturingFog.App/publish/$RID"
APP_BUNDLE="$REPO_ROOT/dist/FracturingFog.app"
INFO_PLIST_SRC="$REPO_ROOT/Resources/macOS/Info.plist"
ICONSET_SRC="$REPO_ROOT/Resources/macOS/icon.iconset"
ICNS_SRC="$REPO_ROOT/Resources/macOS/FracturingFog.icns"

# ── 1. Publish the App self-contained ─────────────────────────────────────
# S-X3 (2026-06-23) — pass -f explicitly. FracturingFog.App multi-targets
# net10.0;net10.0-windows so the Win leg can ProjectReference FracturingFog.Win.
# MSBuild's CrossTargeting Publish target refuses without an explicit TFM
# (NETSDK1129). macOS RIDs always publish the net10.0 leg.
echo "==> Publishing $APP_CSPROJ for $RID"
dotnet publish "$APP_CSPROJ" \
    -c Release \
    -f net10.0 \
    -p:PublishProfile="$RID"

if [ ! -d "$PUBLISH_DIR" ]; then
    echo "build-mac-app.sh: publish dir '$PUBLISH_DIR' not found after publish." >&2
    exit 1
fi

# ── 2. Lay out the .app bundle ────────────────────────────────────────────
echo "==> Laying out $APP_BUNDLE"
rm -rf "$APP_BUNDLE"
mkdir -p "$APP_BUNDLE/Contents/MacOS" \
         "$APP_BUNDLE/Contents/Resources"

cp -R "$PUBLISH_DIR/." "$APP_BUNDLE/Contents/MacOS/"
chmod +x "$APP_BUNDLE/Contents/MacOS/FracturingFog.App"

# Info.plist: prefer the version-controlled template; emit a stub if the
# user has not added one yet so the .app still launches.
if [ -f "$INFO_PLIST_SRC" ]; then
    cp "$INFO_PLIST_SRC" "$APP_BUNDLE/Contents/Info.plist"
else
    cat > "$APP_BUNDLE/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>Fracturing Fog</string>
    <key>CFBundleDisplayName</key>
    <string>Fracturing Fog</string>
    <key>CFBundleIdentifier</key>
    <string>io.github.alonebutunsober.fracturingfog</string>
    <key>CFBundleVersion</key>
    <string>1.0.0</string>
    <key>CFBundleShortVersionString</key>
    <string>1.0</string>
    <key>CFBundleExecutable</key>
    <string>FracturingFog.App</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleSignature</key>
    <string>????</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>LSMinimumSystemVersion</key>
    <string>11.0</string>
</dict>
</plist>
PLIST
fi

# PkgInfo: 8-byte legacy file. Optional but expected by some launchers.
printf 'APPL????' > "$APP_BUNDLE/Contents/PkgInfo"

# ── 3. Icon ───────────────────────────────────────────────────────────────
# Preference: hand-rolled .icns if checked in; else convert an .iconset
# folder (Resources/macOS/icon.iconset/icon_16x16.png, etc.) via iconutil;
# else skip the icon and warn (Finder draws a generic application icon).
if [ -f "$ICNS_SRC" ]; then
    cp "$ICNS_SRC" "$APP_BUNDLE/Contents/Resources/FracturingFog.icns"
elif [ -d "$ICONSET_SRC" ] && command -v iconutil >/dev/null 2>&1; then
    iconutil -c icns "$ICONSET_SRC" -o "$APP_BUNDLE/Contents/Resources/FracturingFog.icns"
else
    echo "build-mac-app.sh: WARN — no Resources/macOS/FracturingFog.icns or icon.iconset." >&2
    echo "                  Bundle will use Finder's generic application icon." >&2
fi

# ── 4. Quick sanity ───────────────────────────────────────────────────────
if command -v plutil >/dev/null 2>&1; then
    plutil -lint "$APP_BUNDLE/Contents/Info.plist" >/dev/null
    echo "==> Info.plist lints clean."
fi

echo "==> Done. $APP_BUNDLE"
echo ""
echo "Manual code-signing + notarisation (run once Apple Developer cert is on hand):"
echo "  codesign --deep --force --options runtime --timestamp \\"
echo "           --sign 'Developer ID Application: <name> (<TEAMID>)' \\"
echo "           '$APP_BUNDLE'"
echo "  ditto -c -k --keepParent '$APP_BUNDLE' dist/FracturingFog.zip"
echo "  xcrun notarytool submit dist/FracturingFog.zip \\"
echo "        --apple-id <appleid> --team-id <TEAMID> --password <app-specific> --wait"
echo "  xcrun stapler staple '$APP_BUNDLE'"
