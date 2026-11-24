// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1665 / #1786 (S2) — the calibration pass (DeepRouteCalibrator), its real runner's
// forced routes (MandelbrotCalculator.CalibrationRoute), the background trigger
// (DeepRouteCalibration) and the Vulkan default-on gap.
//   • the pass, on a fake runner: CPU LA first on each scene as the reference, the
//     second pass in reverse order with CPU LA again; a 3x loser skips its second pass;
//     disagreement is measured against the reference; a fallback is not eligible; the
//     budget / cancellation stop it early; timings shaped after this machine store
//     today's choices under the runner's device key;
//   • forced routes on real CPU frames: each CPU route runs as forced whatever the store
//     and the switches say; a GPU route with no kernel falls back;
//   • a GPU run measured too slow during calibration does not latch GPU perturbation off
//     for the session (the same failure outside calibration does);
//   • the float-exp kernel, once chosen, runs instead of the fp64 SA / BLA kernel;
//   • the background trigger: off unless the host enables it, once per process, not
//     when the store already has this device, the result stored; a cancelled pass stores
//     nothing;
//   • a Vulkan session's default-on GPU deep yields to a measured "GPU slower", an
//     FF_GPU_PERTURB switch does not.
// Mutants (by hand): see the PR.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using FracturingFog.Models;
using FracturingFog.Rendering;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // route statics + the store + the service (run alone)
public sealed class S1786DeepRouteCalibrationTests : IDisposable
{
    private readonly bool _cal = MandelbrotCalculator.UseDeepRouteCalibration, _gp = MandelbrotCalculator.UseGpuPerturbation,
        _gpDefault = MandelbrotCalculator.UseGpuPerturbationIsDefault, _sa = MandelbrotCalculator.UseGpuSeriesApproximation,
        _bla = MandelbrotCalculator.UseGpuBla;
    private readonly bool? _envGp = MandelbrotCalculator.GpuPerturbEnvSwitch;
    private readonly GpuDeepPrecision _prec = MandelbrotCalculator.GpuDeepPrecisionMode;

    public S1786DeepRouteCalibrationTests()
    {
        DeepRouteStore.Reset();
        DeepRouteCalibration.ResetForTests();
        MandelbrotCalculator.UseDeepRouteCalibration = true;
        MandelbrotCalculator.GpuPerturbEnvSwitch = null;
    }

    public void Dispose()
    {
        DeepRouteCalibration.ResetForTests();
        DeepRouteCalibration.AutoCalibrate = false;
        DeepRouteCalibration.GpuKernelFactory = null;
        DeepRouteCalibration.RunnerFactory = null;
        DeepRouteCalibration.BackgroundBudget = DeepRouteCalibrator.DefaultBudget;
        DeepRouteStore.Reset();
        MandelbrotCalculator.ResetGpuPerturbationLatchForTests();
        MandelbrotCalculator.UseDeepRouteCalibration = _cal;
        MandelbrotCalculator.UseGpuPerturbation = _gp;
        MandelbrotCalculator.UseGpuPerturbationIsDefault = _gpDefault;
        MandelbrotCalculator.UseGpuSeriesApproximation = _sa;
        MandelbrotCalculator.UseGpuBla = _bla;
        MandelbrotCalculator.GpuPerturbEnvSwitch = _envGp;
        MandelbrotCalculator.GpuDeepPrecisionMode = _prec;
    }

    // ── a fake runner ────────────────────────────────────────────────────────

