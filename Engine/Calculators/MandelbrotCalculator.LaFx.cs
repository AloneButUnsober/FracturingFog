// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown
// SPDX-FileCopyrightText: Yu, Zhuoran (GitHub 5E-324), Imagina <https://github.com/5E-324/Imagina>, AGPL-3.0

// Calculators/MandelbrotCalculator.LaFx.cs
//
// #1847 (epic #1428, C5c) — the LA stage evaluator (MandelbrotCalculator.La.cs,
// EvaluateLa + LaRunStages: Imagina's EvaluateLA, Zhuoran, with FF's climb / ascend /
// tail) in FloatExp, for the floatexp pixel loop past double's range. A line-for-line
// port of the scalar evaluator without the AT (#1627): the stages with their δ and δc
// tests, climbing back on a rebase, ascending at stage boundaries (#1614), the plain
// floatexp tail (BLA by reference index when LaTailBla), and the periodicity check at
// restarts (#1804, bit-exact repeats of δ in FloatExp).

using System;
using System.Threading;
using FracturingFog.FFMath;
using FracturingFog.Interefaces;

namespace FracturingFog;

public sealed partial class MandelbrotCalculator
{
    private LaStageTableFx? _laFx;
    private int _laFxGen = -1;

    // As EnsureLaTable: kept while the orbit, the tolerance, the length, the period test
    // and the stage bits are the same.
    private LaStageTableFx? EnsureLaTableFx()
    {
        double eps = BlaTolerance > 0 && double.IsFinite(BlaTolerance) ? BlaTolerance : BlaTable.DefaultEpsilon;
        double tol = LaStageTable.ToleranceFor(eps);
        int r = _refPeriod > 0 ? _refPeriod : _refOrbitLen;
        var test = LaPeriodTest;
        if (_laFx != null && _laFxGen == _refOrbitGen && _laFx.Tolerance == tol && _laFx.RefLen == r
            && _laFx.PeriodTest == test && _laFx.StageBits == LaStageBits) return _laFx;
        _laFx = LaStageTableFx.Build(_refZr, _refZi, r, tol, test, LaStageBits);
        _laFxGen = _refOrbitGen;
        return _laFx;
    }

    /// <summary>#1847 — test / probe access: the floatexp LA table the last FX frame used.</summary>
    public LaStageTableFx? LastLaTableFx => _laFx;

    // #1889 — the last FX frame's tail steps run in double (TailInDouble).
    private long _fxDoubleStepsTotal;

    /// <summary>#1889 — the last float-exp frame's plain tail steps that ran in double
    /// (of <see cref="LastLaWork"/>'s plain steps).</summary>
    public long LastFxDoubleSteps => _fxDoubleStepsTotal;

    private long _fxDoubleClimbs, _fxDoubleRenorms;

    /// <summary>#1889 — the last float-exp frame's rebases that climbed back to the stages,
    /// and derivative renormalisations, inside the double tail (diagnostic).</summary>
    public (long Climbs, long Renorms) LastFxDoubleTailEvents => (_fxDoubleClimbs, _fxDoubleRenorms);

    // #1886 (C5g) — the frame's AT over the float-exp table (LaAtFx; null: none usable, or off).
    private LaAtFx? _laAtFx;

    /// <summary>#1886 — test / probe access: the AT the last FX frame used (null: none).</summary>
    public LaAtFx? LastLaAtFx => _laAtFx;

    // #1886 — pick the frame's AT (as SelectLaAt: the view's half diagonal in δc).
    private void SelectLaAtFx(LaStageTableFx? t, FloatExp dcMaxAbs)
        => _laAtFx = UseLaAt && t != null ? LaAtFx.Select(t, dcMaxAbs, LaAtLoosen, LaAtOffsetBits) : null;

