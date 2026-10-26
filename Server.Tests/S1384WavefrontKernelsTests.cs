// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1384 — the wavefront schedule (#1340) for the kernels it did not cover: the
// shallow orbit colour entry, the deep orbit kernel (CSPerturbOrbit) and the fp32
// deep kernels (CSPerturbFX / CSPerturbF32). Their bit-identity against one thread
// per pixel is checked kernel by kernel in S1340GpuWavefrontTests (orbit colour)
// and S1340GpuWavefrontDeepTests (deep orbit, float-exp, scaled fp32). Here, the
// calculator on the Vulkan device (skipped without fp64):
//   • the deep orbit path and the fp32 path follow Auto (#1340): the first frame of
//     a view runs one thread per pixel, the next runs the wavefront schedule exactly
//     when the first frame's warp waste reached the threshold (the boundary-heavy
//     Marina preset yes, the balanced Spider Star no), the route says so, and the
//     colour / iterations are those of a one-thread-per-pixel frame;
//   • the opt-in seed (#1386) seeds the fp32 path's first frame; the orbit path runs
//     no seed dispatch (it would need an orbit-means buffer of its own).
// The D3D11 kernels are covered by --gpuwavefrontprobe.

using System;
using System.Threading;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Vulkan;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // writes PerturbBudgetMs / UseGpuPerturbation / UseGpuDeepOrbit / GpuDeepPrecisionMode
public sealed class S1384WavefrontKernelsTests(ITestOutputHelper output) : IDisposable
{
    private readonly double _savedBudget = MandelbrotKernelSource.PerturbBudgetMs;
    private readonly bool _savedPerturb = MandelbrotCalculator.UseGpuPerturbation;
    private readonly bool _savedOrbit = MandelbrotCalculator.UseGpuDeepOrbit;
    private readonly GpuDeepPrecision _savedPrecision = MandelbrotCalculator.GpuDeepPrecisionMode;
    private readonly bool _savedSa = MandelbrotCalculator.UseGpuSeriesApproximation;
    private readonly bool _savedBla = MandelbrotCalculator.UseGpuBla;

    public void Dispose()
    {
        MandelbrotKernelSource.PerturbBudgetMs = _savedBudget;
        MandelbrotCalculator.UseGpuPerturbation = _savedPerturb;
        MandelbrotCalculator.UseGpuDeepOrbit = _savedOrbit;
        MandelbrotCalculator.GpuDeepPrecisionMode = _savedPrecision;
        MandelbrotCalculator.UseGpuSeriesApproximation = _savedSa;
        MandelbrotCalculator.UseGpuBla = _savedBla;
        GpuWavefront.Reset();
    }

    private static VulkanComputeKernel Device()
    {
        var k = VulkanComputeKernel.TryCreateWithOwnContext();
        if (k is null) Assert.Skip("no Vulkan device on this host");
        if (!k.SupportsPerturbationSA) { k.Dispose(); Assert.Skip("Vulkan device has no shaderFloat64"); }
        MandelbrotKernelSource.PerturbBudgetMs = 0;   // no first-band abort under the suite's load
        MandelbrotCalculator.UseGpuPerturbation = true;
        return k;
    }

    public enum Path { Orbit, Fp32 }

    private static IColorMap Map(Path path)
    {
        if (path == Path.Fp32) return new HsvPalette();
        bool prev = InterpretedOrbitColorMap.GpuEnabled;
        InterpretedOrbitColorMap.GpuEnabled = true;
        try
        {
            var t = InterpretedColorMap.TryCreate("return hsv(fract(stripeAvg * 7.0 + trapMin), 0.85, 1.0);", null, out var err);
            Assert.True(t is InterpretedOrbitColorMap, $"not an orbit theme: {err}");
            return t!;
        }
        finally { InterpretedOrbitColorMap.GpuEnabled = prev; }
    }

    // Shipped deep presets (Resources/regions.json): Marina boundary-heavy, Spider Star balanced.
    private static MandelbrotCalculator Preset(VulkanComputeKernel k, bool marina, IColorMap map) => marina
        ? new MandelbrotCalculator(192, 108)
        {
            CenterX = -0.8166959533897611, CenterY = 0.17829091833221097, Zoom = 37039746442841656, MaxIterations = 20000,
            ColorMap = map, GpuKernel = k,
        }
        : new MandelbrotCalculator(192, 108)
        {
            CenterX = -1.9998000000003664, CenterXLo = -4.920546579744369e-17, CenterY = -2.56187704972203e-24,
            Zoom = 1.2103202390345426e+21, MaxIterations = 20000, ColorMap = map, GpuKernel = k,
        };

