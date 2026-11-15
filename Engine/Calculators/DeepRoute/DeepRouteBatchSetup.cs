// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Calculators/DeepRoute/DeepRouteBatchSetup.cs
// #1788 (#1665 S4) — the deep-zoom route for --batch (Batch/BatchDeepRouteSetup calls it;
// here so the tests reach it):
//   • --gpu-deep auto|on|off: the live view's Control Center toggle (on / off beat
//     FF_GPU_PERTURB and the measured route; auto keeps them);
//   • --deep-route-calibration auto|off|force: use the stored per-device calibration,
//     ignore it, or run a pass first (on a GPU device of its own; CPU only with --gpu off)
//     and store it. A batch never runs a pass by itself: its renders must not change under it.

using System;
using FracturingFog.Batch;
using FracturingFog.Rendering;

namespace FracturingFog
{
    public static class DeepRouteBatchSetup
    {
        /// <summary>Applies the flags. False (and an error) for --gpu-deep on with --gpu off.
        /// <paramref name="ownKernel"/> opens the GPU device a forced pass measures on.</summary>
        public static bool Apply(BatchGpuDeepMode gpuDeep, BatchDeepRouteMode deepRoute, BatchGpuMode gpu,
            Func<IGpuKernel?>? ownKernel, Action<string> log, out string? error)
        {
            ArgumentNullException.ThrowIfNull(log);
            error = null;
            if (gpuDeep == BatchGpuDeepMode.On && gpu == BatchGpuMode.Off)
            {
                error = "--gpu-deep on needs a GPU: it conflicts with --gpu off.";
                return false;
            }
            switch (deepRoute)
            {
                case BatchDeepRouteMode.Off: MandelbrotCalculator.UseDeepRouteCalibration = false; break;
                case BatchDeepRouteMode.Force: MandelbrotCalculator.UseDeepRouteCalibration = true; break;
            }
            if (gpuDeep != BatchGpuDeepMode.Auto)
                MandelbrotCalculator.SetGpuPerturbationFromUser(gpuDeep == BatchGpuDeepMode.On);

            if (deepRoute == BatchDeepRouteMode.Force)
            {
                var saved = DeepRouteCalibration.GpuKernelFactory;
                DeepRouteCalibration.GpuKernelFactory = gpu == BatchGpuMode.Off ? null : ownKernel;
                try { DeepRouteCalibration.RunNow(log: m => log("deep-route calibration: " + m)); }
                finally { DeepRouteCalibration.GpuKernelFactory = saved; }
            }
            log("deep route: " + FractalRenderHost.BuildDeepRouteSummary(null).Replace('\n', ' '));
            return true;
        }

        /// <summary>Where deep frames go on <paramref name="kernel"/> and why
        /// (FF_GPU_PERTURB, --gpu-deep, the measured route, the default): the batch's
        /// "GPU opened" line.</summary>
        public static string DeepZoomLine(IGpuKernel? kernel)
        {
            var d = new MandelbrotCalculator(1, 1) { GpuKernel = kernel }.DescribeDeepRoute();
            string src = d.GpuDeepSource switch
            {
                DeepRouteSource.Env => "FF_GPU_PERTURB",
                DeepRouteSource.Setting => "explicitly on",
                DeepRouteSource.Toggle => "--gpu-deep",
                DeepRouteSource.Measured => "measured on this device",
                DeepRouteSource.Latch => "switched off this run",
                DeepRouteSource.BackendDefault => "Vulkan default",
                _ => "default; --gpu-deep on or FF_GPU_PERTURB=1 moves it to the GPU",
            };
            return $"deep zoom on the {(d.GpuDeep ? "GPU" : "CPU")} ({src})";
        }
    }
}
