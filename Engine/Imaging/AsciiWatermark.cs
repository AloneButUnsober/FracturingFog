// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Imaging/AsciiWatermark.cs
//
// ASCII-art renderer for the resolved watermark (issue #241). An add-in to the
// existing watermark system: every surface already turns the precedence chain
// into one WatermarkRender (TopText / SubText / colour / placement / justify) via
// WatermarkResolver. The GPU overlay paints that with a TTF font, but in Terminal
// Mode the GPU surface is hidden and ASCII stills / video / .cast carry nothing.
//
// This paints the SAME resolved payload into the character grid instead, using a
// block font so the watermark complements the character art. It changes only the
// glyph shapes, never what is drawn — text, colour, placement, and the mandatory
// program sub-line all still come from WatermarkResolver.
//
// Pure grid mutation over AsciiCell[] (row-major, cols*rows). Ink cells take the
// resolved colour; blank cells are left untouched so the fractal shows through.
// Anything falling outside the grid is clipped (small terminals).

using System;
using System.Collections.Generic;
using System.Text;

using FracturingFog.Models;

namespace FracturingFog.Imaging
{
    /// <summary>Stamps a resolved <see cref="WatermarkRender"/> into an
    /// <see cref="AsciiCell"/> grid as character art. See file header.</summary>
    public static class AsciiWatermark
    {
        private const char Ink = '#'; // block-font ink glyph
        private const int EdgePad = 1; // cells from the grid edge

        /// <summary>Draw <paramref name="wm"/> into <paramref name="cells"/>
        /// (row-major, <paramref name="cols"/>×<paramref name="rows"/>) using the
        /// given <paramref name="style"/>. No-op on an empty grid or empty text.</summary>
        public static void Stamp(
            AsciiCell[] cells, int cols, int rows,
            WatermarkRender wm, AsciiWatermarkStyle style)
        {
            if (cells == null || cols <= 0 || rows <= 0) return;
            if (cells.Length < (long)cols * rows) return;
            PlaceInk(cols, rows, wm, style,
                (x, y, g, r, gg, b) => cells[y * cols + x] = new AsciiCell(g, r, gg, b));
        }

        /// <summary>Resolve the watermark to a sparse ink overlay over a
        /// <paramref name="cols"/>×<paramref name="rows"/> character grid. The
        /// string-export formats (HTML / SVG / half-block / Braille) build their
        /// glyphs directly instead of an <see cref="AsciiCell"/> grid, so they
        /// consult this overlay per output cell rather than calling
        /// <see cref="Stamp"/>. Empty (no ink) when text is blank.</summary>
        public static AsciiWatermarkOverlay BuildOverlay(
            int cols, int rows, WatermarkRender wm, AsciiWatermarkStyle style)
        {
            var overlay = new AsciiWatermarkOverlay(cols, rows);
            PlaceInk(cols, rows, wm, style, overlay.Set);
            return overlay;
        }

        // Shared placement + block layout. Emits one call to <paramref name="write"/>
        // per ink cell (x, y, glyph, r, g, b); blank glyphs are skipped so the
        // fractal shows through. Both Stamp and BuildOverlay ride this so the
        // grid mutation and the export overlay place identically.
        private static void PlaceInk(
            int cols, int rows, WatermarkRender wm, AsciiWatermarkStyle style,
            Action<int, int, char, byte, byte, byte> write)
        {
            if (wm == null || cols <= 0 || rows <= 0) return;

            string top = (wm.TopText ?? string.Empty).Trim();
            string sub = (wm.SubText ?? string.Empty).Trim();
            if (top.Length == 0 && sub.Length == 0) return;

            var lines = BuildLines(top, sub, style);
            if (lines.Count == 0) return;

            int blockW = 0;
            foreach (var l in lines) if (l.Length > blockW) blockW = l.Length;
            int blockH = lines.Count;
            if (blockW == 0) return;

            var (bx, by) = Anchor(cols, rows, blockW, blockH, wm.Placement, wm.Justify);

            byte cr = wm.TextColor?.R ?? 255;
            byte cg = wm.TextColor?.G ?? 255;
            byte cb = wm.TextColor?.B ?? 255;

            for (int r = 0; r < lines.Count; r++)
            {
                string line = lines[r];
                int y = by + r;
                if (y < 0 || y >= rows) continue;
                // Right/Center justify pads shorter lines within the block.
                int lineX = wm.Justify switch
                {
                    WatermarkJustify.Center => bx + (blockW - line.Length) / 2,
                    WatermarkJustify.Right  => bx + (blockW - line.Length),
                    _                       => bx,
                };
                for (int c = 0; c < line.Length; c++)
                {
                    char g = line[c];
                    if (g == ' ') continue; // transparent — fractal shows through
                    int x = lineX + c;
                    if (x < 0 || x >= cols) continue;
                    write(x, y, g, cr, cg, cb);
                }
            }
        }

