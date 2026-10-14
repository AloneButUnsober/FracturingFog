#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 Bradley Brown
#
# Tools/Packaging/ico-to-png.py
#
# #429 — pull the largest PNG-encoded frame out of a Windows .ico, so the Linux
# packaging uses the same artwork as the Windows exe (Resources/FracturingFog.ico,
# PaletteBuilder/Resources/PaletteBuilder_Logo.ico) without a committed copy or
# an image library. Modern .ico files store their 256x256 frame as a plain PNG;
# the smaller BMP frames are ignored.
#
# Usage: ico-to-png.py <in.ico> <out.png>
# Exit 1 when the file has no PNG frame (the caller must not ship a placeholder).

import struct
import sys

PNG_SIG = b"\x89PNG\r\n\x1a\n"


def largest_png_frame(data):
    if len(data) < 6:
        raise ValueError("not an .ico file (too short)")
    reserved, kind, count = struct.unpack("<HHH", data[:6])
    if reserved != 0 or kind != 1:
        raise ValueError("not an .ico file (bad header)")
    best = None
    for i in range(count):
        entry = data[6 + 16 * i: 22 + 16 * i]
        if len(entry) < 16:
            raise ValueError("truncated directory")
        w, h, _, _, _, _, size, offset = struct.unpack("<BBBBHHII", entry)
        frame = data[offset: offset + size]
        if not frame.startswith(PNG_SIG):
            continue
        side = (w or 256) * (h or 256)
        if best is None or side > best[0]:
            best = (side, w or 256, h or 256, frame)
    return best


def main(argv):
    if len(argv) != 3:
        print("usage: ico-to-png.py <in.ico> <out.png>", file=sys.stderr)
        return 2
    with open(argv[1], "rb") as f:
        data = f.read()
    try:
        best = largest_png_frame(data)
    except ValueError as ex:
        print(f"ico-to-png: {argv[1]}: {ex}", file=sys.stderr)
        return 1
    if best is None:
        print(f"ico-to-png: {argv[1]} has no PNG-encoded frame", file=sys.stderr)
        return 1
    _, w, h, png = best
    with open(argv[2], "wb") as f:
        f.write(png)
    print(f"ico-to-png: {argv[1]} -> {argv[2]} ({w}x{h})")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
