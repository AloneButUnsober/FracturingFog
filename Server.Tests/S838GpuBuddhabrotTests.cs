// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #838 / GPU parity plan G4.7 — the Buddhabrot family's uniform sample pass on the
// GPU (BuddhaKernelSource, D3D11 + Vulkan). The GPU draws its own random samples,
// so its image is a different realization of the same distribution as the CPU's:
// parity is statistical. These run on the Vulkan device (skipped without one):
//   • #1218 — the GPU and the CPU render the same image (one shared sampler,
//     BuddhaUniformSampler), and the CPU's parallel split equals a one-thread run;
//   • the shared float sampler against the classic double-precision System.Random
//     sampler: distance between block-summed, jointly normalised histograms, held
//     to the distance between two seeds of the double sampler (the noise floor),
//     with a shifted-view contrast so a loose metric can't pass — Buddhabrot and
//     AntiBuddhabrot, Standard and HD splats;
//   • real-axis symmetry of the GPU's own uniform (non-mirrored) histogram, an
//     invariant of the Mandelbrot map that holds independently of the CPU;
//   • the kernel against BuddhaUniformSampler.Run, a strict-IEEE-float C#
//     replay of its algorithm (same RNG, cycle detection, splat) — near pixel-
//     exact, which pins the two passes replaying one orbit (the bug a multiply-add
//     fused differently in the two passes caused);
//   • the same seed gives a bit-identical GPU image, a different seed does not;
//   • Metropolis, Dual Buddhabrot, the opt-out, the zoom limit and a missing
//     kernel stay on the CPU with a reason; a failing kernel falls back to the
//     exact CPU frame.

using System;
using System.Linq;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Vulkan;
using Xunit;
using GpuRoute = FracturingFog.Render.GpuRoute;
using GpuRouteState = FracturingFog.Render.GpuRouteState;

namespace FracturingFog.Server.Tests;

public sealed class S838GpuBuddhabrotTests
{
    private const int W = 160, H = 120, Block = 8, Iter = 2000;

    private static VulkanComputeKernel Device()
    {
        // #1218 — GPU Buddhabrot sampling is opt-in (per-thread override here).
        BuddhaFamilyCalculator.UseGpuBuddha = true;
        var k = VulkanComputeKernel.TryCreateWithOwnContext();
        if (k is null) Assert.Skip("no Vulkan device on this host");
        return k;
    }

    private static FractalParameters Params(int seed, bool hd, int samples = 400_000) => new()
    {
        BuddhaSamples = samples, BuddhaSeed = seed,
        BuddhaIterLow = 20, BuddhaIterMid = 200, BuddhaIterHigh = Iter,
        BuddhaQualityMode = hd ? BuddhaQualityMode.HighDefinition : BuddhaQualityMode.Standard,
        BuddhaMetropolis = false, BuddhaProgressive = false,
    };

    private static BuddhaFamilyCalculator Calc(bool anti, int seed, bool hd, IGpuKernel? k, double centerY = 0)
    {
        BuddhaFamilyCalculator c = anti ? new AntiBuddhabrotCalculator(W, H) : new BuddhabrotCalculator(W, H);
        c.CenterX = -0.5; c.CenterY = centerY; c.Zoom = 1.0; c.MaxIterations = Iter;
        c.FractalParameters = Params(seed, hd);
        c.GpuKernel = k; c.UseGpuCompute = k != null;
        return c;
    }

    private static (uint[] R, uint[] G, uint[] B) Run(BuddhaFamilyCalculator c)
    {
        c.Calculate();
        return ((uint[])c.HitsR.Clone(), (uint[])c.HitsG.Clone(), (uint[])c.HitsB.Clone());
    }

    // The classic double-precision System.Random sampler (Metropolis / deep zoom /
    // pre-#1218 uniform renders): the independent statistical reference.
    private static (uint[] R, uint[] G, uint[] B) RunDouble(BuddhaFamilyCalculator c)
    {
        bool saved = BuddhaFamilyCalculator.UseSharedUniformSampler;
        BuddhaFamilyCalculator.UseSharedUniformSampler = false;
        try { return Run(c); }
        finally { BuddhaFamilyCalculator.UseSharedUniformSampler = saved; }
    }

