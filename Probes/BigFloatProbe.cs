// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1830 (epic #1428, C1): --bigfloatprobe. The cost of a Mandelbrot reference-orbit
// iteration (z ← z² + c: three products and four sums) in BigFloat at several
// precisions, against OD (octuple-double, the ≤1e100 path). The zoom each precision
// reaches is BigFloat.PrecisionForZoom's inverse. FF_BIGFLOAT_ITERS sets the
// iterations per precision (default 20,000). c is the bench-kit p951,094 minibrot
// centre (fractadyne bench-kit, WindySnowOwl, from rith's FF 0.7.1 report; see the
// bibliography): its orbit stays bounded for 951,094 iterations, like a deep reference.

using System;
using System.Diagnostics;
using FracturingFog.FFMath;

namespace FracturingFog
{
    internal static class BigFloatProbe
    {
        public static int Run(string[] args)
        {
            int iters = int.TryParse(Environment.GetEnvironmentVariable("FF_BIGFLOAT_ITERS"), out int n) && n > 0 ? n : 20_000;
            Console.WriteLine($"bigfloatprobe: {iters:N0} orbit iterations per row, c = the p951,094 centre");
            double[] X = { -0.028041054305504548, 1.3292199503012176e-18, -5.275751227722853e-35, -4.2033498374722585e-51, 2.1867860643118847e-67, -1.2673249633835861e-83, -2.7319253065659894e-100, -1.9376664330122738e-116 };
            double[] Y = { -0.6948927538996524, 5.189489838305484e-17, 5.567887477024055e-34, 1.3765984796783024e-50, 7.914053078872335e-67, -5.2156113374567495e-83, -1.2800174978852658e-99, -7.991933193373865e-116 };
            OD cxOd = new(X[0], X[1], X[2], X[3], X[4], X[5], X[6], X[7]), cyOd = new(Y[0], Y[1], Y[2], Y[3], Y[4], Y[5], Y[6], Y[7]);

            // Every row runs 5 times and keeps the best: the first runs execute tier-0 JIT
            // code (OD measured 11-15 µs / iteration there, 1.3 µs once optimised).
            {
                double best = double.MaxValue, mag = 0;
                for (int rep = 0; rep < 5; rep++)
                {
                    var sw = Stopwatch.StartNew();
                    mag = OdRun(cxOd, cyOd, iters);
                    best = Math.Min(best, sw.Elapsed.TotalMilliseconds * 1e6 / iters);
                }
                Console.WriteLine($"  OD (424 bits, to 1e100)        {best,10:F0} ns / iteration   (1 M iterations: {best * 1e6 / 1e9,7:F2} s)  |z| {mag:F3}");
            }

            foreach (int log10Zoom in new[] { 100, 300, 1000, 3000, 10000 })
            {
                int p = BigFloat.PrecisionForZoom(log10Zoom);
                var cr = BigFloat.FromOD(cxOd, p); var ci = BigFloat.FromOD(cyOd, p);
                int n2 = p > 5000 ? Math.Max(200, iters / 10) : iters;
                double best = double.MaxValue, kb = 0, mag = 0;
                for (int rep = 0; rep < 5; rep++)
                {
                    BigFloat zr = BigFloat.Zero(p), zi = BigFloat.Zero(p);
                    long gc0 = GC.GetTotalAllocatedBytes(false);
                    var sw = Stopwatch.StartNew();
                    for (int k = 0; k < n2; k++) Step(ref zr, ref zi, cr, ci, p);
                    double ns = sw.Elapsed.TotalMilliseconds * 1e6 / n2;
                    if (ns < best) { best = ns; kb = (GC.GetTotalAllocatedBytes(false) - gc0) / 1024.0 / n2; }
                    mag = Math.Sqrt(zr.ToDouble() * zr.ToDouble() + zi.ToDouble() * zi.ToDouble());
                }
                Console.WriteLine($"  BigFloat {p,5} bits (to 1e{log10Zoom,-5}) {best,10:F0} ns / iteration   (1 M iterations: {best * 1e6 / 1e9,7:F2} s)  " +
                                  $"{kb:F1} KiB allocated / iteration  |z| {mag:F3}");
            }
            Console.WriteLine("bigfloatprobe done");
            return 0;
        }

        private static double OdRun(OD cr, OD ci, int n)
        {
            OD zr = OD.Zero, zi = OD.Zero;
            for (int k = 0; k < n; k++) { var t = (zr * zi) * 2.0 + ci; zr = zr.Square() - zi.Square() + cr; zi = t; }
            return Math.Sqrt(zr.X0 * zr.X0 + zi.X0 * zi.X0);
        }

        private static void Step(ref BigFloat zr, ref BigFloat zi, BigFloat cr, BigFloat ci, int p)
        {
            var t = BigFloat.Add(BigFloat.Mul(zr, zi, p).ScaleB(1), ci, p);
            zr = BigFloat.Add(BigFloat.Sub(zr.Square(p), zi.Square(p), p), cr, p);
            zi = t;
        }
    }
}