    private sealed class FakeRunner : IDeepRouteRunner
    {
        public readonly List<(string Scene, DeepRouteCandidate Route)> Calls = new();
        public Func<DeepRouteScene, DeepRouteCandidate, double> Ms = (_, _) => 10;
        public Func<DeepRouteScene, DeepRouteCandidate, int[]?> Iter = (_, _) => new int[100];
        public HashSet<DeepRouteCandidate> FallBack = new();
        public Action? OnRun;
        public IReadOnlyList<DeepRouteCandidate> Candidates { get; init; } = Enum.GetValues<DeepRouteCandidate>();
        public string? GpuDeviceKey { get; init; } = "fake|gpu|1";
        public bool Disposed;
        public DeepRouteRun Run(DeepRouteScene scene, DeepRouteCandidate route, CancellationToken ct)
        {
            Calls.Add((scene.Name, route));
            OnRun?.Invoke();
            if (FallBack.Contains(route)) return DeepRouteRun.Fallback;
            return new DeepRouteRun(Ms(scene, route), Iter(scene, route), false);
        }
    }

    private sealed class DisposableFakeRunner : IDeepRouteRunner, IDisposable
    {
        public readonly FakeRunner Inner = new();
        public bool Disposed;
        public IReadOnlyList<DeepRouteCandidate> Candidates => Inner.Candidates;
        public string? GpuDeviceKey => Inner.GpuDeviceKey;
        public DeepRouteRun Run(DeepRouteScene s, DeepRouteCandidate r, CancellationToken ct) => Inner.Run(s, r, ct);
        public void Dispose() => Disposed = true;
    }

    private static DeepRouteScene Sc(string name, double zoom) => new(name, [0.0], [0.0], zoom, 100);

    [Fact]
    public void Each_Scene_Starts_With_The_Cpu_La_Reference_And_Alternates_The_Order()
    {
        var f = new FakeRunner { Candidates = [DeepRouteCandidate.CpuLa, DeepRouteCandidate.CpuBla, DeepRouteCandidate.GpuSaFp64] };
        var r = DeepRouteCalibrator.Run(f, [Sc("b", 1e30), Sc("a", 1e20)], store: false, warmUp: false);
        Assert.True(r.Complete);
        // Shallow first; per scene: the untimed LA reference, then LA, BLA, GPU / GPU, BLA,
        // LA / LA, BLA, GPU (#1789: CPU LA alternates like the others; three passes).
        Assert.Equal(3, DeepRouteCalibrator.Passes);
        string[] one = ["CpuLa", "CpuLa", "CpuBla", "GpuSaFp64", "GpuSaFp64", "CpuBla", "CpuLa", "CpuLa", "CpuBla", "GpuSaFp64"];
        string[] expect = [.. one.Select(r => "a:" + r), .. one.Select(r => "b:" + r)];
        Assert.Equal(expect, f.Calls.Select(c => $"{c.Scene}:{c.Route}"));
        Assert.Equal(6, r.Timings.Count);
    }

    [Fact]
    public void A_Warm_Pass_Runs_Every_Scene_And_Route_Untimed_First()
    {
        // #1789 — a fresh process's first frames of each code path ran up to 6x slow.
        int n = 0;
        var f = new FakeRunner
        {
            Candidates = [DeepRouteCandidate.CpuLa, DeepRouteCandidate.CpuBla],
            Ms = (_, _) => ++n <= 4 ? 1000 : 10,   // the warm pass is slow
        };
        var r = DeepRouteCalibrator.Run(f, [Sc("b", 1e30), Sc("a", 1e20)], store: false);
        Assert.Equal(["a:CpuLa", "a:CpuBla", "b:CpuLa", "b:CpuBla"], f.Calls.Take(4).Select(c => $"{c.Scene}:{c.Route}"));
        Assert.All(r.Timings, t => Assert.Equal(10, t.Ms));             // none of the warm frames counts
        Assert.Equal(4 + 2 * (1 + 2 * DeepRouteCalibrator.Passes), f.Calls.Count);
    }

