// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown
// SPDX-FileCopyrightText: Yu, Zhuoran (GitHub 5E-324), Imagina <https://github.com/5E-324/Imagina>, AGPL-3.0

// Calculators/MandelbrotCalculator.La.cs
//
// #1626 (epic #1623, S3) — the per-pixel LA stage evaluator, scalar double, opt-in
// (UseLaEvaluator, FF_LA=1) on the CPU deep path. It reads the stage table of #1625
// (FFMath.LaStageTable) and replaces the PT4 / PT8 rows for the frame; the BLA path
// is unchanged when it is off.
//
// DERIVED CODE: the stage loop is a C# translation of Imagina's
// HInfLAEvaluator::EvaluationContext::EvaluateLA, and the derivative update of
// LAInfo::EvaluateDzdc (Imagina by GitHub user 5E-324, profile name "Yu, Zhuoran",
// https://github.com/5E-324/Imagina, HInfLAEvaluator.{h,cpp} at commit cd046377, read
// 2026-11-03, AGPL-3.0; see THIRD-PARTY-NOTICES.md §3 and
// Docs/Resources-Bibliography.md#imagina). It came here from the S1 probe
// (Probes/LaProbe.cs). The maths is in Docs/Technical/Period-Synchronised-LA-DesignPlan.md
// §2.4; §2.7 maps each part to its Imagina counterpart and §2.6 lists where FF
// departs (marked "FF:" below). The plain tail is FF's own rebased loop
// (ResumePixelPTRebased, with Zhuoran's rebasing), not Imagina's EvaluatePT.
//
// #1627 (S4) adds the approximation transform first: a pixel with ‖δc‖ under the AT's
// bound iterates z' ← z'² + c' (one step per top-stage period, Math/LaAt.cs) until
// |z'|² exceeds the AT's escape radius or the cap is reached, then converts back
// (z = z'/A, a rebase) and continues with the stages. That block is a translation of
// the AT part of Imagina's HInfLAEvaluationTask::Evaluate and of EvaluateAT.
//
// One pixel:
//   0. The AT, if the frame has a usable one (#1627).
//   1. Stages from the top down at index j = 0. A stage whose first step's δc bound
//      the pixel fails is skipped. At step j: w = δ(2·Ref + δ); if ‖w‖ ≥ Thr or
//      ‖δc‖ ≥ ThrC, drop a stage at the step's next index; else δ ← A·w + B·δc,
//      n += L, z = δ + Ref_{j+1}, and rebase (j ← 0, δ ← z) at the stage's end or when
//      ‖δ‖ > ‖z‖ — then climb back to the top stage (FF).
//   2. Plain perturbation from the stage-0 next index, with FF's escape test and
//      rebasing (|z| < |δ| or the end of the reference), optionally BLA skips by
//      reference index (LaTailBla, off); a rebase climbs back to the stages (FF).
// LA steps keep ‖δ‖ ≤ τ‖Z‖ at every iteration they cover, so no step passes the
// escape; the plain phase finds it. The derivative dz/dc rides along exactly
// through each step: d ← A·2·z_s·d + B.

using System;
using System.Threading;
using FracturingFog.FFMath;
using FracturingFog.Interefaces;

namespace FracturingFog;

public sealed partial class MandelbrotCalculator
{
    /// <summary>#1629 — which evaluator renders CPU deep frames (FractalParameters.DeepLa).
    /// Auto (default) = the LA stages at zoom ≥ <see cref="LaAutoMinZoom"/>, the PT4 / PT8
    /// BLA rows below (design doc §4.5); On =
    /// LA at every deep zoom, also in place of GPU perturbation; Off = the BLA rows. The
    /// environment overrides it for A/B: FF_LA=0 (Off) or FF_LA=1 (On).</summary>
    public DeepLaMode LaMode { get; set; } = DeepLaMode.Auto;

