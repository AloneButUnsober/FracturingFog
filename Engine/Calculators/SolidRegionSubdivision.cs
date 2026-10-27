// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Calculators/SolidRegionSubdivision.cs
//
// #1343 / #1394 — the solid-region frame (Mariani–Silver rectangle subdivision),
// shared by the calculators whose in-set area is full (no holes): MandelbrotCalculator
// (the Mandelbrot set) and, #1394 part 2, EscapeTimeCalculator's Julia and Multibrot
// (a polynomial's filled Julia set and the unicritical connectedness loci). See
// MandelbrotCalculator.SolidRegions.cs for the algorithm, Auto and the measurements;
// this file is that code moved behind ISolidRegionTarget, unchanged.
//
// Why "full" is what makes the fill safe, at a finite iteration cap too: the pixels
// that have not escaped after N steps are ∩ₖ<N { |Pₖ| < R }, where Pₖ — the k-th
// iterate as a function of the pixel's plane coordinate — is a polynomial (in c for
// Mandelbrot / Multibrot, in z₀ for Julia). No { |Pₖ| < R } has a hole: |Pₖ| = R on a
// hole's boundary, so |Pₖ| ≤ R inside it (maximum modulus principle), so Pₖ would be
// constant there. Every complement then reaches infinity, their union is connected,
// and a tile whose border is in-set encloses only in-set points — up to an escaping
// channel thinner than a pixel that slips between the border samples (the known
// Mariani–Silver artifact).
//
// #1394 part 1 — a target whose passes go to a masked GPU kernel (SolidFewPasses) runs
// Auto on the whole grid in one pass and decides at its own threshold
// (SolidAutoMinCostlyShare; AutoNever = compute the frame whole, no probe).

using System;
using System.Collections.Generic;
using System.Threading;

namespace FracturingFog;

/// <summary>#1394 — the calculator side of a solid-region frame.</summary>
internal interface ISolidRegionTarget
{
    int Width { get; }
    int Height { get; }
    int MaxIterations { get; }

    /// <summary>The frame's iteration buffer (the backing array: no deferred read-back).</summary>
    int[] SolidIterations { get; }
    uint[] SolidColors { get; }

    /// <summary>The other per-pixel buffers a frame defines for every in-set pixel;
    /// a fill copies them and a border must agree on them.</summary>
    float[][] SolidAux { get; }

    /// <summary>The frame starts: <paramref name="done"/> is where a batch path records
    /// the lanes it wrote (see MandelbrotCalculator.TakeBatch), <paramref name="batchCount"/>
    /// (diagnostics, may be null) how often.</summary>
    void SolidBegin(byte[] done, byte[]? batchCount);

    /// <summary>Compute the marked pixels (a masked pass: exactly the bytes a full frame
    /// gives them); <paramref name="rows"/> flags the rows with a marked pixel. #1394 —
    /// null for both: every pixel, unmasked (Auto's <see cref="SolidRegionSubdivision.AutoNever"/>).</summary>
    void SolidPass(byte[]? mark, bool[]? rows, CancellationToken ct);

    /// <summary>The last pass ran on the GPU, which ignores the mask: the frame is complete.</summary>
    bool SolidFrameOnGpu { get; }

    /// <summary>#1394 part 1 — each pass costs a fixed overhead on top of its pixels (a
    /// masked GPU dispatch: the read-back, and a band at least as long as its slowest
    /// pixel), so Auto decides on the first pass of the whole grid instead of running
    /// the rows on their own first.</summary>
    bool SolidFewPasses { get; }

    /// <summary>#1394 part 1 — Auto subdivides when at least this share of the grid rows'
    /// pixels lie in long runs of costly in-set pixels: <see cref="SolidRegionSubdivision.AutoMinCostlyShare"/>
    /// on the CPU, <see cref="SolidRegionSubdivision.AutoMinCostlyShareGpu"/> when the frame
    /// goes to a masked GPU kernel; <see cref="SolidRegionSubdivision.AutoNever"/>: never
    /// (the frame is computed whole, with no probe pass).</summary>
    double SolidAutoMinCostlyShare { get; }

