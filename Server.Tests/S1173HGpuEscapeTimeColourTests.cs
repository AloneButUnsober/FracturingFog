// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1173-H / GPU parity plan G4.2 — the GPU palette path already served Julia /
// Burning Ship / Tricorn (EscapeTimeCalculator.TryDispatchGpu, "T3.1 phase 4"); the
// "phase 5" note in MandelbrotCalculator was stale. What made their GPU frames differ
// from the CPU's was the smooth iteration count:
//   * the SP shader computed nu = log2(ln|z|), the CPU (and the perturbation shader)
//     log2(log2|z|) — every GPU pixel 0.529 (= −log2 ln 2) off, Mandelbrot included;
//   * EscapeTimeCalculator ran the GPU at a hardcoded bailout radius 2 (CPU 512), so the
//     escape iteration differed nearly everywhere and the smooth term was unconverged.
// Measured on a GT 710 (GPU-palette theme): mean drift Mandelbrot 5.00 → 0.42, Julia
// 6.33 → 1.43, Tricorn 6.44 → 0.35, Burning Ship 8.73 → 4.22 (its residual is fp32
// chaos on ~3% boundary pixels; per-pixel smooth otherwise agrees).
//
// Runs on the host's Vulkan device (the same HLSL as D3D11, via DXC); skipped without one.

using System;
using System.Linq;
using System.Threading;

using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1173HGpuEscapeTimeColourTests
{
    private const int W = 320, H = 240, MaxIter = 256;

    private static (uint[] color, int[] iter, float[] smooth, GpuRoute route) Render(
        FractalType kind, IColorMap map, VulkanComputeKernel? gpu)
    {
        var c = new EscapeTimeCalculator(W, H)
        {
            FractalType = kind, ColorMap = map, MaxIterations = MaxIter, Zoom = 1.0,
            UseGpuCompute = gpu != null, GpuKernel = gpu,
        };
        c.Calculate(CancellationToken.None);
        return ((uint[])c.ColorBuffer.Clone(), (int[])c.IterationBuffer.Clone(), (float[])c.SmoothBuffer.Clone(), c.LastGpuRoute);
    }

    private static double MeanDrift(uint[] a, uint[] b)
    {
        long sum = 0;
        for (int i = 0; i < a.Length; i++)
            for (int s = 0; s < 24; s += 8)
                sum += Math.Abs((int)((a[i] >> s) & 0xFF) - (int)((b[i] >> s) & 0xFF));
        return sum / (a.Length * 3.0);
    }

    private static VulkanComputeKernel? Device()
    {
        var k = VulkanComputeKernel.TryCreateWithOwnContext();
        if (k is null) Assert.Skip("no Vulkan device on this host");
        return k;
    }

    [Theory]
    [InlineData(FractalType.Mandelbrot, 1.0)]
    [InlineData(FractalType.Julia, 2.0)]
    [InlineData(FractalType.Tricorn, 1.0)]
    [InlineData(FractalType.BurningShip, 5.0)]   // fp32 chaos on its boundary filaments
    public void Gpu_Frame_Matches_The_Cpu_Frame(FractalType kind, double bound)
    {
        using var k = Device();
        var theme = ColorPalette.BuiltIns.First(m => m is FracturingFog.Interefaces.IGpuHlslPalette);
        var g = Render(kind, theme, k);
        var c = Render(kind, theme, null);

        Assert.Equal(GpuRouteState.Gpu, g.route.State);
        Assert.True(string.IsNullOrEmpty(g.route.Detail), $"{kind}: expected GPU colouring, got '{g.route.Detail}'");

        // The smooth iteration agrees where the escape iteration does (it was +0.529 everywhere).
        var d = Enumerable.Range(0, g.iter.Length)
            .Where(i => g.iter[i] == c.iter[i] && g.iter[i] < MaxIter)
            .Select(i => (double)Math.Abs(g.smooth[i] - c.smooth[i])).OrderBy(v => v).ToArray();
        Assert.True(d.Length > g.iter.Length / 2, $"{kind}: only {d.Length} escaped pixels share their iteration (bailout mismatch?)");
        double median = d[d.Length / 2];
        Assert.True(median < 0.02, $"{kind}: median |smooth GPU − CPU| {median:F3}");

        double drift = MeanDrift(g.color, c.color);
        Assert.True(drift < bound, $"{kind}: GPU vs CPU mean colour drift {drift:F2} (bound {bound})");
    }

    [Fact]
    public void Theme_Without_A_Gpu_Palette_Colours_On_The_Cpu_And_Says_So()
    {
        using var k = Device();
        var theme = ColorPalette.BuiltIns.First(m => m is not FracturingFog.Interefaces.IGpuHlslPalette);
        var g = Render(FractalType.Julia, theme, k);
        Assert.Equal(GpuRouteState.Gpu, g.route.State);
        Assert.Contains("colouring on the CPU", g.route.Detail);
    }
}
