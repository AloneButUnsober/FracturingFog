// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1331 — fp32 deep-zoom GPU perturbation kernels (MandelbrotKernelSource
// .BuildPerturbFloatExp: a scaled-fp32 entry while the pixel scale is in float
// range, float-exp — fp32 mantissa + int exponent — deeper). No doubles at all,
// so they run on GPUs without fp64 and 3–8× faster than the double kernel on
// weak-fp64 cards, at a 24-bit mantissa's accuracy. Checked:
//   • the precision mode: FF_GPU_PERTURB_PRECISION parsing, and which kernel a
//     device gets (Auto = fp32 only without fp64);
//   • on the Vulkan device, against the CPU deep path at four views (DD and QD
//     tiers), each entry: the fp32 frame ran (and the entry the depth calls for),
//     and stays within the measured accuracy band (≤ 0.5% of pixels, mean colour
//     drift ≤ 0.5 / 255);
//   • the oracle is sound: where the fp32 kernels and the CPU disagree, an exact
//     600-bit fixed-point orbit sides with the CPU (the #1319 method);
//   • the route names the fp32 path ("[GPU fp32]"), and Auto on an fp64 device
//     keeps the double kernel.

using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // GPU statics
public sealed class S1331Fp32PerturbTests(ITestOutputHelper output)
{
    // ── Mode ─────────────────────────────────────────────────────────────────

    private sealed class FakeKernel(bool fp64, bool fx) : IGpuKernel
    {
        public bool HasGpuPalette => false;
        public double LastDispatchMs => 0;
        public double LastReadbackMs => 0;
        public bool SupportsPerturbation => fp64;
        public bool SupportsPerturbationFloatExp => fx;
        public void SetPalette(FracturingFog.Interefaces.IGpuHlslPalette? palette) { }
        public void Run(int width, int height, double centerX, double centerY, double scale, int maxIter, double bailout2,
            int[] iterDst, float[] smoothDst, float[] finalZrDst, float[] finalZiDst, float[] finalDrDst, float[] finalDiDst,
            int[]? perRowMaxIter = null, FractalKind kind = FractalKind.Mandelbrot, float param0 = 0f, float param1 = 0f,
            uint[]? colorDst = null, float[]? trapDst = null, GpuDomainWarp warp = default) { }
        public void Dispose() { }
    }

    [Theory]
    [InlineData(null, GpuDeepPrecision.Auto)]
    [InlineData("", GpuDeepPrecision.Auto)]
    [InlineData("auto", GpuDeepPrecision.Auto)]
    [InlineData("bogus", GpuDeepPrecision.Auto)]
    [InlineData("double", GpuDeepPrecision.Double)]
    [InlineData(" FP64 ", GpuDeepPrecision.Double)]
    [InlineData("fp32", GpuDeepPrecision.Fp32)]
    [InlineData("Fast", GpuDeepPrecision.Fp32)]
    public void Precision_Env_Parses(string? value, GpuDeepPrecision expected)
        => Assert.Equal(expected, MandelbrotCalculator.ParseGpuDeepPrecision(value));

    [Fact]
    public void The_Mode_Picks_The_Kernel_Per_Device()
    {
        var saved = MandelbrotCalculator.GpuDeepPrecisionMode;
        try
        {
            // (mode, fp64, fp32 kernel) → fp32 used?
            var table = new (GpuDeepPrecision Mode, bool Fp64, bool Fx, bool Expect)[]
            {
                (GpuDeepPrecision.Auto, true, true, false),     // fp64 card: double by default
                (GpuDeepPrecision.Auto, false, true, true),     // no fp64: fp32 instead of the CPU
                (GpuDeepPrecision.Auto, false, false, false),
                (GpuDeepPrecision.Fp32, true, true, true),      // opt-in
                (GpuDeepPrecision.Fp32, true, false, false),    // no fp32 kernel
                (GpuDeepPrecision.Double, false, true, false),  // never
                (GpuDeepPrecision.Double, true, true, false),
            };
            foreach (var r in table)
            {
                MandelbrotCalculator.GpuDeepPrecisionMode = r.Mode;
                Assert.True(MandelbrotCalculator.UseFloatExpKernel(new FakeKernel(r.Fp64, r.Fx)) == r.Expect,
                    $"{r.Mode}, fp64 {r.Fp64}, fp32 kernel {r.Fx}");
            }
        }
        finally { MandelbrotCalculator.GpuDeepPrecisionMode = saved; }
    }

