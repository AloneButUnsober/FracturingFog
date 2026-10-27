// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Calculators/EscapeTimeCalculator.SolidRegions.cs
//
// #1394 part 2 — solid-region skipping (#1343, Mariani–Silver subdivision) for the
// Julia and Multibrot families: the same opt-in FractalParameters.SolidRegionSkip /
// SolidRegionAuto, the same shared subdivision (SolidRegionSubdivision.cs), driven
// through masked passes of this calculator's SIMD and scalar cores.
//
// Which families: a polynomial's filled Julia set and the Multibrot (unicritical)
// connectedness loci are full, so a tile with an in-set border holds only in-set
// points (the argument is in SolidRegionSubdivision.cs). Julia here is z² + c and
// Multibrot zᵈ + c, both polynomial in the pixel's coordinate. Not the others:
// Burning Ship and Tricorn are not holomorphic (the maximum modulus argument does
// not apply), Phoenix keeps the previous z, Magnet is rational, Glynn a z^1.5 branch
// and Spider changes c as it goes.
//
// Neither family has a closed-form in-set test (no cardioid skip), so every in-set
// pixel costs the full cap — the case the skip is for. Filled Julia sets are mostly
// interior: a connected c (the Douady rabbit, the basilica) leaves large in-set areas.
//
// #1394 part 1 — a frame on the shallow GPU kernel subdivides too: each pass hands the
// kernel its mask (IGpuKernel.PixelMask), which computes and reads back only the marked
// pixels. Auto computes such a frame whole while the kernel's periodicity check is on
// (it settles most interiors already; see SolidRegionSubdivision.AutoNever), and probes
// at the GPU threshold with it off. A kernel without mask support still completes the
// frame in the first pass.
//
// Runs unchanged (an ordinary frame): the domain warp (#253: a pixel no longer maps to its
// plane point linearly, so a tile border need not enclose its interior's points), a
// per-row iteration cap (video: border and interior would be different sets). The
// whole-frame passes of Calculate (the out-of-bounds surround, interior alpha) run
// once, on the finished frame. Off (the default) is byte-identical.

using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace FracturingFog;

public sealed partial class EscapeTimeCalculator : ISolidRegionTarget
{
    /// <summary>#1394 — pixels the last frame filled without iterating (0 when the
    /// skip was off or did not apply).</summary>
    public long LastFrameFilledPixels { get; private set; }

    /// <summary>#1394 — masked passes the last frame ran (0 = an ordinary frame).</summary>
    public int LastFrameSolidPasses { get; private set; }

    /// <summary>#1394 — diagnostics / tests: record which pixels the next frames fill
    /// (<see cref="LastFilledMask"/>, <see cref="LastBatchComputeCount"/>).</summary>
    public bool CaptureFilledMask { get; set; }

    /// <summary>#1394 — with <see cref="CaptureFilledMask"/>, 1 for each pixel the last
    /// frame filled without iterating (null otherwise).</summary>
    public byte[]? LastFilledMask { get; private set; }

    /// <summary>#1394 — with <see cref="CaptureFilledMask"/>: how many times a SIMD batch
    /// or the scalar core computed each pixel over the last frame's masked passes.</summary>
    public byte[]? LastBatchComputeCount { get; private set; }

    /// <summary>#1394 — Auto: the share of the grid rows' pixels in long runs of in-set
    /// pixels (NaN when the last frame didn't measure it).</summary>
    public double LastFrameSolidCostlyShare { get; private set; } = double.NaN;

    /// <summary>#1394 — Auto declined the last frame (computed it whole after the rows).</summary>
    public bool LastFrameSolidDeclined { get; private set; }

    private bool _lastFrameOnGpu;   // the last CalculateInternal ran on the GPU

    private bool SolidRegionsApply()
        => FractalParameters.SolidRegionSkip
           && FractalType is FractalType.Julia or FractalType.Multibrot
           && !WarpActive
           && !(PerRowMaxIter is { } perRow && perRow.Length >= Height)
           && SolidRegionSubdivision.FrameFits(Width, Height);

    private void CalculateSolidRegions(CancellationToken ct)
    {
        var stats = new SolidRegionStats(CaptureFilledMask, Width * Height);
        LastFilledMask = stats.FilledMask;
        LastBatchComputeCount = stats.BatchCount;
        try { SolidRegionSubdivision.Run(this, FractalParameters.SolidRegionAuto, stats, ct); }
        finally
        {
            LastFrameFilledPixels = stats.Filled;
            LastFrameSolidPasses = stats.Passes;
            LastFrameSolidCostlyShare = stats.CostlyShare;
            LastFrameSolidDeclined = stats.Declined;
        }
    }

    // ── The pixel mask of a masked pass ─────────────────────────────────────
    //
    // As MandelbrotCalculator's (#1344): with a mask, the CPU cores skip unmarked
    // pixels — the scalar loops pixel by pixel, a SIMD batch only when none of its lanes
    // is marked, a row with no marked pixel altogether. A SIMD lane's result depends on
    // its own pixel only, and a batch covers the same lanes as in a full frame, so a
    // marked pixel gets exactly the full frame's bytes.