    private static void Setup(Path path)
    {
        MandelbrotCalculator.UseGpuDeepOrbit = path == Path.Orbit;
        MandelbrotCalculator.GpuDeepPrecisionMode = path == Path.Fp32 ? GpuDeepPrecision.Fp32 : GpuDeepPrecision.Double;
        // The SA / BLA kernels take a deep frame before the fp32 one does.
        MandelbrotCalculator.UseGpuSeriesApproximation = path != Path.Fp32;
        MandelbrotCalculator.UseGpuBla = path != Path.Fp32;
    }

    private static void AssertPath(MandelbrotCalculator c, Path path)
    {
        Assert.True(c.LastFrameUsedGpuPerturbation, c.LastGpuRoute.Detail);
        if (path == Path.Orbit) Assert.True(c.LastFrameUsedGpuOrbitPerturbation, $"not the orbit kernel: {c.LastGpuRoute.Detail}");
        else Assert.True(c.LastFrameUsedGpuFloatExp, $"not the fp32 kernel: {c.LastGpuRoute.Detail}");
    }

    [Theory]
    [InlineData(Path.Orbit, true)]
    [InlineData(Path.Orbit, false)]
    [InlineData(Path.Fp32, true)]
    [InlineData(Path.Fp32, false)]
    public void Auto_Picks_The_Next_Frame_From_The_Warp_Waste(Path path, bool marina)
    {
        using var k = Device();
        Setup(path);
        var map = Map(path);

        var reference = Preset(k, marina, map);
        GpuWavefront.PerturbMode = GpuWavefront.Off;
        try { reference.Calculate(CancellationToken.None); }
        finally { GpuWavefront.Reset(); }
        AssertPath(reference, path);

        var c = Preset(k, marina, map);
        c.Calculate(CancellationToken.None);
        AssertPath(c, path);
        Assert.False(c.LastFrameUsedGpuWavefront, "the first frame has no warp waste to go on");
        double waste = c.LastGpuWarpWaste;
        Assert.Equal(GpuWavefront.WarpWaste(c.IterationBuffer, c.Width, c.Height), waste);
        output.WriteLine($"{path} {(marina ? "Marina" : "Spider Star")}: warp waste {waste:F2}, route {c.LastGpuRoute.Detail}");
        if (marina) Assert.True(waste > GpuWavefront.PerturbWasteThreshold + 0.1, $"precondition: Marina waste {waste:F2}");
        else Assert.True(waste < GpuWavefront.PerturbWasteThreshold - 0.2, $"precondition: Spider Star waste {waste:F2}");

        c.Calculate(CancellationToken.None);
        AssertPath(c, path);
        Assert.Equal(marina, c.LastFrameUsedGpuWavefront);
        Assert.Equal(marina, k.LastFrameWavefront);
        Assert.Equal(marina, c.LastGpuRoute.Detail!.Contains("wavefront"));
        Assert.Equal(reference.IterationBuffer, c.IterationBuffer);
        Assert.Equal(reference.ColorBuffer, c.ColorBuffer);
        Assert.False(GpuWavefront.PerturbHint, "the calculator left its hint set");
    }

    [Fact]
    public void The_Seed_Covers_The_Fp32_Path_And_Not_The_Orbit_Path()
    {
        using var k = Device();
        GpuWavefront.Seed = true;

        Setup(Path.Fp32);
        var fp32 = Preset(k, marina: true, Map(Path.Fp32));
        fp32.Calculate(CancellationToken.None);
        AssertPath(fp32, Path.Fp32);
        Assert.True(fp32.LastFrameRanWavefrontSeed, "the fp32 first frame ran no seed dispatch");
        Assert.Equal(fp32.LastGpuWavefrontSeed >= GpuWavefront.PerturbWasteThreshold, fp32.LastFrameUsedGpuWavefront);
        Assert.True(fp32.LastFrameUsedGpuWavefront, $"precondition: Marina's seed estimate {fp32.LastGpuWavefrontSeed:F2} picks wavefront");

        Setup(Path.Orbit);
        var orbit = Preset(k, marina: true, Map(Path.Orbit));
        orbit.Calculate(CancellationToken.None);
        AssertPath(orbit, Path.Orbit);
        Assert.False(orbit.LastFrameRanWavefrontSeed, "the orbit path ran a seed dispatch");
        Assert.False(orbit.LastFrameUsedGpuWavefront);
    }
}
