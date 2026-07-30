// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Calculators/HistogramEqualizer.cs
//
// Shared histogram-equalization ("Adaptive" slider) core, lifted out of
// MandelbrotCalculator so every escape-time calculator that carries the same
// per-pixel buffer set (IterationBuffer + SmoothBuffer + the aux
// Distance/Normal/FinalZ/FinalD buffers) can equalize with one implementation
// instead of drifting copies (#144 / #145).
//
// The math is a rank-order remap: build a CDF over the escaped-pixel smooth-
// iteration histogram, then blend each pixel's linear smooth value toward its
// CDF rank by `strength`. At strength 1.0 every band carries equal pixel area.
//
// Callers pass their live buffers through EscapeTimeColorState (fetched once,
// not per-pixel) plus their cached ParallelOptions. Interior-alpha stamping and
// the no-escape recolor fallback stay with the caller — they differ per
// calculator (MandelbrotCalculator stamps #96 interior alpha; EscapeTime leaves
// the Calculate-coloured buffer untouched).

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;

namespace FracturingFog;

/// <summary>
/// Snapshot of the per-frame buffers + coloring inputs an escape-time
/// calculator exposes. Grouped so the shared equalizer takes one argument
/// instead of a dozen; all arrays are the calculator's live buffers (not
/// copies), same length/layout as <see cref="ColorBuffer"/>.
/// </summary>
internal readonly struct EscapeTimeColorState
{
    public readonly int Width;
    public readonly int Height;
    public readonly int MaxIterations;
    public readonly double LastPixelScale;
    public readonly IColorMap ColorMap;

    public readonly int[] IterationBuffer;
    public readonly float[] SmoothBuffer;
    public readonly float[] DistanceBuffer;
    public readonly float[] NormalXBuffer;
    public readonly float[] NormalYBuffer;
    public readonly float[] FinalZrBuffer;
    public readonly float[] FinalZiBuffer;
    public readonly float[] FinalDrBuffer;
    public readonly float[] FinalDiBuffer;
    public readonly uint[] ColorBuffer;

    public readonly ParallelOptions Po;

    public EscapeTimeColorState(
        int width, int height, int maxIterations, double lastPixelScale,
        IColorMap colorMap,
        int[] iterationBuffer, float[] smoothBuffer, float[] distanceBuffer,
        float[] normalXBuffer, float[] normalYBuffer,
        float[] finalZrBuffer, float[] finalZiBuffer,
        float[] finalDrBuffer, float[] finalDiBuffer,
        uint[] colorBuffer, ParallelOptions po)
    {
        Width = width;
        Height = height;
        MaxIterations = maxIterations;
        LastPixelScale = lastPixelScale;
        ColorMap = colorMap;
        IterationBuffer = iterationBuffer;
        SmoothBuffer = smoothBuffer;
        DistanceBuffer = distanceBuffer;
        NormalXBuffer = normalXBuffer;
        NormalYBuffer = normalYBuffer;
        FinalZrBuffer = finalZrBuffer;
        FinalZiBuffer = finalZiBuffer;
        FinalDrBuffer = finalDrBuffer;
        FinalDiBuffer = finalDiBuffer;
        ColorBuffer = colorBuffer;
        Po = po;
    }
}

/// <summary>
/// Stateless histogram-equalization core shared by every escape-time
/// calculator. See file header.
/// </summary>
internal static class HistogramEqualizer
{
    /// <summary>
    /// Builds the equalization CDF over the current smooth-iteration histogram
    /// without applying it. Returns false (zeroed outputs) when the view has no
    /// escaped pixels — the caller treats that as the identity case. Serial: the
    /// histogram accumulation contends across rows, so a single pass is cheaper
    /// than atomics. Callers cache the result so live-slider / sweep ticks skip
    /// the rebuild.
    /// </summary>
    public static bool BuildCdf(
        int width, int height, int maxIter,
        int[] iterationBuffer, float[] smoothBuffer,
        out double[]? cdf, out int bins)
    {
        cdf = null;
        bins = 0;

        int n = width * height;
        if (n == 0 || maxIter <= 0) return false;

        bins = Math.Min(2048, Math.Max(256, maxIter));
        int[] hist = new int[bins];
        int totalEscaped = 0;
        float invMax = 1.0f / maxIter;

        for (int i = 0; i < n; i++)
        {
            if (iterationBuffer[i] >= maxIter) continue;
            float s = smoothBuffer[i];
            float t = s * invMax;
            if (t < 0f) t = 0f; else if (t > 0.9999999f) t = 0.9999999f;
            int b = (int)(t * bins);
            hist[b]++;
            totalEscaped++;
        }

        if (totalEscaped == 0) { bins = 0; return false; }

        cdf = new double[bins];
        long cum = 0;
        double invTotal = 1.0 / totalEscaped;
        for (int i = 0; i < bins; i++)
        {
            cum += hist[i];
            cdf[i] = cum * invTotal;
        }
        return true;
    }