        /// <summary>The rendered text rows for a style. Public for tests.</summary>
        public static List<string> BuildLines(string top, string sub, AsciiWatermarkStyle style)
        {
            top ??= string.Empty; sub ??= string.Empty;
            return style switch
            {
                AsciiWatermarkStyle.PlainLabel  => BuildPlainLabel(top, sub),
                AsciiWatermarkStyle.BoxedBanner => BuildBoxed(top, sub),
                _                               => BuildBlock(top, sub),
            };
        }

        // Single-row label: "TOP  ·  Sub". Smallest footprint, plain glyphs.
        private static List<string> BuildPlainLabel(string top, string sub)
        {
            var parts = new List<string>();
            if (top.Length > 0) parts.Add(top);
            if (sub.Length > 0) parts.Add(sub);
            var s = string.Join("  ·  ", parts);
            return s.Length == 0 ? new List<string>() : new List<string> { s };
        }

        // Block-font top-line (5 rows) + plain sub-line under it.
        private static List<string> BuildBlock(string top, string sub)
        {
            var lines = new List<string>();
            if (top.Length > 0) lines.AddRange(RenderBlockText(top));
            if (sub.Length > 0) lines.Add(sub);
            return lines;
        }

        // Block top-line + sub-line wrapped in a line-drawing border box.
        private static List<string> BuildBoxed(string top, string sub)
        {
            var inner = new List<string>();
            if (top.Length > 0) inner.AddRange(RenderBlockText(top));
            if (sub.Length > 0) inner.Add(sub);
            if (inner.Count == 0) return inner;

            int w = 0;
            foreach (var l in inner) if (l.Length > w) w = l.Length;

            var boxed = new List<string>(inner.Count + 2);
            boxed.Add("+" + new string('-', w + 2) + "+");
            foreach (var l in inner)
                boxed.Add("| " + l.PadRight(w) + " |");
            boxed.Add("+" + new string('-', w + 2) + "+");
            return boxed;
        }

        // Lay out block glyphs horizontally with a one-column gutter. Unknown
        // characters (after upper-casing) become a blank glyph so spacing holds.
        private static string[] RenderBlockText(string text)
        {
            var rows = new StringBuilder[BlockHeight];
            for (int i = 0; i < BlockHeight; i++) rows[i] = new StringBuilder();

            bool first = true;
            foreach (char raw in text.ToUpperInvariant())
            {
                var glyph = GlyphFor(raw);
                if (!first) for (int r = 0; r < BlockHeight; r++) rows[r].Append(' ');
                first = false;
                for (int r = 0; r < BlockHeight; r++) rows[r].Append(glyph[r]);
            }

            var outRows = new string[BlockHeight];
            for (int r = 0; r < BlockHeight; r++) outRows[r] = rows[r].ToString();
            return outRows;
        }

        private static string[] GlyphFor(char c)
            => Font.TryGetValue(c, out var g) ? g : Blank;

