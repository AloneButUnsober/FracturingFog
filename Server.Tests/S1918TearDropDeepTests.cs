// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1918 (epic #1253, D13) — TearDrop on the generic perturbation engine. Its hand-written DD-PT /
// QD-PT (a QD reference, ~5e58 ceiling) give way, past the HP threshold, to R2Plan.TearDrop on
// PerturbationEngine: (z³ / c³)·(−i) + c, rational in c, through D6's quotient difference. Checks:
//   • the plan against direct BigFloat iteration of the formula written out by hand (S1909's oracle),
//     counts and dz/dc, at two Misiurewicz views (preperiod 3, period 1), 1e40 and 1e120 deep;
//   • the calculator: its counts against the same oracle, 1e40 and 1e120 (the latter past the old QD
//     ceiling);
//   • parity with today's PT below its ceiling: at 1e20 (DD-PT) and 1e40 (QD-PT) the engine and the
//     old tier (UseEngine = false) give the same counts on ≥ 97 % of pixels;
//   • below the HP threshold nothing changed (SP, byte-identical with the engine off);
//   • the explorer limit is the engine's.
// Mutants (by hand), all caught: the sink's dz/dc zeroed; the centre's Hi limb only; the engine
// never used; the plan's bailout² as 4; the plan's −i as +i.

using System;
using System.Linq;
using System.Threading;
using FracturingFog;
using FracturingFog.Abstractions.Explore;
using FracturingFog.FFMath;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Perturbation;
using Xunit;
using static FracturingFog.Server.Tests.S1909ConjR2Tests;

namespace FracturingFog.Server.Tests;

public sealed class S1918TearDropDeepTests(ITestOutputHelper output)
{
    // (z³ / c³)·(−i) + c: w = z³, q = c³, w / q = w·conj(q) / |q|², then ·(−i): (a + bi)(−i) = b − ai.
    internal static readonly Step TearDropStep = (a, b, cr, ci, p) =>
    {
        (BigFloat, BigFloat) Cube(BigFloat x, BigFloat y)
        {
            var x2 = Sq(x, p); var y2 = Sq(y, p);
            return (BigFloat.Sub(BigFloat.Mul(x2, x, p), BigFloat.Mul(BigFloat.FromDouble(3, p), BigFloat.Mul(x, y2, p), p), p),
                    BigFloat.Sub(BigFloat.Mul(BigFloat.FromDouble(3, p), BigFloat.Mul(x2, y, p), p), BigFloat.Mul(y2, y, p), p));
        }
        var (wr, wi) = Cube(a, b);
        var (qr, qi) = Cube(cr, ci);
        var n2 = BigFloat.Add(Sq(qr, p), Sq(qi, p), p);
        var re = BigFloat.Div(BigFloat.Add(BigFloat.Mul(wr, qr, p), BigFloat.Mul(wi, qi, p), p), n2, p);
        var im = BigFloat.Div(BigFloat.Sub(BigFloat.Mul(wi, qr, p), BigFloat.Mul(wr, qi, p), p), n2, p);
        return (BigFloat.Add(im, cr, p), BigFloat.Add(-re, ci, p));
    };

    private const double Bailout2 = 1024.0;

    // Misiurewicz points (z₄ = z₃, repelling), found by a Newton search in double.
    private static readonly (double Re, double Im)[] Views =
    [
        (-1.1572550410888338, 0.2566039639387012),
        (-0.24662224018590695, 0.641046064081321),
    ];

    private static TearDropCalculator Calc(ExactCentre centre, double zoom, int maxIter, bool engine)
    {
        var c = new TearDropCalculator(W, H)
        {
            Zoom = zoom, MaxIterations = maxIter, Quality = QualityPreset.Ultra, ColorMap = ColorPalette.BuiltIns[0], UseEngine = engine,
        };
        c.SetDeepCentre(centre.ToDeep(), centre);
        c.Calculate(CancellationToken.None);
        return c;
    }

    private static ExactCentre View(int view, int zl)
    {
        double scale = 3.5 / W / Math.Pow(10, zl);
        return Off(Misiurewicz(TearDropStep, Views[view].Re, Views[view].Im, zl), scale, zl);
    }

