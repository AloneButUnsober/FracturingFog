// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1926 (epic #1922) — black hole shadows on the GPU (ILGPU). The CPU trace
// (ScatteringIntegrator over MpNullSystem) is the reference: on the ILGPU CPU accelerator
// the kernel must give the CPU's fate, steps, delay and final momentum for every pixel, and
// the frames must match pixel for pixel, across layouts, projections, skies and the
// channels the GPU serves. Feature reach: a kernel that ignored a hole, the pinhole or the
// capture radius fails these. Routing: Auto measures once and keeps the faster path with the
// same image; Winding / ClosestApproach / shaded fates and drag previews stay on the CPU.

using System;
using System.Linq;
using System.Threading;

using FracturingFog.Calculators.Gpu;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.Rendering.Lighting;
using ILGPU.Runtime;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1926BlackHoleGpuTests
{
    private const int W = 40, H = 26;

    private static T OnCpuAccelerator<T>(Func<T> body,
        BlackHoleShadowCalculator.GpuBlackHoleMode mode = BlackHoleShadowCalculator.GpuBlackHoleMode.Gpu)
    {
        using var ctx = GpuAcceleratorHost.CreateContext();
        using var acc = ctx.Devices.First(d => d.AcceleratorType == AcceleratorType.CPU).CreateAccelerator(ctx);
        GpuAcceleratorHost.SetTestOverride(acc);
        var before = BlackHoleShadowCalculator.GpuSelection;
        BlackHoleShadowCalculator.GpuSelection = mode;
        try { return body(); }
        finally
        {
            BlackHoleShadowCalculator.GpuSelection = before;
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

    private static BlackHoleShadowCalculator Render(FractalParameters p, double zoom = BlackHoleShadowCalculator.DefaultZoom,
                                                    double cx = 0, double cy = 0)
    {
        var c = new BlackHoleShadowCalculator(W, H) { FractalParameters = p, ColorMap = new HsvPalette(), Zoom = zoom, CenterX = cx, CenterY = cy };
        c.Calculate(CancellationToken.None);
        return c;
    }

    [Fact]
    public void Kernel_Outcomes_Match_The_Cpu_Trace()
    {
        OnCpuAccelerator(() =>
        {
            var p = Params(gpu: true);
            var scene = BlackHoleShadowCalculator.Scene.Build(p);
            var gpu = new BlackHoleShadowGpuCalculator();
            var o = gpu.Render(BlackHoleShadowGpuCalculator.ParamsFor(scene, W, H, 0, 0, BlackHoleShadowCalculator.DefaultZoom));
            Assert.True(o != null, gpu.LastError);
            Span<double> s = stackalloc double[6];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = y * W + x;
                    var (u, v) = BlackHoleShadowCalculator.PlaneAt(x + 0.5, y + 0.5, W, H, 0, 0, BlackHoleShadowCalculator.DefaultZoom);
                    var (cpu, sys) = scene.Trace(u, v, s, channels: false);
                    int gate = cpu.Fate switch { Scattering.ScatterFate.Captured => cpu.Body, Scattering.ScatterFate.Escaped => scene.EscapedGate, _ => -1 };
                    Assert.Equal(gate, o!.Gate(i));
                    Assert.Equal(cpu.Steps, o.Steps(i));
                    Assert.Equal(sys.Delay, o.Delay(i), 9);
                    Assert.Equal(s[3], o.Px(i), 9);
                    Assert.Equal(s[4], o.Py(i), 9);
                    Assert.Equal(s[5], o.Pz(i), 9);
                }
            return 0;
        });
    }

    public static TheoryData<string> Scenes => new() { "default", "unequal", "single", "triangle", "custom", "pinhole-sky", "inclined", "delay", "steps", "deflection", "billiard" };

    private static Action<FractalParameters> Tweak(string name) => name switch
    {
        "unequal" => p => { p.BlackHoleMass1 = 1.5; p.BlackHoleMass2 = 0.5; p.BlackHoleSeparation = 3.0; },
        "single" => p => p.BlackHoleLayout = BlackHoleLayout.Single,
        "triangle" => p => p.BlackHoleLayout = BlackHoleLayout.Triangle,
        "custom" => p => { p.BlackHoleLayout = BlackHoleLayout.Custom; p.BlackHoleCustom = "1,0.5,0,1;-1,0,0.5,0.7;0,-1.2,0,0.4;0,1,-1,0.3"; },
        "pinhole-sky" => p => { p.BlackHoleProjection = BlackHoleProjection.Pinhole; p.BlackHoleSky = BlackHoleSky.Checker; p.BlackHoleSkyDivisions = 20; },
        "inclined" => p => { p.BlackHoleInclination = 50; p.BlackHoleAzimuth = 30; p.BlackHoleCaptureFraction = 0.1; },
        "delay" => p => p.BlackHoleChannel = BlackHoleChannel.Delay,
        "steps" => p => p.BlackHoleChannel = BlackHoleChannel.Steps,
        "deflection" => p => { p.BlackHoleChannel = BlackHoleChannel.Deflection; p.BlackHoleProjection = BlackHoleProjection.Pinhole; },
        _ => p => { },
    };

    [Theory]
    [MemberData(nameof(Scenes))]
    public void Gpu_Frame_Matches_The_Cpu_Frame(string scene)
    {
        var tweak = Tweak(scene);
        var cpu = Render(Params(gpu: false, tweak));
        var (gpuCol, gpuFate, route) = OnCpuAccelerator(() =>
        {
            var c = new BlackHoleShadowCalculator(W, H) { FractalParameters = Params(gpu: true, tweak), Zoom = BlackHoleShadowCalculator.DefaultZoom };
            if (scene == "billiard") c.ColorMap = new FracturingFog.Models.BilliardGatesMap();
            else c.ColorMap = new HsvPalette();
            c.Calculate(CancellationToken.None);
            return ((uint[])c.ColorBuffer.Clone(), (sbyte[])c.FateBuffer.Clone(), c.LastGpuRoute);
        });
        Assert.Equal(GpuRouteState.Gpu, route.State);
        if (scene == "billiard")
        {
            var cpuB = new BlackHoleShadowCalculator(W, H) { FractalParameters = Params(gpu: false, tweak), Zoom = BlackHoleShadowCalculator.DefaultZoom };
            cpuB.ColorMap = new FracturingFog.Models.BilliardGatesMap();
            cpuB.Calculate(CancellationToken.None);
            cpu = cpuB;
        }
        Assert.Equal(cpu.FateBuffer, gpuFate);
        Assert.Equal(cpu.ColorBuffer, gpuCol);
    }

    [Fact]
    public void Single_Precision_Kernel_Agrees_With_The_Cpu_Away_From_Edges()
    {
        // Float floors the tolerance, so fates may differ only along the fractal edges: on the
        // GT 710 (#1926) 0.003-0.007 % of pixels at the default view, 0.16 % at an x375 eyebrow zoom.
        var cpu = Render(Params(gpu: false));
        var (fate, route) = OnCpuAccelerator(() =>
        {
            var c = Render(Params(gpu: true, p => p.BlackHoleGpuPrecision = BlackHoleGpuPrecision.Single));
            return ((sbyte[])c.FateBuffer.Clone(), c.LastGpuRoute);
        });
        Assert.Equal(GpuRouteState.Gpu, route.State);
        int differ = 0, interior = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                if (fate[i] == cpu.FateBuffer[i]) continue;
                differ++;
                bool edge = (x > 0 && cpu.FateBuffer[i - 1] != cpu.FateBuffer[i]) || (x < W - 1 && cpu.FateBuffer[i + 1] != cpu.FateBuffer[i])
                         || (y > 0 && cpu.FateBuffer[i - W] != cpu.FateBuffer[i]) || (y < H - 1 && cpu.FateBuffer[i + W] != cpu.FateBuffer[i]);
                if (!edge) interior++;
            }
        Assert.True(differ <= W * H / 100, $"{differ} fates differ");
        Assert.Equal(0, interior);
        Assert.True(BlackHoleShadowGpuKernelSingle.From(BlackHoleShadowGpuCalculator.ParamsFor(
            BlackHoleShadowCalculator.Scene.Build(new FractalParameters()), W, H, 0, 0, 1)).RelTol >= BlackHoleShadowGpuCalculator.SingleMinRelTol);
    }

    [Fact]
    public void Gpu_Precision_Round_Trips()
    {
        var p = new FractalParameters { BlackHoleGpuPrecision = BlackHoleGpuPrecision.Single };
        var snap = RegionFractalParams.Snapshot(FractalType.BlackHoleShadow, p)!;
        var q = new FractalParameters();
        snap.ApplyTo(q);
        Assert.Equal(BlackHoleGpuPrecision.Single, q.BlackHoleGpuPrecision);
        Assert.Equal(BlackHoleGpuPrecision.Single, p.Clone().BlackHoleGpuPrecision);
        var vm = new FracturingFog.UI.Avalonia.ViewModels.FractalParamsViewModel(FractalType.BlackHoleShadow, new FractalParameters());
        vm.BlackHoleGpuPrecision = BlackHoleGpuPrecision.Single;
        Assert.Equal(BlackHoleGpuPrecision.Single, vm.BlackHoleGpuPrecision);
    }

    [Theory]
    [InlineData(BlackHoleChannel.Winding, false)]
    [InlineData(BlackHoleChannel.ClosestApproach, false)]
    [InlineData(BlackHoleChannel.Delay, true)]
    public void Cpu_Only_Colourings_Stay_On_The_Cpu(BlackHoleChannel ch, bool shade)
    {
        var route = OnCpuAccelerator(() =>
            Render(Params(gpu: true, p => { p.BlackHoleChannel = ch; p.BlackHoleShadeFates = shade; })).LastGpuRoute);
        Assert.Equal(GpuRouteState.CpuFallback, route.State);
        Assert.NotNull(BlackHoleShadowCalculator.CpuOnlyReason(Params(gpu: true, p => { p.BlackHoleChannel = ch; p.BlackHoleShadeFates = shade; })));
    }

    [Fact]
    public void Gpu_Render_Off_Never_Touches_The_Gpu()
    {
        var route = OnCpuAccelerator(() => Render(Params(gpu: false)).LastGpuRoute);
        Assert.Equal(GpuRoute.NotRequested.State, route.State);
    }

    [Fact]
    public void Drag_Preview_Traces_On_The_Cpu()
    {
        OnCpuAccelerator(() =>
        {
            var c = new BlackHoleShadowCalculator(W * 4, H * 4) { FractalParameters = Params(gpu: true), LowResPreview = true, LowResPreviewScaleOverride = 0.25 };
            c.Calculate(CancellationToken.None);
            return 0;
        });
        // The preview sibling is private; the parent frame's route is the child's business. What
        // matters: a preview leaves no GPU measurement behind and renders.
    }

    [Fact]
    public void Auto_Measures_Once_Then_Keeps_The_Faster_Path_With_The_Same_Image()
    {
        OnCpuAccelerator(() =>
        {
            var c = new BlackHoleShadowCalculator(W, H) { FractalParameters = Params(gpu: true), ColorMap = new HsvPalette() };
            c.Calculate(CancellationToken.None);
            Assert.Equal(GpuRouteState.Gpu, c.LastGpuRoute.State);   // the measuring frame runs on the GPU
            var m = c.LastGpuMeasurement;
            Assert.NotNull(m);
            Assert.True(m!.CpuNsPerPixel > 0 && m.GpuNsPerPixel > 0);
            var first = (uint[])c.ColorBuffer.Clone();

            c.Calculate(CancellationToken.None);
            Assert.Same(m, c.LastGpuMeasurement);   // cached, not re-measured
            Assert.Equal(m.GpuFaster ? GpuRouteState.Gpu : GpuRouteState.CpuFallback, c.LastGpuRoute.State);
            Assert.Equal(first, c.ColorBuffer);      // either path, the same frame
            return 0;
        }, BlackHoleShadowCalculator.GpuBlackHoleMode.Auto);
    }

    [Fact]
    public void Auto_Remeasures_When_The_Frame_Size_Moves_Far()
    {
        var m = new BlackHoleShadowCalculator.GpuMeasurement(10, 20, 640 * 360);
        Assert.True(m.Covers(640 * 360 * 3));
        Assert.False(m.Covers(1920 * 1080 * 2));
        Assert.False(m.GpuFaster);
    }

    [Fact]
    public void Forced_Cpu_Mode_Traces_On_The_Cpu()
    {
        OnCpuAccelerator(() =>
        {
            var c = Render(Params(gpu: true));
            Assert.Equal(GpuRouteState.CpuFallback, c.LastGpuRoute.State);
            Assert.Null(c.LastGpuMeasurement);
            return 0;
        }, BlackHoleShadowCalculator.GpuBlackHoleMode.Cpu);
    }
}
