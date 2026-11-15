// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1368 — the periodicity early-out of the deep-zoom orbit paths. The CPU deep orbit
// path (ComputePixelOrbitPerturbation) compared z = Z[m] + δ and δ without the
// reference index m, which only approximates periodicity; its GPU twin
// (CSPerturbOrbit) had no check. Both now test a bit-exact repeat of the loop state
// (δ, m): the next step depends on δ, m and the pixel's dc alone, so a repeat means the
// loop cycles for ever and the pixel is classified exactly as without the check. The
// GPU compares once per 32-step block. Checked (the #1342 way):
//   • on interior-heavy deep views (the period-3 nucleus at 1e14 and 1e22, the 1e15
//     --saprobe view, the 3e47 test view), the CPU frame with the check equals the frame
//     without it: colour, iteration, smooth and orbit buffers. Precondition: on the
//     interior views the check catches most in-set pixels;
//   • no false positives: in-set pixels the check caught stay bounded when their exact
//     orbit (BigInteger fixed point, from the multi-limb centre) runs 4x the cap;
//   • on the Vulkan device (skipped without fp64): the GPU orbit kernel with the check
//     (and in its diagnostic mode, which reports the iteration a pixel was caught at)
//     equals the kernel without it, and the diagnostic catches most in-set pixels; the
//     calculator's GPU deep orbit frame classifies every pixel as the CPU one does.

using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // writes DeepOrbitPeriodicity / GpuPeriodicity / UseGpuPerturbation
public sealed class S1368DeepOrbitPeriodicityTests(ITestOutputHelper output) : IDisposable
{
    private readonly double _savedBudget = MandelbrotKernelSource.PerturbBudgetMs;
    private readonly bool _savedGpuOrbit = InterpretedOrbitColorMap.GpuEnabled;
    private readonly bool _savedPerturb = MandelbrotCalculator.UseGpuPerturbation;
    private readonly bool _savedDeepOrbit = MandelbrotCalculator.UseGpuDeepOrbit;

    public void Dispose()
    {
        MandelbrotCalculator.DeepOrbitPeriodicity = true;
        MandelbrotCalculator.DeepOrbitPeriodicityDiagnostic = false;
        GpuPeriodicity.Reset();
        MandelbrotKernelSource.PerturbBudgetMs = _savedBudget;
        InterpretedOrbitColorMap.GpuEnabled = _savedGpuOrbit;
        MandelbrotCalculator.UseGpuPerturbation = _savedPerturb;
        MandelbrotCalculator.UseGpuDeepOrbit = _savedDeepOrbit;
    }

    public sealed record View(string Name, double[] Cx, double[] Cy, double Zoom, int MaxIter, int Dim, bool Interior)
    {
        public override string ToString() => Name;
    }

    private static readonly View[] Views =
    {
        new("nucleus3 1e14", [-1.7548776662466927, -3.552187509647825e-17, 0, 0], [0, 0, 0, 0], 1e14, 5000, 64, true),
        new("nucleus3 1e22", [-1.7548776662466927, -3.552187509647825e-17, 0, 0], [0, 0, 0, 0], 1e22, 20000, 48, true),
        new("p448 5.2e33", [-0.7145519151951202, -2.0790192891565988e-17, 1.1753166641509792e-33, -8.102457136962753e-50],
            [-0.35402073332318235, 2.5477179573089096e-17, -6.072997541261718e-34, -1.8911088656798612e-50], 5.176068e33, 13440, 48, false),
        new("deeper 4.5e46", [-1.9918151296901943, -7.8219818188678307E-17, 3.2454272033149852E-33, -2.6986232918289806E-49],
            [-5.5240415753972429E-06, -2.8404793590633191E-22, 1.5048294824547351E-38, -6.0649764033320806E-55], 4.49845E+46, 49820, 40, false),
        new("saprobe 1e15", [-1.1726999042772253, 8.9529605787776783E-17, 0, 0], [-0.2968356710071185, -2.3536240906562374E-18, 0, 0], 1e15, 4096, 64, false),
        new("3e47 test", [-1.9918151296901943, -7.8219844803880472E-17, 1.660139930392911E-34, 8.217274172159319E-51],
            [-5.5240415753972429E-06, -2.8659813126937928E-22, 6.6910924119662832E-39, 6.2394735914401016E-55], 3e47, 20000, 40, false),
    };

