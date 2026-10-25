// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Calculators/MandelbrotCalculator.SolidRegions.cs
//
// #1343 — solid-region skipping (Mariani–Silver rectangle subdivision), opt-in,
// on the CPU Mandelbrot paths (the shallow double / SIMD path and the deep
// perturbation path). Large in-set areas are mostly interior; iterating every one
// of their pixels to the cap (or to the periodicity check) is the bulk of an
// interior-heavy frame.
//
//   1. Compute a grid of lines every TileSize pixels (and the last row / column),
//      through the #1344 pixel mask — a masked pass computes exactly the bytes a
//      full frame gives those pixels.
//   2. A tile whose whole border is in-set AND whose border pixels all carry
//      identical per-pixel data (colour, iteration, every aux buffer) gets its
//      interior filled with that data: no iteration. The Mandelbrot set is full
//      (no holes), so an in-set closed border encloses in-set points; the one way
//      to be wrong is an escaping channel thinner than a pixel that slips between
//      the border samples (the known Mariani–Silver artifact — measured by the
//      tests and the probe).
//      The "identical data" test keeps the fill exact for any theme whose in-set
//      colour is position-independent. Escaping bands are never filled.
//   3. Otherwise the tile splits in four along its midlines (the next pass computes
//      them); a tile SolidMinTile px or smaller, one with no in-set border pixel, or
//      (#1394) one whose in-set border pixels are all free (below) computes its
//      interior outright.
//
// Where it pays: in-set areas that are expensive to iterate — minibrots, satellite
// bulbs and every deep (perturbation) interior. The shallow path already gets the
// main cardioid and the period-2 bulb for free (an analytic test), so a view whose
// interior is mostly those only pays the extra passes (measured 0.6-0.9x).
//
// #1394 — Auto (on by default with the skip): the grid rows are computed first, and
// a frame with too little costly in-set area in long runs on them (SolidAutoMinCostlyShare)
// is computed whole in a second pass instead — the cardioid views above now cost
// 0.91-0.97x rather than 0.6-0.9x. And no pixel is computed twice: a SIMD pass computes
// whole 4/8-lane batches, so a 1-px column leaves its batch neighbours computed; later
// passes skip them (and a fill must agree with every one inside the tile, which also
// rejects some sub-pixel-channel fills).
//
// A frame that went to the GPU (the GPU paths ignore the mask) is complete after
// the first pass. These run unchanged: a frame with its own pixel mask (#1344
// adaptive AA) or a pan (#1330 reuse); the orbit-aware themes (their path takes no
// mask); and the interior themes (atom domains, multiplier, …) — they colour each
// in-set pixel by its own cycle, so no tile border is ever uniform and the passes
// would only cost. The whole-frame passes (post-process themes, interior alpha)
// run once on the finished frame, not after each masked pass.
// Off (the default) is byte-identical.

using System;
using System.Collections.Generic;
using System.Threading;
using FracturingFog.Interefaces;

namespace FracturingFog;

public sealed partial class MandelbrotCalculator
{
    /// <summary>#1343 — opt-in solid-region skipping (FractalParameters.SolidRegionSkip):
    /// fill rectangles whose computed border is uniformly in-set instead of iterating
    /// their interior (see the file header). Off = every pixel iterated.</summary>
    public bool SolidRegionSkip { get; set; }

    /// <summary>#1343 — pixels the last frame filled without iterating (0 when the
    /// skip was off or did not apply).</summary>
    public long LastFrameFilledPixels { get; private set; }

    /// <summary>#1343 — masked passes the last frame ran (0 = an ordinary frame).</summary>
    public int LastFrameSolidPasses { get; private set; }

    /// <summary>#1343 — diagnostics / tests: record which pixels the next frames fill
    /// (<see cref="LastFilledMask"/>).</summary>
    public bool CaptureFilledMask { get; set; }

    /// <summary>#1343 — with <see cref="CaptureFilledMask"/>, 1 for each pixel the last
    /// frame filled without iterating (null otherwise).</summary>
    public byte[]? LastFilledMask { get; private set; }

    /// <summary>#1394 — with <see cref="CaptureFilledMask"/>: how many times a SIMD batch
    /// computed each pixel over the last frame's masked passes (scalar paths uncounted).</summary>
    public byte[]? LastBatchComputeCount { get; private set; }