    [Fact]
    public void The_Route_Names_The_Fp32_Path()
    {
        var r = GpuRoute.OnGpu("Vulkan", "x") with { Mode = "fp32" };
        Assert.Equal("[GPU fp32]", r.StatusTag);
        Assert.Equal("[GPU]", GpuRoute.OnGpu("Vulkan").StatusTag);
    }

    // ── Vulkan frames ────────────────────────────────────────────────────────

    private sealed record View(string Name, double Zoom, double[] Cx, double[] Cy, int MaxIter, bool Scaled);

    private static readonly View[] Views =
    {
        new("seahorse 1e14", 1e14, new[] { -0.743643887037151, 0, 0, 0 }, new[] { 0.13182590420533, 0, 0, 0 }, 3000, true),
        new("saprobe 1e15", 1e15, new[] { -1.1726999042772253, 8.9529605787776783E-17, 0, 0 },
            new[] { -0.2968356710071185, -2.3536240906562374E-18, 0, 0 }, 4096, true),
        new("Deeper 4.5e46", 4.49845E+46,
            new[] { -1.9918151296901943, -7.8219818188678307E-17, 3.2454272033149852E-33, -2.6986232918289806E-49 },
            new[] { -5.5240415753972429E-06, -2.8404793590633191E-22, 1.5048294824547351E-38, -6.0649764033320806E-55 }, 20000, false),
        new("3E47", 3E+47,
            new[] { -1.9918151296901943, -7.8219844803880472E-17, 1.660139930392911E-34, 8.217274172159319E-51 },
            new[] { -5.5240415753972429E-06, -2.8659813126937928E-22, 6.6910924119662832E-39, 6.2394735914401016E-55 }, 20000, false),
    };

    private const int W = 160, H = 120;

    private static MandelbrotCalculator Calc(View v) => new(W, H)
    {
        Quality = QualityPreset.Extreme,
        CenterX = v.Cx[0], CenterXLo = v.Cx[1], CenterX2 = v.Cx[2], CenterX3 = v.Cx[3],
        CenterY = v.Cy[0], CenterYLo = v.Cy[1], CenterY2 = v.Cy[2], CenterY3 = v.Cy[3],
        Zoom = v.Zoom, MaxIterations = v.MaxIter, ColorMap = new HsvPalette(),
    };

    private sealed class GpuStatics : IDisposable
    {
        private readonly double _budget = MandelbrotKernelSource.PerturbBudgetMs;
        private readonly bool _gp = MandelbrotCalculator.UseGpuPerturbation, _sa = MandelbrotCalculator.UseGpuSeriesApproximation,
            _bla = MandelbrotCalculator.UseGpuBla;
        private readonly GpuDeepPrecision _prec = MandelbrotCalculator.GpuDeepPrecisionMode;
        public GpuStatics()
        {
            MandelbrotKernelSource.PerturbBudgetMs = 0;   // correctness, not speed: no first-band abort
            MandelbrotCalculator.UseGpuSeriesApproximation = false;
            MandelbrotCalculator.UseGpuBla = false;
        }
        public void Dispose()
        {
            MandelbrotKernelSource.PerturbBudgetMs = _budget;
            MandelbrotCalculator.UseGpuPerturbation = _gp; MandelbrotCalculator.UseGpuSeriesApproximation = _sa;
            MandelbrotCalculator.UseGpuBla = _bla; MandelbrotCalculator.GpuDeepPrecisionMode = _prec;
            VulkanComputeKernel.ForceFloatExpEntry = false;
        }
    }

