// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Calculators/MandelbrotCalculator.La4.cs
//
// #1628 (epic #1623, S5) — the LA evaluator with its plain tail on 4 AVX2 lanes. It
// runs EXACTLY the operations of the scalar EvaluateLa (MandelbrotCalculator.La.cs),
// lane by lane, so the frame is byte-identical to the scalar one (the contract #1274
// set for rebasing). This file holds no code translated from Imagina: its tail is FF's
// rebased loop (ResumePixelPTRebased / the PT4 rows, with Zhuoran's rebasing), and for
// the AT and the stages it calls RunLaAt and LaRunStages in MandelbrotCalculator.La.cs,
// which are the translations of Imagina's code (credited there, in THIRD-PARTY-NOTICES.md
// §3 and Docs/Resources-Bibliography.md#imagina). Imagina's own vector evaluator (its
// vec4 / mask64x4 EvaluationContext) was not read for this.
//
// OFF by default (FF_LA_SIMD=1): on the bench CPU (i5-13420H, AVX2, SMT, P + E cores)
// it measured no faster than the scalar evaluator — ×0.92–1.05 on tail-heavy scenes,
// ×0.86–0.90 on the stage-heavy deepest ones — design doc §4.4.
//
// Lanes are grouped by phase (design doc §4.4 measures the alternative):
//   • a pixel's AT and stages run in scalar when its lane loads it and whenever it
//     climbs back from the tail (LaRunStages, the scalar code itself);
//   • the plain tail — most of the steps on every bench scene but the deepest — runs on
//     4 lanes, one perturbation step per lane per iteration, as the PT4 rows do
//     (#1613): gathered reference values, vector escape / rebase masks, and scalar work
//     only when a lane escapes, reaches the cap or rebases (then it climbs back to the
//     stages in scalar and returns to the tail).
// Two quads per thread interleave so the core overlaps their dependency chains.
//
// Not covered (those rows stay scalar): BLA skips in the tail (LaTailBla) and no
// climbing back (LaClimbOnRebase off).

using System;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using FracturingFog.FFMath;
using FracturingFog.Interefaces;

namespace FracturingFog;

public sealed partial class MandelbrotCalculator
{
    /// <summary>#1628 — the LA evaluator's plain tail on 4 AVX2 lanes (byte-identical to
    /// the scalar evaluator). Off by default: measured no faster than scalar on the bench
    /// CPU (design doc §4.4). FF_LA_SIMD=1 turns it on where AVX2 is present.</summary>
    public bool LaSimd { get; set; } = Environment.GetEnvironmentVariable("FF_LA_SIMD") == "1";

    private bool _lastFrameUsedLaSimd;
    private long _laTailClimbs;

    /// <summary>#1628 — the last 4-lane frame's climbs from the tail back to the stages
    /// (each a scalar round trip). Diagnostic.</summary>
    public long LastLaTailClimbs => _laTailClimbs;

    /// <summary>#1628 — the last deep frame went through the 4-lane LA evaluator.</summary>
    public bool LastFrameUsedLaSimd => _lastFrameUsedLaSimd;

    // #1628 — gather the reference with AVX2 gathers (FF_LA_GATHER=1) instead of four
    // scalar loads; for measuring (design doc §4.4).
    private static readonly bool LaGather = Environment.GetEnvironmentVariable("FF_LA_GATHER") == "1";

    private bool LaSimdActive => LaSimd && Avx2.IsSupported && !LaTailBla && LaClimbOnRebase;

    private unsafe struct LaQuad
    {
        public Vector256<double> Dr, Di, Zr, Zi, Drv, Div, DcR;
        public Vector128<int> M, N;
        public fixed int Pix[4];
        public int Active;
    }

    private sealed class LaRow
    {
        public required LaStageTable T;
        public required int[] Px;
        public required long[] PerStage;
        public LaAt? At;
        public int Count, MaxIter, RefLen, RowBase;
        public double DcI, Scale, HalfW, OffX, RecycleDx;
        public long LaSteps, PlainSteps, AtSteps, AtPixels, Climbs;
    }

