// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1665 / #1785 (S1) — the per-device deep-route calibration store and the route
// switches that read it (MandelbrotCalculator.DeepRoute.cs).
//   • keying: a GPU row matches only its CPU + device key (a changed driver or CPU is
//     a miss); a CPU row only its CPU; rows survive a reload, +inf included;
//   • invalidation: a file of another schema version is ignored and rewritten; Reset
//     forgets everything;
//   • fallback: with no row every switch keeps today's default;
//   • a stored row moves each switch, observed on real frames where it can be (the LA
//     crossover: LA runs at 1e25 only with a measured 1e20; the LA SIMD tail);
//   • overrides: the env switch and explicit settings beat the store, either way;
//     FF_DEEP_ROUTE_CALIBRATION=0 ignores it;
//   • the chooser: today's machine reproduces today's choices; a fast route that
//     disagrees on iterations is excluded; the crossover, LA SIMD, GPU LA and
//     fp64-or-float-exp decisions.
// Mutants (by hand): see the PR.

using System;
using System.IO;
using System.Linq;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;
using System.Threading;
using FracturingFog.Models;
using FracturingFog.Rendering;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // route statics + the store (run alone)
public sealed class S1785DeepRouteStoreTests : IDisposable
{
    private readonly bool _cal = MandelbrotCalculator.UseDeepRouteCalibration, _gp = MandelbrotCalculator.UseGpuPerturbation,
        _gla = MandelbrotCalculator.UseGpuLa;
    private readonly bool? _envGp = MandelbrotCalculator.GpuPerturbEnvSwitch, _envGla = MandelbrotCalculator.GpuLaEnvSwitch,
        _envSimd = MandelbrotCalculator.LaSimdEnvSwitch;
    private readonly GpuDeepPrecision _prec = MandelbrotCalculator.GpuDeepPrecisionMode;

    public S1785DeepRouteStoreTests()
    {
        DeepRouteStore.Reset();
        MandelbrotCalculator.UseDeepRouteCalibration = true;
        MandelbrotCalculator.UseGpuPerturbation = false;
        MandelbrotCalculator.UseGpuLa = false;
        MandelbrotCalculator.GpuPerturbEnvSwitch = null;
        MandelbrotCalculator.GpuLaEnvSwitch = null;
        MandelbrotCalculator.LaSimdEnvSwitch = null;
        MandelbrotCalculator.GpuDeepPrecisionMode = GpuDeepPrecision.Auto;
    }

    public void Dispose()
    {
        DeepRouteStore.Reset();
        MandelbrotCalculator.UseDeepRouteCalibration = _cal;
        MandelbrotCalculator.UseGpuPerturbation = _gp;
        MandelbrotCalculator.UseGpuLa = _gla;
        MandelbrotCalculator.GpuPerturbEnvSwitch = _envGp;
        MandelbrotCalculator.GpuLaEnvSwitch = _envGla;
        MandelbrotCalculator.LaSimdEnvSwitch = _envSimd;
        MandelbrotCalculator.GpuDeepPrecisionMode = _prec;
    }

    private sealed class FakeKernel(string? key, bool fp64 = true, bool fx = true, bool la = true) : IGpuKernel
    {
        public bool HasGpuPalette => false;
        public double LastDispatchMs => 0;
        public double LastReadbackMs => 0;
        public bool SupportsPerturbation => fp64;
        public bool SupportsPerturbationFloatExp => fx;
        public bool SupportsPerturbationLa => la;
        public string? DeviceKey => key;
        public void SetPalette(FracturingFog.Interefaces.IGpuHlslPalette? palette) { }
        public void Run(int width, int height, double centerX, double centerY, double scale, int maxIter, double bailout2,
            int[] iterDst, float[] smoothDst, float[] finalZrDst, float[] finalZiDst, float[] finalDrDst, float[] finalDiDst,
            int[]? perRowMaxIter = null, FractalKind kind = FractalKind.Mandelbrot, float param0 = 0f, float param1 = 0f,
            uint[]? colorDst = null, float[]? trapDst = null, GpuDomainWarp warp = default) { }
        public void Dispose() { }
    }

