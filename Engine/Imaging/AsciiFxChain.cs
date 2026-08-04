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
        public static void Apply(
            AsciiCell[] cells, int cols, int rows, string ramp, AsciiFxSettings fx)
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
            bool doGlyph = (fx.Breathe && rampLen > 1) || (swapLen > 1 && rampLen > 1);

            for (int y = 0; y < rows; y++)
            {
                bool dimRow = fx.Crt && (y & 1) == 1;
                double rowDim = dimRow ? Math.Clamp(fx.CrtScanlineDim, 0.0, 1.0) : 1.0;
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

                    // Colour-space: hue cycle, monochrome tint, then scanline dim.
                    if (fx.HueCycle && (r != 0 || g != 0 || b != 0))
                        RotateHue(ref r, ref g, ref b, hueShift);
                    if (fx.Monochrome)
                    {
                        // Preserve brightness (luma), replace chroma with the tint.
                        double luma = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
                        if (luma > 1.0) luma = 1.0;
                        r = (byte)Math.Round(fx.MonochromeR * luma);
                        g = (byte)Math.Round(fx.MonochromeG * luma);
                        b = (byte)Math.Round(fx.MonochromeB * luma);
                    }
                    if (rowDim < 1.0)
                    {
                        r = (byte)(r * rowDim);
                        g = (byte)(g * rowDim);
                        b = (byte)(b * rowDim);
                    }

                    cells[i] = new AsciiCell(glyph, r, g, b);
                }
            }
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

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