    [Theory]
    [InlineData(0, 40)]
    [InlineData(0, 120)]
    [InlineData(1, 40)]
    [InlineData(1, 120)]
    public void The_Plan_Matches_Direct_Iteration(int view, int zl)
    {
        double scale = 3.5 / W / Math.Pow(10, zl);
        var centre = View(view, zl);
        var got = Engine(R2Plan.TearDrop(), centre, scale, 2000);
        Assert.True(got.Stats.BlaSkips > 0, "precondition: the matrix BLA skipped");
        Check(output, $"TearDrop plan, view {view}, 1e{zl}", got, TearDropStep, Bailout2, centre, scale, 2000);
    }

    [Theory]
    [InlineData(0, 40)]
    [InlineData(1, 120)]
    public void The_Calculator_Matches_Direct_Iteration(int view, int zl)
    {
        double scale = 3.5 / W / Math.Pow(10, zl);
        var centre = View(view, zl);
        var c = Calc(centre, Math.Pow(10, zl), 2000, engine: true);
        Assert.Equal("PT", c.LastPrecisionLabel);
        var got = new Result(c.IterationBuffer.Take(W * H).ToArray(), new double[W * H], new double[W * H], c.LastPerturbationStats);
        Check(output, $"TearDropCalculator, view {view}, 1e{zl}", got, TearDropStep, Bailout2, centre, scale, 2000, derivative: false);
    }

    [Theory]
    [InlineData(0, 20, "DD-PT")]
    [InlineData(1, 20, "DD-PT")]
    [InlineData(0, 40, "QD-PT")]
    [InlineData(1, 40, "QD-PT")]
    public void The_Engine_Agrees_With_The_Old_PT_Below_Its_Ceiling(int view, int zl, string oldTier)
    {
        var centre = View(view, zl);
        var eng = Calc(centre, Math.Pow(10, zl), 2000, engine: true);
        var old = Calc(centre, Math.Pow(10, zl), 2000, engine: false);
        Assert.Equal(oldTier, old.LastPrecisionLabel);
        int n = W * H, eq = Enumerable.Range(0, n).Count(i => eng.IterationBuffer[i] == old.IterationBuffer[i]);
        int distinct = eng.IterationBuffer.Take(n).Distinct().Count();
        output.WriteLine($"view {view} 1e{zl}: engine vs {oldTier} {eq}/{n} equal, {distinct} distinct; {eng.LastPerturbationStats}");
        Assert.True(distinct >= 5, $"precondition: structure ({distinct} distinct counts)");
        Assert.True(eq >= n * 0.97, $"engine vs {oldTier}: {eq}/{n} equal");
        // dz/dc: the old tier carries the closed form d' = −3i·z²·d / c³ + 3i·z³ / c⁴ + 1, the engine
        // its own tangent; they must agree where the counts do (escaped pixels). The buffers are float,
        // so only at 1e20 (|dz/dc| overflows float at 1e40; the plan test checks it deep).
        if (zl > 20) return;
        int checkedD = 0, badD = 0;
        for (int i = 0; i < n; i++)
        {
            if (eng.IterationBuffer[i] != old.IterationBuffer[i] || eng.IterationBuffer[i] >= 2000) continue;
            double er = eng.FinalDrBuffer[i], ei = eng.FinalDiBuffer[i], or = old.FinalDrBuffer[i], oi = old.FinalDiBuffer[i];
            checkedD++;
            if (!(Math.Sqrt((er - or) * (er - or) + (ei - oi) * (ei - oi)) <= 1e-3 * Math.Sqrt(or * or + oi * oi))) badD++;
        }
        output.WriteLine($"  dz/dc off by > 0.1 % on {badD}/{checkedD}");
        Assert.True(checkedD >= n / 2, $"precondition: escaped pixels ({checkedD})");
        Assert.True(badD <= n / 100, $"dz/dc off on {badD}/{checkedD}");
    }

    [Fact]
    public void Below_The_HP_Threshold_The_Frame_Is_Byte_Identical()
    {
        var centre = Misiurewicz(TearDropStep, Views[0].Re, Views[0].Im, 13);
        var on = Calc(centre, 1e9, 800, engine: true);
        var off = Calc(centre, 1e9, 800, engine: false);
        Assert.Equal("SP", on.LastPrecisionLabel);
        Assert.True(on.ColorBuffer.Distinct().Count() >= 3, "precondition: structure");
        Assert.Equal(off.ColorBuffer, on.ColorBuffer);
    }

    [Fact]
    public void The_Explorer_Limit_Is_The_Engines()
        => Assert.Equal(FamilyDepth.EngineDoubleDeltaMaxZoom, FamilyDepth.MaxZoomFor(FractalType.TearDrop));
}
