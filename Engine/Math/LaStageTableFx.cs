// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Math/LaStageTableFx.cs
//
// #1847 (epic #1428, C5c) — the LA stage table (LaStageTable.cs: Imagina's linear
// approximation stages, Zhuoran; see its header and Docs/Resources-Bibliography.md
// #imagina) with A, B, Thr and ThrC in FloatExp, for the floatexp pixel loop past
// double's range. There the double table can't hold its numbers: A and B (∏2Z and the
// dc response over a step) overflow, Thr and ThrC (the δ and δc validity radii) underflow.
//
// A line-for-line port of LaStageTable's La struct and Builder: the same stage 0 /
// NextStage walk, period tests and fixed-length stages, the arithmetic in FloatExp. Where
// double holds every number both build the same stages (S1847 checks it).

using System;
using System.Collections.Generic;

namespace FracturingFog.FFMath
{
    // CFx (complex FloatExp) lives in Abstractions/Math/CFx.cs since #1866.

    /// <summary>#1847 — one LA step (as <see cref="LaStep"/>) with FloatExp coefficients.</summary>
    public readonly struct LaStepFx
    {
        public readonly double RefRe, RefIm;
        public readonly CFx A, B;
        public readonly FloatExp Thr, ThrC;
        public readonly int Len, Next;
        public LaStepFx(double refRe, double refIm, CFx a, CFx b, FloatExp thr, FloatExp thrC, int len, int next)
        {
            RefRe = refRe; RefIm = refIm; A = a; B = b; Thr = thr; ThrC = thrC; Len = len; Next = next;
        }
    }

    /// <summary>#1847 — <see cref="LaStageTable"/> in FloatExp.</summary>
    public sealed class LaStageTableFx
    {
        public readonly LaStepFx[] Data;
        public readonly int[] StageStart, StageCount;
        public readonly int RefLen;
        public readonly double Tolerance;
        public readonly LaPeriodTest PeriodTest;
        public readonly int StageBits;
        public readonly bool Finite;
        public int Stages => StageStart.Length;

        private int _maxLen = -1;
        public int MaxLen
        {
            get
            {
                if (_maxLen < 0) { int mx = 0; foreach (var s in Data) if (s.Len > mx) mx = s.Len; _maxLen = mx; }
                return _maxLen;
            }
        }

        private int[]? _up;
        /// <summary>As <see cref="LaStageTable.Up"/>.</summary>
        public int[] Up => _up ??= BuildUp();

        private int[] BuildUp()
        {
            var up = new int[Data.Length];
            Array.Fill(up, -1);
            for (int k = 0; k + 1 < Stages; k++)
            {
                int s1 = StageStart[k + 1], c0 = StageCount[k];
                for (int i = 0; i < StageCount[k + 1]; i++)
                {
                    int nx = Data[s1 + i].Next;
                    if ((uint)nx < (uint)c0) up[StageStart[k] + nx] = i;
                }
            }
            return up;
        }

        private LaStageTableFx(List<LaStepFx> data, List<int> start, List<int> count, int refLen, double tol, LaPeriodTest test, int bits)
        {
            Data = data.ToArray(); StageStart = start.ToArray(); StageCount = count.ToArray();
            RefLen = refLen; Tolerance = tol; PeriodTest = test; StageBits = bits;
            bool ok = true;
            foreach (ref readonly var d in Data.AsSpan())
                if (!(double.IsFinite(d.RefRe) && double.IsFinite(d.RefIm) && d.A.IsFinite && d.B.IsFinite
                      && !double.IsNaN(d.Thr.M) && !double.IsNaN(d.ThrC.M)))
                { ok = false; break; }
            Finite = ok;
        }

        /// <summary>As <see cref="LaStageTable.Build"/>.</summary>
        public static LaStageTableFx? Build(double[] refZr, double[] refZi, int refLen, double tolerance = LaStageTable.DefaultTolerance,
                                            LaPeriodTest periodTest = LaPeriodTest.ThresholdDip, int stageBits = LaStageTable.DefaultStageBits)
        {
            if (refLen < 2 || refZr.Length <= refLen || refZi.Length <= refLen) return null;
            stageBits = Math.Clamp(stageBits, 1, LaStageTable.ImaginaStageBits);
            var b = new Builder(refZr, refZi, refLen, FloatExp.FromDouble(tolerance), periodTest == LaPeriodTest.MinMagnitude, stageBits);
            var stage = new List<La>();
            bool more = b.Stage0(stage);
            var stages = new List<List<La>> { stage };
            while (more && stages.Count < LaStageTable.MaxStages)
            {
                var next = new List<La>();
                more = b.NextStage(stages[^1], next);
                stages.Add(next);
            }
            var data = new List<LaStepFx>();
            var start = new List<int>();
            var count = new List<int>();
            foreach (var st in stages)
            {
                start.Add(data.Count);
                count.Add(st.Count - 1);
                foreach (var la in st) data.Add(new LaStepFx(la.RefRe, la.RefIm, la.A, la.B, la.Thr, la.ThrC, la.Len, la.Next));
            }
            return new LaStageTableFx(data, start, count, refLen, tolerance, periodTest, stageBits);
        }

