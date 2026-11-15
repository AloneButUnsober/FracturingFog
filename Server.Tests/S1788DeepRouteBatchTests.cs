// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1788 (#1665 S4) — the deep-zoom route in --batch:
//   • --gpu-deep auto|on|off and --deep-route-calibration auto|off|force parse (defaults
//     auto; bad values name the flag) and are in the flag catalog;
//   • the Command builder emits --gpu-deep from the live Control Center toggle (only with
//     GPU compute on) and it round-trips; the old fidelity gap is gone;
//   • DeepRouteBatchSetup: --gpu-deep on/off is the toggle; on with --gpu off is an error;
//     off ignores the store; force runs a pass (on the batch's own device factory, none
//     with --gpu off) and stores it; auto never runs one; the stderr lines name the route.
// Mutants (by hand): see the PR.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // route statics + the store (run alone)
public sealed class S1788DeepRouteBatchTests : IDisposable
{
    private readonly bool _cal = MandelbrotCalculator.UseDeepRouteCalibration, _gp = MandelbrotCalculator.UseGpuPerturbation;
    private readonly bool? _envGp = MandelbrotCalculator.GpuPerturbEnvSwitch, _user = MandelbrotCalculator.GpuPerturbUserOverride;

    public S1788DeepRouteBatchTests()
    {
        DeepRouteStore.Reset();
        DeepRouteCalibration.ResetForTests();
        MandelbrotCalculator.ResetGpuPerturbationLatchForTests();
        MandelbrotCalculator.UseDeepRouteCalibration = true;
        MandelbrotCalculator.UseGpuPerturbation = false;
        MandelbrotCalculator.GpuPerturbEnvSwitch = null;
        MandelbrotCalculator.GpuPerturbUserOverride = null;
    }

    public void Dispose()
    {
        DeepRouteCalibration.ResetForTests();
        DeepRouteCalibration.RunnerFactory = null;
        DeepRouteCalibration.GpuKernelFactory = null;
        DeepRouteStore.Reset();
        MandelbrotCalculator.ResetGpuPerturbationLatchForTests();
        MandelbrotCalculator.UseDeepRouteCalibration = _cal;
        MandelbrotCalculator.UseGpuPerturbation = _gp;
        MandelbrotCalculator.GpuPerturbEnvSwitch = _envGp;
        MandelbrotCalculator.GpuPerturbUserOverride = _user;
    }

    private static readonly string[] Base = { "--x", "0", "--y", "0", "--zoom", "1", "--out", "o.png" };

    private static BatchOptions Parse(params string[] extra)
    {
        Assert.True(BatchOptions.TryParse(Base.Concat(extra).ToArray(), 0, out var o, out var err), err);
        return o;
    }

    // ── parse + catalog ──────────────────────────────────────────────────────

    [Fact]
    public void The_Flags_Parse_With_Auto_Defaults_And_Name_Themselves_On_A_Bad_Value()
    {
        var d = Parse();
        Assert.Equal((BatchGpuDeepMode.Auto, BatchDeepRouteMode.Auto), (d.GpuDeep, d.DeepRoute));
        Assert.Equal(BatchGpuDeepMode.On, Parse("--gpu-deep", "on").GpuDeep);
        Assert.Equal(BatchGpuDeepMode.Off, Parse("--gpu-deep", "OFF").GpuDeep);
        Assert.Equal(BatchGpuDeepMode.Auto, Parse("--gpu-deep", "auto").GpuDeep);
        Assert.Equal(BatchDeepRouteMode.Off, Parse("--deep-route-calibration", "off").DeepRoute);
        Assert.Equal(BatchDeepRouteMode.Force, Parse("--deep-route-calibration", "Force").DeepRoute);
        Assert.Equal(BatchDeepRouteMode.Auto, Parse("--deep-route-calibration", "auto").DeepRoute);
        Assert.False(BatchOptions.TryParse(Base.Concat(new[] { "--gpu-deep", "maybe" }).ToArray(), 0, out _, out var e1));
        Assert.Contains("--gpu-deep", e1);
        Assert.False(BatchOptions.TryParse(Base.Concat(new[] { "--deep-route-calibration", "now" }).ToArray(), 0, out _, out var e2));
        Assert.Contains("--deep-route-calibration", e2);
    }

