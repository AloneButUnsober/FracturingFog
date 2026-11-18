// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1882 (epic #1428, C6b) — the float-exp LA GPU kernel past 1e290, with a float-exp pixel
// scale (mantissa · 2^exponent) where double underflows. Checked:
//   • the cbuffer: a scale given as (m, k) packs to the same words as the double m · 2^k
//     wherever double holds it; past double the exponent is kept (ILogB(m) + k); past the
//     kernel's exponent range Make refuses;
//   • on the Vulkan device (skipped without one), S1842's views: c = i (a Misiurewicz
//     point, structure at every depth) at 1e320, 1e600 and 1e1000, and a centre whose
//     offsets OD can't hold (1e600): the frame runs on the kernel, and its counts against
//     the ground truth (each sampled pixel iterated directly in BigFloat at the zoom's
//     precision, bailout 512) are at most one pixel off (the fp32 mantissa, #1664), the
//     CPU float-exp frame exact on all (the oracle's own check); against the CPU frame on
//     every pixel within the #1664 budget; escaped pixels' smooth values in
//     [n − 3.17, n − 2.17] as on the CPU (S1842);
//   • an AA pixel jitter (#1851) moves the GPU frame as it moves the CPU's: the jittered
//     frames agree, and differ from the unjittered one;
//   • with LA off the frame stays on the CPU rows, and the route says why.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FracturingFog.FFMath;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1882FloatExpScaleParamsTests
{
    [Theory]
    [InlineData(1.0, 0)]
    [InlineData(1.75, -40)]
    [InlineData(1.3, -1000)]
    [InlineData(1.9999999999999998, -900)]
    public void A_Split_Scale_Packs_As_Its_Double(double m, int k)
    {
        var at = default(GpuLaAt);
        var split = GpuLaFxParams.Make(64, 36, m, 1000, 512 * 512, -32, -18, 100, 3, at, k);
        Assert.Equal(Math.ILogB(m) + k, split.ScaleE);
        Assert.Equal((float)Math.ScaleB(m, -Math.ILogB(m)), split.ScaleM);
        double d = Math.ScaleB(m, k);
        if (d > 0 && Math.ILogB(d) == Math.ILogB(m) + k)   // double holds it (not subnormal)
        {
            var whole = GpuLaFxParams.Make(64, 36, d, 1000, 512 * 512, -32, -18, 100, 3, at);
            Assert.Equal((whole.ScaleM, whole.ScaleE), (split.ScaleM, split.ScaleE));
        }
    }

    [Fact]
    public void Past_The_Kernels_Exponent_Range_Make_Refuses()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GpuLaFxParams.Make(8, 8, 1.5, 100, 4, 0, 0, 10, 1, default, -GpuLaFxParams.MaxScaleExponent - 1));
        var ok = GpuLaFxParams.Make(8, 8, 1.5, 100, 4, 0, 0, 10, 1, default, -GpuLaFxParams.MaxScaleExponent);
        Assert.Equal(-GpuLaFxParams.MaxScaleExponent, ok.ScaleE);
    }
}

[Collection(MandelbrotGpuStaticsCollection.Name)]   // writes UseGpuPerturbation / UseGpuLa / UseGpuLaFloatExp / GpuDeepPrecisionMode
public sealed class S1882GpuFloatExpPixelsTests : IDisposable
{
    private const int W = 24, H = 24;

    private readonly ITestOutputHelper _out;
    private readonly double _savedBudget = MandelbrotKernelSource.PerturbBudgetMs;
    private readonly bool _savedPerturb = MandelbrotCalculator.UseGpuPerturbation, _savedLa = MandelbrotCalculator.UseGpuLa,
        _savedLaFx = MandelbrotCalculator.UseGpuLaFloatExp;
    private readonly GpuDeepPrecision _savedPrecision = MandelbrotCalculator.GpuDeepPrecisionMode;

    public S1882GpuFloatExpPixelsTests(ITestOutputHelper output)
    {
        _out = output;
        MandelbrotKernelSource.PerturbBudgetMs = 0;   // as S1664 / S1880: no too-slow abort under load
    }

    public void Dispose()
    {
        MandelbrotKernelSource.PerturbBudgetMs = _savedBudget;
        MandelbrotCalculator.UseGpuPerturbation = _savedPerturb;
        MandelbrotCalculator.UseGpuLa = _savedLa;
        MandelbrotCalculator.UseGpuLaFloatExp = _savedLaFx;
        MandelbrotCalculator.GpuDeepPrecisionMode = _savedPrecision;
    }

    private static VulkanComputeKernel Device()
    {
        var k = VulkanComputeKernel.TryCreateWithOwnContext();
        if (k is null) Assert.Skip("no Vulkan device on this host");
        Assert.True(k.SupportsPerturbationLaFloatExp);
        return k;
    }

