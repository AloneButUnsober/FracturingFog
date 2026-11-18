// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1630 (epic #1623, S7): --gpulaprobe [W H]. The LA stage kernel on the GPU against
// the GPU's SA / BLA kernel and the CPU paths, on the deep views inside both the LA
// default (zoom >= 1e30) and the GPU's deep range (<= MaxGpuPerturbZoom): the
// bench-kit p448 and path scenes (fractadyne bench-kit, WindySnowOwl,
// https://github.com/WindySnowOwl/fractadyne/tree/main/bench-kit, from rith's FF 0.7.1
// benchmark report; see the bibliography) and FF's own deep built-in regions
// (DeepCostProbe.FfViews). Per backend (D3D11, Vulkan) and scene: frame time of CPU
// BLA (DeepLa Off), CPU LA, GPU SA / BLA (Off) and GPU LA (On; #1664: was Auto), best of
// FF_GPULA_REPS (default 2), alternating; and how many pixels' iteration counts differ
// from the CPU LA frame. The too-slow abort is off (speed is what is measured).
// FF_GPULA_BACKEND=d3d11|vulkan picks one backend, FF_GPULA_MODES a subset of the four
// columns (digits 0..3, e.g. "13" = CPU LA and GPU LA only), FF_GPULA_SCENE a scene,
// FF_GPULA_WARP=1 the software D3D11 device instead of the hardware one.
// FF_GPULA_SKEW=1 compiles the D3D11 kernel with LaKernelSource.SkewPlainMagnitudesForTests
// (#1661: the rounding difference that hung the GT 710, forced on any device; the
// iteration differences must stay the same).
// #1664: two more columns, the float-exp kernels (GpuDeepPrecision Fp32): 4 = GPU float-exp
// (LA Off), 5 = GPU float-exp LA (UseGpuLaFloatExp). FF_GPULA_DEEP=1 adds the bench scenes
// past the GPU deep limit (p953 … p951,094), where only CPU and float-exp LA run.
// #1880 (epic #1428, C6a): FF_GPULA_EXACT=1 adds an exact view past 1e100 (the dense
// 340-digit centre near M(4,1), AP reference orbit) at 1e150 and 1e285: CPU (LA Auto)
// against the float-exp LA kernel, with which LA table it ran (double, or float-exp
// where A and B overflow the double one). #1882 (C6b): also 1e320 and 1e600, past
// double's pixel scale (the kernel with a float-exp scale, "fx px").
// Exit 0 always.

