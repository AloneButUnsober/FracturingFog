// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/AsciiFxChain.cs
//
// ASCII-native FX chain (#229). Transforms a rendered AsciiCell grid in place
// — colour and/or glyph — with cheap per-cell effects that need no fractal
// recompute. Applied by the host after RenderCells, before AsciiFrame
// conversion, so both the live view and (later) animation exports pick it up.

using System;

namespace FracturingFog.Imaging
{
    /// <summary>Post effects over an <see cref="AsciiCell"/> grid. See
    /// <see cref="AsciiFxSettings"/>.</summary>
    public static class AsciiFxChain
    {
        /// <summary>Apply the enabled effects to <paramref name="cells"/> in place.
        /// <paramref name="ramp"/> is the glyph ramp the cells were mapped from —
        /// needed by glyph-space effects (Breathe) to shift a cell along it.</summary>
        // Half-width katakana + digits — the canonical "digital rain" glyph pool.
        private const string MatrixGlyphs =
            "0123456789ABCDEFｱｲｳｴｵｶｷｸｹｺｻｼｽｾｿﾀﾁﾂﾃﾄﾅﾆﾇﾈﾉﾊﾋﾌﾍﾎ";

        public static void Apply(
            AsciiCell[] cells, int cols, int rows, string ramp, AsciiFxSettings fx,
            AsciiFxState? state = null)
        {
            if (cells is null || fx is null || !fx.AnyEnabled) return;

            // Ordered pipeline. Each stage is skipped unless its effect is on, so
            // the common single-effect case stays one pass. Glyph-space runs
            // before colour-space so density-changing effects (Breathe, charset
            // swap) settle the glyph first; shading (CRT) is last.
            //
            //   1. glyph-space   : Breathe, CharsetSwap   (per cell)
            //   2. colour-space  : HueCycle               (per cell)
            //   3. shading       : Crt scanline dim       (per row)

            // Per-frame constants.
            double hueShift = fx.HueCycle ? (fx.TimeSeconds * fx.HueCycleDegPerSec) % 360.0 : 0.0;
            double gamma = 1.0;
            if (fx.Breathe)
            {
                double s = Math.Sin(fx.TimeSeconds * fx.BreatheHz * 2.0 * Math.PI);
                gamma = Math.Max(0.05, fx.BreatheGammaMid + fx.BreatheGammaAmp * s);
            }
            int rampLen = ramp?.Length ?? 0;
            string? swap = fx.CharsetSwap ? fx.SwapRamp : null;
            int swapLen = swap?.Length ?? 0;
            int scrollOff = fx.RampScroll && rampLen > 1
                ? ((int)Math.Floor(fx.TimeSeconds * fx.RampScrollSpeed) % rampLen + rampLen) % rampLen : 0;
            int grainFrame = fx.Grain ? (int)Math.Floor(fx.TimeSeconds * fx.GrainHz) : 0;
            uint grainThresh = (uint)(Math.Clamp(fx.GrainAmount, 0.0, 1.0) * uint.MaxValue);
            bool doGlyph = ((fx.Breathe || fx.RampScroll || fx.Grain) && rampLen > 1)
                || (swapLen > 1 && rampLen > 1);

            double satScale = 1.0;
            if (fx.Saturate)
            {
                double s = fx.SaturateAmp != 0
                    ? Math.Sin(fx.TimeSeconds * fx.SaturateHz * 2.0 * Math.PI) : 0.0;
                satScale = Math.Max(0.0, fx.SaturateMid + fx.SaturateAmp * s);
            }

            byte solThresh = (byte)Math.Clamp(fx.SolarizeThreshold * 255.0, 0, 255);
            int qLevels = Math.Max(2, fx.QuantizeLevels);
            bool doPerCell = doGlyph || fx.HueCycle || fx.Monochrome || fx.Saturate
                || fx.Invert || fx.Solarize || fx.Quantize || fx.Duotone;
            if (doPerCell)
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < cols; x++)
                {
                    int i = y * cols + x;
                    var c = cells[i];
                    char glyph = c.Glyph;
                    byte r = c.R, g = c.G, b = c.B;

                    if (doGlyph && glyph != ' ' && rampLen > 1)
                    {
                        int idx = ramp!.IndexOf(glyph);
                        if (idx >= 0)
                        {
                            // Breathe: gamma on the normalized ramp index, so
                            // density pulses.
                            if (fx.Breathe)
                            {
                                double t = idx / (double)(rampLen - 1);
                                double tg = Math.Pow(t, gamma);
                                idx = Clamp((int)Math.Round(tg * (rampLen - 1)), 0, rampLen - 1);
                                glyph = ramp[idx];
                            }
                            // Ramp scroll: cyclic shift through the ramp → shimmer.
                            if (scrollOff != 0)
                            {
                                idx = (idx + scrollOff) % rampLen;
                                glyph = ramp[idx];
                            }
                            // Grain: hashed ±1 jitter, re-rolled per frame → twinkle.
                            if (fx.Grain)
                            {
                                uint h = Hash(x, y, grainFrame);
                                if (h < grainThresh)
                                {
                                    idx = Clamp(idx + ((h & 0x10000) != 0 ? 1 : -1), 0, rampLen - 1);
                                    glyph = ramp[idx];
                                }
                            }
                            // Charset swap: carry the (post-Breathe) density to the
                            // same fractional position along the replacement set.
                            if (swapLen > 1)
                            {
                                double t = idx / (double)(rampLen - 1);
                                int ni = Clamp((int)Math.Round(t * (swapLen - 1)), 0, swapLen - 1);
                                glyph = swap![ni];
                            }
                        }
                    }

                    // Colour-space. Duotone first (full remap from luma), then
                    // hue / saturation / tone can further process.
                    if (fx.Duotone)
                    {
                        double t = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
                        if (t > 1.0) t = 1.0;
                        r = (byte)Math.Round(fx.DuotoneLoR + (fx.DuotoneHiR - fx.DuotoneLoR) * t);
                        g = (byte)Math.Round(fx.DuotoneLoG + (fx.DuotoneHiG - fx.DuotoneLoG) * t);
                        b = (byte)Math.Round(fx.DuotoneLoB + (fx.DuotoneHiB - fx.DuotoneLoB) * t);
                    }
                    if (fx.HueCycle && (r != 0 || g != 0 || b != 0))
                        RotateHue(ref r, ref g, ref b, hueShift);
                    if (fx.Saturate)
                        ScaleSaturation(ref r, ref g, ref b, satScale);
                    if (fx.Invert)
                    {
                        r = (byte)(255 - r); g = (byte)(255 - g); b = (byte)(255 - b);
                    }
                    if (fx.Solarize)
                    {
                        if (r > solThresh) r = (byte)(255 - r);
                        if (g > solThresh) g = (byte)(255 - g);
                        if (b > solThresh) b = (byte)(255 - b);
                    }
                    if (fx.Monochrome)
                    {
                        // Preserve brightness (luma), replace chroma with the tint.
                        double luma = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
                        if (luma > 1.0) luma = 1.0;
                        r = (byte)Math.Round(fx.MonochromeR * luma);
                        g = (byte)Math.Round(fx.MonochromeG * luma);
                        b = (byte)Math.Round(fx.MonochromeB * luma);
                    }
                    if (fx.Quantize)
                    {
                        if (fx.QuantizeTerminal16) SnapTerminal16(ref r, ref g, ref b);
                        else { r = Posterize(r, qLevels); g = Posterize(g, qLevels); b = Posterize(b, qLevels); }
                    }
                    cells[i] = new AsciiCell(glyph, r, g, b);
                }
            }

