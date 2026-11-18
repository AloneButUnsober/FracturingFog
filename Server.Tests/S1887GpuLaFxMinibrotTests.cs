// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1887 — the float-exp LA GPU kernel (#1664) on minibrot views. Before the fix it returned
// the cap for every pixel from ~1e77: near a nucleus the reference orbit passes within
// |Z_p| < FLT_MIN (1.2e-38) of 0 at every period (Z_66 ≈ 5.8e-40 on the p66 view at 1e78),
// and the kernel kept Z_n and each LA step's Ref as plain floats — denormal (flushed to 0
// on GPUs), so δ' = (2·Z + δ)·δ + dc lost its 2·Z·δ term. (Found by a C# emulation of the
// kernel's float arithmetic on that pixel: 6,600 against the CPU's 524.) Now Z_n and Ref
// are float-exp. Checked on the Vulkan device (skipped without one):
//   • S1864's antenna minibrots of periods 40 … 415 (~1e46 … ~1e498; past 1e290 on C6b's
//     float-exp-pixel route), at their home framing: the GPU frame against the CPU frame
//     within the #1664 budget (counts differ on ≤ 1.35 % of pixels, in / out ≤ 0.5 %) and no
//     count more than 1 % off, with the AT (C5g) and without. (An AT whose escape radius²
//     is past FLT_MAX, which #1886 clamped, put ~1.3 % of these frames' pixels off by up to
//     95 %: the kernel now runs no AT there.)
//   • the packed reference: each entry decodes to Z_n within a float rounding, including
//     |Z_n| far below FLT_MIN.

using System;
using System.Linq;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1887RefPackingTests
{
    [Theory]
    [InlineData(1.25, -0.5)]
    [InlineData(5.77e-40, 0.0)]       // the p66 view's Z_66: a float denormal
    [InlineData(-3.1e-200, 2.2e-201)]
    [InlineData(0.0, 0.0)]
    public void A_Reference_Entry_Decodes_To_Z_Within_A_Float_Rounding(double re, double im)
    {
        var w = LaKernelSource.PackRefFloatExp(new[] { 0.5, re }, new[] { 0.0, im }, 2);
        Assert.Equal(2 * LaKernelSource.RefWordsFloatExp, w.Length);
        var (dr, di) = GpuFloatExp.Decode(BitConverter.UInt32BitsToSingle(w[3]), BitConverter.UInt32BitsToSingle(w[4]), (int)w[5]);
        double mag = Math.Max(Math.Abs(re), Math.Abs(im));
        Assert.True(Math.Abs(dr - re) <= mag * 1.2e-7 && Math.Abs(di - im) <= mag * 1.2e-7, $"{re},{im} -> {dr},{di}");
        if (mag > 0) Assert.InRange(Math.Max(Math.Abs(BitConverter.UInt32BitsToSingle(w[3])), Math.Abs(BitConverter.UInt32BitsToSingle(w[4]))), 1f, 1.9999999f);
    }
}

[Collection(MandelbrotGpuStaticsCollection.Name)]   // writes UseGpuPerturbation / UseGpuLa / UseGpuLaFloatExp / GpuDeepPrecisionMode
public sealed class S1887GpuLaFxMinibrotTests : IDisposable
{
    private const int W = 48, H = 36;
    private readonly ITestOutputHelper _out;
    private readonly double _savedBudget = MandelbrotKernelSource.PerturbBudgetMs;
    private readonly bool _savedPerturb = MandelbrotCalculator.UseGpuPerturbation, _savedLa = MandelbrotCalculator.UseGpuLa,
        _savedLaFx = MandelbrotCalculator.UseGpuLaFloatExp;
    private readonly GpuDeepPrecision _savedPrecision = MandelbrotCalculator.GpuDeepPrecisionMode;

    public S1887GpuLaFxMinibrotTests(ITestOutputHelper output)
    {
        _out = output;
        MandelbrotKernelSource.PerturbBudgetMs = 0;
    }