    private void EvaluateLaFx<TMap>(
        LaStageTableFx t, LaAtFx? atOpt, BlaTableFx? bla, FloatExp dcR, FloatExp dcI, int maxIter, int idx, TMap colorMap,
        ref long laSteps, ref long plainSteps, ref long blaSkips, ref long atSteps, ref long atPixels, ref long doubleSteps)
        where TMap : IColorMap
    {
        var data = t.Data;
        int top = t.Stages - 1;
        int refLen = _refOrbitLen;
        bool climb = LaClimbOnRebase;
        int[]? up = LaAscend ? t.Up : null;
        var mdc = FloatExp.Max(dcR.Abs(), dcI.Abs());

        FloatExp dr = FloatExp.Zero, di = FloatExp.Zero;
        FloatExp zr = FloatExp.Zero, zi = FloatExp.Zero;
        FloatExp drv = FloatExp.One, div = FloatExp.Zero;
        int n = 0, stage = top, j = 0;
        var cyc = new LaCycleFx { Enabled = DeepPeriodicity, Idx = idx };

        // ── 0. The AT (#1886; EvaluateLa's AT block): c' = RefC + C·δc and z' in double,
        // the conversion back z = z'/A, dz/dc = (dz'/dc')·C/A in FloatExp.
        if (atOpt is { } at && !FloatExp.AbsLess(at.ThrC, mdc))
        {
            atPixels++;
            var cp = new CFx(dcR, dcI) * at.C;
            double cr = cp.Re.ToDouble() + at.RefCRe, ci = cp.Im.ToDouble() + at.RefCIm;
            bool inSet = RunLaAtCore(at.Len, at.Esc2, cr, ci, maxIter, out double xr, out double xi,
                out double er, out double ei, out long k, ref atSteps);
            var z = CFx.Of(xr, xi) * at.InvZ;
            var g = CFx.Of(er, ei) * at.C * at.InvZ;
            zr = z.Re; zi = z.Im; drv = g.Re; div = g.Im;
            if (inSet)
            {
                IterationBuffer[idx] = maxIter;
                FillAuxAndColorHP(idx, maxIter, maxIter, zr.ToDouble(), zi.ToDouble(), drv, div, colorMap);   // #1843
                return;
            }
            n = (int)(k * at.Len);
            dr = zr; di = zi;
        }

        while (true)
        {
            LaRunStagesFx(t, up, dcR, dcI, mdc, maxIter, climb, ref dr, ref di, ref zr, ref zi, ref drv, ref div,
                ref n, ref stage, ref j, ref laSteps, ref cyc);
            if (cyc.Caught) { Interlocked.Increment(ref _deepPeriodicPixels); break; }

            int m = j;
            bool back = false;
            while (n < maxIter)
            {
                // #1889 (C5h) — the tail in double while δ is in double's range (it is |z| after
                // a rebase): the same steps, bit for bit (see TailInDouble). Back here at the
                // range's edge, on escape, on a rebase that climbs, or at the cap.
                if (bla == null && FxTailInDouble)
                {
                    var r = TailInDouble(dcR, dcI, maxIter, refLen, climb && top >= 0, ref dr, ref di, ref zr, ref zi,
                        ref drv, ref div, ref m, ref n, ref plainSteps, ref doubleSteps);
                    if (r == DoubleTailExit.Escaped) break;
                    if (r == DoubleTailExit.Climb) { back = true; break; }
                    if (n >= maxIter) break;
                }
                if (bla != null)
                {
                    int bi = bla.Lookup(m, FloatExp.Norm2(dr, di), Math.Min(refLen - 1, m + (maxIter - n)));
                    if (bi >= 0)
                    {
                        ref readonly var e = ref bla.Data[bi];
                        if (e.L >= 2)
                        {
                            var nDr = e.ARe * dr - e.AIm * di + (e.BRe * dcR - e.BIm * dcI);
                            var nDi = e.ARe * di + e.AIm * dr + (e.BRe * dcI + e.BIm * dcR);
                            dr = nDr; di = nDi;
                            var nDrv = e.ARe * drv - e.AIm * div + e.BRe;   // d' = A·d + B (#1782)
                            var nDiv = e.ARe * div + e.AIm * drv + e.BIm;
                            drv = nDrv; div = nDiv;
                            m += e.L; n += e.L;
                            blaSkips++;
                            continue;
                        }
                    }
                }

                double Zr = _refZr[m], Zi = _refZi[m];
                zr = dr + Zr; zi = di + Zi;
                double zrD = zr.ToDouble(), ziD = zi.ToDouble();
                if (zrD * zrD + ziD * ziD >= EscapeRadius2) break;

                bool rebase = FloatExp.AbsLess(FloatExp.Norm2(zr, zi), FloatExp.Norm2(dr, di)) || m + 1 >= refLen;
                if (rebase && climb && top >= 0)
                {
                    dr = zr; di = zi;
                    back = true;
                    break;
                }

                var nDrv2 = (zr * drv - zi * div).ScaleB(1) + 1.0;
                var nDiv2 = (zr * div + zi * drv).ScaleB(1);
                drv = nDrv2; div = nDiv2;
                if (rebase) { dr = zr; di = zi; Zr = 0; Zi = 0; m = 0; }
                FloatExp a = dr + 2.0 * Zr, b = di + 2.0 * Zi;
                var nDr2 = a * dr - b * di + dcR;
                var nDi2 = a * di + b * dr + dcI;
                dr = nDr2; di = nDi2;
                m++; n++;
                plainSteps++;
            }
            if (back) { stage = top; j = 0; continue; }
            break;
        }

        IterationBuffer[idx] = n;
        FillAuxAndColorHP(idx, n, maxIter, zr.ToDouble(), zi.ToDouble(), drv, div, colorMap);   // #1843: FloatExp derivative
    }

