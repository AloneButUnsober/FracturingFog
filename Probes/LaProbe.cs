// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown
// SPDX-FileCopyrightText: Yu, Zhuoran (GitHub 5E-324), Imagina <https://github.com/5E-324/Imagina>, AGPL-3.0

// #1624 (epic #1623): --laprobe [W H]. Proof of concept of period-synchronised linear
// approximation ("LA stages" + the approximation transform, AT) on the bench-kit
// minibrot chain p953 -> p15,248 -> p121,984 -> p951,094 (fractadyne bench-kit,
// WindySnowOwl, https://github.com/WindySnowOwl/fractadyne/tree/main/bench-kit, from
// rith's FF 0.7.1 benchmark report; scenes shared with DeepCostProbe). Not wired
// into the calculator.
//
// DERIVED CODE: the evaluator and the AT are a C# translation of Imagina's
// EvaluateLA / EvaluatePT / EvaluateAT, LAInfo::CreateAT and CreateATFromLA (Imagina
// by GitHub user 5E-324, profile name "Yu, Zhuoran", https://github.com/5E-324/Imagina,
// HInfLAEvaluator.{h,cpp} at commit cd046377, read 2026-11-03, AGPL-3.0; see
// THIRD-PARTY-NOTICES.md §3 and the bibliography #imagina). The maths is in
// Docs/Technical/Period-Synchronised-LA-DesignPlan.md §2; §2.7 maps each function to
// its Imagina counterpart. Since #1625 the stages come from Engine/Math/LaStageTable.cs; this file
// keeps the AT and the evaluator (S3 / S4 move them into the engine). The places
// they depart from Imagina are marked "FF:" and listed in §2.6 of the doc.
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
// (Imagina's behaviour) instead of climbing back to the top stage. Since #1626 it also
// renders the frame with the engine's LA evaluator (MandelbrotCalculator.UseLaEvaluator,
// "eng") and scores it against the same exact samples. Exit 0 always.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using FracturingFog.FFMath;

namespace FracturingFog
{
    internal static class LaProbe
    {
        private const double Bailout2 = 512.0 * 512.0;   // FF's escape radius (MandelbrotCalculator)

        private static double Cheb(Complex z) => Math.Max(Math.Abs(z.Real), Math.Abs(z.Imaginary));
        private static double Norm(Complex z) => z.Real * z.Real + z.Imaginary * z.Imaginary;

        private struct At
        {
            public int Len;
            public double ThrC, Esc2;
            public Complex RefC, C, InvZ;
        }

        // #1625 — the stages are FF's own LaStageTable (Engine/Math), built over the
        // probe's reference; the probe adds the AT (S4) and the evaluator (S3).
        private sealed class Table
        {
            public Complex[] Ref = [];       // Z_0 .. Z_R
            public int R;                    // reference length (the period, or the cap)
            public LaStageTable L = null!;
            public At AT;
            public bool UseAT;
        }

