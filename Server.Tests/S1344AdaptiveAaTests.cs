// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1344 — adaptive anti-aliasing (Engine/Rendering/Supersampling.cs) and the
// MandelbrotCalculator.PixelMask it rides on.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // flips the PT path statics
public sealed class S1344PixelMaskTests : IDisposable
{
    private readonly bool _scalar = MandelbrotCalculator.ForceScalarPtPath;
    private readonly bool _rebase = MandelbrotCalculator.AllowPtRebasing;
    private readonly bool _simdRebase = MandelbrotCalculator.UseSimdRebasing;

    public void Dispose()
    {
        MandelbrotCalculator.ForceScalarPtPath = _scalar;
        MandelbrotCalculator.AllowPtRebasing = _rebase;
        MandelbrotCalculator.UseSimdRebasing = _simdRebase;
    }

    private const int W = 101, H = 53;   // odd: SIMD tails in every row

    public sealed record View(string Name, double Cx, double Cy, double Zoom, int MaxIter, QualityPreset Quality,
        Func<IColorMap> Theme, bool Scalar = false, bool Rebase = true, bool SimdRebase = true,
        bool NoBla = false, bool NoSa = false, bool Lattice = false, double[]? XLimbs = null, double[]? YLimbs = null)
    {
        public override string ToString() => Name;
    }

    public static TheoryData<View> Views() =>
    [
        new View("SP vector theme", -0.7436, 0.1318, 300, 600, QualityPreset.Standard, () => new HsvPalette()),
        new View("SP scalar theme", -0.7436, 0.1318, 300, 600, QualityPreset.Standard, () => new CyclePeriodMap()),
        new View("SP orbit trap", -0.7436, 0.1318, 300, 600, QualityPreset.Standard, () => new OrbitTrapPointMap()),
        new View("DD perturbation SIMD", -0.743643887037151, 0.13182590420533, 1e14, 3000, QualityPreset.High, () => new HsvPalette()),
        new View("DD perturbation scalar", -0.743643887037151, 0.13182590420533, 1e14, 3000, QualityPreset.High, () => new HsvPalette(), Scalar: true),
        new View("DD no rebasing", -0.743643887037151, 0.13182590420533, 1e14, 3000, QualityPreset.High, () => new HsvPalette(), Rebase: false),
        new View("DD scalar rebasing", -0.743643887037151, 0.13182590420533, 1e14, 3000, QualityPreset.High, () => new HsvPalette(), SimdRebase: false),
        new View("DD no BLA / SA", -0.743643887037151, 0.13182590420533, 1e14, 3000, QualityPreset.High, () => new HsvPalette(), NoBla: true, NoSa: true),
        new View("DD lattice", -0.743643887037151, 0.13182590420533, 1e14, 3000, QualityPreset.High, () => new HsvPalette(), Lattice: true),
        new View("DD orbit trap", -0.743643887037151, 0.13182590420533, 1e14, 3000, QualityPreset.High, () => new OrbitTrapPointMap()),
        // "Deep Lightning in Space" (S1267) and S1319's period-1808 minibrot (OD tier, framed).
        new View("QD", -1.4181949444785762, -0.12700786443815276, 7.58348E+26, 20000, QualityPreset.Extreme, () => new HsvPalette(),
            XLimbs: [-7.4415882477902279E-17], YLimbs: [-2.3429499355532375E-18]),
        OdMinibrot(),
    ];

