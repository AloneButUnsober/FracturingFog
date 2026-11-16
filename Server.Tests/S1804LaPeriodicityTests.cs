// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1804 (slice S3 of #1800) — the periodicity early-out of the CPU LA evaluator (#1623):
// the scalar EvaluateLa and the 4-lane LA4 rows (#1628), which share the stages
// (LaRunStages) and the AT (RunLaAt).
//   • the stages restart from a fresh δ with j = 0 at every rebase and every climb to
//     the top; a bit-exact repeat of (δ, stage) there, while the cap test can't bind
//     across the next cycle, puts the pixel in set (LaCycleRepeats);
//   • the AT's own iteration z' ← z'² + c' gets Brent's check on z'.
// Checked, the #1342 / #1368 way, with LA on (scalar and 4-lane):
//   • on == off byte for byte (iteration, colour, smooth, distance, final z / dz) on the
//     period-3 nucleus (QD Newton) at 1e30 and 1e40, the whole period-3 minibrot, and
//     exterior deep views (saprobe 1e15, 4.5e46);
//   • precondition: the frames went through the LA evaluator, and on the interior views
//     the check caught most in-set pixels;
//   • no false positives: caught pixels (edge pixels of the whole minibrot included) stay
//     bounded for 4x the cap on their exact orbit (BigInteger fixed point);
//   • the AT check: on == off on a frame that runs the AT, and it catches there.

using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(MandelbrotGpuStaticsCollection.Name)]   // writes DeepPeriodicity / DeepPeriodicityCaught
public sealed class S1804LaPeriodicityTests(ITestOutputHelper output) : IDisposable
{
    public void Dispose()
    {
        MandelbrotCalculator.DeepPeriodicity = true;
        MandelbrotCalculator.DeepPeriodicityCaught = null;
    }

    private static readonly double[] N3 = Nucleus3();
    private static readonly double[] Zero = [0, 0, 0, 0];

    private static double[] Nucleus3()
    {
        var c = new FracturingFog.FFMath.QD(-1.7548776662466927, -3.552187509647825e-17, 0, 0);
        for (int k = 0; k < 8; k++)
        {
            FracturingFog.FFMath.QD z = new(0, 0, 0, 0), dz = new(0, 0, 0, 0);
            for (int n = 0; n < 3; n++) { dz = 2.0 * (z * dz) + 1.0; z = z * z + c; }
            c = c - z / dz;
        }
        return [c.X0, c.X1, c.X2, c.X3];
    }

    public sealed record View(string Name, double[] Cx, double[] Cy, double Zoom, int MaxIter, int W, int H, bool Interior,
                              double HpThreshold = 0)
    {
        public override string ToString() => Name;
    }

    private static readonly View[] Views =
    {
        new("nucleus3 1e30", N3, Zero, 1e30, 20000, 64, 48, true),
        new("nucleus3 1e40", N3, Zero, 1e40, 20000, 64, 48, true),
        new("minibrot3 whole", N3, Zero, 60, 6000, 96, 64, false, HpThreshold: 1),
        new("saprobe 1e15", [-1.1726999042772253, 8.9529605787776783E-17, 0, 0], [-0.2968356710071185, -2.3536240906562374E-18, 0, 0], 1e15, 4096, 64, 48, false),
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
                                int Hits, bool La, bool La4, long AtPixels, int Period, double Ms);

