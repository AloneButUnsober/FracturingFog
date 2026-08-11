#!/bin/sh
# Fracturing Fog — Linux installer for the AppImage build.
#
# Installs the FracturingFog AppImage plus desktop-menu integration
# (launcher entry + icon) so it shows up in your application menu.
#
# Usage:
#   ./install.sh                 Install for the current user (no root needed)
#   sudo ./install.sh --system   Install system-wide for all users
#   ./install.sh --uninstall     Remove a per-user install
#   sudo ./install.sh --uninstall --system   Remove a system-wide install
#
# Options:
#   --system      Install into /opt + /usr/local/bin + /usr/share (needs root)
#   --uninstall   Remove a previous install instead of installing
#   --appimage <path>  Use a specific AppImage file (default: auto-detect
#                 the newest FracturingFog*.AppImage next to this script)
#   -h, --help    Show this help

set -eu

APP_NAME="FracturingFog"
APP_PRETTY="Fracturing Fog"
WM_CLASS="FracturingFog.App"

# --- Resolve script directory (POSIX-ish) -------------------------------------
SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)

# --- Defaults -----------------------------------------------------------------
MODE_SYSTEM=0
DO_UNINSTALL=0
APPIMAGE_SRC=""

usage() {
    sed -n '2,26p' "$0" | sed 's/^# \{0,1\}//'
    exit "${1:-0}"
}

# --- Parse args ---------------------------------------------------------------
while [ $# -gt 0 ]; do
    case "$1" in
        --system)     MODE_SYSTEM=1 ;;
        --uninstall)  DO_UNINSTALL=1 ;;
        --appimage)   shift; APPIMAGE_SRC="${1:-}" ;;
        -h|--help)    usage 0 ;;
        *) echo "Unknown option: $1" >&2; usage 1 ;;
    esac
    shift
done

# --- Locations depending on scope ---------------------------------------------
if [ "$MODE_SYSTEM" -eq 1 ]; then
    if [ "$(id -u)" -ne 0 ]; then
        echo "error: --system needs root. Re-run with: sudo $0 --system" >&2
        exit 1
    fi
    INSTALL_DIR="/opt/$APP_NAME"
    BIN_DIR="/usr/local/bin"
    DESKTOP_DIR="/usr/share/applications"
    ICON_DIR="/usr/share/icons/hicolor/256x256/apps"
else
    INSTALL_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/$APP_NAME"
    BIN_DIR="$HOME/.local/bin"
    DESKTOP_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
    ICON_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/256x256/apps"
fi

INSTALLED_APPIMAGE="$INSTALL_DIR/$APP_NAME.AppImage"
BIN_LINK="$BIN_DIR/fracturingfog"
DESKTOP_FILE="$DESKTOP_DIR/$APP_NAME.desktop"
ICON_FILE="$ICON_DIR/$APP_NAME.png"

# --- Cache refresh helpers ----------------------------------------------------
refresh_caches() {
    if command -v update-desktop-database >/dev/null 2>&1; then
        update-desktop-database "$DESKTOP_DIR" >/dev/null 2>&1 || true
    fi
    if command -v gtk-update-icon-cache >/dev/null 2>&1; then
        gtk-update-icon-cache -f -t "$(dirname "$(dirname "$(dirname "$ICON_DIR")")")" >/dev/null 2>&1 || true
    fi
}

# --- Uninstall ----------------------------------------------------------------
if [ "$DO_UNINSTALL" -eq 1 ]; then
    echo "Removing $APP_PRETTY..."
    rm -f "$BIN_LINK" "$DESKTOP_FILE" "$ICON_FILE"
    rm -f "$INSTALLED_APPIMAGE"
    rmdir "$INSTALL_DIR" 2>/dev/null || true
    refresh_caches
    echo "Done. $APP_PRETTY removed."
    exit 0
fi

# --- Locate the AppImage ------------------------------------------------------
if [ -z "$APPIMAGE_SRC" ]; then
    # Newest FracturingFog*.AppImage under the script dir (recursive).
    APPIMAGE_SRC=$(find "$SCRIPT_DIR" -maxdepth 3 -type f -iname "$APP_NAME*.AppImage" \
        -printf '%T@ %p\n' 2>/dev/null | sort -nr | head -n1 | cut -d' ' -f2- || true)
fi