    [Fact]
    public void The_Reference_Run_Is_Not_Timed()
    {
        // #1789 — the first frame after a scene switch runs slow (measured ~25 %); timing it
        // biased CPU LA. Here the reference is the fastest frame: it must not count.
        int la = 0;
        var f = new FakeRunner
        {
            Candidates = [DeepRouteCandidate.CpuLa, DeepRouteCandidate.CpuBla],
            Ms = (_, r) => r == DeepRouteCandidate.CpuLa ? (++la == 1 ? 1 : 10) : 12,
        };
        var r = DeepRouteCalibrator.Run(f, [Sc("a", 1e35)], store: false, warmUp: false);
        Assert.Equal(1 + DeepRouteCalibrator.Passes, f.Calls.Count(c => c.Route == DeepRouteCandidate.CpuLa));   // reference + timed
        Assert.Equal(10, r.Timings.Single(t => t.Route == DeepRouteCandidate.CpuLa).Ms);
    }

    [Fact]
    public void A_Threefold_Loser_Skips_Its_Second_Pass_And_Best_Of_Is_Kept()
    {
        int n = 0;
        var f = new FakeRunner
        {
            Candidates = [DeepRouteCandidate.CpuLa, DeepRouteCandidate.CpuBla, DeepRouteCandidate.GpuSaFp64],
            Ms = (_, r) => r switch { DeepRouteCandidate.GpuSaFp64 => 100, DeepRouteCandidate.CpuBla => ++n == 1 ? 20 : 15, _ => 10 },
        };
        var r = DeepRouteCalibrator.Run(f, [Sc("a", 1e20)], store: false, warmUp: false);
        Assert.Equal(1, f.Calls.Count(c => c.Route == DeepRouteCandidate.GpuSaFp64));   // 100 > 3 x 10
        Assert.Equal(DeepRouteCalibrator.Passes, f.Calls.Count(c => c.Route == DeepRouteCandidate.CpuBla));
        Assert.Equal(15, r.Timings.Single(t => t.Route == DeepRouteCandidate.CpuBla).Ms);   // the better pass
    }

    [Fact]
    public void Disagreement_Is_Against_Cpu_La_And_A_Fallback_Is_Not_Eligible()
    {
        var f = new FakeRunner
        {
            Candidates = [DeepRouteCandidate.CpuLa, DeepRouteCandidate.CpuBla, DeepRouteCandidate.GpuSaFp64, DeepRouteCandidate.GpuFloatExp],
            // GPU SA differs on 10 of 100 pixels; float-exp falls back.
            Iter = (_, r) => r == DeepRouteCandidate.GpuSaFp64 ? Enumerable.Range(0, 100).Select(i => i < 10 ? 1 : 0).ToArray() : new int[100],
            Ms = (_, r) => IsGpu(r) ? 1 : 10,
            FallBack = [DeepRouteCandidate.GpuFloatExp],
        };
        var r = DeepRouteCalibrator.Run(f, [Sc("a", 1e20)], store: false, warmUp: false);
        Assert.Equal(0.10, r.Timings.Single(t => t.Route == DeepRouteCandidate.GpuSaFp64).Disagreement, 9);
        Assert.Equal(0.0, r.Timings.Single(t => t.Route == DeepRouteCandidate.CpuBla).Disagreement);
        var fx = r.Timings.Single(t => t.Route == DeepRouteCandidate.GpuFloatExp);
        Assert.True(double.IsNaN(fx.Ms));
        Assert.Equal(1, f.Calls.Count(c => c.Route == DeepRouteCandidate.GpuFloatExp));   // not retried
        var ok = DeepRouteChooser.Eligible(r.Timings);
        Assert.DoesNotContain(DeepRouteCandidate.GpuSaFp64, ok);
        Assert.DoesNotContain(DeepRouteCandidate.GpuFloatExp, ok);
        Assert.False(r.Gpu!.GpuDeep);   // the fast GPU routes were wrong or fell back
    }

    private static bool IsGpu(DeepRouteCandidate r) => r is DeepRouteCandidate.GpuSaFp64 or DeepRouteCandidate.GpuFloatExp or DeepRouteCandidate.GpuLaFp64;

