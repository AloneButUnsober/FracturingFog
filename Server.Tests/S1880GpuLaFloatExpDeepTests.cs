// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1880 (epic #1428, C6a) — the float-exp LA GPU kernel (#1664) past 1e100, up to the CPU's
// float-exp threshold (1e290). Checked, on the Vulkan device (skipped without one), on an
// exact view (the dense 340-digit centre near M(4,1), past what OD holds):
//   • at 1e150 and 1e285 the frame runs on the float-exp LA kernel (AP reference orbit):
//     at 1e150 with the double LA table; at 1e285 A and B overflow it (the CPU frame
//     drops LA there), so the kernel takes the float-exp table (C5c);
//   • its counts against the ground truth, each sampled pixel iterated directly in
//     BigFloat (z ← z² + c at the zoom's precision, bailout 512, as S1842): at most one
//     pixel off (the fp32 mantissa, #1664), the CPU frame exact on all of them (the
//     oracle's own check);
//   • against the CPU frame on every pixel: within the #1664 budget (S1664: the
//     float-exp LA frame at most 1 % + the plain float-exp kernel's, ~0.35 %);
//   • past 1e290 the frame stays on the CPU float-exp rows, and the route says why;
//   • a FloatExp encodes for the kernel as its double does wherever double holds it,
//     and FloatExp.Huge as +∞ (the double table's x / 0).

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

[Collection(MandelbrotGpuStaticsCollection.Name)]   // writes UseGpuPerturbation / UseGpuLa / UseGpuLaFloatExp / GpuDeepPrecisionMode
public sealed class S1880GpuLaFloatExpDeepTests : IDisposable
{
    private const string Mx = "-0.1010963638456221610257854457386225654638054428262534838769311776607808407404705842748212198105167790334045319085567411939715461442609118823557039076803016157584181434354544823068106664029419378267411891607819526448995396995334466823393126699009817557150425206538866991861344333961600610562775385144259609560703568221643279956133036926714760";
    private const string My = "0.9562865108091415007710960577299774358098333365105291700343143215005246590657167325269784107873398072043444724926469284366752406567465722656200815719741551313831228054884443810571677870203738395055477279309548311953190385170024877917670113105649462054553485859930615004427397447762045980085973915977603864276670744042253335354323566859404712";
    private const int W = 48, H = 27;

    private readonly ITestOutputHelper _out;
    private readonly double _savedBudget = MandelbrotKernelSource.PerturbBudgetMs;
    private readonly bool _savedPerturb = MandelbrotCalculator.UseGpuPerturbation, _savedLa = MandelbrotCalculator.UseGpuLa,
        _savedLaFx = MandelbrotCalculator.UseGpuLaFloatExp;
    private readonly GpuDeepPrecision _savedPrecision = MandelbrotCalculator.GpuDeepPrecisionMode;