    public void Dispose()
    {
        MandelbrotKernelSource.PerturbBudgetMs = _savedBudget;
        MandelbrotCalculator.UseGpuPerturbation = _savedPerturb;
        MandelbrotCalculator.UseGpuLa = _savedLa;
        MandelbrotCalculator.UseGpuLaFloatExp = _savedLaFx;
        MandelbrotCalculator.GpuDeepPrecisionMode = _savedPrecision;
    }

    [Theory]
    [InlineData(40, true)]
    [InlineData(66, false)]
    [InlineData(66, true)]
    [InlineData(90, true)]
    [InlineData(125, true)]
    [InlineData(250, false)]
    [InlineData(250, true)]
    [InlineData(415, true)]
    public void The_Kernel_Renders_The_Minibrot_As_The_Cpu(int n, bool at)
    {
        if (MandelbrotCalculator.LaModeOverride != null) Assert.Skip("FF_LA set");
        using var k = VulkanComputeKernel.TryCreateWithOwnContext();
        if (k is null) Assert.Skip("no Vulkan device on this host");
        var (e, zl, _) = S1886LaAtFloatExpTests.Minibrot(n);
        int maxIter = n * 100;
        MandelbrotCalculator.UseGpuPerturbation = false;
        var cpu = S1886LaAtFloatExpTests.Render(e, zl, maxIter, at);
        MandelbrotCalculator.UseGpuPerturbation = true;
        MandelbrotCalculator.UseGpuLa = false;
        MandelbrotCalculator.GpuDeepPrecisionMode = GpuDeepPrecision.Fp32;
        MandelbrotCalculator.UseGpuLaFloatExp = true;
        var gpu = S1886LaAtFloatExpTests.Render(e, zl, maxIter, at, k);
        Assert.True(gpu.LastFrameUsedGpuPerturbation && gpu.LastFrameUsedGpuLaFloatExp,
            $"p{n}: the float-exp LA kernel did not run: {gpu.LastGpuRoute.Reason} / {gpu.LastGpuRoute.Detail}");
        int N = W * H;
        int differ = Enumerable.Range(0, N).Count(i => gpu.IterationBuffer[i] != cpu.IterationBuffer[i]);
        int inOut = Enumerable.Range(0, N).Count(i => (gpu.IterationBuffer[i] >= maxIter) != (cpu.IterationBuffer[i] >= maxIter));
        int inSet = cpu.IterationBuffer.Take(N).Count(v => v >= maxIter);
        // How far off: the largest relative count difference among escaping pixels.
        double maxRel = Enumerable.Range(0, N).Where(i => cpu.IterationBuffer[i] < maxIter)
            .Max(i => Math.Abs((double)gpu.IterationBuffer[i] - cpu.IterationBuffer[i]) / cpu.IterationBuffer[i]);
        _out.WriteLine($"p{n} 1e{zl:F1} AT {at}: differ {differ}/{N} (max relative {maxRel:E2}), in/out {inOut}, CPU in set {inSet}; " +
                       $"fx px {gpu.LastFrameUsedGpuFloatExpPixels}, fx table {gpu.LastFrameUsedGpuLaTableFx}, fx AT {gpu.LastFrameUsedGpuLaAtFx}");
        Assert.True(inSet > 0 && inSet < N, "precondition: in-set and escaping pixels");
        Assert.True(differ <= N * 135 / 10000, $"p{n}: counts differ on {differ}/{N}");
        // The fp32 mantissa's error, not a wrong path: no count more than 1 % off.
        Assert.True(maxRel < 0.01, $"p{n}: a count {maxRel:P1} off");
        // The kernel's AT is the CPU's where its Esc² fits a float, none where it doesn't (#1887).
        if (gpu.LastFrameUsedGpuLaAtFx) Assert.True(gpu.LastLaAtFx is { } a && a.Esc2 <= float.MaxValue);
        Assert.True(inOut <= N / 200, $"p{n}: in/out {inOut}");
    }
}
