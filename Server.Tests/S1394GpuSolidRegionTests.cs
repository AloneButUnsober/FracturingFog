// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1394 part 1 — solid-region skipping on the GPU, the parts that need no GPU:
//   • GpuPixelMask: the bits a byte mask gives, and the masked copies the backends
//     read back with (every marked element copied, every other one untouched);
//   • MandelbrotKernelSource.PerturbBands: without a mask the bands are the uniform
//     ones; with a mask they still tile the frame in whole groups, no band iterates
//     more than the budget (its marked pixels only) unless it is one group, and a
//     sparse mask needs fewer bands;
//   • the calculators' wiring, through a software kernel that honours the mask the
//     way the GPU kernels do (computes and writes only the marked pixels): each pass
//     hands the kernel its mask, every pixel is computed exactly once or filled, the
//     frame equals the full GPU frame (the invariants of S1343 / S1394 part 2), the
//     kernel's mask is cleared afterwards; Auto computes a shallow GPU frame with the
//     periodicity check on whole (no probe pass, no mask), and with the check off
//     decides at the GPU threshold on the share of the full frame's grid rows; a
//     kernel without mask support and the #1344 anti-aliasing mask run as before.
// The real kernels: S1394GpuPixelMaskTests (Vulkan), --gpupixelmaskprobe (D3D11).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1394GpuSolidRegionTests(ITestOutputHelper output)
{
    private const int W = 320, H = 200;

    // ── the mask ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1000, 0.3)]
    [InlineData(1024, 0.0)]
    [InlineData(1024, 1.0)]
    [InlineData(37, 0.5)]
    public void A_Mask_Marks_Exactly_The_Nonzero_Bytes(int n, double share)
    {
        var rng = new Random(n);
        var mark = new byte[n];
        for (int i = 0; i < n; i++) mark[i] = (byte)(rng.NextDouble() < share ? 1 + rng.Next(255) : 0);
        var m = GpuPixelMask.FromBytes(mark, n, 1);
        Assert.Equal(1u, m.Words[0]);   // the header: a mask is active
        Assert.Equal(GpuPixelMask.WordCount(n), m.Words.Length);
        Assert.Equal(mark.Count(b => b != 0), m.Count);
        for (int i = 0; i < n; i++) Assert.Equal(mark[i] != 0, m.Marked(i));
        // No bit past the last pixel.
        if (n % 32 != 0) Assert.Equal(0u, m.Words[^1] >> (n % 32));
        Assert.True(m.Fits(n, 1));
        Assert.False(m.Fits(n, 2));
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(96)]
    [InlineData(31)]
    public void The_Masked_Copies_Write_The_Marked_Elements_Only(int n)
    {
        var rng = new Random(n);
        var mark = new byte[n];
        // Whole marked words, empty words and mixed ones.
        for (int i = 0; i < n; i++) mark[i] = (byte)((i / 32) % 3 == 0 ? 1 : (i / 32) % 3 == 1 ? 0 : rng.Next(2));
        var m = GpuPixelMask.FromBytes(mark, n, 1);
        var src = Enumerable.Range(0, n * 4).Select(i => (float)i + 0.5f).ToArray();
        var dst = Enumerable.Repeat(-1f, n).ToArray();
        m.CopyMarked<float>(src, dst);
        for (int i = 0; i < n; i++) Assert.Equal(mark[i] != 0 ? src[i] : -1f, dst[i]);

        var zr = Enumerable.Repeat(-1f, n).ToArray(); var zi = (float[])zr.Clone();
        var dr = (float[])zr.Clone(); var di = (float[])zr.Clone();
        m.ScatterMarkedFinalZD(src, zr, zi, dr, di, n);
        for (int i = 0; i < n; i++)
        {
            bool on = mark[i] != 0;
            Assert.Equal(on ? src[4 * i] : -1f, zr[i]); Assert.Equal(on ? src[4 * i + 1] : -1f, zi[i]);
            Assert.Equal(on ? src[4 * i + 2] : -1f, dr[i]); Assert.Equal(on ? src[4 * i + 3] : -1f, di[i]);
        }
    }

    // ── the perturbation bands ───────────────────────────────────────────────

    [Theory]
    [InlineData(960, 540, 50000)]
    [InlineData(960, 540, 2000)]
    [InlineData(321, 203, 8000)]
    [InlineData(1920, 1080, 100)]
    public void Unmasked_Bands_Are_The_Uniform_Ones(int w, int h, int maxIt)
    {
        int rows = MandelbrotKernelSource.PerturbBandRows(w, h, maxIt);
        var bands = MandelbrotKernelSource.PerturbBands(w, h, maxIt);
        Assert.Equal((h + rows - 1) / rows, bands.Length);
        for (int b = 0; b < bands.Length; b++)
            Assert.Equal((b * rows, Math.Min(rows, h - b * rows)), bands[b]);
    }

    [Theory]
    [InlineData(960, 540, 50000, "grid")]
    [InlineData(960, 540, 50000, "scatter")]
    [InlineData(960, 540, 2000, "grid")]
    [InlineData(321, 203, 8000, "scatter")]
    [InlineData(960, 540, 50000, "dense")]
    public void Masked_Bands_Tile_The_Frame_Within_The_Budget_Of_Their_Marked_Pixels(int w, int h, int maxIt, string kind)
    {
        int n = w * h;
        var mark = new byte[n];
        var rng = new Random(w + h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                mark[y * w + x] = (byte)(kind switch
                {
                    "grid" => y % 64 == 0 || x % 64 == 0 || y == h - 1 || x == w - 1,
                    "scatter" => rng.NextDouble() < 0.02,
                    _ => true,
                } ? 1 : 0);
        var m = GpuPixelMask.FromBytes(mark, w, h);
        var bands = MandelbrotKernelSource.PerturbBands(w, h, maxIt, MandelbrotKernelSource.PerturbGroupRows, m.Words);
        var uniform = MandelbrotKernelSource.PerturbBands(w, h, maxIt);
        // The budget: the larger of the fixed one and an unmasked band of this frame.
        long budget = Math.Max(MandelbrotKernelSource.PerturbDispatchIterBudget,
            (long)MandelbrotKernelSource.PerturbBandRows(w, h, maxIt) * w * maxIter(maxIt));
        int next = 0;
        foreach (var (rowBase, rows) in bands)
        {
            Assert.Equal(next, rowBase);
            Assert.True(rows > 0);
            if (rowBase + rows < h) Assert.Equal(0, rows % MandelbrotKernelSource.PerturbGroupRows);
            long marked = 0;
            for (int i = rowBase * w; i < (rowBase + rows) * w; i++) marked += mark[i];
            if (rows > MandelbrotKernelSource.PerturbGroupRows)
                Assert.True(marked * maxIt <= budget, $"band at {rowBase} ({rows} rows): {marked} marked x {maxIt} > {budget}");
            // As tall as the budget allows: one more group would overrun it.
            int end = rowBase + rows;
            if (end < h)
            {
                long more = 0;
                for (int i = end * w; i < Math.Min(h, end + MandelbrotKernelSource.PerturbGroupRows) * w; i++) more += mark[i];
                Assert.True((marked + more) * maxIt > budget, $"band at {rowBase} ({rows} rows) stopped short: {marked} + {more} marked fit");
            }
            next = rowBase + rows;
        }
        Assert.Equal(h, next);
        Assert.True(bands.Length <= uniform.Length, $"{bands.Length} masked bands > {uniform.Length} unmasked");
        if (kind != "dense" && uniform.Length > 4)
            Assert.True(bands.Length * 2 <= uniform.Length, $"a sparse mask kept {bands.Length} of {uniform.Length} bands");
        output.WriteLine($"{w}x{h} {maxIt} {kind}: {bands.Length} bands (unmasked {uniform.Length})");

        static long maxIter(int it) => Math.Max(1, it);
    }

    // ── the calculators, through a software kernel that honours the mask ─────

    /// <summary>A CPU stand-in for the shallow GPU kernel: z² + c (Mandelbrot) or the
    /// Julia map in double, writing iter / smooth / final z / dz like the kernel, and,
    /// with <see cref="HonourMask"/>, only the marked pixels of a fitting mask.</summary>
    private sealed class SoftMaskKernel : IGpuKernel
    {
        public bool HonourMask = true;
        public readonly List<long> MaskCounts = new();   // marked pixels of each masked dispatch
        public int Dispatches;
        public bool MaskSetDuringDispatch;

        public bool SupportsPixelMask => HonourMask;
        public GpuPixelMask? PixelMask { get; set; }
        public bool HasGpuPalette => false;
        public double LastDispatchMs => 0;
        public double LastReadbackMs => 0;
        public void SetPalette(IGpuHlslPalette? palette) { }
        public void Dispose() { }

        public void Run(int width, int height, double centerX, double centerY, double scale, int maxIter, double bailout2,
            int[] iterDst, float[] smoothDst, float[] finalZrDst, float[] finalZiDst, float[] finalDrDst, float[] finalDiDst,
            int[]? perRowMaxIter = null, FractalKind kind = FractalKind.Mandelbrot, float param0 = 0f, float param1 = 0f,
            uint[]? colorDst = null, float[]? trapDst = null, GpuDomainWarp warp = default)
        {
            Dispatches++;
            if (PixelMask != null) MaskSetDuringDispatch = true;
            var mask = HonourMask && PixelMask is { } m && m.Fits(width, height) ? m : null;
            if (mask != null) MaskCounts.Add(mask.Count);
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;
                    if (mask != null && !mask.Marked(i)) continue;
                    double px = centerX + (x - 0.5 * width) * scale, py = centerY + (y - 0.5 * height) * scale;
                    bool julia = kind == FractalKind.Julia;
                    double zr = julia ? px : 0, zi = julia ? py : 0, cr = julia ? param0 : px, ci = julia ? param1 : py;
                    double dr = julia ? 1 : 0, di = 0;
                    int it = 0;
                    for (; it < maxIter && zr * zr + zi * zi < bailout2; it++)
                    {
                        (dr, di) = (2 * (zr * dr - zi * di) + (julia ? 0 : 1), 2 * (zr * di + zi * dr));
                        (zr, zi) = (zr * zr - zi * zi + cr, 2 * zr * zi + ci);
                    }
                    bool inSet = it >= maxIter;
                    iterDst[i] = inSet ? maxIter : it;
                    smoothDst[i] = inSet ? 0f : (float)(it + 1 - Math.Log2(Math.Log2(Math.Sqrt(zr * zr + zi * zi))));
                    finalZrDst[i] = inSet ? 0f : (float)zr; finalZiDst[i] = inSet ? 0f : (float)zi;
                    finalDrDst[i] = inSet ? 0f : (float)dr; finalDiDst[i] = inSet ? 0f : (float)di;
                }
        }
    }

    private static MandelbrotCalculator Mandel(IGpuKernel k, double x, double y, double zoom, int iter, bool skip, bool auto = false) => new(W, H)
    {
        CenterX = x, CenterY = y, Zoom = zoom, MaxIterations = iter, ColorMap = new HsvPalette(),
        UseGpuCompute = true, GpuKernel = k, SolidRegionSkip = skip, SolidRegionAuto = auto, CaptureFilledMask = true,
    };

    private static EscapeTimeCalculator Julia(IGpuKernel k, Complex c, double x, double y, double zoom, int iter, bool skip, bool auto = false) => new(W, H)
    {
        CenterX = x, CenterY = y, Zoom = zoom, MaxIterations = iter, ColorMap = new HsvPalette(),
        UseGpuCompute = true, GpuKernel = k, FractalType = FractalType.Julia, CaptureFilledMask = true,
        FractalParameters = new FractalParameters { JuliaC = c, SolidRegionSkip = skip, SolidRegionAuto = auto },
    };

    private static IEnumerable<(string Name, Array A)> Buffers(object calc) => calc switch
    {
        MandelbrotCalculator c => new (string, Array)[]
        {
            ("Color", c.ColorBuffer), ("Iteration", c.IterationBuffer), ("Smooth", c.SmoothBuffer), ("Distance", c.DistanceBuffer),
            ("NormalX", c.NormalXBuffer), ("NormalY", c.NormalYBuffer), ("FinalZr", c.FinalZrBuffer), ("FinalZi", c.FinalZiBuffer),
            ("FinalDr", c.FinalDrBuffer), ("FinalDi", c.FinalDiBuffer),
        },
        EscapeTimeCalculator c => new (string, Array)[]
        {
            ("Color", c.ColorBuffer), ("Iteration", c.IterationBuffer), ("Smooth", c.SmoothBuffer), ("Distance", c.DistanceBuffer),
            ("NormalX", c.NormalXBuffer), ("NormalY", c.NormalYBuffer), ("FinalZr", c.FinalZrBuffer), ("FinalZi", c.FinalZiBuffer),
            ("FinalDr", c.FinalDrBuffer), ("FinalDi", c.FinalDiBuffer),
        },
        _ => throw new ArgumentException("not a calculator", nameof(calc)),
    };

    private static int[] Iterations(object calc) => (int[])Buffers(calc).First(b => b.Name == "Iteration").A;

    private static string? PixelDiff(object a, object b, int i)
    {
        foreach (var ((name, x), (_, y)) in Buffers(a).Zip(Buffers(b)))
        {
            if (x.Length != y.Length) return $"{name} length {x.Length} vs {y.Length}";
            if (x.Length <= i) continue;
            if (!Equals(x.GetValue(i), y.GetValue(i))) return $"{name} {x.GetValue(i)} vs {y.GetValue(i)}";
        }
        return null;
    }

    // (filled, wrong = filled but escaping in the full frame); everything else exact.
    private static (long Filled, long Wrong) Compare(object off, object on, byte[] fm, long reportedFilled, int maxIt)
    {
        var offIter = Iterations(off);
        long filled = 0, wrong = 0;
        for (int i = 0; i < W * H; i++)
        {
            bool offIn = offIter[i] >= maxIt;
            if (fm[i] == 0)
            {
                Assert.True(PixelDiff(off, on, i) is null, $"computed pixel {i} differs from the full GPU frame: {PixelDiff(off, on, i)}");
                continue;
            }
            filled++;
            if (offIn) Assert.True(PixelDiff(off, on, i) is null, $"filled in-set pixel {i} differs: {PixelDiff(off, on, i)}");
            else wrong++;
        }
        Assert.Equal(filled, reportedFilled);
        return (filled, wrong);
    }

    [Theory]
    [InlineData("period-3 minibrot", -1.7548776662466927, 0.0, 30.0, 1000)]
    [InlineData("whole set", -0.75, 0.0, 1.0, 1000)]
    [InlineData("minibrot interior", -1.7548776662466927, 0.0, 3e3, 2000)]
    public void A_Mandelbrot_Gpu_Frame_Takes_Each_Pass_Mask_And_Equals_The_Full_Gpu_Frame(string name, double x, double y, double zoom, int iter)
    {
        var kOff = new SoftMaskKernel();
        var off = Mandel(kOff, x, y, zoom, iter, false);
        off.Calculate(CancellationToken.None);
        var k = new SoftMaskKernel();
        var on = Mandel(k, x, y, zoom, iter, true);
        on.Calculate(CancellationToken.None);

        Assert.True(on.LastFrameUsedGpuCompute, "precondition: the frame ran on the (soft) GPU");
        Assert.Empty(kOff.MaskCounts);   // skip off: no mask
        // Every pass went to the kernel masked (it used to finish the frame in pass 1, the
        // whole frame, so nothing was filled).
        Assert.Equal(on.LastFrameSolidPasses, k.MaskCounts.Count);
        Assert.All(k.MaskCounts, c => Assert.True(c < W * H));
        Assert.Equal(k.Dispatches, k.MaskCounts.Count);
        var (filled, wrong) = Compare(off, on, on.LastFilledMask!, on.LastFrameFilledPixels, iter);
        // Each pixel computed exactly once, or filled.
        Assert.Equal((long)W * H, k.MaskCounts.Sum() + filled);
        Assert.Null(k.PixelMask);   // cleared after the frame
        output.WriteLine($"{name}: {on.LastFrameSolidPasses} passes, masks {string.Join("/", k.MaskCounts)}, filled {filled}, wrong {wrong}");
        Assert.True(filled > 0, "precondition: the skip fills");
        Assert.True(wrong <= W * H / 2000, $"{wrong} filled pixels escape in the full frame");
    }

    [Theory]
    [InlineData(-0.123, 0.745, -0.0884, 0.4, 20.0, 2000)]   // rabbit, zoomed into its inside
    [InlineData(-1.0, 0.0, 0.0, 0.0, 1.0, 1000)]            // basilica
    public void A_Julia_Gpu_Frame_Takes_Each_Pass_Mask_And_Equals_The_Full_Gpu_Frame(double cr, double ci, double x, double y, double zoom, int iter)
    {
        var off = Julia(new SoftMaskKernel(), new(cr, ci), x, y, zoom, iter, false);
        off.Calculate(CancellationToken.None);
        var k = new SoftMaskKernel();
        var on = Julia(k, new(cr, ci), x, y, zoom, iter, true);
        on.Calculate(CancellationToken.None);
        Assert.Equal(on.LastFrameSolidPasses, k.MaskCounts.Count);
        Assert.All(k.MaskCounts, c => Assert.True(c < W * H));
        var (filled, wrong) = Compare(off, on, on.LastFilledMask!, on.LastFrameFilledPixels, iter);
        Assert.Equal((long)W * H, k.MaskCounts.Sum() + filled);
        Assert.Null(k.PixelMask);
        Assert.True(filled > 0, "precondition: the skip fills");
        Assert.True(wrong <= W * H / 2000, $"{wrong} filled pixels escape in the full frame");
    }

    private static void AssertFrameEqual(object expected, object actual, string what)
    {
        for (int i = 0; i < W * H; i++)
            if (PixelDiff(expected, actual, i) is { } d) Assert.Fail($"{what}: pixel {i} differs: {d}");
    }

    [Fact]
    public void Auto_Computes_A_Shallow_Gpu_Frame_With_Periodicity_Whole_Without_A_Probe()
    {
        Assert.Equal(GpuPeriodicity.On, GpuPeriodicity.Mode);   // the shipped default
        var off = Mandel(new SoftMaskKernel(), -1.7548776662466927, 0, 3e3, 2000, false);
        off.Calculate(CancellationToken.None);
        var k = new SoftMaskKernel();
        var on = Mandel(k, -1.7548776662466927, 0, 3e3, 2000, true, auto: true);
        on.Calculate(CancellationToken.None);
        Assert.True(on.LastFrameSolidDeclined);
        Assert.Equal(1, on.LastFrameSolidPasses);
        Assert.True(double.IsNaN(on.LastFrameSolidCostlyShare), "no probe pass measured a share");
        Assert.False(k.MaskSetDuringDispatch, "the whole frame went to the kernel unmasked");
        Assert.Equal(0, on.LastFrameFilledPixels);
        AssertFrameEqual(off, on, "Auto, shallow GPU frame");

        var j = Julia(new SoftMaskKernel(), new(-0.123, 0.745), -0.0884, 0.4, 20, 2000, true, auto: true);
        j.Calculate(CancellationToken.None);
        Assert.True(j.LastFrameSolidDeclined);
        Assert.Equal(1, j.LastFrameSolidPasses);
    }

    // From the full frame: the share of the grid rows' pixels in runs of ≥ 2·MinTile
    // in-set pixels (Julia has no free in-set test).
    private static double ExpectedShare(EscapeTimeCalculator off)
    {
        var rows = new List<int>();
        for (int v = 0; v < H - 1; v += MandelbrotCalculator.SolidTileSize) rows.Add(v);
        rows.Add(H - 1);
        long costly = 0;
        foreach (int y in rows)
        {
            int run = 0;
            for (int x = 0; x <= W; x++)
            {
                if (x < W && off.IterationBuffer[y * W + x] >= off.MaxIterations) { run++; continue; }
                if (run >= 2 * MandelbrotCalculator.SolidMinTile) costly += run;
                run = 0;
            }
        }
        return costly / (double)(rows.Count * W);
    }

    [Theory]
    [InlineData(-0.123, 0.745, 1.0)]      // rabbit: 5.0 %, between the CPU and the GPU threshold
    [InlineData(-0.123, 0.745, 1.5)]      // rabbit, closer: 23 %, above both
    [InlineData(-0.75, 0.0, 1.0)]         // San Marco: 14.9 %
    [InlineData(-0.7, 0.27015, 1.0)]      // the default constant: a thin inside, 0 %
    public void Auto_With_The_Periodicity_Check_Off_Decides_At_The_Gpu_Threshold(double cr, double ci, double zoom)
    {
        GpuPeriodicity.Mode = GpuPeriodicity.Off;
        try
        {
            var off = Julia(new SoftMaskKernel(), new(cr, ci), 0, 0, zoom, 1000, false);
            off.Calculate(CancellationToken.None);
            var k = new SoftMaskKernel();
            var on = Julia(k, new(cr, ci), 0, 0, zoom, 1000, true, auto: true);
            on.Calculate(CancellationToken.None);
            double expected = ExpectedShare(off);
            output.WriteLine($"c = {cr} + {ci}i at {zoom}: share {expected:P2} (measured {on.LastFrameSolidCostlyShare:P2}), declined {on.LastFrameSolidDeclined}");
            Assert.Equal(expected, on.LastFrameSolidCostlyShare, 12);
            Assert.Equal(expected < MandelbrotCalculator.SolidAutoMinCostlyShareGpu, on.LastFrameSolidDeclined);
            // The probe was the whole grid, in one masked pass (#1394: passes are dear here).
            Assert.True(k.MaskCounts.Count >= 1);
            int lines = 0;
            for (int i = 0; i < W * H; i++)
            {
                int x = i % W, y = i / W;
                if (x % MandelbrotCalculator.SolidTileSize == 0 || y % MandelbrotCalculator.SolidTileSize == 0 || x == W - 1 || y == H - 1) lines++;
            }
            Assert.Equal(lines, k.MaskCounts[0]);
            if (on.LastFrameSolidDeclined) AssertFrameEqual(off, on, "declined frame");
            else Compare(off, on, on.LastFilledMask!, on.LastFrameFilledPixels, 1000);
            if (cr == -0.123 && zoom == 1.0)   // precondition: the CPU threshold would subdivide it
                Assert.InRange(expected, MandelbrotCalculator.SolidAutoMinCostlyShare, MandelbrotCalculator.SolidAutoMinCostlyShareGpu);
        }
        finally { GpuPeriodicity.Reset(); }
    }

    [Fact]
    public void A_Kernel_Without_Mask_Support_Finishes_The_Frame_In_The_First_Pass()
    {
        var off = Mandel(new SoftMaskKernel { HonourMask = false }, -1.7548776662466927, 0, 30, 1000, false);
        off.Calculate(CancellationToken.None);
        var k = new SoftMaskKernel { HonourMask = false };
        var on = Mandel(k, -1.7548776662466927, 0, 30, 1000, true);
        on.Calculate(CancellationToken.None);
        Assert.Equal(1, on.LastFrameSolidPasses);
        Assert.Equal(1, k.Dispatches);
        Assert.False(k.MaskSetDuringDispatch);
        Assert.Equal(0, on.LastFrameFilledPixels);
        AssertFrameEqual(off, on, "GPU without mask support");
    }

    [Fact]
    public void The_Anti_Aliasing_Mask_Leaves_The_Gpu_Computing_And_Colouring_Every_Pixel()
    {
        // #1344 — a PixelMask alone never reaches the kernel: the GPU computes every pixel
        // and the writeback colours them all, as before #1394.
        var off = Mandel(new SoftMaskKernel(), -0.7436, 0.1318, 60, 1000, false);
        off.Calculate(CancellationToken.None);
        var k = new SoftMaskKernel();
        var on = Mandel(k, -0.7436, 0.1318, 60, 1000, false);
        var aa = new byte[W * H];
        for (int i = 0; i < aa.Length; i += 3) aa[i] = 1;
        on.PixelMask = aa;
        on.Calculate(CancellationToken.None);
        Assert.False(k.MaskSetDuringDispatch);
        AssertFrameEqual(off, on, "GPU frame under a #1344 mask");
    }
}