    private const string DevA = "d3d11|GeForce GT 710|10DE:128B:A1|0020001F0E5C0B1A";
    private const string DevADriver2 = "d3d11|GeForce GT 710|10DE:128B:A1|0020001F0E5C0B2B";

    // ── keying / persistence / invalidation ──────────────────────────────────

    [Fact]
    public void The_Cpu_Key_Names_The_Cpu_Its_Threads_And_Simd()
    {
        string k = DeepRouteStore.CpuKey;
        Assert.Equal(k, DeepRouteStore.CpuKey);
        Assert.Contains($"|{Environment.ProcessorCount}t|", k);
        Assert.EndsWith("|" + DeepRouteStore.SimdSet(), k);
        Assert.Equal(Avx2.IsSupported, DeepRouteStore.SimdSet().Contains("avx2"));
        Assert.True(k.Split('|')[0].Length > 0, "a CPU brand");
    }

    [Fact]
    public void A_Gpu_Row_Matches_Only_Its_Cpu_And_Device()
    {
        string otherCpu = DeepRouteStore.BuildCpuKey("Other CPU", "X64", 64, "avx2+avx512f");
        DeepRouteStore.StoreGpu(DevA, new DeepRouteGpuResult { GpuDeep = true });
        Assert.True(DeepRouteStore.LookupGpu(DevA)?.GpuDeep);
        Assert.Null(DeepRouteStore.LookupGpu(DevADriver2));            // a new driver
        Assert.Null(DeepRouteStore.LookupGpu(DevA, otherCpu));         // the same GPU with another CPU
        DeepRouteStore.StoreCpu(new DeepRouteCpuResult { LaMinZoom = 1e20 });
        Assert.Equal(1e20, DeepRouteStore.LookupCpu()?.LaMinZoom);
        Assert.Null(DeepRouteStore.LookupCpu(otherCpu));
        Assert.True(DeepRouteStore.AnyGpuDeepFor());
        Assert.False(DeepRouteStore.AnyGpuDeepFor(otherCpu));
    }

    [Fact]
    public void Rows_Survive_A_Reload_Infinity_Included()
    {
        DeepRouteStore.StoreCpu(new DeepRouteCpuResult { LaMinZoom = double.PositiveInfinity, LaSimd = false, Measured = "2026-11-15T00:00:00Z" });
        DeepRouteStore.StoreGpu(DevA, new DeepRouteGpuResult { GpuDeep = false, GpuLa = true, Precision = GpuDeepPrecision.Fp32 });
        Assert.True(File.Exists(DeepRouteStore.StorePath));
        DeepRouteStore.ForgetLoaded();
        var c = DeepRouteStore.LookupCpu();
        Assert.Equal(double.PositiveInfinity, c?.LaMinZoom);
        Assert.False(c?.LaSimd);
        var g = DeepRouteStore.LookupGpu(DevA);
        Assert.Equal(new DeepRouteGpuResult { GpuDeep = false, GpuLa = true, Precision = GpuDeepPrecision.Fp32 }, g);
    }

    [Fact]
    public void Another_Schema_Version_Is_Ignored_And_Rewritten()
    {
        DeepRouteStore.StoreCpu(new DeepRouteCpuResult { LaMinZoom = 1e20 });
        string json = File.ReadAllText(DeepRouteStore.StorePath);
        Assert.Contains($"\"Version\": {DeepRouteStore.SchemaVersion}", json);
        File.WriteAllText(DeepRouteStore.StorePath, json.Replace($"\"Version\": {DeepRouteStore.SchemaVersion}", "\"Version\": 0"));
        DeepRouteStore.ForgetLoaded();
        Assert.Null(DeepRouteStore.LookupCpu());
        DeepRouteStore.StoreGpu(DevA, new DeepRouteGpuResult { GpuDeep = true });
        using var doc = JsonDocument.Parse(File.ReadAllText(DeepRouteStore.StorePath));
        Assert.Equal(DeepRouteStore.SchemaVersion, doc.RootElement.GetProperty("Version").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("Cpu").EnumerateObject().Count());   // the old rows are gone
    }