    // A row through the 4-lane evaluator: the marked pixels of [xBegin, xEnd) are queued
    // and fed to two quads.
    private unsafe void ComputeRowLa4<TMap>(
        int y, double scale, int maxIter, int rowBase, TMap colorMap, int xBegin, int xEnd)
        where TMap : IColorMap
    {
        var t = _laTable!;
        int count = 0;
        var px = new int[Math.Max(1, xEnd - xBegin)];
        for (int x = xBegin; x < xEnd; x++)
            if (Marked(rowBase + x)) px[count++] = x;   // #1344
        if (count == 0) return;

        var row = new LaRow
        {
            T = t, Px = px, PerStage = new long[t.Stages], At = _laAt, Count = count, MaxIter = maxIter,
            RefLen = _refOrbitLen, RowBase = rowBase,
            DcI = (SubRectOffsetY + y - EffectiveImageHeight * 0.5) * scale + _refRecycleDy,
            Scale = scale, HalfW = EffectiveImageWidth * 0.5, OffX = SubRectOffsetX, RecycleDx = _refRecycleDx,
        };
        fixed (double* pZr = _refZr, pZi = _refZi)
        {
            LaQuad qa = default, qb = default;
            int next = 0;
            for (int k = 0; k < 4; k++) LaLoad(ref qa, k, ref next, row, colorMap);
            for (int k = 0; k < 4; k++) LaLoad(ref qb, k, ref next, row, colorMap);
            while ((qa.Active | qb.Active) != 0)
            {
                if (qa.Active != 0) LaTailStep4(ref qa, ref next, row, pZr, pZi, colorMap);
                if (qb.Active != 0) LaTailStep4(ref qb, ref next, row, pZr, pZi, colorMap);
            }
        }

        var tot = _laStageSteps;
        if (tot.Length == row.PerStage.Length)
            for (int k = 0; k < row.PerStage.Length; k++) Interlocked.Add(ref tot[k], row.PerStage[k]);
        Interlocked.Add(ref _laAtStepsTotal, row.AtSteps);
        Interlocked.Add(ref _laAtPixelsTotal, row.AtPixels);
        Interlocked.Add(ref _laPixelsTotal, count);
        Interlocked.Add(ref _laStepsTotal, row.LaSteps);
        Interlocked.Add(ref _laPlainTotal, row.PlainSteps);
        Interlocked.Add(ref _laTailClimbs, row.Climbs);
    }

    // The pixel at x enters (or re-enters) the stages at the top from (dr, di) at
    // iteration n, scalar, exactly as EvaluateLa; true when it comes out in the tail with
    // n < maxIter (state left in the ref parameters, m its reference index), false when
    // it finished (resolved here).
    private bool LaStagesToTail<TMap>(LaRow row, int x, double dcR, double mdc, TMap colorMap,
        ref double dr, ref double di, ref double zr, ref double zi, ref double drv, ref double div, ref int n, out int m)
        where TMap : IColorMap
    {
        int stage = row.T.Stages - 1, j = 0;
        LaRunStages(row.T, dcR, row.DcI, mdc, row.MaxIter, true, ref dr, ref di, ref zr, ref zi, ref drv, ref div,
            ref n, ref stage, ref j, ref row.LaSteps, row.PerStage);
        m = j;
        if (n < row.MaxIter) return true;
        // The tail's loop test fails at once: the pixel ends with the stages' last z.
        int idx = row.RowBase + x;
        IterationBuffer[idx] = n;
        FillAuxAndColorHP(idx, n, row.MaxIter, zr, zi, drv, div, colorMap);
        return false;
    }

