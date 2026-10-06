// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1172 / GPU parity plan G2.2 — the 3D GPU kernels take their surface albedo
// from the active colour map (GpuAlbedoLut → GpuKernelUtils.SurfaceAlbedo)
// instead of a fixed sine rainbow. The CPU ShadingPipeline is the reference:
// for every family the GPU frame (ILGPU CPU accelerator) must stay within a
// small drift of the CPU frame, with and without the post stack on. Before
// G2.2 the same comparison measured 2.7-79 mean channel drift (Kleinian and
// Mandelbox above the old S742 ceiling of 40); with the LUT it is ~0.03-1.3.

using System;
using System.Linq;
using System.Threading;

using FracturingFog.Calculators.Gpu;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.Rendering.Lighting;
using ILGPU;
using ILGPU.Runtime;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1172Gpu3DAlbedoTests
{
    private const int W = 64, H = 48;

    public static TheoryData<string> Families => new()
    {
        "Mandelbulb", "Mandelbox", "Menger", "QuatJulia", "QuatMandel", "Kleinian", "Bicomplex", "Coquaternion",
    };

    private static IFractalCalculator Make(string family) => family switch
    {
        "Mandelbulb" => new MandelbulbCalculator(W, H),
        "Mandelbox" => new MandelboxCalculator(W, H),
        "Menger" => new KifsCalculator(W, H),
        "QuatJulia" => new QuatJuliaCalculator(W, H),
        "QuatMandel" => new QuatMandelbrotCalculator(W, H),
        "Kleinian" => new KleinianCalculator(W, H),
        "Bicomplex" => new BicomplexMandelbrotCalculator(W, H),
        "Coquaternion" => new CoquaternionMandelbrotCalculator(W, H),   // #1173-G
        _ => throw new ArgumentException(family),
    };

    private static (uint[] color, GpuRoute route) Render(string family, LightingFxData fx, IColorMap map)
    {
        var c = Make(family);
        c.ColorMap = map;
        c.Zoom = 1.0;
        ((dynamic)c).FractalParameters = new FractalParameters { Lighting = fx, KifsFold = KifsFoldKind.Menger };
        c.Calculate(CancellationToken.None);
        return ((uint[])c.ColorBuffer.Clone(), ((IGpuRouteSource)c).LastGpuRoute);
    }

    private static double MeanDrift(uint[] a, uint[] b)
    {
        long sum = 0;
        for (int i = 0; i < a.Length; i++)
            for (int s = 0; s < 24; s += 8)
                sum += Math.Abs((int)((a[i] >> s) & 0xFF) - (int)((b[i] >> s) & 0xFF));
        return sum / (a.Length * 3.0);
    }

    private static (double plain, double post) DriftFor(string family, IColorMap map)
    {
        using var ctx = GpuAcceleratorHost.CreateContext();
        using var acc = ctx.Devices.First(d => d.AcceleratorType == AcceleratorType.CPU).CreateAccelerator(ctx);
        GpuAcceleratorHost.SetTestOverride(acc);
        try
        {
            var gpu = LightingFxData.CreateDefault(); gpu.UseGpuRender = true;
            var cpu = gpu; cpu.UseGpuRender = false;
            var g = Render(family, gpu, map);
            Assert.Equal(GpuRouteState.Gpu, g.route.State);
            double plain = MeanDrift(g.color, Render(family, cpu, map).color);

            var gpuPost = gpu; gpuPost.ToneMap = ToneMapOperator.Reinhard; gpuPost.SsaoSamples = 8; gpuPost.EdgeStrength = 0.8;
            var cpuPost = gpuPost; cpuPost.UseGpuRender = false;
            double post = MeanDrift(Render(family, gpuPost, map).color, Render(family, cpuPost, map).color);
            return (plain, post);
        }
        finally { GpuAcceleratorHost.SetTestOverride(null); }
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void Gpu_Albedo_Tracks_The_Cpu_Colour_Map(string family)
    {
        foreach (int theme in new[] { 0, 40 })   // PhongStone (normal-dependent), MonoBandPhong3D
        {
            var (plain, post) = DriftFor(family, ColorPalette.BuiltIns[theme]);
            Assert.True(plain < 3.0, $"{family} theme {theme}: GPU vs CPU mean drift {plain:F2} (bound 3)");
            Assert.True(post < 3.0, $"{family} theme {theme} + post FX: GPU vs CPU mean drift {post:F2} (bound 3)");
        }
    }

    // ── LUT ──────────────────────────────────────────────────────────────

    /// <summary>A map that ignores the normal: colour is a function of smooth only.</summary>
    private sealed class RampMap : IColorMap
    {
        public int Version;
        public int MaxIterations { get; set; } = 256;
        public ColorPaletteType Type => default;
        public int Map(float smooth, float distance, int iterations)
            => unchecked((int)(0xFF000000u | ((uint)(smooth + Version) & 0xFF) << 16));
    }

    [Fact]
    public void Lut_Detects_Normal_Dependence_And_Caches()
    {
        var sp = new GpuShadingParams();
        var ramp = new RampMap();
        uint[] a = GpuAlbedoLut.Bake(ramp, 192, 0.5, 10, ref sp);
        Assert.Equal(1, sp.AlbedoLutNormals);
        Assert.Equal(GpuAlbedoLut.SmoothSamples, a.Length);
        Assert.Same(a, GpuAlbedoLut.Bake(ramp, 192, 0.5, 10, ref sp));       // unchanged → reused
        Assert.NotSame(a, GpuAlbedoLut.Bake(ramp, 192, 0.5, 20, ref sp));    // range changed → rebuilt
        ramp.Version = 7;
        uint[] b = GpuAlbedoLut.Bake(ramp, 192, 0.5, 20, ref sp);
        Assert.Equal(0x07u, (b[0] >> 16) & 0xFF);                             // map changed → rebuilt

        var phong = ColorPalette.BuiltIns[0];                                 // PhongStone reads the normal
        uint[] p = GpuAlbedoLut.Bake(phong, 192, 0.5, 10, ref sp);
        Assert.Equal(GpuAlbedoLut.NormalGrid, sp.AlbedoLutNormals);
        Assert.Equal(GpuAlbedoLut.SmoothSamples * GpuAlbedoLut.NormalGrid * GpuAlbedoLut.NormalGrid, p.Length);
    }
}
