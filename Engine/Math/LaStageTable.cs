// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown
// SPDX-FileCopyrightText: Yu, Zhuoran (GitHub 5E-324), Imagina <https://github.com/5E-324/Imagina>, AGPL-3.0

// Math/LaStageTable.cs
//
// #1625 (epic #1623, S2) — period-synchronised linear approximation: the stage
// table. DERIVED CODE: Stage0 / NextStage are a C# translation of Imagina's
// CreateLAFromOrbit / CreateNewLAStage, and La.Step / Compose / DetectPeriod of its
// LAInfo::Step / Composite / DetectPeriod (Imagina by GitHub user 5E-324, profile
// name "Yu, Zhuoran", https://github.com/5E-324/Imagina, HInfLAEvaluator.{h,cpp} at
// commit cd046377, AGPL-3.0; see THIRD-PARTY-NOTICES.md §3 and
// Docs/Resources-Bibliography.md#imagina). The maths, derived from the perturbation
// recurrence, is in Docs/Technical/Period-Synchronised-LA-DesignPlan.md §2; §2.7 maps
// each function here to its Imagina counterpart.
//
// A step anchored at reference index s, length L:
//      w = δ_s·(2·Z_s + δ_s)                 (the first iteration, exact)
//      δ_{s+L} ≈ A·w + B·δc                  (the other L − 1, linearised)
// valid while ‖w‖ < Thr and ‖δc‖ < ThrC (Chebyshev norms). Unlike a BLA run, a step
// may START at a point where Z_s is near zero, so the near-zero points of the
// minibrot chain are where steps begin rather than walls no skip crosses.
//
// Stage 0 steps run over the reference, cut where the threshold dips (Z near zero)
// or at a length cap; stage k + 1 composes stage-k steps the same way, so each stage
// spans the next ancestor period. Each step records where it starts one stage down
// (stage 0: the reference index), so a pixel that cannot take a step drops a stage.
//
// FF departs from Imagina here in two places (design doc §2.6): the period test is
// the threshold dip (Imagina's #else branch; its build defines
// PERIOD_DETECTION_METHOD2, a minimum-|Z| test), and a composite's δc threshold
// also keeps the second step's own ThrC.
//
// The thresholds do not depend on the view's δc radius (only the AT, S4, will), so
// the table is keyed on the reference orbit and the tolerance alone.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;

namespace FracturingFog.FFMath
{
    /// <summary>One LA step: δ_{s+L} ≈ A·w + B·δc with w = δ_s(2·Ref + δ_s), valid while
    /// ‖w‖ &lt; <see cref="Thr"/> and ‖δc‖ &lt; <see cref="ThrC"/>.</summary>
    public readonly struct LaStep
    {
        /// <summary>Z_s, the reference value the step is anchored at.</summary>
        public readonly double RefRe, RefIm;
        public readonly double ARe, AIm, BRe, BIm;
        public readonly double Thr, ThrC;
        /// <summary>Iterations the step covers.</summary>
        public readonly int Len;
        /// <summary>Where the step starts one stage down: the index of its first
        /// stage-(k − 1) step, or for stage 0 the reference index s.</summary>
        public readonly int Next;

        public LaStep(Complex refZ, Complex a, Complex b, double thr, double thrC, int len, int next)
        {
            RefRe = refZ.Real; RefIm = refZ.Imaginary;
            ARe = a.Real; AIm = a.Imaginary; BRe = b.Real; BIm = b.Imaginary;
            Thr = thr; ThrC = thrC; Len = len; Next = next;
        }

        public Complex Ref => new(RefRe, RefIm);
        public Complex A => new(ARe, AIm);
        public Complex B => new(BRe, BIm);
    }

    /// <summary>
    /// #1625 — the LA stages over one reference orbit (design doc §2.3). Stage k's
    /// steps are <c>Data[StageStart[k] .. StageStart[k] + StageCount[k] − 1]</c>,
    /// followed by a sentinel anchored at Z_R (so step j's end value is the anchor of
    /// step j + 1). Every stage covers the reference exactly: its lengths sum to R.
    /// </summary>
    public sealed class LaStageTable
    {
        /// <summary>Validity tolerance τ at the default BLA tolerance (Imagina's 2⁻²⁴).</summary>
        public const double DefaultTolerance = 1.0 / (1 << 24);
        /// <summary>The dip that marks a period: a threshold falling below 2⁻¹⁰ of the
        /// one before (Imagina's PeriodDetectionThreshold, used by its non-default
        /// period test; design doc §2.6, item 7).</summary>
        public const double PeriodDip = 1.0 / (1 << 10);
        /// <summary>Stages are capped as in Imagina.</summary>
        public const int MaxStages = 256;

