// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1803 (slice S2 of #1800) — the periodicity early-out of the main deep path's rebased
// loops: ResumePixelPTRebased (with and without BLA; the scalar rows against a periodic
// reference run every pixel through it), its DD-reference twin and the 4-lane rebased loop
// (#1274). Each rebase leaves the state (δ := z, m = 0); a bit-exact repeat of that state at
// a later rebase (snapshots at doubling rebase counts, per pixel / per lane) means the loop
// cycles for ever, so the pixel is in set ("PeriodicAtRebase"). With BLA the skips' limit
// is fixed between rebases only while maxIter − iter ≥ refLen, so the check waits for that.
// Each case steers the frame into one loop and checks, the #1342 / #1368 way:
//   • precondition: the frame went through the rebased loops (PtRebasedPixels), and on the
//     nucleus views the check caught most in-set pixels;
//   • on == off byte for byte (iteration, colour, smooth, distance, final z / dz);
//   • no false positives: caught pixels (edge pixels of the whole period-3 minibrot
//     included) stay bounded for 4x the cap on their exact orbit (BigInteger fixed point).

using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // writes DeepPeriodicity and the path statics
public sealed class S1803RebasedPeriodicityTests(ITestOutputHelper output) : IDisposable
{
    private readonly bool _savedScalar = MandelbrotCalculator.ForceScalarPtPath;
    private readonly bool _savedDd = MandelbrotCalculator.UseDdRebaseReference;
    private readonly bool _savedSimdRebase = MandelbrotCalculator.UseSimdRebasing;

    public void Dispose()
    {
        MandelbrotCalculator.DeepPeriodicity = true;
        MandelbrotCalculator.DeepPeriodicityCaught = null;
        MandelbrotCalculator.ForceScalarPtPath = _savedScalar;
        MandelbrotCalculator.UseDdRebaseReference = _savedDd;
        MandelbrotCalculator.UseSimdRebasing = _savedSimdRebase;
        MandelbrotCalculator.ForceRebasedLoops = false;
    }

    public enum Loop
    {
        ResumeBla,      // the scalar rows against a periodic reference (BLA on)
        ResumePlain,    // the same rows, acceleration off
        QdResumeBla,    // QD band, BLA on: the PT4 rows hand over to the resumed loop (#1613)
        Rebased4,       // QD band, acceleration off: the PT4 rows queue to the 4-lane loop
        RebasedScalar,  // as Rebased4 with UseSimdRebasing off (ResumePixelPTRebased, no BLA)
        DdTwin,         // the DD-reference twin (SM-11a); every batch handed over (ForceRebasedLoops)
    }

    private static readonly double[] N3 = [-1.7548776662466927, -3.552187509647825e-17, 0, 0];
    private static readonly double[] Zero = [0, 0, 0, 0];

    public sealed record View(string Name, double[] Cx, double Zoom, int MaxIter, int W, int H, double HpThreshold = 0)
    {
        public override string ToString() => Name;
    }

    private static readonly View Dd1e22 = new("nucleus3 1e22", N3, 1e22, 20000, 48, 32);

    // The period-3 nucleus to quad-double (Newton on z_3(c) = 0, real axis), so the
    // reference stays periodic (|Z_3| below the pixel) in the QD band.
    private static readonly double[] N3Qd = Nucleus3();
    private static readonly View Qd1e28 = new("nucleus3 1e28", N3Qd, 1e28, 30000, 48, 32);

    private static double[] Nucleus3()
    {
        var c = new FracturingFog.FFMath.QD(N3[0], N3[1], 0, 0);
        for (int k = 0; k < 8; k++)
        {
            FracturingFog.FFMath.QD z = new(0, 0, 0, 0), dz = new(0, 0, 0, 0);
            for (int n = 0; n < 3; n++) { dz = 2.0 * (z * dz) + 1.0; z = z * z + c; }
            c = c - z / dz;
        }
        return [c.X0, c.X1, c.X2, c.X3];
    }
    // The whole period-3 minibrot, perturbation forced: interior, edge and exterior.
    private static readonly View Whole = new("minibrot3 whole", N3, 60, 6000, 96, 64, HpThreshold: 1);

    private static View ViewFor(Loop loop) => loop is Loop.ResumeBla or Loop.ResumePlain ? Dd1e22 : Qd1e28;