    // Block-summed bands, normalised by the grand total (band fractions count too).
    private static double[] Blocks((uint[] R, uint[] G, uint[] B) h, bool flipY = false)
    {
        int bw = W / Block, bh = H / Block;
        var v = new double[3 * bw * bh];
        double total = 0;
        var bands = new[] { h.R, h.G, h.B };
        for (int band = 0; band < 3; band++)
            for (int y = 0; y < H; y++)
            {
                int sy = flipY ? H - 1 - y : y;
                for (int x = 0; x < W; x++)
                {
                    double a = bands[band][sy * W + x];
                    v[band * bw * bh + (y / Block) * bw + x / Block] += a;
                    total += a;
                }
            }
        Assert.True(total > 0, "empty histogram");
        for (int i = 0; i < v.Length; i++) v[i] /= total;
        return v;
    }

    private static double Distance(double[] a, double[] b) => a.Zip(b).Sum(p => Math.Abs(p.First - p.Second));

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Gpu_Histogram_Matches_The_Cpu_Distribution(bool anti, bool hd)
    {
        using var k = Device();
        var gpuCalc = Calc(anti, 1, hd, k);
        var gpu = Blocks(Run(gpuCalc));
        Assert.True(gpuCalc.LastCalculateUsedGpu, $"GPU sampling did not run: {gpuCalc.LastGpuRoute.Detail}");
        Assert.Equal(GpuRouteState.Gpu, gpuCalc.LastGpuRoute.State);

        var cpu1 = Blocks(RunDouble(Calc(anti, 1, hd, null)));
        var cpu2 = Blocks(RunDouble(Calc(anti, 2, hd, null)));
        var shifted = Blocks(RunDouble(Calc(anti, 1, hd, null, centerY: 0.3)));

        double noise = Distance(cpu1, cpu2), parity = Distance(gpu, cpu1), contrast = Distance(shifted, cpu1);
        TestContext.Current.TestOutputHelper?.WriteLine($"anti={anti} hd={hd}: GPU vs CPU {parity:F4}, noise {noise:F4}, contrast {contrast:F4}");
        Assert.True(parity < 1.4 * noise + 0.002,
            $"GPU vs CPU {parity:F4}, CPU seed vs seed {noise:F4} (contrast {contrast:F4})");
        Assert.True(contrast > 4 * parity, $"a shifted view differs by only {contrast:F4} (GPU vs CPU {parity:F4})");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gpu_Uniform_Histogram_Is_Symmetric_About_The_Real_Axis(bool anti)
    {
        // Standard splat: no mirror duplication, so symmetry comes only from
        // sampling c over the symmetric domain through the conjugation-symmetric map.
        using var k = Device();
        var g = Run(Calc(anti, 3, hd: false, k));
        double asym = Distance(Blocks(g), Blocks(g, flipY: true));
        double noise = Distance(Blocks(RunDouble(Calc(anti, 1, false, null))), Blocks(RunDouble(Calc(anti, 2, false, null))));
        TestContext.Current.TestOutputHelper?.WriteLine($"anti={anti}: asymmetry {asym:F4}, noise {noise:F4}");
        Assert.True(asym < 1.4 * noise + 0.002, $"GPU histogram vs its mirror {asym:F4}, CPU seed noise {noise:F4}");
    }

    [Fact]
    public void Gpu_Sampling_Is_Deterministic_Per_Seed()
    {
        using var k = Device();
        var a = Run(Calc(false, 7, false, k));
        var b = Run(Calc(false, 7, false, k));
        var c = Run(Calc(false, 8, false, k));
        Assert.Equal(a.R, b.R); Assert.Equal(a.G, b.G); Assert.Equal(a.B, b.B);
        Assert.NotEqual(a.R, c.R);
    }

    [Fact]
    public void Progressive_Batches_All_Sample_On_The_Gpu()
    {
        using var k = Device();
        var c = Calc(false, 1, false, k);
        c.FractalParameters.BuddhaProgressive = true;
        c.ProgressiveBatchesOverride = 4;
        int callbacks = 0;
        c.OnBatchComposited = (_, _) => callbacks++;
        Run(c);
        Assert.True(c.LastCalculateUsedGpu);
        Assert.Equal(4, callbacks);
        // #1218 — the CPU's progressive render is the same image.
        var cpu = Calc(false, 1, false, null);
        cpu.FractalParameters.BuddhaProgressive = true;
        cpu.ProgressiveBatchesOverride = 4;
        Run(cpu);
        Assert.True(Mismatch(c.ColorBuffer, cpu.ColorBuffer) <= c.ColorBuffer.Length / 1000,
            $"progressive GPU vs CPU: {Mismatch(c.ColorBuffer, cpu.ColorBuffer)} px differ");
    }

    private static int Mismatch(uint[] a, uint[] b) => a.Zip(b).Count(p => p.First != p.Second);

    // #1218 — the point of the shared sampler: switching GPU compute does not change
    // the picture. Only the GPU's approximate division may move a point one pixel or
    // a sample one band, so a handful of pixels may differ.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Gpu_And_Cpu_Render_The_Same_Image(bool anti, bool hd)
    {
        using var k = Device();
        var g = Calc(anti, 4, hd, k);
        var gh = Run(g);
        var c = Calc(anti, 4, hd, null);
        var ch = Run(c);
        Assert.True(g.LastCalculateUsedGpu && !c.LastCalculateUsedGpu);
        int n = W * H;
        int px = Mismatch(g.ColorBuffer, c.ColorBuffer);
        TestContext.Current.TestOutputHelper?.WriteLine($"anti={anti} hd={hd}: {px} of {n} px differ");
        Assert.True(px <= n / 1000, $"GPU vs CPU image: {px} of {n} pixels differ");
        foreach (var (a, b) in new[] { (gh.R, ch.R), (gh.G, ch.G), (gh.B, ch.B) })
        {
            long ta = a.Sum(v => (long)v), tb = b.Sum(v => (long)v);
            Assert.True(Math.Abs(ta - tb) <= Math.Max(4, tb / 2000), $"band totals GPU {ta} vs CPU {tb}");
        }
    }

    // #1218 — the CPU splits a batch across threads; every sample owns its stream, so
    // the split is invisible: the frame equals one sequential run of the sampler.
    [Fact]
    public void Cpu_Parallel_Split_Equals_A_Single_Threaded_Run()
    {
        var c = Calc(false, 5, true, null);
        c.FractalParameters.BuddhaSamples = 100_000;
        c.FractalParameters.BuddhaMinIter = 0;   // most samples drawn: a chunk-boundary slip shows
        var h = Run(c);
        int n = W * H;
        var s = (R: new uint[n], G: new uint[n], B: new uint[n]);
        BuddhaUniformSampler.Run(new GpuBuddhaBatch(W, H, 3.5 / Math.Max(W, H), -0.5, 0, Iter, false, true, 20, 200,
            5, 0, 100_000, c.FractalParameters.BuddhaMinIter), s.R, s.G, s.B);
        Assert.Equal(s.R, h.R); Assert.Equal(s.G, h.G); Assert.Equal(s.B, h.B);
    }

    [Fact]
    public void Cpu_Only_Cases_Stay_On_The_Cpu_With_A_Reason()
    {
        using var k = Device();
        bool saved = BuddhaFamilyCalculator.UseGpuBuddha;
        try
        {
            (string Reason, bool Gpu) Route(BuddhaFamilyCalculator c)
            {
                c.FractalParameters.BuddhaSamples = 20_000;
                c.Calculate();
                return (c.LastGpuRoute.Reason ?? "", c.LastCalculateUsedGpu);
            }

            var mh = Calc(false, 1, false, k); mh.FractalParameters.BuddhaMetropolis = true;
            Assert.Equal(("Metropolis sampling", false), Route(mh));

            // #1224 — zoom compensation keeps uniform sampling (so the GPU) up to
            // zoom 100; past it, it turns Metropolis on.
            var zoomUniform = Calc(false, 1, false, k); zoomUniform.Zoom = 3;
            Assert.True(Route(zoomUniform).Gpu);
            var zoomComp = Calc(false, 1, false, k); zoomComp.Zoom = 150;
            Assert.Equal(("Metropolis sampling", false), Route(zoomComp));

            var deep = Calc(false, 1, false, k); deep.Zoom = 200; deep.FractalParameters.BuddhaZoomCompensation = false;
            Assert.Equal(($"zoom > {BuddhaFamilyCalculator.MaxGpuBuddhaZoom:0}", false), Route(deep));

            var dual = new DualBuddhabrotCalculator(W, H) { MaxIterations = Iter, GpuKernel = k, UseGpuCompute = true };
            dual.FractalParameters = Params(1, false);
            Assert.Equal(("CPU-only sampler", false), Route(dual));

            var noKernel = Calc(false, 1, false, null); noKernel.UseGpuCompute = true;
            Assert.Equal(("no GPU kernel", false), Route(noKernel));

            BuddhaFamilyCalculator.UseGpuBuddha = false;
            Assert.Equal(("CPU faster", false), Route(Calc(false, 1, false, k)));
            BuddhaFamilyCalculator.UseGpuBuddha = true;

            var off = Calc(false, 1, false, null);
            off.Calculate();
            Assert.Equal(GpuRoute.NotRequested, off.LastGpuRoute);
        }
        finally { BuddhaFamilyCalculator.UseGpuBuddha = saved; }
    }

    private sealed class ThrowingKernel : IGpuKernel
    {
        public bool HasGpuPalette => false;
        public double LastDispatchMs => 0;
        public double LastReadbackMs => 0;
        public string BackendLabel => "Stub";
        public bool SupportsBuddhabrot => true;
        public void SetPalette(FracturingFog.Interefaces.IGpuHlslPalette? palette) { }
        public void Run(int width, int height, double centerX, double centerY, double scale, int maxIter,
            double bailout2, int[] iterDst, float[] smoothDst, float[] finalZrDst, float[] finalZiDst,
            float[] finalDrDst, float[] finalDiDst, int[]? perRowMaxIter = null,
            FractalKind kind = FractalKind.Mandelbrot, float param0 = 0f, float param1 = 0f, uint[]? colorDst = null,
            float[]? trapDst = null, GpuDomainWarp warp = default) => throw new NotSupportedException();
        // The backends add into the arrays only after a successful readback.
        public void RunBuddhaBatch(in GpuBuddhaBatch batch, uint[] hitsR, uint[] hitsG, uint[] hitsB)
            => throw new InvalidOperationException("stub dispatch failure");
        public void Dispose() { }
    }

    [Fact]
    public void A_Failing_Kernel_Falls_Back_To_The_Exact_Cpu_Frame()
    {
        BuddhaFamilyCalculator.UseGpuBuddha = true;
        var cpu = Calc(false, 5, false, null);
        cpu.FractalParameters.BuddhaSamples = 50_000;
        cpu.Calculate();

        var g = Calc(false, 5, false, new ThrowingKernel());
        g.FractalParameters.BuddhaSamples = 50_000;
        g.Calculate();
        Assert.False(g.LastCalculateUsedGpu);
        Assert.Equal("GPU error", g.LastGpuRoute.Reason);
        Assert.Equal(cpu.ColorBuffer, g.ColorBuffer);
    }

    // The last case: short in-set orbits with bands across the in-set class range,
    // so a slip in the cycle-repeat arithmetic moves samples between bands.
    [Theory]
    [InlineData(false, false, Iter, 20, 200)]
    [InlineData(false, true, Iter, 20, 200)]
    [InlineData(true, false, Iter, 20, 200)]
    [InlineData(true, true, Iter, 20, 200)]
    [InlineData(true, false, 300, 60, 120)]
    public void Kernel_Matches_A_Strict_Float_Replay_Of_Its_Algorithm(bool anti, bool hd, int maxOrbit, int low, int mid)
    {
        using var k = Device();
        const int samples = 200_000;
        int n = W * H;
        var batch = new GpuBuddhaBatch(W, H, 3.5 / Math.Max(W, H), -0.5, 0, maxOrbit, anti, hd, low, mid, 9, 0, samples);
        var g = (R: new uint[n], G: new uint[n], B: new uint[n]);
        k.RunBuddhaBatch(batch, g.R, g.G, g.B);
        var e = (R: new uint[n], G: new uint[n], B: new uint[n]);
        BuddhaUniformSampler.Run(batch, e.R, e.G, e.B);
        // The GPU divides approximately (projection, in-set mean), so a point may land
        // one pixel over or a sample one band over; everything else is bit-identical.
        foreach (var (gb, eb, name) in new[] { (g.R, e.R, "low"), (g.G, e.G, "mid"), (g.B, e.B, "high") })
        {
            long gt = gb.Sum(v => (long)v), et = eb.Sum(v => (long)v);
            int mismatch = Enumerable.Range(0, n).Count(i => gb[i] != eb[i]);
            TestContext.Current.TestOutputHelper?.WriteLine($"anti={anti} hd={hd} maxOrbit={maxOrbit} {name}: gpu {gt} replay {et}, {mismatch} px differ");
            Assert.True(et > 0, $"{name} band empty");
            // In-set orbits stay inside the view, so no point can drift across its
            // edge: the totals are exact there.
            if (anti) Assert.True(gt == et, $"{name} band: GPU {gt} hits, replay {et}");
            else Assert.True(Math.Abs(gt - et) <= Math.Max(4, et / 2000), $"{name} band: GPU {gt} hits, replay {et}");
            Assert.True(mismatch <= n / 100, $"{name} band: {mismatch} of {n} pixels differ from the replay");
        }
    }

    [Fact]
    public void In_Set_Orbits_Land_Every_Point_When_The_View_Holds_The_Disc()
    {
        // Zoomed out so the view holds |z| <= 2 whole: a kept in-set orbit splats all
        // its points but z0 (#1218), maxOrbit - 1 of them (cycle repeats included),
        // and nothing else does, so every band total is a multiple of maxOrbit - 1 —
        // whatever the RNG drew.
        using var k = Device();
        int n = W * H;
        var g = (R: new uint[n], G: new uint[n], B: new uint[n]);
        k.RunBuddhaBatch(new GpuBuddhaBatch(W, H, 6.0 / H, 0, 0, Iter, true, false, 20, 200, 4, 0, 100_000), g.R, g.G, g.B);
        long total = 0;
        foreach (var (h, name) in new[] { (g.R, "low"), (g.G, "mid"), (g.B, "high") })
        {
            long t = h.Sum(v => (long)v);
            Assert.True(t % (Iter - 1) == 0, $"{name} band: {t} hits is not a whole number of {Iter - 1}-point orbits");
            total += t;
        }
        Assert.True(total / (Iter - 1) > 10_000, $"only {total / (Iter - 1)} in-set orbits kept");
    }

    [Fact]
    public void Progressive_Batches_Draw_Fresh_Samples()
    {
        // Batch b's samples come from the (seed, b) stream: a two-batch render is the
        // first batch plus a different second one, never the first one twice.
        using var k = Device();
        var two = Calc(false, 6, false, k);
        two.FractalParameters.BuddhaSamples = 100_000;
        two.FractalParameters.BuddhaProgressive = true;
        two.ProgressiveBatchesOverride = 2;
        var p = Run(two);
        var one = Calc(false, 6, false, k);
        one.FractalParameters.BuddhaSamples = 50_000;
        var f = Run(one);
        Assert.True(two.LastCalculateUsedGpu && one.LastCalculateUsedGpu);
        bool doubled = Enumerable.Range(0, W * H).All(i => p.R[i] == 2 * f.R[i] && p.G[i] == 2 * f.G[i] && p.B[i] == 2 * f.B[i]);
        Assert.False(doubled, "the second batch replayed the first batch's samples");
        Assert.True(Enumerable.Range(0, W * H).All(i => p.R[i] >= f.R[i]), "the first batch is not part of the two-batch render");
    }
}
