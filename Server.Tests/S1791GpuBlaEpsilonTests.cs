// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1791 — past 1e27 the GPU SA / BLA kernel (CSPerturbSA) disagreed with the CPU about 10x
// as often as the CPU BLA rows did. Cause: in the QD band (1e25..1e50) the CPU rows build
// their BLA table at ε ≤ QdBandBlaTolerance (1e-10, #1613), because they resume rebased
// pixels with BLA, and the default 1e-6 put ~2.5 % of a p448 minibrot's pixels a few
// iterations off. The GPU kernel rebases with BLA too, but its table kept 1e-6.
// Checked on p448 at 1e27, 1e31 and 5e33 against the plain perturbation frame (no SA, no
// BLA, no LA): the GPU kernel's iteration counts must be off on no more pixels than
// max(1 %, 2x the CPU BLA rows'). Before the fix it was off on 3–5 % against the CPU
// rows' 0.3 %. Mutant (the GPU table back at the default ε): caught on all three views.

using System;
using System.Threading;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // writes UseGpu*
public sealed class S1791GpuBlaEpsilonTests(ITestOutputHelper output) : IDisposable
{
    private const int W = 96, H = 64, MaxIter = 13440;
    private readonly bool _gp = MandelbrotCalculator.UseGpuPerturbation, _sa = MandelbrotCalculator.UseGpuSeriesApproximation,
        _bla = MandelbrotCalculator.UseGpuBla, _la = MandelbrotCalculator.UseGpuLa;
    private readonly double _budget = MandelbrotKernelSource.PerturbBudgetMs;
    private readonly GpuDeepPrecision _prec = MandelbrotCalculator.GpuDeepPrecisionMode;

    public void Dispose()
    {
        MandelbrotCalculator.UseGpuPerturbation = _gp;
        MandelbrotCalculator.UseGpuSeriesApproximation = _sa;
        MandelbrotCalculator.UseGpuBla = _bla;
        MandelbrotCalculator.UseGpuLa = _la;
        MandelbrotCalculator.GpuDeepPrecisionMode = _prec;
        MandelbrotKernelSource.PerturbBudgetMs = _budget;
    }

    // The bench-kit p448 minibrot centre (S1660 / S1782's view), at three depths.
    private static readonly double[] Cx = [-0.7145519151951202, -2.0790192891565988e-17, 1.1753166641509792e-33, -8.102457136962753e-50];
    private static readonly double[] Cy = [-0.35402073332318235, 2.5477179573089096e-17, -6.072997541261718e-34, -1.8911088656798612e-50];

    private static MandelbrotCalculator Frame(double zoom, Action<MandelbrotCalculator> setup, IGpuKernel? k = null)
    {
        var c = new MandelbrotCalculator(W, H)
        {
            Quality = QualityPreset.Extreme,
            CenterX = Cx[0], CenterXLo = Cx[1], CenterX2 = Cx[2], CenterX3 = Cx[3],
            CenterY = Cy[0], CenterYLo = Cy[1], CenterY2 = Cy[2], CenterY3 = Cy[3],
            Zoom = zoom, MaxIterations = MaxIter, ColorMap = new HsvPalette(), GpuKernel = k,
        };
        setup(c);
        c.Calculate(CancellationToken.None);
        return c;
    }

    private static int Off(MandelbrotCalculator want, MandelbrotCalculator got)
    {
        int off = 0;
        for (int i = 0; i < W * H; i++)
            if (got.IterationBuffer[i] != want.IterationBuffer[i]) off++;
        return off;
    }

    [Theory]
    [InlineData(1e27)]
    [InlineData(1e31)]
    [InlineData(5.176068e33)]
    public void The_Gpu_Bla_Kernel_Agrees_As_Well_As_The_Cpu_Rows(double zoom)
    {
        var k = VulkanComputeKernel.TryCreateWithOwnContext();
        if (k is null) Assert.Skip("no Vulkan device on this host");
        using var _ = k;
        if (!k.SupportsPerturbationSA) Assert.Skip("the Vulkan device lacks the SA / BLA kernel");
        MandelbrotKernelSource.PerturbBudgetMs = 0;
        MandelbrotCalculator.UseGpuPerturbation = false;
        var plain = Frame(zoom, c => { c.DisableAcceleration = true; c.LaMode = DeepLaMode.Off; });
        var cpu = Frame(zoom, c => c.LaMode = DeepLaMode.Off);   // the CPU SA + BLA rows
        MandelbrotCalculator.UseGpuPerturbation = true;
        MandelbrotCalculator.UseGpuSeriesApproximation = true;
        MandelbrotCalculator.UseGpuBla = true;
        MandelbrotCalculator.UseGpuLa = false;
        MandelbrotCalculator.GpuDeepPrecisionMode = GpuDeepPrecision.Double;
        var gpu = Frame(zoom, c => c.LaMode = DeepLaMode.Off, k);
        Assert.True(gpu.LastFrameUsedGpuBla, $"precondition: the GPU BLA kernel ran ({gpu.LastGpuRoute.Detail})");

        int cpuOff = Off(plain, cpu), gpuOff = Off(plain, gpu);
        output.WriteLine($"{zoom:G3}: CPU rows off at {cpuOff}, GPU SA+BLA off at {gpuOff} of {W * H} (GPU ε {gpu.LastBlaEpsilon:G2})");
        int allow = Math.Max(W * H / 100, 2 * cpuOff);
        Assert.True(gpuOff <= allow, $"{zoom:G3}: GPU SA+BLA off at {gpuOff}, CPU rows at {cpuOff} (allow {allow})");
    }
}
