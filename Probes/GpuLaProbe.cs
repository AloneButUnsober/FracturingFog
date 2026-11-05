// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1630 (epic #1623, S7): --gpulaprobe [W H]. The LA stage kernel on the GPU against
// the GPU's SA / BLA kernel and the CPU paths, on the deep views inside both the LA
// default (zoom >= 1e30) and the GPU's deep range (<= MaxGpuPerturbZoom): the
// bench-kit p448 and path scenes (fractadyne bench-kit, WindySnowOwl,
// https://github.com/WindySnowOwl/fractadyne/tree/main/bench-kit, from rith's FF 0.7.1
// benchmark report; see the bibliography) and FF's own deep built-in regions
// (DeepCostProbe.FfViews). Per backend (D3D11, Vulkan) and scene: frame time of CPU
// BLA (DeepLa Off), CPU LA, GPU SA / BLA (Off) and GPU LA (Auto), best of
// FF_GPULA_REPS (default 2), alternating; and how many pixels' iteration counts differ
// from the CPU LA frame. The too-slow abort is off (speed is what is measured).
// FF_GPULA_BACKEND=d3d11|vulkan picks one backend, FF_GPULA_MODES a subset of the four
// columns (digits 0..3, e.g. "13" = CPU LA and GPU LA only), FF_GPULA_SCENE a scene,
// FF_GPULA_WARP=1 the software D3D11 device instead of the hardware one.
// Exit 0 always.

using System;
using System.Diagnostics;
using System.Linq;
using FracturingFog.Interefaces;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Vulkan;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace FracturingFog
{
    internal static class GpuLaProbe
    {
        public static int Run(string[] args)
        {
            int w = args.Length > 2 ? int.Parse(args[1]) : 960, h = args.Length > 2 ? int.Parse(args[2]) : 540;
            int reps = int.TryParse(Environment.GetEnvironmentVariable("FF_GPULA_REPS"), out int r) && r > 0 ? r : 2;
            // Speed is measured, not guarded: no first-band abort (as GpuBandProbe).
            if (Environment.GetEnvironmentVariable("FF_GPU_PERTURB_BUDGET_MS") == null)
                Environment.SetEnvironmentVariable("FF_GPU_PERTURB_BUDGET_MS", "600000");
            Console.WriteLine($"gpulaprobe {w}x{h}, best of {reps}");

            // FF_GPULA_WARP=1: the software D3D11 device (no TDR) instead of the hardware one.
            var driver = Environment.GetEnvironmentVariable("FF_GPULA_WARP") == "1" ? DriverType.Warp : DriverType.Hardware;
            var hr = D3D11.D3D11CreateDevice(null, driver, DeviceCreationFlags.None,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
                out ID3D11Device? device, out _, out ID3D11DeviceContext? context);
            string? only = Environment.GetEnvironmentVariable("FF_GPULA_BACKEND")?.ToLowerInvariant();
            if (only == "vulkan") { device?.Dispose(); context?.Dispose(); device = null; context = null; }
            if (hr.Failure || device == null || context == null) Console.WriteLine("  D3D11 SKIP: no hardware device (or not selected)");
            else
            {
                try { using var k = new MandelbrotGpuKernel(device, context, new object()); Backend("D3D11", k, w, h, reps); }
                finally { context.Dispose(); device.Dispose(); }
            }
            VulkanComputeKernel? vk = null;
            if (only != "d3d11") try { vk = VulkanComputeKernel.TryCreateWithOwnContext(); } catch { }
            if (vk == null) Console.WriteLine("  Vulkan SKIP: no device");
            else using (vk) Backend("Vulkan", vk, w, h, reps);
            Console.WriteLine("gpulaprobe done");
            return 0;
        }

        private static void Backend(string name, IGpuKernel k, int w, int h, int reps)
        {
            if (!k.SupportsPerturbationLa) { Console.WriteLine($"  {name} SKIP: no fp64 LA kernel"); return; }
            Console.WriteLine($"  {name}: {k.DeviceName ?? name}");
            var scenes = DeepCostProbe.Scenes.Where(s => s.Name.StartsWith("p448") || s.Name.StartsWith("path"))
                .Concat(DeepCostProbe.FfViews.Where(s => s.KfZoom >= 1e30))
                .Where(s => Environment.GetEnvironmentVariable("FF_GPULA_SCENE") is not { } f || s.Name.Contains(f, StringComparison.OrdinalIgnoreCase));
            string modes = Environment.GetEnvironmentVariable("FF_GPULA_MODES") ?? "0123";
            bool saved = MandelbrotCalculator.UseGpuPerturbation;
            try
            {
                foreach (var s in scenes)
                {
                    double zoom = s.FfZoom ? s.KfZoom : s.KfZoom * 3.5 * h / (4.0 * Math.Max(w, h));
                    var best = new double[4];
                    Array.Fill(best, double.MaxValue);
                    MandelbrotCalculator?[] last = new MandelbrotCalculator?[4];
                    for (int rep = 0; rep < reps; rep++)
                        for (int m = 0; m < 4; m++)
                        {
                            if (!modes.Contains((char)('0' + m))) continue;
                            bool gpu = m >= 2, la = m % 2 == 1;
                            MandelbrotCalculator.UseGpuPerturbation = gpu;
                            var c = DeepCostProbe.Make(s, zoom, w, h);
                            c.LaMode = la ? DeepLaMode.Auto : DeepLaMode.Off;
                            if (gpu) c.GpuKernel = k;
                            var sw = Stopwatch.StartNew();
                            c.Calculate();
                            best[m] = Math.Min(best[m], sw.Elapsed.TotalMilliseconds);
                            last[m] = c;
                        }
                    var refIt = last[1]?.IterationBuffer ?? last.First(x => x != null)!.IterationBuffer;
                    string D(int m) => last[m] == null ? "-" : $"{100.0 * last[m]!.IterationBuffer.Zip(refIt).Count(p => p.First != p.Second) / refIt.Length:F2} %";
                    bool gLa = last[3]?.LastFrameUsedGpuLa ?? false, gOn = last[2]?.LastFrameUsedGpuPerturbation ?? false;
                    Console.WriteLine($"    {s.Name,-18} {zoom,9:0.00e0} | CPU BLA {best[0],7:F0} | CPU LA {best[1],7:F0} | GPU SA/BLA {best[2],7:F0}{(gOn ? "" : " (CPU!)")} | " +
                                      $"GPU LA {best[3],7:F0}{(gLa ? "" : " (not LA!)")} ms | GPU LA x{best[2] / best[3]:F2} vs GPU SA/BLA, x{best[1] / best[3]:F2} vs CPU LA | " +
                                      $"iter diff vs CPU LA: GPU LA {D(3)}, GPU SA/BLA {D(2)}, CPU BLA {D(0)}");
                }
            }
            finally { MandelbrotCalculator.UseGpuPerturbation = saved; }
        }
    }
}
