// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// V6 (#82, GPU parity plan G0.6): --d3dpturbcalc. The D3D11 twin of
// Rendering.Vulkan.Smoke's --vulkanpturbcalc: the automated "live D3D deep-zoom
// parity smoke" #82 was left waiting on.
//
// It creates a headless D3D11 hardware device (as MandelbrotGpuKernelBench does),
// attaches a real MandelbrotGpuKernel to a MandelbrotCalculator at deep zoom, and
// compares the frame BOTH ways: CPU deep path (UseGpuPerturbation off) vs the
// D3D11 RunPerturb path (on). Checked:
//   - the GPU path really ran (LastFrameUsedGpuPerturbation, LastGpuRoute = Gpu),
//     so a silent CPU fallback can't pass as parity;
//   - the CPU frame is non-degenerate (many distinct iteration counts, not all
//     in-set), so the comparison isn't vacuous;
//   - iteration frames disagree on at most MaxDisagreeFrac of pixels. Like the
//     Vulkan gate, the paths aren't bit-identical: the CPU runs SIMD perturbation
//     with a rebased scalar fallback for glitched lanes, the GPU runs the rebased
//     loop for every pixel, so filament pixels at the escape knife-edge differ;
//   - the coloured image (what the user sees) stays within a mean drift bound.
// Several depths, to cover the double-δ range up to MaxGpuPerturbZoom.
//
// Exit 0 = pass or SKIP (no D3D11 hardware device, or no FP64 shader ops);
// 1 = parity failure.

