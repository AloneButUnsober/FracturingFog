// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1170 — the 3D GPU kernels are dispatched in several watchdog-sized launches
// (GpuTiledDispatch) instead of one launch per frame. Splitting the frame must
// not change a single pixel: every thread still shades exactly one pixel, and
// every pixel is shaded exactly once. The reference is the untiled single
// launch (the pre-#1170 dispatch), so a pixel the comb skips or shades twice
// shows up as a colour or depth mismatch.
//
// The kernels run on the ILGPU CPU accelerator (GpuAcceleratorHost
// .SetTestOverride), so this runs on any host. The tiling thresholds are
// lowered so a small frame takes several launches.

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

public sealed class S1170GpuTiledDispatchTests
{
    private const int W = 64, H = 48;
    private const uint InSet = 0xFF000000u;

    private static T WithCpuAccelerator<T>(Func<T> body)
    {
        using var ctx = GpuAcceleratorHost.CreateContext();
        using var acc = ctx.Devices.First(d => d.AcceleratorType == AcceleratorType.CPU).CreateAccelerator(ctx);
        GpuAcceleratorHost.SetTestOverride(acc);
        try { return body(); }
        finally { GpuAcceleratorHost.SetTestOverride(null); }
    }

    /// <summary>Run <paramref name="body"/> with tiling forced on for small
    /// frames: no single-launch shortcut and a tiny probe, so a 64x48 frame
    /// takes several comb launches.</summary>
    /// <summary>Run <paramref name="body"/> with tiling off: the whole frame in one
    /// launch, the untiled reference.</summary>
    private static void Single(Action body)
    {
        GpuTiledDispatch.SetTestThresholds(singleLaunchPixels: int.MaxValue, probePixels: 64);
        try { body(); }
        finally { GpuTiledDispatch.ClearTestThresholds(); }
    }

    private static T Tiled<T>(Func<T> body)
    {
        GpuTiledDispatch.SetTestThresholds(singleLaunchPixels: 0, probePixels: 64);
        try { return body(); }
        finally { GpuTiledDispatch.ClearTestThresholds(); }
    }

    private static GpuRaymarchParams Camera(double camDist)
    {
        double fov = Math.Tan(Math.PI / 6);
        return new GpuRaymarchParams
        {
            Width = W, Height = H,
            CamX = 0, CamY = 0, CamZ = camDist,
            FwdX = 0, FwdY = 0, FwdZ = -1,
            RightX = 1, RightY = 0, RightZ = 0,
            UpX = 0, UpY = 1, UpZ = 0,
            FovScale = fov, Aspect = (double)W / H,
            LightX = 0.577, LightY = 0.577, LightZ = 0.577,
            MaxSteps = 200, Eps = 1e-4, InSetColor = InSet,
        };
    }

    private static void AssertSameFrame(uint[] single, uint[] tiled, int tiledLaunches)
    {
        Assert.True(tiledLaunches > 1, $"expected several launches, got {tiledLaunches}");
        Assert.Contains(single, c => c != InSet);       // the view hits the fractal…
        Assert.Contains(single, c => c == InSet);       // …and misses it somewhere
        Assert.Equal(single, tiled);
    }

    [Fact]
    public void Mandelbulb_Tiled_Frame_And_Depth_Match_Single_Launch()
    {
        var rp = Camera(2.6);
        var bp = new MandelbulbGpuParams { Power = 8, DEIter = 8, Bailout = 2.0, SceneRadius = 12.0 };
        var sp = GpuShadingParams.Build(LightingFxData.CreateDefault());

        var (single, singleDepth, tiled, tiledDepth, launches) = WithCpuAccelerator(() =>
        {
            var gpu = new MandelbulbGpuCalculator();
            var a = new uint[W * H]; var ad = new float[W * H];
            Single(() => Assert.True(gpu.Render(a, rp, sp, bp, null, ad), gpu.LastError));
            Assert.Equal(1, GpuTiledDispatch.LastLaunchCount);

            var b = new uint[W * H]; var bd = new float[W * H];
            int n = Tiled(() =>
            {
                Assert.True(gpu.Render(b, rp, sp, bp, null, bd), gpu.LastError);
                return GpuTiledDispatch.LastLaunchCount;
            });
            return (a, ad, b, bd, n);
        });

        AssertSameFrame(single, tiled, launches);
        Assert.Equal(singleDepth, tiledDepth);
    }

