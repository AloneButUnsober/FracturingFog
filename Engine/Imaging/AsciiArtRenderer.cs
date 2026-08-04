// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/AsciiArtRenderer.cs
//
// ASCII / text-art exporter (#226). Consumes the render output the engine
// already produces — a BGRA ColorBuffer (uint[], packed 0xAARRGGBB like the
// rest of the pipeline; see ImageExport.ComputeContrastColor) and, when
// available, the per-pixel smooth iteration count (IHeightFieldSource
// .SmoothBuffer) — and box-downsamples it into a character grid, then emits one
// of several text encodings (see AsciiArtFormat).
//
// No engine/kernel change: this is a pure post-process over an existing frame,
// a sibling of ImageExport. Every supported format is UTF-8 text, so Render
// returns a string and the caller writes the file (see WriteToFile helper).

using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace FracturingFog.Imaging
{
    /// <summary>One painted character cell: a glyph plus its box-averaged
    /// colour. Produced by <see cref="AsciiArtRenderer.RenderCells"/> for the
    /// live display path (#227); the file formats stringify the same grid.</summary>
    public readonly struct AsciiCell
    {
        public readonly char Glyph;
        public readonly byte R, G, B;
        public AsciiCell(char glyph, byte r, byte g, byte b)
        { Glyph = glyph; R = r; G = g; B = b; }
    }

    /// <summary>Renders a rendered fractal frame as character art. See the file
    /// header and <see cref="AsciiArtFormat"/>.</summary>
    public static class AsciiArtRenderer
    {
        private readonly struct Cell
        {
            public readonly double R, G, B; // 0..255 box-average
            public readonly double Field;   // 0..1 ramp driver (smooth or luma)
            public Cell(double r, double g, double b, double field)
            { R = r; G = g; B = b; Field = field; }
        }

        /// <summary>Render <paramref name="pixels"/> (BGRA, width*height) to text
        /// art. <paramref name="smooth"/> (optional, same layout) drives the glyph
        /// ramp when <see cref="AsciiArtOptions.UseSmoothField"/> is set; colour
        /// always comes from the pixels.</summary>
        public static string Render(
            uint[] pixels, float[]? smooth, int width, int height, AsciiArtOptions options)
        {
            if (pixels is null) throw new ArgumentNullException(nameof(pixels));
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            var opt = options ?? new AsciiArtOptions();

            int cols = Math.Max(1, opt.Columns);
            // Rows from the source aspect, corrected for the font cell being
            // ~CellAspect times taller than wide, so the art keeps its shape.
            int rows = Math.Max(1, (int)Math.Round(
                cols * ((double)height / width) / Math.Max(0.1, opt.CellAspect)));

            double smoothMax = ComputeSmoothMax(opt, smooth);

            return opt.Format switch
            {
                AsciiArtFormat.PlainText     => RenderChars(pixels, smooth, width, height, cols, rows, smoothMax, opt, color: false),
                AsciiArtFormat.Ansi          => RenderChars(pixels, smooth, width, height, cols, rows, smoothMax, opt, color: true),
                AsciiArtFormat.AnsiHalfBlock => RenderHalfBlock(pixels, smooth, width, height, cols, rows, smoothMax, opt),
                AsciiArtFormat.Html          => RenderHtml(pixels, smooth, width, height, cols, rows, smoothMax, opt),
                AsciiArtFormat.Svg           => RenderSvg(pixels, smooth, width, height, cols, rows, smoothMax, opt),
                AsciiArtFormat.Braille       => RenderBraille(pixels, smooth, width, height, cols, rows, smoothMax, opt),
                _                            => RenderChars(pixels, smooth, width, height, cols, rows, smoothMax, opt, color: false),
            };
        }

        /// <summary>Produce the standard one-glyph-per-cell grid (glyph +
        /// box-averaged colour) used by the live display path (#227). Same
        /// <see cref="Sample"/> + ramp core the PlainText/Ansi string formats use.
        /// <paramref name="cols"/>/<paramref name="rows"/> are the derived grid
        /// dimensions (rows corrected for <see cref="AsciiArtOptions.CellAspect"/>).</summary>
        public static AsciiCell[] RenderCells(
            uint[] pixels, float[]? smooth, int width, int height,
            AsciiArtOptions options, out int cols, out int rows)
        {
            if (pixels is null) throw new ArgumentNullException(nameof(pixels));
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            var opt = options ?? new AsciiArtOptions();

            cols = Math.Max(1, opt.Columns);
            rows = Math.Max(1, (int)Math.Round(
                cols * ((double)height / width) / Math.Max(0.1, opt.CellAspect)));

            double smoothMax = ComputeSmoothMax(opt, smooth);
            var sampled = Sample(pixels, smooth, width, height, cols, rows, opt.UseSmoothField, smoothMax);

            var cells = new AsciiCell[cols * rows];
            for (int i = 0; i < sampled.Length; i++)
            {
                var c = sampled[i];
                char g = Glyph(c.Field, opt.Ramp, opt.Invert);
                cells[i] = new AsciiCell(g, ToByte(c.R), ToByte(c.G), ToByte(c.B));
            }
            return cells;
        }

        private static double ComputeSmoothMax(AsciiArtOptions opt, float[]? smooth)
        {
            double m = 0.0;
            if (opt.UseSmoothField && smooth != null)
                for (int i = 0; i < smooth.Length; i++)
                    if (smooth[i] > m) m = smooth[i];
            return m;
        }

        private static byte ToByte(double v)
        {
            if (v < 0) v = 0; else if (v > 255) v = 255;
            return (byte)v;
        }

        /// <summary>Suggested file extension for a format.</summary>
        public static string ExtensionFor(AsciiArtFormat fmt) => fmt switch
        {
            AsciiArtFormat.Ansi or AsciiArtFormat.AnsiHalfBlock => ".ans",
            AsciiArtFormat.Html    => ".html",
            AsciiArtFormat.Svg     => ".svg",
            _                       => ".txt",
        };

        /// <summary>Render then write UTF-8 to <paramref name="path"/>.</summary>
        public static void WriteToFile(
            uint[] pixels, float[]? smooth, int width, int height,
            AsciiArtOptions options, string path)
        {
            string text = Render(pixels, smooth, width, height, options);
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        // ── Box downsample ────────────────────────────────────────────────
        //
        // Averages the source over each sub-cell of an (sw × sh) grid. Colour is
        // the mean BGRA; Field is the normalised mean smooth count when a smooth
        // buffer is supplied (banding-free), else the mean luma. Interior pixels
        // (smooth == 0) pull the field toward 0 → the blank end of the ramp.

        private static Cell[] Sample(
            uint[] px, float[]? smooth, int w, int h, int sw, int sh,
            bool useSmooth, double smoothMax)
        {
            var cells = new Cell[sw * sh];
            for (int cy = 0; cy < sh; cy++)
            {
                int y0 = (int)((long)cy * h / sh);
                int y1 = (int)((long)(cy + 1) * h / sh);
                if (y1 <= y0) y1 = y0 + 1;
                if (y1 > h) y1 = h;
                for (int cx = 0; cx < sw; cx++)
                {
                    int x0 = (int)((long)cx * w / sw);
                    int x1 = (int)((long)(cx + 1) * w / sw);
                    if (x1 <= x0) x1 = x0 + 1;
                    if (x1 > w) x1 = w;

                    long sr = 0, sg = 0, sb = 0; double sf = 0; long n = 0;
                    for (int y = y0; y < y1; y++)
                    {
                        int rb = y * w;
                        for (int x = x0; x < x1; x++)
                        {
                            uint p = px[rb + x];
                            sr += (p >> 16) & 0xFF;
                            sg += (p >> 8) & 0xFF;
                            sb += p & 0xFF;
                            if (smooth != null) sf += smooth[rb + x];
                            n++;
                        }
                    }
                    double inv = n > 0 ? 1.0 / n : 0.0;
                    double r = sr * inv, g = sg * inv, b = sb * inv;
                    double field;
                    if (useSmooth && smooth != null && smoothMax > 1e-9)
                        field = Math.Clamp((sf * inv) / smoothMax, 0.0, 1.0);
                    else
                        field = Math.Clamp((0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0, 0.0, 1.0);

                    cells[cy * sw + cx] = new Cell(r, g, b, field);
                }
            }
            return cells;
        }

        private static char Glyph(double field, string ramp, bool invert)
        {
            double f = invert ? 1.0 - field : field;
            int idx = (int)Math.Round(f * (ramp.Length - 1));
            if (idx < 0) idx = 0; else if (idx >= ramp.Length) idx = ramp.Length - 1;
            return ramp[idx];
        }

        // ── PlainText / Ansi (one glyph per cell) ─────────────────────────

        private static string RenderChars(
            uint[] px, float[]? smooth, int w, int h, int cols, int rows,
            double smoothMax, AsciiArtOptions opt, bool color)
        {
            // Shares the grid producer with the live display path (#227).
            var cells = RenderCells(px, smooth, w, h, opt, out cols, out rows);
            var sb = new StringBuilder(rows * (cols + (color ? cols * 20 : 1)));
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < cols; x++)
                {
                    var c = cells[y * cols + x];
                    if (color)
                        sb.Append("\x1b[38;2;")
                          .Append(c.R).Append(';')
                          .Append(c.G).Append(';')
                          .Append(c.B).Append('m')
                          .Append(c.Glyph);
                    else
                        sb.Append(c.Glyph);
                }
                if (color) sb.Append("\x1b[0m");
                sb.Append('\n');
            }
            return sb.ToString();
        }

        // ── ANSI half-block (▀): 2 stacked colours per character cell ──────

        private static string RenderHalfBlock(
            uint[] px, float[]? smooth, int w, int h, int cols, int rows,
            double smoothMax, AsciiArtOptions opt)
        {
            int sh = rows * 2; // two vertical sub-rows per character cell
            var cells = Sample(px, smooth, w, h, cols, sh, opt.UseSmoothField, smoothMax);
            var sb = new StringBuilder(rows * cols * 40);
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < cols; x++)
                {
                    var top = cells[(2 * y) * cols + x];
                    var bot = cells[(2 * y + 1) * cols + x];
                    sb.Append("\x1b[38;2;")
                      .Append((int)top.R).Append(';').Append((int)top.G).Append(';').Append((int)top.B).Append('m')
                      .Append("\x1b[48;2;")
                      .Append((int)bot.R).Append(';').Append((int)bot.G).Append(';').Append((int)bot.B).Append('m')
                      .Append('▀'); // ▀ upper half block
                }
                sb.Append("\x1b[0m\n");
            }
            return sb.ToString();
        }

        // ── HTML: <pre> of coloured <span> glyphs ─────────────────────────

        private static string RenderHtml(
            uint[] px, float[]? smooth, int w, int h, int cols, int rows,
            double smoothMax, AsciiArtOptions opt)
        {
            var cells = Sample(px, smooth, w, h, cols, rows, opt.UseSmoothField, smoothMax);
            var sb = new StringBuilder(rows * cols * 40);
            sb.Append("<!doctype html><meta charset=\"utf-8\">\n");
            sb.Append(string.Create(CultureInfo.InvariantCulture,
                $"<pre style=\"background:{opt.BackgroundCss};line-height:1;font:{opt.FontSizePx.ToString("0.##", CultureInfo.InvariantCulture)}px/1 monospace;white-space:pre;margin:0\">"));
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < cols; x++)
                {
                    var c = cells[y * cols + x];
                    char g = Glyph(c.Field, opt.Ramp, opt.Invert);
                    sb.Append("<span style=\"color:#")
                      .Append(Hex2((int)c.R)).Append(Hex2((int)c.G)).Append(Hex2((int)c.B))
                      .Append("\">").Append(HtmlEscape(g)).Append("</span>");
                }
                sb.Append('\n');
            }
            sb.Append("</pre>\n");
            return sb.ToString();
        }

        // ── SVG: one <text> row of coloured <tspan> glyphs (vector) ───────

        private static string RenderSvg(
            uint[] px, float[]? smooth, int w, int h, int cols, int rows,
            double smoothMax, AsciiArtOptions opt)
        {
            var cells = Sample(px, smooth, w, h, cols, rows, opt.UseSmoothField, smoothMax);
            double fs = opt.FontSizePx;
            double advance = fs * 0.6;              // monospace glyph advance
            double lineH = fs;                       // cell height in the SVG
            double canvasW = advance * cols;
            double canvasH = lineH * rows;
            var sb = new StringBuilder(rows * cols * 44);
            sb.Append(string.Create(CultureInfo.InvariantCulture,
                $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{canvasW:0.#}\" height=\"{canvasH:0.#}\" viewBox=\"0 0 {canvasW:0.#} {canvasH:0.#}\">\n"));
            sb.Append(string.Create(CultureInfo.InvariantCulture,
                $"<rect width=\"100%\" height=\"100%\" fill=\"{opt.BackgroundCss}\"/>\n"));
            sb.Append(string.Create(CultureInfo.InvariantCulture,
                $"<g font-family=\"monospace\" font-size=\"{fs:0.#}\" xml:space=\"preserve\">\n"));
            for (int y = 0; y < rows; y++)
            {
                double baseline = (y + 0.8) * lineH;
                sb.Append(string.Create(CultureInfo.InvariantCulture,
                    $"<text x=\"0\" y=\"{baseline:0.##}\">"));
                for (int x = 0; x < cols; x++)
                {
                    var c = cells[y * cols + x];
                    char g = Glyph(c.Field, opt.Ramp, opt.Invert);
                    double gx = x * advance;
                    sb.Append(string.Create(CultureInfo.InvariantCulture,
                        $"<tspan x=\"{gx:0.##}\" fill=\"#{Hex2((int)c.R)}{Hex2((int)c.G)}{Hex2((int)c.B)}\">"))
                      .Append(HtmlEscape(g)).Append("</tspan>");
                }
                sb.Append("</text>\n");
            }
            sb.Append("</g></svg>\n");
            return sb.ToString();
        }

        // ── Braille: 2×4 Unicode dot cells (monochrome, high density) ─────

        private static string RenderBraille(
            uint[] px, float[]? smooth, int w, int h, int cols, int rows,
            double smoothMax, AsciiArtOptions opt)
        {
            int sw = cols * 2, sh = rows * 4;
            var cells = Sample(px, smooth, w, h, sw, sh, opt.UseSmoothField, smoothMax);
            // Dot bit per (dx,dy) in a braille cell (U+2800 + mask).
            //   1 4        col0: 0x01 0x02 0x04 0x40
            //   2 5        col1: 0x08 0x10 0x20 0x80
            //   3 6
            //   7 8
            int[,] dotBit =
            {
                { 0x01, 0x02, 0x04, 0x40 },
                { 0x08, 0x10, 0x20, 0x80 },
            };
            var sb = new StringBuilder(rows * (cols + 1));
            for (int cy = 0; cy < rows; cy++)
            {
                for (int cx = 0; cx < cols; cx++)
                {
                    int mask = 0;
                    for (int dx = 0; dx < 2; dx++)
                        for (int dy = 0; dy < 4; dy++)
                        {
                            var c = cells[(cy * 4 + dy) * sw + (cx * 2 + dx)];
                            // Perceptual boost (sqrt) before thresholding: the
                            // interesting structure is a thin high-field boundary
                            // band that box-averaging otherwise dilutes below any
                            // linear 0.5 cutoff, leaving an all-blank grid.
                            double f = Math.Sqrt(Math.Clamp(c.Field, 0.0, 1.0));
                            if (opt.Invert) f = 1.0 - f;
                            if (f >= opt.BrailleThreshold) mask |= dotBit[dx, dy];
                        }
                    sb.Append((char)(0x2800 + mask));
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        // ── helpers ───────────────────────────────────────────────────────

        private static string Hex2(int v)
        {
            if (v < 0) v = 0; else if (v > 255) v = 255;
            return v.ToString("x2", CultureInfo.InvariantCulture);
        }

        private static string HtmlEscape(char g) => g switch
        {
            '<' => "&lt;",
            '>' => "&gt;",
            '&' => "&amp;",
            _   => g.ToString(),
        };
    }
}
