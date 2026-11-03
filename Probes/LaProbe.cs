// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1624 (epic #1623): --laprobe [W H]. Proof of concept of period-synchronised linear
// approximation ("LA stages" + the approximation transform, AT) on the bench-kit
// minibrot chain p953 -> p15,248 -> p121,984 -> p951,094 (fractadyne bench-kit,
// WindySnowOwl, https://github.com/WindySnowOwl/fractadyne/tree/main/bench-kit, from
// rith's FF 0.7.1 benchmark report; scenes shared with DeepCostProbe). Not wired
// into the calculator.
//
// The method is Imagina's HInfLAEvaluator (GitHub user 5E-324,
// https://github.com/5E-324/Imagina, AGPL-3.0, HInfLAEvaluator.{h,cpp} at
// cd046377, read 2026-11-03), re-implemented here from the maths written out in
// Docs/Technical/Period-Synchronised-LA-DesignPlan.md §2; see the bibliography
// (#imagina). The places this file departs from Imagina are marked "FF:" and listed
// in §2.6 of the doc.
//
// Per scene: a reference orbit in fixed-point BigInteger, stopped at the period
// (|Z_p| under one pixel, as #1614); the LA stages built over it; one W x H frame
// evaluated with LA (scalar double, Parallel.For over rows) and with FF's
// current deep path (MandelbrotCalculator, SA + BLA + rebasing); and
// FF_LAPROBE_SAMPLES pixels (default 12) iterated exactly (fixed-point BigInteger,
// no perturbation, FF_LAPROBE_BITS bits, default 320). Reports the stages, steps
// per pixel (AT / LA / plain), agreement of LA and of FF's frame with exact
// iteration, and LA vs FF on the whole frame. FF_LAPROBE_SCENE filters by name;
// FF_LAPROBE_CLIMB=0 keeps a pixel in the stage it fell to after a rebase
// (Imagina's behaviour) instead of climbing back to the top stage. Exit 0 always.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace FracturingFog
{
    internal static class LaProbe
    {
        // Tolerances (§2.2): validity scale 2^-24 and the dip that marks a period 2^-10,
        // Imagina's LAThresholdScale / PeriodDetectionThreshold.
        private const double Tol = 1.0 / (1 << 24);
        private const double Dip = 1.0 / (1 << 10);
        private const double Bailout2 = 512.0 * 512.0;   // FF's escape radius (MandelbrotCalculator)

        private static double Cheb(Complex z) => Math.Max(Math.Abs(z.Real), Math.Abs(z.Imaginary));
        private static double Norm(Complex z) => z.Real * z.Real + z.Imaginary * z.Imaginary;

        /// <summary>One LA step anchored at reference value <see cref="Ref"/> (index s):
        /// δ_{s+L} ≈ Z·w + C·δc with w = δ_s(2·Ref + δ_s), valid while |w| &lt; Thr and
        /// |δc| &lt; ThrC (Chebyshev norms). Len = L; Next = where the step begins one
        /// stage down (stage 0: the reference index s).</summary>
        private struct La
        {
            public Complex Ref, Z, C;
            public double Thr, ThrC;
            public int Len, Next;

            public static La Start(Complex z) => new() { Ref = z, Z = 1, C = 1, Thr = 1, ThrC = 1, Len = 1 };

            /// <summary>Extend by one iteration at reference value z; true = the threshold
            /// dipped (z is near zero: a period boundary).</summary>
            public readonly bool Step(Complex z, out La o)
            {
                double mz = Cheb(z);
                o = this;
                o.Thr = Math.Min(Thr, mz / Cheb(Z) * Tol);
                o.ThrC = Math.Min(ThrC, mz / Cheb(C) * Tol);
                o.Z = 2 * z * Z;
                o.C = 2 * z * C + 1;
                o.Len = Len + 1;
                return o.Thr < Thr * Dip;
            }

            /// <summary>This step followed by <paramref name="b"/>; true = dip at b's anchor.</summary>
            public readonly bool Compose(in La b, out La o)
            {
                Complex z = b.Ref;
                double mz = Cheb(z);
                o = this;
                double t = Math.Min(Thr, mz / Cheb(Z) * Tol);
                double tc = Math.Min(ThrC, mz / Cheb(C) * Tol);
                Complex z2 = 2 * z * Z, c2 = 2 * z * C;
                o.Thr = Math.Min(t, b.Thr / Cheb(z2));
                // FF: b's own δc threshold also bounds the composite (Imagina keeps only b.Thr).
                o.ThrC = Math.Min(Math.Min(tc, b.Thr / Cheb(c2)), b.ThrC);
                o.Z = z2 * b.Z;
                o.C = c2 * b.Z + b.C;
                o.Len = Len + b.Len;
                return t < Thr * Dip;
            }

            public readonly bool DetectPeriod(Complex z) => Cheb(z) / Cheb(Z) * Tol < Thr * Dip;
        }

        private sealed class Stage
        {
            public readonly List<La> Steps = new();   // plus a sentinel anchored at Z_R
        }

        private struct At
        {
            public int Len;
            public double ThrC, Esc2;
            public Complex RefC, C, InvZ;
        }

        private sealed class Table
        {
            public Complex[] Ref = [];       // Z_0 .. Z_R
            public int R;                    // reference length (the period, or the cap)
            public readonly List<Stage> Stages = new();
            public At AT;
            public bool UseAT;
        }

        // ── Stage construction (§2.3) ───────────────────────────────────────────────

        private static int FixedLen(double ratio) =>
            (int)Math.Round(Math.Pow(ratio, 1.0 / Math.Max(1.0, Math.Round(Math.Log2(ratio) / 4))));

        private static bool BuildStage0(Table t)
        {
            var z = t.Ref; int R = t.R;
            var st = new Stage(); t.Stages.Add(st);
            int period = 0, i, next = 0;
            La.Start(z[0]).Step(z[1], out var la);
            for (i = 2; i < R; i++)
            {
                if (la.Step(z[i], out var nl))
                {
                    period = i;
                    la.Len = period; la.Next = 0; st.Steps.Add(la);
                    next = i;
                    if (i + 1 < R) { La.Start(z[i]).Step(z[i + 1], out la); i += 2; }
                    else { la = La.Start(z[i]); i += 1; }
                    break;
                }
                la = nl;
            }
            int begin = period, end;
            if (period == 0)
            {
                if (R <= 64)
                {
                    la.Len = R; la.Next = 0; st.Steps.Add(la);
                    st.Steps.Add(La.Start(z[R]));
                    return false;
                }
                La.Start(z[0]).Step(z[1], out la); next = 0; i = 2;
                period = FixedLen(R); begin = 0;
            }
            else if (period > 64)
            {
                st.Steps.RemoveAt(st.Steps.Count - 1);
                La.Start(z[0]).Step(z[1], out la); next = 0; i = 2;
                period = FixedLen(period); begin = 0;
            }
            end = begin + period;
            for (; i < R; i++)
            {
                bool dip = la.Step(z[i], out var nl);
                if (dip || i >= end)
                {
                    la.Len = i - begin; la.Next = next; st.Steps.Add(la);
                    next = i; begin = i; end = begin + period;
                    if (nl.DetectPeriod(z[i + 1]) || i + 1 >= R) la = La.Start(z[i]);
                    else { La.Start(z[i]).Step(z[i + 1], out la); i++; }
                }
                else la = nl;
            }
            la.Len = i - begin; la.Next = next; st.Steps.Add(la);
            st.Steps.Add(La.Start(z[R]));
            return true;
        }

        private static bool BuildStage(Table t)
        {
            var prev = t.Stages[^1].Steps;
            int M = prev.Count - 1;   // without the sentinel
            var st = new Stage();
            int period = 0, i, j, next = 0;
            prev[0].Compose(prev[1], out var la);
            i = prev[0].Len + prev[1].Len;
            for (j = 2; j < M; j++)
            {
                if (la.Compose(prev[j], out var nl))
                {
                    if (prev[j].Thr == 0.0) break;
                    period = i;
                    la.Len = period; la.Next = 0; st.Steps.Add(la);
                    next = j;
                    if (nl.DetectPeriod(prev[j + 1].Ref) || j + 1 >= M) { la = prev[j]; i += prev[j].Len; j++; }
                    else { prev[j].Compose(prev[j + 1], out la); i += prev[j].Len + prev[j + 1].Len; j += 2; }
                    break;
                }
                la = nl; i += prev[j].Len;
            }
            t.Stages.Add(st);
            int len0 = prev[0].Len, begin = period, end;
            if (period == 0)
            {
                if (t.R / len0 <= 64)
                {
                    la.Len = t.R; la.Next = 0; st.Steps.Add(la);
                    st.Steps.Add(La.Start(t.Ref[t.R]));
                    return false;
                }
                prev[0].Compose(prev[1], out la); i = len0 + prev[1].Len; next = 0; j = 2;
                period = len0 * FixedLen((double)t.R / len0); begin = 0;
            }
            else if (period > len0 * 64)
            {
                st.Steps.RemoveAt(st.Steps.Count - 1);
                prev[0].Compose(prev[1], out la); i = len0 + prev[1].Len; next = 0; j = 2;
                period = len0 * FixedLen((double)period / len0); begin = 0;
            }
            end = begin + period;
            for (; j < M; j++)
            {
                bool dip = la.Compose(prev[j], out var nl);
                if (dip || i >= end)
                {
                    la.Len = i - begin; la.Next = next; st.Steps.Add(la);
                    next = j; begin = i; end = begin + period;
                    if (nl.DetectPeriod(prev[j + 1].Ref) || j + 1 >= M) la = prev[j];
                    else { prev[j].Compose(prev[j + 1], out la); i += prev[j].Len; j++; }
                }
                else la = nl;
                i += prev[j].Len;
            }
            la.Len = i - begin; la.Next = next; st.Steps.Add(la);
            st.Steps.Add(La.Start(t.Ref[t.R]));
            return true;
        }

        // §2.5 — the AT from the first step of the highest stage that gives one.
        private static void BuildAt(Table t, double radius, bool verbose = false)
        {
            for (int s = t.Stages.Count - 1; s >= 0; s--)
            {
                var a = t.Stages[s].Steps[0];
                var nx = t.Stages[s].Steps[1];
                var at = new At
                {
                    Len = a.Len,
                    C = a.Z * a.C,
                    InvZ = 1.0 / a.Z,
                    RefC = nx.Ref * a.Z,
                    Esc2 = Math.Min(Norm(a.Z) * a.Thr, Math.Pow(2, 256)),
                };
                at.ThrC = Math.Min(a.ThrC, Math.Pow(2, 256) / Cheb(at.C));
                bool usable = radius * radius * Norm(at.C) * Math.Pow(2, 32) > Norm(at.RefC) && at.Esc2 > 4.0
                              && double.IsFinite(Norm(at.C)) && double.IsFinite(Norm(at.InvZ));
                if (verbose)
                    Console.WriteLine($"      AT from stage {s}: step {at.Len:N0}, escape r {Math.Sqrt(at.Esc2):G3}, dc threshold {at.ThrC:G3}, " +
                                      $"view/offset {radius * Math.Sqrt(Norm(at.C)) / Math.Sqrt(Norm(at.RefC)):G3} (needs > 2^-16) -> {(usable ? "usable" : "not usable")}");
                if (usable) { t.AT = at; t.UseAT = true; return; }
            }
        }

        // ── Evaluation (§2.4) ──────────────────────────────────────────────────────

        private struct Ops { public long At, La, Pt; public bool UsedAt; }

        private static int Evaluate(Table t, Complex dc, int maxIter, bool climb, ref Ops ops)
        {
            int top = t.Stages.Count - 1;
            long n = 0;
            Complex dz = 0, z = 0;
            int stage = top, j = 0;   // j = index in t.Stages[stage] (or the reference, stage -1)
            double mdc = Cheb(dc);

            if (t.UseAT && mdc <= t.AT.ThrC)
            {
                ops.UsedAt = true;
                long atMax = (maxIter - 1) / t.AT.Len + 1;
                Complex cp = dc * t.AT.C + t.AT.RefC, zp = 0;
                long k = 0;
                while (true)
                {
                    zp = zp * zp + cp; k++; ops.At++;
                    if (k >= atMax) return maxIter;
                    if (Norm(zp) > t.AT.Esc2) break;
                }
                z = zp * t.AT.InvZ; dz = z; n = k * t.AT.Len;
            }

            while (true)
            {
                // Stages from `stage` down to 0.
                for (; stage >= 0; stage--)
                {
                    var steps = t.Stages[stage].Steps;
                    int M = steps.Count - 1;
                    if (mdc >= steps[0].ThrC) { j = steps[j].Next; continue; }
                    bool rebased = false;
                    while (true)
                    {
                        var s = steps[j];
                        Complex w = dz * (2 * s.Ref + dz);
                        // FF: the step's own δc threshold too (Imagina checks only the stage's first step).
                        if (Cheb(w) >= s.Thr || mdc >= s.ThrC) { j = s.Next; break; }
                        dz = w * s.Z + dc * s.C;
                        n += s.Len; ops.La++;
                        z = dz + steps[j + 1].Ref;
                        if (n >= maxIter) return maxIter;
                        j++;
                        if (j == M || Cheb(dz) > Cheb(z))
                        {
                            j = 0; dz = z;   // rebase: Z_0 = 0, exact
                            if (climb && stage < top) { rebased = true; break; }
                        }
                    }
                    if (rebased) { stage = top + 1; continue; }   // loop decrement → top
                }

                // Plain perturbation from reference index j (stage -1).
                var Z = t.Ref;
                int m = j;
                while (true)
                {
                    dz = 2 * Z[m] * dz + dz * dz + dc;
                    m++; n++; ops.Pt++;
                    z = Z[m] + dz;
                    if (Norm(z) >= Bailout2) return (int)n;
                    if (n >= maxIter) return maxIter;
                    if (m == t.R || Norm(z) < Norm(dz))
                    {
                        m = 0; dz = z;
                        if (climb && top >= 0) break;
                    }
                }
                stage = top; j = 0;
            }
        }

        // ── Exact iteration and the reference (fixed-point BigInteger) ──────────────

        private static BigInteger Fixed(double d, int bits)
        {
            if (d == 0) return BigInteger.Zero;
            long b = BitConverter.DoubleToInt64Bits(d);
            int exp = (int)((b >> 52) & 0x7FF);
            long mant = b & 0xFFFFFFFFFFFFFL;
            if (exp == 0) exp = 1; else mant |= 1L << 52;
            int shift = exp - 1075 + bits;
            BigInteger v = shift >= 0 ? new BigInteger(mant) << shift : new BigInteger(mant) >> -shift;
            return d < 0 ? -v : v;
        }

        private static double ToDouble(BigInteger v, int bits)
        {
            if (v.IsZero) return 0;
            long len = (long)v.GetBitLength();   // magnitude bits (sign ignored)
            int drop = (int)Math.Max(0, len - 62);
            return (double)(v >> drop) * Math.Pow(2, drop - bits);
        }

        private static int Exact(BigInteger cr, BigInteger ci, int maxIter, int bits)
        {
            BigInteger er2 = new BigInteger(512 * 512) << bits, zr = 0, zi = 0;
            for (int n = 0; n < maxIter; n++)
            {
                BigInteger zr2 = (zr * zr) >> bits, zi2 = (zi * zi) >> bits;
                if (zr2 + zi2 >= er2) return n;
                zi = ((zr * zi) >> (bits - 1)) + ci;
                zr = zr2 - zi2 + cr;
            }
            return maxIter;
        }

        private static Complex[] Reference(BigInteger cr, BigInteger ci, int maxIter, int bits, double pixel, int periodHint, out int period)
        {
            var z = new List<Complex> { 0 };
            BigInteger zr = 0, zi = 0, er2 = new BigInteger(4) << bits;
            period = 0;
            for (int n = 1; n <= maxIter; n++)
            {
                BigInteger zr2 = (zr * zr) >> bits, zi2 = (zi * zi) >> bits;
                if (zr2 + zi2 >= er2) break;   // the reference escaped: stop there
                zi = ((zr * zi) >> (bits - 1)) + ci;
                zr = zr2 - zi2 + cr;
                var zd = new Complex(ToDouble(zr, bits), ToDouble(zi, bits));
                z.Add(zd);
                // #1614 — back within a pixel of 0; or at the period FF's own reference found
                // (its double-double / OD orbit lands closer to the nucleus than this one).
                if (n == periodHint || Math.Sqrt(Norm(zd)) < pixel) { period = n; break; }
            }
            return z.ToArray();
        }

        // ── Driver ─────────────────────────────────────────────────────────────────

        public static int Run(string[] args)
        {
            int W = args.Length > 2 ? int.Parse(args[1]) : 160, H = args.Length > 2 ? int.Parse(args[2]) : 90;
            int samples = int.TryParse(Environment.GetEnvironmentVariable("FF_LAPROBE_SAMPLES"), out int sv) ? sv : 12;
            int bits = int.TryParse(Environment.GetEnvironmentVariable("FF_LAPROBE_BITS"), out int bv) ? bv : 320;
            bool climb = Environment.GetEnvironmentVariable("FF_LAPROBE_CLIMB") != "0";
            string? only = Environment.GetEnvironmentVariable("FF_LAPROBE_SCENE");
            Console.WriteLine($"laprobe {W}x{H}, {samples} exact samples at {bits} bits, climb-on-rebase {(climb ? "on" : "off")}");

            foreach (var s in DeepCostProbe.Scenes)
            {
                if (only != null && !s.Name.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
                double zoom = s.KfZoom * 3.5 * H / (4.0 * Math.Max(W, H));   // #1615 conversion, as DeepCostProbe
                double scale = 3.5 / Math.Max(W, H) / zoom;
                double[] X = s.X.Split('|').Select(double.Parse).ToArray();
                double[] Y = s.Y.Split('|').Select(double.Parse).ToArray();
                BigInteger cr = X.Aggregate(BigInteger.Zero, (a, l) => a + Fixed(l, bits));
                BigInteger ci = Y.Aggregate(BigInteger.Zero, (a, l) => a + Fixed(l, bits));

                // FF's current deep path on the same grid.
                var ff = DeepCostProbe.Make(s, zoom, W, H);
                var sw = Stopwatch.StartNew();
                ff.Calculate();
                double ffMs = sw.Elapsed.TotalMilliseconds;
                var ffIt = (int[])ff.IterationBuffer.Clone();
                var wk = ff.LastPtWork;

                // Reference + LA table.
                sw.Restart();
                var refZ = Reference(cr, ci, s.Iter, bits, scale, ff.ReferencePeriod, out int period);
                double refMs = sw.Elapsed.TotalMilliseconds;
                var t = new Table { Ref = refZ, R = refZ.Length - 1 };
                sw.Restart();
                if (BuildStage0(t))
                    while (t.Stages.Count < 256 && BuildStage(t)) { }
                double radius = scale * Math.Sqrt(W * W + H * H) * 0.5;
                BuildAt(t, radius, verbose: true);
                double buildMs = sw.Elapsed.TotalMilliseconds;
                Console.WriteLine($"  {s.Name,-14} reference {t.R:N0} (period {period:N0}) in {refMs:F0} ms; {t.Stages.Count} stages built in {buildMs:F1} ms; " +
                                  (t.UseAT ? $"AT step {t.AT.Len:N0}, escape r {Math.Sqrt(t.AT.Esc2):G3}" : "no AT"));
                for (int k = 0; k < t.Stages.Count; k++)
                {
                    var st = t.Stages[k].Steps;
                    int cnt = st.Count - 1;
                    Console.WriteLine($"      stage {k}: {cnt,7:N0} steps, first {st[0].Len,9:N0} iter, longest {st.Take(cnt).Max(x => x.Len),9:N0}, " +
                                      $"min dc threshold {st.Take(cnt).Min(x => x.ThrC):G3} (pixel {scale:G3})");
                }


                // LA over the frame.
                var laIt = new int[W * H];
                var opsAll = new Ops[W * H];
                sw.Restart();
                Parallel.For(0, H, y =>
                {
                    for (int x = 0; x < W; x++)
                    {
                        var o = new Ops();
                        var dc = new Complex((x - W * 0.5) * scale, (y - H * 0.5) * scale);
                        laIt[y * W + x] = Evaluate(t, dc, s.Iter, climb, ref o);
                        opsAll[y * W + x] = o;
                    }
                });
                double laMs = sw.Elapsed.TotalMilliseconds;
                double px = W * H;
                double atAvg = opsAll.Average(o => o.At), laAvg = opsAll.Average(o => o.La), ptAvg = opsAll.Average(o => o.Pt);
                long maxOps = opsAll.Max(o => o.At + o.La + o.Pt);
                int usedAt = opsAll.Count(o => o.UsedAt);
                int diff = 0, inOut = 0;
                for (int i = 0; i < laIt.Length; i++)
                {
                    if (laIt[i] != ffIt[i]) diff++;
                    if ((laIt[i] >= s.Iter) != (ffIt[i] >= s.Iter)) inOut++;
                }
                double ffOpsPx = (wk.PlainSteps + wk.BlaSkips) / (px / 4.0);
                Console.WriteLine($"      LA   : {laMs,7:F0} ms scalar; per px AT {atAvg:F1} + LA {laAvg:F1} + plain {ptAvg:F1} = {atAvg + laAvg + ptAvg:F1} steps " +
                                  $"(max {maxOps:N0}); AT used by {100.0 * usedAt / px:F0} % of px");
                var fst = ff.LastHpStageMs;
                Console.WriteLine($"      FF   : {ffMs,7:F0} ms (orbit {fst.RefOrbit:F0}, BLA {fst.Bla:F0}, SA {fst.Sa:F0}, pixels {fst.Pixels:F0}; period {ff.ReferencePeriod:N0}) SIMD x4; per 4-px batch {wk.PlainSteps / (px / 4.0):F0} plain + {wk.BlaSkips / (px / 4.0):F0} BLA = {ffOpsPx:F0} steps; " +
                                  $"iterations per px {ffIt.Average(v => (double)v):F0}");
                Console.WriteLine($"      LA vs FF frame: iteration counts differ at {diff:N0} px ({100.0 * diff / px:F2} %), in/out {inOut:N0}");

                // Exact samples.
                var rng = new Random(1624);
                var pts = Enumerable.Range(0, samples).Select(_ => (x: rng.Next(W), y: rng.Next(H))).ToArray();
                var exact = new int[samples];
                sw.Restart();
                Parallel.For(0, samples, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, k =>
                {
                    var (x, y) = pts[k];
                    exact[k] = Exact(cr + Fixed((x - W * 0.5) * scale, bits), ci + Fixed((y - H * 0.5) * scale, bits), s.Iter, bits);
                });
                double exMs = sw.Elapsed.TotalMilliseconds;
                int laOk = 0, ffOk = 0; double laErr = 0, ffErr = 0;
                var sb = new System.Text.StringBuilder();
                for (int k = 0; k < samples; k++)
                {
                    int i = pts[k].y * W + pts[k].x;
                    if (laIt[i] == exact[k]) laOk++;
                    if (ffIt[i] == exact[k]) ffOk++;
                    laErr += Math.Abs((double)laIt[i] - exact[k]);
                    ffErr += Math.Abs((double)ffIt[i] - exact[k]);
                    sb.Append($" {exact[k]}/{laIt[i]}/{ffIt[i]}");
                }
                Console.WriteLine($"      exact ({exMs / 1000:F0} s): LA {laOk}/{samples} exact (mean |err| {laErr / samples:F0}), " +
                                  $"FF {ffOk}/{samples} (mean |err| {ffErr / samples:F0})");
                Console.WriteLine($"      samples exact/LA/FF:{sb}");
            }
            Console.WriteLine("laprobe done");
            return 0;
        }
    }
}
