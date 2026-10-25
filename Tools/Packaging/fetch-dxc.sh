#!/usr/bin/env bash
# Tools/Packaging/fetch-dxc.sh
#
# #1455 — fetch the pinned DXC (HLSL -> SPIR-V compiler) for Linux x86-64 into
# <dest>: <dest>/bin/dxc, <dest>/lib (libdxcompiler.so, ...) and DXC's
# licence files. The Vulkan compute kernels compile their shaders with it at run
# time (Rendering.Vulkan/DxcCompiler.cs looks for dxc/bin/dxc next to the app),
# so the AppImage ships it and users need no Vulkan SDK. CI's lavapipe leg uses
# the same pin (#1453).
#
# The archive comes from Microsoft's GitHub release and is checked against the
# SHA-256 GitHub publishes for the asset; a mismatch fails.
#
# Usage: Tools/Packaging/fetch-dxc.sh <dest-dir>
# Prints the dxc path on stdout.

set -euo pipefail

DXC_TAG=v1.9.2609
DXC_ASSET=linux_dxc_2026_09_28.x86_x64.tar.gz
DXC_SHA256=96faadc7f5c282d2ffda49804beb4c3ee38127bc252b723234e3c5cdf7aa39a1

DEST="${1:?usage: fetch-dxc.sh <dest-dir>}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

curl -fsSL -o "$WORK/$DXC_ASSET" \
    "https://github.com/microsoft/DirectXShaderCompiler/releases/download/$DXC_TAG/$DXC_ASSET"
echo "$DXC_SHA256  $WORK/$DXC_ASSET" | sha256sum -c - >&2
mkdir -p "$WORK/x"
tar -xzf "$WORK/$DXC_ASSET" -C "$WORK/x"

BIN="$(find "$WORK/x" \( -type f -o -type l \) -name dxc -path '*/bin/*' | head -1)"
if [ -z "$BIN" ]; then
    echo "fetch-dxc.sh: no bin/dxc in $DXC_ASSET" >&2
    exit 1
fi
ROOT="$(dirname "$(dirname "$BIN")")"

mkdir -p "$DEST/bin" "$DEST/lib"
cp -a "$ROOT/bin/." "$DEST/bin/"
cp -a "$ROOT/lib/." "$DEST/lib/"
# The licence texts travel with the binaries (THIRD-PARTY-NOTICES points here).
find "$WORK/x" -maxdepth 2 -type f \( -iname 'LICENSE*' -o -iname 'ThirdPartyNotices*' \) \
    -exec cp {} "$DEST/" \;
chmod +x "$DEST/bin/dxc"
echo "$DXC_TAG" > "$DEST/VERSION"

echo "$DEST/bin/dxc"