    [Fact]
    public void Cancellation_And_The_Budget_Stop_The_Pass_Early()
    {
        using var cts = new CancellationTokenSource();
        var f = new FakeRunner { Candidates = [DeepRouteCandidate.CpuLa, DeepRouteCandidate.CpuBla] };
        f.OnRun = () => { if (f.Calls.Count == 4) cts.Cancel(); };
        var r = DeepRouteCalibrator.Run(f, [Sc("a", 1e20), Sc("b", 1e30), Sc("c", 1e40)], ct: cts.Token, warmUp: false);
        Assert.False(r.Complete);
        Assert.Equal(4, f.Calls.Count);                         // scene a finished, nothing after
        Assert.All(r.Timings, t => Assert.Equal("a", t.Scene));

        var slow = new FakeRunner { Candidates = [DeepRouteCandidate.CpuLa, DeepRouteCandidate.CpuBla], OnRun = () => Thread.Sleep(30) };
        var r2 = DeepRouteCalibrator.Run(slow, [Sc("a", 1e20), Sc("b", 1e30), Sc("c", 1e40)], budget: TimeSpan.FromMilliseconds(50), store: false, warmUp: false);
        Assert.False(r2.Complete);
        Assert.True(slow.Calls.Count < 3 * (1 + 2 * DeepRouteCalibrator.Passes), $"{slow.Calls.Count} runs");
    }

    [Fact]
    public void Timings_Shaped_After_This_Machine_Store_Todays_Choices_Under_The_Device_Key()
    {
        // BLA faster to 1e27, LA from 1e31; no SIMD gain; the GPU 3-7x slower; GPU LA slower still.
        double Ms(DeepRouteScene s, DeepRouteCandidate r) => r switch
        {
            DeepRouteCandidate.CpuBla => s.Zoom < 1e30 ? 50 : 400,
            DeepRouteCandidate.CpuLa => s.Zoom < 1e30 ? 70 : 120,
            DeepRouteCandidate.CpuLaSimd => s.Zoom < 1e30 ? 72 : 123,
            DeepRouteCandidate.GpuSaFp64 => s.Zoom < 1e30 ? 250 : 600,
            DeepRouteCandidate.GpuFloatExp => s.Zoom < 1e30 ? 260 : 650,
            _ => s.Zoom < 1e30 ? 400 : 900,
        };
        var f = new FakeRunner { Ms = Ms };
        var r = DeepRouteCalibrator.Run(f, DeepRouteCalibrator.DefaultScenes);
        Assert.True(r.Complete);
        Assert.Equal(1e31, r.Cpu.LaMinZoom);   // the shallowest default scene past 1e30
        var cpu = DeepRouteStore.LookupCpu();
        Assert.Equal(1e31, cpu?.LaMinZoom);
        Assert.False(cpu?.LaSimd);
        var gpu = DeepRouteStore.LookupGpu("fake|gpu|1");
        Assert.NotNull(gpu);
        Assert.False(gpu!.GpuDeep);
        Assert.False(gpu.GpuLa);
        Assert.Equal(GpuDeepPrecision.Double, gpu.Precision);
        Assert.Null(DeepRouteStore.LookupGpu("another|gpu"));
    }

    [Fact]
    public void Gpu_Routes_Are_Judged_Against_The_Cpu_Noise_Floor()
    {
        // Measured on this machine (480x270 is similar): at p448 1e22 CPU BLA differs from
        // CPU LA on 3.78 % of pixels, the GPU SA kernel on 3.85 %; at 1e31 CPU BLA 0.29 %,
        // GPU SA 4.80 %.
        DeepRouteTiming[] runs =
        [
            new(DeepRouteCandidate.CpuLa, "1e22", 1e22, 8, 0), new(DeepRouteCandidate.CpuBla, "1e22", 1e22, 7, 0.0378),
            new(DeepRouteCandidate.GpuSaFp64, "1e22", 1e22, 44, 0.0385),
        ];
        var ok = DeepRouteChooser.Eligible(runs);
        Assert.Contains(DeepRouteCandidate.CpuBla, ok);     // a shipped evaluator: the floor itself
        Assert.Contains(DeepRouteCandidate.GpuSaFp64, ok);  // within 1.5 x the floor
        DeepRouteTiming[] deeper = [.. runs,
            new(DeepRouteCandidate.CpuLa, "1e31", 1e31, 16, 0), new(DeepRouteCandidate.CpuBla, "1e31", 1e31, 19, 0.0029),
            new(DeepRouteCandidate.GpuSaFp64, "1e31", 1e31, 65, 0.0480)];
        Assert.DoesNotContain(DeepRouteCandidate.GpuSaFp64, DeepRouteChooser.Eligible(deeper));   // 4.8 % > max(2 %, 0.44 %)
    }