    // S1842's views: c = i, or a centre near it whose offsets (≈1e-(zl-40)) OD can't hold.
    private static ExactCentre Centre(int zl, bool tail)
    {
        int prec = BigFloat.PrecisionForZoom(zl);
        var re = tail ? BigFloat.Parse("3.1415926535e-" + (zl - 40), prec) : BigFloat.Zero(prec);
        var im = tail ? BigFloat.Add(BigFloat.FromDouble(1, prec), BigFloat.Parse("2.7182818284e-" + (zl - 45), prec), prec)
                      : BigFloat.FromDouble(1, prec);
        return new ExactCentre(re, im);
    }

    // Escapes here run to ~3 per decade (1e600: up to 1,800): room to spare.
    private static int MaxIt(int zl) => 600 + 4 * zl;

    private static MandelbrotCalculator Render(int zl, bool tail, IGpuKernel? k, DeepLaMode mode = DeepLaMode.Auto,
                                               (double X, double Y) jitter = default)
    {
        var centre = Centre(zl, tail);
        var d = centre.ToDeep();
        var calc = new MandelbrotCalculator(W, H)
        {
            Quality = QualityPreset.Extreme, MaxIterations = MaxIt(zl), ColorMap = new HsvPalette(), GpuKernel = k, LaMode = mode,
            CenterX = d.Re.X0, CenterXLo = d.Re.X1, CenterX2 = d.Re.X2, CenterX3 = d.Re.X3,
            CenterX4 = d.Re.X4, CenterX5 = d.Re.X5, CenterX6 = d.Re.X6, CenterX7 = d.Re.X7,
            CenterY = d.Im.X0, CenterYLo = d.Im.X1, CenterY2 = d.Im.X2, CenterY3 = d.Im.X3,
            CenterY4 = d.Im.X4, CenterY5 = d.Im.X5, CenterY6 = d.Im.X6, CenterY7 = d.Im.X7,
        };
        calc.SetZoomLog10(zl);
        calc.SetDepth(centre);
        calc.PixelJitter = jitter;
        calc.Calculate(CancellationToken.None);
        return calc;
    }

    private static void GpuOn()
    {
        MandelbrotCalculator.UseGpuPerturbation = true;
        MandelbrotCalculator.UseGpuLa = false;
        MandelbrotCalculator.GpuDeepPrecisionMode = GpuDeepPrecision.Fp32;
        MandelbrotCalculator.UseGpuLaFloatExp = true;
    }

    // Escape count of c = centre + (ox, oy)·pixel, iterated directly in BigFloat (S1842's oracle).
    private static int Direct(ExactCentre centre, double ox, double oy, BigFloat pixel, int prec, int maxIter)
    {
        var cr = BigFloat.Add(centre.Re.WithPrecision(prec), BigFloat.Mul(pixel, BigFloat.FromDouble(ox, prec), prec), prec);
        var ci = BigFloat.Add(centre.Im.WithPrecision(prec), BigFloat.Mul(pixel, BigFloat.FromDouble(oy, prec), prec), prec);
        BigFloat zr = BigFloat.Zero(prec), zi = BigFloat.Zero(prec);
        for (int n = 0; n < maxIter; n++)
        {
            double a = zr.ToDouble(), b = zi.ToDouble();
            if (a * a + b * b >= 512.0 * 512.0) return n;
            var zr2 = BigFloat.Mul(zr, zr, prec); var zi2 = BigFloat.Mul(zi, zi, prec); var zri = BigFloat.Mul(zr, zi, prec);
            zi = BigFloat.Add(zri.ScaleB(1), ci, prec);
            zr = BigFloat.Add(BigFloat.Sub(zr2, zi2, prec), cr, prec);
        }
        return maxIter;
    }

