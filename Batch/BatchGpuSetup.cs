// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1611 — GPU compute for --batch. The live shell attaches its compute kernel in
// AvaloniaShellBootstrap from the window's renderer; --batch runs before that and
// has no renderer, so every batch render used to compute on the CPU (a user's
// benchmark found the RTX 3080 idle). This builds one kernel on a device of its own
// (D3D11 through BootstrapHooks.HeadlessGpuKernelFactoryHook on Windows, Vulkan
// elsewhere, chosen as the live shell chooses) and sets HeadlessGpu.Kernel, which
// the batch's calculators pick up as they are built. Deep-zoom perturbation keeps
// the live shell's opt-in (FF_GPU_PERTURB=1). One stderr line names the route.

using System;
using FracturingFog.Hosting;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Vulkan;

namespace FracturingFog.Batch
{
    internal static class BatchGpuSetup
    {
        /// <summary>Applies <c>--gpu</c>. Returns false (and an error) only for
        /// <c>--gpu on</c> with no GPU compute.</summary>
        public static bool Apply(BatchGpuMode mode, out string? error)
        {
            error = null;
            HeadlessGpu.Kernel = null;
            if (mode == BatchGpuMode.Off)
            {
                Console.Error.WriteLine("batch: compute on the CPU (--gpu off)");
                return true;
            }

            var (kernel, why) = CreateKernel();
            if (kernel == null)
            {
                if (mode == BatchGpuMode.On)
                {
                    error = $"--gpu on, but there is no GPU compute: {why}";
                    return false;
                }
                Console.Error.WriteLine($"batch: compute on the CPU: {why}");
                return true;
            }

            HeadlessGpu.Kernel = kernel;
            // Read every frame back inside its dispatch (no #1332 deferral): the kernel's
            // device-loss check then sees a frame that tripped the OS watchdog, and that
            // frame renders on the CPU rather than being written out wrong.
            GpuDeferredReadback.ProcessSetting = false;
            // Deep zoom (past MandelbrotCalculator.MaxGpuZoom) — the live shell's opt-in.
            bool perturb = IsTruthyEnv("FF_GPU_PERTURB");
            if (perturb) MandelbrotCalculator.UseGpuPerturbation = true;
            Console.Error.WriteLine($"batch: {GpuComputeSelection.FromAttachedKernel(kernel).Status}; deep zoom "
                + (perturb ? "on the GPU too (FF_GPU_PERTURB)" : "on the CPU (FF_GPU_PERTURB=1 moves it to the GPU)"));
            return true;
        }

        private static (IGpuKernel? Kernel, string Why) CreateKernel()
        {
            if (BootstrapHooks.HeadlessGpuKernelFactoryHook is { } d3d)
            {
                try
                {
                    var k = d3d();
                    return k != null ? (k, "") : (null, "no D3D11 hardware device");
                }
                catch (Exception ex)
                {
                    return (null, $"the D3D11 kernel failed to start ({ex.Message})");
                }
            }

            var decision = GpuComputeSelection.Decide(
                OperatingSystem.IsLinux(),   // Vulkan by default on Linux (#1455), as the live shell
                RendererBackend.Auto, hasD3dHook: false,
                GpuComputeSelection.ProbeDevices(),
                () => (DxcCompiler.Probe(out string d), d),
                GpuComputeSelection.AllowCpuFromEnvironment());
            if (!decision.UseGpu)
                return (null, decision.Status ?? "no GPU compute device");
            try
            {
                var k = VulkanComputeKernel.TryCreateWithOwnContext();
                return k != null ? (k, "") : (null, "the Vulkan device could not be opened");
            }
            catch (Exception ex)
            {
                return (null, $"the Vulkan kernel failed to start ({ex.Message})");
            }
        }

        private static bool IsTruthyEnv(string name)
        {
            string? v = Environment.GetEnvironmentVariable(name)?.Trim();
            return v is not null && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)
                                     || v.Equals("yes", StringComparison.OrdinalIgnoreCase)
                                     || v.Equals("on", StringComparison.OrdinalIgnoreCase));
        }
    }
}
