// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1660 — the GPU deep kernels wrote dz/dc as a plain float (gFinalZD), which overflows
// past zoom ~1e35 (|dz/dc| ≈ 1/pixel > 3.4e38): the CPU write-back then built the
// normals and the distance estimate from ±inf. The kernels now write dz/dc · 2^E,
// E = ILogB(scale) (MandelbrotKernelSource.DzOut; the float-exp kernel adds gScaleE to
// its exponent), and the calculator multiplies by GpuPerturbDerivative.Unscale in double.
//   • CPU: the kernel's bit trick for 2^E (the exponent bits of gScale) equals the host's
//     ScaleB(1, ILogB(scale)) over the whole deep range; the encode/decode round trip
//     is exact where the plain float was finite, and finite where it overflowed.
//   • GPU (Vulkan, skipped without a device): on every deep route (double, SA,
//     LA, float-exp, scaled fp32, orbit) the GPU frame's normals and distance estimate
//     agree with the CPU deep frame's (the derivative in double) on the escaped pixels
//     with equal iteration counts, at 1e15 (finite before), 5e33 and 3e47 (inf before,
//     #1660's table), and none is NaN / inf. (Before #1783 the distance, ~ the pixel size,
//     underflowed the float DistanceBuffer to 0 on both paths at 3e47; it is now stored in a
//     power-of-two unit near the pixel size, so the distance check bites at every view.)
//   • a zoom sweep over one octave at 5e33: the kernel's 2^E ignores the scale's mantissa.
// (SA without BLA: the BLA skip's derivative drops + B, #1782. Distance underflow: #1783, fixed.)
// Mutants (by hand): caught — DzOutScale() returning 1; the float-exp entry without
// + gScaleE; the scaled-fp32 entry writing v · invS (the old output); the calculator
// not unscaling on the plain path or on the orbit path; the LA kernel writing the plain
// derivative; the exponent mask keeping a mantissa bit (0x7FF80000).