    [Fact]
    public void Noise_Does_Not_Flip_A_Default()
    {
        // LA SIMD 3 % faster, the GPU 3 % faster: within MinGain, the defaults stay.
        DeepRouteTiming[] runs =
        [
            new(DeepRouteCandidate.CpuBla, "a", 1e35, 300, 0), new(DeepRouteCandidate.CpuLa, "a", 1e35, 100, 0),
            new(DeepRouteCandidate.CpuLaSimd, "a", 1e35, 97, 0), new(DeepRouteCandidate.GpuSaFp64, "a", 1e35, 97, 0),
        ];
        var (cpu, gpu) = DeepRouteChooser.Choose(runs, "t");
        Assert.False(cpu.LaSimd);
        Assert.False(gpu!.GpuDeep);
        var (cpu2, gpu2) = DeepRouteChooser.Choose(runs.Select(t => t.Route == DeepRouteCandidate.CpuLa ? t with { Ms = 110 } : t).ToArray(), "t");
        Assert.True(cpu2.LaSimd);   // 97 vs 110: 12 %
        Assert.False(gpu2!.GpuDeep); // 97 against the CPU's best, 97 (SIMD)
        var (_, gpu3) = DeepRouteChooser.Choose(runs.Select(t => t.Route == DeepRouteCandidate.GpuSaFp64 ? t with { Ms = 80 } : t).ToArray(), "t");
        Assert.True(gpu3!.GpuDeep);  // 80 vs 97: 18 %
    }

    [Fact]
    public void The_Default_Scenes_Span_The_Crossover_And_The_Gpu_Range()
    {
        var z = DeepRouteCalibrator.DefaultScenes.Select(s => s.Zoom).ToArray();
        Assert.True(z.Min() <= 1e15 && z.Max() >= 1e45 && z.Max() <= MandelbrotCalculator.MaxGpuPerturbZoom);
        Assert.Contains(z, v => v < MandelbrotCalculator.LaAutoMinZoom);
        Assert.Contains(z, v => v > MandelbrotCalculator.LaAutoMinZoom);
        Assert.Equal(z.OrderBy(v => v), z);
    }

    // ── forced routes on real CPU frames ─────────────────────────────────────

    private static readonly DeepRouteScene P448At1e35 = DeepRouteCalibrator.DefaultScenes[0] with { Name = "p448 1e35", Zoom = 1e35, MaxIter = 4000 };

