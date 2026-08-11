#!/bin/sh
# Fracturing Fog — network bootstrap installer.
#
# One-liner:
#   curl -fsSL https://github.com/AloneButUnsober/MandelbrotExplorer/releases/latest/download/web-install.sh | sh
#
# Or pin a release / choose scope via env vars:
#   FF_TAG=v1.0.0 curl -fsSL .../web-install.sh | sh      # specific release
#   FF_SYSTEM=1   curl -fsSL .../web-install.sh | sudo -E sh   # system-wide
#
# What it does:
#   1. Finds the latest (or FF_TAG) GitHub release.
#   2. Downloads the matching install.sh + FracturingFog AppImage.
#   3. Runs install.sh (desktop-menu integration, PATH launcher, icon).
#
# It downloads install.sh and the AppImage from the *same* release, so the
# install logic always matches the binary it installs.

set -eu

REPO="${FF_REPO:-AloneButUnsober/MandelbrotExplorer}"
APP_NAME="FracturingFog"
TAG="${FF_TAG:-latest}"

# --- Pick a downloader --------------------------------------------------------
if command -v curl >/dev/null 2>&1; then
    DL() { curl -fsSL "$1" -o "$2"; }
    DLO() { curl -fsSL "$1"; }
elif command -v wget >/dev/null 2>&1; then
    DL() { wget -qO "$2" "$1"; }
    DLO() { wget -qO- "$1"; }
else
    echo "error: need curl or wget installed." >&2
    exit 1
fi

# --- Arch guard (only x86_64 shipped today) -----------------------------------
ARCH=$(uname -m 2>/dev/null || echo unknown)
case "$ARCH" in
    x86_64|amd64) ;;
    *) echo "error: no prebuilt AppImage for arch '$ARCH' (x86_64 only)." >&2
       exit 1 ;;
esac

# --- Resolve asset base URL ---------------------------------------------------
if [ "$TAG" = "latest" ]; then
    BASE="https://github.com/$REPO/releases/latest/download"
    echo "Fetching latest $APP_NAME release from $REPO..."
else
    BASE="https://github.com/$REPO/releases/download/$TAG"
    echo "Fetching $APP_NAME release $TAG from $REPO..."
fi

APPIMAGE_ASSET="$APP_NAME-linux-x64.AppImage"

# --- Download into a temp dir -------------------------------------------------
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT INT TERM

echo "Downloading install.sh..."
DL "$BASE/install.sh" "$TMP/install.sh"

echo "Downloading $APPIMAGE_ASSET (~120 MB)..."
DL "$BASE/$APPIMAGE_ASSET" "$TMP/$APPIMAGE_ASSET"

# Optional integrity check if the release ships a SHA256SUMS file.
if DLO "$BASE/SHA256SUMS" > "$TMP/SHA256SUMS" 2>/dev/null && [ -s "$TMP/SHA256SUMS" ]; then
    if command -v sha256sum >/dev/null 2>&1; then
        echo "Verifying checksum..."
        EXPECTED=$(grep "$APPIMAGE_ASSET" "$TMP/SHA256SUMS" | awk '{print $1}' | head -n1)
        if [ -n "$EXPECTED" ]; then
            ACTUAL=$(sha256sum "$TMP/$APPIMAGE_ASSET" | awk '{print $1}')
            if [ "$EXPECTED" != "$ACTUAL" ]; then
                echo "error: checksum mismatch — refusing to install." >&2
                echo "  expected $EXPECTED" >&2
                echo "  actual   $ACTUAL" >&2
                exit 1
            fi
            echo "Checksum OK."
        fi
    fi
fi

# --- Hand off to the installer ------------------------------------------------
chmod +x "$TMP/install.sh"

INSTALL_ARGS="--appimage $TMP/$APPIMAGE_ASSET"
if [ "${FF_SYSTEM:-0}" = "1" ]; then
    INSTALL_ARGS="$INSTALL_ARGS --system"
fi

echo
# shellcheck disable=SC2086
sh "$TMP/install.sh" $INSTALL_ARGS