    private static Frame Render(View v, bool la4, bool periodicity)
    {
        MandelbrotCalculator.DeepPeriodicity = periodicity;
        var c = new MandelbrotCalculator(v.W, v.H)
        {
            Quality = v.HpThreshold > 0
                ? new QualityPreset { Name = "perturb", AllowHighPrecision = true, HPZoomThreshold = v.HpThreshold }
                : QualityPreset.Extreme,
            CenterX = v.Cx[0], CenterXLo = v.Cx[1], CenterX2 = v.Cx[2], CenterX3 = v.Cx[3],
            CenterY = v.Cy[0], CenterYLo = v.Cy[1], CenterY2 = v.Cy[2], CenterY3 = v.Cy[3],
            Zoom = v.Zoom, MaxIterations = v.MaxIter, ColorMap = ColorPalette.BuiltIns[0],
            LaMode = DeepLaMode.On, LaSimd = la4,
        };
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        c.Calculate(CancellationToken.None);
        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        return new((int[])c.IterationBuffer.Clone(), (uint[])c.ColorBuffer.Clone(), (float[])c.SmoothBuffer.Clone(),
            (float[])c.DistanceBuffer.Clone(), (float[])c.FinalZrBuffer.Clone(), (float[])c.FinalZiBuffer.Clone(),
            (float[])c.FinalDrBuffer.Clone(), (float[])c.FinalDiBuffer.Clone(), c.LastDeepPeriodicPixels,
            c.LastFrameUsedLa, c.LastFrameUsedLaSimd, c.LastLaAtWork.Pixels, c.ReferencePeriod, ms);
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
    public void The_La_Check_Changes_No_Pixel_And_Catches_The_Interior(View v, bool la4)
    {
        MandelbrotCalculator.TakeLaAtPeriodicPixels();
        var off = Render(v, la4, periodicity: false);
        Assert.Equal(0, MandelbrotCalculator.TakeLaAtPeriodicPixels());
        var on = Render(v, la4, periodicity: true);
        int atHits = MandelbrotCalculator.TakeLaAtPeriodicPixels();
        int n = v.W * v.H, inSet = off.It.Count(i => i >= v.MaxIter);
        string label = $"{v.Name} {(la4 ? "LA4" : "LA")}";
        output.WriteLine($"{label}: la {on.La} la4 {on.La4} period {on.Period} AT px {on.AtPixels}, {inSet}/{n} in set, caught {on.Hits} (+AT {atHits}); off {off.Ms:F0} ms, on {on.Ms:F0} ms");
        Assert.True(off.La, $"{label}: precondition: not on the LA evaluator");
        Assert.Equal(la4, off.La4);
        AssertSame(label, off, on);
        Assert.True(on.Hits + atHits <= inSet, $"{on.Hits} + {atHits} caught > {inSet} in set");
        if (v.Interior)
        {
            Assert.True(inSet > n / 2, $"precondition: only {inSet} of {n} pixels in set");
            Assert.True(on.Hits + atHits > inSet * 0.9, $"the check caught only {on.Hits} + {atHits} of {inSet} in-set pixels");
        }
    }

    public static TheoryData<View, bool> CaughtCases()
    {
        var d = new TheoryData<View, bool>();
        foreach (var v in Views.Where(v => v.Interior || v.Name.StartsWith("minibrot3"))) { d.Add(v, false); d.Add(v, true); }
        return d;
    }

    [Theory]
    [MemberData(nameof(CaughtCases))]
    public void Pixels_The_Check_Caught_Stay_Bounded_On_Their_Exact_Orbit(View v, bool la4)
    {
        var log = new System.Collections.Concurrent.ConcurrentQueue<int>();
        MandelbrotCalculator.DeepPeriodicityCaught = log;
        Frame on;
        try { on = Render(v, la4, periodicity: true); }
        finally { MandelbrotCalculator.DeepPeriodicityCaught = null; }
        int n = v.W * v.H;
        var caught = log.Where(i => i >= 0 && i < n).Distinct().OrderBy(i => i).ToArray();
        if (caught.Length == 0) Assert.Skip($"{v.Name}: the stage check caught nothing");
        Assert.All(caught, i => Assert.True(on.It[i] >= v.MaxIter, $"caught pixel {i} not in set"));
        double scale = 3.5 / Math.Max(v.W, v.H) / v.Zoom;
        BigInteger cx0 = v.Cx.Aggregate(BigInteger.Zero, (s, d) => s + Fix(d));
        BigInteger cy0 = v.Cy.Aggregate(BigInteger.Zero, (s, d) => s + Fix(d));
        bool Edge(int i)
        {
            int x = i % v.W, y = i / v.W;
            return (x > 0 && on.It[i - 1] < v.MaxIter) || (x + 1 < v.W && on.It[i + 1] < v.MaxIter)
                || (y > 0 && on.It[i - v.W] < v.MaxIter) || (y + 1 < v.H && on.It[i + v.W] < v.MaxIter);
        }
        int stepK = Math.Max(1, caught.Length / 16);
        var sample = caught.Where((_, k) => k % stepK == 0).Take(16).Concat(caught.Where(Edge).Take(60)).Distinct().ToArray();
        int steps = v.MaxIter * 4;
        foreach (int i in sample)
        {
            int x = i % v.W, y = i / v.W;
            BigInteger cx = cx0 + Fix((x - v.W * 0.5) * scale), cy = cy0 + Fix((y - v.H * 0.5) * scale);
            Assert.True(StaysBounded(cx, cy, steps), $"{v.Name}: pixel ({x}, {y}) escapes within {steps} exact steps");
        }
        output.WriteLine($"{v.Name} {(la4 ? "LA4" : "LA")}: {sample.Length} of {caught.Length} caught pixels ({caught.Count(Edge)} on the edge) bounded for {steps} exact steps");
    }

    // The period-448 minibrot at 5.2e33 (8-limb centre, as --deepcostprobe has it): its
    // interior pixels run the AT.
    private static readonly double[] P448X = [-0.7145519151951202, -2.0790192891565988e-17, 1.1753166641509792e-33, -8.102457136962753e-50,
        -1.0894092006534645e-66, -5.481175082318642e-83, -1.5340609125118276e-99, 1.4285946495395532e-116];
    private static readonly double[] P448Y = [-0.35402073332318235, 2.5477179573089096e-17, -6.072997541261718e-34, -1.8911088656798612e-50,
        -4.820603700098655e-67, -3.1189999875481587e-83, 1.2339668272603997e-99, -9.480108552256602e-117];

    private static (int[] It, uint[] Col, float[] Sm, long AtPx, bool La) RenderP448(bool la4, bool periodicity, int zoomMul)
    {
        MandelbrotCalculator.DeepPeriodicity = periodicity;
        var c = new MandelbrotCalculator(64, 48)
        {
            Quality = QualityPreset.Extreme,
            CenterX = P448X[0], CenterXLo = P448X[1], CenterX2 = P448X[2], CenterX3 = P448X[3],
            CenterX4 = P448X[4], CenterX5 = P448X[5], CenterX6 = P448X[6], CenterX7 = P448X[7],
            CenterY = P448Y[0], CenterYLo = P448Y[1], CenterY2 = P448Y[2], CenterY3 = P448Y[3],
            CenterY4 = P448Y[4], CenterY5 = P448Y[5], CenterY6 = P448Y[6], CenterY7 = P448Y[7],
            Zoom = 5.176068e33 * zoomMul, MaxIterations = 40000, ColorMap = ColorPalette.BuiltIns[0],
            LaMode = DeepLaMode.On, LaSimd = la4,
        };
        c.Calculate(CancellationToken.None);
        return ((int[])c.IterationBuffer.Clone(), (uint[])c.ColorBuffer.Clone(), (float[])c.SmoothBuffer.Clone(),
            c.LastLaAtWork.Pixels, c.LastFrameUsedLa);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_At_Check_Changes_No_Pixel_And_Catches_There(bool la4)
    {
        MandelbrotCalculator.TakeLaAtPeriodicPixels();
        var off = RenderP448(la4, periodicity: false, zoomMul: 8);
        Assert.Equal(0, MandelbrotCalculator.TakeLaAtPeriodicPixels());
        var on = RenderP448(la4, periodicity: true, zoomMul: 8);
        int atHits = MandelbrotCalculator.TakeLaAtPeriodicPixels();
        int inSet = off.It.Count(i => i >= 40000);
        output.WriteLine($"p448 x8 {(la4 ? "LA4" : "LA")}: la {on.La}, AT px {on.AtPx}, in set {inSet}, AT caught {atHits}");
        Assert.True(on.La, "precondition: not on the LA evaluator");
        Assert.True(on.AtPx > 0, "precondition: no pixel ran the AT");
        Assert.True(off.It.AsSpan().SequenceEqual(on.It), "iterations differ");
        Assert.True(off.Col.AsSpan().SequenceEqual(on.Col), "colours differ");
        Assert.True(off.Sm.AsSpan().SequenceEqual(on.Sm), "smooth differs");
        Assert.True(atHits > 0, "precondition: the AT check caught nothing");
    }

    [Fact]
    public void An_La_Restart_Repeats_Only_When_Delta_And_The_Stage_Do()
    {
        Assert.True(MandelbrotCalculator.SameLaRestart(1e-30, -2e-30, 3, 1e-30, -2e-30, 3));
        Assert.False(MandelbrotCalculator.SameLaRestart(1e-30, -2e-30, 2, 1e-30, -2e-30, 3));                 // another stage
        Assert.False(MandelbrotCalculator.SameLaRestart(1e-30, Math.BitIncrement(-2e-30), 3, 1e-30, -2e-30, 3));  // Im
        Assert.False(MandelbrotCalculator.SameLaRestart(Math.BitIncrement(1e-30), -2e-30, 3, 1e-30, -2e-30, 3));  // Re
        Assert.False(MandelbrotCalculator.SameLaRestart(double.NaN, 0, 0, double.NaN, 0, 0));
        // The AT compares z' with the same two-part test as the rebased loops.
        Assert.False(MandelbrotCalculator.SameRebaseState(0.25, Math.BitIncrement(0.5), 0.25, 0.5));
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