using System;
using System.Collections.Generic;
using FracturingFog.Models;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FracturingFog
{
    internal static class D3DPerturbProbe
    {
        private const int W = 128, H = 128;
        private const double MaxDisagreeFrac = 0.02;
        private const double MaxMeanColourDrift = 2.0;   // mean |Δ| per channel, 0..255

        // Multi-limb centres (hi + lo + ...) so the deep views stay off the set interior
        // and above each centre's detail-depth floor: the --saprobe point at 1e15, and
        // two --qdfloorprobe regions at ~1e46-1e47 (inside MaxGpuPerturbZoom = 1e50).
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
            // The TOO-SLOW guard would otherwise disable GPU perturbation on a weak-fp64
            // card mid-probe and turn the run into a CPU-vs-CPU comparison.
            if (Environment.GetEnvironmentVariable("FF_GPU_PERTURB_BUDGET_MS") == null)
                Environment.SetEnvironmentVariable("FF_GPU_PERTURB_BUDGET_MS", "600000");

            var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
                out ID3D11Device? device, out _, out ID3D11DeviceContext? context);
            if (hr.Failure || device == null || context == null)
            {
                Console.WriteLine($"d3dpturbcalc SKIP: no D3D11 hardware device (0x{hr.Code:X8}).");
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

            bool savedFlag = MandelbrotCalculator.UseGpuPerturbation;
            try
            {
                using var kernel = new FracturingFog.Rendering.MandelbrotGpuKernel(device, context, new object());
                if (!kernel.SupportsPerturbation)
                {
                    Console.WriteLine($"d3dpturbcalc SKIP: {adapterName} has no FP64 shader ops (DoublePrecisionFloatShaderOps).");
                    return 0;
                }

                Console.WriteLine($"d3dpturbcalc {W}x{H} on {adapterName}");
                int failures = 0;
                foreach (var c in Cases)
                    failures += RunCase(kernel, c) ? 0 : 1;

                if (failures > 0)
                {
                    Console.Error.WriteLine($"d3dpturbcalc FAIL: {failures}/{Cases.Length} case(s).");
                    return 1;
                }
                Console.WriteLine($"d3dpturbcalc OK: {adapterName}");
                return 0;
            }
            finally
            {
                MandelbrotCalculator.UseGpuPerturbation = savedFlag;
                context.Dispose();
                device.Dispose();
            }
        }

        private static bool RunCase(FracturingFog.Rendering.IGpuKernel kernel,
            (string Label, double Zoom, double[] Cx, double[] Cy, int MaxIter) c)
        {
            string label = c.Label;
            int maxIter = c.MaxIter;
            // CPU reference (deep path, GPU perturbation off).
            var cpuCalc = MakeCalc(c);
            MandelbrotCalculator.UseGpuPerturbation = false;
            cpuCalc.GpuKernel = null;
            cpuCalc.Calculate();
            int[] cpuIter = (int[])cpuCalc.IterationBuffer.Clone();
            uint[] cpuColor = (uint[])cpuCalc.ColorBuffer.Clone();

            // D3D11 GPU perturbation.
            var gpuCalc = MakeCalc(c);
            gpuCalc.GpuKernel = kernel;
            MandelbrotCalculator.UseGpuPerturbation = true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            gpuCalc.Calculate();
            long ms = sw.ElapsedMilliseconds;
            int[] gpuIter = (int[])gpuCalc.IterationBuffer.Clone();
            uint[] gpuColor = (uint[])gpuCalc.ColorBuffer.Clone();
            MandelbrotCalculator.UseGpuPerturbation = false;

            int n = W * H;
            var distinct = new HashSet<int>(cpuIter);
            int inSet = 0;
            for (int i = 0; i < n; i++) if (cpuIter[i] >= maxIter) inSet++;
            int disagree = 0, maxDelta = 0;
            for (int i = 0; i < n; i++)
            {
                int d = Math.Abs(gpuIter[i] - cpuIter[i]);
                if (d != 0) { disagree++; if (d > maxDelta) maxDelta = d; }
            }
            double frac = (double)disagree / n;
            long colourSum = 0;
            for (int i = 0; i < n; i++)
                for (int s = 0; s < 24; s += 8)
                    colourSum += Math.Abs((int)((gpuColor[i] >> s) & 0xFF) - (int)((cpuColor[i] >> s) & 0xFF));
            double colourMean = colourSum / (n * 3.0);

            Console.WriteLine(
                $"  {label}: refLen={cpuCalc.ReferenceOrbitLength} distinct={distinct.Count} inSet={inSet}/{n} " +
                $"gpu={gpuCalc.LastFrameUsedGpuPerturbation} route='{gpuCalc.LastGpuRoute.State} {gpuCalc.LastGpuRoute.Reason}' {ms} ms");
            Console.WriteLine(
                $"    iter disagree={disagree}/{n} ({frac:P3}) maxΔiter={maxDelta}; colour mean drift={colourMean:F3}");

            if (!gpuCalc.LastFrameUsedGpuPerturbation)
            {
                Console.Error.WriteLine($"    FAIL {label}: the GPU perturbation path did not run ({gpuCalc.LastGpuRoute.Detail ?? gpuCalc.LastGpuRoute.Reason}).");
                return false;
            }
            if (distinct.Count < 8 || inSet >= n)
            {
                Console.Error.WriteLine($"    FAIL {label}: degenerate CPU frame — comparison vacuous.");
                return false;
            }
            if (frac > MaxDisagreeFrac)
            {
                Console.Error.WriteLine($"    FAIL {label}: iteration disagreement {frac:P3} > {MaxDisagreeFrac:P0}.");
                return false;
            }
            if (colourMean > MaxMeanColourDrift)
            {
                Console.Error.WriteLine($"    FAIL {label}: colour mean drift {colourMean:F3} > {MaxMeanColourDrift}.");
                return false;
            }
            return true;
        }

        private static MandelbrotCalculator MakeCalc(
            (string Label, double Zoom, double[] Cx, double[] Cy, int MaxIter) c) => new(W, H)
        {
            Quality = QualityPreset.Extreme,   // HP + deep-path thresholds cover every case
            CenterX = c.Cx[0], CenterXLo = c.Cx[1], CenterX2 = c.Cx[2], CenterX3 = c.Cx[3],
            CenterY = c.Cy[0], CenterYLo = c.Cy[1], CenterY2 = c.Cy[2], CenterY3 = c.Cy[3],
            Zoom = c.Zoom,
            MaxIterations = c.MaxIter,
            ColorMap = new HsvPalette(),
        };
    }
}
