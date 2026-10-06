// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1173-B / GPU parity plan G2.3 — HDRI environment on the 3D GPU kernels. Before
// this, SkyMode = Hdri was silently replaced by the gradient sky on the GPU: miss
// pixels, IBL ambient and reflections all saw the gradient while the CPU sampled
// the image. The kernels now get the relief kernel's flattened HDRI buffer
// (GpuHdriEnv → ReliefHdriBuffer.Flatten) and sample it with GpuKernelUtils.SampleHdri.
//
// The CPU ShadingPipeline is the reference. For every family (ILGPU CPU
// accelerator) the GPU frame must stay within a small drift of the CPU frame under
// an HDRI that is strongly direction-dependent, and the HDRI must actually reach
// the pixels: the GPU HDRI frame is far from the GPU gradient frame. The same
// holds for SkyMode Solid, whose flat-colour env ambient the GPU also ignored.

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

public sealed class S1173BGpu3DHdriTests
{
    private const int W = 64, H = 48;
    private const string EnvName = "s1173b-test-env";

    private static readonly string[] s_families =
    {
        "Mandelbulb", "Mandelbox", "Menger", "Sierpinski", "QuatJulia", "QuatMandel", "Kleinian", "Bicomplex",
    };

    /// <summary>Each family with the sky backdrop on (miss pixels sample the HDRI) and
    /// off (misses are the flat in-set colour on both paths, so the whole frame
    /// measures the surface terms: IBL ambient and reflections).</summary>
    public static TheoryData<string, bool> Families()
    {
        var d = new TheoryData<string, bool>();
        foreach (var f in s_families) { d.Add(f, true); d.Add(f, false); }
        return d;
    }

    private static IFractalCalculator Make(string family) => family switch
    {
        "Mandelbulb" => new MandelbulbCalculator(W, H),
        "Mandelbox" => new MandelboxCalculator(W, H),
        "Menger" or "Sierpinski" => new KifsCalculator(W, H),
        "QuatJulia" => new QuatJuliaCalculator(W, H),
        "QuatMandel" => new QuatMandelbrotCalculator(W, H),
        "Kleinian" => new KleinianCalculator(W, H),
        "Bicomplex" => new BicomplexMandelbrotCalculator(W, H),
        _ => throw new ArgumentException(family),
    };