    [Fact]
    public void Mandelbox_Tiled_Frame_Matches_Single_Launch()
    {
        var rp = Camera(12.0);
        var bp = new MandelboxGpuParams
        {
            Scale = -1.5, FixedR2 = 1.0, MinR2 = 0.25,
            Bailout2 = 256.0, DEIter = 12, SceneRadius = 20.0,
        };
        var sp = GpuShadingParams.Build(LightingFxData.CreateDefault());

        var (single, tiled, launches) = WithCpuAccelerator(() =>
        {
            var gpu = new MandelboxGpuCalculator();
            var a = new uint[W * H];
            Single(() => Assert.True(gpu.Render(a, rp, sp, bp), gpu.LastError));

            var b = new uint[W * H];
            int n = Tiled(() =>
            {
                Assert.True(gpu.Render(b, rp, sp, bp), gpu.LastError);
                return GpuTiledDispatch.LastLaunchCount;
            });
            return (a, b, n);
        });

        AssertSameFrame(single, tiled, launches);
    }

    [Fact]
    public void Cancelled_Tiled_Render_Stops_Before_Launching()
    {
        var rp = Camera(2.6);
        var bp = new MandelbulbGpuParams { Power = 8, DEIter = 8, Bailout = 2.0, SceneRadius = 12.0 };
        var sp = GpuShadingParams.Build(LightingFxData.CreateDefault());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (ok, launches) = WithCpuAccelerator(() => Tiled(() =>
        {
            var gpu = new MandelbulbGpuCalculator();
            bool r = gpu.Render(new uint[W * H], rp, sp, bp, null, null, cts.Token);
            return (r, GpuTiledDispatch.LastLaunchCount);
        }));

        Assert.False(ok);           // caller falls through to the CPU path, which observes ct
        Assert.Equal(0, launches);
    }

    // ── #1169 ────────────────────────────────────────────────────────────

    [Fact]
    public void Default_Thresholds_Tile_Even_A_Small_Frame()
    {
        // #1169 — a 96x72 Mandelbulb frame went out as ONE launch and outlasted
        // the watchdog on a GT 710. Every frame above SingleLaunchPixels is now
        // tiled, starting from a ~ProbePixels launch.
        Assert.True(W * H > GpuTiledDispatch.SingleLaunchPixels);
        var rp = Camera(2.6);
        var bp = new MandelbulbGpuParams { Power = 8, DEIter = 8, Bailout = 2.0, SceneRadius = 12.0 };
        var sp = GpuShadingParams.Build(LightingFxData.CreateDefault());
        int launches = WithCpuAccelerator(() =>
        {
            var gpu = new MandelbulbGpuCalculator();
            Assert.True(gpu.Render(new uint[W * H], rp, sp, bp), gpu.LastError);
            return GpuTiledDispatch.LastLaunchCount;
        });
        Assert.True(launches > 1, $"expected a tiled frame, got {launches} launch(es)");
    }

    [Fact]
    public void Too_Slow_Device_Gives_Up_And_Stays_On_The_Cpu()
    {
        var rp = Camera(2.6);
        var bp = new MandelbulbGpuParams { Power = 8, DEIter = 8, Bailout = 2.0, SceneRadius = 12.0 };
        var sp = GpuShadingParams.Build(LightingFxData.CreateDefault());
        var (first, firstErr, firstLaunches, second, secondLaunches) = WithCpuAccelerator(() =>
        {
            // Any measurable cost counts as "too slow", and the CPU accelerator is judged.
            GpuTiledDispatch.SetTestThresholds(singleLaunchPixels: 0, probePixels: 8,
                maxSecondsPerPixel: 0.0, minGuardSeconds: 0.0, judgeCpuAccelerator: true);
            try
            {
                var gpu = new MandelbulbGpuCalculator();
                bool a = gpu.Render(new uint[W * H], rp, sp, bp);
                string err = gpu.LastError;
                int n1 = GpuTiledDispatch.LastLaunchCount;
                bool b = gpu.Render(new uint[W * H], rp, sp, bp);
                int n2 = GpuTiledDispatch.LastLaunchCount;   // unchanged: the per-device latch skips Run entirely
                return (a, err, n1, b, n2);
            }
            finally { GpuTiledDispatch.ClearTestThresholds(); }
        });

        Assert.False(first);
        Assert.StartsWith("GPU too slow for Mandelbulb", firstErr);
        Assert.Equal(GpuTiledDispatch.TooSlowStrikes, firstLaunches);   // gave up after N over-budget launches
        Assert.False(second);
        Assert.Equal(firstLaunches, secondLaunches);
        Assert.Equal("GPU too slow", Gpu3DRoute.AfterRender(false, firstErr).Reason);
    }
}