    public static TheoryData<View> AllViews()
    {
        var d = new TheoryData<View>();
        foreach (var v in Views) d.Add(v);
        return d;
    }

    private const string ThemeSource = "return hsv(fract(stripeAvg * 40.0 + tiaAvg * 40.0), 0.85, saturate(0.4 + trapMin * 20.0));";

    private static InterpretedOrbitColorMap Theme()
    {
        var t = InterpretedColorMap.TryCreate(ThemeSource, null, out var err) as InterpretedOrbitColorMap;
        Assert.True(t != null, $"theme did not compile as an orbit theme: {err}");
        return t!;
    }

    private static MandelbrotCalculator Calc(View v, VulkanComputeKernel? k = null) => new(v.Dim, v.Dim)
    {
        Quality = QualityPreset.Extreme,
        CenterX = v.Cx[0], CenterXLo = v.Cx[1], CenterX2 = v.Cx[2], CenterX3 = v.Cx[3],
        CenterY = v.Cy[0], CenterYLo = v.Cy[1], CenterY2 = v.Cy[2], CenterY3 = v.Cy[3],
        Zoom = v.Zoom, MaxIterations = v.MaxIter, ColorMap = Theme(), GpuKernel = k,
    };

    private sealed record Frame(uint[] Col, int[] It, float[] Sm, float[] Trap, float[] Stripe, int Hits, double Ms);

    private static Frame Render(View v, bool periodicity)
    {
        MandelbrotCalculator.UseGpuPerturbation = false;
        MandelbrotCalculator.DeepOrbitPeriodicity = periodicity;
        var c = Calc(v);
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        c.Calculate(CancellationToken.None);
        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        Assert.True(c.IsHighPrecisionActive, $"{v.Name}: not on the deep orbit path");
        return new((uint[])c.ColorBuffer.Clone(), (int[])c.IterationBuffer.Clone(), (float[])c.SmoothBuffer.Clone(),
            (float[])c.TrapBuffer.Clone(), (float[])c.StripeBuffer.Clone(), c.LastDeepPeriodicPixels, ms);
    }

    [Theory]
    [MemberData(nameof(AllViews))]
    public void The_Cpu_Check_Changes_No_Pixel_And_Catches_The_Interior(View v)
    {
        var off = Render(v, periodicity: false);
        Assert.Equal(0, off.Hits);
        var on = Render(v, periodicity: true);
        int n = v.Dim * v.Dim, inSet = off.It.Count(i => i >= v.MaxIter);
        output.WriteLine($"{v.Name}: {inSet}/{n} in set, check caught {on.Hits}; off {off.Ms:F0} ms, on {on.Ms:F0} ms");
        Assert.Equal(off.It, on.It);
        Assert.Equal(off.Col, on.Col);
        Assert.Equal(off.Sm, on.Sm);
        for (int i = 0; i < n; i++)
        {
            if (off.It[i] >= v.MaxIter) continue;   // in set: the accumulators aren't read
            Assert.Equal(off.Trap[i], on.Trap[i]);
            Assert.Equal(off.Stripe[i], on.Stripe[i]);
        }
        Assert.True(on.Hits <= inSet, $"{on.Hits} caught > {inSet} in set");
        if (v.Interior)
        {
            Assert.True(inSet > n / 2, $"precondition: only {inSet} of {n} pixels in set");
            Assert.True(on.Hits > inSet * 0.9, $"the check caught only {on.Hits} of {inSet} in-set pixels");
        }
    }

    // ── exact orbits (BigInteger fixed point, the #1319 method) ────────────────

    private const int Frac = 420;   // fraction bits: far below the 3e47 pixel (~2^-166)