    [Fact]
    public void The_Flags_Are_Catalogued_With_Their_Choices()
    {
        var gd = BatchFlagCatalog.All.Single(f => f.Name == BatchFlags.GpuDeep);
        Assert.Equal(new[] { "auto", "on", "off" }, gd.Choices);
        var dr = BatchFlagCatalog.All.Single(f => f.Name == BatchFlags.DeepRouteCalibration);
        Assert.Equal(new[] { "auto", "off", "force" }, dr.Choices);
        Assert.Contains("measured", dr.Help);
    }

    // ── the Command builder ──────────────────────────────────────────────────

    [Theory]
    [InlineData(null, true, null)]
    [InlineData(true, true, BatchGpuDeepMode.On)]
    [InlineData(false, true, BatchGpuDeepMode.Off)]
    [InlineData(true, false, null)]    // GPU compute off: the live view has no kernel either
    [InlineData(false, false, null)]
    public void The_Builder_Emits_The_Toggle_And_It_Round_Trips(bool? toggle, bool gpuCompute, BatchGpuDeepMode? expect)
    {
        var snap = new BatchCommandSnapshot { GpuDeepZoomToggle = toggle, GpuCompute = gpuCompute };
        string cmd = BatchCommandBuilder.Build(snap);
        Assert.Equal(expect != null, cmd.Contains("--gpu-deep", StringComparison.Ordinal));
        var argv = cmd.Replace("<OUTPUT.png>", "out.png").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(BatchOptions.TryParse(argv, 2, out var o, out var err), err);
        Assert.Equal(expect ?? BatchGpuDeepMode.Auto, o.GpuDeep);
        Assert.DoesNotContain(BatchCommandBuilder.DetectGaps(snap), g => g.Contains("GPU deep zoom"));
    }

    // ── DeepRouteBatchSetup ──────────────────────────────────────────────────

    private sealed class FakeRunner : IDeepRouteRunner
    {
        public int Runs;
        public IReadOnlyList<DeepRouteCandidate> Candidates => [DeepRouteCandidate.CpuLa, DeepRouteCandidate.CpuBla];
        public string? GpuDeviceKey => null;
        public DeepRouteRun Run(DeepRouteScene scene, DeepRouteCandidate route, CancellationToken ct)
        {
            Runs++;
            return new(route == DeepRouteCandidate.CpuLa ? 10 : 30, new int[16], false);
        }
    }

    private sealed class FakeKernel(string key) : IGpuKernel
    {
        public bool HasGpuPalette => false;
        public double LastDispatchMs => 0;
        public double LastReadbackMs => 0;
        public bool SupportsPerturbation => true;
        public string? DeviceKey => key;
        public void SetPalette(IGpuHlslPalette? palette) { }
        public void Run(int width, int height, double centerX, double centerY, double scale, int maxIter, double bailout2,
            int[] iterDst, float[] smoothDst, float[] finalZrDst, float[] finalZiDst, float[] finalDrDst, float[] finalDiDst,
            int[]? perRowMaxIter = null, FractalKind kind = FractalKind.Mandelbrot, float param0 = 0f, float param1 = 0f,
            uint[]? colorDst = null, float[]? trapDst = null, GpuDomainWarp warp = default) { }
        public void Dispose() { }
    }

    private static (bool Ok, string? Error, List<string> Log) Apply(BatchGpuDeepMode deep, BatchDeepRouteMode route, BatchGpuMode gpu = BatchGpuMode.Auto,
        Func<IGpuKernel?>? own = null)
    {
        var log = new List<string>();
        bool ok = DeepRouteBatchSetup.Apply(deep, route, gpu, own, log.Add, out var err);
        return (ok, err, log);
    }

