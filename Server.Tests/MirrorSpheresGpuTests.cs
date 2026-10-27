// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1443 — the Wada mirror spheres on the GPU (ILGPU, the 3D families' path). The CPU
// MirrorSpheresScene.Trace is the reference: on the ILGPU CPU accelerator the kernel's
// per-pixel outcomes (opening, bounces, kept light, path, exit direction) must match
// the CPU trace of the same camera ray, and the frames must match pixel for pixel.
// Feature reach: the GPU frame must follow the radius and the Fresnel switch, so a
// kernel that ignored them fails.

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

public sealed class MirrorSpheresGpuTests
{
    private const int W = 72, H = 56;

    // The parity tests pin the GPU path (Auto could hand later frames to the CPU, which
    // renders the same image and would hide a kernel bug); the Auto tests pass their own.
    private static T OnCpuAccelerator<T>(Func<T> body,
        MirrorSpheresCalculator.GpuMirrorSpheresMode mode = MirrorSpheresCalculator.GpuMirrorSpheresMode.Gpu)
    {
        using var ctx = GpuAcceleratorHost.CreateContext();
        using var acc = ctx.Devices.First(d => d.AcceleratorType == AcceleratorType.CPU).CreateAccelerator(ctx);
        GpuAcceleratorHost.SetTestOverride(acc);
        var before = MirrorSpheresCalculator.GpuSelection;
        MirrorSpheresCalculator.GpuSelection = mode;
        try { return body(); }
        finally
        {
            MirrorSpheresCalculator.GpuSelection = before;
            GpuAcceleratorHost.SetTestOverride(null);
        }
    }

    private static FractalParameters Params(bool gpu, Action<FractalParameters>? tweak = null)
    {
        var fx = LightingFxData.CreateDefault();
        fx.UseGpuRender = gpu;
        var p = new FractalParameters { Lighting = fx };
        tweak?.Invoke(p);
        return p;
    }

    private static (uint[] Color, GpuRoute Route) Render(FractalParameters p)
    {
        var c = new MirrorSpheresCalculator(W, H) { FractalParameters = p, Zoom = 1.0 };
        c.Calculate(CancellationToken.None);
        return ((uint[])c.ColorBuffer.Clone(), c.LastGpuRoute);
    }