            // Structural overlay (stateful): Matrix rain rewrites the grid.
            if (fx.MatrixRain && state != null)
                RainPass(cells, cols, rows, fx, state);

            // Shading (last): CRT scanline dim over whatever the stages produced.
            if (fx.Crt)
            {
                double dim = Math.Clamp(fx.CrtScanlineDim, 0.0, 1.0);
                for (int y = 1; y < rows; y += 2)
                    for (int x = 0; x < cols; x++)
                    {
                        int i = y * cols + x;
                        var c = cells[i];
                        cells[i] = new AsciiCell(c.Glyph,
                            (byte)(c.R * dim), (byte)(c.G * dim), (byte)(c.B * dim));
                    }
            }
        }

        // Matrix digital rain: per column a falling drop with a fading trail; the
        // underlying grid brightness masks it so the fractal ghosts through.
        private static void RainPass(
            AsciiCell[] cells, int cols, int rows, AsciiFxSettings fx, AsciiFxState state)
        {
            state.EnsureSize(cols, rows);
            if (!state.RainInitialised) state.InitRain(Math.Clamp(fx.MatrixRainDensity, 0.0, 1.0));
            double dt = state.AdvanceClock(fx.TimeSeconds);

            // Snapshot brightness (mask), then dim the background to a faint ghost.
            var luma = state.Luma;
            for (int i = 0; i < cells.Length; i++)
            {
                var c = cells[i];
                luma[i] = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
                cells[i] = new AsciiCell(c.Glyph,
                    (byte)(c.R * 0.12), (byte)(c.G * 0.12), (byte)(c.B * 0.12));
            }

            double maskAmt = Math.Clamp(fx.MatrixRainMask, 0.0, 1.0);
            var rng = state.Rng;
            for (int x = 0; x < cols; x++)
            {
                if (!state.RainActive[x]) continue;
                state.RainHead[x] += state.RainSpeed[x] * fx.MatrixRainSpeed * dt;
                if (state.RainHead[x] - state.RainLen[x] > rows)
                    state.RespawnRainColumn(x, aboveOnly: true);

                int head = (int)Math.Floor(state.RainHead[x]);
                int len = state.RainLen[x];
                for (int k = 0; k < len; k++)
                {
                    int row = head - k;
                    if (row < 0 || row >= rows) continue;
                    int i = row * cols + x;
                    double fall = 1.0 - (k / (double)len);        // 1 at head → 0 at tail
                    double mask = (1.0 - maskAmt) + maskAmt * luma[i];
                    double bright = fall * fall * Math.Clamp(mask, 0.0, 1.0);
                    char glyph = MatrixGlyphs[rng.Next(MatrixGlyphs.Length)];

                    byte r, g, b;
                    if (k == 0) { r = (byte)(200 * bright + 55); g = 255; b = (byte)(200 * bright + 55); } // near-white head
                    else { r = (byte)(30 * bright); g = (byte)(255 * bright); b = (byte)(70 * bright); }    // green trail
                    cells[i] = new AsciiCell(glyph, r, g, b);
                }
            }
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        // Cheap stateless spatial-temporal hash → uniform-ish uint. Used by grain
        // so the noise is reproducible from (x, y, frame) with no RNG state.
        private static uint Hash(int x, int y, int frame)
        {
            uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(frame * 83492791);
            h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
            return h;
        }

        // Snap a channel to N evenly-spaced levels across [0,255].
        private static byte Posterize(byte v, int levels)
        {
            double step = (levels - 1);
            int q = (int)Math.Round(v / 255.0 * step);
            return (byte)Math.Clamp(q / step * 255.0, 0, 255);
        }

        // Standard 16-colour ANSI palette (system + bright).
        private static readonly (byte r, byte g, byte b)[] Ansi16 =
        {
            (0,0,0),(128,0,0),(0,128,0),(128,128,0),(0,0,128),(128,0,128),(0,128,128),(192,192,192),
            (128,128,128),(255,0,0),(0,255,0),(255,255,0),(0,0,255),(255,0,255),(0,255,255),(255,255,255),
        };

        // Snap a colour to the nearest ANSI-16 palette entry (squared distance).
        private static void SnapTerminal16(ref byte r, ref byte g, ref byte b)
        {
            int best = 0, bestD = int.MaxValue;
            for (int i = 0; i < Ansi16.Length; i++)
            {
                int dr = r - Ansi16[i].r, dg = g - Ansi16[i].g, db = b - Ansi16[i].b;
                int d = dr * dr + dg * dg + db * db;
                if (d < bestD) { bestD = d; best = i; }
            }
            r = Ansi16[best].r; g = Ansi16[best].g; b = Ansi16[best].b;
        }

        // In-place saturation scale about the pixel's luma (grey axis). scale 0 →
        // greyscale, 1 → unchanged, >1 → more vivid (clamped to byte range).
        private static void ScaleSaturation(ref byte r, ref byte g, ref byte b, double scale)
        {
            double luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            r = (byte)Math.Clamp(luma + (r - luma) * scale, 0, 255);
            g = (byte)Math.Clamp(luma + (g - luma) * scale, 0, 255);
            b = (byte)Math.Clamp(luma + (b - luma) * scale, 0, 255);
        }

        // In-place RGB hue rotation by degrees. Standard HSV round-trip; cheap
        // enough per cell at ASCII grid sizes (a few thousand cells).
        private static void RotateHue(ref byte r, ref byte g, ref byte b, double deg)
        {
            double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
            double max = Math.Max(rf, Math.Max(gf, bf));
            double min = Math.Min(rf, Math.Min(gf, bf));
            double v = max, d = max - min;
            double s = max <= 0 ? 0 : d / max;
            double h = 0;
            if (d > 1e-9)
            {
                if (max == rf) h = ((gf - bf) / d) % 6.0;
                else if (max == gf) h = (bf - rf) / d + 2.0;
                else h = (rf - gf) / d + 4.0;
                h *= 60.0;
                if (h < 0) h += 360.0;
            }
            h = (h + deg) % 360.0;
            if (h < 0) h += 360.0;

            double c = v * s;
            double xx = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            double m = v - c;
            double rr, gg, bb;
            switch ((int)(h / 60.0))
            {
                case 0: rr = c; gg = xx; bb = 0; break;
                case 1: rr = xx; gg = c; bb = 0; break;
                case 2: rr = 0; gg = c; bb = xx; break;
                case 3: rr = 0; gg = xx; bb = c; break;
                case 4: rr = xx; gg = 0; bb = c; break;
                default: rr = c; gg = 0; bb = xx; break;
            }
            r = (byte)Math.Clamp((rr + m) * 255.0, 0, 255);
            g = (byte)Math.Clamp((gg + m) * 255.0, 0, 255);
            b = (byte)Math.Clamp((bb + m) * 255.0, 0, 255);
        }
    }
}