using System;
using System.Linq;
using System.Threading;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // writes the UseGpu* statics
public sealed class S1660GpuDerivativeScaleTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly double _budget = MandelbrotKernelSource.PerturbBudgetMs;
    private readonly bool _gp = MandelbrotCalculator.UseGpuPerturbation, _sa = MandelbrotCalculator.UseGpuSeriesApproximation,
        _bla = MandelbrotCalculator.UseGpuBla, _la = MandelbrotCalculator.UseGpuLa, _orbit = MandelbrotCalculator.UseGpuDeepOrbit,
        _gpuOrbit = InterpretedOrbitColorMap.GpuEnabled;
    private readonly GpuDeepPrecision _prec = MandelbrotCalculator.GpuDeepPrecisionMode;

    public S1660GpuDerivativeScaleTests(ITestOutputHelper output)
    {
        _out = output;
        MandelbrotKernelSource.PerturbBudgetMs = 0;   // as S88: no too-slow abort under load
        InterpretedOrbitColorMap.GpuEnabled = true;
    }

    public void Dispose()
    {
        MandelbrotKernelSource.PerturbBudgetMs = _budget;
        MandelbrotCalculator.UseGpuPerturbation = _gp;
        MandelbrotCalculator.UseGpuSeriesApproximation = _sa;
        MandelbrotCalculator.UseGpuBla = _bla;
        MandelbrotCalculator.UseGpuLa = _la;
        MandelbrotCalculator.UseGpuDeepOrbit = _orbit;
        MandelbrotCalculator.GpuDeepPrecisionMode = _prec;
        InterpretedOrbitColorMap.GpuEnabled = _gpuOrbit;
        VulkanComputeKernel.ForceFloatExpEntry = false;
    }

    // ── CPU: the encoding ────────────────────────────────────────────────────

    // The HLSL DzOutScale() in C#: gScale's high word with the mantissa and sign cleared.
    private static double KernelPow2(double scale)
    {
        ulong bits = (ulong)BitConverter.DoubleToInt64Bits(scale);
        uint hi = (uint)(bits >> 32);
        return BitConverter.Int64BitsToDouble((long)((ulong)(hi & 0x7FF00000u) << 32));
    }

    [Fact]
    public void The_Kernel_And_Host_Agree_On_The_Power_Of_Two()
    {
        var rng = new Random(1660);
        for (int i = 0; i < 20000; i++)
        {
            // Pixel scales of the GPU's deep range and well past it (1e-1 .. 1e-300).
            double scale = Math.Pow(10, -1 - rng.NextDouble() * 299) * (1 + rng.NextDouble());
            double p = KernelPow2(scale);
            Assert.Equal(1.0, p * GpuPerturbDerivative.Unscale(scale));   // exact inverse
            Assert.True(p <= scale && scale < 2 * p, $"2^E brackets the scale: {scale:R} vs {p:R}");
            // The float-exp host splits the scale the same way (MandelbrotGpuKernel / Vulkan).
            int e = Math.ILogB(scale);
            Assert.Equal(p, Math.ScaleB(1.0, e));
            float m = (float)Math.ScaleB(scale, -e);
            Assert.True(m >= 1f && m <= 2f);
        }
    }

    [Fact]
    public void The_Round_Trip_Is_Exact_Where_The_Plain_Float_Was_Finite_And_Finite_Where_It_Was_Not()
    {
        var rng = new Random(16601);
        int finiteBefore = 0, overflowBefore = 0;
        for (int i = 0; i < 20000; i++)
        {
            double scale = Math.Pow(10, -4 - rng.NextDouble() * 46);         // zoom ~1e4 .. 1e50
            // |dz/dc| at escape ≈ |z| ln|z| / (distance in pixels · scale), distance in
            // pixels from 1e-3 to 1e4.
            double dz = (1 + rng.NextDouble() * 1e3) / (Math.Pow(10, -3 + rng.NextDouble() * 7) * scale);
            if (rng.Next(2) == 0) dz = -dz;
            float encoded = (float)(dz * KernelPow2(scale));
            Assert.True(float.IsNormal(encoded), $"encoded {encoded} for dz {dz:R}, scale {scale:R}");
            double decoded = encoded * GpuPerturbDerivative.Unscale(scale);
            Assert.True(Math.Abs(decoded / dz - 1) < 1e-7, $"{decoded:R} vs {dz:R}");
            float plain = (float)dz;
            if (float.IsFinite(plain))
            {
                finiteBefore++;
                Assert.Equal((double)plain, decoded);   // bit for bit what the old output gave
            }
            else overflowBefore++;
        }
        Assert.True(finiteBefore > 1000 && overflowBefore > 1000, $"precondition: both sides ({finiteBefore} / {overflowBefore})");
    }

    // ── GPU: normals and distance against the CPU deep frame ─────────────────

    private const int W = 96, H = 64;

    private sealed record View(string Name, double Zoom, double[] Cx, double[] Cy, int MaxIter);

    // S1331's views: saprobe (fp64 / scaled-fp32 range, finite before #1660) and 3E47.
    private static readonly View Saprobe = new("saprobe 1e15", 1e15,
        [-1.1726999042772253, 8.9529605787776783E-17, 0, 0], [-0.2968356710071185, -2.3536240906562374E-18, 0, 0], 4096);
    private static readonly View P448 = new("p448 5e33", 5.176068e33 * 3.5 * H / (4.0 * W),
        [-0.7145519151951202, -2.0790192891565988e-17, 1.1753166641509792e-33, -8.102457136962753e-50],
        [-0.35402073332318235, 2.5477179573089096e-17, -6.072997541261718e-34, -1.8911088656798612e-50], 13440);
    private static readonly View E47 = new("3E47", 3e47,
        [-1.9918151296901943, -7.8219844803880472E-17, 1.660139930392911E-34, 8.217274172159319E-51],
        [-5.5240415753972429E-06, -2.8659813126937928E-22, 6.6910924119662832E-39, 6.2394735914401016E-55], 20000);

    // SA without BLA: the BLA skip carries dz' = A dz and drops + B (the CPU's BLA too),
    // so its normals miss the CPU's on half of p448's pixels whatever the encoding;
    // that is #1782.
    public enum Route { Double, Sa, La, FloatExp, ScaledF32, Orbit }

    public static TheoryData<Route, string> Cases => new()
    {
        { Route.Double, "saprobe" }, { Route.Double, "p448" }, { Route.Double, "3e47" },
        { Route.Sa, "saprobe" }, { Route.Sa, "p448" }, { Route.Sa, "3e47" },
        { Route.La, "p448" }, { Route.La, "3e47" },
        { Route.FloatExp, "3e47" },
        { Route.ScaledF32, "saprobe" },
        { Route.Orbit, "saprobe" }, { Route.Orbit, "3e47" },
    };

    private static View ByName(string n) => n switch { "saprobe" => Saprobe, "p448" => P448, _ => E47 };

    private const string OrbitTheme = "return hsv(fract(stripeAvg * 40.0 + tiaAvg * 40.0), 0.85, 1.0);";

    private static FracturingFog.Interefaces.IColorMap Map(Route r)
    {
        if (r != Route.Orbit) return new HsvPalette();
        var t = InterpretedColorMap.TryCreate(OrbitTheme, null, out var err) as InterpretedOrbitColorMap;
        Assert.True(t != null, $"orbit theme: {err}");
        return t!;
    }

    private static MandelbrotCalculator Frame(View v, Route r, VulkanComputeKernel? k)
    {
        var c = new MandelbrotCalculator(W, H)
        {
            Quality = QualityPreset.Extreme,
            CenterX = v.Cx[0], CenterXLo = v.Cx[1], CenterX2 = v.Cx[2], CenterX3 = v.Cx[3],
            CenterY = v.Cy[0], CenterYLo = v.Cy[1], CenterY2 = v.Cy[2], CenterY3 = v.Cy[3],
            Zoom = v.Zoom, MaxIterations = v.MaxIter, ColorMap = Map(r), GpuKernel = k,
        };
        c.Calculate(CancellationToken.None);
        return c;
    }

    private static void Select(Route r)
    {
        MandelbrotCalculator.UseGpuSeriesApproximation = r == Route.Sa;
        MandelbrotCalculator.UseGpuBla = false;
        MandelbrotCalculator.UseGpuLa = r == Route.La;
        MandelbrotCalculator.UseGpuDeepOrbit = r == Route.Orbit;
        MandelbrotCalculator.GpuDeepPrecisionMode =
            r is Route.FloatExp or Route.ScaledF32 ? GpuDeepPrecision.Fp32 : GpuDeepPrecision.Double;
    }

    // The kernel's 2^E must not depend on the scale's mantissa. Four zooms 2^(k/4) apart
    // span an octave, so at least one pixel scale has a mantissa >= 1.5 (log2 1.5 = 0.585:
    // the gap [0.585, 1) is wider than the 0.25 step), where a mask that kept a mantissa
    // bit would scale by 1.5 · 2^E and throw the distance off. p448: one view is enough (the
    // distance at 3E47 was 0 on both paths before #1783).
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void The_Scale_Mantissa_Does_Not_Leak_Into_The_Derivative(int quarter)
        => Compare(Route.Double, P448 with { Name = $"p448 x2^({quarter}/4)", Zoom = P448.Zoom * Math.Pow(2, quarter / 4.0) });

    [Theory]
    [MemberData(nameof(Cases))]
    public void Gpu_Normals_And_Distance_Match_The_Cpu_Deep_Frame(Route route, string view)
        => Compare(route, ByName(view));

    private void Compare(Route route, View v)
    {
        var k = VulkanComputeKernel.TryCreateWithOwnContext();
        if (k is null) Assert.Skip("no Vulkan device on this host");
        using var _ = k;
        bool fp32 = route is Route.FloatExp or Route.ScaledF32;
        if (fp32 ? !k.SupportsPerturbationFloatExp : !k.SupportsPerturbation)
            Assert.Skip("the Vulkan device lacks this kernel");
        if (route == Route.La && MandelbrotCalculator.LaModeOverride != null) Assert.Skip("FF_LA set");
        string view = v.Name;

        MandelbrotCalculator.UseGpuPerturbation = false;
        Select(route);
        var cpu = Frame(v, route, null);
        Assert.False(cpu.LastFrameUsedGpuPerturbation);

        MandelbrotCalculator.UseGpuPerturbation = true;
        var gpu = Frame(v, route, k);
        Assert.True(gpu.LastFrameUsedGpuPerturbation, $"{route}/{view}: GPU did not run: {gpu.LastGpuRoute.Detail}");
        switch (route)
        {
            case Route.Sa: Assert.True(gpu.LastFrameUsedGpuSeriesApproximation && !gpu.LastFrameUsedGpuBla, "SA kernel"); break;
            case Route.La: Assert.True(gpu.LastFrameUsedGpuLa, "LA kernel"); break;
            case Route.FloatExp: Assert.True(gpu.LastFrameUsedGpuFloatExp && !k.LastPerturbFloatExpWasScaledF32, "float-exp entry"); break;
            case Route.ScaledF32: Assert.True(gpu.LastFrameUsedGpuFloatExp && k.LastPerturbFloatExpWasScaledF32, "scaled fp32 entry"); break;
            case Route.Orbit: Assert.True(gpu.LastFrameUsedGpuOrbitPerturbation, "orbit kernel"); break;
            default:
                Assert.False(gpu.LastFrameUsedGpuSeriesApproximation || gpu.LastFrameUsedGpuBla || gpu.LastFrameUsedGpuLa
                    || gpu.LastFrameUsedGpuFloatExp || gpu.LastFrameUsedGpuOrbitPerturbation, "plain double kernel");
                break;
        }

        int max = v.MaxIter, same = 0, badN = 0, badD = 0, notFinite = 0, dChecked = 0;
        for (int i = 0; i < W * H; i++)
        {
            if (gpu.IterationBuffer[i] >= max) continue;
            float gx = gpu.NormalXBuffer[i], gy = gpu.NormalYBuffer[i], gd = gpu.DistanceBuffer[i];
            if (!float.IsFinite(gx) || !float.IsFinite(gy) || !float.IsFinite(gd)) { notFinite++; continue; }
            if (gpu.IterationBuffer[i] != cpu.IterationBuffer[i]) continue;
            same++;
            float cx = cpu.NormalXBuffer[i], cy = cpu.NormalYBuffer[i], cd = cpu.DistanceBuffer[i];
            double ang = Math.Abs(Math.Atan2(gy * cx - gx * cy, gx * cx + gy * cy));
            if (!(ang <= 1e-2)) badN++;
            if (float.IsNormal(cd)) dChecked++;
            if (!(Math.Abs(gd - cd) <= 1e-2 * Math.Abs(cd))) badD++;
        }
        _out.WriteLine($"{route}/{v.Name}: {same} escaped px with equal iterations; normals off > 0.01 rad at {badN}, " +
            $"distance off > 1% at {badD}; {notFinite} escaped px not finite");
        Assert.Equal(0, notFinite);
        // #1783: the float distance is normal at every view (it was 0 past ~1e45 before).
        Assert.True(dChecked >= same / 2, $"precondition: normal float distances ({dChecked} of {same})");
        Assert.True(same >= W * H / 8, $"precondition: escaped pixels to compare ({same})");
        int allow = same / (fp32 ? 20 : 100);
        Assert.True(badN <= allow, $"{route}/{v.Name}: normals off at {badN} of {same}");
        Assert.True(badD <= allow, $"{route}/{v.Name}: distance off at {badD} of {same}");
    }
}
