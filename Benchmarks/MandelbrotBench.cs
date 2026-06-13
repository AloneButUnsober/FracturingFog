// MandelbrotBench.cs — BenchmarkDotNet harness for MandelbrotCalculator.
//
// Invoke via:   FracturingFog.exe --bench
//
// Coverage matrix:
//   • Resolution: 640x360 (fast), 1920x1080 (representative)
//   • Precision regime:
//       - Shallow SP  : Zoom=1, scalar SIMD double path (ComputeRowSP)
//       - Medium  SP  : Zoom=1e8, higher iteration cost, still SP
//       - Deep    HP  : Zoom=1e15, DD perturbation AVX2 4-lane (ComputeRowHP)
//   • Theme dispatch:
//       - HsvPalette          : cheap 2D, devirtualized
//       - PhongStoneMap       : 3D normal + lighting, devirtualized
//       - StripeAverageClassic: orbit-aware scalar path (different code path)
//
// MemoryDiagnoser shows per-frame allocations so resize / hot-path churn
// regressions are visible. Each combo runs full Calculate() so all stages
// (iteration + aux fill + color) are timed end to end.

using System;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog.Benchmarks;

public enum PrecisionRegime
{
    ShallowSP,
    MediumSP,
    DeepHP,
    // Same center as DeepHP but maxIter = 2048 < refLen (~3088) so every
    // pixel stays inside the AVX2 perturbation loop — no fall-through to
    // the scalar DD glitch fallback. This is the workload where SA and
    // BLA acceleration are actually visible in the wall-time numbers.
    DeepHPInPT,
}

public enum ThemeChoice
{
    Hsv,
    PhongStone,
    StripeAverage,
}

[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[Config(typeof(Config))]
public class MandelbrotBench
{
    private MandelbrotCalculator _calc = null!;
    private IColorMap _theme = null!;

    // 1080p is the cap for default sweep — 4K added as opt-in via the
    // Resolution param so a normal run finishes in reasonable wall time.
    [Params(640, 1920)]
    public int Width { get; set; }

    [Params(PrecisionRegime.ShallowSP, PrecisionRegime.MediumSP,
            PrecisionRegime.DeepHP, PrecisionRegime.DeepHPInPT)]
    public PrecisionRegime Regime { get; set; }

    [Params(ThemeChoice.Hsv, ThemeChoice.PhongStone)]
    public ThemeChoice Theme { get; set; }

    /// <summary>
    /// HP-path acceleration toggle. Only meaningful for DeepHP / DeepHPInPT
    /// regimes — SA + BLA are HP-only. SP regimes ignore this flag.
    /// </summary>
    [Params(true, false)]
    public bool Accel { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        int height = Width == 640 ? 360 : 1080;
        _calc = new MandelbrotCalculator(Width, height);

        _theme = Theme switch
        {
            ThemeChoice.Hsv          => new HsvPalette(),
            ThemeChoice.PhongStone   => new PhongStoneMap(),
            ThemeChoice.StripeAverage => new StripeAverageClassicMap(),
            _ => new HsvPalette(),
        };
        _calc.ColorMap = _theme;
        _calc.DisableAcceleration = !Accel;

        // Center on a region with rich detail so iteration counts approximate
        // real usage (not just empty in-set or fast-escape outer halo).
        switch (Regime)
        {
            case PrecisionRegime.ShallowSP:
                _calc.CenterX = -0.5;
                _calc.CenterY = 0.0;
                _calc.Zoom = 1.0;
                _calc.MaxIterations = 512;
                break;

            case PrecisionRegime.MediumSP:
                _calc.CenterX = -0.743643887037151;
                _calc.CenterY = 0.131825904205330;
                _calc.Zoom = 1e8;
                _calc.MaxIterations = 2048;
                break;

            case PrecisionRegime.DeepHP:
                _calc.CenterX = -0.743643887037151;
                _calc.CenterY = 0.131825904205330;
                _calc.Zoom = 1e15;
                _calc.MaxIterations = 4096;
                break;

            case PrecisionRegime.DeepHPInPT:
                _calc.CenterX = -0.743643887037151;
                _calc.CenterY = 0.131825904205330;
                _calc.Zoom = 1e15;
                // Reference orbit at this centre escapes near iter 3088; cap
                // maxIter below that so every pixel resolves inside the AVX2
                // perturbation loop instead of falling through to scalar DD.
                _calc.MaxIterations = 2048;
                break;
        }

        // Warm — first call sometimes JITs the generic specialisation.
        _calc.Calculate();
    }

    [Benchmark]
    public void Calculate() => _calc.Calculate();

    private sealed class Config : ManualConfig
    {
        public Config()
        {
            // InProcessEmitToolchain bypasses the external-build path. Needed
            // because BenchmarkDotNet's default CsProjGenerator looks for a
            // .csproj whose filename matches AssemblyName ("FracturingFog"),
            // but the project file is actually "FracturingFogCLD.csproj".
            // In-process trades per-bench process isolation for a working run
            // — fine here since Calculate() owns its own state.
            AddJob(Job.Default
                .WithToolchain(InProcessEmitToolchain.Instance)
                .WithWarmupCount(2)
                .WithIterationCount(5)
                .WithInvocationCount(1)
                .WithUnrollFactor(1));
            AddDiagnoser(MemoryDiagnoser.Default);
        }
    }
}

public static class BenchEntry
{
    // WinExe has no attached console — without one, BenchmarkDotNet output
    // is invisible. Attach the parent terminal's console if launched from
    // one; otherwise allocate a fresh console window so the user sees the
    // progress + final summary table.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();
    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();
    private const int ATTACH_PARENT_PROCESS = -1;

    public static int Run(string[] args)
    {
        // Phase X.3 / Slice 3.2: gate Win32 console-attach so the calls are
        // unreachable on non-Win hosts once this file follows the entry point
        // into FracturingFog.App (net10.0). On Linux/macOS stdout/stderr are
        // already wired to the launching terminal.
        if (OperatingSystem.IsWindows())
            AttachOrAllocConsoleAndRebindStreams();

        Console.WriteLine("FracturingFog benchmark harness");
        Console.WriteLine($"Args after --bench: [{string.Join(' ', args.AsSpan(1).ToArray())}]");

        Summary summary;
        if (args.Length > 1)
        {
            // Pass-through to BenchmarkSwitcher for --filter, --list, etc.
            var summaries = BenchmarkSwitcher
                .FromAssembly(typeof(MandelbrotBench).Assembly)
                .Run(args[1..]);
            summary = null!;
            foreach (var s in summaries) summary = s;
        }
        else
        {
            // Default: run all benchmarks in MandelbrotBench. Switcher with
            // empty args prints help and exits without running anything.
            summary = BenchmarkRunner.Run<MandelbrotBench>();
        }

        Console.WriteLine("Bench complete. Press any key to exit.");
        if (Console.IsInputRedirected == false) Console.ReadKey();
        if (OperatingSystem.IsWindows())
            FreeConsole();
        return 0;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AttachOrAllocConsoleAndRebindStreams()
    {
        if (!AttachConsole(ATTACH_PARENT_PROCESS))
            AllocConsole();

        // Reopen stdout/stderr against the now-attached console handle.
        // Without this, Console.WriteLine no-ops because the streams were
        // bound to NUL at process start (WinExe default).
        var stdout = new System.IO.StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(stdout);
        Console.SetError(stdout);
    }
}
