// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// V6 (#82): --vulkanpturbcalc. End-to-end gate for the CALCULATOR wiring of the
// GPU perturbation path. Where --vulkanpturbprobe drives the kernel object in
// isolation, this drives a real MandelbrotCalculator at deep zoom BOTH ways —
// CPU (UseGpuPerturbation off) and GPU (a VulkanComputeKernel attached,
// UseGpuPerturbation on) — and compares the iteration frames.
//
// The two paths are NOT bit-identical: the CPU deep path runs SIMD
// perturbation with a rebased scalar fallback only for glitched lanes, while
// the GPU runs the rebased loop for EVERY pixel. Those agree except on the
// handful of filament pixels at the escape-time knife-edge (boundary chaos,
// doc §2), so the gate is on the disagreement FRACTION, same ULP-band
// philosophy as the other probes.

using System;
using FracturingFog;                       // MandelbrotCalculator
using FracturingFog.Models;                // QualityPreset
using FracturingFog.Rendering.Vulkan;      // VulkanComputeKernel, VulkanContext

namespace FracturingFog.Rendering.Vulkan.Smoke;

internal static class PerturbCalcProbe
{
    private const int W = 128, H = 128;
    private const int MaxIter = 3000;
    private const double Zoom = 1e14;   // > HPZoomThreshold (1e12) → CalculateHighPrecision
    private const double CenterX = -0.743643887037151, CenterY = 0.13182590420533;
    private const double MaxDisagreeFrac = 0.02;

    public static int Run(VulkanContext ctx)
    {
        using var kernel = new VulkanComputeKernel(ctx);
        if (!kernel.SupportsPerturbation)
        {
            Console.WriteLine(
                $"vulkanpturbcalc SKIP: {ctx.PickedType} {ctx.PickedName} has no shaderFloat64 " +
                "(GPU perturbation path unavailable — finding for #82 checkbox 4).");
            return 0;
        }

        // ── CPU reference frame (deep path, GPU perturbation OFF). ────────────
        var cpuCalc = MakeCalc();
        MandelbrotCalculator.UseGpuPerturbation = false;
        cpuCalc.GpuKernel = null;
        cpuCalc.Calculate();
        int[] cpu = (int[])cpuCalc.IterationBuffer.Clone();

        // ── GPU frame (same view, VulkanComputeKernel attached, path ON). ────
        var gpuCalc = MakeCalc();
        gpuCalc.GpuKernel = kernel;
        MandelbrotCalculator.UseGpuPerturbation = true;
        gpuCalc.Calculate();
        int[] gpu = (int[])gpuCalc.IterationBuffer.Clone();
        MandelbrotCalculator.UseGpuPerturbation = false;

        int n = W * H;
        var distinct = new System.Collections.Generic.HashSet<int>(cpu);
        int inSet = 0; for (int i = 0; i < n; i++) if (cpu[i] >= MaxIter) inSet++;

        int disagree = 0, maxDelta = 0;
        for (int i = 0; i < n; i++)
        {
            int d = Math.Abs(gpu[i] - cpu[i]);
            if (d != 0) { disagree++; if (d > maxDelta) maxDelta = d; }
        }
        double frac = (double)disagree / n;

        Console.WriteLine(
            $"vulkanpturbcalc {W}x{H} zoom={Zoom:0e+0} maxIter={MaxIter} refLen={cpuCalc.ReferenceOrbitLength}");
        Console.WriteLine(
            $"  non-degeneracy: distinct={distinct.Count} inSet={inSet}/{n}");
        Console.WriteLine(
            $"  GPU-perturb frame vs CPU deep frame: disagree={disagree}/{n} ({frac:P3}) maxΔiter={maxDelta} (boundary chaos)");

        bool nonDegenerate = distinct.Count >= 8 && inSet < n;
        if (!nonDegenerate)
        {
            Console.Error.WriteLine("vulkanpturbcalc FAIL: degenerate CPU frame — comparison vacuous.");
            return 1;
        }
        if (frac > MaxDisagreeFrac)
        {
            Console.Error.WriteLine(
                $"vulkanpturbcalc FAIL: GPU-perturb frame diverged from the CPU deep frame " +
                $"beyond the band ({frac:P3} > {MaxDisagreeFrac:P0}).");
            return 1;
        }

        Console.WriteLine($"vulkanpturbcalc OK: {ctx.PickedType} {ctx.PickedName}");
        return 0;
    }

    private static MandelbrotCalculator MakeCalc()
    {
        var c = new MandelbrotCalculator(W, H)
        {
            Quality = QualityPreset.High,   // AllowHighPrecision, HPZoomThreshold 1e12
            CenterX = CenterX,
            CenterY = CenterY,
            Zoom = Zoom,
            MaxIterations = MaxIter,
            ColorMap = new HsvPalette(),
        };
        return c;
    }
}