    // Measured (--solidregionprobe, 960x540) over 16/4, 32/8, 32/16, 64/16 and 64/32:
    // 64/16 was best overall. A masked SIMD pass computes a whole 4-lane batch for a
    // one-pixel column, so the first-pass grid costs ~2.6x its pixel share; small tiles
    // pay it on every level, and splitting below 16 px fills little more.
    /// <summary>#1343 — grid spacing of the first pass, px.</summary>
    public const int SolidTileSize = 64;
    /// <summary>#1343 — a tile this size or smaller computes its interior outright.</summary>
    public const int SolidMinTile = 16;

    /// <summary>#1394 — with <see cref="SolidRegionSkip"/>: decide per frame. The grid
    /// rows are computed first; when too few of their in-set pixels are ones the frame
    /// would have to iterate (outside the main cardioid / period-2 bulb, which the
    /// shallow path settles in closed form), the rest of the frame is computed in one
    /// pass instead — the skip could only add passes there. Off = always subdivide.</summary>
    public bool SolidRegionAuto { get; set; } = true;

    /// <summary>#1394 — Auto: the share of the grid rows' pixels that are in-set and
    /// costly (NaN when the last frame didn't measure it).</summary>
    public double LastFrameSolidCostlyShare { get; private set; } = double.NaN;

    /// <summary>#1394 — Auto declined the last frame (computed it whole after the rows).</summary>
    public bool LastFrameSolidDeclined { get; private set; }

    /// <summary>#1394 — Auto subdivides a frame when at least this share of its grid
    /// rows' pixels lie in long runs of costly in-set pixels (see CostlyInSetShare).
    /// Measured (--solidregionprobe, 16 views, 960x540 and 1920x1080): every view the
    /// skip slowed down (whole set, seahorse / elephant valley, a satellite bulb, a small
    /// minibrot in a wide frame) measured 0.4-2.6 %; every view it sped up 5.7 % or more
    /// (a period-3 minibrot filling 7 % of the frame: 1.2-1.3x). 4 % sits between.</summary>
    public const double SolidAutoMinCostlyShare = 0.04;

    private bool SolidRegionsApply()
        => SolidRegionSkip && _mask == null && _panRegion == null
           && ColorMap is not IOrbitAwareColorMap && ColorMap is not IInteriorAwareColorMap
           && Width >= 2 * SolidTileSize && Height >= 2 * SolidTileSize;

