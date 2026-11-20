// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1916 (epic #1253, D11) — DeepGeneralFinder: GeneralFinder's minibrot and Misiurewicz finders past
// double, with the orbit in BigFloat. Checks against oracles outside the finder:
//   • on z² + c (MultibrotMap(2)), the deep z² + c finder (DeepNucleusFinder, #1866) as the oracle: the
//     same period and nucleus, and a size within a few percent (the generalised atom size reduces to
//     1/(b·l²) there);
//   • for Tricorn, Burning Ship and z³ + c: a found point is a root — its Newton step, computed here
//     with the steps written out and a BigFloat finite-difference Jacobian (not the finder's double
//     tangent), is far below the view — and the period is minimal;
//   • the real calculator renders the nucleus in set at the framing zoom (EscapeTimeCalculator on the
//     engine), with escaping pixels around it;
//   • where double holds, GeneralFinder (body radius measured by escape) agrees on period, nucleus and
//     framing;
//   • a Misiurewicz point re-found at 1e40 from a seed a fifth of a view off (Burning Ship too; its deep
//     minibrot search refuses — see DeepGeneralFinder's header).
// Mutants (by hand), all caught:
//   • the polish at the minibrot's precision skipped;
//   • the Jacobian's ∂f/∂c term dropped;
//   • Λ dropped from the size;
//   • the Newton step's scale with the wrong sign;
//   • the minimal-period test removed (only the direct test catches it: the scan tries the lower period
//     first).

