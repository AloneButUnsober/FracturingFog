// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1802 (slice S1 of #1800) — the periodicity early-out of the main deep path's SIMD rows
// (ComputeRowPT4, ComputeRowPT8). With a periodic reference (#1614) a row batch wraps at
// the period (m := 0); there the batch's state (every lane's δ, the escaped lanes) is
// compared with a snapshot taken at an earlier wrap (doubling wrap counts), and a bit-exact
// repeat stops the batch with its active lanes in set (MandelbrotCalculator.DeepPeriodicity,
// "PeriodicAtWrap"). Checked, the #1342 / #1368 way, on the AVX2 rows and on the 8-wide rows
// (ForcePt8Rows; Vector512 emulated without AVX-512):
//   • the frame with the check equals the frame without it, byte for byte (iteration,
//     colour, smooth, distance, final z / dz), on deep views from 1e14 to 4.5e46, interior
//     and exterior;
//   • precondition: on the interior views (the period-3 nucleus at 1e14, 1e22) the check
//     catches most pixels, and the frame time drops;
//   • no false positives: pixels the check caught (DeepPeriodicityCaught) stay bounded for
//     4x the cap on their exact orbit (BigInteger fixed point, the #1319 method);
//   • the check never runs without a periodic reference (FF_PERIODIC_REFERENCE off).

using System;
using System.Linq;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Threading;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // writes DeepPeriodicity / ForcePt8Rows / PeriodicReferenceOrbit
public sealed class S1802DeepRowPeriodicityTests(ITestOutputHelper output) : IDisposable
{
    private readonly bool _savedPeriodicRef = MandelbrotCalculator.PeriodicReferenceOrbit;

    public void Dispose()
    {
        MandelbrotCalculator.DeepPeriodicity = true;
        MandelbrotCalculator.ForcePt8Rows = false;
        MandelbrotCalculator.DeepPeriodicityCaught = null;
        MandelbrotCalculator.PeriodicReferenceOrbit = _savedPeriodicRef;
    }

    public sealed record View(string Name, double[] Cx, double[] Cy, double Zoom, int MaxIter, int W, int H, bool Interior,
                              double HpThreshold = 0)
    {
        public override string ToString() => Name;
    }

    private static readonly double[] N3 = [-1.7548776662466927, -3.552187509647825e-17, 0, 0];
    private static readonly double[] Zero = [0, 0, 0, 0];

    private static readonly View[] Views =
    {
        new("nucleus3 1e14", N3, Zero, 1e14, 5000, 96, 64, true),
        new("nucleus3 1e22", N3, Zero, 1e22, 20000, 64, 48, true),
        // The whole period-3 minibrot (interior, edge, exterior), the reference on its
        // nucleus, perturbation forced (HP threshold 1): the false-positive stress view.
        new("minibrot3 whole", N3, Zero, 60, 6000, 128, 96, false, HpThreshold: 1),
        new("saprobe 1e15", [-1.1726999042772253, 8.9529605787776783E-17, 0, 0], [-0.2968356710071185, -2.3536240906562374E-18, 0, 0], 1e15, 4096, 96, 64, false),
        new("p448 5.2e33", [-0.7145519151951202, -2.0790192891565988e-17, 1.1753166641509792e-33, -8.102457136962753e-50],
            [-0.35402073332318235, 2.5477179573089096e-17, -6.072997541261718e-34, -1.8911088656798612e-50], 5.176068e33, 13440, 64, 48, false),
        new("deeper 4.5e46", [-1.9918151296901943, -7.8219818188678307E-17, 3.2454272033149852E-33, -2.6986232918289806E-49],
            [-5.5240415753972429E-06, -2.8404793590633191E-22, 1.5048294824547351E-38, -6.0649764033320806E-55], 4.49845E+46, 20000, 48, 32, false),
    };

    public static TheoryData<View, bool> Cases()
    {
        var d = new TheoryData<View, bool>();
        foreach (var v in Views) { d.Add(v, false); d.Add(v, true); }
        return d;
    }

    private sealed record Frame(int[] It, uint[] Col, float[] Sm, float[] Dist, float[] Zr, float[] Zi, float[] Dr, float[] Di,
                                int Hits, int Period, double Ms);