    [Fact]
    public void Gpu_Deep_On_Off_Is_The_Toggle_And_On_Needs_A_Gpu()
    {
        var k = new FakeKernel("fake|batch");
        DeepRouteStore.StoreGpu("fake|batch", new DeepRouteGpuResult { GpuDeep = true });
        Assert.True(MandelbrotCalculator.GpuPerturbationEnabled(k));             // measured on
        Assert.True(Apply(BatchGpuDeepMode.Off, BatchDeepRouteMode.Auto).Ok);
        Assert.False(MandelbrotCalculator.GpuPerturbationEnabled(k));            // --gpu-deep off beats it
        Assert.Contains("--gpu-deep", DeepRouteBatchSetup.DeepZoomLine(k));
        Assert.True(Apply(BatchGpuDeepMode.On, BatchDeepRouteMode.Auto).Ok);
        Assert.True(MandelbrotCalculator.GpuPerturbationEnabled(k));
        var bad = Apply(BatchGpuDeepMode.On, BatchDeepRouteMode.Auto, BatchGpuMode.Off);
        Assert.False(bad.Ok);
        Assert.Contains("--gpu off", bad.Error);
        MandelbrotCalculator.GpuPerturbUserOverride = null;   // a fresh batch: no toggle,
        MandelbrotCalculator.UseGpuPerturbation = false;       // nothing explicit
        Assert.True(Apply(BatchGpuDeepMode.Auto, BatchDeepRouteMode.Auto).Ok);
        Assert.Null(MandelbrotCalculator.GpuPerturbUserOverride);               // auto leaves it alone
        Assert.Equal("deep zoom on the GPU (measured on this device)", DeepRouteBatchSetup.DeepZoomLine(k));
    }

    [Fact]
    public void Calibration_Off_Ignores_The_Store_And_Auto_Never_Runs_A_Pass()
    {
        var runner = new FakeRunner();
        DeepRouteCalibration.RunnerFactory = _ => runner;
        DeepRouteStore.StoreCpu(new DeepRouteCpuResult { LaMinZoom = 1e20 });
        var auto = Apply(BatchGpuDeepMode.Auto, BatchDeepRouteMode.Auto);
        Assert.True(auto.Ok);
        Assert.Equal(0, runner.Runs);
        Assert.Equal(1e20, MandelbrotCalculator.EffectiveLaAutoMinZoom);
        Assert.Contains(auto.Log, l => l.StartsWith("deep route: CPU: LA from 1e20", StringComparison.Ordinal));

        Assert.True(Apply(BatchGpuDeepMode.Auto, BatchDeepRouteMode.Off).Ok);
        Assert.False(MandelbrotCalculator.UseDeepRouteCalibration);
        Assert.Equal(MandelbrotCalculator.LaAutoMinZoom, MandelbrotCalculator.EffectiveLaAutoMinZoom);
        Assert.Equal(0, runner.Runs);
    }

    [Fact]
    public void Force_Runs_A_Pass_On_The_Own_Device_And_Stores_It()
    {
        var runner = new FakeRunner();
        IGpuKernel? handed = null;
        DeepRouteCalibration.RunnerFactory = k => { handed = k; return runner; };
        var k = new FakeKernel("fake|own");
        int opened = 0;
        MandelbrotCalculator.UseDeepRouteCalibration = false;   // force turns it on
        var r = Apply(BatchGpuDeepMode.Auto, BatchDeepRouteMode.Force, BatchGpuMode.Auto, () => { opened++; return k; });
        Assert.True(r.Ok);
        Assert.True(runner.Runs > 0);
        Assert.Equal(1, opened);
        Assert.Same(k, handed);
        Assert.True(MandelbrotCalculator.UseDeepRouteCalibration);
        Assert.NotNull(DeepRouteStore.LookupCpu());
        Assert.Contains(r.Log, l => l.StartsWith("deep-route calibration: ", StringComparison.Ordinal));
        Assert.Null(DeepRouteCalibration.GpuKernelFactory);    // restored

        // --gpu off: CPU only, the device factory is not used.
        handed = k; opened = 0;
        Assert.True(Apply(BatchGpuDeepMode.Auto, BatchDeepRouteMode.Force, BatchGpuMode.Off, () => { opened++; return k; }).Ok);
        Assert.Equal(0, opened);
        Assert.Null(handed);
    }
}