using System;
using System.Linq;
using System.Threading;
using FracturingFog;
using FracturingFog.Abstractions.Explore;
using FracturingFog.FFMath;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1916DeepGeneralFinderTests(ITestOutputHelper output)
{
    private const double L2_10 = 3.3219280948873622;

    private static double Log2Of(BigFloat re, BigFloat im, int p)
    {
        var n2 = BigFloat.Add(BigFloat.Mul(re, re, p), BigFloat.Mul(im, im, p), p);
        if (n2.IsZero) return double.NegativeInfinity;
        long e = n2.Exponent + (long)n2.Mantissa.GetBitLength();
        return 0.5 * (e + Math.Log2(n2.ScaleB(-e).ToDouble()));
    }

    private delegate (BigFloat, BigFloat) Step(BigFloat zr, BigFloat zi, BigFloat cr, BigFloat ci, int p);

    private static readonly Step Tri = (a, b, cr, ci, p) =>
        (BigFloat.Add(BigFloat.Sub(BigFloat.Mul(a, a, p), BigFloat.Mul(b, b, p), p), cr, p), BigFloat.Add(-BigFloat.Mul(a, b, p).ScaleB(1), ci, p));

    private static readonly Step Ship = (a, b, cr, ci, p) =>
    {
        var x = a.Abs(); var y = b.Abs();
        return (BigFloat.Add(BigFloat.Sub(BigFloat.Mul(x, x, p), BigFloat.Mul(y, y, p), p), cr, p), BigFloat.Add(BigFloat.Mul(x, y, p).ScaleB(1), ci, p));
    };

    private static readonly Step Cube = (a, b, cr, ci, p) =>
    {
        var sr = BigFloat.Sub(BigFloat.Mul(a, a, p), BigFloat.Mul(b, b, p), p); var si = BigFloat.Mul(a, b, p).ScaleB(1);
        return (BigFloat.Add(BigFloat.Sub(BigFloat.Mul(sr, a, p), BigFloat.Mul(si, b, p), p), cr, p),
                BigFloat.Add(BigFloat.Add(BigFloat.Mul(sr, b, p), BigFloat.Mul(si, a, p), p), ci, p));
    };

    private static (BigFloat, BigFloat) Iter(Step f, BigFloat cr, BigFloat ci, int n, int p, int k = -1)
    {
        BigFloat zr = BigFloat.Zero(p), zi = BigFloat.Zero(p);
        for (int i = 0; i < n; i++) (zr, zi) = f(zr, zi, cr, ci, p);
        return (zr, zi);
    }

    // log2 of the Newton step for G(c) = z_{k+p} − z_k at c, with the Jacobian by a BigFloat forward
    // difference (h = 2^−(prec/2)); the oracle's own measure of "how far c is from a root".
    private static double RootStepLog2(Step f, ExactCentre c, int k, int period, int prec)
    {
        (BigFloat, BigFloat) G(BigFloat cr, BigFloat ci)
        {
            var (ar, ai) = Iter(f, cr, ci, k + period, prec);
            if (k == 0) return (ar, ai);
            var (br, bi) = Iter(f, cr, ci, k, prec);
            return (BigFloat.Sub(ar, br, prec), BigFloat.Sub(ai, bi, prec));
        }
        var cr0 = c.Re.WithPrecision(prec); var ci0 = c.Im.WithPrecision(prec);
        var (g1, g2) = G(cr0, ci0);
        if (g1.IsZero && g2.IsZero) return double.NegativeInfinity;   // an exact root
        var h = BigFloat.FromDouble(1, prec).ScaleB(-prec / 2);
        var (x1, x2) = G(BigFloat.Add(cr0, h, prec), ci0);
        var (y1, y2) = G(cr0, BigFloat.Add(ci0, h, prec));
        double hd = Math.Pow(2, -prec / 2);
        // The Jacobian fits a double (|∂z/∂c| ~ λ^p); G may not (an accurate root is ~2^−2000), so G is
        // scaled by its own exponent and the exponent added back to the step's log.
        double j11 = BigFloat.Sub(x1, g1, prec).ToDouble() / hd, j21 = BigFloat.Sub(x2, g2, prec).ToDouble() / hd;
        double j12 = BigFloat.Sub(y1, g1, prec).ToDouble() / hd, j22 = BigFloat.Sub(y2, g2, prec).ToDouble() / hd;
        long e = Math.Max(g1.IsZero ? long.MinValue / 4 : g1.Exponent + (long)g1.Mantissa.GetBitLength(),
                          g2.IsZero ? long.MinValue / 4 : g2.Exponent + (long)g2.Mantissa.GetBitLength());
        double a = g1.ScaleB(-e).ToDouble(), b = g2.ScaleB(-e).ToDouble();
        double det = j11 * j22 - j12 * j21;
        double dx = (j22 * a - j12 * b) / det, dy = (-j21 * a + j11 * b) / det;
        return Math.Log2(Math.Sqrt(dx * dx + dy * dy)) + e;
    }

    // ── z² + c against the deep z² + c finder ──

    // The period-n nucleus on the real antenna by Newton from −2 (S1866's construction).
    private static BigFloat AntennaNucleus(int n, int prec)
    {
        var c = BigFloat.FromDouble(-2, prec); var one = BigFloat.FromDouble(1, prec);
        for (int step = 0; step < 40; step++)
        {
            var z = BigFloat.Zero(prec); var dz = BigFloat.Zero(prec);
            for (int k = 0; k < n; k++)
            {
                dz = BigFloat.Add(BigFloat.Mul(z, dz, prec).ScaleB(1), one, prec);
                z = BigFloat.Add(BigFloat.Mul(z, z, prec), c, prec);
            }
            c = BigFloat.Sub(c, BigFloat.Div(z, dz, prec), prec);
        }
        return c;
    }

    [Theory]
    [InlineData(40)]
    [InlineData(80)]
    public void On_Z_Squared_It_Agrees_With_The_Deep_Mandelbrot_Finder(int n)
    {
        int prec = BigFloat.PrecisionForZoom(1.25 * n + 20) + 64;
        var c = AntennaNucleus(n, prec);
        // The deep z² + c finder's own answer first, then the same view for the general finder.
        var probe = DeepNucleusFinder.FindMinibrot(new ExactCentre(c, BigFloat.Zero(prec)), -0.6 * n);
        Assert.True(probe.Found, $"precondition: the deep z² + c finder ({probe.Status})");
        double sizeLog10 = probe.SizeLog10;
        var view = new ExactCentre(BigFloat.Add(c, BigFloat.FromDouble(3 * Math.Pow(10, sizeLog10), prec), prec), BigFloat.Zero(prec));
        double radiusLog10 = sizeLog10 + Math.Log10(20);
        var expect = DeepNucleusFinder.FindMinibrot(view, radiusLog10);
        var got = DeepGeneralFinder.FindMinibrot(new MultibrotMap(2), view, radiusLog10, maxPeriod: 4 * n);
        output.WriteLine($"z²+c n {n}: deep Mandelbrot finder p {expect.Period} size 10^{expect.SizeLog10:F3}; general p {got.Period} size 10^{got.SizeLog10:F3}, {got.Attempts} attempts");
        Assert.True(expect.Found && got.Found, $"{expect.Status} / {got.Status}");
        Assert.Equal(expect.Period, got.Period);
        double err = Log2Of(BigFloat.Sub(got.Point.Re, expect.Nucleus.Re, prec), BigFloat.Sub(got.Point.Im, expect.Nucleus.Im, prec), prec);
        Assert.True(err < expect.SizeLog10 * L2_10 - 19, $"nucleus off by 2^{err:F1}");
        Assert.True(Math.Abs(got.SizeLog10 - expect.SizeLog10) < 0.05, $"size 10^{got.SizeLog10:F3} vs 10^{expect.SizeLog10:F3}");
    }

    [Fact]
    public void A_Multiple_Of_The_Period_Is_Not_A_Nucleus()
    {
        // A period-40 nucleus is also a root of z_80 = 0; the minimal-period test must reject it at 80
        // (the scan tries the lower period first, so this is its only direct check).
        int prec = BigFloat.PrecisionForZoom(70) + 64;
        var c = new ExactCentre(AntennaNucleus(40, prec), BigFloat.Zero(prec));
        var map = new MultibrotMap(2);
        Assert.True(DeepGeneralFinder.FindNucleus(map, c, 40, -48).Found, "precondition: the nucleus at its own period");
        Assert.False(DeepGeneralFinder.FindNucleus(map, c, 80, -48).Found);
    }

    // ── the other families ──

    public static TheoryData<string, double, double, int> Families => new()
    {
        // Misiurewicz points of earlier slices: minibrots accumulate there at every depth.
        { "tricorn", 0.7718445063460382, 1.336873900534257, 30 },
        { "tricorn", 0.7718445063460382, 1.336873900534257, 80 },
        { "cube", 0.34062501931404023, 1.2712709082638338, 30 },   // √(ω − 1), z³ + c (S1908)
    };

    private static ExactCentre Exact(Step f, double gr, double gi, int zl)
        => S1909ConjR2Tests.Misiurewicz((zr, zi, cr, ci, p) => f(zr, zi, cr, ci, p), gr, gi, zl + 8, 3, 1);

    private static (IDeepOrbitMap Map, Step F, FractalType T) Of(string fam) => fam switch
    {
        "tricorn" => (new TricornMap(), Tri, FractalType.Tricorn),
        "ship" => (new BurningShipMap(), Ship, FractalType.BurningShip),
        _ => (new MultibrotMap(3), Cube, FractalType.Multibrot),
    };

    [Theory]
    [MemberData(nameof(Families))]
    public void A_Deep_Minibrot_Is_A_Root_And_Renders_In_Set(string fam, double gr, double gi, int zl)
    {
        var (map, f, type) = Of(fam);
        int prec = BigFloat.PrecisionForZoom(zl + 4);
        // A view centred on the Misiurewicz point itself (S1909's BigFloat Newton, independent of the
        // finder): minibrots accumulate on it, at periods ~ log(1/R)/log λ for a view of radius R.
        var view = Exact(f, gr, gi, zl);
        double radiusLog10 = -zl;
        var got = DeepGeneralFinder.FindMinibrot(map, view, radiusLog10, maxPeriod: 1500);
        output.WriteLine($"{fam} 1e{zl}: {got.Status} p {got.Period} size 10^{got.SizeLog10:F2} zoom 10^{got.ZoomLog10:F2}, {got.Attempts} attempts");
        Assert.True(got.Found, $"{fam}: no minibrot ({got.Status}, {got.Attempts} attempts)");
        // Inside the view disk.
        double off = Log2Of(BigFloat.Sub(got.Point.Re, view.Re, prec), BigFloat.Sub(got.Point.Im, view.Im, prec), prec);
        Assert.True(off <= radiusLog10 * L2_10 + 1e-9, $"outside the view: 2^{off:F1}");
        // A root, by the oracle's own Newton step, and of minimal period.
        int q = got.Point.Precision + 64;
        double step = RootStepLog2(f, got.Point, 0, got.Period, q);
        output.WriteLine($"  root step 2^{step:F1} (view 2^{radiusLog10 * L2_10:F1})");
        Assert.True(step < radiusLog10 * L2_10 - 40, $"not a root: Newton step 2^{step:F1}");
        for (int d = 1; d < got.Period; d++)
            if (got.Period % d == 0)
                Assert.True(RootStepLog2(f, got.Point, 0, d, q) > radiusLog10 * L2_10 - 20, $"period {got.Period} not minimal: {d} is a root");
        // The real calculator: the nucleus in set at the framing zoom, with escaping pixels around.
        double zoom = Math.Pow(10, got.ZoomLog10);
        if (zoom > EscapeTimeCalculator.DeepZoomThreshold && zoom < 1e280)
        {
            var calc = new EscapeTimeCalculator(32, 32)
            {
                FractalType = type, Zoom = zoom, MaxIterations = Math.Max(2000, 40 * got.Period),
                ColorMap = ColorPalette.BuiltIns[0], FractalParameters = new FractalParameters { MultibrotExponent = 3 },
            };
            calc.SetDeepCentre(got.Point.ToDeep(), got.Point);
            calc.Calculate(CancellationToken.None);
            int maxIt = calc.MaxIterations;
            int inSet = calc.IterationBuffer.Take(32 * 32).Count(n => n >= maxIt);
            output.WriteLine($"  calculator at 10^{got.ZoomLog10:F2}: centre {calc.IterationBuffer[16 * 32 + 16]} / {maxIt}, {inSet}/1024 in set ({calc.LastPrecisionLabel})");
            Assert.Equal("PT", calc.LastPrecisionLabel);
            Assert.True(calc.IterationBuffer[16 * 32 + 16] >= maxIt, "the nucleus pixel escapes");
            Assert.True(inSet < 1024, "precondition: escaping pixels around the minibrot (the framing is not inside it)");
        }
    }

    [Fact]
    public void Burning_Ship_Refuses_Deep_Minibrots_But_Not_Misiurewicz_Points()
    {
        // The ship's folds break the ball bound and the size estimate (DeepGeneralFinder's header), so
        // its deep minibrot search refuses rather than report a wrong period or size.
        var map = new BurningShipMap();
        Assert.False(map.DeepNuclei);
        var view = Exact(Ship, 0.3014809547257814, 0.4519892606922896, 30);
        Assert.False(DeepGeneralFinder.FindMinibrot(map, view, -30).Found);
    }

    [Theory]
    [InlineData("tricorn", 0.7718445063460382, 1.336873900534257, 28)]
    public void Shallow_It_Agrees_With_The_Double_Finder(string fam, double gr, double gi, int period)
    {
        // Where double holds, GeneralFinder (Newton in double, the body radius measured by escape) is
        // the oracle for the period, the nucleus and the framing.
        var (map, _, _) = Of(fam);
        var g = GeneralFinder.FindMinibrot(map, new V2(gr, gi), 1e-6);
        var d = DeepGeneralFinder.FindMinibrot(map, new ExactCentre(BigFloat.FromDouble(gr, 200), BigFloat.FromDouble(gi, 200)), -6);
        output.WriteLine($"{fam}: double p {g.Period} zoom 10^{Math.Log10(g.Zoom):F2}; deep p {d.Period} zoom 10^{d.ZoomLog10:F2}");
        Assert.True(g.Found && d.Found);
        Assert.Equal(period, g.Period);
        Assert.Equal(g.Period, d.Period);
        Assert.True(Math.Abs(d.Point.Re.ToDouble() - g.Point.X) + Math.Abs(d.Point.Im.ToDouble() - g.Point.Y) < 1e-14, "the same nucleus");
        Assert.True(Math.Abs(d.ZoomLog10 - Math.Log10(g.Zoom)) < 0.3, "the size estimate frames as the measured body does");
    }

    [Theory]
    [InlineData("tricorn", 0.7718445063460382, 1.336873900534257)]
    [InlineData("ship", 0.3014809547257814, 0.4519892606922896)]
    public void A_Misiurewicz_Point_Is_Refound_Deep(string fam, double gr, double gi)
    {
        var (map, f, _) = Of(fam);
        const int zl = 40;
        int prec = BigFloat.PrecisionForZoom(zl + 4);
        // The exact point (S1909's Newton), then a seed a fifth of a 1e-40 view off it.
        var exact = Exact(f, gr, gi, zl);
        var seed = new ExactCentre(BigFloat.Add(exact.Re.WithPrecision(prec), BigFloat.FromDouble(0.2e-40, prec), prec), exact.Im.WithPrecision(prec));
        var got = DeepGeneralFinder.FindMisiurewicz(map, seed, 3, 1, -zl);
        output.WriteLine($"{fam}: M({got.Preperiod},{got.Period}) multiplier 2^{got.MultiplierLog2:F2}");
        Assert.True(got.Found);
        Assert.Equal((3, 1), (got.Preperiod, got.Period));
        double dist = Log2Of(BigFloat.Sub(got.Point.Re, exact.Re.WithPrecision(prec), prec), BigFloat.Sub(got.Point.Im, exact.Im.WithPrecision(prec), prec), prec);
        Assert.True(dist < -zl * L2_10 - 20, $"refound 2^{dist:F1} from the point");
        Assert.True(got.MultiplierLog2 > 0, "repelling");
        double step = RootStepLog2(f, got.Point, 3, 1, got.Point.Precision + 64);
        Assert.True(step < -zl * L2_10 - 40, $"not a root: Newton step 2^{step:F1}");
    }
}