if [ -z "$APPIMAGE_SRC" ] || [ ! -f "$APPIMAGE_SRC" ]; then
    echo "error: no FracturingFog AppImage found." >&2
    echo "       Put install.sh next to FracturingFog-linux-x64.AppImage," >&2
    echo "       or pass one: $0 --appimage /path/to/File.AppImage" >&2
    exit 1
fi

echo "AppImage: $APPIMAGE_SRC"

# --- FUSE check (AppImages need libfuse2 to self-mount) -----------------------
NEEDS_EXTRACT=0
if ! ldconfig -p 2>/dev/null | grep -q 'libfuse\.so\.2'; then
    if [ -e /dev/fuse ]; then
        : # /dev/fuse present but libfuse2 shared lib not found — warn below.
    fi
    NEEDS_EXTRACT=1
fi

# --- Install ------------------------------------------------------------------
echo "Installing to: $INSTALL_DIR"
mkdir -p "$INSTALL_DIR" "$BIN_DIR" "$DESKTOP_DIR" "$ICON_DIR"

cp -f "$APPIMAGE_SRC" "$INSTALLED_APPIMAGE"
chmod +x "$INSTALLED_APPIMAGE"

# Convenience CLI launcher on PATH.
ln -sf "$INSTALLED_APPIMAGE" "$BIN_LINK"

# Extract the bundled icon from the AppImage (falls back gracefully).
ICON_INSTALLED=0
if [ "$NEEDS_EXTRACT" -eq 0 ]; then
    TMP_EXTRACT=$(mktemp -d)
    if ( cd "$TMP_EXTRACT" && "$INSTALLED_APPIMAGE" --appimage-extract "$APP_NAME.png" >/dev/null 2>&1 ); then
        if [ -f "$TMP_EXTRACT/squashfs-root/$APP_NAME.png" ]; then
            cp -f "$TMP_EXTRACT/squashfs-root/$APP_NAME.png" "$ICON_FILE"
            ICON_INSTALLED=1
        fi
    fi
    rm -rf "$TMP_EXTRACT"
fi
if [ "$ICON_INSTALLED" -eq 0 ]; then
    # Fallback: icon shipped alongside the script, if any.
    for cand in "$SCRIPT_DIR/$APP_NAME.png" \
                "$SCRIPT_DIR"/*/"$APP_NAME.png" \
                "$SCRIPT_DIR"/*/*/"$APP_NAME.png"; do
        if [ -f "$cand" ]; then
            cp -f "$cand" "$ICON_FILE"
            ICON_INSTALLED=1
            break
        fi
    done
fi

# Write the .desktop entry pointing at the installed AppImage.
cat > "$DESKTOP_FILE" <<EOF
[Desktop Entry]
Type=Application
Name=$APP_PRETTY
GenericName=Mandelbrot Explorer
Comment=Mandelbrot set explorer with audio-reactive slideshow and palette tools
Exec=$INSTALLED_APPIMAGE %F
Icon=$APP_NAME
Categories=Graphics;Science;Math;
Terminal=false
StartupNotify=true
StartupWMClass=$WM_CLASS
Keywords=fractal;mandelbrot;palette;slideshow;
EOF
chmod 644 "$DESKTOP_FILE"

refresh_caches

# --- Report -------------------------------------------------------------------
echo
echo "$APP_PRETTY installed."
echo "  Launcher:  $DESKTOP_FILE"
echo "  Binary:    $INSTALLED_APPIMAGE"
echo "  On PATH:   fracturingfog"
[ "$ICON_INSTALLED" -eq 1 ] || echo "  (icon not found — menu entry may show a generic icon)"

if [ "$MODE_SYSTEM" -eq 0 ]; then
    case ":$PATH:" in
        *":$BIN_DIR:"*) ;;
        *) echo
           echo "note: $BIN_DIR is not on your PATH."
           echo "      Add it: echo 'export PATH=\"\$HOME/.local/bin:\$PATH\"' >> ~/.profile" ;;
    esac
fi

if [ "$NEEDS_EXTRACT" -eq 1 ]; then
    echo
    echo "warning: libfuse2 not detected. The AppImage may fail to launch."
    echo "  Fix (Debian/Ubuntu):  sudo apt install libfuse2"
    echo "  Fix (Fedora):         sudo dnf install fuse-libs"
    echo "  Or run without FUSE:  fracturingfog --appimage-extract-and-run"
fi

echo
echo "Launch from your app menu, or run: fracturingfog"