        public readonly LaStep[] Data;
        public readonly int[] StageStart;
        public readonly int[] StageCount;
        /// <summary>The reference length the stages cover (Z_0 .. Z_R).</summary>
        public readonly int RefLen;
        public readonly double Tolerance;
        public readonly double BuildMs;

        public int Stages => StageStart.Length;
        public long Bytes => (long)Data.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<LaStep>();

        /// <summary>The steps of stage <paramref name="k"/>, without its sentinel.</summary>
        public ReadOnlySpan<LaStep> Stage(int k) => Data.AsSpan(StageStart[k], StageCount[k]);

        /// <summary>Stage <paramref name="k"/>'s step <paramref name="j"/>; j = StageCount[k]
        /// is the sentinel at Z_R.</summary>
        public ref readonly LaStep Step(int k, int j) => ref Data[StageStart[k] + j];

        /// <summary>τ for a BLA tolerance ε, proportional, so the Fast…Exact presets keep
        /// their meaning: ε = 1e-6 (the default) gives 2⁻²⁴; clamped to [2⁻⁵², 2⁻⁸].</summary>
        public static double ToleranceFor(double blaEpsilon)
        {
            if (!(blaEpsilon > 0) || !double.IsFinite(blaEpsilon)) return DefaultTolerance;
            double t = DefaultTolerance * blaEpsilon / BlaTable.DefaultEpsilon;
            return Math.Clamp(t, Math.Pow(2, -52), Math.Pow(2, -8));
        }

        private LaStageTable(List<LaStep> data, List<int> start, List<int> count, int refLen, double tol, double ms)
        {
            Data = data.ToArray(); StageStart = start.ToArray(); StageCount = count.ToArray();
            RefLen = refLen; Tolerance = tol; BuildMs = ms;
        }

        /// <summary>Builds the stages over Z_0 .. Z_R (<paramref name="refZr"/> /
        /// <paramref name="refZi"/>, at least R + 1 entries). Null when R &lt; 2.</summary>
        public static LaStageTable? Build(double[] refZr, double[] refZi, int refLen, double tolerance = DefaultTolerance)
        {
            if (refLen < 2 || refZr.Length <= refLen || refZi.Length <= refLen) return null;
            var sw = Stopwatch.StartNew();
            var b = new Builder(refZr, refZi, refLen, tolerance);
            var stage = new List<La>();
            bool more = b.Stage0(stage);
            var stages = new List<List<La>> { stage };
            while (more && stages.Count < MaxStages)
            {
                var next = new List<La>();
                more = b.NextStage(stages[^1], next);
                stages.Add(next);
            }
            var data = new List<LaStep>();
            var start = new List<int>();
            var count = new List<int>();
            foreach (var st in stages)
            {
                start.Add(data.Count);
                count.Add(st.Count - 1);
                foreach (var la in st) data.Add(new LaStep(la.Ref, la.A, la.B, la.Thr, la.ThrC, la.Len, la.Next));
            }
            return new LaStageTable(data, start, count, refLen, tolerance, sw.Elapsed.TotalMilliseconds);
        }

        private static double Cheb(Complex z) => Math.Max(Math.Abs(z.Real), Math.Abs(z.Imaginary));

        // The step while it is being built (design doc §2.2–2.3).
        private struct La
        {
            public Complex Ref, A, B;
            public double Thr, ThrC;
            public int Len, Next;

            public static La Start(Complex z) => new() { Ref = z, A = 1, B = 1, Thr = 1, ThrC = 1, Len = 1 };

            // One more iteration at reference value z; true = the threshold dipped.
            public readonly bool Step(Complex z, double tol, out La o)
            {
                double mz = Cheb(z);
                o = this;
                o.Thr = Math.Min(Thr, mz / Cheb(A) * tol);
                o.ThrC = Math.Min(ThrC, mz / Cheb(B) * tol);
                o.A = 2 * z * A;
                o.B = 2 * z * B + 1;
                o.Len = Len + 1;
                return o.Thr < Thr * PeriodDip;
            }

            // This step, then b; true = a dip at b's anchor.
            public readonly bool Compose(in La b, double tol, out La o)
            {
                Complex z = b.Ref;
                double mz = Cheb(z);
                o = this;
                double t = Math.Min(Thr, mz / Cheb(A) * tol);
                double tc = Math.Min(ThrC, mz / Cheb(B) * tol);
                Complex a2 = 2 * z * A, b2 = 2 * z * B;
                o.Thr = Math.Min(t, b.Thr / Cheb(a2));
                // FF (design doc §2.6, item 1): b's own δc bound holds too.
                o.ThrC = Math.Min(Math.Min(tc, b.Thr / Cheb(b2)), b.ThrC);
                o.A = a2 * b.A;
                o.B = b2 * b.A + b.B;
                o.Len = Len + b.Len;
                return t < Thr * PeriodDip;
            }