    private static Frame Render(View v, bool periodicity, bool pt8)
    {
        MandelbrotCalculator.DeepPeriodicity = periodicity;
        MandelbrotCalculator.ForcePt8Rows = pt8;
        var c = new MandelbrotCalculator(v.W, v.H)
        {
            Quality = v.HpThreshold > 0
                ? new QualityPreset { Name = "perturb", AllowHighPrecision = true, HPZoomThreshold = v.HpThreshold }
                : QualityPreset.Extreme,
            CenterX = v.Cx[0], CenterXLo = v.Cx[1], CenterX2 = v.Cx[2], CenterX3 = v.Cx[3],
            CenterY = v.Cy[0], CenterYLo = v.Cy[1], CenterY2 = v.Cy[2], CenterY3 = v.Cy[3],
            Zoom = v.Zoom, MaxIterations = v.MaxIter, ColorMap = ColorPalette.BuiltIns[0],
        };
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        c.Calculate(CancellationToken.None);
        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        return new((int[])c.IterationBuffer.Clone(), (uint[])c.ColorBuffer.Clone(), (float[])c.SmoothBuffer.Clone(),
            (float[])c.DistanceBuffer.Clone(), (float[])c.FinalZrBuffer.Clone(), (float[])c.FinalZiBuffer.Clone(),
            (float[])c.FinalDrBuffer.Clone(), (float[])c.FinalDiBuffer.Clone(), c.LastDeepPeriodicPixels, c.ReferencePeriod, ms);
    }