    /// <summary>Pixel (x, y) is in-set by a closed-form test, so it costs nothing to
    /// compute (#1394: steers only how a tile splits, never what a pixel gets).</summary>
    bool SolidFreeInSet(int x, int y);

    /// <summary>The frame is over (also when cancelled or failed): clear the mask state.</summary>
    void SolidEnd(CancellationToken ct);
}

/// <summary>#1394 — what a solid-region frame did.</summary>
internal sealed class SolidRegionStats
{
    public long Filled;
    public int Passes;
    public double CostlyShare = double.NaN;
    public bool Declined;
    public byte[]? FilledMask;
    public byte[]? BatchCount;

    public SolidRegionStats(bool capture, int pixels)
    {
        if (!capture) return;
        FilledMask = new byte[pixels];
        BatchCount = new byte[pixels];
    }
}

internal static class SolidRegionSubdivision
{
    // Measured (--solidregionprobe, 960x540) over 16/4, 32/8, 32/16, 64/16 and 64/32:
    // 64/16 was best overall. A masked SIMD pass computes a whole 4-lane batch for a
    // one-pixel column, so the first-pass grid costs ~2.6x its pixel share; small tiles
    // pay it on every level, and splitting below 16 px fills little more.
    /// <summary>#1343 — grid spacing of the first pass, px.</summary>
    public const int TileSize = 64;
    /// <summary>#1343 — a tile this size or smaller computes its interior outright.</summary>
    public const int MinTile = 16;

    /// <summary>#1394 — Auto subdivides a frame when at least this share of its grid
    /// rows' pixels lie in long runs of costly in-set pixels (see CostlyInSetShare).
    /// Measured (--solidregionprobe, 16 views, 960x540 and 1920x1080): every view the
    /// skip slowed down (whole set, seahorse / elephant valley, a satellite bulb, a small
    /// minibrot in a wide frame) measured 0.4-2.6 %; every view it sped up 5.7 % or more
    /// (a period-3 minibrot filling 7 % of the frame: 1.2-1.3x). 4 % sits between.</summary>
    public const double AutoMinCostlyShare = 0.04;

    /// <summary>#1394 part 1 — Auto's threshold for a frame on a masked GPU kernel whose
    /// in-set pixels run to the cap (the deep perturbation kernels; the shallow one
    /// with its periodicity check off). A pass costs more there: a read-back, and on a
    /// deep frame bands that each last as long as their slowest pixel. Measured
    /// (--solidregionprobe with FF_SOLID_GPU=1, GT 710, 960x540, a deep minibrot at
    /// 20 000–50 000 iterations framed wider and wider): costly share 2.1 % 0.90x (Auto
    /// declined, the grid pass lost), 7.9 % 0.98x, 17.5 % 1.25x, 41.7 % 1.9x, 82 % 3.7x.</summary>
    public const double AutoMinCostlyShareGpu = 0.10;

    /// <summary>#1394 part 1 — Auto's threshold that never subdivides: the frame is
    /// computed whole in one unmasked pass, as with the skip off. The shallow GPU kernel
    /// with its periodicity check on (#1342, the default) takes it: the check settles most
    /// in-set pixels after a period or two, so only a frame almost all in-set gained, one
    /// that was cheap already (GT 710, 960x540: 100 % costly 1.4–2x, 24 ms → 12 ms; 78 %
    /// ~1.0x; 66 % 0.6x), while the probe pass alone cost a declined frame 0.63–0.87x.
    /// Auto off still subdivides it.</summary>
    public const double AutoNever = double.PositiveInfinity;

    /// <summary>The frame is big enough for the grid to leave any interior.</summary>
    public static bool FrameFits(int width, int height) => width >= 2 * TileSize && height >= 2 * TileSize;