    private static VulkanComputeKernel Kernel()
    {
        var k = VulkanComputeKernel.TryCreateWithOwnContext();
        if (k is null) Assert.Skip("no Vulkan device on this host");
        return k;
    }

    private static int[] CpuFrame(View v, out uint[] colour) => CpuFrame(v, out colour, out _);

    private static int[] CpuFrame(View v, out uint[] colour, out double[] dzMag)
    {
        MandelbrotCalculator.UseGpuPerturbation = false;
        var c = Calc(v);
        c.Calculate(CancellationToken.None);
        colour = (uint[])c.ColorBuffer.Clone();
        dzMag = DzMag(c);
        return (int[])c.IterationBuffer.Clone();
    }

    private static double[] DzMag(MandelbrotCalculator c) => Enumerable.Range(0, W * H)
        .Select(i => Math.Sqrt((double)c.FinalDrBuffer[i] * c.FinalDrBuffer[i] + (double)c.FinalDiBuffer[i] * c.FinalDiBuffer[i]))
        .ToArray();

    private static MandelbrotCalculator GpuFrame(View v, VulkanComputeKernel k, GpuDeepPrecision mode, bool forceFx = false)
    {
        MandelbrotCalculator.UseGpuPerturbation = true;
        MandelbrotCalculator.GpuDeepPrecisionMode = mode;
        VulkanComputeKernel.ForceFloatExpEntry = forceFx;
        var g = Calc(v);
        g.GpuKernel = k;
        g.Calculate(CancellationToken.None);
        VulkanComputeKernel.ForceFloatExpEntry = false;
        return g;
    }

    [Fact]
    public void Fp32_Frames_Stay_Within_The_Accuracy_Band_Of_The_Cpu()
    {
        using var k = Kernel();
        using var st = new GpuStatics();
        int n = W * H;
        foreach (var v in Views)
        {
            var cpu = CpuFrame(v, out var cpuColour, out var cpuDz);
            Assert.True(cpu.Distinct().Count() > 20, $"{v.Name}: precondition, a busy frame");
            foreach (bool forceFx in v.Scaled ? new[] { false, true } : new[] { false })
            {
                var g = GpuFrame(v, k, GpuDeepPrecision.Fp32, forceFx);
                Assert.True(g.LastFrameUsedGpuPerturbation && g.LastFrameUsedGpuFloatExp, $"{v.Name}: the fp32 kernel didn't run");
                bool scaled = k.LastPerturbFloatExpWasScaledF32;
                Assert.Equal(v.Scaled && !forceFx, scaled);
                Assert.Equal("[GPU fp32]", g.LastGpuRoute.StatusTag);
                int dis = 0; long col = 0;
                for (int i = 0; i < n; i++)
                {
                    if (g.IterationBuffer[i] != cpu[i]) dis++;
                    for (int s = 0; s < 24; s += 8)
                        col += Math.Abs((int)((g.ColorBuffer[i] >> s) & 0xFF) - (int)((cpuColour[i] >> s) & 0xFF));
                }
                double frac = (double)dis / n, drift = col / (3.0 * n);
                // The derivative (distance estimate, normals): |dz/dc| against the CPU's
                // on escaped pixels both sides agree on, while it is in float range.
                var gDz = DzMag(g);
                var ratios = Enumerable.Range(0, n)
                    .Where(i => g.IterationBuffer[i] == cpu[i] && cpu[i] < v.MaxIter
                                && cpuDz[i] > 0 && double.IsFinite(cpuDz[i]) && double.IsFinite(gDz[i]))
                    .Select(i => gDz[i] / cpuDz[i]).OrderBy(r => r).ToList();
                double median = ratios.Count > 0 ? ratios[ratios.Count / 2] : double.NaN;
                output.WriteLine($"{v.Name} {(scaled ? "scaled fp32" : "float-exp")}: {dis}/{n} ({frac:P2}) differ, colour drift {drift:F3}, |dz/dc| median ratio {median:F4} over {ratios.Count}");
                if (ratios.Count > n / 10)
                    Assert.True(Math.Abs(median - 1) < 0.01, $"{v.Name}: |dz/dc| median ratio {median}");
                Assert.True(frac <= 0.005, $"{v.Name}: {frac:P2} of pixels differ from the CPU");
                Assert.True(drift <= 0.5, $"{v.Name}: colour drift {drift:F3}");
            }
        }
    }

