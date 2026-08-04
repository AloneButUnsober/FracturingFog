// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/AsciiAnimationRecorder.cs
//
// ASCII animation recorder (#230). Collects a sequence of AsciiCell grids — one
// per animation frame, exactly what AsciiArtRenderer.RenderCells produces and
// what the live Terminal Mode pump paints (#227), optionally already carrying
// the ASCII-native FX chain (#229) — with per-frame timing, and serializes them
// to a shareable animated container (see AsciiAnimationFormat).
//
// Pure post-process over grids the engine already makes: no kernel change, a
// sibling of AsciiArtRenderer. The three text formats here are self-contained;
// an MP4 target (rasterize the grid per frame → PngSequenceWriter → ffmpeg)
// lives on the UI side where the AsciiView + video pipeline already are.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace FracturingFog.Imaging
{
    /// <summary>Accumulates ASCII animation frames and emits them as one of the
    /// <see cref="AsciiAnimationFormat"/> containers. Not thread-safe; drive it
    /// from one frame producer.</summary>
    public sealed class AsciiAnimationRecorder
    {
        private readonly struct Frame
        {
            public readonly AsciiCell[] Cells;
            public readonly double Hold; // seconds this frame is shown
            public Frame(AsciiCell[] cells, double hold) { Cells = cells; Hold = hold; }
        }

        private readonly List<Frame> _frames = new();

        /// <summary>Grid width in character cells (from the first frame).</summary>
        public int Cols { get; private set; }
        /// <summary>Grid height in character cells (from the first frame).</summary>
        public int Rows { get; private set; }
        /// <summary>Frames captured so far.</summary>
        public int FrameCount => _frames.Count;
        /// <summary>Total animation duration (sum of per-frame holds), seconds.</summary>
        public double TotalSeconds { get; private set; }

        /// <summary>Append one frame. <paramref name="cells"/> is a
        /// <paramref name="cols"/>×<paramref name="rows"/> grid (row-major, the
        /// layout <see cref="AsciiArtRenderer.RenderCells"/> returns).
        /// <paramref name="holdSeconds"/> is how long it is shown before the next
        /// frame (clamped to a small positive floor).</summary>
        public void AddFrame(AsciiCell[] cells, int cols, int rows, double holdSeconds)
        {
            if (cells is null) throw new ArgumentNullException(nameof(cells));
            if (cols <= 0 || rows <= 0) throw new ArgumentOutOfRangeException(nameof(cols));
            if (cells.Length < cols * rows)
                throw new ArgumentException("cells shorter than cols*rows", nameof(cells));

            if (_frames.Count == 0) { Cols = cols; Rows = rows; }
            else if (cols != Cols || rows != Rows)
                throw new ArgumentException(
                    $"frame size {cols}x{rows} differs from first frame {Cols}x{Rows}", nameof(cells));

            double hold = holdSeconds > 1e-4 ? holdSeconds : 1e-4;
            // Defensive copy: the producer typically reuses one grid buffer.
            var copy = new AsciiCell[cols * rows];
            Array.Copy(cells, copy, copy.Length);
            _frames.Add(new Frame(copy, hold));
            TotalSeconds += hold;
        }

        /// <summary>Append one frame from an <see cref="FracturingFog.Render.AsciiFrame"/>
        /// (the live-view payload) — unpacks its 0x00RRGGBB colours into cells. Used
        /// by the live "record" capture, which only has AsciiFrames on hand.</summary>
        public void AddFrame(FracturingFog.Render.AsciiFrame frame, double holdSeconds)
        {
            if (frame.IsEmpty) throw new ArgumentException("empty frame", nameof(frame));
            int cols = frame.Cols, rows = frame.Rows;
            var cells = new AsciiCell[cols * rows];
            bool hasColor = frame.HasColor && frame.Colors != null && frame.Colors.Length >= cols * rows;
            for (int i = 0; i < cells.Length; i++)
            {
                uint c = hasColor ? frame.Colors[i] : 0xDCDCDCu;
                cells[i] = new AsciiCell(frame.Glyphs[i],
                    (byte)((c >> 16) & 0xFF), (byte)((c >> 8) & 0xFF), (byte)(c & 0xFF));
            }
            AddFrame(cells, cols, rows, holdSeconds);
        }

        /// <summary>Materialise the captured grids as <see cref="FracturingFog.Render.AsciiFrame"/>s
        /// (colour re-packed 0x00RRGGBB) — for the MP4 exporter, which rasterises
        /// each frame to pixels.</summary>
        public System.Collections.Generic.IReadOnlyList<FracturingFog.Render.AsciiFrame> ExportFrames()
        {
            var list = new System.Collections.Generic.List<FracturingFog.Render.AsciiFrame>(_frames.Count);
            foreach (var fr in _frames)
            {
                int n = Cols * Rows;
                var glyphs = new char[n];
                var colors = new uint[n];
                for (int i = 0; i < n; i++)
                {
                    var c = fr.Cells[i];
                    glyphs[i] = c.Glyph;
                    colors[i] = ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
                }
                list.Add(new FracturingFog.Render.AsciiFrame(Cols, Rows, glyphs, colors, true));
            }
            return list;
        }

        /// <summary>Suggested file extension for a format.</summary>
        public static string ExtensionFor(AsciiAnimationFormat fmt) => fmt switch
        {
            AsciiAnimationFormat.AsciinemaCast => ".cast",
            AsciiAnimationFormat.AnimatedSvg   => ".svg",
            AsciiAnimationFormat.AnsiSequence  => ".ans",
            _                                  => ".txt",
        };

        /// <summary>Serialize the captured frames to <paramref name="fmt"/>.
        /// <paramref name="options"/> supplies visual knobs shared with the still
        /// exporter (background, font size); pass the same options used to build
        /// the frames.</summary>
        public string Serialize(AsciiAnimationFormat fmt, AsciiArtOptions? options = null)
        {
            if (_frames.Count == 0) throw new InvalidOperationException("no frames recorded");
            var opt = options ?? new AsciiArtOptions();
            return fmt switch
            {
                AsciiAnimationFormat.AsciinemaCast => SerializeCast(opt),
                AsciiAnimationFormat.AnimatedSvg   => SerializeSvg(opt),
                AsciiAnimationFormat.AnsiSequence  => SerializeAnsiSequence(opt),
                _                                  => SerializeAnsiSequence(opt),
            };
        }

        /// <summary>Serialize then write UTF-8 (no BOM) to <paramref name="path"/>.</summary>
        public void WriteToFile(AsciiAnimationFormat fmt, string path, AsciiArtOptions? options = null)
        {
            string text = Serialize(fmt, options);
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        // ── asciinema cast v2 ─────────────────────────────────────────────
        //
        // Header JSON line, then one output event per frame at its cumulative
        // start time. Each event homes the cursor (first frame clears) and writes
        // the truecolor grid, so playback overwrites in place.

        private string SerializeCast(AsciiArtOptions opt)
        {
            var sb = new StringBuilder(_frames.Count * Cols * Rows * 20);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            sb.Append(string.Create(CultureInfo.InvariantCulture,
                $"{{\"version\":2,\"width\":{Cols},\"height\":{Rows},\"timestamp\":{ts},\"env\":{{\"TERM\":\"xterm-256color\"}}}}\n"));

            double t = 0.0;
            for (int f = 0; f < _frames.Count; f++)
            {
                var grid = new StringBuilder(Cols * Rows * 20);
                grid.Append(f == 0 ? "\x1b[2J\x1b[H" : "\x1b[H");
                AppendAnsiGrid(grid, _frames[f].Cells, "\r\n");
                sb.Append('[')
                  .Append(t.ToString("0.000", CultureInfo.InvariantCulture))
                  .Append(", \"o\", \"");
                JsonEscape(sb, grid.ToString());
                sb.Append("\"]\n");
                t += _frames[f].Hold;
            }
            return sb.ToString();
        }

        // ── Animated SVG ──────────────────────────────────────────────────
        //
        // Each frame is a <g> holding one <text> per row of coloured <tspan>s
        // (same geometry as AsciiArtRenderer's still SVG). A discrete opacity
        // <animate> shows exactly one group per time slot, looping forever.

        private string SerializeSvg(AsciiArtOptions opt)
        {
            double fs = opt.FontSizePx;
            double advance = fs * 0.6;
            double lineH = fs;
            double canvasW = advance * Cols;
            double canvasH = lineH * Rows;
            int n = _frames.Count;
            double dur = Math.Max(1e-3, TotalSeconds);

            // keyTimes: cumulative frame-start fractions, ending at 1.0.
            var keyTimes = new string[n + 1];
            double acc = 0.0;
            keyTimes[0] = "0";
            for (int i = 0; i < n; i++)
            {
                acc += _frames[i].Hold;
                keyTimes[i + 1] = (acc / dur).ToString("0.####", CultureInfo.InvariantCulture);
            }
            keyTimes[n] = "1"; // guard against rounding drift

            var sb = new StringBuilder(n * Cols * Rows * 20);
            sb.Append(string.Create(CultureInfo.InvariantCulture,
                $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{canvasW:0.#}\" height=\"{canvasH:0.#}\" viewBox=\"0 0 {canvasW:0.#} {canvasH:0.#}\">\n"));
            sb.Append(string.Create(CultureInfo.InvariantCulture,
                $"<rect width=\"100%\" height=\"100%\" fill=\"{opt.BackgroundCss}\"/>\n"));
            sb.Append(string.Create(CultureInfo.InvariantCulture,
                $"<g font-family=\"monospace\" font-size=\"{fs:0.#}\" xml:space=\"preserve\">\n"));

            string keyTimesAttr = string.Join(";", keyTimes);
            for (int f = 0; f < n; f++)
            {
                // Discrete opacity: 1 during this frame's slot, 0 otherwise. The
                // values list has n+1 stops to match keyTimes (last == first).
                var vals = new string[n + 1];
                for (int k = 0; k <= n; k++) vals[k] = (k == f) ? "1" : "0";

                sb.Append("<g opacity=\"").Append(f == 0 ? "1" : "0").Append("\">");
                sb.Append(string.Create(CultureInfo.InvariantCulture,
                    $"<animate attributeName=\"opacity\" values=\"{string.Join(";", vals)}\" keyTimes=\"{keyTimesAttr}\" dur=\"{dur.ToString("0.###", CultureInfo.InvariantCulture)}s\" calcMode=\"discrete\" repeatCount=\"indefinite\"/>"));

                var cells = _frames[f].Cells;
                for (int y = 0; y < Rows; y++)
                {
                    double baseline = (y + 0.8) * lineH;
                    sb.Append(string.Create(CultureInfo.InvariantCulture,
                        $"<text x=\"0\" y=\"{baseline:0.##}\">"));
                    for (int x = 0; x < Cols; x++)
                    {
                        var c = cells[y * Cols + x];
                        double gx = x * advance;
                        sb.Append(string.Create(CultureInfo.InvariantCulture,
                            $"<tspan x=\"{gx:0.##}\" fill=\"#{Hex2(c.R)}{Hex2(c.G)}{Hex2(c.B)}\">"))
                          .Append(XmlEscape(c.Glyph)).Append("</tspan>");
                    }
                    sb.Append("</text>");
                }
                sb.Append("</g>\n");
            }
            sb.Append("</g></svg>\n");
            return sb.ToString();
        }

        // ── Raw ANSI frame sequence ───────────────────────────────────────

        private string SerializeAnsiSequence(AsciiArtOptions opt)
        {
            var sb = new StringBuilder(_frames.Count * Cols * Rows * 20);
            for (int f = 0; f < _frames.Count; f++)
            {
                sb.Append("\x1b[2J\x1b[3J\x1b[H"); // clear screen + scrollback + home
                AppendAnsiGrid(sb, _frames[f].Cells, "\n");
            }
            return sb.ToString();
        }

        // ── shared helpers ────────────────────────────────────────────────

        // One truecolor-ANSI grid: per row, per cell a 24-bit fg SGR + glyph,
        // reset at end of row. Skips redundant colour codes for spaces (blank).
        private void AppendAnsiGrid(StringBuilder sb, AsciiCell[] cells, string newline)
        {
            for (int y = 0; y < Rows; y++)
            {
                int r = -1, g = -1, b = -1; // last emitted colour, force first
                for (int x = 0; x < Cols; x++)
                {
                    var c = cells[y * Cols + x];
                    if (c.Glyph == ' ')
                    {
                        sb.Append(' ');
                        continue;
                    }
                    if (c.R != r || c.G != g || c.B != b)
                    {
                        sb.Append("\x1b[38;2;")
                          .Append(c.R).Append(';').Append(c.G).Append(';').Append(c.B).Append('m');
                        r = c.R; g = c.G; b = c.B;
                    }
                    sb.Append(c.Glyph);
                }
                sb.Append("\x1b[0m").Append(newline);
            }
        }

        private static string Hex2(byte v) => v.ToString("x2", CultureInfo.InvariantCulture);

        private static string XmlEscape(char g) => g switch
        {
            '<' => "&lt;",
            '>' => "&gt;",
            '&' => "&amp;",
            _   => g.ToString(),
        };

        // Minimal JSON string escaping for the cast data payload (ANSI text with
        // ESC/CR/LF control bytes).
        private static void JsonEscape(StringBuilder sb, string s)
        {
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
        }
    }
}