    public static void Run(ISolidRegionTarget t, bool auto, SolidRegionStats stats, CancellationToken ct)
    {
        int w = t.Width, h = t.Height, n = w * h;
        var mark = new byte[n];
        var done = new byte[n];   // #1394 — pixels some pass already computed
        var buf = new Buffers(t.SolidIterations, t.SolidColors, t.SolidAux, t.MaxIterations, w);

        var xs = GridLines(w);
        var ys = GridLines(h);
        var tiles = new List<(int X0, int Y0, int X1, int Y1)>();
        for (int j = 0; j + 1 < ys.Count; j++)
            for (int i = 0; i + 1 < xs.Count; i++)
                tiles.Add((xs[i], ys[j], xs[i + 1], ys[j + 1]));

        var filledMask = stats.FilledMask;
        t.SolidBegin(done, stats.BatchCount);

        // Compute the marked pixels no earlier pass computed (#1394: a batch path computes
        // whole 4/8-lane batches, so a 1-px line leaves its neighbours computed too).
        // False when the frame is over: cancelled, or the GPU computed every pixel.
        bool Pass()
        {
            ClearDone(mark, done);
            if (mark.AsSpan().IndexOfAnyExcept((byte)0) >= 0)
            {
                t.SolidPass(mark, MarkedRows(mark, w, h), ct);
                stats.Passes++;
                MergeDone(done, mark);
            }
            if (ct.IsCancellationRequested) return false;
            return !(stats.Passes == 1 && t.SolidFrameOnGpu);
        }

        try
        {
            // 1. The grid lines. Auto: the rows first, full SIMD rows, which sample the
            // frame cheaply enough to decide on (#1394: with the columns, in one pass,
            // when passes are dear).
            foreach (int y in ys) for (int x = 0; x < w; x++) mark[y * w + x] = 1;
            if (auto && double.IsPositiveInfinity(t.SolidAutoMinCostlyShare))
            {
                stats.Declined = true;
                t.SolidPass(null, null, ct);
                stats.Passes++;
                return;
            }
            if (auto && t.SolidFewPasses)
                foreach (int x in xs) for (int y = 0; y < h; y++) mark[y * w + x] = 1;
            if (auto)
            {
                if (!Pass()) return;
                stats.CostlyShare = CostlyInSetShare(t, buf, ys);
                if (stats.CostlyShare < t.SolidAutoMinCostlyShare)
                {
                    stats.Declined = true;
                    Array.Fill(mark, (byte)1);
                    Pass();
                    return;
                }
                Array.Clear(mark);
            }
            foreach (int x in xs) for (int y = 0; y < h; y++) mark[y * w + x] = 1;

            while (true)
            {
                if (!Pass()) return;
                if (tiles.Count == 0) break;

                Array.Clear(mark);
                bool any = false;
                var next = new List<(int, int, int, int)>();
                foreach (var tile in tiles)
                {
                    var (x0, y0, x1, y1) = tile;
                    if (x1 - x0 < 2 || y1 - y0 < 2) continue;   // no interior
                    var border = ClassifyBorder(t, buf, x0, y0, x1, y1, out int src, out bool costly);
                    // #1394 — interior pixels a batch already computed must agree too: one
                    // that escapes (or carries other data) is a channel the border missed.
                    if (border == BorderKind.UniformInSet && !InteriorAgrees(buf, x0, y0, x1, y1, src, done))
                        (border, costly) = (BorderKind.Mixed, true);
                    if (border == BorderKind.UniformInSet)
                    {
                        stats.Filled += FillInterior(buf, x0, y0, x1, y1, src);
                        for (int y = y0 + 1; y < y1; y++)
                        {
                            // A computed pixel keeps its (identical) bytes: not counted as filled.
                            var row = done.AsSpan(y * w + x0 + 1, x1 - x0 - 1);
                            stats.Filled -= row.Count((byte)1);
                            if (filledMask != null)
                                for (int k = 0; k < row.Length; k++) filledMask[y * w + x0 + 1 + k] = (byte)(row[k] ^ 1);
                        }
                        continue;
                    }
                    // No in-set pixel on the border: nothing in it can be filled except a
                    // whole island inside — compute it outright rather than subdividing.
                    // #1394 — nor when every in-set border pixel is one the target gets
                    // free (closed-form in-set): splitting would only add line passes
                    // around pixels that cost nothing to compute.
                    if (border == BorderKind.NoInSet || !costly
                        || x1 - x0 <= MinTile || y1 - y0 <= MinTile)
                    {
                        for (int y = y0 + 1; y < y1; y++)
                            for (int x = x0 + 1; x < x1; x++) mark[y * w + x] = 1;
                        any = true;
                        continue;
                    }
                    int xm = (x0 + x1) >> 1, ym = (y0 + y1) >> 1;
                    for (int y = y0 + 1; y < y1; y++) mark[y * w + xm] = 1;
                    for (int x = x0 + 1; x < x1; x++) mark[ym * w + x] = 1;
                    any = true;
                    next.Add((x0, y0, xm, ym)); next.Add((xm, y0, x1, ym));
                    next.Add((x0, ym, xm, y1)); next.Add((xm, ym, x1, y1));
                }
                tiles = next;
                if (!any) break;
            }
        }
        finally
        {
            t.SolidEnd(ct);
        }
    }

