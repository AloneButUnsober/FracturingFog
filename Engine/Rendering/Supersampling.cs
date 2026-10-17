// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Rendering/Supersampling.cs
//
// The 2D sub-pixel anti-aliasing of QualityPreset.AaSamples (Wave 2.6, MSAA in
// the code's naming): N = side² samples per pixel on a side × side jitter grid,
// the unjittered frame counting as the grid's centre sample, averaged per BGRA
// channel. Moved here from FractalRenderHost (#1344) so the live view, --batch
// and export all run the same code (batch and export used to skip it, so a High /
// Ultra / Extreme render came out un-anti-aliased headless).
//
// #1344 — adaptive mode (Mandelbrot): most pixels of a typical frame sit in flat
// bands or in the set, where N samples average to the one already computed.
//   1. The unjittered frame is sample 0.
//   2. A pixel is marked when a neighbour (8-connected) is on the other side of
//      the set boundary, or differs from it by more than Threshold in any channel
//      (BuildMask).
//   3. The other N − 1 samples are computed for the marked pixels only
//      (MandelbrotCalculator.PixelMask), in an order that spreads them over the
//      pixel, and a pixel stops early once the standard error of its mean is
//      within Tolerance in every channel (after at least MinSamples).
// Unmarked pixels keep sample 0's colour exactly. A marked pixel that takes all N
// samples gets exactly the full-supersample colour: the same jitter positions,
// the same integer sums and rounding (the order of the sum doesn't matter).
// Off by default (FractalParameters.AdaptiveAa), where this is the full grid as
// before, byte for byte.