        // Anchor the block rectangle to the grid edge per placement + justify.
        private static (int X, int Y) Anchor(
            int cols, int rows, int bw, int bh,
            WatermarkPlacement placement, WatermarkJustify justify)
        {
            int x, y;
            switch (placement)
            {
                case WatermarkPlacement.Top:
                    y = EdgePad;
                    x = HJust(cols, bw, justify);
                    break;
                case WatermarkPlacement.Left:
                    x = EdgePad;
                    y = VJust(rows, bh, justify);
                    break;
                case WatermarkPlacement.Right:
                    x = cols - bw - EdgePad;
                    y = VJust(rows, bh, justify);
                    break;
                case WatermarkPlacement.Bottom:
                default:
                    y = rows - bh - EdgePad;
                    x = HJust(cols, bw, justify);
                    break;
            }
            return (Math.Max(0, x), Math.Max(0, y));
        }

        private static int HJust(int cols, int bw, WatermarkJustify j) => j switch
        {
            WatermarkJustify.Left   => EdgePad,
            WatermarkJustify.Center => (cols - bw) / 2,
            _                       => cols - bw - EdgePad,
        };

        // Left/Center/Right map to top/middle/bottom along a vertical edge, matching
        // WatermarkResolver.ComputeBlockBounds.
        private static int VJust(int rows, int bh, WatermarkJustify j) => j switch
        {
            WatermarkJustify.Left   => EdgePad,
            WatermarkJustify.Center => (rows - bh) / 2,
            _                       => rows - bh - EdgePad,
        };

        // ---- 5-row block font -------------------------------------------------
        // Ink '#', blank ' '. Region / theme / custom text is upper-cased before
        // lookup; the sub-line is plain so digits/punctuation there need no glyph.

        private const int BlockHeight = 5;

        private static readonly string[] Blank = { "   ", "   ", "   ", "   ", "   " };

