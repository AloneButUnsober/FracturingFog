// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1787 (#1665 S3, folds in #1493) — the deep-zoom route in the UI:
//   • the Control Center's GPU deep zoom toggle (MandelbrotCalculator.GpuPerturbUserOverride):
//     on and off beat the env switch, the Vulkan default and the store; on re-arms the
//     session latch; it is disabled with a reason when the device can't do deep zoom;
//   • the CPU route reasons say how to turn GPU deep zoom on, or that it was measured
//     slower; the latch reasons name FF_GPU_PERTURB_BUDGET_MS and the toggle (on real
//     deep frames with a fake kernel);
//   • the perf HUD's deep line: each choice with its source;
//   • the host (IFractalRenderHost): toggle, reason, the stored calibration as text,
//     re-run (raises the completion event) and reset;
//   • the menu VM seeds without echoing and shows the reason as its tooltip;
//   • the Command builder reports a set toggle as a batch fidelity gap (#1788).
// Mutants (by hand): see the PR.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FracturingFog.Cli;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // route statics + the store (run alone)
public sealed class S1787DeepRouteUiTests : IDisposable
{
    private readonly bool _cal = MandelbrotCalculator.UseDeepRouteCalibration, _gp = MandelbrotCalculator.UseGpuPerturbation,
        _gpDefault = MandelbrotCalculator.UseGpuPerturbationIsDefault, _gla = MandelbrotCalculator.UseGpuLa;
    private readonly bool? _envGp = MandelbrotCalculator.GpuPerturbEnvSwitch, _user = MandelbrotCalculator.GpuPerturbUserOverride,
        _envGla = MandelbrotCalculator.GpuLaEnvSwitch, _envSimd = MandelbrotCalculator.LaSimdEnvSwitch;
    private readonly GpuDeepPrecision _prec = MandelbrotCalculator.GpuDeepPrecisionMode;

    public S1787DeepRouteUiTests()
    {
        DeepRouteStore.Reset();
        DeepRouteCalibration.ResetForTests();
        MandelbrotCalculator.ResetGpuPerturbationLatchForTests();
        MandelbrotCalculator.UseDeepRouteCalibration = true;
        MandelbrotCalculator.UseGpuPerturbation = false;
        MandelbrotCalculator.UseGpuPerturbationIsDefault = false;
        MandelbrotCalculator.UseGpuLa = false;
        MandelbrotCalculator.GpuPerturbEnvSwitch = null;
        MandelbrotCalculator.GpuPerturbUserOverride = null;
        MandelbrotCalculator.GpuLaEnvSwitch = null;
        MandelbrotCalculator.LaSimdEnvSwitch = null;
        MandelbrotCalculator.GpuDeepPrecisionMode = GpuDeepPrecision.Auto;
    }

    public void Dispose()
    {
        DeepRouteCalibration.ResetForTests();
        DeepRouteCalibration.RunnerFactory = null;
        DeepRouteStore.Reset();
        MandelbrotCalculator.ResetGpuPerturbationLatchForTests();
        MandelbrotCalculator.UseDeepRouteCalibration = _cal;
        MandelbrotCalculator.UseGpuPerturbation = _gp;
        MandelbrotCalculator.UseGpuPerturbationIsDefault = _gpDefault;
        MandelbrotCalculator.UseGpuLa = _gla;
        MandelbrotCalculator.GpuPerturbEnvSwitch = _envGp;
        MandelbrotCalculator.GpuPerturbUserOverride = _user;
        MandelbrotCalculator.GpuLaEnvSwitch = _envGla;
        MandelbrotCalculator.LaSimdEnvSwitch = _envSimd;
        MandelbrotCalculator.GpuDeepPrecisionMode = _prec;
    }

