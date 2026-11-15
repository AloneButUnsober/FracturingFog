// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1786 (#1665 S2): --deeproutecalprobe. Runs the deep-zoom route calibration pass
// (DeepRouteCalibrator, the real CalculatorDeepRouteRunner) on this machine and prints
// each scene's route times, iteration agreement and the choice. Per GPU backend:
// D3D11 (a hardware device of its own) and Vulkan; FF_DEEPCAL_BACKEND=d3d11|vulkan|cpu
// picks one. FF_DEEPCAL_BUDGET_S sets the budget (default the calibrator's).
// Measures only: FF_DEEPCAL_STORE=1 also writes the store. Exit 0 always.

using System;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Vulkan;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace FracturingFog
{
    internal static class DeepRouteCalProbe
    {
        public static int Run(string[] args)
        {
            string only = Environment.GetEnvironmentVariable("FF_DEEPCAL_BACKEND")?.ToLowerInvariant() ?? "";
            bool store = Environment.GetEnvironmentVariable("FF_DEEPCAL_STORE") == "1";
            TimeSpan? budget = double.TryParse(Environment.GetEnvironmentVariable("FF_DEEPCAL_BUDGET_S"), out double b) && b > 0
                ? TimeSpan.FromSeconds(b) : null;
            Console.WriteLine($"deeproutecalprobe: {DeepRouteStore.CpuKey}, {CalculatorDeepRouteRunner.DefaultWidth}x{CalculatorDeepRouteRunner.DefaultHeight}, " +
                $"budget {(budget ?? DeepRouteCalibrator.DefaultBudget).TotalSeconds:F0} s, store {(store ? "on" : "off")}");

            if (only is "" or "cpu") Pass("CPU only", null, budget, store);
            if (only is "" or "d3d11")
            {
                var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.None,
                    new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out ID3D11Device? device, out _, out ID3D11DeviceContext? context);
                if (hr.Failure || device == null || context == null) Console.WriteLine("  D3D11 SKIP: no hardware device");
                else
                {
                    try { Pass("D3D11", new MandelbrotGpuKernel(device, context, new object()), budget, store); }
                    finally { context.Dispose(); device.Dispose(); }
                }
            }
            if (only is "" or "vulkan")
            {
                VulkanComputeKernel? vk = null;
                try { vk = VulkanComputeKernel.TryCreateWithOwnContext(); } catch { }
                if (vk == null) Console.WriteLine("  Vulkan SKIP: no device");
                else Pass("Vulkan", vk, budget, store);
            }
            Console.WriteLine("deeproutecalprobe done");
            return 0;
        }

        private static void Pass(string name, IGpuKernel? kernel, TimeSpan? budget, bool store)
        {
            Console.WriteLine($"  {name}: {kernel?.DeviceName ?? "-"}  key {kernel?.DeviceKey ?? "-"}");
            using var runner = new CalculatorDeepRouteRunner(kernel, ownsKernel: true);
            Console.WriteLine($"    candidates: {string.Join(", ", runner.Candidates)}");
            var r = DeepRouteCalibrator.Run(runner, budget: budget, store: store, log: m => Console.WriteLine("    " + m));
            Console.WriteLine($"    chose: LA from {r.Cpu.LaMinZoom:0e0}, LA SIMD {r.Cpu.LaSimd}" +
                (r.Gpu is { } g ? $", GPU deep {g.GpuDeep}, GPU LA {g.GpuLa}, kernel {g.Precision}" : "") +
                $"  ({r.ElapsedMs / 1000:F1} s{(r.Complete ? "" : ", stopped early")})");
        }
    }
}
