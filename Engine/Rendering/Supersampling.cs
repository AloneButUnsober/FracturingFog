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
//
// #1381 — adaptive contrast (histogram equalisation, FractalViewState.HistogramEq)
// recolours a frame from its iteration / smooth buffers, which after a supersample
// pass hold only the last jittered sample. Equalising after the average therefore
// threw the anti-aliasing away. With histogramEq > 0 the pass builds one CDF from
// sample 0 (SampleEqualizer), equalises every sample with it and averages the
// equalised colours; the caller then skips its own equalisation
// (FinishHistogramEq). Without a supersample pass nothing changes.
//
// #1380 — the quality presets' temporal AA (QualityPreset.TaaMaxSamples: High 8,
// Ultra 16, Extreme 32), which the live view accumulates while it is still: the
// finished (MSAA) frame seeds a running per-channel sum, then each Halton-jittered
// full frame (TaaOffset) is added and the rounded mean written back. The live host
// runs one sample per frame through TemporalAccumulator; AccumulateTemporal runs
// them all at once for --batch / export stills, so a still equals the frame the
// live view settles to. AccumulateQuality chains MSAA → TAA → adaptive contrast.

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

        /// <summary>#1381 — the equalisation every sample went through (null: none, so
        /// the caller equalises as before).</summary>
        public SampleEqualizer? Equalizer { get; init; }

        /// <summary>#1381 — the colour is already the mean of equalised samples.</summary>
        public bool Equalized => Equalizer != null;

        /// <summary>#1380 — the temporal samples accumulated on top (incl. the seed);
        /// 0 when no TAA ran.</summary>
        public int TemporalSamples { get; init; }
    }

    /// <summary>#1380 — the live view's TAA running mean: a per-channel sum seeded
    /// with the finished frame, each later sample added, the mean (rounded half up)
    /// written back. One instance per view; shared by the host and the offline pass.</summary>
    public sealed class TemporalAccumulator
    {
        private long[] _r = Array.Empty<long>(), _g = Array.Empty<long>(), _b = Array.Empty<long>(), _a = Array.Empty<long>();

        public int Pixels { get; private set; }
        public int Count { get; private set; }

        /// <summary>Start over from <paramref name="color"/> (sample count 1).</summary>
        public void Seed(uint[] color, int pixels)
        {
            if (_r.Length != pixels)
            {
                _r = new long[pixels]; _g = new long[pixels]; _b = new long[pixels]; _a = new long[pixels];
            }
            Pixels = pixels;
            for (int i = 0; i < pixels; i++)
            {
                uint c = color[i];
                _b[i] = c & 0xFF;
                _g[i] = (c >> 8) & 0xFF;
                _r[i] = (c >> 16) & 0xFF;
                _a[i] = (c >> 24) & 0xFF;
            }
            Count = 1;
        }

        /// <summary>Add one more sample and overwrite <paramref name="color"/> with the
        /// running mean.</summary>
        public void AddAndWriteMean(uint[] color)
        {
            int pixels = Pixels;
            for (int i = 0; i < pixels; i++)
            {
                uint c = color[i];
                _b[i] += c & 0xFF;
                _g[i] += (c >> 8) & 0xFF;
                _r[i] += (c >> 16) & 0xFF;
                _a[i] += (c >> 24) & 0xFF;
            }
            int count = ++Count;
            long half = count / 2;
            for (int i = 0; i < pixels; i++)
            {
                uint b = (uint)((_b[i] + half) / count) & 0xFF;
                uint g = (uint)((_g[i] + half) / count) & 0xFF;
                uint r = (uint)((_r[i] + half) / count) & 0xFF;
                uint a = (uint)((_a[i] + half) / count) & 0xFF;
                color[i] = (a << 24) | (r << 16) | (g << 8) | b;
            }
        }
    }

    /// <summary>#1381 — adaptive contrast at a fixed CDF, so every sample of a pixel is
    /// equalised the same way (see the file header).</summary>
    public sealed class SampleEqualizer
    {
        public double[] Cdf { get; }
        public int Bins { get; }
        public int SourceMaxIter { get; }
        public double Strength { get; }

        private SampleEqualizer(double[] cdf, int bins, int sourceMaxIter, double strength)
        { Cdf = cdf; Bins = bins; SourceMaxIter = sourceMaxIter; Strength = strength; }

        /// <summary>The CDF of <paramref name="calc"/>'s current frame at
        /// <paramref name="histogramEq"/> % strength; null when it is 0, the calculator
        /// has no adaptive contrast, or no pixel escaped (the identity case).</summary>
        public static SampleEqualizer? Build(object? calc, int histogramEq)
        {
            if (histogramEq <= 0 || calc is not ISupportsHistogramEq he) return null;
            if (!he.BuildHistogramCdf(out double[]? cdf, out int bins, out int src) || cdf == null) return null;
            return new SampleEqualizer(cdf, bins, src, histogramEq / 100.0);
        }

        /// <summary>Recolour <paramref name="calc"/>'s current buffers with this CDF.</summary>
        public void Apply(object calc)
        {
            if (calc is ISupportsHistogramEq he)
                he.ApplyHistogramEqualizationWithCdf(Cdf, Bins, SourceMaxIter, Strength);
        }
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
        /// <paramref name="histogramEq"/> (%) &gt; 0 equalises every sample with the
        /// CDF of sample 0 first (#1381). Restores the view centre (every limb) on
        /// exit; a cancellation averages the samples taken so far.</summary>
        public static SupersampleStats AccumulateMandelbrot(MandelbrotCalculator calc, int aaSamples,
            AdaptiveAa adaptive, CancellationToken token, int histogramEq = 0)
        {
            if (!Supersamples(calc.Width, calc.Height, calc.ColorBuffer, aaSamples, out _)) return Single(calc.Width, calc.Height);
            var eq = SampleEqualizer.Build(calc, histogramEq);
            eq?.Apply(calc);
            return AccumulateMandelbrot(calc, aaSamples, adaptive, token, eq);
        }

        /// <summary>As above, with an equaliser the caller built from sample 0 and has
        /// already applied to it (the live view equalises every frame on its calc
        /// thread, #1381); null = no adaptive contrast.</summary>
        public static SupersampleStats AccumulateMandelbrot(MandelbrotCalculator calc, int aaSamples,
            AdaptiveAa adaptive, CancellationToken token, SampleEqualizer? equalizer)
        {
            if (!Supersamples(calc.Width, calc.Height, calc.ColorBuffer, aaSamples, out int side)) return Single(calc.Width, calc.Height);
            var st = adaptive.Enabled
                ? Adaptive(calc, side, adaptive, equalizer, token)
                : Full(calc, side, equalizer, token);
            return st with { Equalizer = equalizer };
        }

        // A pass runs for a square sample count > 1 over a full colour buffer.
        private static bool Supersamples(int w, int h, uint[] color, int aaSamples, out int side)
        {
            side = (int)Math.Round(Math.Sqrt(aaSamples));
            return aaSamples > 1 && side * side == aaSamples && color.Length >= w * h;
        }

        private static SupersampleStats Single(int w, int h) => new(w * h, 1, 0, (long)w * h);

        /// <summary>One pixel of the image in complex units: 3.5 / max(W, H) / zoom. A
        /// cluster tile (#1380 found) renders a sub-rect of a larger image
        /// (ImageWidth / ImageHeight), whose pixel is the whole image's, not the
        /// tile's — the jitter used the tile's and so overshot.</summary>
        public static double PixelScale(MandelbrotCalculator calc)
        {
            int w = calc.ImageWidth > 0 ? calc.ImageWidth : calc.Width;
            int h = calc.ImageHeight > 0 ? calc.ImageHeight : calc.Height;
            return (3.5 / Math.Max(w, h)) / calc.Zoom;
        }

        /// <summary>#1380 — the pixel-unit jitter of TAA sample <paramref name="sampleIndex"/>
        /// (1 … TaaMaxSamples − 1; the seed is sample 0): Halton bases 2 and 3 from
        /// index + 2, centred on the pixel.</summary>
        public static (double Jx, double Jy) TaaOffset(int sampleIndex)
            => (Halton(sampleIndex + 2, 2) - 0.5, Halton(sampleIndex + 2, 3) - 0.5);

        /// <summary>Halton radical inverse of <paramref name="index"/> in base <paramref name="b"/>, in [0, 1).</summary>
        public static double Halton(int index, int b)
        {
            double f = 1.0, r = 0.0;
            for (int i = index; i > 0; i /= b)
            {
                f /= b;
                r += f * (i % b);
            }
            return r;
        }

        /// <summary>#1380 — run the TAA samples the live view would accumulate on top
        /// of <paramref name="calc"/>'s finished frame (its ColorBuffer is the seed):
        /// samples 1 … <paramref name="taaMaxSamples"/> − 1 at <see cref="TaaOffset"/>,
        /// each equalised with <paramref name="equalizer"/> (#1381) when given, into a
        /// running mean left in ColorBuffer. Skipped (returns 0) when the centre can't
        /// represent a sub-pixel jitter, as live. Restores the centre; a cancellation
        /// keeps the mean so far. Returns the samples in the mean (incl. the seed).</summary>
        public static int AccumulateTemporal(MandelbrotCalculator calc, int taaMaxSamples,
            SampleEqualizer? equalizer, CancellationToken token)
        {
            int pixels = calc.Width * calc.Height;
            if (taaMaxSamples <= 1 || calc.ColorBuffer.Length < pixels) return 0;
            double scale = PixelScale(calc);
            if (!CentreJitter.IsResolvable(calc, scale)) return 0;

            var acc = new TemporalAccumulator();
            acc.Seed(calc.ColorBuffer, pixels);
            var orig = CentreLimbs.Capture(calc);
            try
            {
                for (int k = 1; k < taaMaxSamples; k++)
                {
                    if (token.IsCancellationRequested) break;
                    var (jx, jy) = TaaOffset(k);
                    CentreJitter.Apply(calc, in orig, jx, jy, scale);
                    var mean = (uint[])calc.ColorBuffer.Clone();   // the mean so far
                    try { calc.Calculate(token); }
                    catch (OperationCanceledException) { Array.Copy(mean, calc.ColorBuffer, pixels); break; }
                    equalizer?.Apply(calc);
                    acc.AddAndWriteMean(calc.ColorBuffer);
                }
            }
            finally { orig.Restore(calc); }
            return acc.Count;
        }

        /// <summary>#1380 — the quality preset's whole offline anti-aliasing for a
        /// Mandelbrot still: the sub-pixel MSAA pass (adaptive per
        /// <paramref name="adaptive"/>), then, when <paramref name="temporal"/>, the
        /// TAA samples the live view settles with, every sample equalised with sample
        /// 0's CDF when <paramref name="histogramEq"/> &gt; 0. Follow with
        /// <see cref="FinishHistogramEq"/>.</summary>
        public static SupersampleStats AccumulateQuality(MandelbrotCalculator calc, QualityPreset? quality,
            AdaptiveAa adaptive, int histogramEq, bool temporal, CancellationToken token)
        {
            var st = AccumulateMandelbrot(calc, quality?.AaSamples ?? 1, adaptive, token, histogramEq);
            int taa = quality?.TaaMaxSamples ?? 1;
            if (!temporal || taa <= 1 || token.IsCancellationRequested) return st;
            var eq = st.Equalizer;
            if (eq == null)
            {
                eq = SampleEqualizer.Build(calc, histogramEq);
                eq?.Apply(calc);
            }
            int n = AccumulateTemporal(calc, taa, eq, token);
            return st with { Equalizer = eq, TemporalSamples = n };
        }

        /// <summary>#1381 — the adaptive contrast a caller applies after a supersample
        /// pass: none when the pass already equalised every sample, otherwise the
        /// calculator's own equalisation at <paramref name="histogramEq"/> % (as
        /// before).</summary>
        public static void FinishHistogramEq(object calc, in SupersampleStats stats, int histogramEq)
        {
            if (histogramEq > 0 && !stats.Equalized && calc is ISupportsHistogramEq he)
                he.ApplyHistogramEqualization(histogramEq / 100.0);
        }

        // The pre-#1344 pass (FractalRenderHost.RunMsaaAccumulateMandelbrot), unchanged.
        private static SupersampleStats Full(MandelbrotCalculator calc, int side, SampleEqualizer? eq, CancellationToken token)
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
            double scale = PixelScale(calc);
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
                    eq?.Apply(calc);   // #1381
                    Add(calc.ColorBuffer, pixels, sumR, sumG, sumB, sumA);
                    count++;
                }
            }
            finally { orig.Restore(calc); }
            WriteMeans(calc.ColorBuffer, pixels, sumR, sumG, sumB, sumA, count);
            return new SupersampleStats(pixels, side * side, pixels, (long)pixels * count);
        }

        private static SupersampleStats Adaptive(MandelbrotCalculator calc, int side, AdaptiveAa a, SampleEqualizer? eq, CancellationToken token)
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

            double scale = PixelScale(calc);
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
                    eq?.Apply(calc);   // #1381
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
        /// jittering CenterX / CenterY. Unchanged from FractalRenderHost, but for
        /// <paramref name="histogramEq"/> (#1381, as in AccumulateMandelbrot).</summary>
        public static SupersampleStats AccumulateAlt(IFractalCalculator calc, int aaSamples, CancellationToken token,
            int histogramEq = 0)
        {
            if (!Supersamples(calc.Width, calc.Height, calc.ColorBuffer, aaSamples, out _)) return Single(calc.Width, calc.Height);
            var eq = SampleEqualizer.Build(calc, histogramEq);
            eq?.Apply(calc);
            return AccumulateAlt(calc, aaSamples, token, eq);
        }

        /// <summary>As above, with an equaliser already applied to sample 0 (#1381).</summary>
        public static SupersampleStats AccumulateAlt(IFractalCalculator calc, int aaSamples, CancellationToken token,
            SampleEqualizer? eq)
        {
            if (!Supersamples(calc.Width, calc.Height, calc.ColorBuffer, aaSamples, out int side)) return Single(calc.Width, calc.Height);
            int pixels = calc.Width * calc.Height;
            uint[] color = calc.ColorBuffer;

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
                    eq?.Apply(calc);   // #1381
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
            return new SupersampleStats(pixels, side * side, pixels, (long)pixels * count) { Equalizer = eq };
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
