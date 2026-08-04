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
            int dLevels = Math.Max(2, fx.DitherLevels);
            double plasmaT = fx.TimeSeconds * fx.PlasmaSpeed;
            double plasmaK = Math.Max(1e-4, fx.PlasmaScale);
            double plasmaBlend = Math.Clamp(fx.PlasmaStrength, 0.0, 1.0);
            bool doPerCell = doGlyph || fx.HueCycle || fx.Monochrome || fx.Saturate
                || fx.Invert || fx.Solarize || fx.Quantize || fx.Dither || fx.Duotone || fx.Plasma;
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
                    if (fx.Dither)
                    {
                        double d = (Bayer4[(y & 3) * 4 + (x & 3)] + 0.5) / 16.0 - 0.5; // -0.5..0.5
                        r = DitherChannel(r, dLevels, d);
                        g = DitherChannel(g, dLevels, d);
                        b = DitherChannel(b, dLevels, d);
                    }
                    if (fx.Plasma)
                    {
                        double p = Math.Sin(x * plasmaK + plasmaT)
                                 + Math.Sin(y * plasmaK * 1.3 - plasmaT * 0.7)
                                 + Math.Sin((x + y) * plasmaK * 0.7 + plasmaT * 1.3);
                        p = p / 3.0 * 0.5 + 0.5;                 // 0..1
                        double t3 = p * 3.0;                      // fire ramp: black→red→yellow→white
                        byte pr = (byte)(Math.Clamp(t3, 0, 1) * 255);
                        byte pg = (byte)(Math.Clamp(t3 - 1, 0, 1) * 255);
                        byte pb = (byte)(Math.Clamp(t3 - 2, 0, 1) * 255);
                        r = (byte)(r * (1 - plasmaBlend) + pr * plasmaBlend);
                        g = (byte)(g * (1 - plasmaBlend) + pg * plasmaBlend);
                        b = (byte)(b * (1 - plasmaBlend) + pb * plasmaBlend);
                    }
                    cells[i] = new AsciiCell(glyph, r, g, b);
                }
            }

            // Structural overlays (stateful). Advance the shared clock ONCE per
            // frame, then hand the delta to each stateful pass.
            if (state != null && (fx.MatrixRain || fx.Particles))
            {
                state.EnsureSize(cols, rows);
                double dt = state.AdvanceClock(fx.TimeSeconds);
                if (fx.MatrixRain) RainPass(cells, cols, rows, fx, state, dt);
                if (fx.Particles) ParticlePass(cells, cols, rows, fx, state, dt);
            }

            // Spatial stage: effects that read neighbours or displace cells. Each
            // snapshots the current grid so reads see pre-effect values.
            if (fx.ChromaticAberration)
            {
                int shift = Math.Max(0, fx.ChromaticShift);
                if (shift > 0)
                {
                    var src = (AsciiCell[])cells.Clone();
                    for (int y = 0; y < rows; y++)
                        for (int x = 0; x < cols; x++)
                        {
                            var mid = src[y * cols + x];
                            byte rr = src[y * cols + Clamp(x - shift, 0, cols - 1)].R;
                            byte bb = src[y * cols + Clamp(x + shift, 0, cols - 1)].B;
                            cells[y * cols + x] = new AsciiCell(mid.Glyph, rr, mid.G, bb);
                        }
                }
            }

            if (fx.Wave && fx.WaveAmplitude != 0)
            {
                double wl = Math.Max(1e-3, fx.WaveLength);
                double phase = fx.TimeSeconds * fx.WaveSpeed;
                var src = (AsciiCell[])cells.Clone();
                for (int y = 0; y < rows; y++)
                {
                    int off = (int)Math.Round(fx.WaveAmplitude * Math.Sin(y / wl * 2.0 * Math.PI + phase));
                    for (int x = 0; x < cols; x++)
                        cells[y * cols + x] = src[y * cols + Clamp(x - off, 0, cols - 1)];
                }
            }

            if (fx.Drift && (fx.DriftDxPerSec != 0 || fx.DriftDyPerSec != 0))
            {
                int dx = ((int)Math.Round(fx.TimeSeconds * fx.DriftDxPerSec) % cols + cols) % cols;
                int dy = ((int)Math.Round(fx.TimeSeconds * fx.DriftDyPerSec) % rows + rows) % rows;
                if (dx != 0 || dy != 0)
                {
                    var src = (AsciiCell[])cells.Clone();
                    for (int y = 0; y < rows; y++)
                        for (int x = 0; x < cols; x++)
                            cells[y * cols + x] = src[((y - dy + rows) % rows) * cols + ((x - dx + cols) % cols)];
                }
            }

            if (fx.Twist && fx.TwistStrength != 0)
            {
                var src = (AsciiCell[])cells.Clone();
                double cx = (cols - 1) * 0.5, cy = (rows - 1) * 0.5;
                double maxR = Math.Max(1e-3, Math.Sqrt(cx * cx + cy * cy));
                for (int y = 0; y < rows; y++)
                    for (int x = 0; x < cols; x++)
                    {
                        double dx = x - cx, dy = y - cy;
                        double r = Math.Sqrt(dx * dx + dy * dy);
                        double a = fx.TwistStrength * (1.0 - r / maxR);   // sample source rotated back
                        double ca = Math.Cos(a), sa = Math.Sin(a);
                        int sx = Clamp((int)Math.Round(cx + dx * ca - dy * sa), 0, cols - 1);
                        int sy = Clamp((int)Math.Round(cy + dx * sa + dy * ca), 0, rows - 1);
                        cells[y * cols + x] = src[sy * cols + sx];
                    }
            }

            if (fx.Glitch && fx.GlitchIntensity > 0)
            {
                int frame = (int)Math.Floor(fx.TimeSeconds * fx.GlitchHz);
                uint thresh = (uint)(Math.Clamp(fx.GlitchIntensity, 0.0, 1.0) * uint.MaxValue);
                int maxShift = Math.Max(1, cols / 5);
                var src = (AsciiCell[])cells.Clone();
                for (int y = 0; y < rows; y++)
                {
                    uint h = Hash(0, y, frame * 7);
                    if (h >= thresh) continue;                       // this row untorn
                    int shift = (int)((h >> 9) % (uint)(2 * maxShift + 1)) - maxShift;
                    if (shift == 0) continue;
                    for (int x = 0; x < cols; x++)
                        cells[y * cols + x] = src[y * cols + Clamp(x - shift, 0, cols - 1)];
                }
            }

            if (fx.Bloom && fx.BloomStrength > 0)
            {
                var src = (AsciiCell[])cells.Clone();
                double thr = Math.Clamp(fx.BloomThreshold, 0.0, 1.0) * 255.0;
                double str = fx.BloomStrength;
                for (int y = 0; y < rows; y++)
                    for (int x = 0; x < cols; x++)
                    {
                        double ar = 0, ag = 0, ab = 0; int n = 0;
                        for (int ny = Math.Max(0, y - 1); ny <= Math.Min(rows - 1, y + 1); ny++)
                            for (int nx = Math.Max(0, x - 1); nx <= Math.Min(cols - 1, x + 1); nx++)
                            {
                                var s = src[ny * cols + nx];
                                double nl = 0.2126 * s.R + 0.7152 * s.G + 0.0722 * s.B;
                                if (nl > thr) { ar += s.R; ag += s.G; ab += s.B; n++; }
                            }
                        var c = src[y * cols + x];
                        if (n == 0) { cells[y * cols + x] = c; continue; }
                        double k = str / n;
                        cells[y * cols + x] = new AsciiCell(c.Glyph,
                            (byte)Math.Clamp(c.R + ar * k, 0, 255),
                            (byte)Math.Clamp(c.G + ag * k, 0, 255),
                            (byte)Math.Clamp(c.B + ab * k, 0, 255));
                    }
            }

            if (fx.Edge)
            {
                var src = (AsciiCell[])cells.Clone();
                double thr = Math.Clamp(fx.EdgeThreshold, 0.0, 1.0) * 1020.0; // Sobel mag scale
                for (int y = 0; y < rows; y++)
                    for (int x = 0; x < cols; x++)
                    {
                        double gx = EdgeLuma(src, cols, rows, x + 1, y) - EdgeLuma(src, cols, rows, x - 1, y);
                        double gy = EdgeLuma(src, cols, rows, x, y + 1) - EdgeLuma(src, cols, rows, x, y - 1);
                        double mag = Math.Sqrt(gx * gx + gy * gy);
                        var c = src[y * cols + x];
                        if (mag >= thr)
                        {
                            // Edge orientation → a line glyph perpendicular to the gradient.
                            double ang = Math.Atan2(gy, gx);
                            char e = OrientedEdgeGlyph(ang);
                            cells[y * cols + x] = new AsciiCell(e, c.R, c.G, c.B);
                        }
                        else
                        {
                            cells[y * cols + x] = new AsciiCell(' ',
                                (byte)(c.R * 0.1), (byte)(c.G * 0.1), (byte)(c.B * 0.1));
                        }
                    }
            }

            // Transitions: gate cells to blank until revealed, over the transition.
            if (fx.Typewriter || fx.Dissolve)
            {
                double dur = Math.Max(1e-4, fx.TransitionSeconds);
                double progress = Math.Clamp(fx.TimeSeconds / dur, 0.0, 1.0);
                if (progress < 1.0)
                {
                    if (fx.Typewriter)
                    {
                        long revealed = (long)(progress * cols * rows);
                        for (long i = revealed; i < (long)cols * rows; i++)
                            cells[i] = new AsciiCell(' ', 0, 0, 0);
                    }
                    if (fx.Dissolve)
                    {
                        uint thresh = (uint)(progress * uint.MaxValue);
                        for (int y = 0; y < rows; y++)
                            for (int x = 0; x < cols; x++)
                                if (Hash(x, y, 0) >= thresh)
                                    cells[y * cols + x] = new AsciiCell(' ', 0, 0, 0);
                    }
                }
            }

            // Shading (last): vignette, then CRT scanline dim.
            if (fx.Vignette)
            {
                double strength = Math.Clamp(fx.VignetteStrength, 0.0, 1.0);
                double cx = (cols - 1) * 0.5, cy = (rows - 1) * 0.5;
                double maxD2 = cx * cx + cy * cy;
                if (maxD2 < 1e-9) maxD2 = 1.0;
                for (int y = 0; y < rows; y++)
                    for (int x = 0; x < cols; x++)
                    {
                        double dx = x - cx, dy = y - cy;
                        double f = 1.0 - strength * (dx * dx + dy * dy) / maxD2; // 1 centre → 1-strength corner
                        if (f < 0) f = 0;
                        int i = y * cols + x;
                        var c = cells[i];
                        cells[i] = new AsciiCell(c.Glyph,
                            (byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));
                    }
            }
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
            AsciiCell[] cells, int cols, int rows, AsciiFxSettings fx, AsciiFxState state, double dt)
        {
            if (!state.RainInitialised) state.InitRain(Math.Clamp(fx.MatrixRainDensity, 0.0, 1.0));

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

        // Drifting particles (snow / rain): fall down with a gentle horizontal
        // sway, wrap at the bottom, painted as a bright glyph over the grid.
        private static void ParticlePass(
            AsciiCell[] cells, int cols, int rows, AsciiFxSettings fx, AsciiFxState state, double dt)
        {
            int count = Math.Max(0, fx.ParticleCount);
            if (count == 0) return;
            if (!state.ParticlesInitialised || state.PartX.Length != count)
                state.InitParticles(count);

            for (int p = 0; p < count; p++)
            {
                state.PartY[p] += fx.ParticleSpeed * dt;
                if (state.PartY[p] >= rows)
                {
                    state.PartY[p] -= rows;                        // wrap to top
                    state.PartX[p] = state.Rng.NextDouble() * cols;
                }
                double sway = fx.ParticleSway * Math.Sin(state.PartY[p] * 0.4 + state.PartSway[p]);
                int x = Clamp((int)Math.Round(state.PartX[p] + sway), 0, cols - 1);
                int y = Clamp((int)state.PartY[p], 0, rows - 1);
                cells[y * cols + x] = new AsciiCell(fx.ParticleGlyph, 235, 240, 255); // near-white fleck
            }
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        // Clamped-edge luma sample for the Sobel edge pass.
        private static double EdgeLuma(AsciiCell[] src, int cols, int rows, int x, int y)
        {
            if (x < 0) x = 0; else if (x >= cols) x = cols - 1;
            if (y < 0) y = 0; else if (y >= rows) y = rows - 1;
            var c = src[y * cols + x];
            return 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
        }

        // Line glyph drawn perpendicular to a gradient at angle `ang` (radians).
        private static char OrientedEdgeGlyph(double ang)
        {
            double a = ang; if (a < 0) a += Math.PI; // fold to [0,π)
            double deg = a * 180.0 / Math.PI;
            if (deg < 22.5 || deg >= 157.5) return '|';   // horizontal gradient → vertical edge
            if (deg < 67.5) return '\\';
            if (deg < 112.5) return '-';                   // vertical gradient → horizontal edge
            return '/';
        }

        // Cheap stateless spatial-temporal hash → uniform-ish uint. Used by grain
        // so the noise is reproducible from (x, y, frame) with no RNG state.
        private static uint Hash(int x, int y, int frame)
        {
            uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(frame * 83492791);
            h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
            return h;
        }

        // Bayer 4×4 ordered-dither matrix (0..15).
        private static readonly int[] Bayer4 =
            { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

        // Ordered-dither one channel to N levels: bias by the Bayer offset (one
        // level-step wide) before snapping, so alternating cells straddle a level
        // boundary and average to intermediate tones.
        private static byte DitherChannel(byte v, int levels, double bias)
        {
            double step = 255.0 / (levels - 1);
            double q = Math.Round((v + bias * step) / step) * step;
            return (byte)Math.Clamp(q, 0, 255);
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