    private sealed record Frame(int[] It, uint[] Col, float[] Sm, float[] Dist, float[] Zr, float[] Zi, float[] Dr, float[] Di,
                                int Hits, long Rebased, int Period, double Ms);

    private static Frame Render(View v, Loop loop, bool periodicity)
    {
        MandelbrotCalculator.DeepPeriodicity = periodicity;
        MandelbrotCalculator.ForceScalarPtPath = loop is Loop.ResumeBla or Loop.ResumePlain;
        MandelbrotCalculator.UseDdRebaseReference = loop == Loop.DdTwin;
        MandelbrotCalculator.UseSimdRebasing = loop != Loop.RebasedScalar;
        // The QD-band loops are only reached from the PT4 rows' glitch hand-over, which a
        // periodic reference's interior rarely trips: hand every batch over (#1803 hook).
        MandelbrotCalculator.ForceRebasedLoops = loop is Loop.QdResumeBla or Loop.Rebased4 or Loop.RebasedScalar or Loop.DdTwin;
        var c = new MandelbrotCalculator(v.W, v.H)
        {
            Quality = v.HpThreshold > 0
                ? new QualityPreset { Name = "perturb", AllowHighPrecision = true, HPZoomThreshold = v.HpThreshold }
                : QualityPreset.Extreme,
            CenterX = v.Cx[0], CenterXLo = v.Cx[1], CenterX2 = v.Cx[2], CenterX3 = v.Cx[3],
            Zoom = v.Zoom, MaxIterations = v.MaxIter, ColorMap = ColorPalette.BuiltIns[0],
            DisableAcceleration = loop is Loop.ResumePlain or Loop.Rebased4 or Loop.RebasedScalar,
            LaMode = DeepLaMode.Off,   // the LA rows are #1804
        };
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        c.Calculate(CancellationToken.None);
        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        return new((int[])c.IterationBuffer.Clone(), (uint[])c.ColorBuffer.Clone(), (float[])c.SmoothBuffer.Clone(),
            (float[])c.DistanceBuffer.Clone(), (float[])c.FinalZrBuffer.Clone(), (float[])c.FinalZiBuffer.Clone(),
            (float[])c.FinalDrBuffer.Clone(), (float[])c.FinalDiBuffer.Clone(), c.LastDeepPeriodicPixels, c.PtRebasedPixels,
            c.ReferencePeriod, ms);
    }

    private static void AssertSame(string label, Frame a, Frame b)
    {
        Assert.True(a.It.AsSpan().SequenceEqual(b.It), $"{label}: iterations differ");
        Assert.True(a.Col.AsSpan().SequenceEqual(b.Col), $"{label}: colours differ");
        foreach (var (x, y, n) in new[] { (a.Sm, b.Sm, "smooth"), (a.Dist, b.Dist, "distance"), (a.Zr, b.Zr, "zr"), (a.Zi, b.Zi, "zi"), (a.Dr, b.Dr, "dr"), (a.Di, b.Di, "di") })
            for (int i = 0; i < x.Length; i++)
                Assert.True(BitConverter.SingleToInt32Bits(x[i]) == BitConverter.SingleToInt32Bits(y[i]), $"{label}: {n} differs at pixel {i}");
    }

    public static TheoryData<Loop> Loops()
    {
        var d = new TheoryData<Loop>();
        foreach (var l in Enum.GetValues<Loop>()) d.Add(l);
        return d;
    }

    [Theory]
    [MemberData(nameof(Loops))]
    public void The_Rebased_Check_Changes_No_Pixel_And_Catches_The_Interior(Loop loop)
    {
        var v = ViewFor(loop);
        var off = Render(v, loop, periodicity: false);
        var on = Render(v, loop, periodicity: true);
        int n = v.W * v.H, inSet = off.It.Count(i => i >= v.MaxIter);
        output.WriteLine($"{loop} on {v.Name}: period {on.Period}, rebased px {on.Rebased}, {inSet}/{n} in set, caught {on.Hits}; off {off.Ms:F0} ms, on {on.Ms:F0} ms");
        Assert.True(off.Rebased > n / 2, $"precondition: only {off.Rebased} pixels ran the rebased loops");
        Assert.Equal(0, off.Hits);
        AssertSame(loop.ToString(), off, on);
        Assert.True(inSet > n / 2, $"precondition: only {inSet} of {n} pixels in set");
        if (loop == Loop.DdTwin)
        {
            // The DD twin turns the periodic reference off (#1614), so only cycles of the
            // |z| < |δ| rebases can repeat; report, don't require.
            Assert.Equal(0, on.Period);
            return;
        }
        Assert.True(on.Period > 0, "precondition: no periodic reference");
        Assert.True(on.Hits > inSet * 0.9, $"the check caught only {on.Hits} of {inSet} in-set pixels");
    }