    // Start lane k on the next queued pixel that reaches the tail (its AT and stages run
    // here), resolving the ones that finish before it; retire the lane when the queue is
    // empty.
    private unsafe void LaLoad<TMap>(ref LaQuad q, int k, ref int next, LaRow row, TMap colorMap)
        where TMap : IColorMap
    {
        while (next < row.Count)
        {
            int x = row.Px[next++];
            double dcR = (row.OffX + x - row.HalfW) * row.Scale + row.RecycleDx;
            double mdc = Math.Max(Math.Abs(dcR), Math.Abs(row.DcI));
            double dr = 0, di = 0, zr = 0, zi = 0, drv = 1, div = 0;
            int n = 0;
            if (row.At is { } at && mdc <= at.ThrC)
            {
                row.AtPixels++;
                if (RunLaAt(in at, dcR, row.DcI, row.MaxIter, out zr, out zi, out drv, out div, out n, ref row.AtSteps))
                {
                    int idx0 = row.RowBase + x;
                    IterationBuffer[idx0] = row.MaxIter;
                    FillAuxAndColorHP(idx0, row.MaxIter, row.MaxIter, zr, zi, drv, div, colorMap);
                    continue;
                }
                dr = zr; di = zi;
            }
            if (!LaStagesToTail(row, x, dcR, mdc, colorMap, ref dr, ref di, ref zr, ref zi, ref drv, ref div, ref n, out int m))
                continue;
            q.Pix[k] = x;
            q.Active |= 1 << k;
            q.DcR = q.DcR.WithElement(k, dcR);
            q.Dr = q.Dr.WithElement(k, dr); q.Di = q.Di.WithElement(k, di);
            q.Zr = q.Zr.WithElement(k, zr); q.Zi = q.Zi.WithElement(k, zi);
            q.Drv = q.Drv.WithElement(k, drv); q.Div = q.Div.WithElement(k, div);
            q.M = q.M.WithElement(k, m); q.N = q.N.WithElement(k, n);
            return;
        }
        q.Active &= ~(1 << k);
        q.DcR = q.DcR.WithElement(k, 0.0);
        q.Dr = q.Dr.WithElement(k, 0.0); q.Di = q.Di.WithElement(k, 0.0);
        q.Zr = q.Zr.WithElement(k, 0.0); q.Zi = q.Zi.WithElement(k, 0.0);
        q.Drv = q.Drv.WithElement(k, 0.0); q.Div = q.Div.WithElement(k, 0.0);
        q.M = q.M.WithElement(k, 0); q.N = q.N.WithElement(k, 0);
    }

