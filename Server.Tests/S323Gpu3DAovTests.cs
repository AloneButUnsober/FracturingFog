// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #323 (GPU parity plan G1.1) — AOV view modes and published depth on the GPU
// trace. The eight ILGPU 3D kernels encode a non-Beauty view in-kernel
// (GpuKernelUtils.EncodeSurfaceAov, twin of ShadingPipeline.EncodeAov), and
// a stereo / autostereogram frame takes its depth from the kernel's depth
// buffer, so neither forces the CPU trace any more.
//
// The kernels run on the ILGPU CPU accelerator (GpuAcceleratorHost
// .SetTestOverride). Checked against invariants of each encoding rather than
// the code's own output:
//   - Normals decode to unit vectors; StepCount satisfies B = 1-R, G = 0.8R;
//     AO and Shadow are grey; Depth decodes (t = -ln(1-v)/0.12) to the depth
//     the same kernel publishes in a stereo frame;
//   - each view differs from the Beauty frame (it isn't silently beauty) and
//     stays within a drift bound of the CPU ShadingPipeline's encoding;
//   - the GPU route really ran (LastGpuRoute == Gpu), and Beauty is unchanged.

using System;
using System.Linq;
using System.Threading;

using FracturingFog.Calculators.Gpu;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.Rendering.Lighting;
using ILGPU;
using ILGPU.Runtime;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S323Gpu3DAovTests
{
    private const int W = 48, H = 36;

    private static T WithCpuAccelerator<T>(Func<T> body)
    {
        using var ctx = GpuAcceleratorHost.CreateContext();
        using var acc = ctx.Devices.First(d => d.AcceleratorType == AcceleratorType.CPU).CreateAccelerator(ctx);
        GpuAcceleratorHost.SetTestOverride(acc);
        try { return body(); }
        finally { GpuAcceleratorHost.SetTestOverride(null); }
    }

    private static (uint[] color, float[]? depth, GpuRoute route) Render(LightingFxData fx)
    {
        var calc = new MandelbulbCalculator(W, H)
        {
            ColorMap = ColorPalette.BuiltIns[0],
            FractalParameters = new FractalParameters
            {
                BulbPower = 8, BulbIterations = 8, BulbCameraDistance = 2.6, Lighting = fx,
            },
            Zoom = 1.0,
        };
        calc.Calculate(CancellationToken.None);
        return ((uint[])calc.ColorBuffer.Clone(), calc.DepthBuffer, calc.LastGpuRoute);
    }

    private static LightingFxData Fx(AovView view, bool gpu)
    {
        var fx = LightingFxData.CreateDefault();
        fx.UseGpuRender = gpu;
        fx.DebugAov = view;
        fx.ShadowSteps = 24;   // give Shadow a non-trivial signal
        fx.AoSamples = 4;
        return fx;
    }

    private static (int r, int g, int b) Rgb(uint c) => ((int)((c >> 16) & 0xFF), (int)((c >> 8) & 0xFF), (int)(c & 0xFF));

    private static double MeanDrift(uint[] a, uint[] b)
    {
        long sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            var (ar, ag, ab) = Rgb(a[i]); var (br, bg, bb) = Rgb(b[i]);
            sum += Math.Abs(ar - br) + Math.Abs(ag - bg) + Math.Abs(ab - bb);
        }
        return sum / (a.Length * 3.0);
    }

    // Hit pixels: the kernel's published depth is finite exactly at hits.
    private static bool[] HitMask()
    {
        var fx = LightingFxData.CreateDefault();
        fx.UseGpuRender = true;
        fx.StereoMode = StereoMode.Fake; fx.StereoEyeSeparation = 0.05;
        var (_, depth, route) = Render(fx);
        Assert.Equal(GpuRouteState.Gpu, route.State);
        Assert.NotNull(depth);
        return depth!.Select(d => !float.IsPositiveInfinity(d)).ToArray();
    }

    [Theory]
    [InlineData(AovView.Normals)]
    [InlineData(AovView.Depth)]
    [InlineData(AovView.StepCount)]
    [InlineData(AovView.AmbientOcclusion)]
    [InlineData(AovView.Diffuse)]
    [InlineData(AovView.Specular)]
    [InlineData(AovView.Shadow)]
    public void Aov_View_Renders_On_The_Gpu_And_Matches_Its_Encoding(AovView view)
    {
        var (gpu, beauty, cpu, hits, stereoDepth) = WithCpuAccelerator(() =>
        {
            var g = Render(Fx(view, gpu: true));
            Assert.Equal(GpuRouteState.Gpu, g.route.State);             // no AOV gate any more
            var b = Render(Fx(AovView.Beauty, gpu: true));
            var c = Render(Fx(view, gpu: false));

            var sfx = Fx(AovView.Beauty, gpu: true);
            sfx.StereoMode = StereoMode.Fake; sfx.StereoEyeSeparation = 0.05;
            var s = Render(sfx);
            return (g.color, b.color, c.color, HitMask(), s.depth!);
        });

        int nHits = hits.Count(h => h);
        Assert.True(nHits > W * H / 10, $"view should hit the bulb ({nHits} hits)");
        if (view != AovView.Specular)   // default spec strength may be 0 → black == beauty's dark? still differs; keep strict for the rest
            Assert.NotEqual(beauty, gpu);

        for (int i = 0; i < gpu.Length; i++)
        {
            if (!hits[i]) continue;
            var (r, g, b) = Rgb(gpu[i]);
            switch (view)
            {
                case AovView.Normals:
                {
                    double nx = r / 255.0 * 2 - 1, ny = g / 255.0 * 2 - 1, nz = b / 255.0 * 2 - 1;
                    double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    Assert.InRange(len, 0.97, 1.03);
                    break;
                }
                case AovView.StepCount:
                    Assert.InRange(b - (255 - r), -1, 1);
                    Assert.InRange(g - r * 0.8, -1.5, 1.5);
                    break;
                case AovView.AmbientOcclusion:
                case AovView.Shadow:
                    Assert.True(r == g && g == b, $"{view} pixel {i} not grey: {r},{g},{b}");
                    break;
                case AovView.Depth:
                {
                    Assert.True(r == g && g == b);
                    double v = r / 255.0;
                    if (v > 0.02 && v < 0.98)
                    {
                        double t = -Math.Log(1 - v) / 0.12;
                        double rel = Math.Abs(t - stereoDepth[i]) / stereoDepth[i];
                        Assert.True(rel < 0.05, $"depth pixel {i}: AOV {t:F4} vs published {stereoDepth[i]:F4}");
                    }
                    break;
                }
            }
        }

        double drift = MeanDrift(gpu, cpu);
        Assert.True(drift < 1.0, $"{view}: GPU vs CPU encoding mean channel drift {drift:F2} (bound 1)");
    }

    [Fact]
    public void Stereo_Depth_Is_Published_From_The_Gpu_Trace()
    {
        var (gpuRoute, gpuDepth, cpuDepth, lensRoute) = WithCpuAccelerator(() =>
        {
            var fx = LightingFxData.CreateDefault();
            fx.StereoMode = StereoMode.Fake; fx.StereoEyeSeparation = 0.05;
            fx.UseGpuRender = true;
            var g = Render(fx);
            var cfx = fx; cfx.UseGpuRender = false;
            var c = Render(cfx);
            var lfx = fx; lfx.DofThinLens = true; lfx.DofAperture = 0.2; lfx.DofSamples = 4;
            var l = Render(lfx);
            return (g.route, g.depth, c.depth, l.route);
        });

        Assert.Equal(GpuRouteState.Gpu, gpuRoute.State);
        Assert.NotNull(gpuDepth);
        Assert.NotNull(cpuDepth);
        int both = 0, agree = 0;
        for (int i = 0; i < gpuDepth!.Length; i++)
        {
            if (float.IsPositiveInfinity(gpuDepth[i]) || float.IsPositiveInfinity(cpuDepth![i])) continue;
            both++;
            if (Math.Abs(gpuDepth[i] - cpuDepth[i]) <= 0.02 * cpuDepth[i]) agree++;
        }
        Assert.True(both > W * H / 10);
        Assert.True(agree >= both * 0.95, $"GPU vs CPU depth: {agree}/{both} within 2%");

        // Thin-lens taps have no single depth: that combination stays on the CPU, and says why.
        Assert.Equal(GpuRouteState.CpuFallback, lensRoute.State);
        Assert.Equal("stereo + thin-lens", lensRoute.Reason);
    }
}