    [Theory]
    [MemberData(nameof(Loops))]
    public void The_Whole_Minibrot_Is_Unchanged_And_Its_Caught_Pixels_Stay_Bounded(Loop loop)
    {
        var off = Render(Whole, loop, periodicity: false);
        var log = new System.Collections.Concurrent.ConcurrentQueue<int>();
        MandelbrotCalculator.DeepPeriodicityCaught = log;
        Frame on;
        try { on = Render(Whole, loop, periodicity: true); }
        finally { MandelbrotCalculator.DeepPeriodicityCaught = null; }
        AssertSame($"{loop} whole", off, on);
        int n = Whole.W * Whole.H;
        var caught = log.Where(i => i >= 0 && i < n).Distinct().ToArray();
        output.WriteLine($"{loop} whole: rebased px {on.Rebased}, {off.It.Count(i => i >= Whole.MaxIter)} in set, caught {caught.Length}");
        if (caught.Length == 0) return;   // this loop saw no cycle here (e.g. the rows caught them first)
        Assert.All(caught, i => Assert.True(on.It[i] >= Whole.MaxIter, $"caught pixel {i} not in set"));

        double scale = 3.5 / Math.Max(Whole.W, Whole.H) / Whole.Zoom;
        BigInteger cx0 = Whole.Cx.Aggregate(BigInteger.Zero, (s, d) => s + Fix(d));
        bool Edge(int i)
        {
            int x = i % Whole.W, y = i / Whole.W;
            return (x > 0 && on.It[i - 1] < Whole.MaxIter) || (x + 1 < Whole.W && on.It[i + 1] < Whole.MaxIter)
                || (y > 0 && on.It[i - Whole.W] < Whole.MaxIter) || (y + 1 < Whole.H && on.It[i + Whole.W] < Whole.MaxIter);
        }
        int stepK = Math.Max(1, caught.Length / 16);
        var sample = caught.Where((_, k) => k % stepK == 0).Take(16).Concat(caught.Where(Edge).Take(60)).Distinct().ToArray();
        foreach (int i in sample)
        {
            int x = i % Whole.W, y = i / Whole.W;
            BigInteger cx = cx0 + Fix((x - Whole.W * 0.5) * scale), cy = Fix((y - Whole.H * 0.5) * scale);
            Assert.True(StaysBounded(cx, cy, Whole.MaxIter * 4), $"{loop}: pixel ({x}, {y}) escapes within {Whole.MaxIter * 4} exact steps");
        }
        output.WriteLine($"  {sample.Length} caught pixels ({caught.Count(Edge)} on the edge) bounded for {Whole.MaxIter * 4} exact steps");
    }

    [Fact]
    public void The_Rebase_State_Repeats_Only_When_Both_Parts_Do()
    {
        Assert.True(MandelbrotCalculator.SameRebaseState(1e-20, -2e-20, 1e-20, -2e-20));
        Assert.False(MandelbrotCalculator.SameRebaseState(1e-20, -2.0000000000000002e-20, 1e-20, -2e-20));
        Assert.False(MandelbrotCalculator.SameRebaseState(1.0000000000000001e-20, -2e-20, 1e-20, -2e-20));
        Assert.False(MandelbrotCalculator.SameRebaseState(double.NaN, 0, double.NaN, 0));

        var a = new FracturingFog.FFMath.DD(1e-20, 3e-37);
        var b = new FracturingFog.FFMath.DD(-2e-20, -5e-37);
        Assert.True(MandelbrotCalculator.SameRebaseState(a, b, a, b));
        Assert.False(MandelbrotCalculator.SameRebaseState(a, new FracturingFog.FFMath.DD(-2e-20, -5.000000000000001e-37), a, b));   // Im low limb
        Assert.False(MandelbrotCalculator.SameRebaseState(a, new FracturingFog.FFMath.DD(-2.0000000000000002e-20, -5e-37), a, b));  // Im high limb
        Assert.False(MandelbrotCalculator.SameRebaseState(new FracturingFog.FFMath.DD(1e-20, 3.0000000000000003e-37), b, a, b));    // Re low limb
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
}