    private static FloatExp Cheb(FloatExp re, FloatExp im) => FloatExp.Max(re.Abs(), im.Abs());

    // As LaRunStages, in FloatExp.
    private static void LaRunStagesFx(LaStageTableFx t, int[]? up, FloatExp dcR, FloatExp dcI, FloatExp mdc, int maxIter, bool climb,
        ref FloatExp dr, ref FloatExp di, ref FloatExp zr, ref FloatExp zi, ref FloatExp drv, ref FloatExp div,
        ref int n, ref int stage, ref int j, ref long laSteps, ref LaCycleFx cyc)
    {
        var data = t.Data;
        int top = t.Stages - 1;
        while (true)
        {
            if (j == 0 && stage == top && LaCycleRepeatsFx(ref cyc, t, dr, di, stage, n, maxIter))
            { n = maxIter; stage = -1; return; }
            bool climbNow = false;
            for (; stage >= 0; stage--)
            {
                int s0 = t.StageStart[stage], last = t.StageCount[stage];
                if (!FloatExp.AbsLess(mdc, data[s0].ThrC)) { j = data[s0 + j].Next; continue; }
                while (true)
                {
                    ref readonly var s = ref data[s0 + j];
                    FloatExp ar = dr + 2.0 * s.RefRe, ai = di + 2.0 * s.RefIm;
                    FloatExp wr = dr * ar - di * ai, wi = dr * ai + di * ar;
                    if (!FloatExp.AbsLess(Cheb(wr, wi), s.Thr) || !FloatExp.AbsLess(mdc, s.ThrC) || s.Len > maxIter - n)
                    {
                        j = s.Next;
                        break;
                    }
                    FloatExp zsr = dr + s.RefRe, zsi = di + s.RefIm;
                    FloatExp tr = (zsr * drv - zsi * div).ScaleB(1), ti = (zsr * div + zsi * drv).ScaleB(1);
                    var nDrv = s.A.Re * tr - s.A.Im * ti + s.B.Re;
                    var nDiv = s.A.Re * ti + s.A.Im * tr + s.B.Im;
                    drv = nDrv; div = nDiv;
                    var nDr = s.A.Re * wr - s.A.Im * wi + (s.B.Re * dcR - s.B.Im * dcI);
                    var nDi = s.A.Re * wi + s.A.Im * wr + (s.B.Re * dcI + s.B.Im * dcR);
                    dr = nDr; di = nDi;
                    n += s.Len;
                    laSteps++;
                    j++;
                    ref readonly var nx = ref data[s0 + j];
                    zr = dr + nx.RefRe; zi = di + nx.RefIm;
                    if (j == last || FloatExp.AbsLess(Cheb(zr, zi), Cheb(dr, di)))
                    {
                        j = 0; dr = zr; di = zi;
                        if (climb && stage < top) { climbNow = true; break; }
                        if (LaCycleRepeatsFx(ref cyc, t, dr, di, stage, n, maxIter)) { n = maxIter; stage = -1; return; }
                    }
                    else if (up != null && up[s0 + j] >= 0) { j = up[s0 + j]; stage += 2; break; }
                }
                if (climbNow) break;
            }
            if (!climbNow) return;
            stage = top; j = 0;
        }
    }

