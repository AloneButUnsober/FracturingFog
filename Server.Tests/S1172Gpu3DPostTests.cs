// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1172 (GPU parity plan G1.2 / P7c) — GPU 3D frames used to skip the whole
// screen-space post stack (SSAO, tonemap + bloom, screen DoF, edge ink). The
// ILGPU kernels now emit the CPU Shade's G-buffers — depth, unit normal and the
// pre-clamp HDR beauty — and the calculators run ScreenSpacePost.ApplyPost3D on
// them. Kernels run on the ILGPU CPU accelerator (SetTestOverride). Checked:
//   - G-buffer contract per pixel: at a hit, depth is finite, the normal is unit
//     length and the HDR beauty clamps to exactly the LDR byte the kernel packed
//     (PackBgra truncates (uint)Clamp(v, 0, 255)); at a miss, depth +Inf,
//     normal 0, HDR NaN (ShadingPipeline.ClearGBuffer / ClearHdrBuffer);
//   - asking for the G-buffers does not change the LDR frame;
//   - each post pass now changes a GPU frame (it used to be dropped), and the
//     frame stays on the GPU;
//   - thin-lens DoF + tonemap stays on the CPU and says why.

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

public sealed class S1172Gpu3DPostTests
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

    [Fact]
    public void Kernel_GBuffers_Follow_The_Cpu_Shade_Contract()
    {
        double fov = Math.Tan(Math.PI / 6);
        var rp = new GpuRaymarchParams
        {
            Width = W, Height = H, CamZ = 2.6, FwdZ = -1, RightX = 1, UpY = 1,
            FovScale = fov, Aspect = (double)W / H, LightX = 0.577, LightY = 0.577, LightZ = 0.577,
            MaxSteps = 200, Eps = 1e-4, InSetColor = 0xFF000000u,
        };
        var bp = new MandelbulbGpuParams { Power = 8, DEIter = 8, Bailout = 2.0, SceneRadius = 12.0 };
        var fx = LightingFxData.CreateDefault();
        fx.SpecularStrength = 2.0;   // push some highlights past 255 so clamping matters
        var sp = GpuShadingParams.Build(in fx);

        var (plain, color, depth, normal, hdr) = WithCpuAccelerator(() =>
        {
            var gpu = new MandelbulbGpuCalculator();
            var a = new uint[W * H];
            Assert.True(gpu.Render(a, rp, sp, bp), gpu.LastError);
            var b = new uint[W * H]; var d = new float[W * H];
            var n = new float[3 * W * H]; var hd = new float[3 * W * H];
            Assert.True(gpu.Render(b, rp, sp, bp, null, d, default, n, hd), gpu.LastError);
            return (a, b, d, n, hd);
        });

        Assert.Equal(plain, color);   // G-buffers don't touch the LDR frame
        int hits = 0;
        for (int i = 0; i < W * H; i++)
        {
            bool hit = !float.IsPositiveInfinity(depth[i]);
            float nx = normal[3 * i], ny = normal[3 * i + 1], nz = normal[3 * i + 2];
            float hr = hdr[3 * i], hg = hdr[3 * i + 1], hb = hdr[3 * i + 2];
            if (!hit)
            {
                Assert.True(nx == 0 && ny == 0 && nz == 0, $"miss {i}: normal not cleared");
                Assert.True(float.IsNaN(hr) && float.IsNaN(hg) && float.IsNaN(hb), $"miss {i}: HDR not NaN");
                continue;
            }
            hits++;
            double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            Assert.InRange(len, 0.999, 1.001);
            uint c = color[i];
            Assert.Equal((c >> 16) & 0xFF, (uint)Math.Clamp(hr, 0f, 255f));
            Assert.Equal((c >> 8) & 0xFF, (uint)Math.Clamp(hg, 0f, 255f));
            Assert.Equal(c & 0xFF, (uint)Math.Clamp(hb, 0f, 255f));
        }
        Assert.True(hits > W * H / 10, $"view should hit the bulb ({hits})");
    }

    private static (uint[] color, GpuRoute route) RenderBulb(LightingFxData fx)
    {
        var calc = new MandelbulbCalculator(W, H)
        {
            ColorMap = ColorPalette.BuiltIns[0],
            FractalParameters = new FractalParameters { BulbPower = 8, BulbIterations = 8, BulbCameraDistance = 2.6, Lighting = fx },
            Zoom = 1.0,
        };
        calc.Calculate(CancellationToken.None);
        return ((uint[])calc.ColorBuffer.Clone(), calc.LastGpuRoute);
    }

    private static LightingFxData Gpu()
    {
        var fx = LightingFxData.CreateDefault();
        fx.UseGpuRender = true;
        fx.ToneMap = ToneMapOperator.None; fx.BloomStrength = 0; fx.SsaoSamples = 0; fx.EdgeStrength = 0;
        return fx;
    }

    [Fact]
    public void Post_Passes_Now_Apply_To_Gpu_Frames()
    {
        var (baseline, tone, bloom, ssao, edge) = WithCpuAccelerator(() =>
        {
            var b = RenderBulb(Gpu());
            var t = Gpu(); t.ToneMap = ToneMapOperator.Reinhard;
            var bl = Gpu(); bl.BloomStrength = 1.0; bl.BloomThreshold = 0.2;   // default 10 = effectively off
            var s = Gpu(); s.SsaoSamples = 8;
            var e = Gpu(); e.EdgeStrength = 1.0;
            return (b, RenderBulb(t), RenderBulb(bl), RenderBulb(s), RenderBulb(e));
        });

        Assert.Equal(GpuRouteState.Gpu, baseline.route.State);
        foreach (var (name, r) in new[] { ("tonemap", tone), ("bloom", bloom), ("SSAO", ssao), ("edge ink", edge) })
        {
            Assert.True(r.route.State == GpuRouteState.Gpu, $"{name}: frame left the GPU ({r.route.Reason})");
            Assert.False(r.color.SequenceEqual(baseline.color), $"{name} had no effect on the GPU frame");
        }
    }

    [Fact]
    public void Thin_Lens_With_Tonemap_Stays_On_The_Cpu()
    {
        var route = WithCpuAccelerator(() =>
        {
            var fx = Gpu();
            fx.ToneMap = ToneMapOperator.Reinhard;
            fx.DofThinLens = true; fx.DofAperture = 0.2; fx.DofSamples = 4;
            return RenderBulb(fx).route;
        });
        Assert.Equal(GpuRouteState.CpuFallback, route.State);
        Assert.Equal("thin-lens + tonemap", route.Reason);
    }
}