    private sealed class FakeKernel(string? key, bool fp64 = true, bool fx = true, string? throwMessage = null) : IGpuKernel
    {
        public int Calls;
        public bool HasGpuPalette => false;
        public double LastDispatchMs => 0;
        public double LastReadbackMs => 0;
        public bool SupportsPerturbation => fp64;
        public bool SupportsPerturbationFloatExp => fx;
        public string? DeviceKey => key;
        public string? DeviceName => "Fake GPU";
        public void SetPalette(IGpuHlslPalette? palette) { }
        public void Run(int width, int height, double centerX, double centerY, double scale, int maxIter, double bailout2,
            int[] iterDst, float[] smoothDst, float[] finalZrDst, float[] finalZiDst, float[] finalDrDst, float[] finalDiDst,
            int[]? perRowMaxIter = null, FractalKind kind = FractalKind.Mandelbrot, float param0 = 0f, float param1 = 0f,
            uint[]? colorDst = null, float[]? trapDst = null, GpuDomainWarp warp = default) { }
        public void RunPerturb(int width, int height, double scale, int maxIter, double escapeRadius2, double offsetX0, double offsetY0,
            double[] refZr, double[] refZi, int refLen, int[] iterDst, float[] smoothDst, float[] finalZrDst, float[] finalZiDst,
            float[] finalDrDst, float[] finalDiDst) { Calls++; throw new InvalidOperationException(throwMessage ?? "test: no frame"); }
        public void RunPerturbFloatExp(int width, int height, double scale, int maxIter, double escapeRadius2, double offsetX0, double offsetY0,
            double[] refZr, double[] refZi, int refLen, int[] iterDst, float[] smoothDst, float[] finalZrDst, float[] finalZiDst,
            float[] finalDrDst, float[] finalDiDst) { Calls++; throw new InvalidOperationException(throwMessage ?? "test: no frame"); }
        public void Dispose() { }
    }

    private const string Dev = "fake|ui|1";

    private static MandelbrotCalculator Deep(IGpuKernel? k) => new(48, 32)
    {
        Quality = QualityPreset.Extreme, Zoom = 1e20, MaxIterations = 2000, ColorMap = new HsvPalette(), LaMode = DeepLaMode.Off,
        CenterX = -0.7145519151951202, CenterXLo = -2.0790192891565988e-17, CenterY = -0.35402073332318235, CenterYLo = 2.5477179573089096e-17,
        GpuKernel = k,
    };

    // ── the toggle ───────────────────────────────────────────────────────────

    [Fact]
    public void The_Toggle_Beats_Env_Default_And_Store_Either_Way()
    {
        var k = new FakeKernel(Dev);
        DeepRouteStore.StoreGpu(Dev, new DeepRouteGpuResult { GpuDeep = true });
        MandelbrotCalculator.SetGpuPerturbationFromUser(false);
        Assert.False(MandelbrotCalculator.GpuPerturbationEnabled(k));     // over a measured "faster"
        MandelbrotCalculator.UseGpuPerturbation = true;                   // and an explicit / Vulkan default on
        Assert.False(MandelbrotCalculator.GpuPerturbationEnabled(k));
        MandelbrotCalculator.UseGpuPerturbation = false;
        DeepRouteStore.StoreGpu(Dev, new DeepRouteGpuResult { GpuDeep = false });
        MandelbrotCalculator.GpuPerturbEnvSwitch = false;                 // FF_GPU_PERTURB=0
        MandelbrotCalculator.SetGpuPerturbationFromUser(true);
        Assert.True(MandelbrotCalculator.GpuPerturbationEnabled(k));      // over env 0 and a measured "slower"
    }