    [Theory]
    [InlineData(320, false, 3)]
    [InlineData(600, false, 3)]
    [InlineData(1000, false, 4)]
    [InlineData(600, true, 3)]
    public void Past_1e290_The_Kernel_Renders_With_A_Float_Exp_Scale(int zl, bool tail, int stride)
    {
        if (MandelbrotCalculator.LaModeOverride != null) Assert.Skip("FF_LA set");
        using var k = Device();
        MandelbrotCalculator.UseGpuPerturbation = false;
        var cpu = Render(zl, tail, null);
        Assert.Contains("FX", cpu.LastPrecisionLabel);
        Assert.False(cpu.LastFrameUsedGpuPerturbation, "precondition: the CPU frame");
        GpuOn();
        var gpu = Render(zl, tail, k);
        Assert.True(gpu.LastFrameUsedGpuFloatExpPixels && gpu.LastFrameUsedGpuPerturbation && gpu.LastFrameUsedGpuLaTableFx,
            $"1e{zl}: the kernel did not run: {gpu.LastGpuRoute.Reason} / {gpu.LastGpuRoute.Detail}");
        Assert.Contains("FX", gpu.LastPrecisionLabel);
        Assert.Contains("LA", gpu.LastPrecisionLabel);

        int prec = BigFloat.PrecisionForZoom(zl);
        var centre = Centre(zl, tail);
        var pixel = BigFloat.Div(BigFloat.Parse($"3.5e-{zl}", prec), BigFloat.FromDouble(Math.Max(W, H), prec), prec);
        var pts = (from y in Enumerable.Range(0, H) where y % stride == 0
                   from x in Enumerable.Range(0, W) where x % stride == 0 select y * W + x).ToArray();
        var want = new int[pts.Length];
        int maxIt = MaxIt(zl);
        Parallel.For(0, pts.Length, q => want[q] = Direct(centre, pts[q] % W - W * 0.5, pts[q] / W - H * 0.5, pixel, prec, maxIt));
        int cpuOff = 0, gpuOff = 0, esc = 0;
        for (int q = 0; q < pts.Length; q++)
        {
            if (cpu.IterationBuffer[pts[q]] != want[q]) cpuOff++;
            if (gpu.IterationBuffer[pts[q]] != want[q]) gpuOff++;
            if (want[q] < maxIt) esc++;
        }
        double dis = Enumerable.Range(0, W * H).Count(i => gpu.IterationBuffer[i] != cpu.IterationBuffer[i]) / (double)(W * H);
        int inOut = Enumerable.Range(0, W * H).Count(i => (gpu.IterationBuffer[i] >= maxIt) != (cpu.IterationBuffer[i] >= maxIt));
        _out.WriteLine($"1e{zl}{(tail ? " tail" : "")}: truth off CPU {cpuOff}, GPU {gpuOff} of {pts.Length} ({esc} escaping, counts {want.Min()}..{want.Max()}); " +
                       $"GPU vs CPU [{cpu.LastPrecisionLabel}] {dis:P2}, in/out {inOut}; GPU {k.LastDispatchMs:F0} ms");
        Assert.True(esc >= pts.Length / 2, "precondition: escaping pixels to compare");
        Assert.True(cpuOff == 0, $"oracle check: the CPU frame is off the truth at {cpuOff} px");
        Assert.True(gpuOff <= 1, $"1e{zl}: GPU off the truth at {gpuOff}/{pts.Length} px");
        Assert.True(dis <= 0.0135, $"1e{zl}: GPU vs CPU {dis:P2}");
        Assert.True(inOut <= 2, $"1e{zl}: in/out {inOut}");
        for (int i = 0; i < W * H; i++)
        {
            int n = gpu.IterationBuffer[i];
            if (n >= maxIt) continue;
            float sm = gpu.SmoothBuffer[i];
            Assert.True(float.IsFinite(sm) && sm >= n - 3.2 && sm <= n - 2.1, $"smooth {i}: {sm} for {n}");
        }
    }

    [Fact]
    public void A_Pixel_Jitter_Moves_The_Gpu_Frame_As_The_Cpu_Frame()
    {
        if (MandelbrotCalculator.LaModeOverride != null) Assert.Skip("FF_LA set");
        using var k = Device();
        var j = (0.37, -0.29);
        MandelbrotCalculator.UseGpuPerturbation = false;
        var cpu = Render(320, false, null, jitter: j);
        GpuOn();
        var gpu = Render(320, false, k, jitter: j);
        var plain = Render(320, false, k);
        Assert.True(gpu.LastFrameUsedGpuFloatExpPixels && plain.LastFrameUsedGpuFloatExpPixels);
        int Diff(MandelbrotCalculator a, MandelbrotCalculator b) => Enumerable.Range(0, W * H).Count(i => a.IterationBuffer[i] != b.IterationBuffer[i]);
        _out.WriteLine($"jittered GPU vs CPU {Diff(gpu, cpu)} px; jittered vs unjittered GPU {Diff(gpu, plain)} px of {W * H}");
        Assert.True(Diff(gpu, cpu) <= W * H * 135 / 10000, $"jittered GPU vs CPU {Diff(gpu, cpu)}");
        Assert.True(Diff(gpu, plain) >= W * H / 10, $"the jitter moved {Diff(gpu, plain)} px");
    }

    [Fact]
    public void With_La_Off_The_Frame_Stays_On_The_Cpu_Rows()
    {
        if (MandelbrotCalculator.LaModeOverride != null) Assert.Skip("FF_LA set");
        using var k = Device();
        GpuOn();
        var gpu = Render(320, false, k, DeepLaMode.Off);
        Assert.False(gpu.LastFrameUsedGpuPerturbation || gpu.LastFrameUsedGpuFloatExpPixels);
        Assert.EndsWith(" FX", gpu.LastPrecisionLabel);
        Assert.Contains("zoom > 1e290", gpu.LastGpuRoute.Reason ?? "");
        Assert.Contains("float-exp LA kernel", gpu.LastGpuRoute.Detail ?? "");
    }
}