    private void CalculateSolidRegions(CancellationToken ct)
    {
        int w = Width, h = Height, n = w * h;
        var mark = new byte[n];
        var done = new byte[n];   // #1394 — pixels some pass already computed

        var xs = GridLines(w);
        var ys = GridLines(h);
        var tiles = new List<(int X0, int Y0, int X1, int Y1)>();
        for (int j = 0; j + 1 < ys.Count; j++)
            for (int i = 0; i + 1 < xs.Count; i++)
                tiles.Add((xs[i], ys[j], xs[i + 1], ys[j + 1]));

        long filled = 0;
        int passes = 0;
        _deferWholeFramePasses = true;
        _maskDone = done;
        LastFrameSolidCostlyShare = double.NaN;
        LastFrameSolidDeclined = false;
        var filledMask = CaptureFilledMask ? new byte[n] : null;
        LastFilledMask = filledMask;
        _batchCount = LastBatchComputeCount = CaptureFilledMask ? new byte[n] : null;

        // Compute the marked pixels no earlier pass computed (#1394: a batch path computes
        // whole 4/8-lane batches, so a 1-px line leaves its neighbours computed too).
        // False when the frame is over: cancelled, or the GPU computed every pixel.
        bool Pass()
        {
            ClearDone(mark, done);
            if (mark.AsSpan().IndexOfAnyExcept((byte)0) >= 0)
            {
                RunMaskedPass(mark, ct);
                passes++;
                MergeDone(done, mark);
            }
            if (ct.IsCancellationRequested) return false;
            return !(passes == 1 && (LastFrameUsedGpuCompute || LastFrameUsedGpuPerturbation || LastFrameUsedGpuOrbitPerturbation));
        }

        try
        {
            // 1. The grid lines. Auto: the rows first, full SIMD rows, which sample the
            // frame cheaply enough to decide on.
            foreach (int y in ys) for (int x = 0; x < w; x++) mark[y * w + x] = 1;
            if (SolidRegionAuto)
            {
                if (!Pass()) return;
                LastFrameSolidCostlyShare = CostlyInSetShare(ys);
                if (LastFrameSolidCostlyShare < SolidAutoMinCostlyShare)
                {
                    LastFrameSolidDeclined = true;
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
                foreach (var t in tiles)
                {
                    var (x0, y0, x1, y1) = t;
                    if (x1 - x0 < 2 || y1 - y0 < 2) continue;   // no interior
                    var border = ClassifyBorder(x0, y0, x1, y1, out int src, out bool costly);
                    // #1394 — interior pixels a batch already computed must agree too: one
                    // that escapes (or carries other data) is a channel the border missed.
                    if (border == BorderKind.UniformInSet && !InteriorAgrees(x0, y0, x1, y1, src, done))
                        (border, costly) = (BorderKind.Mixed, true);
                    if (border == BorderKind.UniformInSet)
                    {
                        filled += FillInterior(x0, y0, x1, y1, src);
                        for (int y = y0 + 1; y < y1; y++)
                        {
                            // A computed pixel keeps its (identical) bytes: not counted as filled.
                            var row = done.AsSpan(y * w + x0 + 1, x1 - x0 - 1);
                            filled -= row.Count((byte)1);
                            if (filledMask != null)
                                for (int k = 0; k < row.Length; k++) filledMask[y * w + x0 + 1 + k] = (byte)(row[k] ^ 1);
                        }
                        continue;
                    }
                    // No in-set pixel on the border: nothing in it can be filled except a
                    // whole island inside — compute it outright rather than subdividing.
                    // #1394 — nor when every in-set border pixel is one the shallow path
                    // gets free (main cardioid / period-2 bulb): splitting would only add
                    // line passes around pixels that cost nothing to compute.
                    if (border == BorderKind.NoInSet || !costly
                        || x1 - x0 <= SolidMinTile || y1 - y0 <= SolidMinTile)
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
            _mask = null;
            _maskRows = null;
            _maskDone = null;
            _batchCount = null;
            _deferWholeFramePasses = false;
            // The post-process / interior-alpha passes, once, on the finished frame.
            if (!ct.IsCancellationRequested) RunWholeFramePasses(ct);
            LastFrameFilledPixels = filled;
            LastFrameSolidPasses = passes;
        }
    }

    // Lines every SolidTileSize px from 0, plus the last index.
    private static List<int> GridLines(int size)
    {
        var l = new List<int>();
        for (int v = 0; v < size - 1; v += SolidTileSize) l.Add(v);
        l.Add(size - 1);
        return l;
    }

    private void RunMaskedPass(byte[] mark, CancellationToken ct)
    {
        int w = Width, h = Height;
        var rows = new bool[h];
        for (int y = 0; y < h; y++)
        {
            int rb = y * w;
            for (int x = 0; x < w; x++) if (mark[rb + x] != 0) { rows[y] = true; break; }
        }
        _mask = mark;
        _maskRows = rows;
        CalculateInternal(ct);
    }

    private enum BorderKind { NoInSet, Mixed, UniformInSet }

    // UniformInSet: every border pixel is in-set with the same data as src (the
    // first in-set border pixel); NoInSet: none is in-set; else Mixed. costly: some
    // in-set border pixel lies outside the main cardioid / period-2 bulb (#1394).
    private BorderKind ClassifyBorder(int x0, int y0, int x1, int y1, out int src, out bool costly)
    {
        int w = Width, maxIt = MaxIterations;
        src = -1;
        costly = false;
        bool anyOut = false, uniform = true;
        for (int k = 0, perimeter = 2 * (x1 - x0) + 2 * (y1 - y0); k < perimeter; k++)
        {
            int i = BorderPixel(x0, y0, x1, y1, k, w);
            if (_iterationBuffer[i] < maxIt) anyOut = true;
            else
            {
                if (src < 0) src = i;
                else if (uniform && !SameInSet(i, src, maxIt)) uniform = false;
                if (!costly && !FreeInSet(i % w, i / w)) costly = true;
            }
            if (anyOut && costly) return BorderKind.Mixed;
        }
        return src < 0 ? BorderKind.NoInSet : !anyOut && uniform ? BorderKind.UniformInSet : BorderKind.Mixed;
    }

    // #1394 — the pixel's c is in the main cardioid or the period-2 bulb, which the
    // shallow path settles with a closed-form test instead of iterating (the standard
    // "cardioid / bulb checking" test — Wikipedia, Plotting algorithms for the
    // Mandelbrot set; see Docs/Resources-Bibliography.md). Steers only
    // how a tile is split (never what a pixel gets), so the plain double mapping of
    // the shallow path is enough at any zoom: deep views never hit it.
    private bool FreeInSet(int x, int y)
    {
        double scale = (3.5 / Math.Max(Width, Height)) / Zoom;
        return IsInMainCardioidOrBulb(CenterX + (x - Width * 0.5) * scale, CenterY + (y - Height * 0.5) * scale);
    }

    // #1394 — the share of the grid rows' pixels that lie in a run of at least
    // 2·SolidMinTile in-set, costly pixels. A shorter run (a small bulb, a filament
    // crossing the row) gives no tile a fill; counting it put views the skip slows
    // down (elephant valley 4.0 % raw) above ones it speeds up (a small minibrot 5.7 %).
    private double CostlyInSetShare(List<int> ys)
    {
        int w = Width, maxIt = MaxIterations;
        long costly = 0;
        foreach (int y in ys)
        {
            int run = 0;
            for (int x = 0, rb = y * w; x <= w; x++)
            {
                if (x < w && _iterationBuffer[rb + x] >= maxIt && !FreeInSet(x, y)) { run++; continue; }
                if (run >= 2 * SolidMinTile) costly += run;
                run = 0;
            }
        }
        return costly / (double)(ys.Count * w);
    }

    // #1394 — every already-computed interior pixel is in-set with src's data.
    private bool InteriorAgrees(int x0, int y0, int x1, int y1, int src, byte[] done)
    {
        int w = Width, maxIt = MaxIterations;
        for (int y = y0 + 1; y < y1; y++)
        {
            int from = y * w + x0 + 1, len = x1 - x0 - 1;
            for (int k = done.AsSpan(from, len).IndexOf((byte)1); k >= 0;)
            {
                if (!SameInSet(from + k, src, maxIt)) return false;
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
    private bool SameInSet(int i, int src, int maxIt)
    {
        if (_iterationBuffer[i] < maxIt || _iterationBuffer[i] != _iterationBuffer[src]) return false;
        if (ColorBuffer[i] != ColorBuffer[src]) return false;
        return Same(_smoothBuffer, i, src) && Same(_distanceBuffer, i, src)
            && Same(NormalXBuffer, i, src) && Same(NormalYBuffer, i, src)
            && Same(_finalZrBuffer, i, src) && Same(_finalZiBuffer, i, src)
            && Same(_finalDrBuffer, i, src) && Same(_finalDiBuffer, i, src);
    }

    private static bool Same(float[] b, int i, int src)
        => b.Length <= Math.Max(i, src) || BitConverter.SingleToInt32Bits(b[i]) == BitConverter.SingleToInt32Bits(b[src]);


    // Copy the source pixel's data into the tile's interior; returns the pixel count.
    private long FillInterior(int x0, int y0, int x1, int y1, int src)
    {
        int w = Width;
        long count = 0;
        for (int y = y0 + 1; y < y1; y++)
        {
            int from = y * w + x0 + 1, len = x1 - x0 - 1;
            Fill(_iterationBuffer, from, len, src);
            Fill(ColorBuffer, from, len, src);
            Fill(_smoothBuffer, from, len, src);
            Fill(_distanceBuffer, from, len, src);
            Fill(NormalXBuffer, from, len, src);
            Fill(NormalYBuffer, from, len, src);
            Fill(_finalZrBuffer, from, len, src);
            Fill(_finalZiBuffer, from, len, src);
            Fill(_finalDrBuffer, from, len, src);
            Fill(_finalDiBuffer, from, len, src);
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