    private static View OdMinibrot()
    {
        var nucleus = new FracturingFog.FFMath.DeepComplex(
            new FracturingFog.FFMath.OD(-1.9918151296901943, -7.82198368112659E-17, -5.6237669196087764E-33, 2.6876672348091084E-49,
                   8.344815422720153E-68, 5.62021278812849E-84, 3.758605147229201E-100, 3.74170214755604E-117),
            new FracturingFog.FFMath.OD(-5.524041575397243E-06, -2.9567164202839686E-22, -2.2585741366702907E-38, 6.439806134954378E-55,
                   1.8890258519870084E-71, 1.8268317099722925E-87, -9.199248922388955E-104, 5.069913980636591E-120));
        var found = new FracturingFog.Abstractions.Explore.NucleusResult(FracturingFog.Abstractions.Explore.NucleusStatus.Found, nucleus, 1808,
            new System.Numerics.Complex(1.2484338945256195E-69, 1.3350422957373403E-69), 0);
        var (fc, zoom) = FracturingFog.Abstractions.Explore.NucleusFinder.Frame(found);
        return new View("OD", fc.Re.X0, fc.Im.X0, zoom, FracturingFog.Abstractions.Explore.NucleusFinder.SuggestedIterations(1808),
            QualityPreset.Extreme, () => new HsvPalette(),
            XLimbs: [fc.Re.X1, fc.Re.X2, fc.Re.X3, fc.Re.X4, fc.Re.X5, fc.Re.X6, fc.Re.X7],
            YLimbs: [fc.Im.X1, fc.Im.X2, fc.Im.X3, fc.Im.X4, fc.Im.X5, fc.Im.X6, fc.Im.X7]);
    }

    private static MandelbrotCalculator Calc(View v)
    {
        MandelbrotCalculator.ForceScalarPtPath = v.Scalar;
        MandelbrotCalculator.AllowPtRebasing = v.Rebase;
        MandelbrotCalculator.UseSimdRebasing = v.SimdRebase;
        var c = new MandelbrotCalculator(W, H)
        {
            CenterX = v.Cx, CenterY = v.Cy, Zoom = v.Zoom, MaxIterations = v.MaxIter,
            Quality = v.Quality, ColorMap = v.Theme(),
            DisableAcceleration = v.NoBla, DisableSeriesApproximation = v.NoSa, LatticeAligned = v.Lattice,
        };
        double[] xl = v.XLimbs ?? [], yl = v.YLimbs ?? [];
        if (xl.Length > 0) c.CenterXLo = xl[0];
        if (xl.Length > 1) c.CenterX2 = xl[1];
        if (xl.Length > 2) c.CenterX3 = xl[2];
        if (xl.Length > 6) { c.CenterX4 = xl[3]; c.CenterX5 = xl[4]; c.CenterX6 = xl[5]; c.CenterX7 = xl[6]; }
        if (yl.Length > 0) c.CenterYLo = yl[0];
        if (yl.Length > 1) c.CenterY2 = yl[1];
        if (yl.Length > 2) c.CenterY3 = yl[2];
        if (yl.Length > 6) { c.CenterY4 = yl[3]; c.CenterY5 = yl[4]; c.CenterY6 = yl[5]; c.CenterY7 = yl[6]; }
        return c;
    }

    private static List<Array> Buffers(MandelbrotCalculator c) =>
    [
        (int[])c.IterationBuffer.Clone(), (float[])c.SmoothBuffer.Clone(), (float[])c.DistanceBuffer.Clone(),
        (uint[])c.ColorBuffer.Clone(), (float[])c.NormalXBuffer.Clone(), (float[])c.NormalYBuffer.Clone(),
        (float[])c.FinalZrBuffer.Clone(), (float[])c.FinalZiBuffer.Clone(),
        (float[])c.FinalDrBuffer.Clone(), (float[])c.FinalDiBuffer.Clone(),
    ];

    private static readonly string[] Names = ["iter", "smooth", "dist", "color", "nx", "ny", "zr", "zi", "dr", "di"];