    /// <summary>#1629 — FF_LA, read once: Off / On for every calculator, or null.</summary>
    public static readonly DeepLaMode? LaModeOverride = Environment.GetEnvironmentVariable("FF_LA") switch
    {
        "0" => DeepLaMode.Off,
        "1" => DeepLaMode.On,
        _ => null,
    };

    /// <summary>#1629 — Auto's zoom threshold: below it the BLA rows were faster on 48 of
    /// FF's built-in regions, above it LA on all but two (within noise) — design doc §4.5.</summary>
    public const double LaAutoMinZoom = 1e30;

    /// <summary>#1629 — the mode this calculator renders with (the environment override,
    /// else <see cref="LaMode"/>).</summary>
    public DeepLaMode EffectiveLaMode => CalibrationLaMode ?? LaModeOverride ?? LaMode;   // #1786: a calibration route first

    /// <summary>#1626 — LA forced on (true) or off (false); kept for probes and tests.
    /// Since #1629 it maps to <see cref="LaMode"/> On / Off; reading it says whether the
    /// effective mode is not Off.</summary>
    public bool UseLaEvaluator
    {
        get => EffectiveLaMode != DeepLaMode.Off;
        set => LaMode = value ? DeepLaMode.On : DeepLaMode.Off;
    }

    // #1629 — the mode wants LA for this frame. (A periodic reference below the threshold
    // was measured neutral — the period-3 nucleus at 1e14..1e28 — so it is not a reason.)
    private bool LaWanted => EffectiveLaMode switch
    {
        DeepLaMode.On => true,
        DeepLaMode.Auto => Zoom >= EffectiveLaAutoMinZoom,   // #1665: measured crossover
        _ => false,
    };

    /// <summary>#1626 — after a rebase, restart at the top stage (FF, design doc §2.4)
    /// rather than stay in the stage the pixel fell to (Imagina). FF_LA_CLIMB=0 turns it off.</summary>
    public bool LaClimbOnRebase { get; set; } = Environment.GetEnvironmentVariable("FF_LA_CLIMB") != "0";

    /// <summary>#1614 — climb back one stage at a shared step boundary: after a stage-k
    /// step that ends where a stage-(k + 1) step begins, try that step next (its own
    /// validity test decides; a failure drops back to the same place). Without it a pixel
    /// that fell a stage stays there until a rebase or the end of the reference (FF,
    /// design doc §2.6 item 12). FF_LA_ASCEND=0 turns it off.</summary>
    public bool LaAscend { get; set; } = Environment.GetEnvironmentVariable("FF_LA_ASCEND") != "0";

    /// <summary>#1614 — the fixed-length rule of LA stages 1 and up, in bits per stage
    /// (LaStageTable.DefaultStageBits = 3; 4 is Imagina's). FF_LA_STAGE_BITS sets it (1..4).</summary>
    public int LaStageBits { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("FF_LA_STAGE_BITS"), out int sb) && sb is >= 1 and <= LaStageTable.ImaginaStageBits
            ? sb : LaStageTable.DefaultStageBits;

    /// <summary>#1626 — BLA skips in the plain tail (design doc §3). Off by default: measured
    /// on the bench scenes they save about a fifth of the tail's steps but no time, and cost
    /// exact matches (design doc §4.2). FF_LA_TAIL_BLA=1 turns them on.</summary>
    public bool LaTailBla { get; set; } = Environment.GetEnvironmentVariable("FF_LA_TAIL_BLA") == "1";

    /// <summary>#1626 — the period test the stages are cut with (design doc §2.6 item 7).
    /// FF_LA_PERIOD=minmag selects Imagina's minimum-|Z| test.</summary>
    public LaPeriodTest LaPeriodTest { get; set; } =
        string.Equals(Environment.GetEnvironmentVariable("FF_LA_PERIOD"), "minmag", StringComparison.OrdinalIgnoreCase)
            ? LaPeriodTest.MinMagnitude : LaPeriodTest.ThresholdDip;