    /// <summary>An equirect map that is red toward +x, blue toward −x and bright green
    /// overhead, with a hot (>1) sun patch so the clamp paths run too.</summary>
    private static void RegisterEnv()
    {
        const int w = 64, h = 32;
        var data = new float[w * h * 3];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            double u = (x + 0.5) / w, v = (y + 0.5) / h;
            double phi = (u - 0.5) * 2 * Math.PI, theta = v * Math.PI;
            double dx = Math.Sin(theta) * Math.Cos(phi), dy = Math.Cos(theta);
            int i = (y * w + x) * 3;
            data[i] = (float)(0.15 + 0.8 * Math.Max(0, dx));
            data[i + 1] = (float)(0.1 + 0.7 * Math.Max(0, dy));
            data[i + 2] = (float)(0.15 + 0.8 * Math.Max(0, -dx));
            if (x is >= 40 and < 44 && y is >= 6 and < 9) { data[i] = 4f; data[i + 1] = 3.5f; data[i + 2] = 2f; }
        }
        HdriRegistry.Register(EnvName, new HdriImage(w, h, data));
    }

    private static LightingFxData Scene(SkyMode mode, bool gpu, bool backdrop = true)
    {
        var fx = LightingFxData.CreateDefault();
        fx.UseGpuRender = gpu;
        fx.SkyMode = mode;
        fx.EnvironmentName = EnvName;
        fx.ShowSkyBackdrop = backdrop;
        fx.IblStrength = 0.7;
        fx.ReflectionStrength = 0.6;
        fx.Metallic = 0.5;
        fx.Roughness = 0.45;   // a mid mip on the reflection lookups
        return fx;
    }

    private static (uint[] color, GpuRoute route) Render(string family, LightingFxData fx)
    {
        var c = Make(family);
        c.ColorMap = ColorPalette.BuiltIns[40];   // MonoBandPhong3D — flat albedo, so the env term dominates
        c.Zoom = 1.0;
        ((dynamic)c).FractalParameters = new FractalParameters
        {
            Lighting = fx,
            KifsFold = family == "Sierpinski" ? KifsFoldKind.Sierpinski : KifsFoldKind.Menger,
        };
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

    private static T OnCpuAccelerator<T>(Func<T> body)
    {
        using var ctx = GpuAcceleratorHost.CreateContext();
        using var acc = ctx.Devices.First(d => d.AcceleratorType == AcceleratorType.CPU).CreateAccelerator(ctx);
        GpuAcceleratorHost.SetTestOverride(acc);
        try { return body(); }
        finally { GpuAcceleratorHost.SetTestOverride(null); }
    }

    [Theory]
    [MemberData(nameof(Families))]
    public void Gpu_Samples_The_Hdri_Like_The_Cpu(string family, bool backdrop)
    {
        RegisterEnv();
        OnCpuAccelerator(() =>
        {
            string label = $"{family} (backdrop {(backdrop ? "on" : "off")})";
            var g = Render(family, Scene(SkyMode.Hdri, gpu: true, backdrop));
            Assert.Equal(GpuRouteState.Gpu, g.route.State);
            uint[] cpu = Render(family, Scene(SkyMode.Hdri, gpu: false, backdrop)).color;
            double drift = MeanDrift(g.color, cpu);
            Assert.True(drift < 1.5, $"{label}: GPU vs CPU mean drift under an HDRI {drift:F2} (bound 1.5)");

            // The HDRI reached the pixels: the gradient-sky GPU frame (what every HDRI
            // scene rendered before #1173-B) is far from the CPU HDRI frame.
            uint[] gradient = Render(family, Scene(SkyMode.Gradient, gpu: true, backdrop)).color;
            double vsGradient = MeanDrift(gradient, cpu);
            Assert.True(vsGradient > Math.Max(1.0, 8 * drift),
                $"{label}: the gradient frame is as close to the CPU HDRI frame ({vsGradient:F2}) as the HDRI frame ({drift:F2})");
            return 0;
        });
    }

    [Theory]
    [InlineData("Mandelbox")]
    [InlineData("QuatJulia")]
    public void Gpu_Solid_Sky_Ambient_Matches_The_Cpu(string family)
    {
        OnCpuAccelerator(() =>
        {
            var gpu = Scene(SkyMode.Solid, gpu: true);
            gpu.BgTopColor = 0xFFE04020u;   // far from the gradient average, so a gradient ambient shows
            gpu.BgBottomColor = 0xFF102040u;
            var cpu = gpu; cpu.UseGpuRender = false;
            var g = Render(family, gpu);
            Assert.Equal(GpuRouteState.Gpu, g.route.State);
            double drift = MeanDrift(g.color, Render(family, cpu).color);
            Assert.True(drift < 3.0, $"{family}: GPU vs CPU mean drift with a Solid sky {drift:F2} (bound 3)");
            return 0;
        });
    }

    [Fact]
    public void Resolve_Reuses_The_Flattened_Buffer_And_Gates_On_SkyMode()
    {
        RegisterEnv();
        var sp = new GpuShadingParams();
        var fx = Scene(SkyMode.Hdri, gpu: true);
        uint[]? a = GpuHdriEnv.Resolve(in fx, ref sp);
        Assert.NotNull(a);
        Assert.Equal(1, sp.HdriOn);
        Assert.Same(a, GpuHdriEnv.Resolve(in fx, ref sp));

        var grad = Scene(SkyMode.Gradient, gpu: true);
        Assert.Null(GpuHdriEnv.Resolve(in grad, ref sp));
        Assert.Equal(0, sp.HdriOn);

        var missing = fx; missing.EnvironmentName = "s1173b-no-such-env";
        Assert.Null(GpuHdriEnv.Resolve(in missing, ref sp));
        Assert.Equal(0, sp.HdriOn);
    }
}
