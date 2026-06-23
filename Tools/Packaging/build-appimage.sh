#!/usr/bin/env bash
# Tools/Packaging/build-appimage.sh
#
# Phase X.6 / Slice 6.2 — Linux AppImage build. Wraps the self-contained
# single-file publish from FracturingFog.App into a portable .AppImage so
# users on any glibc-2.27-or-newer distro can download a single file and
# run it without installing dependencies.
#
# Prereqs (install on the build host):
#   * dotnet 10 SDK    — `dotnet --version` reports 10.x
#   * appimagetool     — https://github.com/AppImage/AppImageKit/releases
#                        download appimagetool-x86_64.AppImage, mark exe,
#                        drop on PATH.
#
# Usage:
#   Tools/Packaging/build-appimage.sh [rid]
#
#     rid : RID to package. Default linux-x64. Also valid: linux-arm64.
#
# Output:
#   dist/FracturingFog-<rid>.AppImage
#
# Idempotent: re-running rebuilds AppDir from scratch + replaces the
# AppImage. The publish step is unconditional so a stale single-file
# archive does not silently ship.

set -euo pipefail

RID="${1:-linux-x64}"
case "$RID" in
    linux-x64|linux-arm64) ;;
    *)
        echo "build-appimage.sh: unsupported RID '$RID'." >&2
        echo "  expected: linux-x64 | linux-arm64" >&2
        exit 2
        ;;
esac

REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
APP_CSPROJ="$REPO_ROOT/FracturingFog.App/FracturingFog.App.csproj"
PUBLISH_DIR="$REPO_ROOT/FracturingFog.App/publish/$RID"
APPDIR="$REPO_ROOT/dist/AppDir-$RID"
DIST_DIR="$REPO_ROOT/dist"
APPIMAGE="$DIST_DIR/FracturingFog-$RID.AppImage"

DESKTOP_SRC="$REPO_ROOT/Resources/Linux/FracturingFog.desktop"
ICON_SRC_PNG="$REPO_ROOT/Resources/Linux/FracturingFog.png"
ICON_SRC_FALLBACK="$REPO_ROOT/Resources/FracturingFog.ico"

# ── 1. Publish the App self-contained ─────────────────────────────────────
# S-X3 (2026-06-23) — pass -f explicitly. FracturingFog.App multi-targets
# net10.0;net10.0-windows so the Win leg can ProjectReference FracturingFog.Win.
# MSBuild's CrossTargeting Publish target refuses without an explicit TFM
# (NETSDK1129). Linux RIDs always publish the net10.0 leg; the Windows leg is
# handled by Tools/Packaging/build-mac-app.sh's sibling Windows script (or
# direct dotnet publish -f net10.0-windows).
echo "==> Publishing $APP_CSPROJ for $RID"
dotnet publish "$APP_CSPROJ" \
    -c Release \
    -f net10.0 \
    -p:PublishProfile="$RID"

if [ ! -d "$PUBLISH_DIR" ]; then
    echo "build-appimage.sh: publish dir '$PUBLISH_DIR' not found after publish." >&2
    exit 1
fi

# ── 2. Lay out AppDir ─────────────────────────────────────────────────────
echo "==> Laying out AppDir at $APPDIR"
rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/bin" \
         "$APPDIR/usr/share/applications" \
         "$APPDIR/usr/share/icons/hicolor/256x256/apps"

cp -r "$PUBLISH_DIR/." "$APPDIR/usr/bin/"
chmod +x "$APPDIR/usr/bin/FracturingFog.App"

# Desktop file: prefer the version-controlled template; emit a stub if the
# user has not added one yet so the AppImage still builds.
if [ -f "$DESKTOP_SRC" ]; then
    cp "$DESKTOP_SRC" "$APPDIR/usr/share/applications/FracturingFog.desktop"
else
    cat > "$APPDIR/usr/share/applications/FracturingFog.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=Fracturing Fog
Comment=Mandelbrot set explorer with audio-reactive slideshow
Exec=FracturingFog.App
Icon=FracturingFog
Categories=Graphics;Science;
Terminal=false
EOF
fi
cp "$APPDIR/usr/share/applications/FracturingFog.desktop" "$APPDIR/FracturingFog.desktop"

# Icon: prefer a hand-rolled PNG; fall back to a 1×1 placeholder so
# appimagetool does not reject the AppDir for missing icons during early
# development. Users wanting the real icon drop Resources/Linux/FracturingFog.png.
if [ -f "$ICON_SRC_PNG" ]; then
    cp "$ICON_SRC_PNG" "$APPDIR/usr/share/icons/hicolor/256x256/apps/FracturingFog.png"
elif [ -f "$ICON_SRC_FALLBACK" ]; then
    # `.ico` is not a valid AppImage icon; warn but copy as a placeholder
    # so appimagetool produces output. Replace at the user's leisure.
    echo "build-appimage.sh: WARN — Resources/Linux/FracturingFog.png missing." >&2
    echo "                  Emitting 1x1 placeholder; AppImage will build but" >&2
    echo "                  the icon will not render in Linux launchers." >&2
    printf '\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR\x00\x00\x00\x01\x00\x00\x00\x01\x08\x06\x00\x00\x00\x1f\x15\xc4\x89\x00\x00\x00\rIDAT\x08\x99c\xf8\x0f\x00\x00\x01\x01\x00\x01\x1b\xb6\xee\x56\x00\x00\x00\x00IEND\xaeB`\x82' \
        > "$APPDIR/usr/share/icons/hicolor/256x256/apps/FracturingFog.png"
else
    echo "build-appimage.sh: no icon source available; cannot proceed." >&2
    exit 1
fi
cp "$APPDIR/usr/share/icons/hicolor/256x256/apps/FracturingFog.png" \
   "$APPDIR/FracturingFog.png"

# AppRun: minimal launcher that runs the embedded binary so users can
# double-click the AppImage on most desktop environments.
cat > "$APPDIR/AppRun" <<'EOF'
#!/bin/sh
HERE="$(cd "$(dirname "$0")" && pwd)"
exec "$HERE/usr/bin/FracturingFog.App" "$@"
EOF
chmod +x "$APPDIR/AppRun"

# ── 3. Pack via appimagetool ──────────────────────────────────────────────
if ! command -v appimagetool >/dev/null 2>&1; then
    echo "build-appimage.sh: appimagetool not on PATH." >&2
    echo "  Install it from https://github.com/AppImage/AppImageKit/releases" >&2
    echo "  (download appimagetool-x86_64.AppImage, chmod +x, drop on PATH)" >&2
    exit 1
fi

mkdir -p "$DIST_DIR"
rm -f "$APPIMAGE"

# ARCH env tells appimagetool which arch to embed when the host's natural
# arch does not match the RID (e.g. cross-build linux-arm64 on linux-x64).
case "$RID" in
    linux-x64)   ARCH=x86_64 ;;
    linux-arm64) ARCH=aarch64 ;;
esac
echo "==> Packing AppImage ($ARCH)"
ARCH="$ARCH" appimagetool "$APPDIR" "$APPIMAGE"

echo "==> Done. $APPIMAGE"
