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
            HeadlessGpu.Kernel = null;   // also clears a pending KernelFactory
            HeadlessGpu.ReliefKernelFactory = null;
            HeadlessGpu.FroxelKernelFactory = null;
            if (mode == BatchGpuMode.Off)
            {
                Console.Error.WriteLine("batch: compute on the CPU (--gpu off)");
                return true;
            }

            // Read every frame back inside its dispatch (no #1332 deferral): the kernel's
            // device-loss check then sees a frame that tripped the OS watchdog, and that
            // frame renders on the CPU rather than being written out wrong.
            GpuDeferredReadback.ProcessSetting = false;
            // Deep zoom (past MandelbrotCalculator.MaxGpuZoom) — the live shell's opt-in.
            bool perturb = IsTruthyEnv("FF_GPU_PERTURB");
            if (perturb) MandelbrotCalculator.UseGpuPerturbation = true;
            InstallRaymarchKernels();   // #1620 — built on the first relief frame

            if (mode == BatchGpuMode.Auto)
            {
                // #1640 — open the device when a calculator first needs it.
                HeadlessGpu.KernelFactory = () => Open(perturb);
                return true;
            }

            // --gpu on opens now, so a missing device fails before any render.
            var (kernel, why) = CreateKernel();
            if (kernel == null)
            {
                error = $"--gpu on, but there is no GPU compute: {why}";
                return false;
            }
            HeadlessGpu.Kernel = kernel;
            ReportOpened(kernel, perturb);
            return true;
        }

        private static IGpuKernel? Open(bool perturb)
        {
            var (kernel, why) = CreateKernel();
            if (kernel == null) Console.Error.WriteLine($"batch: compute on the CPU: {why}");
            else ReportOpened(kernel, perturb);
            return kernel;
        }

        private static void ReportOpened(IGpuKernel kernel, bool perturb) =>
            Console.Error.WriteLine($"batch: {GpuComputeSelection.FromAttachedKernel(kernel).Status}; deep zoom "
                + (perturb ? "on the GPU too (FF_GPU_PERTURB)" : "on the CPU (FF_GPU_PERTURB=1 moves it to the GPU)"));

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

        // #1620 — the relief raymarch and froxel kernels, as the live shell picks them:
        // on Windows over the escape-time kernel's D3D11 device (it shares the handles
        // live too), elsewhere a Vulkan context of their own. Built when the first
        // relief frame asks (Relief2DGpuRaymarch), never for a flat render.
        private static void InstallRaymarchKernels()
        {
            var d3dRelief = BootstrapHooks.HeadlessReliefKernelFactoryHook;
            var d3dFroxel = BootstrapHooks.HeadlessFroxelKernelFactoryHook;
            bool d3d = BootstrapHooks.HeadlessGpuKernelFactoryHook != null;
            HeadlessGpu.ReliefKernelFactory = k => Build("relief raymarch",
                () => d3d ? d3dRelief?.Invoke(k) : ReliefRaymarchVulkanKernel.TryCreateWithOwnContext());
            HeadlessGpu.FroxelKernelFactory = k => Build("froxel volumetrics",
                () => d3d ? d3dFroxel?.Invoke(k) : FroxelVolumeVulkanKernel.TryCreateWithOwnContext());
        }

        private static T? Build<T>(string what, Func<T?> create) where T : class
        {
            try
            {
                var k = create();
                Console.Error.WriteLine(k != null
                    ? $"batch: {what} on the GPU"
                    : $"batch: {what} on the CPU: no GPU kernel for it on this device");
                return k;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"batch: {what} on the CPU: the GPU kernel failed to start ({ex.Message})");
                return null;
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