    private readonly record struct Buffers(int[] Iter, uint[] Color, float[][] Aux, int MaxIt, int W);

    // Lines every TileSize px from 0, plus the last index.
    private static List<int> GridLines(int size)
    {
        var l = new List<int>();
        for (int v = 0; v < size - 1; v += TileSize) l.Add(v);
        l.Add(size - 1);
        return l;
    }

    private static bool[] MarkedRows(byte[] mark, int w, int h)
    {
        var rows = new bool[h];
        for (int y = 0; y < h; y++)
            rows[y] = mark.AsSpan(y * w, w).IndexOfAnyExcept((byte)0) >= 0;
        return rows;
    }

    private enum BorderKind { NoInSet, Mixed, UniformInSet }

    // UniformInSet: every border pixel is in-set with the same data as src (the
    // first in-set border pixel); NoInSet: none is in-set; else Mixed. costly: some
    // in-set border pixel is not free (#1394).
    private static BorderKind ClassifyBorder(ISolidRegionTarget t, in Buffers b, int x0, int y0, int x1, int y1, out int src, out bool costly)
    {
        int w = b.W, maxIt = b.MaxIt;
        src = -1;
        costly = false;
        bool anyOut = false, uniform = true;
        for (int k = 0, perimeter = 2 * (x1 - x0) + 2 * (y1 - y0); k < perimeter; k++)
        {
            int i = BorderPixel(x0, y0, x1, y1, k, w);
            if (b.Iter[i] < maxIt) anyOut = true;
            else
            {
                if (src < 0) src = i;
                else if (uniform && !SameInSet(b, i, src)) uniform = false;
                if (!costly && !t.SolidFreeInSet(i % w, i / w)) costly = true;
            }
            if (anyOut && costly) return BorderKind.Mixed;
        }
        return src < 0 ? BorderKind.NoInSet : !anyOut && uniform ? BorderKind.UniformInSet : BorderKind.Mixed;
    }

    // #1394 — the share of the grid rows' pixels that lie in a run of at least
    // 2·MinTile in-set, costly pixels. A shorter run (a small bulb, a filament
    // crossing the row) gives no tile a fill; counting it put views the skip slows
    // down (elephant valley 4.0 % raw) above ones it speeds up (a small minibrot 5.7 %).
    private static double CostlyInSetShare(ISolidRegionTarget t, in Buffers b, List<int> ys)
    {
        int w = b.W, maxIt = b.MaxIt;
        long costly = 0;
        foreach (int y in ys)
        {
            int run = 0;
            for (int x = 0, rb = y * w; x <= w; x++)
            {
                if (x < w && b.Iter[rb + x] >= maxIt && !t.SolidFreeInSet(x, y)) { run++; continue; }
                if (run >= 2 * MinTile) costly += run;
                run = 0;
            }
        }
        return costly / (double)(ys.Count * w);
    }