        // §2.5 — the AT from the first step of the highest stage that gives one.
        private static void BuildAt(Table t, double radius, bool verbose = false)
        {
            for (int s = t.L.Stages - 1; s >= 0; s--)
            {
                var a = t.L.Step(s, 0);
                var nx = t.L.Step(s, 1);
                var at = new At
                {
                    Len = a.Len,
                    C = a.A * a.B,
                    InvZ = 1.0 / a.A,
                    RefC = nx.Ref * a.A,
                    Esc2 = Math.Min(Norm(a.A) * a.Thr, Math.Pow(2, 256)),
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
            int top = t.L.Stages - 1;
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
                    var steps = t.L.Data.AsSpan(t.L.StageStart[stage], t.L.StageCount[stage] + 1);   // with the sentinel
                    int M = steps.Length - 1;
                    if (mdc >= steps[0].ThrC) { j = steps[j].Next; continue; }
                    bool rebased = false;
                    while (true)
                    {
                        var s = steps[j];
                        Complex w = dz * (2 * s.Ref + dz);
                        // FF: the step's own δc threshold too (Imagina checks only the stage's first step).
                        if (Cheb(w) >= s.Thr || mdc >= s.ThrC) { j = s.Next; break; }
                        dz = w * s.A + dc * s.B;
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
                // #1626 — the engine's LA evaluator on the same grid.
                var eng = DeepCostProbe.Make(s, zoom, W, H);
                eng.UseLaEvaluator = true;
                sw.Restart();
                eng.Calculate();
                double engMs = sw.Elapsed.TotalMilliseconds;
                var engIt = (int[])eng.IterationBuffer.Clone();
                var ew = eng.LastLaWork;
                string engLine = ($"      eng  : {engMs,7:F0} ms (LA on {eng.LastFrameUsedLa}; pixels {eng.LastHpStageMs.Pixels:F0} ms); per px LA {ew.LaSteps / (double)Math.Max(1, ew.Pixels):F1} + plain {ew.PlainSteps / (double)Math.Max(1, ew.Pixels):F1} + BLA {ew.BlaSkips / (double)Math.Max(1, ew.Pixels):F1}" +
                                  $" (period test {eng.LaTable?.PeriodTest}, {eng.LaTable?.Stages} stages, first lengths " +
                                  string.Join(", ", Enumerable.Range(0, eng.LaTable?.Stages ?? 0).Select(k => eng.LaTable!.Stage(k)[0].Len)) + ")");

                // Reference + LA table.
                sw.Restart();
                var refZ = Reference(cr, ci, s.Iter, bits, scale, ff.ReferencePeriod, out int period);
                double refMs = sw.Elapsed.TotalMilliseconds;
                var t = new Table { Ref = refZ, R = refZ.Length - 1 };
                sw.Restart();
                t.L = LaStageTable.Build(refZ.Select(z => z.Real).ToArray(), refZ.Select(z => z.Imaginary).ToArray(), t.R)!;
                double radius = scale * Math.Sqrt(W * W + H * H) * 0.5;
                BuildAt(t, radius, verbose: true);
                double buildMs = sw.Elapsed.TotalMilliseconds;
                Console.WriteLine($"  {s.Name,-14} reference {t.R:N0} (period {period:N0}) in {refMs:F0} ms; {t.L.Stages} stages ({t.L.Bytes / 1024.0:N0} KiB) built in {buildMs:F1} ms; " +
                                  (t.UseAT ? $"AT step {t.AT.Len:N0}, escape r {Math.Sqrt(t.AT.Esc2):G3}" : "no AT"));
                for (int k = 0; k < t.L.Stages; k++)
                {
                    var st = t.L.Stage(k).ToArray();
                    Console.WriteLine($"      stage {k}: {st.Length,7:N0} steps, first {st[0].Len,9:N0} iter, longest {st.Max(x => x.Len),9:N0}, " +
                                      $"min dc threshold {st.Min(x => x.ThrC):G3} (pixel {scale:G3})");
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
                Console.WriteLine(engLine);
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
                int laOk = 0, ffOk = 0, engOk = 0; double laErr = 0, ffErr = 0, engErr = 0;
                var sb = new System.Text.StringBuilder();
                for (int k = 0; k < samples; k++)
                {
                    int i = pts[k].y * W + pts[k].x;
                    if (laIt[i] == exact[k]) laOk++;
                    if (ffIt[i] == exact[k]) ffOk++;
                    if (engIt[i] == exact[k]) engOk++;
                    engErr += Math.Abs((double)engIt[i] - exact[k]);
                    laErr += Math.Abs((double)laIt[i] - exact[k]);
                    ffErr += Math.Abs((double)ffIt[i] - exact[k]);
                    sb.Append($" {exact[k]}/{laIt[i]}/{ffIt[i]}/{engIt[i]}");
                }
                Console.WriteLine($"      exact ({exMs / 1000:F0} s): LA {laOk}/{samples} exact (mean |err| {laErr / samples:F0}), " +
                                  $"FF {ffOk}/{samples} (mean |err| {ffErr / samples:F0}), eng {engOk}/{samples} (mean |err| {engErr / samples:F0})");
                Console.WriteLine($"      samples exact/LA/FF/eng:{sb}");
            }
            Console.WriteLine("laprobe done");
            return 0;
        }
    }
}
