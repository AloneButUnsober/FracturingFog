// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #2033 — --bigfloatopsprobe: the cost of each operation of one BigFloat reference-orbit
// step (MandelbrotCalculator.ComputeReferenceOrbitAP) at the precisions of 1e300, 1e1000,
// 1e3000 and 1e10000 (~1k, 3.4k, 10k, 33k bits), on full-precision random operands of
// orbit-like size: Mul, Add / Sub, ScaleB, ToOD, ToFloatExp, and the whole step. Best of
// several batches (µs per call). Exit 0 always.

using System;
using System.Diagnostics;
using FracturingFog.FFMath;

namespace FracturingFog
{
    internal static class BigFloatOpsProbe
    {
        public static int Run(string[] args)
        {
            Console.WriteLine("bigfloatopsprobe: µs per call, best of 5 batches");
            Console.WriteLine("bits | Mul | Add | ScaleB | ToOD | ToFloatExp | step (3 Mul + 3 Add/Sub + ScaleB + 2 ToOD + 2 ToFloatExp) | the step with BigFloatStepWorkers");
            var rng = new Random(2033);
            foreach (int bits in new[] { 1100, 2200, 3400, 6000, 10000, 33300 })
            {
                BigFloat a = Rand(rng, bits, -1), b = Rand(rng, bits, -2), c = Rand(rng, bits, -1);
                int n = Math.Max(20, 2_000_000 / bits);
                double mul = Time(n, () => BigFloat.Mul(a, b, bits));
                double sq = Time(n, () => BigFloat.Mul(a, a, bits));
                double add = Time(n, () => BigFloat.Add(a, c, bits));
                double sb = Time(n, () => a.ScaleB(1));
                double od = Time(n, () => a.ToOD());
                double fx = Time(n, () => a.ToFloatExp());
                BigFloat zr = a, zi = b;
                double step = Time(n, () =>
                {
                    var odr = zr.ToOD(); var odi = zi.ToOD();
                    var fr = zr.ToFloatExp(); var fi = zi.ToFloatExp();
                    var zr2 = BigFloat.Mul(zr, zr, bits);
                    var zi2 = BigFloat.Mul(zi, zi, bits);
                    var zri = BigFloat.Mul(zr, zi, bits);
                    var ni = BigFloat.Add(zri.ScaleB(1), c, bits);
                    var nr = BigFloat.Add(BigFloat.Sub(zr2, zi2, bits), c, bits);
                    return (odr, odi, fr, fi, ni, nr);
                });
                double par;
                using (var w = new BigFloatStepWorkers())
                    par = Time(n, () =>
                    {
                        var fr = zr.ToFloatExp(); var fi = zi.ToFloatExp();
                        w.Step(zr, zi, bits, out var zr2, out var zi2, out var zri, out var odr, out var odi);
                        var ni = BigFloat.Add(zri.ScaleB(1), c, bits);
                        var nr = BigFloat.Add(BigFloat.Sub(zr2, zi2, bits), c, bits);
                        return (odr, odi, fr, fi, ni, nr);
                    });
                Console.WriteLine($"{bits} | {mul:F2} (square {sq:F2}) | {add:F2} | {sb:F3} | {od:F2} | {fx:F3} | {step:F2} | parallel {par:F2} (x{step / par:F2})");
            }
            Console.WriteLine("bigfloatopsprobe done");
            return 0;
        }

        // A full-precision value of about 2^exp2, from a random decimal of the precision's digits.
        private static BigFloat Rand(Random rng, int bits, int exp2)
        {
            int digits = (int)(bits * 0.30103) + 2;
            var sb = new System.Text.StringBuilder(rng.Next(2) == 0 ? "0." : "-0.");
            sb.Append((char)('1' + rng.Next(9)));
            for (int i = 1; i < digits; i++) sb.Append((char)('0' + rng.Next(10)));
            return BigFloat.Parse(sb.ToString(), bits).ScaleB(exp2);
        }

        private static double Time<T>(int n, Func<T> f)
        {
            for (int i = 0; i < Math.Min(n, 10); i++) f();   // warm-up / JIT
            double best = double.MaxValue;
            for (int r = 0; r < 5; r++)
            {
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < n; i++) f();
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds * 1000.0 / n);
            }
            return best;
        }
    }
}
