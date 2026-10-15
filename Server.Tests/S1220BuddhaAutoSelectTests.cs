// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1220 — uniform Buddhabrot sampling picks the GPU or the CPU by measured speed
// (GpuBuddhaMode.Auto, the new default) instead of the FF_GPU_BUDDHA opt-in. The
// first render per (kernel, in-set / escape, Std / HD) runs 1/MeasureGpuShare of its
// first batch on the GPU and the rest on the CPU twin; the faster (per whole batch)
// wins and is cached until the frame or the sample budget
// moves by more than RemeasureFactor. FF_GPU_BUDDHA=1 / 0 force a path.
//
// The stub kernels run BuddhaUniformSampler — the exact CPU twin — and report a
// chosen dispatch time, so the decision is controlled while the image stays
// checkable: the invariant is that every frame, whichever path (or split) ran it,
// is bit-identical to the CPU-only frame.

using System;
using System.Threading;
using FracturingFog.Models;
using FracturingFog.Rendering;
using Xunit;
using GpuRouteState = FracturingFog.Render.GpuRouteState;
using Mode = FracturingFog.BuddhaFamilyCalculator.GpuBuddhaMode;

namespace FracturingFog.Server.Tests;

public sealed class S1220BuddhaAutoSelectTests : IDisposable
{
    private const int W = 120, H = 90, Iter = 500;
    private readonly Mode _saved = BuddhaFamilyCalculator.GpuBuddhaSelection;
    public S1220BuddhaAutoSelectTests() => BuddhaFamilyCalculator.GpuBuddhaSelection = Mode.Auto;
    public void Dispose() => BuddhaFamilyCalculator.GpuBuddhaSelection = _saved;

    /// <summary>A "GPU" that runs the CPU twin and reports <see cref="MsPerCall"/>.</summary>
    private sealed class StubKernel(double msPerCall, bool fail = false) : IGpuKernel
    {
        public double MsPerCall = msPerCall;
        public int Calls, Samples;
        public bool HasGpuPalette => false;
        public double LastDispatchMs { get; private set; }
        public double LastReadbackMs => 0;
        public string BackendLabel => "Stub";
        public bool SupportsBuddhabrot => true;
        public void SetPalette(FracturingFog.Interefaces.IGpuHlslPalette? palette) { }
        public void Run(int width, int height, double centerX, double centerY, double scale, int maxIter,
            double bailout2, int[] iterDst, float[] smoothDst, float[] finalZrDst, float[] finalZiDst,
            float[] finalDrDst, float[] finalDiDst, int[]? perRowMaxIter = null,
            FractalKind kind = FractalKind.Mandelbrot, float param0 = 0f, float param1 = 0f, uint[]? colorDst = null,
            float[]? trapDst = null, GpuDomainWarp warp = default) => throw new NotSupportedException();
        public void RunBuddhaBatch(in GpuBuddhaBatch batch, uint[] hitsR, uint[] hitsG, uint[] hitsB)
        {
            if (fail) throw new InvalidOperationException("stub dispatch failure");
            Interlocked.Increment(ref Calls);
            Interlocked.Add(ref Samples, batch.Samples);
            BuddhaUniformSampler.Run(batch, hitsR, hitsG, hitsB);
            LastDispatchMs = MsPerCall;
        }
        public void Dispose() { }
    }

    private static BuddhaFamilyCalculator Calc(IGpuKernel? k, bool anti = false, bool hd = false,
        int samples = 60_000, bool progressive = false, int w = W, int h = H)
    {
        BuddhaFamilyCalculator c = anti ? new AntiBuddhabrotCalculator(w, h) : new BuddhabrotCalculator(w, h);
        c.CenterX = -0.5; c.CenterY = 0; c.Zoom = 1.0; c.MaxIterations = Iter;
        c.FractalParameters = new FractalParameters
        {
            BuddhaSamples = samples, BuddhaSeed = 7, BuddhaIterLow = 20, BuddhaIterMid = 100, BuddhaIterHigh = Iter,
            BuddhaQualityMode = hd ? BuddhaQualityMode.HighDefinition : BuddhaQualityMode.Standard,
            BuddhaMetropolis = false, BuddhaProgressive = progressive, BuddhaZoomCompensation = false,
        };
        c.GpuKernel = k; c.UseGpuCompute = k != null;
        if (progressive) c.ProgressiveBatchesOverride = 4;
        return c;
    }

    private static (uint[] R, uint[] G, uint[] B) Hits(BuddhaFamilyCalculator c)
    {
        c.Calculate();
        return ((uint[])c.HitsR.Clone(), (uint[])c.HitsG.Clone(), (uint[])c.HitsB.Clone());
    }