        private uint[]? _gpuStepsFx, _gpuInts;

        /// <summary>#1880 (epic #1428, C6a) — the steps for the float-exp GPU LA kernel, in
        /// <see cref="LaStageTable.GpuStepsFloatExp"/>'s layout (12 words each), from the
        /// FloatExp coefficients: past where the double table overflows.</summary>
        public uint[] GpuStepsFloatExp => _gpuStepsFx ??= BuildGpuStepsFloatExp();

        /// <summary>#1880 — as <see cref="LaStageTable.GpuInts"/>.</summary>
        public uint[] GpuInts => _gpuInts ??= BuildGpuInts();

        private uint[] BuildGpuStepsFloatExp()
        {
            var a = new uint[Data.Length * 12];
            static uint F(float f) => BitConverter.SingleToUInt32Bits(f);
            for (int i = 0; i < Data.Length; i++)
            {
                ref readonly var d = ref Data[i];
                int o = i * 12;
                a[o] = F((float)d.RefRe); a[o + 1] = F((float)d.RefIm);
                FracturingFog.Rendering.GpuFloatExp.Encode(d.A, out float ar, out float ai, out int ae);
                a[o + 2] = F(ar); a[o + 3] = F(ai); a[o + 4] = (uint)ae;
                FracturingFog.Rendering.GpuFloatExp.Encode(d.B, out float br, out float bi, out int be);
                a[o + 5] = F(br); a[o + 6] = F(bi); a[o + 7] = (uint)be;
                FracturingFog.Rendering.GpuFloatExp.Encode(d.Thr, out float tm, out int te);
                a[o + 8] = F(tm); a[o + 9] = (uint)te;
                FracturingFog.Rendering.GpuFloatExp.Encode(d.ThrC, out float cm, out int ce);
                a[o + 10] = F(cm); a[o + 11] = (uint)ce;
            }
            return a;
        }

        private uint[] BuildGpuInts()
        {
            int s = Stages;
            var a = new uint[2 * s + 2 * Data.Length];
            for (int k = 0; k < s; k++) { a[2 * k] = (uint)StageStart[k]; a[2 * k + 1] = (uint)StageCount[k]; }
            for (int i = 0; i < Data.Length; i++) { a[2 * s + 2 * i] = (uint)Data[i].Len; a[2 * s + 2 * i + 1] = (uint)Data[i].Next; }
            return a;
        }

        private static readonly FloatExp PeriodDip = FloatExp.FromDouble(LaStageTable.PeriodDip);

        private static double Cheb(double re, double im) => Math.Max(Math.Abs(re), Math.Abs(im));

        // b / c, +∞ (a value no comparison reaches) for c = 0, as double's x / 0.
        private static FloatExp Over(FloatExp b, FloatExp c) => c.IsZero ? FloatExp.Huge : b / c;

        private struct La
        {
            public double RefRe, RefIm;
            public CFx A, B;
            public FloatExp Thr, ThrC;
            public int Len, Next;
            public double MinMag;

            public static La Start(double zr, double zi) => new()
            { RefRe = zr, RefIm = zi, A = CFx.One, B = CFx.One, Thr = FloatExp.One, ThrC = FloatExp.One, Len = 1, MinMag = 4 };

            public readonly bool Step(double zr, double zi, FloatExp tol, bool minMag, out La o)
            {
                double mz = Cheb(zr, zi);
                var fmz = FloatExp.FromDouble(mz);
                var z2 = CFx.Of(2 * zr, 2 * zi);
                o = this;
                o.MinMag = Math.Min(mz, MinMag);
                o.Thr = FloatExp.Min(Thr, Over(fmz, A.Cheb()) * tol);
                o.ThrC = FloatExp.Min(ThrC, Over(fmz, B.Cheb()) * tol);
                o.A = z2 * A;
                o.B = (z2 * B).PlusOne();
                o.Len = Len + 1;
                return minMag ? o.MinMag < MinMag * LaStageTable.MinMagDipStage0 : FloatExp.AbsLess(o.Thr, Thr * PeriodDip);
            }

            public readonly bool Compose(in La b, FloatExp tol, bool minMag, out La o)
            {
                double mz = Cheb(b.RefRe, b.RefIm);
                var fmz = FloatExp.FromDouble(mz);
                var z2 = CFx.Of(2 * b.RefRe, 2 * b.RefIm);
                o = this;
                var t = FloatExp.Min(Thr, Over(fmz, A.Cheb()) * tol);
                var tc = FloatExp.Min(ThrC, Over(fmz, B.Cheb()) * tol);
                CFx a2 = z2 * A, b2 = z2 * B;
                o.Thr = FloatExp.Min(t, Over(b.Thr, a2.Cheb()));
                o.ThrC = FloatExp.Min(FloatExp.Min(tc, Over(b.Thr, b2.Cheb())), b.ThrC);
                o.A = a2 * b.A;
                o.B = b2 * b.A + b.B;
                o.Len = Len + b.Len;
                double mm = Math.Min(mz, MinMag);
                o.MinMag = Math.Min(mm, b.MinMag);
                return minMag ? mm < MinMag * LaStageTable.MinMagDip : FloatExp.AbsLess(t, Thr * PeriodDip);
            }