    [Fact]
    public void Turning_It_On_Rearms_The_Session_Latch()
    {
        MandelbrotCalculator.UseGpuPerturbation = true;
        var k = new FakeKernel(Dev, throwMessage: "GPU-PERTURB-TOO-SLOW: band0=99ms");
        var c = Deep(k);
        c.Calculate(CancellationToken.None);
        Assert.False(MandelbrotCalculator.GpuPerturbationLatched);        // #2012: one too-slow frame only falls back
        c.Calculate(CancellationToken.None);
        Assert.True(MandelbrotCalculator.GpuPerturbationLatched);         // precondition: two in a row latched
        Assert.False(MandelbrotCalculator.GpuPerturbationEnabled(k));
        // The latching frame reports the GPU error; from the next frame on, the latch.
        var next = Deep(k);
        next.UseGpuCompute = true;
        next.Calculate(CancellationToken.None);
        Assert.Equal("deep zoom too slow", next.LastGpuRoute.Reason);
        string detail = next.LastGpuRoute.Detail ?? "";
        Assert.Contains("FF_GPU_PERTURB_BUDGET_MS", detail);
        Assert.Contains("GPU deep zoom", detail);
        Assert.Equal(DeepRouteSource.Latch, c.DescribeDeepRoute().GpuDeepSource);

        MandelbrotCalculator.SetGpuPerturbationFromUser(true);
        Assert.False(MandelbrotCalculator.GpuPerturbationLatched);
        Assert.True(MandelbrotCalculator.GpuPerturbationEnabled(k));
        int before = k.Calls;
        Deep(k).Calculate(CancellationToken.None);
        Assert.True(k.Calls > before, "re-armed: the next deep frame tries the GPU again");
    }

    [Fact]
    public void The_Toggle_Is_Unavailable_With_A_Reason_Where_The_Device_Cannot()
    {
        Assert.Contains("No GPU compute kernel", MandelbrotCalculator.GpuDeepUnavailableReason(null));
        Assert.Null(MandelbrotCalculator.GpuDeepUnavailableReason(new FakeKernel(Dev)));
        Assert.Null(MandelbrotCalculator.GpuDeepUnavailableReason(new FakeKernel(Dev, fp64: false)));   // fp32 kernels
        MandelbrotCalculator.GpuDeepPrecisionMode = GpuDeepPrecision.Double;
        Assert.Contains("FF_GPU_PERTURB_PRECISION=double", MandelbrotCalculator.GpuDeepUnavailableReason(new FakeKernel(Dev, fp64: false)));
        Assert.Contains("no fp64", MandelbrotCalculator.GpuDeepUnavailableReason(new FakeKernel(Dev, fp64: false, fx: false)));
    }

    // ── route reasons ────────────────────────────────────────────────────────

    [Fact]
    public void A_Deep_Frame_On_The_Cpu_Says_How_To_Turn_The_Gpu_On()
    {
        var k = new FakeKernel(Dev);
        var c = Deep(k);
        c.UseGpuCompute = true;   // GPU asked for, deep zoom off
        c.Calculate(CancellationToken.None);
        Assert.Equal("deep zoom", c.LastGpuRoute.Reason);
        Assert.Contains("GPU deep zoom", c.LastGpuRoute.Detail);
        Assert.Contains("FF_GPU_PERTURB=1", c.LastGpuRoute.Detail);
        Assert.Equal(0, k.Calls);

        DeepRouteStore.StoreGpu(Dev, new DeepRouteGpuResult { GpuDeep = false });
        c.Calculate(CancellationToken.None);
        Assert.Contains("measured faster than this GPU", c.LastGpuRoute.Detail);

        MandelbrotCalculator.SetGpuPerturbationFromUser(false);
        c.Calculate(CancellationToken.None);
        Assert.Contains("off in the Control Center", c.LastGpuRoute.Detail);
        Assert.True((c.LastGpuRoute.StatusTag ?? "").Length < 40, "the status bar tag stays short");
    }

    // ── the HUD line ─────────────────────────────────────────────────────────