    private byte[]? _mask;       // this pass's marked pixels (null = every pixel)
    private bool[]? _maskRows;   // rows with at least one marked pixel
    private byte[]? _maskDone;   // pixels a batch wrote this frame
    private byte[]? _batchCount; // diagnostics: times a batch or the scalar core computed each pixel

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Marked(int idx) => _mask == null || _mask[idx] != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool RowMarked(int y) => _maskRows == null || _maskRows[y];

    // #1394 part 1 — the pass's mask on the GPU kernel (null when there is no pass or
    // the kernel can't take one), and whether a GPU dispatch of this pass ran with it.
    private FracturingFog.Rendering.GpuPixelMask? _gpuPassMask;
    private bool _gpuPassMasked;

    /// <summary>A GPU dispatch of this pass wrote pixel <paramref name="idx"/> (it took
    /// the mask, or there is none).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool GpuMarked(int idx) => _gpuPassMask == null || _mask![idx] != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool GpuRowMarked(int y) => _gpuPassMask == null || _maskRows![y];

    /// <summary>Any lane of the batch at columns [x, x + lanes) is marked; a batch this
    /// takes writes every lane, so the lanes are recorded as computed and later passes
    /// skip them.</summary>
    private bool TakeBatch(int rowBase, int x, int lanes)
    {
        var m = _mask!;
        int to = Math.Min(x + lanes, Width);
        if (m.AsSpan(rowBase + x, to - x).IndexOfAnyExcept((byte)0) < 0) return false;
        if (_maskDone is { } d)
        {
            d.AsSpan(rowBase + x, to - x).Fill(1);
            if (_batchCount is { } bc)
                for (int c = x; c < to; c++) bc[rowBase + c]++;
        }
        return true;
    }

    // ── ISolidRegionTarget (the shared subdivision drives these) ────────────

    int[] ISolidRegionTarget.SolidIterations => _iterationBuffer;
    uint[] ISolidRegionTarget.SolidColors => ColorBuffer;
    float[][] ISolidRegionTarget.SolidAux => new[]
    {
        _smoothBuffer, _distanceBuffer, NormalXBuffer, NormalYBuffer,
        _finalZrBuffer, _finalZiBuffer, _finalDrBuffer, _finalDiBuffer,
    };

    void ISolidRegionTarget.SolidBegin(byte[] done, byte[]? batchCount)
    {
        _maskDone = done;
        _batchCount = batchCount;
    }

    void ISolidRegionTarget.SolidPass(byte[]? mark, bool[]? rows, CancellationToken ct)
    {
        _mask = mark;
        _maskRows = rows;
        _gpuPassMasked = false;
        var kernel = GpuKernel;
        _gpuPassMask = mark != null && kernel is { SupportsPixelMask: true } && UseGpuCompute
            ? FracturingFog.Rendering.GpuPixelMask.FromBytes(mark, Width, Height) : null;
        if (_gpuPassMask != null) kernel!.PixelMask = _gpuPassMask;
        try { CalculateInternal(ct); }
        finally
        {
            if (_gpuPassMask != null) kernel!.PixelMask = null;
            _gpuPassMask = null;
        }
    }

    // #1394 part 1 — the frame will go to the shallow GPU kernel, which takes the mask
    // (TryDispatchGpu's gate; it only steers Auto's decision).
    private bool SolidOnGpu
        => GpuKernel is { SupportsPixelMask: true } && UseGpuCompute && Zoom <= MandelbrotCalculator.MaxGpuZoom
           && (FractalType != FractalType.Multibrot
               || new FracturingFog.Models.FractalKernels.MultibrotKernel(FractalParameters.MultibrotExponent).Exponent <= MaxGpuMultibrotExponent);

    bool ISolidRegionTarget.SolidFewPasses => SolidOnGpu;

    double ISolidRegionTarget.SolidAutoMinCostlyShare
        => !SolidOnGpu ? SolidRegionSubdivision.AutoMinCostlyShare
           : FracturingFog.Rendering.GpuPeriodicity.Mode != FracturingFog.Rendering.GpuPeriodicity.Off
               ? SolidRegionSubdivision.AutoNever
               : SolidRegionSubdivision.AutoMinCostlyShareGpu;

    // A GPU pass that took the mask computed only the marked pixels, like a CPU one.
    bool ISolidRegionTarget.SolidFrameOnGpu => _lastFrameOnGpu && !_gpuPassMasked;

    // Julia and Multibrot have no closed-form in-set test: every in-set pixel iterates
    // to the cap.
    bool ISolidRegionTarget.SolidFreeInSet(int x, int y) => false;

    void ISolidRegionTarget.SolidEnd(CancellationToken ct)
    {
        _mask = null;
        _maskRows = null;
        _maskDone = null;
        _batchCount = null;
    }
}
