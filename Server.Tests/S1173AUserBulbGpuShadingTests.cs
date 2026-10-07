// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1173-A / GPU parity plan G2.4 — User Bulb GPU shading. Both User Bulb GPU paths
// used to shade with one Lambert light over a 0.15 ambient and a sine rainbow, with
// no sky, no G-buffers (so no post stack) and AOV views / stereo depth forced onto
// the CPU. They now run UserBulbShadeKernel: the family kernels' shading around the
// user DE — the sandbox path splices the compiled DE into that kernel's own source.
//
// The CPU UserBulbCalculator (ShadingPipeline.Shade + the post tail) is the
// reference. On the ILGPU CPU accelerator the GPU frame must match the CPU frame for
// vec and quaternion sources, with shadows / AO / specular / reflections / fog, with
// tonemap + bloom (the post stack on the kernel's HDR G-buffer), in an AOV view and
// with the Normal colour driver — and the lighting must reach the pixels: the
// features-on GPU frame is far from the CPU features-off frame.

using System;
using System.Linq;
using System.Threading;

using FracturingFog.Calculators;
using FracturingFog.Calculators.Gpu;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.Rendering.Lighting;
using ILGPU;
using ILGPU.Runtime;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1173AUserBulbGpuShadingTests
{
    private const int W = 64, H = 48;

    public enum Look { Plain, Lit, Post, AovNormals, NormalDriver }

    private static LightingFxData Fx(Look look)
    {
        var fx = LightingFxData.CreateDefault();
        switch (look)
        {
            case Look.Lit:
                fx.ShadowSteps = 24; fx.ShadowLightMask = 0x1;
                fx.AoSamples = 4; fx.AoStrength = 0.6;
                fx.SpecularStrength = 0.6; fx.Roughness = 0.4;
                fx.ReflectionStrength = 0.3; fx.Metallic = 0.3;
                fx.FogDensity = 0.08; fx.VolumeSteps = 24;
                break;
            case Look.Post:
                fx.ToneMap = ToneMapOperator.Reinhard; fx.BloomStrength = 0.6; fx.BloomThreshold = 0.2;
                break;
            case Look.AovNormals:
                fx.DebugAov = AovView.Normals;
                break;
        }
        return fx;
    }

    private static (uint[] color, GpuRoute route) Render(bool gpu, bool quat, Look look)
    {
        var fp = new FractalParameters
        {
            UserBulbSource = quat ? "qpow(z, 2) + c" : "z^8 + c",
            UserBulbAxisMode = quat ? UserBulbAxisModeKind.Quat : UserBulbAxisModeKind.Vec3,
            UserBulbCompiler = UserBulbCompilerKind.Sandbox,
            UserBulbBackend = gpu ? UserBulbBackendKind.GPU : UserBulbBackendKind.CPU,
            UserBulbIterations = 8,
            UserBulbMaxSteps = 96,
            UserBulbTemporalReuse = false,
            // Vec3 runs on the GPU only with the analytic power DE the CPU also uses
            // (Auto rejects it for z^8 + c); quaternion runs the numerical Jacobian.
            UserBulbDEMode = quat ? UserBulbDEModeKind.Auto : UserBulbDEModeKind.Analytic,
            UserBulbColorDriver = look == Look.NormalDriver ? BulbColorDriver.Normal : BulbColorDriver.StepDepth,
            Lighting = Fx(look),
        };
        var calc = new UserBulbCalculator(W, H) { ColorMap = ColorPalette.BuiltIns[39], FractalParameters = fp };
        calc.Calculate(CancellationToken.None);
        return ((uint[])calc.ColorBuffer.Clone(), calc.LastGpuRoute);
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
    [InlineData(false, Look.Plain)]
    [InlineData(false, Look.Lit)]
    [InlineData(false, Look.Post)]
    [InlineData(false, Look.AovNormals)]
    [InlineData(false, Look.NormalDriver)]
    [InlineData(true, Look.Plain)]
    [InlineData(true, Look.Lit)]
    public void Gpu_User_Bulb_Matches_The_Cpu(bool quat, Look look)
    {
        OnCpuAccelerator(() =>
        {
            string label = $"{(quat ? "quat" : "vec")} {look}";
            var g = Render(gpu: true, quat, look);
            Assert.True(g.route.State == GpuRouteState.Gpu, $"{label}: frame did not run on the GPU ({g.route.Reason}: {g.route.Detail})");
            double same = MeanDrift(g.color, Render(gpu: false, quat, look).color);
            Assert.True(same < 1.0, $"{label}: GPU vs CPU mean drift {same:F2} (bound 1.0)");

            if (look != Look.Plain)
            {
                // The look reached the pixels: the CPU frame without it is far away.
                double vsPlain = MeanDrift(g.color, Render(gpu: false, quat, Look.Plain).color);
                Assert.True(vsPlain > Math.Max(1.0, 8 * same),
                    $"{label}: the GPU frame is as close to the plain CPU frame ({vsPlain:F2}) as to its own ({same:F2})");
            }
            return 0;
        });
    }

    [Fact]
    public void Orbit_Colour_Drivers_Say_They_Stay_On_The_Cpu()
    {
        OnCpuAccelerator(() =>
        {
            var fp = new FractalParameters
            {
                UserBulbSource = "z^8 + c", UserBulbCompiler = UserBulbCompilerKind.Sandbox,
                UserBulbBackend = UserBulbBackendKind.GPU, UserBulbColorDriver = BulbColorDriver.OrbitTrap,
                Lighting = LightingFxData.CreateDefault(),
            };
            var calc = new UserBulbCalculator(32, 24) { ColorMap = ColorPalette.BuiltIns[39], FractalParameters = fp };
            calc.Calculate(CancellationToken.None);
            Assert.Equal(GpuRouteState.CpuFallback, calc.LastGpuRoute.State);
            Assert.Equal("colour driver", calc.LastGpuRoute.Reason);
            return 0;
        });
    }

    [Fact]
    public void Legacy_And_Sandbox_Kernels_Render_The_Same_Frame()
    {
        // The legacy path (built-in triplex DE, compiled with Engine) and the sandbox
        // path (the same template text, user DE spliced in, Roslyn-compiled) share one
        // shading body: for the triplex the built-in DE implements, the frames agree.
        OnCpuAccelerator(() =>
        {
            var q = new GpuRenderParams
            {
                Width = 48, Height = 36,
                CamZ = -3.0, FwdZ = 1.0, RightX = 1.0, UpY = 1.0,
                FovScale = Math.Tan(Math.PI / 6.0), Aspect = 48.0 / 36.0,
                DEIter = 8, MaxSteps = 96, Eps = 1e-3, Bailout = 4.0, CullRadiusSq = 4.0,
                Power = 8.0, UseAnalyticDE = 1,   // the built-in DE is the analytic power DE
                InSetColor = 0xFF000000u, BgTop = 0xFF203040u, BgBottom = 0xFF101010u,
            };
            var r = UserBulbGpuDispatch.Raymarch(in q);
            var sp = GpuShadingParams.Build(LightingFxData.CreateDefault());
            uint[] lut = GpuAlbedoLut.Bake(ColorPalette.BuiltIns[39], 256.0, 4.0, 8.0, ref sp);
            var legacy = new uint[48 * 36];
            using var gpu = new UserBulbGpuCalculator();
            Assert.True(gpu.Render(legacy, null, r, sp, q, null, null, null, null, lut, null), gpu.LastError);
            using var sbx = new UserBulbSandboxGpuCompiler();
            Assert.True(sbx.TryCompile("triplex(z, 8) + c", Array.Empty<string>(), quatMode: false), sbx.LastError);
            var sandbox = new uint[48 * 36];
            Assert.True(sbx.Render(sandbox, new[] { 0.0 }, r, sp, q, null, null, null, null, lut, null), sbx.LastError);
            double d = MeanDrift(legacy, sandbox);
            Assert.True(legacy.Count(c => c != q.InSetColor) > legacy.Length / 10, "the legacy frame shows almost no surface");
            Assert.True(d < 1.0, $"legacy vs sandbox kernel drift {d:F2}");
            return 0;
        });
    }

    [Fact]
    public void Sandbox_Kernel_Is_The_Shared_Template_With_The_User_De()
    {
        // The splice keeps everything outside the USERDE region byte-for-byte.
        string spliced = UserBulbSandboxGpuCompiler.SpliceIntoTemplate("    // step\n", quatMode: false);
        Assert.Contains("public static class SandboxBulbShade", spliced);
        Assert.Contains("=> SandboxDE(cx, cy, cz, q, __p);", spliced);
        Assert.DoesNotContain("TriplexPower", spliced);
        Assert.Contains("GpuKernelUtils.ComposeSurfacePbr(", spliced);   // the family shading body
    }
}
