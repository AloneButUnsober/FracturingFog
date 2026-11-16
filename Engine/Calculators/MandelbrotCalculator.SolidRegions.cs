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
// #1394 part 1 — the GPU kernels take the mask too (IGpuKernel.PixelMask, GpuPixelMask):
// a masked dispatch computes and reads back only the marked pixels, the same bytes a
// full dispatch gives them, so a frame on the shallow kernel or the deep perturbation
// kernels subdivides like a CPU one. A sparse pass runs the wavefront schedule (its
// threads pull only marked pixels: the persistent-thread scheme of Aila & Laine, HPG
// 2009 — see Docs/Resources-Bibliography.md) in bands sized by their marked pixels.
// Auto there: the frame's grid in one pass (passes cost a read-back and, deep, bands as
// long as their slowest pixel), subdividing at SolidAutoMinCostlyShareGpu; on the shallow
// kernel with its periodicity check on (which settles most interiors already) Auto
// computes the frame whole, without a probe pass. A kernel without mask support still
// completes the frame in the first pass. These run unchanged: a frame with its own pixel mask (#1344
// adaptive AA); the orbit-aware themes (their path takes no
// mask); and the interior themes (atom domains, multiplier, …) — they colour each
// in-set pixel by its own cycle, so no tile border is ever uniform and the passes
// would only cost. The whole-frame passes (post-process themes, interior alpha)
// run once on the finished frame, not after each masked pass.
// Off (the default) is byte-identical.
//
// #1394 part 2 — the subdivision itself now lives in SolidRegionSubdivision.cs, shared
// with EscapeTimeCalculator (Julia / Multibrot); this file keeps the Mandelbrot side.
//
// #1394 part 4 — a #1330 pan frame subdivides its exposed strips (PanExposed): each one at
// least 2 · SolidTileSize each way gets its own grid, with the kept pixels next to it as
// free tile borders; a narrower strip is computed whole, as before. The lattice keeps the
// frame on the CPU, so Auto decides at the CPU threshold.

using System;
using System.Threading;
using FracturingFog.Interefaces;

namespace FracturingFog;