using System;
using System.Diagnostics;
using System.Linq;
using FracturingFog.FFMath;
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
            MandelbrotGpuKernel.LaSkewPlainMagnitudesForTests = Environment.GetEnvironmentVariable("FF_GPULA_SKEW") == "1";

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
            Console.WriteLine($"  {name}: {k.DeviceName ?? name}{(k.SupportsPerturbationLa ? "" : " (no fp64 LA kernel: columns 2-3 skipped)")}");
            bool deep = Environment.GetEnvironmentVariable("FF_GPULA_DEEP") == "1";
            var scenes = DeepCostProbe.Scenes.Where(s => s.Name.StartsWith("p448") || s.Name.StartsWith("path")
                    || (deep && s.KfZoom > 1e50))
                .Concat(DeepCostProbe.FfViews.Where(s => s.KfZoom >= 1e30))
                .Where(s => Environment.GetEnvironmentVariable("FF_GPULA_SCENE") is not { } f || s.Name.Contains(f, StringComparison.OrdinalIgnoreCase));
            if (Environment.GetEnvironmentVariable("FF_GPULA_EXACT") == "1" && k.SupportsPerturbationLaFloatExp)
                foreach (int zl in new[] { 150, 285, 320, 600 }) ExactDeep(k, zl, w, h, reps);   // #1882: past 1e290 too
            string modes = Environment.GetEnvironmentVariable("FF_GPULA_MODES") ?? "012345";
            if (!k.SupportsPerturbationLa) modes = modes.Replace("2", "").Replace("3", "");
            if (!k.SupportsPerturbationLaFloatExp) modes = modes.Replace("5", "");
            bool saved = MandelbrotCalculator.UseGpuPerturbation, savedFx = MandelbrotCalculator.UseGpuLaFloatExp, savedLa = MandelbrotCalculator.UseGpuLa;
            var savedPrec = MandelbrotCalculator.GpuDeepPrecisionMode;
            try
            {
                foreach (var s in scenes)
                {
                    double zoom = s.FfZoom ? s.KfZoom : s.KfZoom * 3.5 * h / (4.0 * Math.Max(w, h));
                    var best = new double[6];
                    Array.Fill(best, double.MaxValue);
                    MandelbrotCalculator?[] last = new MandelbrotCalculator?[6];
                    bool past = zoom > MandelbrotCalculator.MaxGpuPerturbZoom;
                    for (int rep = 0; rep < reps; rep++)
                        for (int m = 0; m < 6; m++)
                        {
                            if (!modes.Contains((char)('0' + m))) continue;
                            if (past && m is 2 or 3 or 4) continue;   // only CPU and float-exp LA run past 1e50
                            bool gpu = m >= 2, la = m % 2 == 1;
                            MandelbrotCalculator.UseGpuPerturbation = gpu;
                            MandelbrotCalculator.GpuDeepPrecisionMode = m >= 4 ? GpuDeepPrecision.Fp32 : GpuDeepPrecision.Double;
                            MandelbrotCalculator.UseGpuLaFloatExp = m == 5;
                            MandelbrotCalculator.UseGpuLa = m == 3;
                            var c = DeepCostProbe.Make(s, zoom, w, h);
                            c.LaMode = la ? DeepLaMode.On : DeepLaMode.Off;   // #1664: On (Auto follows the device calibration, #1665)
                            if (gpu) c.GpuKernel = k;
                            var sw = Stopwatch.StartNew();
                            c.Calculate();
                            best[m] = Math.Min(best[m], sw.Elapsed.TotalMilliseconds);
                            last[m] = c;
                        }
                    var refIt = last[1]?.IterationBuffer ?? last.First(x => x != null)!.IterationBuffer;
                    string D(int m) => last[m] == null ? "-" : $"{100.0 * last[m]!.IterationBuffer.Zip(refIt).Count(p => p.First != p.Second) / refIt.Length:F2} %";
                    string R(int a, int b) => best[a] == double.MaxValue || best[b] == double.MaxValue ? "-" : $"x{best[a] / best[b]:F2}";
                    string T(int m) => best[m] == double.MaxValue ? "      -" : $"{best[m],7:F0}";
                    bool gLa = last[3]?.LastFrameUsedGpuLa ?? false, gOn = last[2]?.LastFrameUsedGpuPerturbation ?? false;
                    bool fxOn = last[4]?.LastFrameUsedGpuFloatExp ?? false, fxLa = last[5]?.LastFrameUsedGpuLaFloatExp ?? false;
                    Console.WriteLine($"    {s.Name,-18} {zoom,9:0.00e0} | CPU BLA {T(0)} | CPU LA {T(1)} | GPU SA/BLA {T(2)}{(last[2] == null || gOn ? "" : " (CPU!)")} | " +
                                      $"GPU LA {T(3)}{(last[3] == null || gLa ? "" : " (not LA!)")} | GPU fx {T(4)}{(last[4] == null || fxOn ? "" : " (not fx!)")} | " +
                                      $"GPU fx LA {T(5)}{(last[5] == null || fxLa ? "" : " (not fx LA!)")} ms | fx LA {R(4, 5)} vs fx, {R(1, 5)} vs CPU LA | " +
                                      $"iter diff vs CPU LA: fx LA {D(5)}, fx {D(4)}, GPU LA {D(3)}, GPU SA/BLA {D(2)}, CPU BLA {D(0)}");
                }
            }
            finally
            {
                MandelbrotCalculator.UseGpuPerturbation = saved;
                MandelbrotCalculator.UseGpuLaFloatExp = savedFx;
                MandelbrotCalculator.UseGpuLa = savedLa;
                MandelbrotCalculator.GpuDeepPrecisionMode = savedPrec;
            }
        }

        // #1880 — the S1880 view (Server.Tests/S1880GpuLaFloatExpDeepTests.cs).
        private const string ExactX = "-0.1010963638456221610257854457386225654638054428262534838769311776607808407404705842748212198105167790334045319085567411939715461442609118823557039076803016157584181434354544823068106664029419378267411891607819526448995396995334466823393126699009817557150425206538866991861344333961600610562775385144259609560703568221643279956133036926714760";
        private const string ExactY = "0.9562865108091415007710960577299774358098333365105291700343143215005246590657167325269784107873398072043444724926469284366752406567465722656200815719741551313831228054884443810571677870203738395055477279309548311953190385170024877917670113105649462054553485859930615004427397447762045980085973915977603864276670744042253335354323566859404712";

        private static void ExactDeep(IGpuKernel k, int zl, int w, int h, int reps)
        {
            if (!ExactCentre.TryParse(ExactX, ExactY, out var centre)) return;
            var d = centre.ToDeep();
            bool saved = MandelbrotCalculator.UseGpuPerturbation, savedFx = MandelbrotCalculator.UseGpuLaFloatExp, savedLa = MandelbrotCalculator.UseGpuLa;
            var savedPrec = MandelbrotCalculator.GpuDeepPrecisionMode;
            try
            {
                MandelbrotCalculator Frame(bool gpu)
                {
                    MandelbrotCalculator.UseGpuPerturbation = gpu;
                    MandelbrotCalculator.GpuDeepPrecisionMode = GpuDeepPrecision.Fp32;
                    MandelbrotCalculator.UseGpuLaFloatExp = gpu;
                    MandelbrotCalculator.UseGpuLa = false;
                    var c = new MandelbrotCalculator(w, h)
                    {
                        Quality = FracturingFog.Models.QualityPreset.Extreme, MaxIterations = 1000 + 12 * zl, ColorMap = new FracturingFog.Models.HsvPalette(),
                        CenterX = d.Re.X0, CenterXLo = d.Re.X1, CenterX2 = d.Re.X2, CenterX3 = d.Re.X3,
                        CenterX4 = d.Re.X4, CenterX5 = d.Re.X5, CenterX6 = d.Re.X6, CenterX7 = d.Re.X7,
                        CenterY = d.Im.X0, CenterYLo = d.Im.X1, CenterY2 = d.Im.X2, CenterY3 = d.Im.X3,
                        CenterY4 = d.Im.X4, CenterY5 = d.Im.X5, CenterY6 = d.Im.X6, CenterY7 = d.Im.X7,
                    };
                    if (gpu) c.GpuKernel = k;
                    c.SetZoomLog10(zl);
                    c.SetDepth(centre);
                    return c;
                }
                double tCpu = double.MaxValue, tGpu = double.MaxValue;
                MandelbrotCalculator? cpu = null, gpuC = null;
                for (int rep = 0; rep < reps; rep++)
                {
                    cpu = Frame(false); var sw = Stopwatch.StartNew(); cpu.Calculate(); tCpu = Math.Min(tCpu, sw.Elapsed.TotalMilliseconds);
                    gpuC = Frame(true); sw.Restart(); gpuC.Calculate(); tGpu = Math.Min(tGpu, sw.Elapsed.TotalMilliseconds);
                }
                double diff = 100.0 * gpuC!.IterationBuffer.Zip(cpu!.IterationBuffer).Count(p => p.First != p.Second) / cpu.IterationBuffer.Length;
                string ran = gpuC.LastFrameUsedGpuFloatExpPixels ? "fx LA, float-exp table, fx px"
                    : gpuC.LastFrameUsedGpuLaFloatExp ? (gpuC.LastFrameUsedGpuLaTableFx ? "fx LA, float-exp table" : "fx LA, double table")
                    : $"CPU! ({gpuC.LastGpuRoute.Reason})";
                Console.WriteLine($"    exact M(4,1) 1e{zl,-8} | CPU [{cpu.LastPrecisionLabel}] {tCpu,7:F0} | GPU [{ran}] {tGpu,7:F0} ms | " +
                                  $"x{tCpu / tGpu:F2} | iter diff vs CPU {diff:F2} %");
            }
            finally
            {
                MandelbrotCalculator.UseGpuPerturbation = saved;
                MandelbrotCalculator.UseGpuLaFloatExp = savedFx;
                MandelbrotCalculator.UseGpuLa = savedLa;
                MandelbrotCalculator.GpuDeepPrecisionMode = savedPrec;
            }
        }
    }
}