    /// <summary>One iteration of EvaluateLa's plain tail on 4 lanes, operation for
    /// operation; lanes that escape, reach the cap or rebase are handled in scalar.</summary>
    private unsafe void LaTailStep4<TMap>(ref LaQuad q, ref int next, LaRow row, double* pZr, double* pZi, TMap colorMap)
        where TMap : IColorMap
    {
        var one = Vector256.Create(1.0);
        var oneI = Vector128.Create(1);

        var dr = q.Dr; var di = q.Di; var drv = q.Drv; var div = q.Div; var m = q.M;
        // Every lane's m is in [0, refLen): a lane at refLen - 1 rebases below before m
        // advances; idle lanes carry m = 0.
        Vector256<double> Zr, Zi;
        if (LaGather)
        {
            Zr = Avx2.GatherVector256(pZr, m, 8);
            Zi = Avx2.GatherVector256(pZi, m, 8);
        }
        else
        {
            // Four scalar loads: AVX2 gathers are slow on some cores (design doc §4.4).
            int m0 = m.GetElement(0), m1 = m.GetElement(1), m2 = m.GetElement(2), m3 = m.GetElement(3);
            Zr = Vector256.Create(pZr[m0], pZr[m1], pZr[m2], pZr[m3]);
            Zi = Vector256.Create(pZi[m0], pZi[m1], pZi[m2], pZi[m3]);
        }
        var zr = Avx.Add(Zr, dr);
        var zi = Avx.Add(Zi, di);
        var zmag2 = Avx.Add(Avx.Multiply(zr, zr), Avx.Multiply(zi, zi));
        int esc = Avx.MoveMask(Avx.Compare(zmag2, Vector256.Create(EscapeRadius2),
            FloatComparisonMode.OrderedGreaterThanOrEqualNonSignaling)) & q.Active;
        var dmag2 = Avx.Add(Avx.Multiply(dr, dr), Avx.Multiply(di, di));
        int reb = (Avx.MoveMask(Avx.Compare(zmag2, dmag2, FloatComparisonMode.OrderedLessThanNonSignaling))
                   | Sse.MoveMask(Sse2.CompareGreaterThan(m, Vector128.Create(row.RefLen - 2)).AsSingle()))   // m + 1 >= refLen
                  & q.Active & ~esc;

        // Derivative (2·x as x + x, the same IEEE result) and δ_{n+1} = (2·Z[m] + δ)·δ + dc
        // for every lane; escaping and rebasing lanes keep their state below.
        var tDrv = Avx.Subtract(Avx.Multiply(zr, drv), Avx.Multiply(zi, div));
        var tDiv = Avx.Add(Avx.Multiply(zr, div), Avx.Multiply(zi, drv));
        var nDrv = Avx.Add(Avx.Add(tDrv, tDrv), one);
        var nDiv = Avx.Add(tDiv, tDiv);
        var a = Avx.Add(Avx.Add(Zr, Zr), dr);
        var b = Avx.Add(Avx.Add(Zi, Zi), di);
        var nDr = Avx.Add(Avx.Subtract(Avx.Multiply(a, dr), Avx.Multiply(b, di)), q.DcR);
        var nDi = Avx.Add(Avx.Add(Avx.Multiply(a, di), Avx.Multiply(b, dr)), Vector256.Create(row.DcI));
        var nN = Sse2.Add(q.N, oneI);

        // The tail's loop test for the next iteration, n < maxIter.
        int capped = Sse.MoveMask(Sse2.CompareGreaterThan(nN, Vector128.Create(row.MaxIter - 1)).AsSingle()) & q.Active & ~(esc | reb);

        int hold = esc | reb;
        if ((hold | capped) == 0)
        {
            q.Dr = nDr; q.Di = nDi; q.Drv = nDrv; q.Div = nDiv; q.Zr = zr; q.Zi = zi;
            q.M = Sse2.Add(m, oneI); q.N = nN;
            row.PlainSteps += System.Numerics.BitOperations.PopCount((uint)q.Active);
            return;
        }

        var keep = s_laneMaskD[hold];
        var keepI = s_laneMaskI[hold];
        q.Dr = Avx.BlendVariable(nDr, dr, keep); q.Di = Avx.BlendVariable(nDi, di, keep);
        q.Drv = Avx.BlendVariable(nDrv, drv, keep); q.Div = Avx.BlendVariable(nDiv, div, keep);
        q.Zr = zr; q.Zi = zi;
        q.M = Sse41.BlendVariable(Sse2.Add(m, oneI), m, keepI);
        q.N = Sse41.BlendVariable(nN, q.N, keepI);
        row.PlainSteps += System.Numerics.BitOperations.PopCount((uint)(q.Active & ~hold));

        for (int k = 0; k < 4; k++)
        {
            int bit = 1 << k;
            if ((esc & bit) != 0)
            {
                // Escape: z of this iteration, derivative before its update.
                int idx = row.RowBase + q.Pix[k];
                int n = q.N.GetElement(k);
                IterationBuffer[idx] = n;
                FillAuxAndColorHP(idx, n, row.MaxIter, zr.GetElement(k), zi.GetElement(k),
                    drv.GetElement(k), div.GetElement(k), colorMap);
                LaLoad(ref q, k, ref next, row, colorMap);
            }
            else if ((reb & bit) != 0)
            {
                // Rebase and climb back to the stages at z_n (scalar), then back to the tail.
                row.Climbs++;
                double pdr = zr.GetElement(k), pdi = zi.GetElement(k), pzr = pdr, pzi = pdi;
                double pdrv = drv.GetElement(k), pdiv = div.GetElement(k);
                int n = q.N.GetElement(k);
                double dcR = q.DcR.GetElement(k);
                double mdc = Math.Max(Math.Abs(dcR), Math.Abs(row.DcI));
                if (LaStagesToTail(row, q.Pix[k], dcR, mdc, colorMap,
                        ref pdr, ref pdi, ref pzr, ref pzi, ref pdrv, ref pdiv, ref n, out int mm))
                {
                    q.Dr = q.Dr.WithElement(k, pdr); q.Di = q.Di.WithElement(k, pdi);
                    q.Zr = q.Zr.WithElement(k, pzr); q.Zi = q.Zi.WithElement(k, pzi);
                    q.Drv = q.Drv.WithElement(k, pdrv); q.Div = q.Div.WithElement(k, pdiv);
                    q.M = q.M.WithElement(k, mm); q.N = q.N.WithElement(k, n);
                }
                else LaLoad(ref q, k, ref next, row, colorMap);
            }
            else if ((capped & bit) != 0)
            {
                // In set at the cap: z of the last iteration, derivative after its update.
                int idx = row.RowBase + q.Pix[k];
                IterationBuffer[idx] = row.MaxIter;
                FillAuxAndColorHP(idx, row.MaxIter, row.MaxIter, zr.GetElement(k), zi.GetElement(k),
                    q.Drv.GetElement(k), q.Div.GetElement(k), colorMap);
                LaLoad(ref q, k, ref next, row, colorMap);
            }
        }
    }
}