    /// <summary>#1627 — the approximation transform before the stages (design doc §2.5).
    /// On by default with the evaluator; FF_LA_AT=0 turns it off.</summary>
    public bool UseLaAt { get; set; } = Environment.GetEnvironmentVariable("FF_LA_AT") != "0";

    /// <summary>#1627 — the AT runs at tolerance s·τ, s ≥ 1 (its escape radius and δc
    /// bound scale with s; design doc §2.6 item 10). 1 = Imagina's AT. FF_LA_AT_LOOSEN sets it.</summary>
    public double LaAtLoosen { get; set; } =
        double.TryParse(Environment.GetEnvironmentVariable("FF_LA_AT_LOOSEN"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double s) && s >= 1 ? s : 1.0;

    /// <summary>#1627 — the AT is usable when the view's c' extent is at least
    /// 2^-bits of c''s offset (design doc §2.6 item 10). 0 (FF's default) keeps the AT's
    /// rounded c' as fine as perturbation's δc; 16 is Imagina's margin.
    /// FF_LA_AT_OFFSET_BITS sets it.</summary>
    public int LaAtOffsetBits { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("FF_LA_AT_OFFSET_BITS"), out int b) && b >= 0 ? b : 0;

    private long _laPixelsTotal, _laStepsTotal, _laPlainTotal, _laBlaTotal, _laAtStepsTotal, _laAtPixelsTotal;
    private bool _lastFrameUsedLa;
    private LaAt? _laAt;
    private long[] _laStageSteps = [];

    /// <summary>#1627 — the last LA frame's steps per stage (index = stage), summed over
    /// pixels. Diagnostic / probe use.</summary>
    public long[] LastLaStageSteps => (long[])_laStageSteps.Clone();

    /// <summary>#1627 — the AT the last LA frame used (null: none usable, or off).</summary>
    public LaAt? LastLaAt => _laAt;

    /// <summary>#1627 — the last LA frame's AT work: pixels that entered the AT and AT
    /// steps, summed.</summary>
    public (long Pixels, long Steps) LastLaAtWork => (_laAtPixelsTotal, _laAtStepsTotal);

    /// <summary>#1626 — the last CPU deep frame's LA work: pixels evaluated, LA steps,
    /// plain perturbation steps and BLA skips in the tail (all per pixel, summed).
    /// Diagnostic / probe use.</summary>
    public (long Pixels, long LaSteps, long PlainSteps, long BlaSkips) LastLaWork
        => (_laPixelsTotal, _laStepsTotal, _laPlainTotal, _laBlaTotal);

    /// <summary>#1626 — the last deep frame went through the LA evaluator.</summary>
    public bool LastFrameUsedLa => _lastFrameUsedLa;

    private bool LaActive =>
        LaWanted && _laTable is { Finite: true } && !DisableAcceleration && AllowPtRebasing && !UseDdRebaseReference;

    private void ResetLaWork()
    {
        _laPixelsTotal = _laStepsTotal = _laPlainTotal = _laBlaTotal = _laAtStepsTotal = _laAtPixelsTotal = _laTailClimbs = 0;
        _laAt = null;
        _laStageSteps = new long[_laTable?.Stages ?? 0];
    }

    // #1627 — pick the frame's AT once the table is current (dcMaxAbs = the view's half
    // diagonal in δc, as Imagina's CreateATFromLA radius).
    private void SelectLaAt(double dcMaxAbs)
    {
        _laAt = UseLaAt && _laTable != null ? LaAt.Select(_laTable, dcMaxAbs, LaAtLoosen, LaAtOffsetBits) : null;
    }

    // A row through the LA evaluator. Per pixel, so a lattice range (#1330) and the
    // pixel mask (#1344) are exact, as in ComputeRowPeriodic.
    private void ComputeRowLa<TMap>(
        int y, double scale, int maxIter, int rowBase, TMap colorMap, int xBegin, int xEnd)
        where TMap : IColorMap
    {
        double halfW = EffectiveImageWidth * 0.5, halfH = EffectiveImageHeight * 0.5;
        double dcY = (SubRectOffsetY + y - halfH) * scale + _refRecycleDy;
        var t = _laTable!;
        var bla = LaTailBla ? _blaTable : null;
        long la = 0, pt = 0, bs = 0, px = 0, ats = 0, atp = 0;
        LaAt? at = _laAt;
        var perStage = new long[t.Stages];
        for (int x = xBegin; x < xEnd; x++)
        {
            int idx = rowBase + x;
            if (!Marked(idx)) continue;   // #1344
            double dcX = (SubRectOffsetX + x - halfW) * scale + _refRecycleDx;
            EvaluateLa(t, at, bla, dcX, dcY, maxIter, idx, colorMap, ref la, ref pt, ref bs, ref ats, ref atp, perStage);
            px++;
        }
        var tot = _laStageSteps;
        if (tot.Length == perStage.Length)
            for (int k = 0; k < perStage.Length; k++) Interlocked.Add(ref tot[k], perStage[k]);
        Interlocked.Add(ref _laAtStepsTotal, ats);
        Interlocked.Add(ref _laAtPixelsTotal, atp);
        Interlocked.Add(ref _laPixelsTotal, px);
        Interlocked.Add(ref _laStepsTotal, la);
        Interlocked.Add(ref _laPlainTotal, pt);
        Interlocked.Add(ref _laBlaTotal, bs);
    }

    private void EvaluateLa<TMap>(
        LaStageTable t, LaAt? atOpt, BlaTable? bla, double dcR, double dcI, int maxIter, int idx, TMap colorMap,
        ref long laSteps, ref long plainSteps, ref long blaSkips, ref long atSteps, ref long atPixels, long[] perStage)
        where TMap : IColorMap
    {
        var data = t.Data;
        int top = t.Stages - 1;
        int refLen = _refOrbitLen;
        bool climb = LaClimbOnRebase;
        int[]? up = LaAscend ? t.Up : null;   // #1614
        double mdc = Math.Max(Math.Abs(dcR), Math.Abs(dcI));

        double dr = 0, di = 0;          // δ against the current reference index
        double zr = 0, zi = 0;          // the full value z (the escape value on exit)
        double drv = 1, div = 0;        // dz/dc, as ResumePixelPTRebased (IQ convention)
        int n = 0;                      // iteration
        int stage = top, j = 0;         // step index in `stage`; for stage -1 a reference index
        var cyc = new LaCycle { Enabled = DeepPeriodicity, Idx = idx };   // #1804

        // ── 0. The AT (#1627; Imagina's AT block of Evaluate + EvaluateAT) ──
        if (atOpt is { } at && mdc <= at.ThrC)
        {
            atPixels++;
            if (RunLaAt(in at, dcR, dcI, maxIter, out zr, out zi, out drv, out div, out n, ref atSteps))
            {
                IterationBuffer[idx] = maxIter;
                FillAuxAndColorHP(idx, maxIter, maxIter, zr, zi, drv, div, colorMap);
                return;
            }
            dr = zr; di = zi;
        }

        while (true)
        {
            // ── 1. The stages (LaRunStages; climbing back restarts them at the top) ──
            LaRunStages(t, up, dcR, dcI, mdc, maxIter, climb, ref dr, ref di, ref zr, ref zi, ref drv, ref div,
                ref n, ref stage, ref j, ref laSteps, perStage, ref cyc);
            if (cyc.Caught) { Interlocked.Increment(ref _deepPeriodicPixels); break; }   // #1804: n = maxIter

            // ── 2. Plain perturbation from reference index j (FF's rebased loop) ──
            int m = j;
            bool back = false;
            while (n < maxIter)
            {
                if (bla != null)
                {
                    int bi = bla.Lookup(m, dr * dr + di * di, Math.Min(refLen - 1, m + (maxIter - n)));
                    if (bi >= 0)
                    {
                        ref readonly var e = ref bla.Data[bi];
                        if (e.L >= 2)
                        {
                            double nDr = e.ARe * dr - e.AIm * di + (e.BRe * dcR - e.BIm * dcI);
                            double nDi = e.ARe * di + e.AIm * dr + (e.BRe * dcI + e.BIm * dcR);
                            dr = nDr; di = nDi;
                            double nDrv = e.ARe * drv - e.AIm * div + e.BRe;   // d' = A·d + B (#1782)
                            double nDiv = e.ARe * div + e.AIm * drv + e.BIm;
                            drv = nDrv; div = nDiv;
                            m += e.L; n += e.L;
                            blaSkips++;
                            continue;
                        }
                    }
                }

                double Zr = _refZr[m], Zi = _refZi[m];
                zr = Zr + dr; zi = Zi + di;
                double zmag2 = zr * zr + zi * zi;
                if (zmag2 >= EscapeRadius2) break;

                double dmag2 = dr * dr + di * di;
                bool rebase = zmag2 < dmag2 || m + 1 >= refLen;
                if (rebase && climb && top >= 0)
                {
                    // FF: rebase and climb back to the stages at z_n (their step from Z_0
                    // makes this iteration's update, derivative included).
                    dr = zr; di = zi;
                    back = true;
                    break;
                }

                double nDrv2 = 2.0 * (zr * drv - zi * div) + 1.0;
                double nDiv2 = 2.0 * (zr * div + zi * drv);
                drv = nDrv2; div = nDiv2;
                if (rebase) { dr = zr; di = zi; Zr = 0; Zi = 0; m = 0; }
                double a = 2.0 * Zr + dr, b = 2.0 * Zi + di;
                double nDr2 = a * dr - b * di + dcR;
                double nDi2 = a * di + b * dr + dcI;
                dr = nDr2; di = nDi2;
                m++; n++;
                plainSteps++;
            }
            if (back) { stage = top; j = 0; continue; }
            break;
        }

        IterationBuffer[idx] = n;
        FillAuxAndColorHP(idx, n, maxIter, zr, zi, drv, div, colorMap);
    }

    // #1627 — the AT for one pixel: z' ← z'² + c' from z' = 0 until |z'|² > Esc² (false:
    // continue with the stages from z, δ = z, iteration n) or the cap (true: in set).
    // Shared by the scalar and the 4-lane evaluators (#1628), so both run the same
    // operations.
    private static bool RunLaAt(in LaAt at, double dcR, double dcI, int maxIter,
        out double zr, out double zi, out double drv, out double div, out int n, ref long atSteps)
    {
        double cr = dcR * at.CRe - dcI * at.CIm + at.RefCRe;
        double ci = dcR * at.CIm + dcI * at.CRe + at.RefCIm;
        bool inSet = RunLaAtCore(at.Len, at.Esc2, cr, ci, maxIter, out double xr, out double xi, out double er, out double ei,
            out long k, ref atSteps);
        // Back: z = z'/A (relative to Z_0 = 0, so δ = z), dz/dc = (dz'/dc')·(A·B)/A.
        zr = xr * at.InvZRe - xi * at.InvZIm; zi = xr * at.InvZIm + xi * at.InvZRe;
        double gr = er * at.CRe - ei * at.CIm, gi = er * at.CIm + ei * at.CRe;
        drv = gr * at.InvZRe - gi * at.InvZIm; div = gr * at.InvZIm + gi * at.InvZRe;
        n = inSet ? maxIter : (int)(k * at.Len);
        return inSet;
    }

    // #1886 — the AT's iteration itself, z' ← z'² + c' from z' = 0 with dz'/dc', in double:
    // shared by the double AT (above) and the float-exp one (LaAtFx, whose c' and z' are
    // renormalised doubles at any depth). True: the cap or a cycle (in set).
    private static bool RunLaAtCore(int len, double esc2, double cr, double ci, int maxIter,
        out double xr, out double xi, out double er, out double ei, out long k, ref long atSteps)
    {
        long atMax = (maxIter - 1) / len + 1;
        xr = 0; xi = 0;      // z'
        er = 0; ei = 0;      // dz'/dc'
        k = 0;
        bool inSet = false;
        // #1804 — Brent's check on z': the AT's step depends on z' and c' alone (dz'/dc'
        // doesn't steer it), so a bit-exact repeat means it cycles for ever: in set.
        bool periodicity = DeepPeriodicity;
        double snapR = double.NaN, snapI = double.NaN;
        long nextSnap = 16;
        while (true)
        {
            double nEr = 2.0 * (xr * er - xi * ei) + 1.0, nEi = 2.0 * (xr * ei + xi * er);
            er = nEr; ei = nEi;
            double nXr = xr * xr - xi * xi + cr, nXi = 2.0 * xr * xi + ci;
            xr = nXr; xi = nXi;
            k++; atSteps++;
            if (k >= atMax) { inSet = true; break; }
            if (xr * xr + xi * xi > esc2) break;
            if (periodicity)
            {
                if (SameRebaseState(xr, xi, snapR, snapI)) { inSet = true; Interlocked.Increment(ref s_laAtPeriodic); break; }
                if (k >= nextSnap) { snapR = xr; snapI = xi; nextSnap = k + Math.Min(k, 1024); }
            }
        }
        return inSet;
    }

    // #1626 — the stages, from `stage` down (Imagina's EvaluateLA, per stage), until a
    // pixel drops below stage 0 (stage = -1; j is then its reference index for the plain
    // tail). A rebase with climbing on restarts at the top stage. Shared by the scalar
    // and the 4-lane evaluators (#1628), so both run the same operations.
    // #1804 — `cyc` carries the pixel's periodicity check across calls (LaCycleRepeats).
    private static void LaRunStages(LaStageTable t, int[]? up, double dcR, double dcI, double mdc, int maxIter, bool climb,
        ref double dr, ref double di, ref double zr, ref double zi, ref double drv, ref double div,
        ref int n, ref int stage, ref int j, ref long laSteps, long[] perStage, ref LaCycle cyc)
    {
        var data = t.Data;
        int top = t.Stages - 1;
        while (true)
        {
            // #1804 — (re)entry at the top with a fresh δ (a pixel's start, a climb from
            // the stages or from the tail).
            if (j == 0 && stage == top && LaCycleRepeats(ref cyc, t, dr, di, stage, n, maxIter))
            { n = maxIter; stage = -1; return; }
            bool climbNow = false;
            for (; stage >= 0; stage--)
            {
                int s0 = t.StageStart[stage], last = t.StageCount[stage];   // last = the sentinel's index
                if (mdc >= data[s0].ThrC) { j = data[s0 + j].Next; continue; }
                while (true)
                {
                    ref readonly var s = ref data[s0 + j];
                    double ar = 2.0 * s.RefRe + dr, ai = 2.0 * s.RefIm + di;
                    double wr = dr * ar - di * ai, wi = dr * ai + di * ar;
                    // FF: the step's own δc bound too (Imagina checks only the stage's first
                    // step), and no step past maxIter, so the frame stops at the cap exactly.
                    if (Math.Max(Math.Abs(wr), Math.Abs(wi)) >= s.Thr || mdc >= s.ThrC || s.Len > maxIter - n)
                    {
                        j = s.Next;
                        break;
                    }
                    // dz/dc: d ← A·(2·z_s·d) + B, z_s = Ref + δ (LAInfo::EvaluateDzdc).
                    double zsr = s.RefRe + dr, zsi = s.RefIm + di;
                    double tr = 2.0 * (zsr * drv - zsi * div), ti = 2.0 * (zsr * div + zsi * drv);
                    double nDrv = s.ARe * tr - s.AIm * ti + s.BRe;
                    double nDiv = s.ARe * ti + s.AIm * tr + s.BIm;
                    drv = nDrv; div = nDiv;
                    // δ ← A·w + B·δc.
                    double nDr = s.ARe * wr - s.AIm * wi + (s.BRe * dcR - s.BIm * dcI);
                    double nDi = s.ARe * wi + s.AIm * wr + (s.BRe * dcI + s.BIm * dcR);
                    dr = nDr; di = nDi;
                    n += s.Len;
                    laSteps++;
                    perStage[stage]++;
                    j++;
                    ref readonly var nx = ref data[s0 + j];
                    zr = nx.RefRe + dr; zi = nx.RefIm + di;
                    if (j == last || Math.Max(Math.Abs(dr), Math.Abs(di)) > Math.Max(Math.Abs(zr), Math.Abs(zi)))
                    {
                        j = 0; dr = zr; di = zi;   // rebase: Z_0 = 0, exact
                        if (climb && stage < top) { climbNow = true; break; }
                        // #1804 — the pixel restarts this stage from (δ, j = 0).
                        if (LaCycleRepeats(ref cyc, t, dr, di, stage, n, maxIter)) { n = maxIter; stage = -1; return; }
                    }
                    // #1614 (FF) — a stage-(k + 1) step begins here: climb to it. The
                    // for-loop's stage-- brings `stage` to k + 1; that stage's δc skip and
                    // its step's own tests apply, and a failure comes back to this j.
                    else if (up != null && up[s0 + j] >= 0) { j = up[s0 + j]; stage += 2; break; }
                }
                if (climbNow) break;
            }
            if (!climbNow) return;
            stage = top; j = 0;
        }
    }

    // #1804 — the LA evaluator's periodicity check, per pixel. The stages restart from a
    // fresh δ with j = 0 at every rebase (and every climb to the top). From such a point the
    // evaluation is a function of (δ, stage) and the pixel's dc, except for the cap test
    // `s.Len > maxIter − n`; a bit-exact repeat of (δ, stage) at a restart, while that test
    // can't bind across the next cycle (maxIter − n ≥ cycle length + the longest step), means
    // the evaluation cycles: the pixel is in set. Snapshots at doubling restart counts.
    // Exactness near the cap is the #1802 argument (the last cycle before the cap takes
    // shorter steps without the check).
    internal struct LaCycle
    {
        public bool Enabled, Have, Caught;
        public double R, I;
        public int Stage, NSnap, Count, Next, Idx;
    }

    private static int s_laAtPeriodic;

    /// <summary>#1804 — AT pixels the AT's periodicity check put in set since the last
    /// call (diagnostic / tests; process-wide).</summary>
    public static int TakeLaAtPeriodicPixels() => Interlocked.Exchange(ref s_laAtPeriodic, 0);

    /// <summary>#1804 — whether an LA restart (fresh δ, j = 0) repeats a snapshot bit for
    /// bit: both parts of δ and the stage.</summary>
    public static bool SameLaRestart(double dr, double di, int stage, double snapR, double snapI, int snapStage)
        => dr == snapR && di == snapI && stage == snapStage;

    private static bool LaCycleRepeats(ref LaCycle c, LaStageTable t, double dr, double di, int stage, int n, int maxIter)
    {
        if (!c.Enabled) return false;
        if (c.Have && SameLaRestart(dr, di, stage, c.R, c.I, c.Stage)
            && (long)maxIter - n >= (long)(n - c.NSnap) + t.MaxLen)
        {
            c.Caught = true;
            LogCaught(c.Idx, 1, 1);
            return true;
        }
        if (++c.Count >= Math.Max(1, c.Next))
        {
            c.R = dr; c.I = di; c.Stage = stage; c.NSnap = n; c.Have = true;
            c.Next = c.Count * 2;
        }
        return false;
    }
}