    [Fact]
    public void Each_Cpu_Route_Runs_As_Forced_Whatever_The_Store_Says()
    {
        DeepRouteStore.StoreCpu(new DeepRouteCpuResult { LaMinZoom = double.PositiveInfinity, LaSimd = !Avx2.IsSupported });
        using var runner = new CalculatorDeepRouteRunner(null, width: 48, height: 32);
        Assert.Equal(Avx2.IsSupported, runner.Candidates.Contains(DeepRouteCandidate.CpuLaSimd));
        Assert.DoesNotContain(runner.Candidates, IsGpu);
        foreach (var route in runner.Candidates)
        {
            var run = runner.Run(P448At1e35, route, CancellationToken.None);
            Assert.False(run.FellBack, $"{route} fell back");
            Assert.True(run.Ms > 0);
            Assert.Equal(48 * 32, run.Iterations!.Length);
        }
        Assert.True(runner.Run(P448At1e35, DeepRouteCandidate.GpuSaFp64, CancellationToken.None).FellBack, "no kernel: a GPU route falls back");
        // The routes really differ: LA forced against "never", BLA against Auto LA past 1e30.
        var c = new MandelbrotCalculator(48, 32) { Quality = QualityPreset.Extreme, Zoom = 1e35, MaxIterations = 4000, ColorMap = new HsvPalette(),
            CenterX = P448At1e35.X[0], CenterXLo = P448At1e35.X[1], CenterX2 = P448At1e35.X[2], CenterY = P448At1e35.Y[0], CenterYLo = P448At1e35.Y[1], CenterY2 = P448At1e35.Y[2] };
        c.Calculate(CancellationToken.None);
        Assert.False(c.LastFrameUsedLa, "precondition: the stored 'never' keeps LA off");
        c.CalibrationRoute = DeepRouteCandidate.CpuLa;
        c.Calculate(CancellationToken.None);
        Assert.True(CalculatorDeepRouteRunner.RanRoute(c, DeepRouteCandidate.CpuLa));
    }

    // ── a fake GPU kernel for the failure / kernel-choice paths ──────────────

    private sealed class ThrowingKernel(string message) : IGpuKernel
    {
        public readonly List<string> Called = new();
        public bool HasGpuPalette => false;
        public double LastDispatchMs => 0;
        public double LastReadbackMs => 0;
        public bool SupportsPerturbation => true;
        public bool SupportsPerturbationSA => true;
        public bool SupportsPerturbationFloatExp => true;
        public string? DeviceKey => "fake|throwing";
        public void SetPalette(FracturingFog.Interefaces.IGpuHlslPalette? palette) { }
        public void Run(int width, int height, double centerX, double centerY, double scale, int maxIter, double bailout2,
            int[] iterDst, float[] smoothDst, float[] finalZrDst, float[] finalZiDst, float[] finalDrDst, float[] finalDiDst,
            int[]? perRowMaxIter = null, FractalKind kind = FractalKind.Mandelbrot, float param0 = 0f, float param1 = 0f,
            uint[]? colorDst = null, float[]? trapDst = null, GpuDomainWarp warp = default) { }
        public void RunPerturb(int width, int height, double scale, int maxIter, double escapeRadius2, double offsetX0, double offsetY0,
            double[] refZr, double[] refZi, int refLen, int[] iterDst, float[] smoothDst, float[] finalZrDst, float[] finalZiDst,
            float[] finalDrDst, float[] finalDiDst) { Called.Add("plain"); throw new InvalidOperationException(message); }
        public void RunPerturbFloatExp(int width, int height, double scale, int maxIter, double escapeRadius2, double offsetX0, double offsetY0,
            double[] refZr, double[] refZi, int refLen, int[] iterDst, float[] smoothDst, float[] finalZrDst, float[] finalZiDst,
            float[] finalDrDst, float[] finalDiDst) { Called.Add("float-exp"); throw new InvalidOperationException(message); }
        public void RunPerturbSA(int width, int height, double scale, int maxIter, double escapeRadius2, double offsetX0, double offsetY0,
            double[] refZr, double[] refZi, int refLen, double saTolerance, int safeMax,
            double[] aR, double[] aI, double[] bR, double[] bI, double[] cR, double[] cI, double[] dR, double[] dI,
            int[] iterDst, float[] smoothDst, float[] finalZrDst, float[] finalZiDst, float[] finalDrDst, float[] finalDiDst,
            double[]? blaCoeffs = null, int blaLevels = 0) { Called.Add("sa"); throw new InvalidOperationException(message); }
        public void Dispose() { }
    }