            public readonly bool DetectPeriod(Complex z, double tol) => Cheb(z) / Cheb(A) * tol < Thr * PeriodDip;
        }

        private sealed class Builder(double[] zr, double[] zi, int r, double tol)
        {
            private Complex Z(int i) => new(zr[i], zi[i]);

            // round(x^(1/round(log16 x))): the fixed step length when the first period
            // is too long (> 64) or missing.
            private static int FixedLen(double ratio) =>
                (int)Math.Round(Math.Pow(ratio, 1.0 / Math.Max(1.0, Math.Round(Math.Log2(ratio) / 4))));

            private La With(La la, int len, int next) { la.Len = len; la.Next = next; return la; }

            public bool Stage0(List<La> st)
            {
                int period = 0, i, next = 0;
                La.Start(Z(0)).Step(Z(1), tol, out var la);
                for (i = 2; i < r; i++)
                {
                    if (la.Step(Z(i), tol, out var nl))
                    {
                        period = i;
                        st.Add(With(la, period, 0));
                        next = i;
                        if (i + 1 < r) { La.Start(Z(i)).Step(Z(i + 1), tol, out la); i += 2; }
                        else { la = La.Start(Z(i)); i += 1; }
                        break;
                    }
                    la = nl;
                }
                int begin = period;
                if (period == 0)
                {
                    if (r <= 64)
                    {
                        st.Add(With(la, r, 0));
                        st.Add(La.Start(Z(r)));
                        return false;
                    }
                    La.Start(Z(0)).Step(Z(1), tol, out la); next = 0; i = 2;
                    period = FixedLen(r); begin = 0;
                }
                else if (period > 64)
                {
                    st.RemoveAt(st.Count - 1);
                    La.Start(Z(0)).Step(Z(1), tol, out la); next = 0; i = 2;
                    period = FixedLen(period); begin = 0;
                }
                int end = begin + period;
                for (; i < r; i++)
                {
                    bool dip = la.Step(Z(i), tol, out var nl);
                    if (dip || i >= end)
                    {
                        st.Add(With(la, i - begin, next));
                        next = i; begin = i; end = begin + period;
                        if (nl.DetectPeriod(Z(i + 1), tol) || i + 1 >= r) la = La.Start(Z(i));
                        else { La.Start(Z(i)).Step(Z(i + 1), tol, out la); i++; }
                    }
                    else la = nl;
                }
                st.Add(With(la, i - begin, next));
                st.Add(La.Start(Z(r)));
                return true;
            }

            public bool NextStage(List<La> prev, List<La> st)
            {
                int m = prev.Count - 1;   // without the sentinel
                int period = 0, i, j, next = 0;
                prev[0].Compose(prev[1], tol, out var la);
                i = prev[0].Len + prev[1].Len;
                for (j = 2; j < m; j++)
                {
                    if (la.Compose(prev[j], tol, out var nl))
                    {
                        if (prev[j].Thr == 0.0) break;
                        period = i;
                        st.Add(With(la, period, 0));
                        next = j;
                        if (nl.DetectPeriod(prev[j + 1].Ref, tol) || j + 1 >= m) { la = prev[j]; i += prev[j].Len; j++; }
                        else { prev[j].Compose(prev[j + 1], tol, out la); i += prev[j].Len + prev[j + 1].Len; j += 2; }
                        break;
                    }
                    la = nl; i += prev[j].Len;
                }
                int len0 = prev[0].Len, begin = period;
                if (period == 0)
                {
                    if (r / len0 <= 64)
                    {
                        st.Add(With(la, r, 0));
                        st.Add(La.Start(Z(r)));
                        return false;
                    }
                    prev[0].Compose(prev[1], tol, out la); i = len0 + prev[1].Len; next = 0; j = 2;
                    period = len0 * FixedLen((double)r / len0); begin = 0;
                }
                else if (period > len0 * 64)
                {
                    st.RemoveAt(st.Count - 1);
                    prev[0].Compose(prev[1], tol, out la); i = len0 + prev[1].Len; next = 0; j = 2;
                    period = len0 * FixedLen((double)period / len0); begin = 0;
                }
                int end = begin + period;
                for (; j < m; j++)
                {
                    bool dip = la.Compose(prev[j], tol, out var nl);
                    if (dip || i >= end)
                    {
                        st.Add(With(la, i - begin, next));
                        next = j; begin = i; end = begin + period;
                        if (nl.DetectPeriod(prev[j + 1].Ref, tol) || j + 1 >= m) la = prev[j];
                        else { prev[j].Compose(prev[j + 1], tol, out la); i += prev[j].Len; j++; }
                    }
                    else la = nl;
                    i += prev[j].Len;
                }
                st.Add(With(la, i - begin, next));
                st.Add(La.Start(Z(r)));
                return true;
            }
        }
    }
}