public sealed partial class MandelbrotCalculator : ISolidRegionTarget
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

    // Measured (--solidregionprobe): see SolidRegionSubdivision.
    /// <summary>#1343 — grid spacing of the first pass, px.</summary>
    public const int SolidTileSize = SolidRegionSubdivision.TileSize;
    /// <summary>#1343 — a tile this size or smaller computes its interior outright.</summary>
    public const int SolidMinTile = SolidRegionSubdivision.MinTile;

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
    /// rows' pixels lie in long runs of costly in-set pixels (measured: see
    /// SolidRegionSubdivision.AutoMinCostlyShare).</summary>
    public const double SolidAutoMinCostlyShare = SolidRegionSubdivision.AutoMinCostlyShare;

    /// <summary>#1394 part 1 — Auto's threshold for a frame on a masked GPU kernel whose
    /// in-set pixels run to the cap (see SolidRegionSubdivision.AutoMinCostlyShareGpu).</summary>
    public const double SolidAutoMinCostlyShareGpu = SolidRegionSubdivision.AutoMinCostlyShareGpu;

    private bool SolidRegionsApply()
        => SolidRegionSkip && _mask == null
           && ColorMap is not IOrbitAwareColorMap && ColorMap is not IInteriorAwareColorMap
           && SolidRegionSubdivision.AnyFits(PanExposed(), Width, Height);

    // #1394 part 4 — a #1330 pan computes only its exposed strips (CalculateHighPrecision
    // reads _panRegion: the rows with y + dy off the old frame whole, the other rows their
    // exposed columns), so the subdivision runs on those rectangles and the kept pixels
    // serve as tile borders. Null: a whole frame.
    private System.Collections.Generic.IReadOnlyList<SolidRect>? PanExposed()
    {
        if (_panRegion is not { } p) return null;
        int w = Width, h = Height;
        var l = new System.Collections.Generic.List<SolidRect>(2);
        // The rows off the old frame, whole.
        if (p.Dy > 0) l.Add(new SolidRect(0, h - p.Dy, w, h));
        else if (p.Dy < 0) l.Add(new SolidRect(0, 0, w, -p.Dy));
        // The exposed columns of the other rows.
        int y0 = p.Dy < 0 ? -p.Dy : 0, y1 = p.Dy > 0 ? h - p.Dy : h;
        if (p.Dx > 0) l.Add(new SolidRect(w - p.Dx, y0, w, y1));
        else if (p.Dx < 0) l.Add(new SolidRect(0, y0, -p.Dx, y1));
        return l;
    }

    System.Collections.Generic.IReadOnlyList<SolidRect>? ISolidRegionTarget.SolidRegions => PanExposed();

    private void CalculateSolidRegions(CancellationToken ct)
    {
        var stats = new SolidRegionStats(CaptureFilledMask, Width * Height);
        LastFrameSolidCostlyShare = double.NaN;
        LastFrameSolidDeclined = false;
        LastFilledMask = stats.FilledMask;
        LastBatchComputeCount = stats.BatchCount;
        try { SolidRegionSubdivision.Run(this, SolidRegionAuto, stats, ct); }
        finally
        {
            LastFrameFilledPixels = stats.Filled;
            LastFrameSolidPasses = stats.Passes;
            LastFrameSolidCostlyShare = stats.CostlyShare;
            LastFrameSolidDeclined = stats.Declined;
        }
    }

    // ── #1394 — ISolidRegionTarget (the shared subdivision drives these) ─────

    int[] ISolidRegionTarget.SolidIterations => _iterationBuffer;
    uint[] ISolidRegionTarget.SolidColors => ColorBuffer;
    float[][] ISolidRegionTarget.SolidAux => new[]
    {
        _smoothBuffer, _distanceBuffer, NormalXBuffer, NormalYBuffer,
        _finalZrBuffer, _finalZiBuffer, _finalDrBuffer, _finalDiBuffer,
    };

    void ISolidRegionTarget.SolidBegin(byte[] done, byte[]? batchCount)
    {
        _deferWholeFramePasses = true;
        _maskDone = done;
        _batchCount = batchCount;
    }

    // #1394 part 1 — the pass's mask on the GPU kernel (null when the kernel can't
    // take one), and whether a GPU dispatch of this pass ran with it.
    private FracturingFog.Rendering.GpuPixelMask? _gpuPassMask;
    private bool _gpuPassMasked;

    void ISolidRegionTarget.SolidPass(byte[]? mark, bool[]? rows, CancellationToken ct)
    {
        _mask = mark;
        _maskRows = rows;
        _gpuPassMasked = false;
        var kernel = GpuKernel;
        _gpuPassMask = mark != null && kernel is { SupportsPixelMask: true } && (UseGpuCompute || GpuDeepHere)
            ? FracturingFog.Rendering.GpuPixelMask.FromBytes(mark, Width, Height) : null;
        if (_gpuPassMask != null) kernel!.PixelMask = _gpuPassMask;
        try { CalculateInternal(ct); }
        finally
        {
            if (_gpuPassMask != null) kernel!.PixelMask = null;
            _gpuPassMask = null;
        }
    }

    // #1394 part 1 — the GPU kernel the frame will go to, if it takes the mask: an
    // estimate of the route CalculateInternal picks (it only steers Auto's decision).
    private enum SolidGpu { None, Shallow, Deep }

    private SolidGpu SolidGpuRoute()
    {
        if (GpuKernel is not { SupportsPixelMask: true } k) return SolidGpu.None;
        if (!Quality.NeedsHighPrecision(Zoom)) return UseGpuCompute && Zoom <= MaxGpuZoom ? SolidGpu.Shallow : SolidGpu.None;
        if (LatticeAligned) return SolidGpu.None;   // #1330: the lattice keeps deep frames on the CPU
        return GpuDeepHere && Zoom <= MaxGpuPerturbZoom && (k.SupportsPerturbation || FloatExpHere(k))
            ? SolidGpu.Deep : SolidGpu.None;
    }

    bool ISolidRegionTarget.SolidFewPasses => SolidGpuRoute() != SolidGpu.None;

    double ISolidRegionTarget.SolidAutoMinCostlyShare => SolidGpuRoute() switch
    {
        SolidGpu.Shallow when FracturingFog.Rendering.GpuPeriodicity.Mode != FracturingFog.Rendering.GpuPeriodicity.Off
            => SolidRegionSubdivision.AutoNever,
        SolidGpu.None => SolidRegionSubdivision.AutoMinCostlyShare,
        _ => SolidRegionSubdivision.AutoMinCostlyShareGpu,
    };

    // A GPU pass that took the mask computed only the marked pixels, like a CPU one.
    bool ISolidRegionTarget.SolidFrameOnGpu
        => (LastFrameUsedGpuCompute || LastFrameUsedGpuPerturbation || LastFrameUsedGpuOrbitPerturbation)
           && !_gpuPassMasked;

    // #1394 — the pixel's c is in the main cardioid or the period-2 bulb, which the
    // shallow path settles with a closed-form test instead of iterating (the standard
    // "cardioid / bulb checking" test — Wikipedia, Plotting algorithms for the
    // Mandelbrot set; see Docs/Resources-Bibliography.md). Steers only
    // how a tile is split (never what a pixel gets), so the plain double mapping of
    // the shallow path is enough at any zoom: deep views never hit it.
    bool ISolidRegionTarget.SolidFreeInSet(int x, int y)
    {
        double scale = (3.5 / Math.Max(Width, Height)) / Zoom;
        return IsInMainCardioidOrBulb(CenterX + (x - Width * 0.5) * scale, CenterY + (y - Height * 0.5) * scale);
    }

    void ISolidRegionTarget.SolidEnd(CancellationToken ct)
    {
        _mask = null;
        _maskRows = null;
        _maskDone = null;
        _batchCount = null;
        _deferWholeFramePasses = false;
        // The post-process / interior-alpha passes, once, on the finished frame.
        if (!ct.IsCancellationRequested) RunWholeFramePasses(ct);
    }
}