    private static MandelbrotCalculator Deep(IGpuKernel k, DeepRouteCandidate? route) => new(48, 32)
    {
        Quality = QualityPreset.Extreme, Zoom = 1e20, MaxIterations = 4000, ColorMap = new HsvPalette(), LaMode = DeepLaMode.Off,
        CenterX = P448At1e35.X[0], CenterXLo = P448At1e35.X[1], CenterY = P448At1e35.Y[0], CenterYLo = P448At1e35.Y[1],
        GpuKernel = k, CalibrationRoute = route,
    };

    [Fact]
    public void Too_Slow_During_Calibration_Does_Not_Latch_The_Session()
    {
        MandelbrotCalculator.UseGpuPerturbation = true;
        var k = new ThrowingKernel("GPU-PERTURB-TOO-SLOW: band0=99ms");
        var c = Deep(k, DeepRouteCandidate.GpuSaFp64);
        c.Calculate(CancellationToken.None);
        Assert.NotEmpty(k.Called);                                  // the GPU was tried
        Assert.False(CalculatorDeepRouteRunner.RanRoute(c, DeepRouteCandidate.GpuSaFp64));   // and fell back
        Assert.False(MandelbrotCalculator.GpuPerturbationLatched);
        Assert.True(MandelbrotCalculator.GpuPerturbationEnabled(k));

        // Control: the same failure on live frames latches GPU deep off. #2012: one abort
        // falls back for that frame only; the second in a row latches.
        var live = Deep(k, null);
        live.Calculate(CancellationToken.None);
        Assert.False(MandelbrotCalculator.GpuPerturbationLatched);
        Assert.True(MandelbrotCalculator.GpuPerturbationEnabled(k));
        live.Calculate(CancellationToken.None);
        Assert.True(MandelbrotCalculator.GpuPerturbationLatched);
        MandelbrotCalculator.ResetGpuPerturbationLatchForTests();
        MandelbrotCalculator.UseGpuPerturbation = true;   // the latch cleared it
    }

    [Fact]
    public void The_Float_Exp_Kernel_Once_Chosen_Replaces_The_Fp64_Sa_Kernel()
    {
        MandelbrotCalculator.UseGpuPerturbation = true;
        MandelbrotCalculator.UseGpuSeriesApproximation = true;
        MandelbrotCalculator.UseGpuBla = true;
        var k = new ThrowingKernel("test: no frame");   // not a latching failure
        MandelbrotCalculator.GpuDeepPrecisionMode = GpuDeepPrecision.Fp32;
        Deep(k, null).Calculate(CancellationToken.None);
        Assert.Equal(["float-exp"], k.Called);
        k.Called.Clear();
        MandelbrotCalculator.GpuDeepPrecisionMode = GpuDeepPrecision.Auto;
        DeepRouteStore.StoreGpu("fake|throwing", new DeepRouteGpuResult { Precision = GpuDeepPrecision.Fp32 });
        Deep(k, null).Calculate(CancellationToken.None);
        Assert.Equal(["float-exp"], k.Called);                      // measured float-exp
        k.Called.Clear();
        DeepRouteStore.StoreGpu("fake|throwing", new DeepRouteGpuResult { Precision = GpuDeepPrecision.Double });
        Deep(k, null).Calculate(CancellationToken.None);
        Assert.Equal(["sa"], k.Called);                             // fp64: the SA / BLA kernel
        k.Called.Clear();
        Deep(k, DeepRouteCandidate.GpuFloatExp).Calculate(CancellationToken.None);
        Assert.Equal(["float-exp"], k.Called);                      // the calibration's route
        Assert.False(MandelbrotCalculator.GpuPerturbationLatched);
    }

    // ── the background trigger ───────────────────────────────────────────────

    private static void WaitIdle()
    {
        for (int i = 0; i < 400 && DeepRouteCalibration.State == DeepRouteCalibrationState.Running; i++) Thread.Sleep(25);
        Assert.NotEqual(DeepRouteCalibrationState.Running, DeepRouteCalibration.State);
    }

