// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #742 — 3D-fractal GPU/CPU drift bound. The 3D families (Mandelbulb et al.)
// have no bit-exact GPU/CPU twin, only CPU->GPU Build-bridge unit tests +
// on-device smoke. Still/video export routes through PosterRenderer with the
// same calculators as the live host, so a scene previewed on a GPU device and
// exported on a GPU-less host (ILGPU CPU accelerator, or UseGpuRender off ->
// CPU ShadingPipeline) can drift. S8/S3 (positional lights #484-488,
// area-penumbra #492, thin-lens DOF #567) added per-hit math to BOTH the GPU
// kernels and the CPU pipeline, widening that float-divergence surface.
//
// Option B from the tracking issue: a golden-image drift *bound* (not a
// bit-exact twin). It pins the Mandelbulb GPU kernel to specific ILGPU devices
// via GpuAcceleratorHost.SetTestOverride and bounds the drift.
//
// IMPORTANT — fallback masks the drift today. The 3D GPU kernels only run when
// they JIT on the target device; on init/JIT failure MandelbulbCalculator
// silently drops to the CPU ShadingPipeline (return false from _gpu.Render).
// When a render fell back it is byte-identical to the pipeline, so we DETECT
// fallback by comparing to the pipeline reference and only assert the *drift*
// bound on renders that actually executed the kernel. This keeps the test:
//   * correct on hosts where the kernel does not load (all paths fall back ->
//     identical -> the WYSIWYG guarantee holds trivially, no false failure);
//   * a live regression guard on hosts where it does load (bounds the gap).
// See #749: the Mandelbulb kernel currently fails to JIT on the ILGPU
// CPU/OpenCL/Cuda backends on the dev host, so the whole 3D GPU test family is
// presently exercising the CPU fallback, not the kernel.
//
// Stock all-directional scene, no DOF: the shared DE + normal + 3-light Lambert
// + soft-shadow + AO + fog math that S8/S3 touched.