    // As LaCycle / LaCycleRepeats (#1804), with bit-exact FloatExp repeats.
    internal struct LaCycleFx
    {
        public bool Enabled, Have, Caught;
        public FloatExp R, I;
        public int Stage, NSnap, Count, Next, Idx;
    }

    private static bool SameFx(FloatExp a, FloatExp b) => a.M == b.M && (a.M == 0 || a.E == b.E);

    private static bool LaCycleRepeatsFx(ref LaCycleFx c, LaStageTableFx t, FloatExp dr, FloatExp di, int stage, int n, int maxIter)
    {
        if (!c.Enabled) return false;
        if (c.Have && SameFx(dr, c.R) && SameFx(di, c.I) && stage == c.Stage
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

    // ── #1889 (epic #1428, C5h) — the float-exp tail in double ─────────────────────
    //
    // Measured (S1886's p250 minibrot at 1e286–1e287, 320×180): the FX rows cost ~7× the
    // double rows for the same steps, and the plain tail is ~93 % of them. A FloatExp
    // operation is one double operation on the mantissas plus exact power-of-two exponent
    // bookkeeping, so where every operand and result is a normal double it rounds exactly
    // as the double operation does. The tail's δ is |z| after a rebase (O(1)), so most
    // steps can run in double:
    //   • δ in double while ‖δ‖ ≥ 2^-500 (|δ|² stays normal for the rebase test) and
    //     ≤ 2^500;
    //   • dc in double when it is a normal double, or else while it is below δ's last bit
    //     (2^-62·‖δ‖), where FloatExp's add drops it as double's does;
    //   • dz/dc as dd·2^s (the exponent kept beside a double, as FloatExp; the +1 becomes
    //     +2^-s), renormalised by powers of two outside [2^-400, 2^400].
    // Outside those bounds the step runs in FloatExp (the caller's loop). FF's own; the
    // idea of an exponent kept beside a double is Kalles Fraktaler 2+'s floatexp
    // (bibliography #kf-floatexp), as FloatExp itself.

    /// <summary>#1889 — run the float-exp tail in double where it is exact (on by default;
    /// FF_FX_TAIL_DOUBLE=0 turns it off, for measuring and the identity tests).</summary>
    public static bool FxTailInDouble { get; set; } = Environment.GetEnvironmentVariable("FF_FX_TAIL_DOUBLE") != "0";

    private enum DoubleTailExit { Range, Escaped, Climb }

    private const double TailLo = 3.054936363499605e-151;   // 2^-500
    private const double TailHi = 3.273390607896142e150;    // 2^500
    private const double DerivLo = 3.872591914849318e-121;  // 2^-400
    private const double DerivHi = 2.5822498780869086e120;  // 2^400

    private DoubleTailExit TailInDouble(FloatExp dcR, FloatExp dcI, int maxIter, int refLen, bool climb,
        ref FloatExp dr, ref FloatExp di, ref FloatExp zr, ref FloatExp zi, ref FloatExp drv, ref FloatExp div,
        ref int m, ref int n, ref long plainSteps, ref long doubleSteps)
    {
        double dR = dr.ToDouble(), dI = di.ToDouble();
        double mag = Math.Max(Math.Abs(dR), Math.Abs(dI));
        if (!(mag >= TailLo && mag <= TailHi)) return DoubleTailExit.Range;
        // Each part of δ must be exactly its double (a part below double's range would
        // lose bits FloatExp keeps).
        if (!Exact(dr, dR) || !Exact(di, dI)) return DoubleTailExit.Range;
        double cR = dcR.ToDouble(), cI = dcI.ToDouble();
        bool dcExact = Exact(dcR, cR) && Exact(dcI, cI);
        // Where dc isn't a double, it must stay below δ's last bit: ‖dc‖·2^62 < ‖δ‖.
        double dcFloor = dcExact ? 0 : FloatExp.Max(dcR.Abs(), dcI.Abs()).ScaleB(62).ToDouble();
        if (!dcExact) { cR = 0; cI = 0; if (!(dcFloor < mag)) return DoubleTailExit.Range; }
        // dz/dc = (gr, gi)·2^s.
        long s = Math.Max(drv.IsZero ? long.MinValue : drv.E, div.IsZero ? long.MinValue : div.E);
        if (s == long.MinValue) return DoubleTailExit.Range;
        double gR = Scaled(drv, s), gI = Scaled(div, s);
        if (!Exact2(drv, gR, s) || !Exact2(div, gI, s)) return DoubleTailExit.Range;
        double one = OneAt(s);
        if (double.IsInfinity(one)) return DoubleTailExit.Range;
        double esc2 = EscapeRadius2;
        double zR = 0, zI = 0;
        bool stepped = false;
        var exit = DoubleTailExit.Range;
        var refZr = _refZr; var refZi = _refZi;
        while (n < maxIter)
        {
            double Zr = refZr[m], Zi = refZi[m];
            zR = dR + Zr; zI = dI + Zi;
            stepped = true;
            if (zR * zR + zI * zI >= esc2) { exit = DoubleTailExit.Escaped; break; }
            bool rebase = zR * zR + zI * zI < dR * dR + dI * dI || m + 1 >= refLen;
            if (rebase && climb) { dR = zR; dI = zI; exit = DoubleTailExit.Climb; Interlocked.Increment(ref _fxDoubleClimbs); break; }
            double nGr = 2.0 * (zR * gR - zI * gI) + one;   // (2·z·dz/dc + 1)·2^-s, as FloatExp's
            double nGi = 2.0 * (zR * gI + zI * gR);
            gR = nGr; gI = nGi;
            if (rebase) { dR = zR; dI = zI; Zr = 0; Zi = 0; m = 0; }
            double a = dR + 2.0 * Zr, b = dI + 2.0 * Zi;
            double nDr = a * dR - b * dI + cR;
            double nDi = a * dI + b * dR + cI;
            dR = nDr; dI = nDi;
            m++; n++;
            plainSteps++; doubleSteps++;
            // The derivative back into [2^-400, 2^400] (exact powers of two).
            double gm = Math.Max(Math.Abs(gR), Math.Abs(gI));
            if (gm > DerivHi || (gm < DerivLo && gm != 0))
            {
                Interlocked.Increment(ref _fxDoubleRenorms);
                int k = Math.ILogB(gm);
                gR = Math.ScaleB(gR, -k); gI = Math.ScaleB(gI, -k); s += k;
                one = OneAt(s);
                if (double.IsInfinity(one)) break;
            }
            mag = Math.Max(Math.Abs(dR), Math.Abs(dI));
            if (!(mag >= TailLo && mag <= TailHi) || (!dcExact && !(dcFloor < mag)))
            {
                // Leaving the range: the step just taken may have rounded where FloatExp
                // would not only if a result left double's normal range, which 2^±500 of
                // headroom excludes. Hand back in FloatExp.
                break;
            }
        }
        dr = FloatExp.FromDouble(dR); di = FloatExp.FromDouble(dI);
        if (stepped) { zr = FloatExp.FromDouble(zR); zi = FloatExp.FromDouble(zI); }
        drv = FloatExp.FromDouble(gR).ScaleB(s); div = FloatExp.FromDouble(gI).ScaleB(s);
        return exit;

        static bool Exact(FloatExp v, double d) => v.IsZero ? d == 0 : FloatExp.FromDouble(d).E == v.E && FloatExp.FromDouble(d).M == v.M;
        static bool Exact2(FloatExp v, double d, long sc) => v.IsZero ? d == 0 : FloatExp.FromDouble(d).ScaleB(sc) is var f && f.E == v.E && f.M == v.M;
        static double Scaled(FloatExp v, long sc) => v.IsZero ? 0 : (v.E - sc < -1074 ? 0 : Math.ScaleB(v.M, (int)(v.E - sc)));
        static double OneAt(long sc) => sc > 1074 ? 0 : sc < -1023 ? double.PositiveInfinity : Math.ScaleB(1.0, (int)-sc);
    }
}