using System;
using System.Collections.Generic;
using System.Threading;
using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog.Rendering
{
    /// <summary>#1344 — the adaptive anti-aliasing settings (see the file header).</summary>
    public readonly record struct AdaptiveAa(bool Enabled, double Threshold, double Tolerance)
    {
        public const double DefaultThreshold = 12.0;
        public const double DefaultTolerance = 1.5;

        /// <summary>Full supersampling (adaptive off).</summary>
        public static readonly AdaptiveAa Off = new(false, DefaultThreshold, DefaultTolerance);

        public static AdaptiveAa From(FractalParameters? p) => p == null ? Off
            : new(p.AdaptiveAa, p.AdaptiveAaThreshold, p.AdaptiveAaTolerance);
    }

    /// <summary>What a supersample pass computed (tests, probes, HUD).</summary>
    public readonly record struct SupersampleStats(int Pixels, int Samples, int Marked, long PixelSamples)
    {
        /// <summary>Pixel-samples full supersampling computes: every pixel, every sample.</summary>
        public long FullPixelSamples => (long)Pixels * Samples;
    }

    public static class Supersampling
    {
        /// <summary>A pixel stops sampling early only after this many samples (incl. sample 0).</summary>
        public const int MinSamples = 4;

        /// <summary>The jitter offsets (pixels, in [−½, ½)) of an N = side² grid other
        /// than its centre cell, which the unjittered frame stands in for; row-major,
        /// the full pass's order.</summary>
        public static List<(double Jx, double Jy)> GridOffsets(int side)
        {
            var list = new List<(double, double)>(side * side);
            for (int sy = 0; sy < side; sy++)
                for (int sx = 0; sx < side; sx++)
                {
                    if (sx == side / 2 && sy == side / 2) continue;
                    list.Add(((sx + 0.5) / side - 0.5, (sy + 0.5) / side - 0.5));
                }
            return list;
        }

        /// <summary>#1344 — <see cref="GridOffsets"/> reordered so any prefix is spread
        /// over the pixel: each next offset is the one farthest from those already
        /// taken (the unjittered centre included), ties to the earlier one.</summary>
        public static List<(double Jx, double Jy)> ProgressiveOffsets(int side)
        {
            var rest = GridOffsets(side);
            var taken = new List<(double Jx, double Jy)> { (0, 0) };
            var order = new List<(double, double)>(rest.Count);
            while (rest.Count > 0)
            {
                int best = 0; double bestD = -1;
                for (int i = 0; i < rest.Count; i++)
                {
                    double d = double.MaxValue;
                    foreach (var t in taken)
                    {
                        double dx = rest[i].Jx - t.Jx, dy = rest[i].Jy - t.Jy;
                        d = Math.Min(d, dx * dx + dy * dy);
                    }
                    if (d > bestD + 1e-12) { bestD = d; best = i; }
                }
                order.Add(rest[best]);
                taken.Add(rest[best]);
                rest.RemoveAt(best);
            }
            return order;
        }

        /// <summary>#1344 — mark (1) each pixel with an 8-connected neighbour on the
        /// other side of the set boundary (iteration ≥ <paramref name="maxIter"/>) or
        /// more than <paramref name="threshold"/> apart in any BGRA channel; clear (0)
        /// the rest. Returns the marked count.</summary>
        public static int BuildMask(uint[] color, int[] iter, int maxIter, int w, int h, double threshold, byte[] mask)
        {
            int marked = 0;
            System.Threading.Tasks.Parallel.For(0, h, () => 0, (y, _, local) =>
            {
                int y0 = Math.Max(0, y - 1), y1 = Math.Min(h - 1, y + 1);
                int rb = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = rb + x;
                    uint c = color[i];
                    bool inSet = iter[i] >= maxIter;
                    int x0 = Math.Max(0, x - 1), x1 = Math.Min(w - 1, x + 1);
                    byte m = 0;
                    for (int ny = y0; ny <= y1 && m == 0; ny++)
                    {
                        int nb = ny * w;
                        for (int nx = x0; nx <= x1; nx++)
                        {
                            int j = nb + nx;
                            if (j == i) continue;
                            if ((iter[j] >= maxIter) != inSet || MaxChannelDiff(c, color[j]) > threshold) { m = 1; break; }
                        }
                    }
                    mask[i] = m;
                    local += m;
                }
                return local;
            }, local => Interlocked.Add(ref marked, local));
            return marked;
        }

        private static int MaxChannelDiff(uint a, uint b)
        {
            int d0 = Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF));
            int d1 = Math.Abs((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF));
            int d2 = Math.Abs((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF));
            int d3 = Math.Abs((int)(a >> 24) - (int)(b >> 24));
            return Math.Max(Math.Max(d0, d1), Math.Max(d2, d3));
        }

        /// <summary>Supersample <paramref name="calc"/>'s just-computed frame (its
        /// ColorBuffer is sample 0) with <paramref name="aaSamples"/> = side² samples
        /// per pixel, fully or (<paramref name="adaptive"/>) only where needed.
        /// Restores the view centre (every limb) on exit; a cancellation averages the
        /// samples taken so far.</summary>
        public static SupersampleStats AccumulateMandelbrot(MandelbrotCalculator calc, int aaSamples,
            AdaptiveAa adaptive, CancellationToken token)
        {
            int side = (int)Math.Round(Math.Sqrt(aaSamples));
            int pixels = calc.Width * calc.Height;
            if (aaSamples <= 1 || side * side != aaSamples || calc.ColorBuffer.Length < pixels)
                return new SupersampleStats(pixels, 1, 0, pixels);
            return adaptive.Enabled
                ? Adaptive(calc, side, adaptive, token)
                : Full(calc, side, token);
        }

        // The pre-#1344 pass (FractalRenderHost.RunMsaaAccumulateMandelbrot), unchanged.
        private static SupersampleStats Full(MandelbrotCalculator calc, int side, CancellationToken token)
        {
            int pixels = calc.Width * calc.Height;
            uint[] color = calc.ColorBuffer;
            var sumR = new int[pixels];
            var sumG = new int[pixels];
            var sumB = new int[pixels];
            var sumA = new int[pixels];
            Add(color, pixels, sumR, sumG, sumB, sumA);

            // Pixel scale matches generated calculators: 3.5/max(W,H)/Zoom.
            // #1270 — the jitter goes through every centre limb (CentreJitter),
            // so it survives past the double's precision (~1e10 zoom).
            double scale = (3.5 / Math.Max(calc.Width, calc.Height)) / calc.Zoom;
            var orig = CentreLimbs.Capture(calc);
            int count = 1; // sample 0 already counted
            try
            {
                foreach (var (jx, jy) in GridOffsets(side))
                {
                    if (token.IsCancellationRequested) break;
                    CentreJitter.Apply(calc, in orig, jx, jy, scale);
                    try { calc.Calculate(token); }
                    catch (OperationCanceledException) { break; }
                    Add(calc.ColorBuffer, pixels, sumR, sumG, sumB, sumA);
                    count++;
                }
            }
            finally { orig.Restore(calc); }
            WriteMeans(calc.ColorBuffer, pixels, sumR, sumG, sumB, sumA, count);
            return new SupersampleStats(pixels, side * side, pixels, (long)pixels * count);
        }

        private static SupersampleStats Adaptive(MandelbrotCalculator calc, int side, AdaptiveAa a, CancellationToken token)
        {
            int w = calc.Width, h = calc.Height, pixels = w * h;
            var color0 = (uint[])calc.ColorBuffer.Clone();
            var mask = new byte[pixels];
            int marked = BuildMask(color0, calc.IterationBuffer, calc.MaxIterations, w, h, a.Threshold, mask);
            long pixelSamples = pixels;
            if (marked == 0) return new SupersampleStats(pixels, side * side, 0, pixelSamples);

            var sumR = new int[pixels]; var sumG = new int[pixels]; var sumB = new int[pixels]; var sumA = new int[pixels];
            bool early = a.Tolerance > 0;
            var sqR = early ? new int[pixels] : null; var sqG = early ? new int[pixels] : null;
            var sqB = early ? new int[pixels] : null; var sqA = early ? new int[pixels] : null;
            var n = new int[pixels];
            var active = (byte[])mask.Clone();
            // The marked pixels as a list, compacted as they converge: the per-sample
            // bookkeeping costs what the marked pixels do, not the frame.
            var list = new int[marked];
            int activeCount = 0;
            for (int i = 0; i < pixels; i++)
                if (mask[i] != 0)
                {
                    AddOne(color0[i], i, sumR, sumG, sumB, sumA, sqR, sqG, sqB, sqA);
                    n[i] = 1;
                    list[activeCount++] = i;
                }

            double scale = (3.5 / Math.Max(w, h)) / calc.Zoom;
            var orig = CentreLimbs.Capture(calc);
            double tol2 = a.Tolerance * a.Tolerance;
            try
            {
                calc.PixelMask = active;
                foreach (var (jx, jy) in ProgressiveOffsets(side))
                {
                    if (activeCount == 0 || token.IsCancellationRequested) break;
                    CentreJitter.Apply(calc, in orig, jx, jy, scale);
                    try { calc.Calculate(token); }
                    catch (OperationCanceledException) { break; }
                    var buf = calc.ColorBuffer;
                    pixelSamples += activeCount;
                    int keep = 0;
                    for (int l = 0; l < activeCount; l++)
                    {
                        int i = list[l];
                        AddOne(buf[i], i, sumR, sumG, sumB, sumA, sqR, sqG, sqB, sqA);
                        int k = ++n[i];
                        if (early && k >= MinSamples
                            && Converged(sumR[i], sqR![i], k, tol2) && Converged(sumG[i], sqG![i], k, tol2)
                            && Converged(sumB[i], sqB![i], k, tol2) && Converged(sumA[i], sqA![i], k, tol2))
                            active[i] = 0;
                        else list[keep++] = i;
                    }
                    activeCount = keep;
                }
            }
            finally
            {
                calc.PixelMask = null;
                orig.Restore(calc);
            }

            var outBuf = calc.ColorBuffer;
            Array.Copy(color0, outBuf, pixels);
            for (int i = 0; i < pixels; i++)
                if (mask[i] != 0) outBuf[i] = Mean(sumR[i], sumG[i], sumB[i], sumA[i], n[i]);
            return new SupersampleStats(pixels, side * side, marked, pixelSamples);
        }

        // Standard error of the mean of k samples (sum, sum of squares) within tol.
        private static bool Converged(int sum, int sq, int k, double tol2)
        {
            double mean = (double)sum / k;
            double var = Math.Max(0, (double)sq / k - mean * mean);
            return var / k <= tol2;
        }

        /// <summary>Wave 2.6 alt-calc twin (IFractalCalculator escape-time families and
        /// the 3D raymarchers whose Calculate honours centre + zoom): the full grid,
        /// jittering CenterX / CenterY. Unchanged from FractalRenderHost.</summary>
        public static SupersampleStats AccumulateAlt(IFractalCalculator calc, int aaSamples, CancellationToken token)
        {
            int side = (int)Math.Round(Math.Sqrt(aaSamples));
            int pixels = calc.Width * calc.Height;
            uint[] color = calc.ColorBuffer;
            if (aaSamples <= 1 || side * side != aaSamples || color.Length < pixels)
                return new SupersampleStats(pixels, 1, 0, pixels);

            var sumR = new int[pixels];
            var sumG = new int[pixels];
            var sumB = new int[pixels];
            var sumA = new int[pixels];
            Add(color, pixels, sumR, sumG, sumB, sumA);

            double scale = (3.5 / Math.Max(calc.Width, calc.Height)) / calc.Zoom;
            double origCx = calc.CenterX;
            double origCy = calc.CenterY;
            int count = 1;
            try
            {
                foreach (var (jx, jy) in GridOffsets(side))
                {
                    if (token.IsCancellationRequested) break;
                    calc.CenterX = origCx + jx * scale;
                    calc.CenterY = origCy + jy * scale;
                    try { calc.Calculate(token); }
                    catch (OperationCanceledException) { break; }
                    Add(calc.ColorBuffer, pixels, sumR, sumG, sumB, sumA);
                    count++;
                }
            }
            finally
            {
                calc.CenterX = origCx;
                calc.CenterY = origCy;
            }
            WriteMeans(calc.ColorBuffer, pixels, sumR, sumG, sumB, sumA, count);
            return new SupersampleStats(pixels, side * side, pixels, (long)pixels * count);
        }

        // Format: ColorBuffer is uint with packed BGRA (matches IColorMap.Map's
        // output everywhere; the renderer's UpdateTexture also expects BGRA). The
        // byte extraction is endian-safe: the same uint is read and written.
        private static void Add(uint[] buf, int pixels, int[] sumR, int[] sumG, int[] sumB, int[] sumA)
        {
            for (int i = 0; i < pixels; i++)
            {
                uint c = buf[i];
                sumB[i] += (int)(c & 0xFF);
                sumG[i] += (int)((c >> 8) & 0xFF);
                sumR[i] += (int)((c >> 16) & 0xFF);
                sumA[i] += (int)((c >> 24) & 0xFF);
            }
        }

        private static void AddOne(uint c, int i, int[] sumR, int[] sumG, int[] sumB, int[] sumA,
            int[]? sqR, int[]? sqG, int[]? sqB, int[]? sqA)
        {
            int b = (int)(c & 0xFF), g = (int)((c >> 8) & 0xFF), r = (int)((c >> 16) & 0xFF), al = (int)(c >> 24);
            sumB[i] += b; sumG[i] += g; sumR[i] += r; sumA[i] += al;
            if (sqR == null) return;
            sqB![i] += b * b; sqG![i] += g * g; sqR[i] += r * r; sqA![i] += al * al;
        }

        private static void WriteMeans(uint[] outBuf, int pixels, int[] sumR, int[] sumG, int[] sumB, int[] sumA, int count)
        {
            if (count <= 0) return;
            for (int i = 0; i < pixels; i++) outBuf[i] = Mean(sumR[i], sumG[i], sumB[i], sumA[i], count);
        }

        private static uint Mean(int sumR, int sumG, int sumB, int sumA, int count)
        {
            int half = count / 2;
            uint b = (uint)((sumB + half) / count) & 0xFF;
            uint g = (uint)((sumG + half) / count) & 0xFF;
            uint r = (uint)((sumR + half) / count) & 0xFF;
            uint a = (uint)((sumA + half) / count) & 0xFF;
            return (a << 24) | (r << 16) | (g << 8) | b;
        }
    }
}
