// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #607 (GPU parity plan G4.6): --d3ddeeporbitprobe. The D3D11 half of the deep-zoom
// orbit parity check; the Vulkan half is Server.Tests' S607GpuDeepOrbitTests (the
// test project is net10.0 and cannot load the D3D backend).
//
// It creates a headless D3D11 hardware device (as --d3dpturbcalc does), attaches a
// real MandelbrotGpuKernel to a MandelbrotCalculator running an orbit ColorGen theme
// at the --d3dpturbcalc deep views, and compares the GPU deep orbit frame
// (RunPerturbOrbit) with the CPU deep orbit path (#609). Checked:
//   - the GPU deep orbit kernel really ran (LastFrameUsedGpuOrbitPerturbation);
//   - the CPU frame is non-degenerate (distinct iteration counts, colours);
//   - iteration disagreement, and the median |GPU - CPU| trap / stripe / TIA value
//     where both took the same escape iteration;
//   - the coloured image stays within a mean drift bound, and at least 10x inside
//     the drift of the direct-double frame (the mush the deep path exists to avoid).
// Timings: CPU deep orbit vs the GPU frame (second run; the first compiles).
//
// Exit 0 = pass or SKIP (no D3D11 hardware device, or no FP64 shader ops);
// 1 = parity failure.