    // #1394 — every already-computed interior pixel is in-set with src's data.
    private static bool InteriorAgrees(in Buffers b, int x0, int y0, int x1, int y1, int src, byte[] done)
    {
        int w = b.W;
        for (int y = y0 + 1; y < y1; y++)
        {
            int from = y * w + x0 + 1, len = x1 - x0 - 1;
            for (int k = done.AsSpan(from, len).IndexOf((byte)1); k >= 0;)
            {
                if (!SameInSet(b, from + k, src)) return false;
                int next = done.AsSpan(from + k + 1, len - k - 1).IndexOf((byte)1);
                k = next < 0 ? -1 : k + 1 + next;
            }
        }
        return true;
    }

    // mark &= ~done, done |= mark — vector-wide (one byte per pixel, 0 or 1).
    private static void ClearDone(byte[] mark, byte[] done)
    {
        var m = mark.AsSpan(); var d = done.AsSpan();
        int i = 0, vl = System.Numerics.Vector<byte>.Count;
        for (; i + vl <= m.Length; i += vl)
            System.Numerics.Vector.AndNot(new System.Numerics.Vector<byte>(m.Slice(i)), new System.Numerics.Vector<byte>(d.Slice(i))).CopyTo(m.Slice(i));
        for (; i < m.Length; i++) m[i] &= (byte)~d[i];
    }

    private static void MergeDone(byte[] done, byte[] mark)
    {
        var m = mark.AsSpan(); var d = done.AsSpan();
        int i = 0, vl = System.Numerics.Vector<byte>.Count;
        for (; i + vl <= m.Length; i += vl)
            (new System.Numerics.Vector<byte>(d.Slice(i)) | new System.Numerics.Vector<byte>(m.Slice(i))).CopyTo(d.Slice(i));
        for (; i < m.Length; i++) d[i] |= m[i];
    }

    // The k-th pixel walking the tile border clockwise from (x0, y0).
    private static int BorderPixel(int x0, int y0, int x1, int y1, int k, int w)
    {
        int tw = x1 - x0, th = y1 - y0;
        if (k < tw) return y0 * w + x0 + k;                    // top, left to right
        k -= tw;
        if (k < th) return (y0 + k) * w + x1;                  // right, top to bottom
        k -= th;
        if (k < tw) return y1 * w + x1 - k;                    // bottom, right to left
        k -= tw;
        return (y1 - k) * w + x0;                               // left, bottom to top
    }

    // The buffers a frame defines for every pixel (the interior and orbit ones belong
    // to themes that never get here, and are otherwise left uninitialised).
    private static bool SameInSet(in Buffers b, int i, int src)
    {
        if (b.Iter[i] < b.MaxIt || b.Iter[i] != b.Iter[src]) return false;
        if (b.Color[i] != b.Color[src]) return false;
        foreach (var a in b.Aux)
            if (!Same(a, i, src)) return false;
        return true;
    }

    private static bool Same(float[] b, int i, int src)
        => b.Length <= Math.Max(i, src) || BitConverter.SingleToInt32Bits(b[i]) == BitConverter.SingleToInt32Bits(b[src]);

    // Copy the source pixel's data into the tile's interior; returns the pixel count.
    private static long FillInterior(in Buffers b, int x0, int y0, int x1, int y1, int src)
    {
        int w = b.W;
        long count = 0;
        for (int y = y0 + 1; y < y1; y++)
        {
            int from = y * w + x0 + 1, len = x1 - x0 - 1;
            Fill(b.Iter, from, len, src);
            Fill(b.Color, from, len, src);
            foreach (var a in b.Aux) Fill(a, from, len, src);
            count += len;
        }
        return count;
    }

    private static void Fill<T>(T[] b, int from, int len, int src)
    {
        if (b.Length <= Math.Max(src, from + len - 1)) return;
        Array.Fill(b, b[src], from, len);
    }
}
