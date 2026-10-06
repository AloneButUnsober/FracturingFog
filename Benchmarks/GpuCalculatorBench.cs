// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// GpuCalculatorBench.cs — #1162 slice A: BenchmarkDotNet harness for the
// ILGPU 3D raymarch kernels.
//
// Invoke via:   FracturingFog.exe --bench --filter *GpuCalculatorBench*
// (the default `--bench` run stays the CPU MandelbrotBench matrix).
//
// Each case drives the production calculator (MandelbulbCalculator,
// KifsCalculator, …) with Lighting.UseGpuRender = true, so the timed frame is
// exactly what the app renders — params built by the real code, kernel
// launch, Synchronize, device→host copy. Coverage: 8 families x {640x360,
// 1920x1080}.
//
// GPU fallback is silent in the app (a failed kernel load or a missing
// device falls through to the CPU ShadingPipeline). A benchmark must not time
// that fallback and call it a GPU number, so Setup proves the GPU path ran:
// every successful GPU frame allocates its output buffers through
// GpuMemoryStats, a CPU frame allocates nothing there. No device bytes on the
// steady-state frame → Setup throws and BenchmarkDotNet reports the case as
// failed instead of producing a number.
//
// Extra columns (CaseMetrics):
//   • DeviceAlloc/op — ILGPU device bytes allocated per frame (the kernels
//     allocate output / LUT / depth buffers per frame; measured on a
//     steady-state frame in Setup).
//   • Device — the accelerator that ran the case (type + name).

using System;
using System.Threading;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

using FracturingFog.Calculators.Gpu;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Benchmarks;

public enum GpuFamily
{
    Mandelbulb,
    Mandelbox,
    Menger,
    Sierpinski,
    QJulia,
    QMandel,
    Kleinian,
    Bicomplex,
}

[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[Config(typeof(Config))]
public class GpuCalculatorBench
{
    private IFractalCalculator _calc = null!;

    [Params(GpuFamily.Mandelbulb, GpuFamily.Mandelbox, GpuFamily.Menger, GpuFamily.Sierpinski,
            GpuFamily.QJulia, GpuFamily.QMandel, GpuFamily.Kleinian, GpuFamily.Bicomplex)]
    public GpuFamily Family { get; set; }

    [Params(640, 1920)]
    public int Width { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        if (!GpuAcceleratorHost.TryAcquire(out var accelerator))
            throw new InvalidOperationException(
                $"No GPU accelerator available ({GpuAcceleratorHost.LastError}); GPU benchmarks cannot run.");

        int height = Width == 640 ? 360 : 1080;
        var fx = LightingFxData.CreateDefault();
        fx.UseGpuRender = true;
        var fp = new FractalParameters { Lighting = fx };

        _calc = Family switch
        {
            GpuFamily.Mandelbulb => new MandelbulbCalculator(Width, height) { FractalParameters = fp },
            GpuFamily.Mandelbox  => new MandelboxCalculator(Width, height) { FractalParameters = fp },
            GpuFamily.Menger     => new KifsCalculator(Width, height) { FractalParameters = WithFold(fp, KifsFoldKind.Menger) },
            GpuFamily.Sierpinski => new KifsCalculator(Width, height) { FractalParameters = WithFold(fp, KifsFoldKind.Sierpinski) },
            GpuFamily.QJulia     => new QuatJuliaCalculator(Width, height) { FractalParameters = fp },
            GpuFamily.QMandel    => new QuatMandelbrotCalculator(Width, height) { FractalParameters = fp },
            GpuFamily.Kleinian   => new KleinianCalculator(Width, height) { FractalParameters = fp },
            GpuFamily.Bicomplex  => new BicomplexMandelbrotCalculator(Width, height) { FractalParameters = fp },
            _ => throw new ArgumentOutOfRangeException(nameof(Family)),
        };
        _calc.ColorMap = ColorPalette.BuiltIns[0];
        _calc.Zoom = 1.0;

        // First frame JITs + loads the kernel; the second is steady state.
        _calc.Calculate(CancellationToken.None);
        long before = GpuMemoryStats.AllocatedBytes;
        _calc.Calculate(CancellationToken.None);
        long perFrame = GpuMemoryStats.AllocatedBytes - before;

        if (perFrame <= 0)
            throw new InvalidOperationException(
                $"{Family}: the GPU path did not run on {accelerator.AcceleratorType} '{accelerator.Name}' " +
                "(the frame fell back to the CPU pipeline). Refusing to time a CPU fallback as a GPU number. " +
                $"GPU calculator state: {DescribeGpuState(_calc)}");

        string key = CaseMetrics.Key(new (string, object?)[]
        {
            (nameof(Family), Family),
            (nameof(Width), Width),
        });
        CaseMetrics.Record(CaseMetrics.DeviceAllocPerOp, key, CaseMetrics.FormatMegabytes(perFrame));
        CaseMetrics.Record(CaseMetrics.Device, key, $"{accelerator.AcceleratorType} {accelerator.Name}");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        (_calc as IDisposable)?.Dispose();
        _calc = null!;
    }

    [Benchmark]
    public void Calculate() => _calc.Calculate(CancellationToken.None);

    // Failure diagnostics only: the production calculators hold their ILGPU
    // calculator in a private lazily-created field. Null means Render was never
    // reached (a gate in the calculator kept the frame on the CPU); non-null
    // with LastError set means the kernel failed on this device.
    private static string DescribeGpuState(object calc)
    {
        var parts = new System.Collections.Generic.List<string>();
        foreach (var f in calc.GetType().GetFields(
                     System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic))
        {
            if (f.FieldType.Namespace != typeof(GpuMemoryStats).Namespace
                || !f.FieldType.Name.EndsWith("GpuCalculator", StringComparison.Ordinal))
                continue;
            object? gpu = f.GetValue(calc);
            if (gpu == null) { parts.Add($"{f.Name}=null (Render never called)"); continue; }
            string err = gpu.GetType().GetProperty("LastError")?.GetValue(gpu) as string ?? "";
            parts.Add($"{f.Name}: LastError='{err}'");
        }
        return parts.Count == 0 ? "no GPU calculator field found" : string.Join("; ", parts);
    }

    private static FractalParameters WithFold(FractalParameters source, KifsFoldKind fold)
    {
        var fp = new FractalParameters { Lighting = source.Lighting };
        fp.KifsFold = fold;
        return fp;
    }

    private sealed class Config : ManualConfig
    {
        public Config()
        {
            // Same in-process job as MandelbrotBench (see the toolchain note
            // there). CaseMetrics columns also depend on running in-process.
            AddJob(Job.Default
                .WithToolchain(InProcessEmitToolchain.Instance)
                .WithWarmupCount(2)
                .WithIterationCount(5)
                .WithInvocationCount(1)
                .WithUnrollFactor(1));
            AddDiagnoser(MemoryDiagnoser.Default);
            AddColumn(new CaseMetricColumn(CaseMetrics.DeviceAllocPerOp,
                "ILGPU device bytes allocated per frame (steady-state frame, via GpuMemoryStats)",
                isNumeric: true, UnitType.Size));
            AddColumn(new CaseMetricColumn(CaseMetrics.Device,
                "Accelerator that ran the case (ILGPU type + device name)",
                isNumeric: false, UnitType.Dimensionless));
        }
    }
}