    public S1880GpuLaFloatExpDeepTests(ITestOutputHelper output)
    {
        _out = output;
        MandelbrotKernelSource.PerturbBudgetMs = 0;   // as S1630 / S1664: no too-slow abort under load
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

    private static ExactCentre Centre()
    {
        Assert.True(ExactCentre.TryParse(Mx, My, out var c));
        return c;
    }

    private static int MaxIt(int zl) => 1000 + 12 * zl;   // escapes at ~8 per decade here

    private static MandelbrotCalculator Render(int zl, IGpuKernel? k)
    {
        var c = Centre();
        var d = c.ToDeep();
        var calc = new MandelbrotCalculator(W, H)
        {
            Quality = QualityPreset.Extreme, MaxIterations = MaxIt(zl), ColorMap = new HsvPalette(), GpuKernel = k,
            CenterX = d.Re.X0, CenterXLo = d.Re.X1, CenterX2 = d.Re.X2, CenterX3 = d.Re.X3,
            CenterX4 = d.Re.X4, CenterX5 = d.Re.X5, CenterX6 = d.Re.X6, CenterX7 = d.Re.X7,
            CenterY = d.Im.X0, CenterYLo = d.Im.X1, CenterY2 = d.Im.X2, CenterY3 = d.Im.X3,
            CenterY4 = d.Im.X4, CenterY5 = d.Im.X5, CenterY6 = d.Im.X6, CenterY7 = d.Im.X7,
        };
        calc.SetZoomLog10(zl);
        calc.SetDepth(c);
        calc.Calculate(CancellationToken.None);
        return calc;
    }

    private (MandelbrotCalculator Cpu, MandelbrotCalculator Gpu) Frames(int zl, IGpuKernel k)
    {
        MandelbrotCalculator.UseGpuPerturbation = false;
        var cpu = Render(zl, null);
        Assert.False(cpu.LastFrameUsedGpuPerturbation, "precondition: the CPU frame");
        MandelbrotCalculator.UseGpuPerturbation = true;
        MandelbrotCalculator.UseGpuLa = false;
        MandelbrotCalculator.GpuDeepPrecisionMode = GpuDeepPrecision.Fp32;
        MandelbrotCalculator.UseGpuLaFloatExp = true;
        var gpu = Render(zl, k);
        return (cpu, gpu);
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
    [InlineData(150, false)]
    [InlineData(285, true)]
    public void Past_1e100_The_Float_Exp_La_Kernel_Renders_The_Exact_View(int zl, bool fxTable)
    {
        if (MandelbrotCalculator.LaModeOverride != null) Assert.Skip("FF_LA set");
        using var k = Device();
        var (cpu, gpu) = Frames(zl, k);
        Assert.True(gpu.LastFrameUsedGpuPerturbation && gpu.LastFrameUsedGpuLaFloatExp,
            $"1e{zl}: the float-exp LA kernel did not run: {gpu.LastGpuRoute.Reason} / {gpu.LastGpuRoute.Detail}");
        Assert.EndsWith(" LA", gpu.LastPrecisionLabel);
        // Which table: the double one while it holds A and B, else the float-exp one.
        Assert.Equal(!fxTable, cpu.LastFrameUsedLa);
        Assert.Equal(fxTable, gpu.LastFrameUsedGpuLaTableFx);

        // The ground truth on sampled pixels.
        int prec = BigFloat.PrecisionForZoom(zl);
        var centre = Centre();
        var pixel = BigFloat.Div(BigFloat.Parse($"3.5e-{zl}", prec), BigFloat.FromDouble(Math.Max(W, H), prec), prec);
        var rng = new Random(1880 + zl);
        var idx = Enumerable.Range(0, W * H).OrderBy(_ => rng.Next()).Take(32).ToArray();
        var want = new int[idx.Length];
        int maxIt = MaxIt(zl);
        Parallel.For(0, idx.Length, q =>
            want[q] = Direct(centre, idx[q] % W - W * 0.5, idx[q] / W - H * 0.5, pixel, prec, maxIt));
        int cpuOff = 0, gpuOff = 0, esc = 0;
        for (int q = 0; q < idx.Length; q++)
        {
            if (cpu.IterationBuffer[idx[q]] != want[q]) cpuOff++;
            if (gpu.IterationBuffer[idx[q]] != want[q]) gpuOff++;
            if (want[q] < maxIt) esc++;
        }
        double dis = Enumerable.Range(0, W * H).Count(i => gpu.IterationBuffer[i] != cpu.IterationBuffer[i]) / (double)(W * H);
        int inOut = Enumerable.Range(0, W * H).Count(i => (gpu.IterationBuffer[i] >= maxIt) != (cpu.IterationBuffer[i] >= maxIt));
        _out.WriteLine($"1e{zl}: truth off CPU {cpuOff}, GPU {gpuOff} of {idx.Length} ({esc} escaping, counts {want.Min()}..{want.Max()}); " +
                       $"GPU vs CPU [{cpu.LastPrecisionLabel}] {dis:P2}, in/out {inOut}; GPU {k.LastDispatchMs:F0} ms");
        Assert.True(esc >= idx.Length / 2, "precondition: escaping pixels to compare");
        Assert.True(cpuOff == 0, $"oracle check: the CPU frame is off the truth at {cpuOff} px");
        Assert.True(gpuOff <= 1, $"1e{zl}: GPU off the truth at {gpuOff}/{idx.Length} px");
        Assert.True(dis <= 0.0135, $"1e{zl}: GPU vs CPU {dis:P2}");
        Assert.True(inOut <= W * H / 200, $"1e{zl}: in/out {inOut}");
    }

    [Fact]
    public void Past_1e290_The_Frame_Stays_On_The_Cpu_Float_Exp_Rows()
    {
        if (MandelbrotCalculator.LaModeOverride != null) Assert.Skip("FF_LA set");
        using var k = Device();
        var (_, gpu) = Frames(295, k);
        Assert.False(gpu.LastFrameUsedGpuPerturbation);
        Assert.EndsWith(" FX", gpu.LastPrecisionLabel);
        Assert.Contains("zoom > 1e290", gpu.LastGpuRoute.Reason ?? "");
    }

    [Theory]
    [InlineData(1.0, 0.0)]
    [InlineData(-3.5, 1e-3)]
    [InlineData(1.9999999999999998, -1.9999999999999998)]   // rounds up to 2 as a float: renormalised
    [InlineData(2.3e-268, -7.1e-269)]
    [InlineData(-4.4e300, 1e290)]
    [InlineData(1e-30, -1e300)]                              // a part far below the other
    [InlineData(5e-310, 0.0)]                                // subnormal double
    [InlineData(0.0, -2.5)]
    [InlineData(0.0, 0.0)]
    public void A_FloatExp_Encodes_As_Its_Double(double re, double im)
    {
        GpuFloatExp.Encode(re, im, out float wr, out float wi, out int we);
        GpuFloatExp.Encode(new CFx(FloatExp.FromDouble(re), FloatExp.FromDouble(im)), out float gr, out float gi, out int ge);
        Assert.Equal((BitConverter.SingleToUInt32Bits(wr), BitConverter.SingleToUInt32Bits(wi), we),
                     (BitConverter.SingleToUInt32Bits(gr), BitConverter.SingleToUInt32Bits(gi), ge));
        GpuFloatExp.Encode(Math.Abs(re), out float wm, out int wme);
        GpuFloatExp.Encode(FloatExp.FromDouble(Math.Abs(re)), out float gm, out int gme);
        Assert.Equal((wm, wme), (gm, gme));
    }

    [Fact]
    public void Past_Double_A_FloatExp_Keeps_Its_Exponent_And_Huge_Is_Infinite()
    {
        // 1.5·2^5000 − 1.25·2^4999 i: one shared exponent, the smaller part scaled to it.
        var v = new CFx(FloatExp.FromDouble(1.5).ScaleB(5000), FloatExp.FromDouble(-1.25).ScaleB(4999));
        GpuFloatExp.Encode(v, out float mr, out float mi, out int e);
        Assert.Equal((1.5f, -0.625f, 5000), (mr, mi, e));
        GpuFloatExp.Encode(FloatExp.Huge, out float hm, out int he);
        GpuFloatExp.Encode(double.PositiveInfinity, out float im_, out int ie);
        Assert.Equal((im_, ie), (hm, he));
    }
}
