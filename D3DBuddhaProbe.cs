// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #838 (GPU parity plan G4.7): --d3dbuddhaprobe. The D3D11 half of the GPU
// Buddhabrot parity check; the Vulkan half is Server.Tests' S838GpuBuddhabrotTests
// (the test project is net10.0 and cannot load the D3D backend).
//
// It creates a headless D3D11 hardware device (as --d3dpturbcalc does) and checks:
//   - the D3D11 kernel (FXC) against BuddhaUniformSampler.Run, the strict-
//     float replay of its algorithm: equal band totals (within the approximate-
//     division slack) and at most 1% of pixels different, for Buddhabrot and
//     AntiBuddhabrot, Standard and HD splats;
//   - the production path (a calculator with a real MandelbrotGpuKernel), 640x480:
//     the GPU really sampled; #1218 — its frame is the CPU frame (one shared
//     sampler), to a few pixels; and its block-summed histogram is as close to the
//     classic double-precision sampler's as two seeds of that sampler are. Checked
//     at bands 20 / 200 / 2000:
//     at the default 500 / 5 000 / 50 000 the high band is a handful of 50 000-
//     iteration orbits, so two CPU seeds already differ by 0.8-1.1 there and a
//     one-seed comparison measures nothing;
//   - CPU vs GPU time per case at the default settings.
//
// Exit 0 = pass or SKIP (no D3D11 hardware device); 1 = parity failure.

