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
//      them); a tile 4 px or smaller computes its interior outright.
//
// Where it pays: in-set areas that are expensive to iterate — minibrots, satellite
// bulbs and every deep (perturbation) interior. The shallow path already gets the
// main cardioid and the period-2 bulb for free (an analytic test), so a view whose
// interior is mostly those only pays the extra passes (measured 0.6-0.9x).
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

    // Measured (--solidregionprobe, 960x540) over 16/4, 32/8, 32/16, 64/16 and 64/32:
    // 64/16 was best overall. A masked SIMD pass computes a whole 4-lane batch for a
    // one-pixel column, so the first-pass grid costs ~2.6x its pixel share; small tiles
    // pay it on every level, and splitting below 16 px fills little more.
    /// <summary>#1343 — grid spacing of the first pass, px.</summary>
    public const int SolidTileSize = 64;
    /// <summary>#1343 — a tile this size or smaller computes its interior outright.</summary>
    public const int SolidMinTile = 16;

    private bool SolidRegionsApply()
        => SolidRegionSkip && _mask == null && _panRegion == null
           && ColorMap is not IOrbitAwareColorMap && ColorMap is not IInteriorAwareColorMap
           && Width >= 2 * SolidTileSize && Height >= 2 * SolidTileSize;

    private void CalculateSolidRegions(CancellationToken ct)
    {
        int w = Width, h = Height, n = w * h;
        var mark = new byte[n];
        var lines = new List<int>();

        // 1. The grid lines.
        var xs = GridLines(w);
        var ys = GridLines(h);
        foreach (int y in ys) for (int x = 0; x < w; x++) mark[y * w + x] = 1;
        foreach (int x in xs) for (int y = 0; y < h; y++) mark[y * w + x] = 1;
        var tiles = new List<(int X0, int Y0, int X1, int Y1)>();
        for (int j = 0; j + 1 < ys.Count; j++)
            for (int i = 0; i + 1 < xs.Count; i++)
                tiles.Add((xs[i], ys[j], xs[i + 1], ys[j + 1]));

        long filled = 0;
        int passes = 0;
        _deferWholeFramePasses = true;
        var filledMask = CaptureFilledMask ? new byte[n] : null;
        LastFilledMask = filledMask;
        try
        {
            while (true)
            {
                RunMaskedPass(mark, ct);
                passes++;
                if (ct.IsCancellationRequested) return;
                if (passes == 1 && (LastFrameUsedGpuCompute || LastFrameUsedGpuPerturbation || LastFrameUsedGpuOrbitPerturbation))
                    return;   // the GPU computed every pixel
                if (tiles.Count == 0) break;

                Array.Clear(mark);
                bool any = false;
                var next = new List<(int, int, int, int)>();
                foreach (var t in tiles)
                {
                    var (x0, y0, x1, y1) = t;
                    if (x1 - x0 < 2 || y1 - y0 < 2) continue;   // no interior
                    var border = ClassifyBorder(x0, y0, x1, y1, out int src);
                    if (border == BorderKind.UniformInSet)
                    {
                        filled += FillInterior(x0, y0, x1, y1, src);
                        if (filledMask != null)
                            for (int y = y0 + 1; y < y1; y++) Array.Fill(filledMask, (byte)1, y * w + x0 + 1, x1 - x0 - 1);
                        continue;
                    }
                    // No in-set pixel on the border: nothing in it can be filled except a
                    // whole island inside — compute it outright rather than subdividing.
                    if (border == BorderKind.NoInSet || x1 - x0 <= SolidMinTile || y1 - y0 <= SolidMinTile)
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
    // first in-set border pixel); NoInSet: none is in-set; else Mixed.
    private BorderKind ClassifyBorder(int x0, int y0, int x1, int y1, out int src)
    {
        int w = Width, maxIt = MaxIterations;
        src = -1;
        bool anyOut = false, uniform = true;
        for (int k = 0, perimeter = 2 * (x1 - x0) + 2 * (y1 - y0); k < perimeter; k++)
        {
            int i = BorderPixel(x0, y0, x1, y1, k, w);
            if (_iterationBuffer[i] < maxIt) anyOut = true;
            else if (src < 0) src = i;
            else if (uniform && !SameInSet(i, src, maxIt)) uniform = false;
            if (anyOut && src >= 0) return BorderKind.Mixed;
        }
        return src < 0 ? BorderKind.NoInSet : uniform ? BorderKind.UniformInSet : BorderKind.Mixed;
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
