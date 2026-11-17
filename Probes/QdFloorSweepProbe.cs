// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// --qdfloorsweep: Wave 2.14 — locate the QD (and OD) coordinate-separation floor
// DIRECTLY, no rendering. For a fixed deep centre and a sweep of zoom levels, build the
// 128 per-pixel X coordinates the render would use (QD / OD.FromCenterOffset, the same
// calls the QD / OD paths make) and count how many are bit-distinct. distinct < 128 ⇒ the
// arithmetic can no longer separate adjacent pixels at that zoom = the pixelation floor.
// This settled that the "pixelation at 1e40–1e58" was a QD floor and put OD's clean floor
// near 1e120 (MandelbrotCalculator.ApReferenceZoomLog10).
//
// #1864 (epic #1428, C11) — moved here from Program.cs and carried past OD to 1e1000:
//   • AP: the per-pixel coordinate the past-OD path's reference sits on — the exact
//     centre in BigFloat at PrecisionForZoom(zoom) plus offset·scale (ExactCentre).
//   • fx: the per-pixel delta from the reference in floatexp (offset·scale), what the
//     floatexp pixel loop (C5) starts from past 1e290.
//   Past double's range (~1e308) the scale is not a double, so the QD / OD columns stop
//   ("-"); AP and fx carry on. Two centres: the 3E47 test centre (4 QD limbs: sparse, so
//   OD can hold more of it than its ~424 bits suggest) and a dense one (the Misiurewicz
//   point near -0.1011 + 0.9563i to 340 digits), where OD's limbs run out.
//   Measured (2026-11-17): QD collapses at 1e66 on both. OD never collapses on the sparse
//   centre (its limbs' independent exponents hold centre + offset exactly), and on the
//   dense one it is clean to 1e132 and collapses at 1e134 — 22 decades past the AP
//   reference threshold (1e110). AP and fx stay 128/128 to 1e1000.

using System;
using System.Collections.Generic;
using FracturingFog.FFMath;

namespace FracturingFog
{
    internal static class QdFloorSweepProbe
    {
        private const int W = 128;

        private const string DenseX = "-0.1010963638456221610257854457386225654638054428262534838769311776607808407404705842748212198105167790334045319085567411939715461442609118823557039076803016157584181434354544823068106664029419378267411891607819526448995396995334466823393126699009817557150425206538866991861344333961600610562775385144259609560703568221643279956133036926714760";

        public static int Run(string[] args)
        {
            double[] zoomLog10 =
            {
                40, 45, 48, 50, 52, 54, 56, 58, 60, 62, 64, 66, 70, 80, 100, 120,
                125, 130, 132, 134, 136, 138, 140, 150, 200, 250, 300, 320, 400, 500, 750, 1000,
            };

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"QD / OD / AP coordinate-separation sweep - {W} pixels wide.");
            sb.AppendLine("  distinct = bit-distinct per-pixel X coords out of 128. <128 => pixels collapse.");
            sb.AppendLine("  AP = exact centre (BigFloat at PrecisionForZoom) + offset*scale; fx = floatexp offset*scale; - = past double.");

            // 3E47 Test centre — |c| ≈ 2, 4 QD limbs down to ~1e-51.
            var cxQ = new QD(-1.9918151296901943, -7.8219844803880472E-17, 1.660139930392911E-34, 8.217274172159319E-51);
            var cxO = new OD(-1.9918151296901943, -7.8219844803880472E-17, 1.660139930392911E-34, 8.217274172159319E-51, 0, 0, 0, 0);
            Sweep(sb, "3E47 centre (|c|~2, 4 QD limbs: sparse)", cxQ, cxO,
                  prec => BigFloat.FromOD(cxO, prec), zoomLog10);

            // Dense: OD's 8 limbs hold its first ~424 bits only.
            var dense = BigFloat.Parse(DenseX, ExactCentre.PrecisionForDigits(ExactCentre.SignificantDigits(DenseX)));
            var dO = dense.ToOD();
            var dQ = new QD(dO.X0, dO.X1, dO.X2, dO.X3);
            Sweep(sb, "Misiurewicz centre (dense, 340 digits)", dQ, dO,
                  prec => dense.WithPrecision(prec), zoomLog10);

            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "qdfloorsweep.out");
            System.IO.File.WriteAllText(path, sb.ToString());
            Console.WriteLine(sb.ToString());
            return 0;
        }

        private static void Sweep(System.Text.StringBuilder sb, string title, QD cxQ, OD cxO,
            Func<int, BigFloat> exactAt, double[] zoomLog10)
        {
            sb.AppendLine();
            sb.AppendLine(title);
            foreach (double zl in zoomLog10)
            {
                // Pixel scale 3.5 / W / zoom, as log2 so it exists past double.
                double scaleLog2 = Math.Log2(3.5 / W) - zl * 3.3219280948873622;
                bool inDouble = scaleLog2 > -1070;
                double scale = inDouble ? Math.Pow(2, scaleLog2) : 0;

                int prec = BigFloat.PrecisionForZoom(zl);
                var cx = exactAt(prec);
                var scaleFx = FloatExp.FromLog2(scaleLog2);
                var scaleBf = BigFloat.FromDouble(scaleFx.M, prec).ScaleB(scaleFx.E);

                var qset = new HashSet<(double, double, double, double)>();
                var oset = new HashSet<(double, double, double, double, double, double, double, double)>();
                var aset = new HashSet<BigFloat>();
                var fset = new HashSet<(double, long)>();
                for (int i = 0; i < W; i++)
                {
                    double off = i - W * 0.5;
                    if (inDouble)
                    {
                        var q = QD.FromCenterOffset(cxQ, off, scale);
                        var o = OD.FromCenterOffset(cxO, off, scale);
                        qset.Add((q.X0, q.X1, q.X2, q.X3));
                        oset.Add((o.X0, o.X1, o.X2, o.X3, o.X4, o.X5, o.X6, o.X7));
                    }
                    aset.Add(BigFloat.Add(cx, BigFloat.Mul(BigFloat.FromDouble(off, prec), scaleBf, prec), prec));
                    var d = FloatExp.FromDouble(off) * scaleFx;
                    fset.Add((d.M, d.E));
                }

                string q4 = inDouble ? $"{qset.Count,4}" : "   -";
                string o8 = inDouble ? $"{oset.Count,4}" : "   -";
                string flag = inDouble && qset.Count < W ? (oset.Count < W ? "  <-- QD + OD floor" : "  <-- QD floor")
                            : inDouble && oset.Count < W ? "  <-- OD floor" : "";
                if (aset.Count < W || fset.Count < W) flag += "  <-- AP/fx COLLAPSE";
                sb.AppendLine($"  zoom=1e{zl,-5:F0} prec={prec,5} bits  distinctQD={q4}/{W} distinctOD={o8}/{W} "
                            + $"distinctAP={aset.Count,4}/{W} distinctFx={fset.Count,4}/{W}{flag}");
            }
        }
    }
}