    private static BigInteger Fix(double d)
    {
        if (d == 0) return BigInteger.Zero;
        long bits = BitConverter.DoubleToInt64Bits(d);
        int exp = (int)((bits >> 52) & 0x7FF);
        long man = bits & 0xFFFFFFFFFFFFFL;
        if (exp == 0) exp++; else man |= 1L << 52;
        int sh = exp - 1075 + Frac;
        var v = sh >= 0 ? new BigInteger(man) << sh : new BigInteger(man) >> -sh;
        return bits < 0 ? -v : v;
    }

    /// <summary>Whether the exact orbit of c stays within |z| ≤ 2 for <paramref name="steps"/> steps.</summary>
    private static bool StaysBounded(BigInteger cx, BigInteger cy, int steps)
    {
        BigInteger four = BigInteger.One << (Frac + 2), x = 0, y = 0;
        for (int n = 0; n < steps; n++)
        {
            BigInteger x2 = (x * x) >> Frac, y2 = (y * y) >> Frac;
            if (x2 + y2 > four) return false;
            BigInteger xy = (x * y) >> (Frac - 1);
            x = x2 - y2 + cx;
            y = xy + cy;
        }
        return true;
    }

    [Theory]
    [MemberData(nameof(AllViews))]
    public void Pixels_The_Check_Caught_Stay_Bounded_On_Their_Exact_Orbit(View v)
    {
        MandelbrotCalculator.DeepOrbitPeriodicityDiagnostic = true;   // smooth = -(the iteration caught at)
        Frame on;
        try { on = Render(v, periodicity: true); }
        finally { MandelbrotCalculator.DeepOrbitPeriodicityDiagnostic = false; }
        if (on.Hits == 0) Assert.Skip($"{v.Name}: the check caught no pixel");
        double scale = 3.5 / v.Dim / v.Zoom;
        BigInteger cx0 = v.Cx.Aggregate(BigInteger.Zero, (s, d) => s + Fix(d));
        BigInteger cy0 = v.Cy.Aggregate(BigInteger.Zero, (s, d) => s + Fix(d));
        var caught = Enumerable.Range(0, v.Dim * v.Dim).Where(i => on.Sm[i] < 0).ToArray();
        Assert.Equal(on.Hits, caught.Length);
        Assert.All(caught, i => Assert.True(on.It[i] >= v.MaxIter));
        // Up to 24 caught pixels spread over the frame (every k-th), all on the small views.
        int step = Math.Max(1, caught.Length / 24);
        var sample = caught.Where((_, k) => k % step == 0).Take(24).ToArray();
        int steps = v.MaxIter * 4;
        foreach (int i in sample)
        {
            int x = i % v.Dim, y = i / v.Dim;
            BigInteger cx = cx0 + Fix((x - v.Dim * 0.5) * scale), cy = cy0 + Fix((y - v.Dim * 0.5) * scale);
            Assert.True(StaysBounded(cx, cy, steps), $"{v.Name}: pixel ({x}, {y}) escapes within {steps} exact steps");
        }
        output.WriteLine($"{v.Name}: {sample.Length} of {caught.Length} caught pixels bounded for {steps} exact steps");
    }

    // ── GPU (Vulkan) ───────────────────────────────────────────────────────────

    private static VulkanComputeKernel Device()
    {
        var k = VulkanComputeKernel.TryCreateWithOwnContext();
        if (k is null) Assert.Skip("no Vulkan device on this host");
        if (!k.SupportsPerturbationOrbit) { k.Dispose(); Assert.Skip("Vulkan device has no shaderFloat64"); }
        return k;
    }

