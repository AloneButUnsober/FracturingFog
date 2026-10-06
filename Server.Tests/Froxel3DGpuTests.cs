// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1070 (froxel F4) — 3D froxel fog on the GPU trace. The ILGPU kernels write a
// per-pixel ray distance (+Inf = miss) and the CPU froxel pass composites over the
// GPU frame. The kernels run on the ILGPU CPU accelerator (forced through
// GpuAcceleratorHost.SetTestOverride), so this exercises the real kernels on any
// host. Checked against independent invariants, not the code's own output:
//   - the depth is on the surface per the CPU distance estimator (a separate
//     implementation from the kernel's DE), and depth-miss ⇔ colour-miss;
//   - asking for depth does not change a single colour byte;
//   - on pixels that miss in both traces, GPU-trace + froxel equals the full CPU
//     froxel frame (the haze is the same volume, so the GPU path is a faithful
//     substitute for what the user saw before F4).

using System;
using System.Linq;
using System.Threading;

using FracturingFog.Calculators.Gpu;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using ILGPU;
using ILGPU.Runtime;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class Froxel3DGpuTests
{
    private const int W = 40, H = 30;

    private static T WithCpuAccelerator<T>(Func<T> body)
    {
        using var ctx = GpuAcceleratorHost.CreateContext();
        using var acc = ctx.Devices.First(d => d.AcceleratorType == AcceleratorType.CPU).CreateAccelerator(ctx);
        GpuAcceleratorHost.SetTestOverride(acc);
        try { return body(); }
        finally { GpuAcceleratorHost.SetTestOverride(null); }
    }

    // ── Gate ──────────────────────────────────────────────────────────────

    [Fact]
    public void Gate_Froxel3DKeepsTheGpuTrace_UnlessThinLens()
    {
        var fx = LightingFxData.CreateDefault();
        fx.FogDensity = 0.05; fx.Froxel3D = true;
        fx = ScreenSpacePost.FogFreeForFroxel3D(in fx);
        Assert.True(ScreenSpacePost.ForcesCpuTrace(in fx));        // still needs depth…
        Assert.True(ScreenSpacePost.GpuFroxel3DHybrid(in fx));     // …which the GPU now supplies
        Assert.True(ScreenSpacePost.GpuWantsDepth(in fx));
        Assert.True(ScreenSpacePost.GpuTraceAllowed(in fx));

        // #323 — stereo depth output comes from the GPU depth buffer too.
        var stereo = fx; stereo.StereoMode = StereoMode.Fake; stereo.StereoEyeSeparation = 0.05;
        Assert.True(ScreenSpacePost.GpuTraceAllowed(in stereo));
        Assert.True(ScreenSpacePost.GpuWantsDepth(in stereo));

        var lens = fx; lens.DofThinLens = true; lens.DofAperture = 0.2; lens.DofSamples = 4;
        Assert.False(ScreenSpacePost.GpuTraceAllowed(in lens));
        var stereoLens = stereo; stereoLens.DofThinLens = true; stereoLens.DofAperture = 0.2; stereoLens.DofSamples = 4;
        Assert.False(ScreenSpacePost.GpuTraceAllowed(in stereoLens));

        Assert.True(ScreenSpacePost.GpuTraceAllowed(LightingFxData.CreateDefault()));   // plain frame unchanged
    }

    // ── Kernel depth (Mandelbulb, direct) ─────────────────────────────────

    [Fact]
    public void MandelbulbKernel_DepthIsOnTheSurface_AndLeavesColourUntouched()
    {
        const double camDist = 2.6, eps = 1e-4;
        const int deIter = 8;
        double fov = Math.Tan(Math.PI / 6);
        var fx = LightingFxData.CreateDefault();
        var cam = FroxelCamera.LookAt((0, 0, camDist), (0, 0, 0), 2 * Math.Atan(fov), 0.05, 12, camDist);
        uint inSet = 0xFF000000u;
        var rp = new GpuRaymarchParams
        {
            Width = W, Height = H,
            CamX = cam.PosX, CamY = cam.PosY, CamZ = cam.PosZ,
            FwdX = cam.Fx, FwdY = cam.Fy, FwdZ = cam.Fz,
            RightX = cam.Rx, RightY = cam.Ry, RightZ = cam.Rz,
            UpX = cam.Ux, UpY = cam.Uy, UpZ = cam.Uz,
            FovScale = fov, Aspect = (double)W / H,
            MaxSteps = 200, Eps = eps, InSetColor = inSet,
        };
        var bp = new MandelbulbGpuParams { Power = 8, DEIter = deIter, Bailout = 2.0, SceneRadius = 12.0 };
        var sp = GpuShadingParams.Build(in fx);

        var (plain, withDepth, depth) = WithCpuAccelerator(() =>
        {
            var gpu = new MandelbulbGpuCalculator();
            var a = new uint[W * H];
            var b = new uint[W * H];
            var d = new float[W * H];
            Assert.True(gpu.Render(a, rp, sp, bp), gpu.LastError);
            Assert.True(gpu.Render(b, rp, sp, bp, null, d), gpu.LastError);
            return (a, b, d);
        });

        Assert.Equal(plain, withDepth);

        var de = new MandelbulbDe(8, deIter);
        int hits = 0;
        for (int i = 0; i < W * H; i++)
        {
            bool colourMiss = withDepth[i] == inSet;
            Assert.Equal(colourMiss, float.IsPositiveInfinity(depth[i]));
            if (colourMiss) continue;
            hits++;
            int x = i % W, y = i / W;
            double u = (2.0 * (x + 0.5) / W - 1.0) * fov * rp.Aspect, v = (1.0 - 2.0 * (y + 0.5) / H) * fov;
            double dx = cam.Fx + cam.Rx * u + cam.Ux * v, dy = cam.Fy + cam.Ry * u + cam.Uy * v, dz = cam.Fz + cam.Rz * u + cam.Uz * v;
            double l = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            double t = depth[i];
            Assert.InRange(t, 0.5, camDist + 1.5);
            double d = de.Evaluate(cam.PosX + dx / l * t, cam.PosY + dy / l * t, cam.PosZ + dz / l * t);
            Assert.True(d < 1e-3, $"pixel ({x},{y}) depth {t} is {d} off the surface");
        }
        Assert.InRange(hits, W * H / 10, W * H - 1);
    }

    // ── Hybrid frame vs full CPU frame, every GPU family ──────────────────

    public enum Family { Mandelbulb, Mandelbox, Menger, Sierpinski, QuatJulia, QuatMandelbrot, Kleinian, Bicomplex }

    private static uint[] Render(Family fam, bool gpu, double fog)
    {
        var fx = LightingFxData.CreateDefault();
        fx.UseGpuRender = gpu;
        fx.FogDensity = fog;
        fx.Froxel3D = fog > 0;
        fx.SsaoSamples = 0; fx.EdgeStrength = 0;
        var fp = new FractalParameters
        {
            Lighting = fx,
            KifsFold = fam == Family.Sierpinski ? KifsFoldKind.Sierpinski : KifsFoldKind.Menger,
        };
        dynamic calc = fam switch
        {
            Family.Mandelbulb     => new MandelbulbCalculator(W, H),
            Family.Mandelbox      => new MandelboxCalculator(W, H),
            Family.Menger         => new KifsCalculator(W, H),
            Family.Sierpinski     => new KifsCalculator(W, H),
            Family.QuatJulia      => new QuatJuliaCalculator(W, H),
            Family.QuatMandelbrot => new QuatMandelbrotCalculator(W, H),
            Family.Kleinian       => new KleinianCalculator(W, H),
            _                     => (object)new BicomplexMandelbrotCalculator(W, H),
        };
        calc.ColorMap = ColorPalette.BuiltIns[0];
        calc.FractalParameters = fp;
        calc.Zoom = 1.0;
        calc.Calculate(CancellationToken.None);
        return (uint[])((uint[])calc.ColorBuffer).Clone();
    }

    private static int MaxChannelDiff(uint a, uint b)
        => Math.Max(Math.Abs((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF)),
           Math.Max(Math.Abs((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF)),
                    Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF))));

    [Theory]
    [InlineData(Family.Mandelbulb)]
    [InlineData(Family.Mandelbox)]
    [InlineData(Family.Menger)]
    [InlineData(Family.Sierpinski)]
    [InlineData(Family.QuatJulia)]
    [InlineData(Family.QuatMandelbrot)]
    [InlineData(Family.Kleinian)]
    [InlineData(Family.Bicomplex)]
    public void GpuTracePlusFroxel_MatchesTheCpuFroxelFrame_OnTheHaze(Family fam)
    {
        const double fog = 0.05;
        var (gpuClear, gpuFog, cpuClear, cpuFog) = WithCpuAccelerator(() =>
            (Render(fam, true, 0), Render(fam, true, fog), Render(fam, false, 0), Render(fam, false, fog)));

        // The GPU trace really ran (its shading differs from the CPU pipeline) —
        // with the froxel pass on too, i.e. Froxel3D no longer forces the CPU trace.
        Assert.NotEqual(gpuClear, cpuClear);
        Assert.NotEqual(gpuFog, cpuFog);

        uint inSet = ColorPalette.BuiltIns[0].InSetColor;
        var miss = Enumerable.Range(0, W * H).Where(i => gpuClear[i] == inSet && cpuClear[i] == inSet).ToArray();
        var hit = Enumerable.Range(0, W * H).Where(i => gpuClear[i] != inSet).ToArray();
        Assert.True(miss.Length > W * H / 20, $"{fam}: only {miss.Length} shared miss pixels");

        // The haze is applied on the GPU frame…
        Assert.True(miss.Count(i => gpuFog[i] != inSet) > miss.Length * 0.9, $"{fam}: no haze on the GPU frame");
        // …and it is the same volume as the CPU frame's.
        int close = miss.Count(i => MaxChannelDiff(gpuFog[i], cpuFog[i]) <= 2);
        Assert.True(close >= miss.Length * 0.98, $"{fam}: {close}/{miss.Length} haze pixels match the CPU frame");

        // The froxel pass also composites over the surfaces (depth reached the CPU).
        if (hit.Length > 0)
            Assert.True(hit.Count(i => gpuFog[i] != gpuClear[i]) > hit.Length / 2, $"{fam}: fog left the surface untouched");
    }
}