            public readonly bool DetectPeriod(double zr, double zi, FloatExp tol, bool minMag) =>
                minMag ? Cheb(zr, zi) < MinMag * LaStageTable.MinMagDip
                       : FloatExp.AbsLess(Over(FloatExp.FromDouble(Cheb(zr, zi)), A.Cheb()) * tol, Thr * PeriodDip);
        }

        private sealed class Builder(double[] zr, double[] zi, int r, FloatExp tol, bool mm, int stageBits)
        {
            private La Start(int i) => La.Start(zr[i], zi[i]);

            private static int FixedLen(double ratio, int bits = LaStageTable.ImaginaStageBits) =>
                (int)Math.Round(Math.Pow(ratio, 1.0 / Math.Max(1.0, Math.Round(Math.Log2(ratio) / bits))));

            private static La With(La la, int len, int next) { la.Len = len; la.Next = next; return la; }

            private bool Step(in La la, int i, out La o) => la.Step(zr[i], zi[i], tol, mm, out o);

            public bool Stage0(List<La> st)
            {
                int period = 0, i, next = 0;
                Step(Start(0), 1, out var la);
                for (i = 2; i < r; i++)
                {
                    if (Step(la, i, out var nl))
                    {
                        period = i;
                        st.Add(With(la, period, 0));
                        next = i;
                        if (i + 1 < r) { Step(Start(i), i + 1, out la); i += 2; }
                        else { la = Start(i); i += 1; }
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
                        st.Add(Start(r));
                        return false;
                    }
                    Step(Start(0), 1, out la); next = 0; i = 2;
                    period = FixedLen(r); begin = 0;
                }
                else if (period > 64)
                {
                    st.RemoveAt(st.Count - 1);
                    Step(Start(0), 1, out la); next = 0; i = 2;
                    period = FixedLen(period); begin = 0;
                }
                int end = begin + period;
                for (; i < r; i++)
                {
                    bool dip = Step(la, i, out var nl);
                    if (dip || i >= end)
                    {
                        st.Add(With(la, i - begin, next));
                        next = i; begin = i; end = begin + period;
                        if (nl.DetectPeriod(zr[i + 1], zi[i + 1], tol, mm) || i + 1 >= r) la = Start(i);
                        else { Step(Start(i), i + 1, out la); i++; }
                    }
                    else la = nl;
                }
                st.Add(With(la, i - begin, next));
                st.Add(Start(r));
                return true;
            }

            public bool NextStage(List<La> prev, List<La> st)
            {
                int m = prev.Count - 1;
                int period = 0, i, j, next = 0;
                prev[0].Compose(prev[1], tol, mm, out var la);
                i = prev[0].Len + prev[1].Len;
                for (j = 2; j < m; j++)
                {
                    if (la.Compose(prev[j], tol, mm, out var nl))
                    {
                        if (prev[j].Thr.IsZero) break;
                        period = i;
                        st.Add(With(la, period, 0));
                        next = j;
                        if (nl.DetectPeriod(prev[j + 1].RefRe, prev[j + 1].RefIm, tol, mm) || j + 1 >= m) { la = prev[j]; i += prev[j].Len; j++; }
                        else { prev[j].Compose(prev[j + 1], tol, mm, out la); i += prev[j].Len + prev[j + 1].Len; j += 2; }
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
                        st.Add(Start(r));
                        return false;
                    }
                    prev[0].Compose(prev[1], tol, mm, out la); i = len0 + prev[1].Len; next = 0; j = 2;
                    period = len0 * FixedLen((double)r / len0, stageBits); begin = 0;
                }
                else if (period > len0 * 64)
                {
                    st.RemoveAt(st.Count - 1);
                    prev[0].Compose(prev[1], tol, mm, out la); i = len0 + prev[1].Len; next = 0; j = 2;
                    period = len0 * FixedLen((double)period / len0, stageBits); begin = 0;
                }
                int end = begin + period;
                for (; j < m; j++)
                {
                    bool dip = la.Compose(prev[j], tol, mm, out var nl);
                    if (dip || i >= end)
                    {
                        st.Add(With(la, i - begin, next));
                        next = j; begin = i; end = begin + period;
                        if (nl.DetectPeriod(prev[j + 1].RefRe, prev[j + 1].RefIm, tol, mm) || j + 1 >= m) la = prev[j];
                        else { prev[j].Compose(prev[j + 1], tol, mm, out la); i += prev[j].Len; j++; }
                    }
                    else la = nl;
                    i += prev[j].Len;
                }
                st.Add(With(la, i - begin, next));
                st.Add(Start(r));
                return true;
            }
        }
    }
}
