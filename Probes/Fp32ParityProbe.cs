// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #2017 — --fp32parityprobe [W H]: the GPU deep routes against the CPU route on a
// directory of .kfr locations (FF_FP32_KFR, e.g. the fractadyne bench-kit scenes, by
// WindySnowOwl: bibliography #fractadyne-bench-kit), by
// two criteria at once, because they disagree:
//   • bit-identical colour: the share of pixels whose packed colour equals the CPU
//     route's (what rith's 0.7.2 bench reported as "same": 71-87 % for fp32);
//   • the same iteration count, and the same in / out (what #1331 reported: "~0.35 %
//     of pixels differ").
// For pixels with the same iteration count it also reports how far the smooth value
// moved, and the share of pixels whose count moved by more than 1 or 10.
// Routes: CPU (the default deep path), GPU double (FF_GPU_PERTURB_PRECISION=double,
// SA / BLA), GPU fp32 (=fp32: scaled fp32 or float-exp, whichever the view takes), and
// fp32 forced onto the float-exp entry (MandelbrotGpuKernel.ForceFloatExpEntry).
// D3D11 hardware device; FF_FP32_WARP=1 for the software device. Exit 0 always.

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using FracturingFog.Interefaces;
using FracturingFog.Rendering;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace FracturingFog
{
    internal static class Fp32ParityProbe
    {
        public static int Run(string[] args)
        {
            int w = args.Length > 2 ? int.Parse(args[1]) : 960, h = args.Length > 2 ? int.Parse(args[2]) : 540;
            string? dir = Environment.GetEnvironmentVariable("FF_FP32_KFR");
            if (dir == null || !Directory.Exists(dir)) { Console.WriteLine("fp32parityprobe: set FF_FP32_KFR to a directory of .kfr files"); return 0; }
            if (Environment.GetEnvironmentVariable("FF_GPU_PERTURB_BUDGET_MS") == null)
                Environment.SetEnvironmentVariable("FF_GPU_PERTURB_BUDGET_MS", "600000");   // measure, don't abort

            var driver = Environment.GetEnvironmentVariable("FF_FP32_WARP") == "1" ? DriverType.Warp : DriverType.Hardware;
            var hr = D3D11.D3D11CreateDevice(null, driver, DeviceCreationFlags.None,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out ID3D11Device? device, out _, out ID3D11DeviceContext? context);
            if (hr.Failure || device == null || context == null) { Console.WriteLine("fp32parityprobe: no D3D11 device"); return 0; }
            using var k = new MandelbrotGpuKernel(device, context, new object());
            Console.WriteLine($"fp32parityprobe {w}x{h}, {k.DeviceName}");
            Console.WriteLine("scene | zoom | route | same colour | same iter | same in/out | iter off >1 / >10 | same iter, |d smooth| > 0.01 / max");

            bool savedPerturb = MandelbrotCalculator.UseGpuPerturbation;
            var savedPrec = MandelbrotCalculator.GpuDeepPrecisionMode;
            try
            {
                foreach (var f in Directory.GetFiles(dir, "*.kfr").OrderBy(x => x, StringComparer.Ordinal))
                {
                    if (!FracturingFog.ViewState.KfrLocation.TryParse(File.ReadAllText(f), out var loc, out var err) || loc == null)
                    { Console.WriteLine($"  {Path.GetFileName(f)}: {err}"); continue; }
                    var (X, Y) = loc.Centre();
                    double zoom = loc.FfZoom(w, h);
                    var sc = new DeepCostProbe.Scene(Path.GetFileNameWithoutExtension(f),
                        string.Join("|", X.Select(v => v.ToString("R", CultureInfo.InvariantCulture))),
                        string.Join("|", Y.Select(v => v.ToString("R", CultureInfo.InvariantCulture))), zoom, loc.Iterations ?? 10000, true);

                    MandelbrotCalculator.UseGpuPerturbation = false;
                    var cpu = DeepCostProbe.Make(sc, zoom, w, h);
                    cpu.Calculate();
                    foreach (var (name, prec, fx) in new[] { ("GPU double", GpuDeepPrecision.Double, false), ("GPU fp32", GpuDeepPrecision.Fp32, false), ("GPU fp32, float-exp entry", GpuDeepPrecision.Fp32, true) })
                    {
                        MandelbrotCalculator.UseGpuPerturbation = true;
                        MandelbrotCalculator.GpuDeepPrecisionMode = prec;
                        var g = DeepCostProbe.Make(sc, zoom, w, h);
                        g.GpuKernel = k;
                        MandelbrotGpuKernel.ForceFloatExpEntry = fx;
                        try { g.Calculate(); } finally { MandelbrotGpuKernel.ForceFloatExpEntry = false; }
                        string route = g.LastFrameUsedGpuPerturbation
                            ? (g.LastFrameUsedGpuFloatExp ? (k.LastPerturbFloatExpWasScaledF32 ? "scaled fp32" : "float-exp") : "double kernel")
                            : "CPU (did not take the GPU)";
                        Console.WriteLine($"| {sc.Name} | {zoom:0.0e0} | {name}: {route} | " + Compare(cpu, g, sc.Iter) + " |");
                    }
                }
            }
            finally
            {
                MandelbrotCalculator.UseGpuPerturbation = savedPerturb;
                MandelbrotCalculator.GpuDeepPrecisionMode = savedPrec;
            }
            Console.WriteLine("fp32parityprobe done");
            return 0;
        }

        /// <summary>The comparison columns of <paramref name="g"/> against <paramref name="cpu"/>.</summary>
        internal static string Compare(MandelbrotCalculator cpu, MandelbrotCalculator g, int maxIter)
        {
            int[] ia = cpu.IterationBuffer, ib = g.IterationBuffer;
            float[] sa = cpu.SmoothBuffer, sb = g.SmoothBuffer;
            uint[] ca = cpu.ColorBuffer, cb = g.ColorBuffer;
            int n = ia.Length, sameColour = 0, sameIter = 0, sameInOut = 0, off1 = 0, off10 = 0, smoothMoved = 0;
            double maxSmooth = 0;
            for (int i = 0; i < n; i++)
            {
                if (ca[i] == cb[i]) sameColour++;
                if ((ia[i] >= maxIter) == (ib[i] >= maxIter)) sameInOut++;
                int d = Math.Abs(ia[i] - ib[i]);
                if (d > 1) off1++;
                if (d > 10) off10++;
                if (d != 0) continue;
                sameIter++;
                if (ia[i] >= maxIter) continue;
                double ds = Math.Abs(sa[i] - sb[i]);
                if (ds > 0.01) smoothMoved++;
                if (ds > maxSmooth) maxSmooth = ds;
            }
            string P(int c) => $"{100.0 * c / n:F2} %";
            return $"{P(sameColour)} | {P(sameIter)} | {P(sameInOut)} | {P(off1)} / {P(off10)} | {P(smoothMoved)} / {maxSmooth:F3}";
        }
    }
}