using System;
using System.Linq;
using FracturingFog.Models;
using FracturingFog.Rendering;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FracturingFog
{
    internal static class D3DBuddhaProbe
    {
        public static int Run()
        {
            var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
                out ID3D11Device? device, out _, out ID3D11DeviceContext? context);
            if (hr.Failure || device == null || context == null)
            {
                Console.WriteLine($"d3dbuddhaprobe SKIP: no D3D11 hardware device (0x{hr.Code:X8}).");
                return 0;
            }

            string adapterName = "D3D11 adapter";
            try
            {
                using var dxgi = device.QueryInterface<IDXGIDevice>();
                using var adapter = dxgi.GetAdapter();
                adapterName = adapter.Description.Description;
            }
            catch { /* cosmetic */ }

            try
            {
                using var kernel = new MandelbrotGpuKernel(device, context, new object());
                BuddhaFamilyCalculator.UseGpuBuddha = true;   // #1218 — opt-in
                Console.WriteLine($"d3dbuddhaprobe on {adapterName}");
                bool ok = true;
                foreach (bool anti in new[] { false, true })
                    foreach (bool hd in new[] { false, true })
                        ok &= ReferenceCase(kernel, anti, hd);
                ok &= ProductionCase(kernel, "Buddhabrot", anti: false, hd: false);
                ok &= ProductionCase(kernel, "Buddhabrot HD", anti: false, hd: true);
                ok &= ProductionCase(kernel, "AntiBuddhabrot", anti: true, hd: false);
                Console.WriteLine(ok ? $"d3dbuddhaprobe OK: {adapterName}" : "d3dbuddhaprobe FAIL");
                return ok ? 0 : 1;
            }
            finally
            {
                context.Dispose();
                device.Dispose();
            }
        }

        private static bool ReferenceCase(IGpuKernel kernel, bool anti, bool hd)
        {
            const int w = 160, h = 120;
            int n = w * h;
            var batch = new GpuBuddhaBatch(w, h, 3.5 / w, -0.5, 0, 2000, anti, hd, 20, 200, 9, 0, 200_000);
            var g = new[] { new uint[n], new uint[n], new uint[n] };
            var e = new[] { new uint[n], new uint[n], new uint[n] };
            kernel.RunBuddhaBatch(batch, g[0], g[1], g[2]);
            BuddhaUniformSampler.Run(batch, e[0], e[1], e[2]);
            bool ok = true;
            var line = $"  reference anti={anti} hd={hd}:";
            for (int b = 0; b < 3; b++)
            {
                long gt = g[b].Sum(v => (long)v), et = e[b].Sum(v => (long)v);
                int mismatch = Enumerable.Range(0, n).Count(i => g[b][i] != e[b][i]);
                line += $" band{b} {gt}/{et} ({mismatch} px)";
                ok &= et > 0 && Math.Abs(gt - et) <= Math.Max(4, et / 2000) && mismatch <= n / 100;
            }
            Console.WriteLine(line + (ok ? "" : "  FAIL"));
            return ok;
        }

        private static BuddhaFamilyCalculator Calc(bool anti, int seed, bool hd, IGpuKernel? k, bool defaults)
        {
            BuddhaFamilyCalculator c = anti ? new AntiBuddhabrotCalculator(640, 480) : new BuddhabrotCalculator(640, 480);
            c.FractalParameters = new FractalParameters
            {
                BuddhaSeed = seed,
                BuddhaQualityMode = hd ? BuddhaQualityMode.HighDefinition : BuddhaQualityMode.Standard,
            };
            if (!defaults)
            {
                c.FractalParameters.BuddhaIterLow = 20;
                c.FractalParameters.BuddhaIterMid = 200;
                c.FractalParameters.BuddhaIterHigh = 2000;
                c.MaxIterations = 2000;
            }
            c.GpuKernel = k; c.UseGpuCompute = k != null;
            return c;
        }

        private static double[] Blocks(BuddhaFamilyCalculator c)
        {
            const int block = 16;
            int bw = c.Width / block, bh = c.Height / block;
            var v = new double[3 * bw * bh];
            double total = 0;
            var bands = new[] { c.HitsR, c.HitsG, c.HitsB };
            for (int b = 0; b < 3; b++)
                for (int y = 0; y < bh * block; y++)
                    for (int x = 0; x < bw * block; x++)
                    {
                        double a = bands[b][y * c.Width + x];
                        v[b * bw * bh + (y / block) * bw + x / block] += a;
                        total += a;
                    }
            for (int i = 0; i < v.Length; i++) v[i] /= total;
            return v;
        }

        private static double Distance(double[] a, double[] b) => a.Zip(b).Sum(p => Math.Abs(p.First - p.Second));

        private static bool ProductionCase(IGpuKernel kernel, string label, bool anti, bool hd)
        {
            var warm = Calc(anti, 1, hd, kernel, defaults: true);
            warm.FractalParameters.BuddhaSamples = 2000;
            warm.Calculate();   // compiles the shader

            // Timing at the defaults.
            var timeCpu = Calc(anti, 1, hd, null, defaults: true);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            timeCpu.Calculate();
            long cpuMs = sw.ElapsedMilliseconds;
            var timeGpu = Calc(anti, 1, hd, kernel, defaults: true);
            sw.Restart();
            timeGpu.Calculate();
            long gpuMs = sw.ElapsedMilliseconds;

            // #1218 — one shared sampler: the GPU and CPU frames at the defaults are the
            // same image (the GPU's approximate division may move a few pixels).
            int differ = 0;
            for (int i = 0; i < timeCpu.ColorBuffer.Length; i++)
                if (timeCpu.ColorBuffer[i] != timeGpu.ColorBuffer[i]) differ++;

            // The float sampler against the classic double-precision one, at bands
            // 20 / 200 / 2000: as close as two seeds of the double sampler are.
            var gpu = Calc(anti, 1, hd, kernel, defaults: false);
            gpu.Calculate();
            bool savedShared = BuddhaFamilyCalculator.UseSharedUniformSampler;
            BuddhaFamilyCalculator.UseSharedUniformSampler = false;
            var dbl1 = Calc(anti, 1, hd, null, defaults: false);
            var dbl2 = Calc(anti, 2, hd, null, defaults: false);
            try { dbl1.Calculate(); dbl2.Calculate(); }
            finally { BuddhaFamilyCalculator.UseSharedUniformSampler = savedShared; }

            double noise = Distance(Blocks(dbl1), Blocks(dbl2));
            double parity = Distance(Blocks(gpu), Blocks(dbl1));
            bool ok = gpu.LastCalculateUsedGpu && timeGpu.LastCalculateUsedGpu
                && differ <= timeCpu.ColorBuffer.Length / 1000 && parity < 1.4 * noise + 0.002;
            Console.WriteLine($"  {label} 640x480: cpu {cpuMs} ms, gpu {gpuMs} ms; GPU vs CPU image {differ} px differ; " +
                              $"vs double sampler {parity:F4} (double seed noise {noise:F4}); " +
                              $"route '{gpu.LastGpuRoute.Detail ?? gpu.LastGpuRoute.Reason}'" + (ok ? "" : "  FAIL"));
            return ok;
        }
    }
}