    /// <summary>A mask with lone pixels, whole empty rows and SIMD-batch-sized gaps.</summary>
    private static byte[] Mask(int seed)
    {
        var rng = new Random(seed);
        var m = new byte[W * H];
        for (int y = 0; y < H; y++)
        {
            if (y % 7 == 3) continue;                         // whole rows unmarked
            for (int x = 0; x < W; x++)
            {
                if (x / 8 % 3 == 1) continue;                 // 8-wide gaps (an AVX-512 batch)
                if (rng.NextDouble() < 0.35) m[y * W + x] = 1;
            }
        }
        return m;
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void A_Marked_Pixel_Gets_The_Full_Frames_Bytes_And_An_Unmarked_One_Is_Not_Computed(View v)
    {
        // Both calculators render the dirty view first: a frame may depend on the
        // one before it (a cached reference orbit), and only the mask may differ.
        var full = Calc(v);
        full.Zoom = v.Zoom / 1000;
        full.Calculate();
        full.Zoom = v.Zoom;
        full.Calculate();
        var expected = Buffers(full);
        int[] it = (int[])expected[0];
        Assert.True(it.Distinct().Count() > 10, $"{v}: precondition: a flat frame");

        var masked = Calc(v);
        masked.Zoom = v.Zoom / 1000;   // dirty every buffer with another view
        masked.Calculate();
        var dirty = (int[])masked.IterationBuffer.Clone();
        Assert.True(Enumerable.Range(0, W * H).Count(i => dirty[i] != it[i]) > W * H / 3, $"{v}: precondition: the dirty view is too alike");

        masked.Zoom = v.Zoom;
        var mask = Mask(v.Name.Length);
        masked.PixelMask = mask;
        masked.Calculate();
        masked.PixelMask = null;
        var got = Buffers(masked);
        for (int i = 0; i < W * H; i++)
        {
            if (mask[i] != 0)
            {
                for (int b = 0; b < got.Count; b++)
                    Assert.True(Equals(expected[b].GetValue(i), got[b].GetValue(i)),
                        $"{v}: marked pixel ({i % W}, {i / W}) {Names[b]} {got[b].GetValue(i)} != {expected[b].GetValue(i)}");
            }
        }
        // An unmarked pixel no SIMD batch had to compute kept the dirty frame's
        // iteration: it was not computed (where the two frames differ, that is seen).
        int seen = 0;
        for (int i = 0; i < W * H; i++)
        {
            if (mask[i] != 0 || !IsWholeBatchSkipped(mask, i, v)) continue;
            Assert.True(dirty[i] == ((int[])got[0])[i], $"{v}: unmarked pixel ({i % W}, {i / W}) was computed");
            if (dirty[i] != it[i]) seen++;
        }
        Assert.True(seen > W * H / 10, $"{v}: too few skipped pixels to see ({seen})");
    }

    // An unmarked pixel is skipped for sure when no pixel of its 8-aligned batch
    // (the widest SIMD batch) is marked; in a partly marked batch it may be computed.
    private static bool IsWholeBatchSkipped(byte[] mask, int i, View v)
    {
        int y = i / W, x = i % W, x0 = x / 8 * 8;
        for (int c = x0; c < Math.Min(W, x0 + 8); c++) if (mask[y * W + c] != 0) return false;
        return true;
    }
}

/// <summary>#1344 — Supersampling (full and adaptive) on real frames.</summary>
public sealed class S1344SupersamplingTests
{
    private const int W = 120, H = 80;

    private static MandelbrotCalculator Calc(double cx, double cy, double zoom, int maxIter, QualityPreset q) => new(W, H)
    {
        CenterX = cx, CenterY = cy, Zoom = zoom, MaxIterations = maxIter, Quality = q, ColorMap = new HsvPalette(),
    };

    private static MandelbrotCalculator Seahorse(QualityPreset q) => Calc(-0.7436, 0.1318, 300, 800, q);
    private static MandelbrotCalculator Spiral(QualityPreset q) => Calc(-0.743643887037151, 0.13182590420533, 1e14, 3000, q);

    private static uint[] Rendered(MandelbrotCalculator c, int n, AdaptiveAa aa, out SupersampleStats st)
    {
        c.Calculate();
        st = Supersampling.AccumulateMandelbrot(c, n, aa, CancellationToken.None);
        return (uint[])c.ColorBuffer.Clone();
    }

    private static int Ch(uint c, int k) => (int)((c >> (8 * k)) & 0xFF);