    private static void AssertSameImage((uint[] R, uint[] G, uint[] B) a, (uint[] R, uint[] G, uint[] B) b)
    {
        Assert.Equal(a.R, b.R); Assert.Equal(a.G, b.G); Assert.Equal(a.B, b.B);
        Assert.True(a.R.AsSpan().IndexOfAnyExcept(0u) >= 0, "empty histogram");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void A_Faster_Gpu_Is_Chosen_And_The_Frame_Is_Unchanged(bool anti, bool hd)
    {
        var cpu = Hits(Calc(null, anti, hd));
        var k = new StubKernel(1e-6);   // reports ~0 ms: far faster than the CPU share
        var c = Calc(k, anti, hd);
        var auto = Hits(c);
        AssertSameImage(cpu, auto);
        Assert.True(c.LastCalculateUsedGpu);
        Assert.Equal(GpuRouteState.Gpu, c.LastGpuRoute.State);
        Assert.True(c.LastGpuBuddhaMeasurement is { GpuFaster: true });
        Assert.Contains("ns/sample", c.LastGpuRoute.Detail);
        Assert.Equal(c.LastEffectiveSamples / BuddhaFamilyCalculator.MeasureGpuShare, k.Samples);   // the measured share went to the GPU
    }

    [Fact]
    public void A_Slower_Gpu_Hands_Sampling_To_The_Cpu_And_The_Frame_Is_Unchanged()
    {
        var cpu = Hits(Calc(null));
        var k = new StubKernel(1e6);
        var c = Calc(k);
        var auto = Hits(c);
        AssertSameImage(cpu, auto);
        Assert.False(c.LastCalculateUsedGpu);
        Assert.Equal("CPU faster", c.LastGpuRoute.Reason);
        Assert.Contains("ns/sample", c.LastGpuRoute.Detail);
        Assert.Equal(1, k.Calls);   // only the measuring share
    }

    [Fact]
    public void The_Decision_Is_Cached_Per_Kernel_And_Kind()
    {
        var slow = new StubKernel(1e6);
        Hits(Calc(slow));
        Assert.Equal(1, slow.Calls);
        Hits(Calc(slow));
        Assert.Equal(1, slow.Calls);                      // no second measurement, all CPU
        Assert.NotNull(BuddhaFamilyCalculator.CachedGpuBuddhaMeasurement(slow, inSet: false, hd: false));
        Assert.Null(BuddhaFamilyCalculator.CachedGpuBuddhaMeasurement(slow, inSet: true, hd: false));

        Hits(Calc(slow, anti: true));                     // a different kind measures afresh
        Assert.Equal(2, slow.Calls);

        var fast = new StubKernel(1e-6);
        Hits(Calc(fast));
        int afterFirst = fast.Calls;
        var c = Calc(fast);
        Hits(c);
        Assert.Equal(afterFirst + 1, fast.Calls);        // the whole (single) batch on the GPU
        Assert.Equal(c.LastEffectiveSamples, fast.Samples - c.LastEffectiveSamples / BuddhaFamilyCalculator.MeasureGpuShare);
    }

    [Fact]
    public void A_Much_Larger_Frame_Or_Budget_Is_Measured_Again()
    {
        var k = new StubKernel(1e6);
        Hits(Calc(k));
        Assert.Equal(1, k.Calls);
        Hits(Calc(k, w: W * 3, h: H * 2));               // 6x the pixels
        Assert.Equal(2, k.Calls);
        Hits(Calc(k, w: W * 3, h: H * 2, samples: 60_000 * 5));   // 5x the samples per batch
        Assert.Equal(3, k.Calls);
        Hits(Calc(k, w: W * 3, h: H * 2, samples: 60_000 * 6));   // within the factor: cached
        Assert.Equal(3, k.Calls);
    }

    [Fact]
    public void A_Progressive_Render_Measures_On_Its_First_Batch_Only()
    {
        var cpu = Hits(Calc(null, samples: 80_000, progressive: true));
        var k = new StubKernel(1e-6);
        var c = Calc(k, samples: 80_000, progressive: true);
        var auto = Hits(c);
        AssertSameImage(cpu, auto);
        Assert.Equal(4, k.Calls);                         // a share of batch 0, then batches 1..3 whole
    }

    [Fact]
    public void Overrides_Force_A_Path()
    {
        var slow = new StubKernel(1e6);
        BuddhaFamilyCalculator.GpuBuddhaSelection = Mode.Gpu;
        var g = Calc(slow);
        Hits(g);
        Assert.True(g.LastCalculateUsedGpu);
        Assert.Null(g.LastGpuBuddhaMeasurement);
        Assert.Equal(1, slow.Calls);

        var fast = new StubKernel(1e-6);
        BuddhaFamilyCalculator.GpuBuddhaSelection = Mode.Cpu;
        var c = Calc(fast);
        Hits(c);
        Assert.False(c.LastCalculateUsedGpu);
        Assert.Equal("CPU forced", c.LastGpuRoute.Reason);
        Assert.Equal(0, fast.Calls);
    }

    [Fact]
    public void A_Failing_Gpu_While_Measuring_Falls_Back_To_The_Exact_Cpu_Frame()
    {
        var cpu = Hits(Calc(null));
        var k = new StubKernel(1e-6, fail: true);
        var c = Calc(k);
        AssertSameImage(cpu, Hits(c));
        Assert.Equal("GPU error", c.LastGpuRoute.Reason);
        Assert.Null(BuddhaFamilyCalculator.CachedGpuBuddhaMeasurement(k, false, false));
    }

    [Theory]
    [InlineData(null, Mode.Auto)]
    [InlineData("", Mode.Auto)]
    [InlineData("auto", Mode.Auto)]
    [InlineData("1", Mode.Gpu)]
    [InlineData(" GPU ", Mode.Gpu)]
    [InlineData("yes", Mode.Gpu)]
    [InlineData("0", Mode.Cpu)]
    [InlineData("off", Mode.Cpu)]
    [InlineData("cpu", Mode.Cpu)]
    public void The_Environment_Value_Parses(string? value, Mode expected)
        => Assert.Equal(expected, BuddhaFamilyCalculator.ParseGpuBuddhaMode(value));
}