using System;
using System.Collections.Generic;
using System.Linq;
using FracturingFog.Models;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FracturingFog
{
    internal static class D3DDeepOrbitProbe
    {
        private const int W = 128, H = 128;
        private const double MaxDisagreeFrac = 0.02;
        private const double MaxMeanColourDrift = 2.0;   // mean |Δ| per channel, 0..255
        private const double MaxMedianInputDelta = 1e-4;

        // Every pixel at these depths shares a long early orbit, so the means vary little
        // across the frame; x40 + fract spreads that over the hue (and magnifies any error).
        private const string ThemeSource =
            "return hsv(fract(stripeAvg * 40.0 + tiaAvg * 40.0), 0.85, saturate(0.4 + trapMin * 20.0));";

        // The --d3dpturbcalc views.
        private static readonly (string Label, double Zoom, double[] Cx, double[] Cy, int MaxIter)[] Cases =
        {
            ("seahorse 1e14", 1e14, new[] { -0.743643887037151, 0, 0, 0 }, new[] { 0.13182590420533, 0, 0, 0 }, 3000),
            ("saprobe 1e15", 1e15,
                new[] { -1.1726999042772253, 8.9529605787776783E-17, 0, 0 },
                new[] { -0.2968356710071185, -2.3536240906562374E-18, 0, 0 }, 4096),
            ("Deeper and Deeper 4.5e46", 4.49845E+46,
                new[] { -1.9918151296901943, -7.8219818188678307E-17, 3.2454272033149852E-33, -2.6986232918289806E-49 },
                new[] { -5.5240415753972429E-06, -2.8404793590633191E-22, 1.5048294824547351E-38, -6.0649764033320806E-55 }, 20000),
            ("3E47 Test", 3E+47,
                new[] { -1.9918151296901943, -7.8219844803880472E-17, 1.660139930392911E-34, 8.217274172159319E-51 },
                new[] { -5.5240415753972429E-06, -2.8659813126937928E-22, 6.6910924119662832E-39, 6.2394735914401016E-55 }, 20000),
        };

        public static int Run()
        {
            if (Environment.GetEnvironmentVariable("FF_GPU_PERTURB_BUDGET_MS") == null)
                Environment.SetEnvironmentVariable("FF_GPU_PERTURB_BUDGET_MS", "600000");

            var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
                out ID3D11Device? device, out _, out ID3D11DeviceContext? context);
            if (hr.Failure || device == null || context == null)
            {
                Console.WriteLine($"d3ddeeporbitprobe SKIP: no D3D11 hardware device (0x{hr.Code:X8}).");
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

            bool savedPerturb = MandelbrotCalculator.UseGpuPerturbation;
            bool savedDeepOrbit = MandelbrotCalculator.UseGpuDeepOrbit;
            bool savedGpuOrbit = InterpretedOrbitColorMap.GpuEnabled;
            try
            {
                using var kernel = new FracturingFog.Rendering.MandelbrotGpuKernel(device, context, new object());
                if (!kernel.SupportsPerturbationOrbit)
                {
                    Console.WriteLine($"d3ddeeporbitprobe SKIP: {adapterName} has no FP64 shader ops.");
                    return 0;
                }
                InterpretedOrbitColorMap.GpuEnabled = true;
                MandelbrotCalculator.UseGpuDeepOrbit = true;
                if (InterpretedColorMap.TryCreate(ThemeSource, null, out var err) is not InterpretedOrbitColorMap theme)
                {
                    Console.Error.WriteLine($"d3ddeeporbitprobe FAIL: theme did not compile: {err}");
                    return 1;
                }

                Console.WriteLine($"d3ddeeporbitprobe {W}x{H} on {adapterName}, orbit mask {theme.OrbitInputs}");
                int failures = 0;
                foreach (var c in Cases)
                    failures += RunCase(kernel, theme, c) ? 0 : 1;
                if (failures > 0)
                {
                    Console.Error.WriteLine($"d3ddeeporbitprobe FAIL: {failures}/{Cases.Length} case(s).");
                    return 1;
                }
                Console.WriteLine($"d3ddeeporbitprobe OK: {adapterName}");
                return 0;
            }
            finally
            {
                MandelbrotCalculator.UseGpuPerturbation = savedPerturb;
                MandelbrotCalculator.UseGpuDeepOrbit = savedDeepOrbit;
                InterpretedOrbitColorMap.GpuEnabled = savedGpuOrbit;
                context.Dispose();
                device.Dispose();
            }
        }

        private static double MeanDrift(uint[] a, uint[] b)
        {
            long sum = 0;
            for (int i = 0; i < a.Length; i++)
                for (int s = 0; s < 24; s += 8)
                    sum += Math.Abs((int)((a[i] >> s) & 0xFF) - (int)((b[i] >> s) & 0xFF));
            return sum / (a.Length * 3.0);
        }

        private static double Median(List<double> xs)
        {
            if (xs.Count == 0) return double.NaN;
            xs.Sort();
            return xs[xs.Count / 2];
        }

        private static bool RunCase(FracturingFog.Rendering.IGpuKernel kernel, InterpretedOrbitColorMap theme,
            (string Label, double Zoom, double[] Cx, double[] Cy, int MaxIter) c)
        {
            int n = W * H;
            MandelbrotCalculator.UseGpuPerturbation = false;
            var cpu = MakeCalc(c, theme, QualityPreset.Extreme);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            cpu.Calculate();
            long cpuMs = sw.ElapsedMilliseconds;
            var direct = MakeCalc(c, theme, new QualityPreset { Name = "direct", AllowHighPrecision = false });
            direct.Calculate();

            int distinct = new HashSet<int>(cpu.IterationBuffer).Count;
            int colours = new HashSet<uint>(cpu.ColorBuffer).Count;
            int inSet = cpu.IterationBuffer.Count(v => v >= c.MaxIter);
            if (!cpu.IsHighPrecisionActive || distinct < 8 || colours < 32 || inSet >= n)
            {
                Console.Error.WriteLine($"  FAIL {c.Label}: degenerate CPU frame (hp={cpu.IsHighPrecisionActive} distinct={distinct} colours={colours} inSet={inSet}).");
                return false;
            }

            MandelbrotCalculator.UseGpuPerturbation = true;
            var warm = MakeCalc(c, theme, QualityPreset.Extreme);
            warm.GpuKernel = kernel;
            warm.Calculate();   // compiles the mask's shader
            var gpu = MakeCalc(c, theme, QualityPreset.Extreme);
            gpu.GpuKernel = kernel;
            sw.Restart();
            gpu.Calculate();
            long gpuMs = sw.ElapsedMilliseconds;
            MandelbrotCalculator.UseGpuPerturbation = false;

            int disagree = 0;
            var dTrap = new List<double>(); var dStripe = new List<double>(); var dTia = new List<double>();
            for (int i = 0; i < n; i++)
            {
                if (gpu.IterationBuffer[i] != cpu.IterationBuffer[i]) { disagree++; continue; }
                if (gpu.IterationBuffer[i] >= c.MaxIter) continue;
                dTrap.Add(Math.Abs(gpu.TrapBuffer[i] - cpu.TrapBuffer[i]));
                dStripe.Add(Math.Abs(gpu.StripeBuffer[i] - cpu.StripeBuffer[i]));
                dTia.Add(Math.Abs(gpu.TiaBuffer[i] - cpu.TiaBuffer[i]));
            }
            double frac = disagree / (double)n;
            double drift = MeanDrift(gpu.ColorBuffer, cpu.ColorBuffer);
            double directDrift = MeanDrift(direct.ColorBuffer, cpu.ColorBuffer);
            double mTrap = Median(dTrap), mStripe = Median(dStripe), mTia = Median(dTia);
            Console.WriteLine(
                $"  {c.Label}: cpu {cpuMs} ms, gpu {gpuMs} ms; iter disagree {frac:P3}; median |d| trap {mTrap:E2} " +
                $"stripe {mStripe:E2} tia {mTia:E2}; colour drift {drift:F3} (direct double {directDrift:F2}); " +
                $"route '{gpu.LastGpuRoute.Detail}'");

            bool ok = true;
            void Fail(string why) { Console.Error.WriteLine($"    FAIL {c.Label}: {why}"); ok = false; }
            if (!gpu.LastFrameUsedGpuOrbitPerturbation)
            {
                Fail($"the GPU deep orbit kernel did not run ({gpu.LastGpuRoute.Detail ?? gpu.LastGpuRoute.Reason}).");
                return false;
            }
            if (frac > MaxDisagreeFrac) Fail($"iteration disagreement {frac:P3} > {MaxDisagreeFrac:P0}.");
            if (!(mTrap < MaxMedianInputDelta && mStripe < MaxMedianInputDelta && mTia < MaxMedianInputDelta))
                Fail($"median orbit-input delta above {MaxMedianInputDelta:E0}.");
            if (drift > MaxMeanColourDrift) Fail($"colour drift {drift:F3} > {MaxMeanColourDrift}.");
            if (!(directDrift > 10 * drift && directDrift > 0.1))
                Fail($"direct double drifts only {directDrift:F2} (GPU {drift:F3}) — the view does not separate the paths.");
            return ok;
        }

        private static MandelbrotCalculator MakeCalc(
            (string Label, double Zoom, double[] Cx, double[] Cy, int MaxIter) c,
            InterpretedOrbitColorMap theme, QualityPreset quality) => new(W, H)
        {
            Quality = quality,
            CenterX = c.Cx[0], CenterXLo = c.Cx[1], CenterX2 = c.Cx[2], CenterX3 = c.Cx[3],
            CenterY = c.Cy[0], CenterYLo = c.Cy[1], CenterY2 = c.Cy[2], CenterY3 = c.Cy[3],
            Zoom = c.Zoom,
            MaxIterations = c.MaxIter,
            ColorMap = theme,
        };
    }
}