    /// <summary>The supersampled colour by definition: a fresh calculator per
    /// sample (the unjittered frame + every grid cell but the centre), per-channel
    /// mean rounded half up.</summary>
    private static uint[] Reference(Func<MandelbrotCalculator> make, int side)
    {
        var frames = new List<uint[]>();
        var c0 = make(); c0.Calculate(); frames.Add((uint[])c0.ColorBuffer.Clone());
        double scale = (3.5 / Math.Max(W, H)) / c0.Zoom;
        for (int sy = 0; sy < side; sy++)
            for (int sx = 0; sx < side; sx++)
            {
                if (sx == side / 2 && sy == side / 2) continue;
                var c = make();
                var orig = CentreLimbs.Capture(c);
                CentreJitter.Apply(c, in orig, (sx + 0.5) / side - 0.5, (sy + 0.5) / side - 0.5, scale);
                c.Calculate();
                frames.Add((uint[])c.ColorBuffer.Clone());
            }
        var outB = new uint[W * H];
        int n = frames.Count;
        for (int i = 0; i < outB.Length; i++)
        {
            uint v = 0;
            for (int k = 0; k < 4; k++)
            {
                int sum = frames.Sum(f => Ch(f[i], k));
                v |= (uint)((sum + n / 2) / n) << (8 * k);
            }
            outB[i] = v;
        }
        return outB;
    }

    [Theory]
    [InlineData(4)]
    [InlineData(16)]
    public void Full_Supersampling_Is_The_Mean_Of_Every_Sample(int n)
    {
        var q = n == 4 ? QualityPreset.High : QualityPreset.Ultra;
        var got = Rendered(Seahorse(q), n, AdaptiveAa.Off, out var st);
        Assert.Equal(Reference(() => Seahorse(q), (int)Math.Sqrt(n)), got);
        Assert.Equal(st.FullPixelSamples, st.PixelSamples);
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(16, false)]
    [InlineData(4, true)]
    [InlineData(16, true)]
    public void Adaptive_With_Every_Pixel_And_No_Early_Stop_Is_Full_Supersampling(int n, bool deep)
    {
        var q = n == 4 ? QualityPreset.High : QualityPreset.Ultra;
        Func<MandelbrotCalculator> make = deep ? () => Spiral(q) : () => Seahorse(q);
        var full = Rendered(make(), n, AdaptiveAa.Off, out _);
        var every = Rendered(make(), n, new AdaptiveAa(true, -1, 0), out var st);
        Assert.Equal(full, every);
        Assert.Equal(W * H, st.Marked);
        Assert.Equal(st.FullPixelSamples, st.PixelSamples);
    }

    /// <summary>The mask by definition, pixel by pixel.</summary>
    private static bool ShouldMark(uint[] c, int[] it, int maxIter, int x, int y, double thr)
    {
        int i = y * W + x;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                int j = ny * W + nx;
                if ((it[i] >= maxIter) != (it[j] >= maxIter)) return true;
                for (int k = 0; k < 4; k++) if (Math.Abs(Ch(c[i], k) - Ch(c[j], k)) > thr) return true;
            }
        return false;
    }

