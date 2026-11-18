// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Calculators/MandelbrotCalculator.GpuFx.cs
//
// #1882 (epic #1428, C6b) — the GPU past FloatExpZoomLog10 (1e290), where the pixel scale
// underflows double and the CPU renders the float-exp rows (CalculateFloatExpRows, C5a).
// The float-exp LA kernel (#1664: LaKernelSource.BuildFloatExp, Imagina's LA evaluator,
// see the bibliography #imagina) already keeps the scale as a float mantissa and an int
// exponent and every δ, dc and coefficient in float-exp; only its host API took a double
// scale. Here it gets the FloatExp scale (mantissa, binary exponent) and the float-exp LA
// table (LaStageTableFx, C5c), without the AT, as the CPU float-exp rows run it.

using System;
using System.Threading;
using FracturingFog.FFMath;
using FracturingFog.Interefaces;
using GpuRoute = FracturingFog.Render.GpuRoute;

namespace FracturingFog;

public sealed partial class MandelbrotCalculator
{
    /// <summary>#1882 — true when the last frame ran past <see cref="FloatExpZoomLog10"/>
    /// on the GPU (the float-exp LA kernel with a float-exp pixel scale).</summary>
    public bool LastFrameUsedGpuFloatExpPixels { get; private set; }

    /// <summary>#1886 — true when the last GPU float-exp LA frame ran the AT built from the
    /// float-exp table (LaAtFx).</summary>
    public bool LastFrameUsedGpuLaAtFx { get; private set; }

    // #1882 — this float-exp-pixel frame on the float-exp LA kernel. False (with the route's
    // reason) where it declines or the dispatch fails: the CPU float-exp rows run then.
    private bool TryRunGpuFloatExpPixels<TMap>(TMap colorMap, int maxIt, CancellationToken ct)
        where TMap : IColorMap
    {
        LastFrameUsedGpuFloatExpPixels = false;
        var kernel = GpuKernel;
        if (!GpuDeepHere || kernel == null || !(kernel.SupportsPerturbation || FloatExpHere(kernel))) return false;
        int[]? prCap = PerRowMaxIter;
        string? why =
              !LaFxRouteHere(kernel) ? "float-exp pixels render on the GPU only on the float-exp LA kernel (FF_GPU_LA_FX=1, float-exp the device's deep kernel, LA wanted)"
            : LatticeAligned ? "pixel reuse while panning (#1330) renders deep frames on the CPU"
            : ForceScalarPtPath ? "a perturbation diagnostic toggle keeps deep zoom on the CPU"
            : prCap != null && prCap.Length >= Height ? "per-tile iteration caps render on the CPU only"
            : _refOrbitLen < 2 ? "the reference orbit is too short"
            : null;
        LaStageTableFx? t = null;
        if (why == null)
        {
            t = EnsureLaTableFx();
            if (t is not { Finite: true, Stages: >= 1 }) why = "the float-exp LA table is empty or not finite";
        }
        int effImgW = EffectiveImageWidth, effImgH = EffectiveImageHeight;
        var scale = FloatExp.FromLog10(Math.Log10(3.5 / Math.Max(effImgW, effImgH)) - ZoomLog10);
        if (why == null && Math.Abs(scale.E) > FracturingFog.Rendering.GpuLaFxParams.MaxScaleExponent)
            why = "the pixel scale is past the kernel's exponent range";
        if (why != null)
        {
            _gpuSkip = GpuRoute.Cpu($"zoom > 1e{FloatExpZoomLog10:0}", $"past 1e{FloatExpZoomLog10:0} (float-exp pixels): {why}");
            return false;
        }

        // dc = (x + off)·scale: the CPU rows' offX + x − W/2 + jitter (#1851), in pixels.
        double offX0 = SubRectOffsetX - effImgW * 0.5 + PixelJitter.X;
        double offY0 = SubRectOffsetY - effImgH * 0.5 + PixelJitter.Y;
        int scaleE = (int)scale.E;
        bool wave = FracturingFog.Rendering.GpuWavefront.UsePerturb;
        // #1886 (C5g) — the AT over the float-exp table, as the CPU rows select it.
        double hd = Math.Sqrt(effImgW * effImgW * 0.25 + effImgH * effImgH * 0.25);
        SelectLaAtFx(t, scale * (hd + Math.Sqrt(PixelJitter.X * PixelJitter.X + PixelJitter.Y * PixelJitter.Y)));
        var gat = _laAtFx is { } afx ? FracturingFog.Rendering.GpuLaAt.FromFx(afx) : default;
        try
        {
            kernel.RunPerturbLaFloatExp(
                Width, Height, scale.M, maxIt, EscapeRadius2,
                offX0, offY0,
                _refZr, _refZi, _refOrbitLen,
                t!.GpuStepsFloatExp, t.GpuInts, t.Stages, gat,
                IterationBuffer, SmoothBuffer,
                FinalZrBuffer, FinalZiBuffer, FinalDrBuffer, FinalDiBuffer,
                scaleE);
        }
        catch (Exception ex)
        {
            OnGpuPerturbationFailed(ex);
            return false;
        }

        // The kernel writes dz/dc · 2^scaleE (#1660). #1843: straight into the distance
        // unit (· 2^-scaleE-DistanceUnitExp2), finite past double's range.
        _po.CancellationToken = ct;
        int unitE = DistanceUnitExp2;
        ParallelForRows(0, Height, _po, y =>
        {
            if (ct.IsCancellationRequested || !GpuRowMarked(y)) return;
            int rb = y * Width;
            for (int x = 0; x < Width; x++)
            {
                int idx = rb + x;
                if (!GpuMarked(idx)) continue;
                FillAuxAndColorHPUnit(idx, IterationBuffer[idx], maxIt,
                    FinalZrBuffer[idx], FinalZiBuffer[idx],
                    Math.ScaleB((double)FinalDrBuffer[idx], -scaleE - unitE), Math.ScaleB((double)FinalDiBuffer[idx], -scaleE - unitE), colorMap);
            }
        });
        LastFrameUsedGpuPerturbation = true;
        LastFrameUsedGpuFloatExp = true;
        LastFrameUsedGpuLa = true;
        LastFrameUsedGpuLaFloatExp = true;
        LastFrameUsedGpuLaTableFx = true;
        LastFrameUsedGpuFloatExpPixels = true;
        LastFrameUsedGpuLaAtFx = FracturingFog.Rendering.GpuLaFxParams.AtUsable(gat);   // #1886; #1887: as the kernel gets it
        LastFrameUsedGpuWavefront = wave;
        _lastFrameUsedLa = true;   // the precision label's " LA"
        LogGpuPerturbEngaged();
        return true;
    }
}
