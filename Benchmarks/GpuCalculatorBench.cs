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

// Execution order follows these enum VALUES (BenchmarkDotNet sorts param
// values; [Params] order is ignored). Mandelbulb is deliberately LAST: on some
// CUDA devices its kernel faults at launch (#1169), a CUDA launch failure
// poisons the whole process, and GpuAcceleratorHost then latches GPU 3D off —
// every later case would report NA. Last keeps the other families measurable.
public enum GpuFamily
{
    Mandelbox,
    Menger,
    Sierpinski,
    // #1173-D — the Wave 5.9.f1 KIFS folds (KifsFoldGpuCalculator); CPU-only before G3.2.
    Octahedron,
    Dodecahedron,
    MandelboxRot,
    QJulia,
    QMandel,
    // #1173-F — QuatMandel with dual-orbit surface colour; CPU-only before G3.3.
    QMandelDual,
    Kleinian,
    // #880 / #1173-E — 9-sphere necklace + rotation fold + last-generator colour; CPU-only before G3.4.
    KleinianNecklace,
    Bicomplex,
    // #1173-C — a non-K slice axis (R, sliceW 0.4): CPU-only before G3.1.
    BicomplexR,
    // #1173-G — the Coquaternion kernel (no GPU path before G3.5).
    Coquaternion,
    // #1173-B — Mandelbox under an HDRI sky with IBL ambient + reflections (gradient sky before G2.3).
    MandelboxHdri,
    Mandelbulb,
}

[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[Config(typeof(Config))]
public class GpuCalculatorBench
{
    private IFractalCalculator _calc = null!;

    // Family is declared FIRST so it is the outer axis, and Mandelbulb (last
    // enum value) runs after every other case at every width. A Mandelbulb
    // fault (#1169) latches GPU 3D off for the rest of the process, so it must
    // not run before anything else is measured. #1170 tiles the dispatch under
    // the GPU watchdog, so a 1080p frame no longer faults on a slow GPU and
    // Width no longer needs to be the outer axis.
    [Params(GpuFamily.Mandelbox, GpuFamily.Menger, GpuFamily.Sierpinski,
            GpuFamily.Octahedron, GpuFamily.Dodecahedron, GpuFamily.MandelboxRot,
            GpuFamily.QJulia, GpuFamily.QMandel, GpuFamily.QMandelDual, GpuFamily.Kleinian, GpuFamily.KleinianNecklace, GpuFamily.Bicomplex,
            GpuFamily.BicomplexR, GpuFamily.Coquaternion, GpuFamily.MandelboxHdri, GpuFamily.Mandelbulb)]
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
            GpuFamily.Octahedron => new KifsCalculator(Width, height) { FractalParameters = WithFold(fp, KifsFoldKind.Octahedron) },
            GpuFamily.Dodecahedron => new KifsCalculator(Width, height) { FractalParameters = WithFold(fp, KifsFoldKind.Dodecahedron) },
            GpuFamily.MandelboxRot => new KifsCalculator(Width, height) { FractalParameters = WithFold(fp, KifsFoldKind.MandelboxRot) },
            GpuFamily.QJulia     => new QuatJuliaCalculator(Width, height) { FractalParameters = fp },
            GpuFamily.QMandel    => new QuatMandelbrotCalculator(Width, height) { FractalParameters = fp },
            GpuFamily.QMandelDual => new QuatMandelbrotCalculator(Width, height)
            {
                FractalParameters = new FractalParameters { Lighting = fx, QMandelDualOrbitColor = true },
            },
            GpuFamily.Kleinian   => new KleinianCalculator(Width, height) { FractalParameters = fp },
            GpuFamily.KleinianNecklace => new KleinianCalculator(Width, height)
            {
                FractalParameters = new FractalParameters
                {
                    Lighting = fx,
                    KleinianPreset = KleinianPreset.NecklaceN, KleinianNecklaceCount = 9,
                    KleinianRotationAngle = 15.0, KleinianRotationAxisY = 0.0, KleinianRotationAxisZ = 1.0,
                    KleinianColorSource = KleinianColorSource.LastGenerator,
                },
            },
            GpuFamily.Bicomplex  => new BicomplexMandelbrotCalculator(Width, height) { FractalParameters = fp },
            GpuFamily.Coquaternion => new CoquaternionMandelbrotCalculator(Width, height) { FractalParameters = fp },
            GpuFamily.MandelboxHdri => new MandelboxCalculator(Width, height) { FractalParameters = new FractalParameters { Lighting = HdriScene(fx) } },
            GpuFamily.BicomplexR => new BicomplexMandelbrotCalculator(Width, height)
            {
                FractalParameters = new FractalParameters
                {
                    Lighting = fx, BicomplexSliceAxis = BicomplexSliceAxis.R, BicomplexSliceW = 0.4,
                },
            },
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

    private const string BenchEnv = "gpu-bench-env";

    /// <summary>A synthetic 512x256 equirect (no file I/O in the timed path) with the
    /// HDRI on every env lookup: backdrop, IBL ambient and a reflection bounce.</summary>
    private static LightingFxData HdriScene(LightingFxData fx)
    {
        const int w = 512, h = 256;
        var data = new float[w * h * 3];
        for (int i = 0; i < w * h; i++)
        {
            int x = i % w, y = i / w;
            data[3 * i] = 0.2f + 0.8f * x / w;
            data[3 * i + 1] = 0.2f + 0.6f * (1f - (float)y / h);
            data[3 * i + 2] = 0.3f + 0.5f * (float)y / h;
        }
        HdriRegistry.Register(BenchEnv, new HdriImage(w, h, data));
        fx.SkyMode = SkyMode.Hdri;
        fx.EnvironmentName = BenchEnv;
        fx.IblStrength = 0.6;
        fx.ReflectionStrength = 0.5;
        fx.Roughness = 0.4;
        return fx;
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
