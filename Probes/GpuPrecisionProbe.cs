// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1611: --gpuprecisionprobe. How far the fp32 shallow GPU kernel moves the picture
// against the CPU (double), as a function of K = pixel spacing / fp32 ulp at the
// frame's largest |c| (the MandelbrotCalculator.Fp32MinPixelUlps gate). Per view, on
// the D3D11 default adapter (a device of its own), with the gate off:
//   displacement d = |smooth_GPU − smooth_CPU| / |∇smooth_CPU| (pixels), over the
//   pixels where the CPU field is locally smooth (escaped, gradient 0.02..0.5 a
//   pixel, all 4-neighbours escaped) — boundary pixels are chaotic in any precision
//   and would hide a shift; reported as median / p95 / share above 0.25 px.
// A frame that trips the OS watchdog reports "GPU not used" (the kernel's device-loss
// check sends it to the CPU) rather than a displacement. Exit 0 always (a
// measurement); SKIP without a D3D11 hardware device.

using System;
using System.Collections.Generic;
using System.Linq;
using FracturingFog.Models;

namespace FracturingFog
{
    internal static class GpuPrecisionProbe
    {
        private sealed record Loc(string Name, double X, double Y);

        public static int Run(string[] args)
        {
            using (var first = FracturingFog.Rendering.MandelbrotGpuKernel.TryCreateWithOwnDevice())
            {
                if (first == null) { Console.WriteLine("gpuprecisionprobe SKIP: no D3D11 hardware device."); return 0; }
                Console.WriteLine($"gpuprecisionprobe on {first.DeviceName}");
            }
            var locs = new[]
            {
                new Loc("seahorse", -0.743643887037151, 0.131825904205330),
                new Loc("elephant", 0.2925755, -0.0149977),
                new Loc("spike -1.77", -1.7686, 0.0016),
                new Loc("top 0.6i", -0.1011, 0.9563),
            };
            int[] widths = { 1920, 3840 };
            double[] zooms = { 30, 100, 300, 1000, 3000, 8000 };
            double saved = MandelbrotCalculator.Fp32MinPixelUlps;
            MandelbrotCalculator.Fp32MinPixelUlps = 0;
            try
            {
                var rows = new List<(double K, double Med, double P95, double Over)>();
                foreach (var l in locs)
                foreach (int w in widths)
                foreach (double z in zooms)
                {
                    int h = w * 9 / 16;
                    var cpu = Make(l, z, w, h, null);
                    cpu.Calculate();
                    // A device of its own per view: a frame past the OS watchdog (#1481)
                    // removes the device, which must not take the later views with it.
                    using var kernel = FracturingFog.Rendering.MandelbrotGpuKernel.TryCreateWithOwnDevice();
                    var gpu = Make(l, z, w, h, kernel);
                    gpu.Calculate();
                    if (gpu.LastGpuRoute.State != FracturingFog.Render.GpuRouteState.Gpu)
                    {
                        Console.WriteLine($"  {l.Name,-12} {w}x{h} zoom {z,6}: GPU not used ({gpu.LastGpuRoute.Detail})");
                        continue;
                    }
                    var d = Displacements(cpu, gpu, w, h);
                    double scale = 3.5 / w / z;
                    double m = Math.Max(Math.Max(Math.Abs(l.X) + w * 0.5 * scale, Math.Abs(l.Y) + h * 0.5 * scale), 1.0);
                    double k = scale / (m / (1 << 23));
                    if (d.Length < 100)
                    {
                        Console.WriteLine($"  {l.Name,-12} {w}x{h} zoom {z,6}: K {k,7:F1}  too few smooth pixels ({d.Length})");
                        continue;
                    }
                    Array.Sort(d);
                    double med = d[d.Length / 2], p95 = d[(int)(d.Length * 0.95)];
                    double over = d.Count(v => v > 0.25) / (double)d.Length;
                    rows.Add((k, med, p95, over));
                    Console.WriteLine($"  {l.Name,-12} {w}x{h} zoom {z,6}: K {k,7:F1}  displacement median {med:F3} px  p95 {p95:F3} px  >0.25 px {100 * over:F2} %  ({d.Length:N0} px)");
                }
                Console.WriteLine("by K (worst p95 / worst >0.25 px share):");
                foreach (var band in new[] { (0.0, 4.0), (4, 16), (16, 32), (32, 64), (64, 128), (128, 1e9) })
                {
                    var r = rows.Where(x => x.K >= band.Item1 && x.K < band.Item2).ToList();
                    if (r.Count == 0) continue;
                    Console.WriteLine($"  K {band.Item1,4}..{band.Item2,-4}: {r.Count,2} views, p95 up to {r.Max(x => x.P95):F3} px, >0.25 px up to {100 * r.Max(x => x.Over):F2} %");
                }
            }
            finally { MandelbrotCalculator.Fp32MinPixelUlps = saved; }
            Console.WriteLine("gpuprecisionprobe done");
            return 0;
        }

        private static MandelbrotCalculator Make(Loc l, double zoom, int w, int h, FracturingFog.Rendering.IGpuKernel? k)
            => new(w, h)
            {
                CenterX = l.X, CenterY = l.Y, Zoom = zoom, MaxIterations = 4000,
                Quality = QualityPreset.Standard, ColorMap = new HsvPalette(),
                GpuKernel = k, UseGpuCompute = k != null,
            };

        private static double[] Displacements(MandelbrotCalculator cpu, MandelbrotCalculator gpu, int w, int h)
        {
            int[] ic = cpu.IterationBuffer, ig = gpu.IterationBuffer;
            float[] sc = cpu.SmoothBuffer, sg = gpu.SmoothBuffer;
            int max = cpu.MaxIterations;
            var outv = new List<double>();
            for (int y = 1; y < h - 1; y++)
            for (int x = 1; x < w - 1; x++)
            {
                int i = y * w + x;
                if (ic[i] >= max || ig[i] >= max) continue;
                if (ic[i - 1] >= max || ic[i + 1] >= max || ic[i - w] >= max || ic[i + w] >= max) continue;
                double gx = (sc[i + 1] - sc[i - 1]) * 0.5, gy = (sc[i + w] - sc[i - w]) * 0.5;
                double g = Math.Sqrt(gx * gx + gy * gy);
                if (g < 0.02 || g > 0.5) continue;
                outv.Add(Math.Abs(sg[i] - sc[i]) / g);
            }
            return outv.ToArray();
        }
    }
}