    private static double SameFraction(uint[] a, uint[] b)
    {
        int same = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] == b[i]) same++;
        return same / (double)a.Length;
    }

    private static readonly Action<FractalParameters> Photo = p =>
    {
        p.MirrorSpheresMode = MirrorSpheresMode.Photo;
        p.MirrorSpheresOpeningColors = "#D55E00,env,#F2F2F2,#000000";
    };

    [Fact]
    public void Kernel_Outcomes_Match_The_Cpu_Trace()
    {
        OnCpuAccelerator(() =>
        {
            foreach (bool free in new[] { false, true })
            {
                var p = Params(gpu: true, q => { if (free) Photo(q); });
                var scene = MirrorSpheresScene.Build(p);
                var cam = MirrorSpheresCamera.From(p, 1.0, 0, 0, 0, scene.BoundingRadius);
                var gpu = new MirrorSpheresGpuCalculator();
                var outs = gpu.Render(cam.ToGpuParams(W, H), MirrorSpheresGpuCalculator.ParamsFor(scene, free));
                Assert.True(outs != null, gpu.LastError);

                int checkedPx = 0, deep = 0, mismatch = 0;
                for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = y * W + x;
                    cam.Ray(x, y, W, H, out double dx, out double dy, out double dz);
                    var c = scene.Trace(cam.X, cam.Y, cam.Z, dx, dy, dz);
                    var g = outs!.Exit(i);
                    checkedPx++;
                    if (c.Bounces >= 4) deep++;
                    // Gates / bounces exact; the floats as the float the readback carries.
                    if (g.Gate != c.Gate || g.Bounces != c.Bounces || g.EnteredChamber != c.EnteredChamber
                        || g.Throughput != (float)c.Throughput || g.PathLength != (float)c.PathLength
                        || (c.Bounces > 0 && g.FirstHit != (float)c.FirstHit))
                        mismatch++;
                    if (free)
                    {
                        var cf = scene.Trace(cam.X, cam.Y, cam.Z, dx, dy, dz, stopAtChamberExit: false);
                        var gf = outs.Free(i);
                        if (gf.Gate != cf.Gate || gf.Throughput != (float)cf.Throughput
                            || gf.EndDx != (float)cf.EndDx || gf.EndDy != (float)cf.EndDy || gf.EndDz != (float)cf.EndDz)
                            mismatch++;
                    }
                }
                Assert.True(deep > 20, $"precondition: only {deep} deep-scattered pixels");
                Assert.True(mismatch <= checkedPx / 1000, $"free={free}: {mismatch} of {checkedPx} pixels differ");
            }
            return 0;
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gpu_Frame_Matches_The_Cpu_Frame(bool photo)
    {
        OnCpuAccelerator(() =>
        {
            var g = Render(Params(gpu: true, photo ? Photo : null));
            Assert.Equal(GpuRouteState.Gpu, g.Route.State);
            var c = Render(Params(gpu: false, photo ? Photo : null));
            Assert.NotEqual(GpuRouteState.Gpu, c.Route.State);
            double same = SameFraction(g.Color, c.Color);
            Assert.True(same >= 0.999, $"photo={photo}: GPU and CPU frames agree on {same:P2} of pixels");
            return 0;
        });
    }

    [Fact]
    public void Gpu_Follows_Radius_And_Fresnel()
    {
        OnCpuAccelerator(() =>
        {
            var baseCpu = Render(Params(gpu: false)).Color;
            var smaller = Render(Params(gpu: true, p => p.MirrorSpheresRadiusScale = 0.8)).Color;
            Assert.True(SameFraction(smaller, baseCpu) < 0.9, "GPU ignored the radius");

            var fresOn = Render(Params(gpu: true, Photo)).Color;
            var fresOff = Render(Params(gpu: true, p => { Photo(p); p.MirrorSpheresFresnel = false; })).Color;
            Assert.True(SameFraction(fresOn, fresOff) < 0.9, "GPU ignored the Fresnel switch");
            return 0;
        });
    }

    [Fact]
    public void LowRes_Preview_Traces_On_The_Cpu()
    {
        OnCpuAccelerator(() =>
        {
            var c = new MirrorSpheresCalculator(W, H) { FractalParameters = Params(gpu: true), LowResPreview = true };
            c.Calculate(CancellationToken.None);
            Assert.Equal(GpuRouteState.CpuFallback, c.LastGpuRoute.State);
            return 0;
        });
    }

    [Fact]
    public void Gpu_Depth_Matches_The_Cpu_Depth()
    {
        OnCpuAccelerator(() =>
        {
            float[] Depth(bool gpu)
            {
                var p = Params(gpu, q =>
                {
                    var fx = q.Lighting;
                    fx.StereoMode = StereoMode.Autostereogram;
                    q.Lighting = fx;
                });
                var c = new MirrorSpheresCalculator(W, H) { FractalParameters = p };
                c.Calculate(CancellationToken.None);
                Assert.NotNull(c.DepthBuffer);
                return (float[])c.DepthBuffer!.Clone();
            }
            var g = Depth(true);
            var cpu = Depth(false);
            int same = 0;
            for (int i = 0; i < g.Length; i++) if (g[i] == cpu[i]) same++;
            Assert.True(same >= g.Length * 999 / 1000, $"depth agrees on {same}/{g.Length}");
            return 0;
        });
    }

    [Fact]
    public void Auto_Measures_Once_Then_Keeps_The_Faster_Path_With_The_Same_Image()
    {
        OnCpuAccelerator(() =>
        {
            var c = new MirrorSpheresCalculator(W, H) { FractalParameters = Params(gpu: true) };
            c.Calculate(CancellationToken.None);
            Assert.Equal(GpuRouteState.Gpu, c.LastGpuRoute.State);   // the measuring frame runs on the GPU
            var m = c.LastGpuMeasurement;
            Assert.NotNull(m);
            Assert.True(m!.CpuNsPerPixel > 0 && m.GpuNsPerPixel > 0);
            var first = (uint[])c.ColorBuffer.Clone();

            c.Calculate(CancellationToken.None);
            Assert.Same(m, c.LastGpuMeasurement);   // cached, not re-measured
            var expected = m.GpuFaster ? GpuRouteState.Gpu : GpuRouteState.CpuFallback;
            Assert.Equal(expected, c.LastGpuRoute.State);
            Assert.Equal(first, c.ColorBuffer);      // either path, the same frame
            return 0;
        }, MirrorSpheresCalculator.GpuMirrorSpheresMode.Auto);
    }

    [Fact]
    public void Auto_Remeasures_When_The_Frame_Size_Moves_Far()
    {
        const int measured = 640 * 360, fullHd = 1920 * 1080;
        var m = new MirrorSpheresCalculator.GpuMeasurement(10, 20, measured);
        Assert.True(m.Covers(measured * 3));
        Assert.False(m.Covers(fullHd * 2));
        Assert.False(m.GpuFaster);
    }

    [Fact]
    public void Forced_Cpu_Mode_Traces_On_The_Cpu()
    {
        OnCpuAccelerator(() =>
        {
            var c = new MirrorSpheresCalculator(W, H) { FractalParameters = Params(gpu: true) };
            c.Calculate(CancellationToken.None);
            Assert.Equal(GpuRouteState.CpuFallback, c.LastGpuRoute.State);
            Assert.Null(c.LastGpuMeasurement);
            return 0;
        }, MirrorSpheresCalculator.GpuMirrorSpheresMode.Cpu);
    }
}