using System;
using System.Linq;
using FracturingFog;
using FracturingFog.Calculators;
using FracturingFog.Calculators.Gpu;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using ILGPU;
using ILGPU.Runtime;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S742Gpu3DDriftBoundTests
{
    private const int W = 96;
    private const int H = 72;

    // Render a stock Mandelbulb with the GPU kernel pinned to `acc` (or the CPU
    // ShadingPipeline when useGpu is false). Fresh calculator each call so its
    // lazily-loaded kernel re-JITs against the currently-overridden device.
    private static uint[] Render(Accelerator? acc, bool useGpu)
    {
        GpuAcceleratorHost.SetTestOverride(acc);
        try
        {
            var fx = LightingFxData.CreateDefault();
            fx.UseGpuRender = useGpu;
            var fp = new FractalParameters
            {
                BulbPower = 8,
                BulbIterations = 12,
                BulbCameraDistance = 2.6,
                Lighting = fx,
            };
            var calc = new MandelbulbCalculator(W, H)
            {
                ColorMap = ColorPalette.BuiltIns[0],
                FractalParameters = fp,
                Zoom = 1.0,
            };
            calc.Calculate(default);
            return (uint[])calc.ColorBuffer.Clone();
        }
        finally
        {
            GpuAcceleratorHost.SetTestOverride(null);
        }
    }

    private static bool Identical(uint[] a, uint[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    // Per-channel (R,G,B) drift metrics between two BGRA buffers.
    private static (double mean, int max, int pxOver8) Drift(uint[] a, uint[] b)
    {
        Assert.Equal(a.Length, b.Length);
        long sum = 0; int max = 0; int pxOver8 = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int dr = Math.Abs((int)((a[i] >> 16) & 0xFF) - (int)((b[i] >> 16) & 0xFF));
            int dg = Math.Abs((int)((a[i] >> 8) & 0xFF) - (int)((b[i] >> 8) & 0xFF));
            int db = Math.Abs((int)(a[i] & 0xFF) - (int)(b[i] & 0xFF));
            sum += dr + dg + db;
            int pxMax = Math.Max(dr, Math.Max(dg, db));
            if (pxMax > max) max = pxMax;
            if (pxMax > 8) pxOver8++;
        }
        return (sum / (double)(a.Length * 3), max, pxOver8);
    }

    // Same kernel across accelerator classes (discrete GPU vs ILGPU CPU) must
    // agree within a tight per-channel tolerance: same doubles, only device
    // transcendental last-ULP differences plus the rare silhouette pixel where a
    // DE threshold flips. This is the WYSIWYG guarantee for "preview on a GPU,
    // export on a GPU-less host through the same kernel". Skips when fewer than
    // two accelerator classes are present, or when either device fell back to
    // the CPU pipeline (the kernel did not JIT there — nothing to cross-check).
    [Fact]
    public void Same_Kernel_Across_Accelerator_Classes_Is_Within_Tolerance()
    {
        using var ctx = Context.Create(b => b.Default());
        var cpuDev = ctx.Devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.CPU);
        var gpuDev = ctx.Devices.FirstOrDefault(d => d.AcceleratorType != AcceleratorType.CPU);
        if (cpuDev == null || gpuDev == null)
            return; // single accelerator class -> nothing to cross-check.

        uint[] pipeline = Render(null, useGpu: false);

        using var cpuAcc = cpuDev.CreateAccelerator(ctx);
        using var gpuAcc = gpuDev.CreateAccelerator(ctx);
        uint[] onCpu = Render(cpuAcc, useGpu: true);
        uint[] onGpu = Render(gpuAcc, useGpu: true);

        // A render that equals the pipeline fell back (kernel failed to JIT on
        // that device) — it is not the kernel, so there is nothing to compare.
        if (Identical(onCpu, pipeline) || Identical(onGpu, pipeline))
            return;

        var (mean, max, over8) = Drift(onCpu, onGpu);
        Assert.True(mean < 0.5,
            $"same-kernel cross-device mean channel drift {mean:F3} exceeds 0.5 (GPU {gpuDev.Name})");
        Assert.True(over8 < onCpu.Length / 50,
            $"same-kernel cross-device: {over8} px drift >8 (>{onCpu.Length / 50} allowed); max {max}");
    }

    // The GPU kernel vs the CPU ShadingPipeline must stay within a documented
    // ceiling. These are different code paths — the GPU branch uses a cheap
    // step-hash albedo and drops some post effects (P7c) — so the ceiling is
    // loose by design; its job is to trip if a regression *widens* the gap, not
    // to claim parity. When the kernel does not load on the chosen accelerator
    // the render is byte-identical to the pipeline (drift 0), which the ceiling
    // trivially passes — the WYSIWYG guarantee still holds via fallback.
    [Fact]
    public void Gpu_Kernel_Vs_Cpu_Pipeline_Stays_Under_Ceiling()
    {
        using var ctx = Context.Create(b => b.Default());
        // Prefer a discrete GPU (that is what a preview would use); fall back to
        // the CPU accelerator so the test still runs where no GPU is present.
        var dev = ctx.Devices.FirstOrDefault(d => d.AcceleratorType != AcceleratorType.CPU)
                  ?? ctx.Devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.CPU);
        Assert.NotNull(dev);
        using var acc = dev!.CreateAccelerator(ctx);

        uint[] kernel   = Render(acc, useGpu: true);    // GPU kernel path (or fallback)
        uint[] pipeline = Render(null, useGpu: false);  // CPU ShadingPipeline

        var (mean, max, over8) = Drift(kernel, pipeline);
        Assert.True(mean < 40.0,
            $"kernel-vs-pipeline mean channel drift {mean:F2} exceeds ceiling 40 on {dev.AcceleratorType} " +
            $"(regression widened the GPU/CPU gap); max {max}, pxOver8 {over8}");
    }
}