    [Fact]
    public void Auto_Keeps_The_Double_Kernel_On_An_Fp64_Device()
    {
        using var k = Kernel();
        if (!k.SupportsPerturbation) Assert.Skip("no fp64 on this device: Auto picks fp32 here");
        using var st = new GpuStatics();
        var g = GpuFrame(Views[1], k, GpuDeepPrecision.Auto);
        Assert.True(g.LastFrameUsedGpuPerturbation);
        Assert.False(g.LastFrameUsedGpuFloatExp);
        Assert.Equal("[GPU]", g.LastGpuRoute.StatusTag);
    }

    // The #1319 ground truth: c = centre + (x − W/2)·scale in 600-bit fixed point.
    private const int Frac = 600;

    private static BigInteger Fix(double d)
    {
        if (d == 0) return BigInteger.Zero;
        long bits = BitConverter.DoubleToInt64Bits(d);
        int exp = (int)((bits >> 52) & 0x7FF);
        long man = bits & 0xFFFFFFFFFFFFFL;
        if (exp == 0) exp = 1; else man |= 1L << 52;
        int sh = exp - 1075 + Frac;
        var v = sh >= 0 ? new BigInteger(man) << sh : new BigInteger(man) >> -sh;
        return d < 0 ? -v : v;
    }

    private static int ExactIter(BigInteger cx, BigInteger cy, int maxIter)
    {
        BigInteger x = 0, y = 0, r2 = (BigInteger)(512 * 512) << Frac;
        for (int n = 0; n < maxIter; n++)
        {
            BigInteger xx = (x * x) >> Frac, yy = (y * y) >> Frac;
            if (xx + yy >= r2) return n;
            BigInteger xy = (x * y) >> (Frac - 1);
            x = xx - yy + cx;
            y = xy + cy;
        }
        return maxIter;
    }

    [Fact]
    public void Where_Fp32_And_The_Cpu_Disagree_The_Exact_Orbit_Sides_With_The_Cpu()
    {
        using var k = Kernel();
        using var st = new GpuStatics();
        var v = Views[1];   // saprobe 1e15: the hard view
        var cpu = CpuFrame(v, out _);
        var g = GpuFrame(v, k, GpuDeepPrecision.Fp32);
        var differ = Enumerable.Range(0, W * H).Where(i => g.IterationBuffer[i] != cpu[i]).Take(30).ToList();
        Assert.True(differ.Count >= 5, $"precondition: only {differ.Count} disagreeing pixels to check");
        double scale = (3.5 / Math.Max(W, H)) / v.Zoom;
        BigInteger bcx = Fix(v.Cx[0]) + Fix(v.Cx[1]), bcy = Fix(v.Cy[0]) + Fix(v.Cy[1]), bs = Fix(scale);
        int cpuRight = 0, fpRight = 0;
        foreach (int i in differ)
        {
            int px = i % W, py = i / W;
            int truth = ExactIter(bcx + bs * (2 * px - W) / 2, bcy + bs * (2 * py - H) / 2, v.MaxIter);
            if (cpu[i] == truth) cpuRight++;
            if (g.IterationBuffer[i] == truth) fpRight++;
        }
        output.WriteLine($"{differ.Count} disagreeing pixels: CPU right on {cpuRight}, fp32 right on {fpRight}");
        Assert.True(cpuRight >= differ.Count * 9 / 10, $"the CPU oracle is right on only {cpuRight}/{differ.Count}");
    }
}
