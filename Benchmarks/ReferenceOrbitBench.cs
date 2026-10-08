// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ReferenceOrbitBench.cs — #1166 part 1: the QD reference-orbit build, CPU
// (ComputeReferenceOrbitQD) vs GPU (MandelbrotRefOrbitGpu, ILGPU, opt-in via
// MandelbrotCalculator.UseGpuReferenceOrbit).
//
// Invoke via:   FracturingFog.exe --bench --filter *ReferenceOrbitBench*
//
// The orbit is built once per view and cached by centre, so a frame bench only
// builds it on the warm frame. Each op here first flips the lowest QD limb of
// the centre (CenterX3 between 1e-60 and 2e-60, far below a pixel at any zoom the
// QD tier covers), which misses the cache, then builds the orbit through
// MandelbrotCalculator.BuildReferenceOrbit, the same path a deep-zoom frame
// takes. Zoom 1e30 selects the QD tier. The centre is c = -1.9: real c in
// [-2, 0.25] never escapes, so Length is the real orbit length, and -1.9 is in
// the chaotic band, so the orbit never settles. The low limb is never zero: a
// centre whose low QD limbs are exactly zero costs the CPU ~3 ms extra over the
// first ~10K iterations (450 against a steady ~130 ns per iteration, while the
// error terms are tiny), and an interior c (an orbit that converges) is slower
// still early on (~1.2 µs). Neither is what a deep-zoom reference orbit, whose
// centre carries all its limbs, looks like.
//
// The GPU orbit is one sequential thread, run in launches of ~100 ms (#1166: a
// single launch of a long orbit tripped the OS watchdog on a weak-fp64 card).
//
// Like the other GPU benches, the Gpu case refuses to time a fallback: Setup
// checks LastFrameBuiltGpuReferenceOrbit after a warm build and throws if the
// orbit was built on the CPU, or if the "GPU" was ILGPU's CPU accelerator (the
// kernel's last-resort device), so BenchmarkDotNet reports the case as failed
// instead of producing a CPU number. The Device column records what ran it.
//
// GPU-vs-CPU orbit parity is --gpurefprobe's job (QD round-off), not this one's.

using System;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

using FracturingFog.Models;

namespace FracturingFog.Benchmarks;

public enum RefOrbitBuilder
{
    Cpu,
    Gpu,
}

[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.Default)]
[Config(typeof(Config))]
public class ReferenceOrbitBench
{
    private MandelbrotCalculator _calc = null!;
    private bool _savedUseGpuReferenceOrbit;
    private bool _flip;

    [Params(RefOrbitBuilder.Cpu, RefOrbitBuilder.Gpu)]
    public RefOrbitBuilder Builder { get; set; }

    [Params(10_000, 100_000, 1_000_000)]
    public int Length { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // Process-wide static (Wave 2.12); restored in Cleanup.
        _savedUseGpuReferenceOrbit = MandelbrotCalculator.UseGpuReferenceOrbit;
        MandelbrotCalculator.UseGpuReferenceOrbit = Builder == RefOrbitBuilder.Gpu;

        _calc = new MandelbrotCalculator(8, 8)
        {
            ColorMap = new HsvPalette(),
            CenterX = -1.9, CenterY = 0.0,   // bounded and chaotic (see the header)
            CenterX3 = 2e-60,
            Zoom = 1e30,                     // QD tier
            MaxIterations = Length,
        };

        // Warm build: JITs the kernel / acquires the accelerator, and proves the
        // orbit was built where the case says.
        int len = _calc.BuildReferenceOrbit();
        if (len != Length)
            throw new InvalidOperationException($"orbit length {len}, expected {Length} (bounded centre).");

        string device = "CPU QD (ComputeReferenceOrbitQD)";
        if (Builder == RefOrbitBuilder.Gpu)
        {
            device = _calc.GpuReferenceOrbitDevice;
            if (!_calc.LastFrameBuiltGpuReferenceOrbit)
                throw new InvalidOperationException(
                    "The GPU reference orbit fell back to the CPU (no CUDA device, or the kernel failed). " +
                    "Refusing to time a CPU fallback as a GPU number.");
            if (device.StartsWith("CPU", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"The GPU reference orbit ran on ILGPU's CPU accelerator ('{device}'), not a GPU. " +
                    "Refusing to time it as a GPU number.");
        }

        CaseMetrics.Record(CaseMetrics.Device,
            CaseMetrics.Key(new (string, object?)[]
            {
                (nameof(Builder), Builder),
                (nameof(Length), Length),
            }),
            device);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        MandelbrotCalculator.UseGpuReferenceOrbit = _savedUseGpuReferenceOrbit;
        _calc = null!;
    }

    /// <summary>One uncached QD reference-orbit build.</summary>
    [Benchmark]
    public int Build()
    {
        _flip = !_flip;
        _calc.CenterX3 = _flip ? 1e-60 : 2e-60;   // a new centre: misses the orbit cache
        return _calc.BuildReferenceOrbit();
    }

    private sealed class Config : ManualConfig
    {
        public Config()
        {
            // Same in-process job as the other benches (the Device column needs it).
            // Few iterations: a GPU build of the longest orbit takes seconds on a
            // weak-fp64 card. The pilot stage sizes each iteration to ~250 ms: a
            // single 5 ms op after BenchmarkDotNet's pause between iterations
            // started on a cold core clock and read 4.6 ms for a 1.3 ms build,
            // while a 15 s GPU build still runs once per iteration (under a longer
            // in-process timeout than the 5-minute default).
            AddJob(Job.Default
                .WithToolchain(new InProcessEmitToolchain(TimeSpan.FromMinutes(30), logOutput: true))
                .WithWarmupCount(1)
                .WithIterationCount(5)
                .WithIterationTime(Perfolizer.Horology.TimeInterval.FromMilliseconds(250))
                .WithUnrollFactor(1));
            AddDiagnoser(MemoryDiagnoser.Default);
            AddColumn(new CaseMetricColumn(CaseMetrics.Device,
                "What built the orbit (CPU QD, or the ILGPU accelerator)",
                isNumeric: false, UnitType.Dimensionless));
        }
    }
}
