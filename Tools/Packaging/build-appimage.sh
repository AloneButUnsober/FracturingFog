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
#   * python3          — extracts the launcher icon from Resources/FracturingFog.ico
#                        (Tools/Packaging/ico-to-png.py, standard library only).
#   * curl + sha256sum — fetch the pinned DXC the Vulkan compute kernels compile
#                        their shaders with (#1455, Tools/Packaging/fetch-dxc.sh).
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
# #429 — the launcher icon is the Windows exe's icon; its 256x256 frame is a PNG.
ICON_SRC_ICO="$REPO_ROOT/Resources/FracturingFog.ico"
ICO_TO_PNG="$REPO_ROOT/Tools/Packaging/ico-to-png.py"

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

# #1455 — DXC next to the app (usr/bin/dxc/{bin,lib}), where DxcCompiler looks,
# so the GPU compute path works without a Vulkan SDK. DXC publishes Linux builds
# for x86-64 only; the arm64 AppImage falls back to a dxc on PATH.
if [ "$RID" = "linux-x64" ]; then
    echo "==> Bundling DXC"
    bash "$REPO_ROOT/Tools/Packaging/fetch-dxc.sh" "$APPDIR/usr/bin/dxc" >/dev/null
else
    echo "==> No DXC build for $RID: GPU compute needs a dxc on PATH (or DXC_PATH)"
fi

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

# Icon (#429): extract the 256x256 PNG frame from Resources/FracturingFog.ico.
# This used to fall back to a 1x1 placeholder PNG when a hand-made
# Resources/Linux/FracturingFog.png was absent (it always was), so installed
# launchers showed no icon. A missing or unreadable icon now fails the build.
if ! command -v python3 >/dev/null 2>&1; then
    echo "build-appimage.sh: python3 not on PATH (needed to extract the icon)." >&2
    exit 1
fi
python3 "$ICO_TO_PNG" "$ICON_SRC_ICO" \
    "$APPDIR/usr/share/icons/hicolor/256x256/apps/FracturingFog.png"
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