    [Theory]
    [InlineData(4)]
    [InlineData(16)]
    public void Unmarked_Pixels_Are_The_One_Sample_Render_And_Marked_Ones_Converge_To_Full(int n)
    {
        var q = n == 4 ? QualityPreset.High : QualityPreset.Ultra;
        var one = Seahorse(q); one.Calculate();
        var single = (uint[])one.ColorBuffer.Clone();
        var it = (int[])one.IterationBuffer.Clone();
        var full = Rendered(Seahorse(q), n, AdaptiveAa.Off, out _);

        // No early stop: a marked pixel takes every sample, so it is exactly the full colour.
        var exact = Rendered(Seahorse(q), n, new AdaptiveAa(true, 12, 0), out var st);
        int marked = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                bool m = ShouldMark(single, it, one.MaxIterations, x, y, 12);
                marked += m ? 1 : 0;
                Assert.Equal(m ? full[i] : single[i], exact[i]);
            }
        Assert.Equal(marked, st.Marked);
        Assert.InRange(marked, W * H / 20, W * H * 3 / 4);   // precondition: a real mix
        Assert.Equal((long)W * H + (long)marked * (n - 1), st.PixelSamples);
        Assert.True(Enumerable.Range(0, W * H).Count(i => full[i] != single[i]) > marked / 4, "precondition: AA changes the marked pixels");
    }

    [Fact]
    public void Early_Stop_Takes_At_Least_MinSamples_And_Saves_Samples_At_4x4()
    {
        var st0 = default(SupersampleStats);
        Rendered(Seahorse(QualityPreset.Ultra), 16, new AdaptiveAa(true, 12, 0), out st0);
        Rendered(Seahorse(QualityPreset.Ultra), 16, new AdaptiveAa(true, 12, 4.0), out var st);
        Assert.Equal(st0.Marked, st.Marked);
        Assert.True(st.PixelSamples >= (long)W * H + (long)st.Marked * (Supersampling.MinSamples - 1), "a pixel stopped before MinSamples");
        Assert.True(st.PixelSamples < st0.PixelSamples, "the tolerance stopped no pixel early");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(16)]
    public void At_The_Defaults_The_Error_Against_Full_Is_Small_At_A_Fraction_Of_The_Samples(int n)
    {
        var q = n == 4 ? QualityPreset.High : QualityPreset.Ultra;
        // (view, the most of full's samples it may take): the busy seahorse only has
        // to save some; the whole set and the deep spiral have large flat areas.
        var views = new (string Name, Func<MandelbrotCalculator> Make, double MaxShare)[]
        {
            ("seahorse", () => Seahorse(q), 0.95),
            ("spiral 1e14", () => Spiral(q), n == 4 ? 0.5 : 0.3),
            ("whole set", () => Calc(-0.5, 0, 1, 400, q), n == 4 ? 0.5 : 0.3),
        };
        foreach (var (name, make, maxShare) in views)
        {
            var full = Rendered(make(), n, AdaptiveAa.Off, out _);
            var ad = Rendered(make(), n, new AdaptiveAa(true, AdaptiveAa.DefaultThreshold, AdaptiveAa.DefaultTolerance), out var st);
            var err = Enumerable.Range(0, W * H).Select(i => Enumerable.Range(0, 4).Max(k => Math.Abs(Ch(ad[i], k) - Ch(full[i], k)))).OrderBy(e => e).ToArray();
            Assert.True(err.Average() < 0.5, $"{name}: mean error {err.Average():F2}");
            Assert.True(err[(int)(err.Length * 0.99)] <= 4, $"{name}: p99 error {err[(int)(err.Length * 0.99)]}");
            double share = (double)st.PixelSamples / st.FullPixelSamples;
            Assert.True(share < maxShare, $"{name}: {share * 100:F0}% of the samples");
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void Progressive_Offsets_Are_The_Grid_In_Farthest_First_Order(int side)
    {
        var grid = Supersampling.GridOffsets(side);
        var prog = Supersampling.ProgressiveOffsets(side);
        Assert.Equal(side * side - 1, grid.Count);
        Assert.Equal(grid.OrderBy(o => o).ToList(), prog.OrderBy(o => o).ToList());
        var taken = new List<(double, double)> { (0, 0) };
        foreach (var o in prog)
        {
            double D(( double Jx, double Jy) p) => taken.Min(t => (p.Jx - t.Item1) * (p.Jx - t.Item1) + (p.Jy - t.Item2) * (p.Jy - t.Item2));
            double best = grid.Except(taken.Skip(1)).Max(D);
            Assert.Equal(best, D(o), 9);
            taken.Add(o);
        }
    }

    [Fact]
    public void A_Cancelled_Pass_Restores_The_Centre_And_Clears_The_Mask()
    {
        var c = Spiral(QualityPreset.Ultra);
        c.CenterXLo = 1.25e-17; c.CenterYLo = -3.5e-18;
        c.Calculate();
        var before = CentreLimbs.Capture(c);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Supersampling.AccumulateMandelbrot(c, 16, new AdaptiveAa(true, 12, 1.5), cts.Token);
        Assert.Equal(before, CentreLimbs.Capture(c));
        Assert.Null(c.PixelMask);
        Supersampling.AccumulateMandelbrot(c, 16, AdaptiveAa.Off, cts.Token);
        Assert.Equal(before, CentreLimbs.Capture(c));
    }

    [Fact]
    public void One_Sample_Is_A_No_Op()
    {
        var c = Seahorse(QualityPreset.Standard);
        c.Calculate();
        var before = (uint[])c.ColorBuffer.Clone();
        var st = Supersampling.AccumulateMandelbrot(c, 1, new AdaptiveAa(true, 12, 1.5), CancellationToken.None);
        Assert.Equal(before, c.ColorBuffer);
        Assert.Equal(1, st.Samples);
    }
}

/// <summary>#1344 — batch / export run the preset's sub-pixel AA like the live view,
/// and the adaptive settings travel with the region snapshot / --param.</summary>
public sealed class S1344AaParityTests
{
    private const int W = 96, H = 64;

    private static FracturingFog.Imaging.PosterRequest Req(QualityPreset q, FractalParameters p) => new()
    {
        FractalType = FractalType.Mandelbrot, Width = W, Height = H,
        CenterX = -0.7436, CenterY = 0.1318, Zoom = 300, MaxIterations = 800, Quality = q,
        ColorMap = new HsvPalette(), FractalParameters = p, Path = string.Empty,
    };

    private static uint[] Poster(QualityPreset q, FractalParameters p) =>
        FracturingFog.Imaging.PosterRenderer.RenderToPixels(Req(q, p), CancellationToken.None, out _, out _);

    private static uint[] Live(QualityPreset q, AdaptiveAa aa)
    {
        var c = new MandelbrotCalculator(W, H)
        {
            CenterX = -0.7436, CenterY = 0.1318, Zoom = 300, MaxIterations = 800, Quality = q, ColorMap = new HsvPalette(),
        };
        c.Calculate();
        Supersampling.AccumulateMandelbrot(c, q.AaSamples, aa, CancellationToken.None);
        return (uint[])c.ColorBuffer.Clone();
    }

    [Fact]
    public void Export_Runs_The_Presets_Supersampling_And_The_Adaptive_Setting()
    {
        var plain = Poster(QualityPreset.Standard, new FractalParameters());
        Assert.Equal(Live(QualityPreset.Standard, AdaptiveAa.Off), plain);   // Standard: one sample, unchanged

        var high = Poster(QualityPreset.High, new FractalParameters());
        Assert.Equal(Live(QualityPreset.High, AdaptiveAa.Off), high);
        Assert.NotEqual(plain, high);                                           // it is anti-aliased now

        var p = new FractalParameters { AdaptiveAa = true, AdaptiveAaThreshold = 8, AdaptiveAaTolerance = 0.5 };
        var adaptive = Poster(QualityPreset.High, p);
        Assert.Equal(Live(QualityPreset.High, new AdaptiveAa(true, 8, 0.5)), adaptive);
        Assert.NotEqual(high, adaptive);
    }

    [Fact]
    public void The_Mandelbrot_Snapshot_Carries_The_Adaptive_Settings_Only_When_Set()
    {
        Assert.Null(RegionFractalParams.Snapshot(FractalType.Mandelbrot, new FractalParameters()));
        var p = new FractalParameters { AdaptiveAa = true, AdaptiveAaThreshold = 8, AdaptiveAaTolerance = 0.5 };
        var snap = RegionFractalParams.Snapshot(FractalType.Mandelbrot, p)!;
        var back = System.Text.Json.JsonSerializer.Deserialize<RegionFractalParams>(System.Text.Json.JsonSerializer.Serialize(snap))!;
        var q = new FractalParameters();
        back.ApplyTo(q);
        Assert.True(q.AdaptiveAa);
        Assert.Equal(8, q.AdaptiveAaThreshold);
        Assert.Equal(0.5, q.AdaptiveAaTolerance);
        var c = p.Clone();
        Assert.Equal((p.AdaptiveAa, p.AdaptiveAaThreshold, p.AdaptiveAaTolerance), (c.AdaptiveAa, c.AdaptiveAaThreshold, c.AdaptiveAaTolerance));

        // Region recall resets them with the family (#960).
        RegionFractalParams.ResetFamilyToDefaults(FractalType.Mandelbrot, q);
        Assert.False(q.AdaptiveAa);
        Assert.Equal(AdaptiveAa.DefaultThreshold, q.AdaptiveAaThreshold);
        Assert.Equal(AdaptiveAa.DefaultTolerance, q.AdaptiveAaTolerance);
        var stock = new FractalParameters();
        Assert.Equal((AdaptiveAa.DefaultThreshold, AdaptiveAa.DefaultTolerance), (stock.AdaptiveAaThreshold, stock.AdaptiveAaTolerance));
    }

    [Theory]
    [InlineData("Standard", false)]
    [InlineData("Draft", false)]
    [InlineData("High", true)]
    [InlineData("Ultra", true)]
    [InlineData("Extreme", true)]
    public void The_Builder_Reports_The_Live_Only_Temporal_AA_As_A_Gap(string quality, bool gap)
    {
        var snap = new FracturingFog.Cli.BatchCommandSnapshot
        {
            Fractal = FractalType.Mandelbrot, Parameters = new FractalParameters(), QualityName = quality,
        };
        var gaps = FracturingFog.Cli.BatchCommandBuilder.DetectGaps(snap);
        Assert.Equal(gap, gaps.Any(g => g.Contains("temporal anti-aliasing", StringComparison.Ordinal) && g.Contains("#1380", StringComparison.Ordinal)));
        Assert.Equal(gap, QualityPreset.FromName(quality).TaaMaxSamples > 1);   // the gap is exactly the TAA presets
    }

    [Fact]
    public void The_Command_Builder_Emits_Them_And_The_Batch_Parser_Reads_Them_Back()
    {
        var p = new FractalParameters { AdaptiveAa = true, AdaptiveAaThreshold = 8, AdaptiveAaTolerance = 0.5 };
        var snap = new FracturingFog.Cli.BatchCommandSnapshot
        {
            Fractal = FractalType.Mandelbrot, Parameters = p,
            FamilyParams = RegionFractalParams.Snapshot(FractalType.Mandelbrot, p)!.ToKeyValues(),
        };
        string cmd = FracturingFog.Cli.BatchCommandBuilder.Build(snap);
        Assert.Contains("AdaptiveAa=", cmd);
        Assert.Contains("AdaptiveAaThreshold=", cmd);
        Assert.Contains("AdaptiveAaTolerance=", cmd);

        var args = new List<string> { "--x", "0", "--y", "0", "--zoom", "1", "--out", "o.png" };
        foreach (var kv in snap.FamilyParams) { args.Add(FracturingFog.Batch.BatchFlags.Param); args.Add(kv.Key + "=" + kv.Value); }
        Assert.True(FracturingFog.Batch.BatchOptions.TryParse(args.ToArray(), 0, out var opts, out var err), err);
        var overlay = RegionFractalParams.FromKeyValues(opts.Params, FractalType.Mandelbrot, out var error);
        Assert.True(overlay != null, error);
        var fresh = new FractalParameters();
        overlay!.ApplyTo(fresh);
        Assert.True(fresh.AdaptiveAa);
        Assert.Equal(8, fresh.AdaptiveAaThreshold);
        Assert.Equal(0.5, fresh.AdaptiveAaTolerance);
    }
}

/// <summary>#1344 — BuildMask on synthetic frames, and adaptive AA where the
/// calculator ignores the mask (the GPU SP path).</summary>
[Collection(MandelbrotGpuStaticsCollection.Name)]
public sealed class S1344MaskAndGpuTests
{
    [Fact]
    public void The_Mask_Marks_The_Set_Boundary_Even_Where_The_Colours_Match()
    {
        const int w = 5, h = 3, max = 100;
        var color = Enumerable.Repeat(0xFF102030u, w * h).ToArray();   // one flat colour
        var iter = Enumerable.Repeat(50, w * h).ToArray();
        iter[1 * w + 2] = max;                                           // one in-set pixel, same colour
        var mask = new byte[w * h];
        int n = Supersampling.BuildMask(color, iter, max, w, h, 12, mask);
        // The pixel and its 8 neighbours, nothing else.
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                Assert.Equal(Math.Abs(x - 2) <= 1 && Math.Abs(y - 1) <= 1 ? 1 : 0, mask[y * w + x]);
        Assert.Equal(9, n);
    }

    [Theory]
    [InlineData(12, 12, false)]   // exactly the threshold: not marked
    [InlineData(13, 12, true)]
    [InlineData(13, 13, false)]
    public void The_Threshold_Is_Strict_And_Diagonals_Count(int diff, double threshold, bool marked)
    {
        const int w = 3, h = 3;
        var color = Enumerable.Repeat(0xFF808080u, w * h).ToArray();
        color[2 * w + 2] = 0xFF808080u + (uint)diff;                     // the corner, blue channel
        var mask = new byte[w * h];
        Supersampling.BuildMask(color, new int[w * h], 1000, w, h, threshold, mask);
        Assert.Equal(marked ? 1 : 0, mask[1 * w + 1]);                   // the centre sees it diagonally
        Assert.Equal(marked ? 1 : 0, mask[2 * w + 2]);
        Assert.Equal(0, mask[0]);                                        // two pixels away: never
    }

    [Fact]
    public void A_Finished_Pass_Restores_Every_Centre_Limb()
    {
        foreach (var aa in new[] { AdaptiveAa.Off, new AdaptiveAa(true, 12, 1.5) })
        {
            var c = new MandelbrotCalculator(64, 48)
            {
                CenterX = -0.7436, CenterXLo = 1.25e-17, CenterY = 0.1318, CenterYLo = -3.5e-18,
                Zoom = 300, MaxIterations = 800, Quality = QualityPreset.Ultra, ColorMap = new HsvPalette(),
            };
            c.Calculate();
            var before = CentreLimbs.Capture(c);
            var st = Supersampling.AccumulateMandelbrot(c, 16, aa, CancellationToken.None);
            Assert.True(st.PixelSamples > st.Pixels, "precondition: the pass jittered");
            Assert.Equal(before, CentreLimbs.Capture(c));
            Assert.Null(c.PixelMask);
        }
    }

    [Fact]
    public void On_The_Gpu_Path_Which_Ignores_The_Mask_Unmarked_Pixels_Are_Still_The_One_Sample_Render()
    {
        using var k = FracturingFog.Rendering.Vulkan.VulkanComputeKernel.TryCreateWithOwnContext();
        if (k is null) Assert.Skip("no Vulkan device on this host");
        const int W = 120, H = 80;
        MandelbrotCalculator Calc() => new(W, H)
        {
            CenterX = -0.7436, CenterY = 0.1318, Zoom = 300, MaxIterations = 800, Quality = QualityPreset.High,
            ColorMap = new HsvPalette(), GpuKernel = k, UseGpuCompute = true,
        };
        var one = Calc(); one.Calculate();
        Assert.True(one.LastFrameUsedGpuCompute, "precondition: the GPU path ran");
        var single = (uint[])one.ColorBuffer.Clone();
        var mask = new byte[W * H];
        int marked = Supersampling.BuildMask(single, one.IterationBuffer, one.MaxIterations, W, H, 12, mask);
        Assert.InRange(marked, W * H / 20, W * H * 3 / 4);

        var full = Calc(); full.Calculate();
        Supersampling.AccumulateMandelbrot(full, 4, AdaptiveAa.Off, CancellationToken.None);
        var ad = Calc(); ad.Calculate();
        Supersampling.AccumulateMandelbrot(ad, 4, new AdaptiveAa(true, 12, 0), CancellationToken.None);
        for (int i = 0; i < W * H; i++)
            Assert.Equal(mask[i] != 0 ? full.ColorBuffer[i] : single[i], ad.ColorBuffer[i]);
    }
}