    [Fact]
    public void The_Hud_Line_Names_Each_Choice_And_Its_Source()
    {
        var k = new FakeKernel(Dev);
        var c = Deep(k);
        c.LaMode = DeepLaMode.Auto;
        var d = c.DescribeDeepRoute();
        Assert.Equal((false, DeepRouteSource.Default), (d.GpuDeep, d.GpuDeepSource));
        Assert.Equal((1e30, DeepRouteSource.Default), (d.LaMinZoom, d.LaSource));
        Assert.Equal(DeepRouteSource.Default, d.LaSimdSource);

        DeepRouteStore.StoreCpu(new DeepRouteCpuResult { LaMinZoom = 1e27, LaSimd = true });
        DeepRouteStore.StoreGpu(Dev, new DeepRouteGpuResult { GpuDeep = true, GpuLa = false });
        d = c.DescribeDeepRoute();
        Assert.Equal((true, DeepRouteSource.Measured), (d.GpuDeep, d.GpuDeepSource));
        Assert.Equal((1e27, DeepRouteSource.Measured), (d.LaMinZoom, d.LaSource));
        Assert.Equal(DeepRouteSource.Measured, d.LaSimdSource);
        Assert.Equal(DeepRouteSource.Measured, d.GpuLaSource);
        Assert.Contains("LA ≥1e27 measured", d.HudLine);
        Assert.Contains("GPU on measured", d.HudLine);
        Assert.StartsWith("deep   ", d.HudLine);

        MandelbrotCalculator.GpuPerturbEnvSwitch = false;
        Assert.Equal((false, DeepRouteSource.Env), (c.DescribeDeepRoute().GpuDeep, c.DescribeDeepRoute().GpuDeepSource));
        MandelbrotCalculator.SetGpuPerturbationFromUser(true);
        Assert.Equal((true, DeepRouteSource.Toggle), (c.DescribeDeepRoute().GpuDeep, c.DescribeDeepRoute().GpuDeepSource));
        MandelbrotCalculator.GpuPerturbUserOverride = null;
        MandelbrotCalculator.GpuPerturbEnvSwitch = null;
        MandelbrotCalculator.UseGpuPerturbation = true;
        MandelbrotCalculator.UseGpuPerturbationIsDefault = true;
        DeepRouteStore.StoreGpu(Dev, new DeepRouteGpuResult());
        Assert.Equal((true, DeepRouteSource.BackendDefault), (c.DescribeDeepRoute().GpuDeep, c.DescribeDeepRoute().GpuDeepSource));
        DeepRouteStore.StoreGpu(Dev, new DeepRouteGpuResult { GpuDeep = false });
        Assert.Equal((false, DeepRouteSource.Measured), (c.DescribeDeepRoute().GpuDeep, c.DescribeDeepRoute().GpuDeepSource));
        c.LaMode = DeepLaMode.Off;
        if (MandelbrotCalculator.LaModeOverride == null)
            Assert.Equal((DeepLaMode.Off, DeepRouteSource.Setting), (c.DescribeDeepRoute().LaMode, c.DescribeDeepRoute().LaSource));
        Assert.Contains("calibrating", new DeepRouteDescription(false, default, 1e30, default, DeepLaMode.Auto, false, default, false, default,
            DeepRouteCalibrationState.Running).HudLine);
    }

    // ── the host ─────────────────────────────────────────────────────────────

    private sealed class NullRenderer : IFractalRenderer
    {
        public void UpdateTexture(uint[] colorBuffer, int width, int height) { }
        public void Render() { }
        public void Resize(int width, int height) { }
        public string RendererDescription => "null";
        public bool VSync { get; set; }
        public void Dispose() { }
    }

    private sealed class FakeRunner : IDeepRouteRunner
    {
        public IReadOnlyList<DeepRouteCandidate> Candidates => [DeepRouteCandidate.CpuLa, DeepRouteCandidate.CpuBla];
        public string? GpuDeviceKey => null;
        public DeepRouteRun Run(DeepRouteScene scene, DeepRouteCandidate route, CancellationToken ct)
            => new(route == DeepRouteCandidate.CpuLa ? 10 : 20, new int[16], false);
    }