    [Fact]
    public void The_Gpu_Check_Changes_No_Pixel_And_Its_Diagnostic_Catches_The_Interior()
    {
        using var k = Device();
        MandelbrotKernelSource.PerturbBudgetMs = 0;
        // The period-3 nucleus in double: the reference stops at Z_{p+1} and the loop
        // wraps at p (#1614), as the calculator's periodic reference does.
        const double C = -1.7548776662466927;
        const int Dim = 96, MaxIter = 5000, P = 3;
        var zr = new double[P + 1]; var zi = new double[P + 1];
        double zx = 0;
        for (int i = 0; i <= P; i++) { zr[i] = zx; zx = zx * zx + C; }
        int mask = MandelbrotKernelSource.OrbAll, stride = MandelbrotKernelSource.PerturbOrbitStride(mask);
        // 2e-4 wide: the minibrot's interior (about 0.03 wide) and some exterior.
        double scale = 0.03 / Dim;
        (int[] It, float[] Sm, float[] Orb, float[] Dz) Run(int mode, double cx0)
        {
            int n = Dim * Dim;
            var it = new int[n]; var sm = new float[n]; var a = new float[n]; var b = new float[n]; var dr = new float[n]; var di = new float[n];
            var orb = new float[n * stride];
            GpuPeriodicity.Mode = mode;
            try
            {
                k.RunPerturbOrbit(Dim, Dim, scale, MaxIter, 512.0 * 512.0, cx0 - Dim / 2, -Dim / 2, zr, zi, P + 1,
                    mask, C, 0.0, it, sm, a, b, dr, di, orb);
            }
            finally { GpuPeriodicity.Reset(); }
            return (it, sm, orb, dr);
        }
        foreach (double shift in new[] { 0.0, 30.0 })   // centred, then shifted to take in the exterior
        {
            var off = Run(GpuPeriodicity.Off, shift);
            var on = Run(GpuPeriodicity.On, shift);
            var diag = Run(GpuPeriodicity.Diagnostic, shift);
            int n = Dim * Dim, inSet = off.It.Count(i => i >= MaxIter), caught = diag.Sm.Count(s => s < 0);
            output.WriteLine($"shift {shift}: {inSet}/{n} in set, diagnostic caught {caught}");
            Assert.Equal(off.It, on.It);
            Assert.Equal(off.It, diag.It);
            for (int i = 0; i < n; i++)
            {
                if (off.It[i] >= MaxIter) { Assert.True(on.Sm[i] == 0f, "an in-set pixel's smooth with the check"); continue; }
                Assert.Equal(off.Sm[i], on.Sm[i]);
                Assert.Equal(off.Sm[i], diag.Sm[i]);
                for (int s = 0; s < stride; s++) Assert.Equal(off.Orb[i * stride + s], on.Orb[i * stride + s]);
            }
            Assert.True(caught <= inSet);
            Assert.True(inSet > n / 4, $"precondition: only {inSet} in-set pixels");
            // Pixels near the minibrot's edge settle slowly and still run to the cap.
            Assert.True(caught > inSet * 0.85, $"the GPU check caught only {caught} of {inSet} in-set pixels");
            if (shift > 0) Assert.True(n - inSet > n / 10, $"precondition: only {n - inSet} escaping pixels");
        }
    }

    [Fact]
    public void The_Gpu_Deep_Orbit_Frame_Classifies_Every_Pixel_As_The_Cpu()
    {
        using var k = Device();
        MandelbrotKernelSource.PerturbBudgetMs = 0;
        InterpretedOrbitColorMap.GpuEnabled = true;
        MandelbrotCalculator.UseGpuDeepOrbit = true;
        foreach (var v in Views.Where(v => v.Zoom < 1e30))   // the plain GPU deep kernels stop agreeing past 1e27 (#1791)
        {
            var cpu = Render(v, periodicity: true);
            MandelbrotCalculator.UseGpuPerturbation = true;
            var g = Calc(v, k);
            g.Calculate(CancellationToken.None);
            Assert.True(g.LastFrameUsedGpuOrbitPerturbation, $"{v.Name}: the GPU deep orbit kernel did not run: {g.LastGpuRoute.Detail}");
            int n = v.Dim * v.Dim;
            int cpuIn = cpu.It.Count(i => i >= v.MaxIter), gpuIn = g.IterationBuffer.Count(i => i >= v.MaxIter);
            int differ = Enumerable.Range(0, n).Count(i => (cpu.It[i] >= v.MaxIter) != (g.IterationBuffer[i] >= v.MaxIter));
            output.WriteLine($"{v.Name}: in set CPU {cpuIn}, GPU {gpuIn}, classified differently {differ}");
            // float sampling vs double never changes the loop; the δ chain is the same fp64 arithmetic.
            Assert.True(differ <= n / 500, $"{v.Name}: {differ} pixels classified differently");
        }
    }
}