    private static void AssertSame(string label, Frame a, Frame b)
    {
        Assert.True(a.It.AsSpan().SequenceEqual(b.It), $"{label}: iterations differ");
        Assert.True(a.Col.AsSpan().SequenceEqual(b.Col), $"{label}: colours differ");
        foreach (var (x, y, n) in new[] { (a.Sm, b.Sm, "smooth"), (a.Dist, b.Dist, "distance"), (a.Zr, b.Zr, "zr"), (a.Zi, b.Zi, "zi"), (a.Dr, b.Dr, "dr"), (a.Di, b.Di, "di") })
            for (int i = 0; i < x.Length; i++)
                Assert.True(BitConverter.SingleToInt32Bits(x[i]) == BitConverter.SingleToInt32Bits(y[i]), $"{label}: {n} differs at pixel {i}");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_Row_Check_Changes_No_Pixel_And_Catches_The_Interior(View v, bool pt8)
    {
        var off = Render(v, periodicity: false, pt8);
        Assert.Equal(0, off.Hits);
        var on = Render(v, periodicity: true, pt8);
        int n = v.W * v.H, inSet = off.It.Count(i => i >= v.MaxIter);
        output.WriteLine($"{v.Name} {(pt8 ? "PT8" : "PT4")}: period {on.Period}, {inSet}/{n} in set, caught {on.Hits}; off {off.Ms:F0} ms, on {on.Ms:F0} ms");
        AssertSame($"{v.Name} {(pt8 ? "PT8" : "PT4")}", off, on);
        Assert.True(on.Hits <= inSet, $"{on.Hits} caught > {inSet} in set");
        if (v.Interior)
        {
            Assert.True(on.Period > 0, "precondition: no periodic reference");
            Assert.True(inSet > n / 2, $"precondition: only {inSet} of {n} pixels in set");
            Assert.True(on.Hits > inSet * 0.9, $"the check caught only {on.Hits} of {inSet} in-set pixels");
        }
    }

    [Fact]
    public void Without_A_Periodic_Reference_The_Check_Never_Runs()
    {
        MandelbrotCalculator.PeriodicReferenceOrbit = false;
        var on = Render(Views[0], periodicity: true, pt8: false);
        Assert.Equal(0, on.Period);
        Assert.Equal(0, on.Hits);
    }

    // ── exact orbits (BigInteger fixed point, the #1319 method) ────────────────

    private const int Frac = 420;

    private static BigInteger Fix(double d)
    {
        if (d == 0) return BigInteger.Zero;
        long bits = BitConverter.DoubleToInt64Bits(d);
        int exp = (int)((bits >> 52) & 0x7FF);
        long man = bits & 0xFFFFFFFFFFFFFL;
        if (exp == 0) exp++; else man |= 1L << 52;
        int sh = exp - 1075 + Frac;
        var r = sh >= 0 ? new BigInteger(man) << sh : new BigInteger(man) >> -sh;
        return bits < 0 ? -r : r;
    }

    private static bool StaysBounded(BigInteger cx, BigInteger cy, int steps)
    {
        BigInteger four = BigInteger.One << (Frac + 2), x = 0, y = 0;
        for (int k = 0; k < steps; k++)
        {
            BigInteger x2 = (x * x) >> Frac, y2 = (y * y) >> Frac;
            if (x2 + y2 > four) return false;
            BigInteger xy = (x * y) >> (Frac - 1);
            x = x2 - y2 + cx;
            y = xy + cy;
        }
        return true;
    }

    public static TheoryData<View> CaughtViews()
    {
        var d = new TheoryData<View>();
        foreach (var v in Views.Where(v => v.Name.StartsWith("nucleus3") || v.Name.StartsWith("minibrot3"))) d.Add(v);
        return d;
    }

    [Theory]
    [MemberData(nameof(CaughtViews))]
    public void Pixels_The_Check_Caught_Stay_Bounded_On_Their_Exact_Orbit(View v)
    {
        var log = new System.Collections.Concurrent.ConcurrentQueue<int>();
        MandelbrotCalculator.DeepPeriodicityCaught = log;
        Frame on;
        try { on = Render(v, periodicity: true, pt8: false); }
        finally { MandelbrotCalculator.DeepPeriodicityCaught = null; }
        int n = v.W * v.H;
        var caught = log.Where(i => i >= 0 && i < n).Distinct().OrderBy(i => i).ToArray();
        if (caught.Length == 0) Assert.Skip($"{v.Name}: the check caught nothing");
        Assert.All(caught, i => Assert.True(on.It[i] >= v.MaxIter, $"caught pixel {i} not in set"));
        double scale = 3.5 / Math.Max(v.W, v.H) / v.Zoom;
        BigInteger cx0 = v.Cx.Aggregate(BigInteger.Zero, (s, d) => s + Fix(d));
        BigInteger cy0 = v.Cy.Aggregate(BigInteger.Zero, (s, d) => s + Fix(d));
        // 24 spread over the frame, plus every caught pixel next to an escaping one (the
        // likeliest false positives), up to 200.
        int stepK = Math.Max(1, caught.Length / 24);
        bool Edge(int i)
        {
            int x = i % v.W, y = i / v.W;
            return (x > 0 && on.It[i - 1] < v.MaxIter) || (x + 1 < v.W && on.It[i + 1] < v.MaxIter)
                || (y > 0 && on.It[i - v.W] < v.MaxIter) || (y + 1 < v.H && on.It[i + v.W] < v.MaxIter);
        }
        var sample = caught.Where((_, k) => k % stepK == 0).Take(24)
            .Concat(caught.Where(Edge).Take(200)).Distinct().ToArray();
        int steps = v.MaxIter * 4;
        foreach (int i in sample)
        {
            int x = i % v.W, y = i / v.W;
            BigInteger cx = cx0 + Fix((x - v.W * 0.5) * scale), cy = cy0 + Fix((y - v.H * 0.5) * scale);
            Assert.True(StaysBounded(cx, cy, steps), $"{v.Name}: pixel ({x}, {y}) escapes within {steps} exact steps");
        }
        output.WriteLine($"{v.Name}: {sample.Length} of {caught.Length} caught pixels ({caught.Count(Edge)} next to an escaping one) bounded for {steps} exact steps");
    }

    // ── the batch-state predicate (the frames above never meet the near-misses) ──

    [Fact]
    public void The_Batch_State_Repeats_Only_When_Every_Lane_And_The_Escaped_Lanes_Do()
    {
        var r = System.Runtime.Intrinsics.Vector256.Create(1e-20, -3e-21, 5e-22, 7e-19);
        var i = System.Runtime.Intrinsics.Vector256.Create(-2e-20, 4e-21, -6e-22, 8e-19);
        Assert.True(MandelbrotCalculator.SameBatchState(r, i, 0b0010, r, i, 0b0010));
        // Real parts repeat, one imaginary part doesn't.
        var i2 = i.WithElement(3, 8.000000000000001e-19);
        Assert.False(MandelbrotCalculator.SameBatchState(r, i2, 0b0010, r, i, 0b0010));
        // Imaginary parts repeat, one real part doesn't.
        Assert.False(MandelbrotCalculator.SameBatchState(r.WithElement(0, 1.0000000000000002e-20), i, 0b0010, r, i, 0b0010));
        // Same δ, another lane escaped since the snapshot.
        Assert.False(MandelbrotCalculator.SameBatchState(r, i, 0b0011, r, i, 0b0010));
        // NaN never repeats.
        var n = r.WithElement(1, double.NaN);
        Assert.False(MandelbrotCalculator.SameBatchState(n, i, 0, n, i, 0));

        var r8 = System.Runtime.Intrinsics.Vector512.Create(1e-20, -3e-21, 5e-22, 7e-19, 2e-20, 3e-20, 4e-20, 5e-20);
        var i8 = System.Runtime.Intrinsics.Vector512.Create(-2e-20, 4e-21, -6e-22, 8e-19, 1e-21, 2e-21, 3e-21, 4e-21);
        Assert.True(MandelbrotCalculator.SameBatchState(r8, i8, 0x10, r8, i8, 0x10));
        Assert.False(MandelbrotCalculator.SameBatchState(r8, i8.WithElement(7, 4.0000000000000004e-21), 0x10, r8, i8, 0x10));
        Assert.False(MandelbrotCalculator.SameBatchState(r8.WithElement(6, 4.0000000000000004e-20), i8, 0x10, r8, i8, 0x10));
        Assert.False(MandelbrotCalculator.SameBatchState(r8, i8, 0x30, r8, i8, 0x10));
    }
}