    [Fact]
    public void The_Host_Exposes_Toggle_Reason_Summary_Rerun_And_Reset()
    {
        using var host = new FractalRenderHost(new NullRenderer(), new FractalViewState(), 64, 48, ColorPalette.BuiltIns[0]);
        FracturingFog.Render.IFractalRenderHost h = host;
        Assert.NotNull(h.GpuDeepZoomUnavailableReason);                   // no kernel attached
        Assert.False(h.GpuDeepZoom);
        Assert.Null(h.GpuDeepZoomUserSetting);
        Assert.Contains("not measured yet", h.DeepRouteSummary);

        var k = new FakeKernel(Dev);
        host.Mandelbrot.GpuKernel = k;
        Assert.Null(h.GpuDeepZoomUnavailableReason);
        h.GpuDeepZoom = true;
        Assert.True(h.GpuDeepZoom);
        Assert.True(h.GpuDeepZoomUserSetting);
        h.GpuDeepZoom = false;
        Assert.False(h.GpuDeepZoom);
        Assert.False(h.GpuDeepZoomUserSetting);

        DeepRouteStore.StoreCpu(new DeepRouteCpuResult { LaMinZoom = 1e27, LaSimd = true, Measured = "2026-11-15T10:00:00Z" });
        DeepRouteStore.StoreGpu(Dev, new DeepRouteGpuResult { GpuDeep = false, GpuLa = false });
        string s = h.DeepRouteSummary;
        Assert.Contains("LA from 1e27, LA SIMD on (measured 2026-11-15)", s);
        Assert.Contains("GPU (Fake GPU): deep zoom off, LA kernel off, kernel default", s);
        h.ResetDeepRouteCalibration();
        Assert.Null(DeepRouteStore.LookupCpu());
        Assert.Contains("not measured yet", h.DeepRouteSummary);

        using var done = new SemaphoreSlim(0);
        EventHandler onDone = (_, _) => done.Release();
        h.DeepRouteCalibrationCompleted += onDone;
        DeepRouteCalibration.RunnerFactory = _ => new FakeRunner();
        Assert.True(h.RerunDeepRouteCalibration());
        Assert.True(done.Wait(TimeSpan.FromSeconds(30)), "the completion event");
        h.DeepRouteCalibrationCompleted -= onDone;
        Assert.NotNull(DeepRouteStore.LookupCpu());
        Assert.Contains("CPU: LA", h.DeepRouteSummary);

        MandelbrotCalculator.UseDeepRouteCalibration = false;
        Assert.False(h.RerunDeepRouteCalibration());
        Assert.Contains("FF_DEEP_ROUTE_CALIBRATION=0", h.DeepRouteSummary);
    }

    // ── the menu VM and the Command builder ──────────────────────────────────

    [Fact]
    public void The_Menu_Seeds_Without_Echo_And_Shows_The_Reason()
    {
        var vm = new FracturingFog.UI.Avalonia.ViewModels.FloatingMenuViewModel();
        bool? got = null;
        vm.UseGpuDeepZoomToggled += (_, v) => got = v;
        vm.SetGpuDeepZoomState(true, null);
        Assert.True(vm.UseGpuDeepZoom);
        Assert.True(vm.GpuDeepZoomAvailable);
        Assert.Null(got);                                                 // seeding does not echo back
        Assert.Contains("weak fp64", vm.GpuDeepZoomTooltip);
        vm.SetGpuDeepZoomState(false, "no fp64 here");
        Assert.False(vm.GpuDeepZoomAvailable);
        Assert.Equal("Unavailable: no fp64 here", vm.GpuDeepZoomTooltip);
        vm.UseGpuDeepZoom = true;
        Assert.True(got);
        vm.DeepRouteSummary = "CPU: LA from 1e27";
        Assert.Equal("CPU: LA from 1e27", vm.DeepRouteSummary);
    }

    [Fact]
    public void A_Set_Toggle_Is_No_Longer_A_Batch_Fidelity_Gap()
    {
        Assert.DoesNotContain(BatchCommandBuilder.DetectGaps(new BatchCommandSnapshot()), g => g.Contains("GPU deep zoom"));
        // #1788: --gpu-deep carries the toggle now; no gap either way (S1788 checks the flag).
        Assert.DoesNotContain(BatchCommandBuilder.DetectGaps(new BatchCommandSnapshot { GpuDeepZoomToggle = true }), g => g.Contains("GPU deep zoom"));
        Assert.DoesNotContain(BatchCommandBuilder.DetectGaps(new BatchCommandSnapshot { GpuDeepZoomToggle = false }), g => g.Contains("GPU deep zoom"));
    }
}