    [Fact]
    public void The_Background_Pass_Runs_Once_Only_Where_Enabled_And_Stores_Its_Result()
    {
        int built = 0;
        DisposableFakeRunner? last = null;
        DeepRouteCalibration.RunnerFactory = _ => { built++; return last = new DisposableFakeRunner(); };

        DeepRouteCalibration.AutoCalibrate = false;
        DeepRouteCalibration.RequestBackground("fake|gpu|1");
        Assert.Equal(DeepRouteCalibrationState.Idle, DeepRouteCalibration.State);
        Assert.Equal(0, built);                                    // off unless the host enables it

        DeepRouteCalibration.AutoCalibrate = true;
        DeepRouteCalibration.RequestBackground("fake|gpu|1");
        WaitIdle();
        Assert.Equal(1, built);
        Assert.Equal(DeepRouteCalibrationState.Done, DeepRouteCalibration.State);
        Assert.True(last!.Disposed);
        Assert.NotNull(DeepRouteStore.LookupCpu());
        Assert.NotNull(DeepRouteStore.LookupGpu("fake|gpu|1"));
        Assert.True(DeepRouteCalibration.HasResultFor("fake|gpu|1"));

        DeepRouteCalibration.RequestBackground("fake|gpu|1");
        DeepRouteCalibration.RequestBackground("other|gpu");
        WaitIdle();
        Assert.Equal(1, built);                                    // once per process

        DeepRouteCalibration.ResetForTests();
        DeepRouteCalibration.RequestBackground("fake|gpu|1");
        WaitIdle();
        Assert.Equal(1, built);                                    // the store has this device
        DeepRouteCalibration.RequestBackground("other|gpu");
        Assert.Equal(1, built);                                    // (and it is once per process again)

        DeepRouteCalibration.ResetForTests();
        MandelbrotCalculator.UseDeepRouteCalibration = false;      // FF_DEEP_ROUTE_CALIBRATION=0
        DeepRouteCalibration.RequestBackground("other|gpu");
        Assert.Equal(DeepRouteCalibrationState.Idle, DeepRouteCalibration.State);
        Assert.Equal(1, built);
    }

    [Fact]
    public void A_Cancelled_Pass_Stores_Nothing()
    {
        using var cts = new CancellationTokenSource();
        DeepRouteCalibration.RunnerFactory = _ =>
        {
            var r = new DisposableFakeRunner();
            // After the warm pass (6 scenes x 6 routes) and a few timed runs.
            r.Inner.OnRun = () => { if (r.Inner.Calls.Count == 40) cts.Cancel(); };
            return r;
        };
        var result = DeepRouteCalibration.RunNow(ct: cts.Token);
        Assert.NotEmpty(result.Timings);
        Assert.Null(DeepRouteStore.LookupCpu());
        Assert.Null(DeepRouteStore.LookupGpu("fake|gpu|1"));
    }

    // ── the Vulkan default-on gap ────────────────────────────────────────────

    [Fact]
    public void A_Default_On_Yields_To_A_Measured_Gpu_Slower_But_An_Env_Switch_Does_Not()
    {
        var k = new ThrowingKernel("unused");
        MandelbrotCalculator.UseGpuPerturbation = true;
        MandelbrotCalculator.UseGpuPerturbationIsDefault = true;
        Assert.True(MandelbrotCalculator.GpuPerturbationEnabled(k));      // nothing measured
        DeepRouteStore.StoreGpu("fake|throwing", new DeepRouteGpuResult { GpuDeep = false });
        Assert.False(MandelbrotCalculator.GpuPerturbationEnabled(k));     // measured slower
        MandelbrotCalculator.GpuPerturbEnvSwitch = true;                  // FF_GPU_PERTURB=1
        Assert.True(MandelbrotCalculator.GpuPerturbationEnabled(k));
        MandelbrotCalculator.GpuPerturbEnvSwitch = null;
        MandelbrotCalculator.UseGpuPerturbationIsDefault = false;         // asked for (a probe / D3D opt-in)
        Assert.True(MandelbrotCalculator.GpuPerturbationEnabled(k));
    }
}