        private static readonly Dictionary<char, string[]> Font = new()
        {
            [' '] = new[] { "   ", "   ", "   ", "   ", "   " },
            ['A'] = new[] { " ## ", "#  #", "####", "#  #", "#  #" },
            ['B'] = new[] { "### ", "#  #", "### ", "#  #", "### " },
            ['C'] = new[] { " ###", "#   ", "#   ", "#   ", " ###" },
            ['D'] = new[] { "### ", "#  #", "#  #", "#  #", "### " },
            ['E'] = new[] { "####", "#   ", "### ", "#   ", "####" },
            ['F'] = new[] { "####", "#   ", "### ", "#   ", "#   " },
            ['G'] = new[] { " ###", "#   ", "# ##", "#  #", " ###" },
            ['H'] = new[] { "#  #", "#  #", "####", "#  #", "#  #" },
            ['I'] = new[] { "###", " # ", " # ", " # ", "###" },
            ['J'] = new[] { "  ##", "   #", "   #", "#  #", " ## " },
            ['K'] = new[] { "#  #", "# # ", "##  ", "# # ", "#  #" },
            ['L'] = new[] { "#   ", "#   ", "#   ", "#   ", "####" },
            ['M'] = new[] { "#   #", "## ##", "# # #", "#   #", "#   #" },
            ['N'] = new[] { "#  #", "## #", "# ##", "#  #", "#  #" },
            ['O'] = new[] { " ## ", "#  #", "#  #", "#  #", " ## " },
            ['P'] = new[] { "### ", "#  #", "### ", "#   ", "#   " },
            ['Q'] = new[] { " ## ", "#  #", "#  #", "# ##", " ###" },
            ['R'] = new[] { "### ", "#  #", "### ", "# # ", "#  #" },
            ['S'] = new[] { " ###", "#   ", " ## ", "   #", "### " },
            ['T'] = new[] { "###", " # ", " # ", " # ", " # " },
            ['U'] = new[] { "#  #", "#  #", "#  #", "#  #", " ## " },
            ['V'] = new[] { "#   #", "#   #", "#   #", " # # ", "  #  " },
            ['W'] = new[] { "#   #", "#   #", "# # #", "## ##", "#   #" },
            ['X'] = new[] { "#  #", " ## ", " ## ", " ## ", "#  #" },
            ['Y'] = new[] { "#   #", " # # ", "  #  ", "  #  ", "  #  " },
            ['Z'] = new[] { "####", "  # ", " #  ", "#   ", "####" },
            ['0'] = new[] { " ## ", "#  #", "# ##", "## #", " ## " },
            ['1'] = new[] { " # ", "## ", " # ", " # ", "###" },
            ['2'] = new[] { "### ", "   #", " ## ", "#   ", "####" },
            ['3'] = new[] { "###", "  #", " ##", "  #", "###" },
            ['4'] = new[] { "#  #", "#  #", "####", "   #", "   #" },
            ['5'] = new[] { "####", "#   ", "### ", "   #", "### " },
            ['6'] = new[] { " ###", "#   ", "### ", "#  #", " ## " },
            ['7'] = new[] { "####", "   #", "  # ", " #  ", " #  " },
            ['8'] = new[] { " ## ", "#  #", " ## ", "#  #", " ## " },
            ['9'] = new[] { " ## ", "#  #", " ###", "   #", " ## " },
            ['-'] = new[] { "   ", "   ", "###", "   ", "   " },
            ['.'] = new[] { " ", " ", " ", " ", "#" },
            [','] = new[] { "  ", "  ", "  ", " #", "# " },
            [':'] = new[] { " ", "#", " ", "#", " " },
            ['_'] = new[] { "    ", "    ", "    ", "    ", "####" },
            ['/'] = new[] { "   #", "  # ", " #  ", "#   ", "#   " },
            ['\''] = new[] { "#", "#", " ", " ", " " },
            ['('] = new[] { " #", "# ", "# ", "# ", " #" },
            [')'] = new[] { "# ", " #", " #", " #", "# " },
            ['&'] = new[] { " ## ", "#  #", " ## ", "#  #", " ###" },
            ['!'] = new[] { "#", "#", "#", " ", "#" },
            ['#'] = new[] { " # # ", "#####", " # # ", "#####", " # # " },
        };
    }

    /// <summary>A sparse watermark ink layer over a character grid, produced by
    /// <see cref="AsciiWatermark.BuildOverlay"/>. The string-export formats query
    /// it per output cell: an inked cell overrides the fractal glyph (and, for the
    /// coloured formats, its colour) with the watermark's.</summary>
    public sealed class AsciiWatermarkOverlay
    {
        private readonly int _cols;
        private readonly int _rows;
        // Glyph == '\0' marks an empty (non-ink) cell — the block font never
        // emits NUL, so it is a safe sentinel.
        private readonly AsciiCell[] _cells;

        internal AsciiWatermarkOverlay(int cols, int rows)
        {
            _cols = Math.Max(0, cols);
            _rows = Math.Max(0, rows);
            _cells = new AsciiCell[_cols * _rows];
        }

        /// <summary>True when at least one cell carries watermark ink.</summary>
        public bool HasInk { get; private set; }

        internal void Set(int x, int y, char glyph, byte r, byte g, byte b)
        {
            if ((uint)x >= (uint)_cols || (uint)y >= (uint)_rows) return;
            _cells[y * _cols + x] = new AsciiCell(glyph, r, g, b);
            HasInk = true;
        }

        /// <summary>Fetch the ink at (<paramref name="x"/>, <paramref name="y"/>).
        /// Returns false for out-of-range or non-ink cells.</summary>
        public bool TryGet(int x, int y, out AsciiCell cell)
        {
            cell = default;
            if ((uint)x >= (uint)_cols || (uint)y >= (uint)_rows) return false;
            var c = _cells[y * _cols + x];
            if (c.Glyph == '\0') return false;
            cell = c;
            return true;
        }
    }
}