    [Fact]
    public void A_Corrupt_File_Reads_As_Empty_And_Reset_Forgets_Everything()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DeepRouteStore.StorePath)!);
        File.WriteAllText(DeepRouteStore.StorePath, "{ not json");
        DeepRouteStore.ForgetLoaded();
        Assert.Null(DeepRouteStore.LookupCpu());
        DeepRouteStore.StoreCpu(new DeepRouteCpuResult { LaMinZoom = 1e20 });
        DeepRouteStore.Reset();
        Assert.False(File.Exists(DeepRouteStore.StorePath));
        Assert.Null(DeepRouteStore.LookupCpu());
        Assert.Equal(MandelbrotCalculator.LaAutoMinZoom, MandelbrotCalculator.EffectiveLaAutoMinZoom);
    }

    // ── the switches ─────────────────────────────────────────────────────────

    [Fact]
    public void With_No_Row_Every_Switch_Keeps_Todays_Default()
    {
        var k = new FakeKernel(DevA);
        Assert.Equal(1e30, MandelbrotCalculator.EffectiveLaAutoMinZoom);
        Assert.False(MandelbrotCalculator.GpuPerturbationEnabled(k));
        Assert.False(MandelbrotCalculator.GpuLaEnabled(k));
        Assert.False(MandelbrotCalculator.UseFloatExpKernel(k));                         // fp64 present: double
        Assert.True(MandelbrotCalculator.UseFloatExpKernel(new FakeKernel(DevA, fp64: false)));   // no fp64: float-exp
        Assert.False(MandelbrotCalculator.GpuPerturbationEnabled(new FakeKernel(null)));
    }

    [Fact]
    public void A_Stored_Gpu_Row_Moves_The_Gpu_Switches_For_That_Device_Only()
    {
        DeepRouteStore.StoreGpu(DevA, new DeepRouteGpuResult { GpuDeep = true, GpuLa = true, Precision = GpuDeepPrecision.Fp32 });
        var a = new FakeKernel(DevA);
        var b = new FakeKernel(DevADriver2);
        Assert.True(MandelbrotCalculator.GpuPerturbationEnabled(a));
        Assert.True(MandelbrotCalculator.GpuLaEnabled(a));
        Assert.True(MandelbrotCalculator.UseFloatExpKernel(a));
        Assert.False(MandelbrotCalculator.GpuPerturbationEnabled(b));
        Assert.False(MandelbrotCalculator.GpuLaEnabled(b));
        Assert.False(MandelbrotCalculator.UseFloatExpKernel(b));

        // Double stored: the fp64 kernel, but never on a device without fp64.
        DeepRouteStore.StoreGpu(DevA, new DeepRouteGpuResult { Precision = GpuDeepPrecision.Double });
        Assert.False(MandelbrotCalculator.UseFloatExpKernel(a));
        Assert.True(MandelbrotCalculator.UseFloatExpKernel(new FakeKernel(DevA, fp64: false)));
        // A kernel without float-exp never runs it, whatever is stored.
        DeepRouteStore.StoreGpu(DevA, new DeepRouteGpuResult { Precision = GpuDeepPrecision.Fp32 });
        Assert.False(MandelbrotCalculator.UseFloatExpKernel(new FakeKernel(DevA, fx: false)));
    }

    [Fact]
    public void Env_Switches_And_Explicit_Settings_Beat_The_Store_Either_Way()
    {
        var a = new FakeKernel(DevA);
        DeepRouteStore.StoreGpu(DevA, new DeepRouteGpuResult { GpuDeep = true, GpuLa = true, Precision = GpuDeepPrecision.Fp32 });
        MandelbrotCalculator.GpuPerturbEnvSwitch = false;     // FF_GPU_PERTURB=0
        MandelbrotCalculator.GpuLaEnvSwitch = false;          // FF_GPU_LA=0
        MandelbrotCalculator.GpuDeepPrecisionMode = GpuDeepPrecision.Double;   // FF_GPU_PERTURB_PRECISION=double
        Assert.False(MandelbrotCalculator.GpuPerturbationEnabled(a));
        Assert.False(MandelbrotCalculator.GpuLaEnabled(a));
        Assert.False(MandelbrotCalculator.UseFloatExpKernel(a));

        DeepRouteStore.StoreGpu(DevA, new DeepRouteGpuResult { GpuDeep = false, GpuLa = false, Precision = GpuDeepPrecision.Double });
        MandelbrotCalculator.GpuPerturbEnvSwitch = null;
        MandelbrotCalculator.GpuLaEnvSwitch = true;           // FF_GPU_LA=1
        MandelbrotCalculator.UseGpuPerturbation = true;       // explicit on (FF_GPU_PERTURB=1 / a probe)
        MandelbrotCalculator.GpuDeepPrecisionMode = GpuDeepPrecision.Fp32;
        Assert.True(MandelbrotCalculator.GpuPerturbationEnabled(a));
        Assert.True(MandelbrotCalculator.GpuLaEnabled(a));
        Assert.True(MandelbrotCalculator.UseFloatExpKernel(a));

        MandelbrotCalculator.GpuLaEnvSwitch = null;
        MandelbrotCalculator.UseGpuLa = true;                 // explicit on beats a stored off
        Assert.True(MandelbrotCalculator.GpuLaEnabled(a));
    }

    [Fact]
    public void Calibration_Off_Ignores_The_Store()
    {
        var a = new FakeKernel(DevA);
        DeepRouteStore.StoreGpu(DevA, new DeepRouteGpuResult { GpuDeep = true, GpuLa = true, Precision = GpuDeepPrecision.Fp32 });
        DeepRouteStore.StoreCpu(new DeepRouteCpuResult { LaMinZoom = 1e20, LaSimd = true });
        MandelbrotCalculator.UseDeepRouteCalibration = false;  // FF_DEEP_ROUTE_CALIBRATION=0
        Assert.False(MandelbrotCalculator.GpuPerturbationEnabled(a));
        Assert.False(MandelbrotCalculator.GpuLaEnabled(a));
        Assert.False(MandelbrotCalculator.UseFloatExpKernel(a));
        Assert.Equal(MandelbrotCalculator.LaAutoMinZoom, MandelbrotCalculator.EffectiveLaAutoMinZoom);
        // #1640's headless check: a deep view stays CPU-only with calibration off, and
        // opens a device with it on (some GPU row for this CPU says GPU deep).
        var deep = new MandelbrotCalculator(32, 24) { Zoom = 1e20, CenterX = -0.7, Quality = QualityPreset.Extreme };
        Assert.NotNull(deep.CpuOnlyView());
        MandelbrotCalculator.UseDeepRouteCalibration = true;
        Assert.Null(deep.CpuOnlyView());
        MandelbrotCalculator.GpuPerturbEnvSwitch = false;
        Assert.NotNull(deep.CpuOnlyView());
    }

    // Frames: p448's centre (S1630) at Auto LA mode, small and CPU only.
    private const int W = 48, H = 32;
    private static MandelbrotCalculator Frame(double zoom, bool? laSimd = null)
    {
        var c = new MandelbrotCalculator(W, H)
        {
            Quality = QualityPreset.Extreme,
            CenterX = -0.7145519151951202, CenterXLo = -2.0790192891565988e-17, CenterX2 = 1.1753166641509792e-33, CenterX3 = -8.102457136962753e-50,
            CenterY = -0.35402073332318235, CenterYLo = 2.5477179573089096e-17, CenterY2 = -6.072997541261718e-34, CenterY3 = -1.8911088656798612e-50,
            Zoom = zoom, MaxIterations = 4000, ColorMap = new HsvPalette(), LaMode = DeepLaMode.Auto,
        };
        if (laSimd is { } s) c.LaSimd = s;
        c.Calculate(CancellationToken.None);
        return c;
    }

    [Fact]
    public void A_Measured_Crossover_Moves_Where_Auto_Runs_La()
    {
        if (MandelbrotCalculator.LaModeOverride != null) Assert.Skip("FF_LA set");
        Assert.False(Frame(1e25).LastFrameUsedLa, "precondition: no LA below the default 1e30");
        Assert.True(Frame(1e35).LastFrameUsedLa, "precondition: LA past 1e30");
        DeepRouteStore.StoreCpu(new DeepRouteCpuResult { LaMinZoom = 1e20 });
        Assert.True(Frame(1e25).LastFrameUsedLa, "measured 1e20: LA at 1e25");
        DeepRouteStore.StoreCpu(new DeepRouteCpuResult { LaMinZoom = double.PositiveInfinity });
        Assert.False(Frame(1e35).LastFrameUsedLa, "measured never: no LA at 1e35");
        DeepRouteStore.StoreCpu(new DeepRouteCpuResult { LaSimd = true });   // crossover unmeasured: the default
        Assert.False(Frame(1e25).LastFrameUsedLa);
        Assert.True(Frame(1e35).LastFrameUsedLa);
    }

    [Fact]
    public void A_Measured_La_Simd_Turns_The_Simd_Tail_On_Unless_Overridden()
    {
        if (MandelbrotCalculator.LaModeOverride != null) Assert.Skip("FF_LA set");
        if (!Avx2.IsSupported) Assert.Skip("no AVX2");
        var off = Frame(1e35, laSimd: false);
        Assert.True(off.LastFrameUsedLa && !off.LastFrameUsedLaSimd, "precondition: the scalar LA tail by default");
        DeepRouteStore.StoreCpu(new DeepRouteCpuResult { LaSimd = true });
        Assert.True(Frame(1e35, laSimd: false).LastFrameUsedLaSimd, "measured: SIMD tail");
        MandelbrotCalculator.LaSimdEnvSwitch = false;   // FF_LA_SIMD=0
        Assert.False(Frame(1e35, laSimd: false).LastFrameUsedLaSimd, "FF_LA_SIMD=0 beats the store");
    }

    // ── the chooser ──────────────────────────────────────────────────────────

    private static DeepRouteTiming T(DeepRouteCandidate r, double zoom, double ms, double dis = 0)
        => new(r, $"z{zoom:0e0}", zoom, ms, dis);

    // Shaped after this machine (i5-13420H + GT 710): BLA faster below 1e30, LA above,
    // SIMD no gain, every GPU route 3–7x slower than the CPU, GPU LA slower than SA.
    private static DeepRouteTiming[] ThisMachine() =>
    [
        T(DeepRouteCandidate.CpuBla, 1e15, 40), T(DeepRouteCandidate.CpuLa, 1e15, 55), T(DeepRouteCandidate.CpuLaSimd, 1e15, 56),
        T(DeepRouteCandidate.CpuBla, 1e25, 90), T(DeepRouteCandidate.CpuLa, 1e25, 100), T(DeepRouteCandidate.CpuLaSimd, 1e25, 101),
        T(DeepRouteCandidate.CpuBla, 1e30, 300), T(DeepRouteCandidate.CpuLa, 1e30, 120), T(DeepRouteCandidate.CpuLaSimd, 1e30, 122),
        T(DeepRouteCandidate.CpuBla, 1e45, 900), T(DeepRouteCandidate.CpuLa, 1e45, 240), T(DeepRouteCandidate.CpuLaSimd, 1e45, 243),
        T(DeepRouteCandidate.GpuSaFp64, 1e15, 200), T(DeepRouteCandidate.GpuSaFp64, 1e25, 500), T(DeepRouteCandidate.GpuSaFp64, 1e30, 900), T(DeepRouteCandidate.GpuSaFp64, 1e45, 1700),
        T(DeepRouteCandidate.GpuLaFp64, 1e30, 1500), T(DeepRouteCandidate.GpuLaFp64, 1e45, 2400),
    ];

    [Fact]
    public void This_Machine_Reproduces_Todays_Choices()
    {
        var (cpu, gpu) = DeepRouteChooser.Choose(ThisMachine(), "t");
        Assert.Equal(1e30, cpu.LaMinZoom);
        Assert.False(cpu.LaSimd);
        Assert.NotNull(gpu);
        Assert.False(gpu!.GpuDeep);
        Assert.False(gpu.GpuLa);
        Assert.Equal(GpuDeepPrecision.Double, gpu.Precision);
    }

    [Fact]
    public void A_Fast_Route_That_Disagrees_On_Iterations_Is_Excluded()
    {
        // A float-exp kernel 10x faster than everything, but 5 % of pixels wrong on one scene.
        var runs = ThisMachine().Concat(
        [
            T(DeepRouteCandidate.GpuFloatExp, 1e15, 4), T(DeepRouteCandidate.GpuFloatExp, 1e25, 9),
            T(DeepRouteCandidate.GpuFloatExp, 1e30, 12, dis: 0.05), T(DeepRouteCandidate.GpuFloatExp, 1e45, 24),
        ]).ToArray();
        Assert.DoesNotContain(DeepRouteCandidate.GpuFloatExp, DeepRouteChooser.Eligible(runs));
        var (_, gpu) = DeepRouteChooser.Choose(runs, "t");
        Assert.False(gpu!.GpuDeep);
        Assert.Equal(GpuDeepPrecision.Double, gpu.Precision);

        // The same timings agreeing: it wins, and takes GPU deep with it.
        var good = runs.Select(t => t with { Disagreement = 0.001 }).ToArray();
        var (_, gpu2) = DeepRouteChooser.Choose(good, "t");
        Assert.True(gpu2!.GpuDeep);
        Assert.Equal(GpuDeepPrecision.Fp32, gpu2.Precision);
    }

    [Fact]
    public void The_Crossover_Is_The_Shallowest_Scene_From_Which_La_Keeps_Winning()
    {
        DeepRouteTiming[] Runs(params (double Z, double Bla, double La)[] s) => s.SelectMany(x => new[]
            { T(DeepRouteCandidate.CpuBla, x.Z, x.Bla), T(DeepRouteCandidate.CpuLa, x.Z, x.La) }).ToArray();
        Assert.Equal(1e20, DeepRouteChooser.Choose(Runs((1e15, 10, 20), (1e20, 30, 20), (1e30, 60, 30)), "t").Cpu.LaMinZoom);
        // LA wins at 1e20, loses at 1e25, wins after: the crossover is 1e30.
        Assert.Equal(1e30, DeepRouteChooser.Choose(Runs((1e20, 30, 20), (1e25, 30, 40), (1e30, 60, 30)), "t").Cpu.LaMinZoom);
        Assert.Equal(1e15, DeepRouteChooser.Choose(Runs((1e15, 30, 20), (1e30, 60, 30)), "t").Cpu.LaMinZoom);
        Assert.Equal(double.PositiveInfinity, DeepRouteChooser.Choose(Runs((1e15, 10, 20), (1e30, 30, 40)), "t").Cpu.LaMinZoom);
        // No CPU runs: nothing decided; no GPU runs: no GPU row.
        var (cpu, gpu) = DeepRouteChooser.Choose([T(DeepRouteCandidate.GpuSaFp64, 1e20, 5)], "t");
        Assert.Null(cpu.LaMinZoom);
        Assert.Null(cpu.LaSimd);
        Assert.Null(gpu!.GpuDeep);   // no CPU time to compare with
        Assert.Null(DeepRouteChooser.Choose(Runs((1e20, 1, 2)), "t").Gpu);
    }

    [Fact]
    public void Simd_And_Gpu_La_Win_Only_When_Faster_And_Eligible()
    {
        var runs = new[]
        {
            T(DeepRouteCandidate.CpuBla, 1e35, 300), T(DeepRouteCandidate.CpuLa, 1e35, 100), T(DeepRouteCandidate.CpuLaSimd, 1e35, 70),
            T(DeepRouteCandidate.GpuSaFp64, 1e35, 60), T(DeepRouteCandidate.GpuLaFp64, 1e35, 30),
        };
        var (cpu, gpu) = DeepRouteChooser.Choose(runs, "t");
        Assert.True(cpu.LaSimd);
        Assert.Equal(1e35, cpu.LaMinZoom);
        Assert.True(gpu!.GpuLa);
        Assert.True(gpu.GpuDeep);   // 30 ms on the GPU against 70 ms on the CPU
        var bad = runs.Select(t => t.Route is DeepRouteCandidate.CpuLaSimd or DeepRouteCandidate.GpuLaFp64 ? t with { Disagreement = 0.5 } : t).ToArray();
        var (cpu2, gpu2) = DeepRouteChooser.Choose(bad, "t");
        Assert.Null(cpu2.LaSimd);     // not comparable: SIMD not eligible
        Assert.False(gpu2!.GpuLa);    // ran, not eligible
        Assert.True(gpu2.GpuDeep);    // SA at 60 ms still beats LA's 100 ms
    }
}