    /// <summary>
    /// Applies a previously-built CDF to the state's buffers, writing
    /// <see cref="EscapeTimeColorState.ColorBuffer"/>. The smooth-iter → bin
    /// lookup is normalized against <paramref name="sourceMaxIter"/> (the
    /// MaxIterations at build time) so a locked CDF stays valid across frames
    /// whose MaxIterations differs by tier auto-promote — pixels above the
    /// build-time iter ceiling saturate into the last bin (no flicker).
    ///
    /// <paramref name="ditherIterStrength"/> adds a stable per-pixel spatial
    /// dither (in iteration units, a pure function of x/y) to blur band edges
    /// without introducing temporal noise. <paramref name="escapedCount"/> /
    /// <paramref name="saturatedCount"/> let the caller detect a drifted locked
    /// CDF and trigger a rebuild.
    /// </summary>
    public static void ApplyWithCdf(
        in EscapeTimeColorState st,
        double[] cdf, int bins, int sourceMaxIter,
        double strength, double ditherIterStrength,
        out long escapedCount, out long saturatedCount)
    {
        escapedCount = 0;
        saturatedCount = 0;
        if (cdf == null || bins <= 0 || sourceMaxIter <= 0) return;
        if (strength < 0.0) strength = 0.0;
        if (strength > 1.0) strength = 1.0;
        if (ditherIterStrength < 0.0) ditherIterStrength = 0.0;

        int w = st.Width, h = st.Height;
        int maxIter = st.MaxIterations;
        if (w == 0 || h == 0 || maxIter <= 0) return;

        var colorMap = st.ColorMap;
        colorMap.MaxIterations = maxIter;
        if (colorMap is IColorMapWithPixelScale pxsEq) pxsEq.PixelScale = st.LastPixelScale;
        bool handlesInSet = colorMap is IColorMapHandlesInSet;

        float invMax = 1.0f / maxIter;             // for current-frame coloring
        float invMaxSrc = 1.0f / sourceMaxIter;    // for CDF bin lookup
        int lastBin = bins - 1;
        float ditherIter = (float)ditherIterStrength;

        int[] iterBuf = st.IterationBuffer;
        float[] smoothBuf = st.SmoothBuffer;
        float[] distBuf = st.DistanceBuffer;
        float[] nxBuf = st.NormalXBuffer, nyBuf = st.NormalYBuffer;
        float[] zrBuf = st.FinalZrBuffer, ziBuf = st.FinalZiBuffer;
        float[] drBuf = st.FinalDrBuffer, diBuf = st.FinalDiBuffer;
        uint[] colorBuf = st.ColorBuffer;

        // Per-row counters summed at the end to avoid contended atomics in the
        // hot loop.
        long[] rowEscaped = new long[h];
        long[] rowSaturated = new long[h];

        st.Po.CancellationToken = CancellationToken.None;
        ParallelForRows(0, h, st.Po, y =>
        {
            int rowBase = y * w;
            long esc = 0;
            long sat = 0;
            for (int x = 0; x < w; x++)
            {
                int idx = rowBase + x;
                int iters = iterBuf[idx];
                if (iters >= maxIter)
                {
                    if (handlesInSet)
                    {
                        colorBuf[idx] = (uint)colorMap.Map(
                            0f, 0f, maxIter,
                            0f, 0f, 0f, 0f, 0f, 0f);
                    }
                    else
                    {
                        colorBuf[idx] = colorMap.InSetColor;
                    }
                    continue;
                }
                esc++;
                float s = smoothBuf[idx];
                float tLin = s * invMax;
                float tLinC = tLin < 0f ? 0f : (tLin > 0.9999999f ? 0.9999999f : tLin);

                float tLookupRaw = s * invMaxSrc;
                float tLookup = tLookupRaw;
                if (tLookup < 0f) tLookup = 0f;
                else if (tLookup > 0.9999999f) tLookup = 0.9999999f;
                int b = (int)(tLookup * bins);
                if (b > lastBin) b = lastBin;
                // A raw lookup at or above 1.0 means s exceeded the locked
                // sourceMaxIter — the pixel is forced into the last bin rather
                // than mapped by its real distribution position.
                if (tLookupRaw >= 0.9999999f) sat++;

                double tEq = cdf[b];
                double tBlend = tLinC + (tEq - tLinC) * strength;
                float smoothEq = (float)(tBlend * maxIter);
                if (ditherIter > 0f)
                    smoothEq += SpatialDither(x, y) * ditherIter;
                int iterArgEq = handlesInSet ? iters : maxIter;
                colorBuf[idx] = (uint)colorMap.Map(
                    smoothEq, distBuf[idx], iterArgEq,
                    nxBuf[idx], nyBuf[idx],
                    zrBuf[idx], ziBuf[idx],
                    drBuf[idx], diBuf[idx]);
            }
            rowEscaped[y] = esc;
            rowSaturated[y] = sat;
        });

        long totalEsc = 0;
        long totalSat = 0;
        for (int i = 0; i < h; i++) { totalEsc += rowEscaped[i]; totalSat += rowSaturated[i]; }
        escapedCount = totalEsc;
        saturatedCount = totalSat;
    }

    // Stable spatial hash → [-0.5, 0.5). Pure function of (x, y), so the dither
    // pattern locks to image space and adds no temporal noise across frames at a
    // given pixel. Mirrors MandelbrotCalculator.SpatialDither.
    internal static float SpatialDither(int x, int y)
    {
        uint h = unchecked((uint)(x * 73856093) ^ (uint)(y * 19349663));
        h ^= h >> 13;
        h *= 0x5bd1e995u;
        h ^= h >> 15;
        return ((h & 0xFFFFu) * (1.0f / 65535.0f)) - 0.5f;
    }

    private static int RowChunk(int count)
    {
        int chunk = count / (Environment.ProcessorCount * 4);
        return chunk < 1 ? 1 : chunk;
    }

    private static void ParallelForRows(int from, int to, ParallelOptions po, Action<int> body)
    {
        int count = to - from;
        if (count <= 0) return;
        Parallel.ForEach(Partitioner.Create(from, to, RowChunk(count)), po, range =>
        {
            for (int y = range.Item1; y < range.Item2; y++)
                body(y);
        });
    }
}
