// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1921 (epic #1253, D2b) — the perturbation engine past double: a FloatExp pixel scale, the scaled-δ
// prelude (δ = d·2^s while |δ| < 2^-900), the derivative held as d·2^t, the sink's distance unit, and
// EscapeTimeCalculator's log zoom (IDeepZoom). Checks, each against something outside the engine:
//   • counts against direct BigFloat iteration of the formula written out by hand, and the derivative
//     (in the distance unit) against a BigFloat finite difference, at 1e320 and 1e600: z² + c, z³ + c
//     (PowerPlan), Tricorn and Burning Ship (R2Plan: the fold through the rescaled step), and Julia
//     (the dynamic plane), all at exact Misiurewicz / repelling-point views;
//   • the prelude forced at a double depth (1e60) agrees with the double loop;
//   • EscapeTimeCalculator (Multibrot d = 3) at 1e200 and 1e400 through SetZoomLog10: counts against
//     the oracle and FinalDr / Di (the distance unit) within 1 % of the finite difference; the
//     derivative there is ~1e200+, past what the double loop held before (S1907 measured ∞ at 1e200);
//   • the poster path hands the log zoom over; FamilyDepth is Mandelbrot's for the covered families.
// Mutants (by hand):
//   • caught: the scaled step not rescaled; the prelude BLA's ε factor dropped; Deliver ignoring the
//     unit; the calculator never passing the FloatExp scale; the poster dropping the log zoom; the
//     prelude's escape step at the reference's end (by the Glynn test's direct-pixel bound);
//   • survive, with reason: the scaled ∂f/∂c weight as 1 (PowerPlan, R2Plan, the double loop's BLA) —
//     once scaled t ≥ 100, so that term is ≤ 2^-100 against f′·d ~ 2^500, below rounding unless
//     |f′| < 2^-450; the prelude's scaled rebase — it needs |Z| < 8·|δ| < 2^-897, which these views
//     (and, but for Z₀ = 0, practically any) never reach. Not tested here: the live host's and the
//     video's IDeepZoom sync (one line each, as the poster's).

using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using FracturingFog;
using FracturingFog.Abstractions.Explore;
using FracturingFog.FFMath;
using FracturingFog.Imaging;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Perturbation;
using Xunit;
using static FracturingFog.Server.Tests.S1909ConjR2Tests;

namespace FracturingFog.Server.Tests;

public sealed class S1921DeepFxTests(ITestOutputHelper output)
{
    private const double Bail = 512.0 * 512.0;

    internal static Step Power(int d) => (a, b, cr, ci, p) =>
    {
        BigFloat r = a, i = b;
        for (int k = 1; k < d; k++)
            (r, i) = (BigFloat.Sub(BigFloat.Mul(r, a, p), BigFloat.Mul(i, b, p), p),
                      BigFloat.Add(BigFloat.Mul(r, b, p), BigFloat.Mul(i, a, p), p));
        return (BigFloat.Add(r, cr, p), BigFloat.Add(i, ci, p));
    };

    internal static long Log2(BigFloat x) => x.IsZero ? long.MinValue / 4 : x.Exponent + (long)BigInteger.Abs(x.Mantissa).GetBitLength();

    // Newton in R² on G (BigFloat in, BigFloat out), from a double guess, to |G| < 2^-(bits): the residual
    // and the step are rescaled before they meet a double, so it works past 1e308 (S1909's stops there).
    internal static (BigFloat, BigFloat) Newton(Func<BigFloat, BigFloat, (BigFloat, BigFloat)> g, double gr, double gi, int p, long bits)
    {
        BigFloat r = BigFloat.FromDouble(gr, p), i = BigFloat.FromDouble(gi, p);
        int hb = p / 2;
        var h = BigFloat.FromDouble(1, p).ScaleB(-hb);
        for (int it = 0; it < 60; it++)
        {
            var (g1, g2) = g(r, i);
            long lg = Math.Max(Log2(g1), Log2(g2));
            if (lg < -bits) return (r, i);
            var (x1, x2) = g(BigFloat.Add(r, h, p), i);
            var (y1, y2) = g(r, BigFloat.Add(i, h, p));
            double j11 = BigFloat.Sub(x1, g1, p).ScaleB(hb).ToDouble(), j21 = BigFloat.Sub(x2, g2, p).ScaleB(hb).ToDouble();
            double j12 = BigFloat.Sub(y1, g1, p).ScaleB(hb).ToDouble(), j22 = BigFloat.Sub(y2, g2, p).ScaleB(hb).ToDouble();
            double det = j11 * j22 - j12 * j21;
            long k = -lg;
            double a = g1.ScaleB(k).ToDouble(), b = g2.ScaleB(k).ToDouble();
            r = BigFloat.Sub(r, BigFloat.FromDouble((j22 * a - j12 * b) / det, p).ScaleB(-k), p);
            i = BigFloat.Sub(i, BigFloat.FromDouble((-j21 * a + j11 * b) / det, p).ScaleB(-k), p);
        }
        throw new InvalidOperationException("precondition: Newton did not converge");
    }

    // A Misiurewicz point (z₄ = z₃) of the parameter plane.
    internal static ExactCentre Misiurewicz(Step f, double gr, double gi, int zl)
    {
        int p = BigFloat.PrecisionForZoom(zl) + 64;
        var (r, i) = Newton((cr, ci) =>
        {
            BigFloat zr = BigFloat.Zero(p), zi = BigFloat.Zero(p);
            for (int k = 0; k < 3; k++) (zr, zi) = f(zr, zi, cr, ci, p);
            var (nr, ni) = f(zr, zi, cr, ci, p);
            return (BigFloat.Sub(nr, zr, p), BigFloat.Sub(ni, zi, p));
        }, gr, gi, p, (long)(zl * 3.33) + 40);
        return new ExactCentre(r, i);
    }

    internal readonly record struct View(ExactCentre Centre, double Sm, int S0, int Zl)
    {
        public FloatExp Scale => FloatExp.FromDouble(Sm).ScaleB(S0);
        public BigFloat Off(int x, int w, BigFloat c, int p) => BigFloat.Add(c.WithPrecision(p), BigFloat.FromDouble((x - w * 0.5) * Sm, p).ScaleB(S0), p);
    }

    // The view: 3.5 / W wide at 10^-zl, centred a third and a fifth of a pixel off the point.
    internal static View ViewAt(ExactCentre m, int zl)
    {
        var fx = FloatExp.FromLog10(Math.Log10(3.5 / W) - zl);
        int p = BigFloat.PrecisionForZoom(zl);
        var c = new ExactCentre(BigFloat.Add(m.Re.WithPrecision(p), BigFloat.FromDouble(0.37 * fx.M, p).ScaleB(fx.E), p),
                                BigFloat.Add(m.Im.WithPrecision(p), BigFloat.FromDouble(0.21 * fx.M, p).ScaleB(fx.E), p));
        return new View(c, fx.M, (int)fx.E, zl);
    }

    internal sealed record Got(int[] Iter, double[] Dr, double[] Di, PerturbationStats Stats);

    private static Got Run(IPerturbationPlan plan, View v, int maxIter, int unitE, int fxBelow = PerturbationEngine.FxScaleLog2, double convEps = 0)
    {
        var it = new int[W * H]; var dr = new double[W * H]; var di = new double[W * H];
        // Magnet's second stop (z = 1); a converged pixel gets the count −1 − iter (no derivative).
        var conv = convEps > 0 ? new PerturbationEngine.Convergence(1, 0, convEps, (idx, n, zr, zi, dp) => it[idx] = -1 - n) : null;
        var st = PerturbationEngine.Render(plan, v.Centre, 0, W, H, maxIter,
            (idx, n, zr, zi, a, b) => { it[idx] = n; dr[idx] = a; di[idx] = b; }, CancellationToken.None,
            converge: conv, pixelScale: v.Scale, derivExp2: unitE, fxBelowLog2: fxBelow);
        return new Got(it, dr, di, st);
    }

    // Counts against direct iteration; the derivative (· 2^-unitE) against a forward difference of
    // 1e-9 pixel along Re(pixel). dynamicC: the dynamic plane (z₀ = the pixel, c fixed).
    internal void Check(string name, Got got, Step f, View v, int maxIter, int unitE, (double, double)? dynamicC = null, bool derivative = true,
                       double bail = Bail, double convEps = 0)
    {
        int pf = BigFloat.PrecisionForZoom(v.Zl) + 96;
        int total = 0, eq = 0, dvBad = 0, dvChecked = 0;
        var seen = new System.Collections.Generic.HashSet<int>();
        string ex = "";
        var h = BigFloat.FromDouble(1e-9 * v.Sm, pf).ScaleB(v.S0);
        (int, BigFloat, BigFloat) Orbit(BigFloat pr, BigFloat pi, int stopAt)
        {
            BigFloat zr, zi, cr, ci;
            if (dynamicC is { } jc) { zr = pr; zi = pi; cr = BigFloat.FromDouble(jc.Item1, pf); ci = BigFloat.FromDouble(jc.Item2, pf); }
            else { zr = BigFloat.Zero(pf); zi = BigFloat.Zero(pf); cr = pr; ci = pi; }
            for (int n = 0; n < maxIter; n++)
            {
                if (n == stopAt) return (n, zr, zi);
                double a = zr.ToDouble(), b = zi.ToDouble();
                if (stopAt < 0 && a * a + b * b >= bail) return (n, zr, zi);
                if (stopAt < 0 && convEps > 0 && (a - 1) * (a - 1) + b * b <= convEps * convEps) return (-1 - n, zr, zi);
                (zr, zi) = f(zr, zi, cr, ci, pf);
            }
            return (maxIter, zr, zi);
        }
        var gate = new object();
        var samples = (from y in Enumerable.Range(0, H) where y % 3 == 1 from x in Enumerable.Range(0, W) where x % 3 == 1 select (x, y)).ToArray();
        System.Threading.Tasks.Parallel.ForEach(samples, xy =>
            {
                var (x, y) = xy;
                var pr = v.Off(x, W, v.Centre.Re, pf);
                var pi = v.Off(y, H, v.Centre.Im, pf);
                var (n, zr, zi) = Orbit(pr, pi, -1);
                int idx = y * W + x;
                double fr = 0, fi = 0;
                bool dv = derivative && n >= 0 && n < maxIter && got.Iter[idx] == n;
                if (dv)
                {
                    var (_, zr2, zi2) = Orbit(BigFloat.Add(pr, h, pf), pi, n);
                    fr = BigFloat.Div(BigFloat.Sub(zr2, zr, pf), h, pf).ScaleB(-unitE).ToDouble();
                    fi = BigFloat.Div(BigFloat.Sub(zi2, zi, pf), h, pf).ScaleB(-unitE).ToDouble();
                }
                lock (gate)
                {
                    total++; seen.Add(n);
                    if (got.Iter[idx] == n) eq++;
                    else if (ex.Length < 160) ex += $"({x},{y}) {got.Iter[idx]} vs {n}; ";
                    if (dv)
                    {
                        double err = Math.Sqrt(Math.Pow(got.Dr[idx] - fr, 2) + Math.Pow(got.Di[idx] - fi, 2));
                        dvChecked++;
                        if (!(err < 1e-2 * Math.Sqrt(fr * fr + fi * fi))) { dvBad++; if (ex.Length < 300) ex += $"dv({x},{y}) {got.Dr[idx]:G4},{got.Di[idx]:G4} vs {fr:G4},{fi:G4}; "; }
                    }
                }
            });
        output.WriteLine($"{name}: {eq}/{total} counts equal, {seen.Count} distinct, derivative off on {dvBad}/{dvChecked}; {got.Stats}");
        Assert.True(seen.Count >= 5, $"{name}: precondition, structure ({seen.Count} distinct counts)");
        Assert.True(eq >= total * 0.97, $"{name}: {eq}/{total} counts equal. {ex}");
        if (derivative)
        {
            Assert.True(dvChecked >= total / 2, $"{name}: precondition, escaped pixels to check ({dvChecked})");
            Assert.True(dvBad <= total / 50, $"{name}: derivative off by > 1 % on {dvBad} of {dvChecked}. {ex}");
        }
    }

    internal static readonly Step TricornF = (a, b, cr, ci, p) =>
        (BigFloat.Add(BigFloat.Sub(Sq(a, p), Sq(b, p), p), cr, p), BigFloat.Add(-BigFloat.Mul(a, b, p).ScaleB(1), ci, p));

    private void Param(string name, IPerturbationPlan plan, Step f, double gr, double gi, int zl, int maxIter, double bail = Bail, double convEps = 0)
    {
        var v = ViewAt(Misiurewicz(f, gr, gi, zl), zl);
        int unitE = -v.S0;   // the pixel in [1, 2) units
        var got = Run(plan, v, maxIter, unitE, convEps: convEps);
        Assert.True(got.Stats.ScaledSteps > 0, $"{name}: precondition, the scaled-δ prelude ran");
        Check(name, got, f, v, maxIter, unitE, bail: bail, convEps: convEps);
        if (convEps > 0) Assert.True(got.Iter.Count(n => n < 0) > 0, $"{name}: precondition, pixels converged");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1e-4)]
    public void Magnet1_Matches_Direct_Iteration(double eps)
        => Param($"Magnet 1 1e320 eps {eps}", R2Plan.Magnet1(), S1911RationalTests.Magnet1, -0.095089, -1.496534, 320, 2000, 100.0, eps);

    [Fact]
    public void Glynn_Matches_Direct_Iteration()
    {
        // z^1.5 − 0.2 on the dynamic plane, at its repelling fixed point (real, ~1.3247). 1e300: just past
        // the prelude's 1e286 (the BigFloat pow oracle is slow; pixels need ~1300 steps to leave the view).
        const int zl = 300;
        // z^1.5 = exp(1.5·Log z) = z·√z with the principal root (√z = exp(½·Log z)): √z = sr + i·si,
        // sr = ((|z| + x) / 2)^½, si = sign(y)·((|z| − x) / 2)^½ (sign(0) = +: arg = π on the cut). The
        // smaller of the two is taken as y / (2·the larger): |z| − x cancels by the real axis, where
        // this view sits. Real square roots only, much cheaper than BigFloatMath.CPow's exp / log.
        Step f = (a, b, cr, ci, p) =>
        {
            var mod = BigFloatMath.Sqrt(BigFloat.Add(Sq(a, p), Sq(b, p), p), p);
            BigFloat sr, si;
            if (a.Sign >= 0)
            {
                sr = BigFloatMath.Sqrt(BigFloat.Add(mod, a, p).ScaleB(-1), p);
                si = sr.IsZero ? BigFloat.Zero(p) : BigFloat.Div(b, sr.ScaleB(1), p);
            }
            else
            {
                si = BigFloatMath.Sqrt(BigFloat.Sub(mod, a, p).ScaleB(-1), p);
                if (b.Sign < 0) si = -si;
                sr = BigFloat.Div(b, si.ScaleB(1), p);
            }
            var x = BigFloat.Sub(BigFloat.Mul(a, sr, p), BigFloat.Mul(b, si, p), p);
            var y = BigFloat.Add(BigFloat.Mul(a, si, p), BigFloat.Mul(b, sr, p), p);
            return (BigFloat.Add(x, BigFloat.FromDouble(-0.2, p), p), y);
        };
        int p = BigFloat.PrecisionForZoom(zl) + 64;
        var (zr, zi) = Newton((a, b) =>
        {
            var (fr, fi) = f(a, b, a, b, p);
            return (BigFloat.Sub(fr, a, p), BigFloat.Sub(fi, b, p));
        }, 1.3247391249176088, 0, p, (long)(zl * 3.33) + 40);
        var v = ViewAt(new ExactCentre(zr, zi), zl);
        int unitE = -v.S0;
        var got = Run(R2Plan.Glynn(-0.2, 0), v, 2000, unitE);
        Assert.True(got.Stats.ScaledSteps > 0, "precondition: the scaled-δ prelude ran");
        Check($"Glynn 1e{zl}", got, f, v, 2000, unitE, dynamicC: (0, 0), bail: 4.0);
        // The prelude's escape step at the reference's end: without it ~1/3 of the pixels went to direct
        // BigFloat iteration (497 of 1536, 5 minutes).
        Assert.True(got.Stats.DirectPixels <= W * H / 100, $"direct pixels: {got.Stats.DirectPixels}");
    }

    [Theory]
    [InlineData(320)]
    [InlineData(600)]
    public void ZSquared_Matches_Direct_Iteration(int zl)
        => Param($"z^2 + c 1e{zl}", new PowerPlan(2, true), Power(2), -0.22815549365396182, 1.1151425080399373, zl, 3000);

    [Fact]
    public void ZCubed_Matches_Direct_Iteration()
        => Param("z^3 + c 1e320", new PowerPlan(3, true), Power(3), -0.23026540906830995, 1.3276480964335278, 320, 3000);

    [Theory]
    [InlineData(320)]
    [InlineData(600)]
    public void Tricorn_Matches_Direct_Iteration(int zl)
        => Param($"Tricorn 1e{zl}", R2Plan.Tricorn(), TricornF, 0.7718445063460382, 1.336873900534257, zl, 3000);

    [Fact]
    public void Burning_Ship_Matches_Direct_Iteration()
        => Param("Burning Ship 1e320", R2Plan.BurningShip(), S1910DiffAbsTests.Ship, 0.3014809547257814, 0.4519892606922896, 320, 3000);

    [Fact]
    public void Julia_Matches_Direct_Iteration()
    {
        // Julia c = i (a dendrite); the view is its repelling fixed point z* = z*² + i.
        const int zl = 320;
        int p = BigFloat.PrecisionForZoom(zl) + 64;
        var one = BigFloat.FromDouble(1, p);
        var (zr, zi) = Newton((a, b) =>
        {
            var (fr, fi) = Power(2)(a, b, BigFloat.Zero(p), one, p);
            return (BigFloat.Sub(fr, a, p), BigFloat.Sub(fi, b, p));
        }, -0.4598, 0.3304, p, (long)(zl * 3.33) + 40);
        var v = ViewAt(new ExactCentre(zr, zi), zl);
        int unitE = -v.S0;
        var got = Run(new PowerPlan(2, false, 0, 1), v, 3000, unitE);
        Assert.True(got.Stats.ScaledSteps > 0, "precondition: the scaled-δ prelude ran");
        Check($"Julia c = i 1e{zl}", got, Power(2), v, 3000, unitE, dynamicC: (0, 1));
    }

    [Theory]
    [InlineData("z^2")]
    [InlineData("Tricorn")]
    [InlineData("Burning Ship")]
    public void The_Prelude_Forced_At_A_Double_Depth_Agrees_With_The_Double_Loop(string which)
    {
        const int zl = 60;
        var (plan, f, gr, gi) = which switch
        {
            "z^2" => ((IPerturbationPlan)new PowerPlan(2, true), Power(2), -0.22815549365396182, 1.1151425080399373),
            "Tricorn" => (R2Plan.Tricorn(), TricornF, 0.7718445063460382, 1.336873900534257),
            _ => (R2Plan.BurningShip(), S1910DiffAbsTests.Ship, 0.3014809547257814, 0.4519892606922896),
        };
        var v = ViewAt(Misiurewicz(f, gr, gi, zl), zl);
        var dbl = Run(plan, v, 2000, 0);
        var fx = Run(plan, v, 2000, 0, fxBelow: 0);
        Assert.Equal(0, dbl.Stats.ScaledSteps);
        Assert.True(fx.Stats.ScaledSteps > 0, "precondition: the prelude ran");
        int n = W * H, eq = Enumerable.Range(0, n).Count(i => dbl.Iter[i] == fx.Iter[i]);
        int dvOff = Enumerable.Range(0, n).Count(i => dbl.Iter[i] == fx.Iter[i] && dbl.Iter[i] < 2000
            && !(Math.Sqrt(Math.Pow(dbl.Dr[i] - fx.Dr[i], 2) + Math.Pow(dbl.Di[i] - fx.Di[i], 2)) <= 1e-6 * Math.Sqrt(dbl.Dr[i] * dbl.Dr[i] + dbl.Di[i] * dbl.Di[i])));
        output.WriteLine($"{which} 1e{zl}: prelude vs double {eq}/{n} counts equal, derivative off on {dvOff}; {fx.Stats}");
        Assert.True(eq >= n * 0.99, $"{which}: {eq}/{n}");
        Assert.True(dvOff <= n / 100, $"{which}: derivative off on {dvOff}");
    }

    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    public void The_Calculator_Renders_Past_Double_With_The_Derivative_In_The_Distance_Unit(int zl)
    {
        var f = Power(3);
        var m = Misiurewicz(f, -0.23026540906830995, 1.3276480964335278, zl);
        var v = ViewAt(m, zl);
        var calc = new EscapeTimeCalculator(W, H)
        {
            FractalType = FractalType.Multibrot, Quality = QualityPreset.Ultra, MaxIterations = 3000,
            ColorMap = ColorPalette.BuiltIns[0], FractalParameters = new FractalParameters { MultibrotExponent = 3 },
        };
        calc.SetDeepCentre(v.Centre.ToDeep(), v.Centre);
        calc.SetZoomLog10(zl);
        Assert.Equal(zl, calc.ZoomLog10, 9);
        calc.Calculate(CancellationToken.None);
        Assert.Equal("PT", calc.LastPrecisionLabel);
        int e = calc.DistanceUnitExp2;
        Assert.True(e > 600, $"precondition: the distance unit ({e})");
        var got = new Got(calc.IterationBuffer.Take(W * H).ToArray(),
            calc.FinalDrBuffer.Take(W * H).Select(x => (double)x).ToArray(), calc.FinalDiBuffer.Take(W * H).Select(x => (double)x).ToArray(),
            default);
        Assert.True(got.Dr.Where((d, i) => got.Iter[i] < 3000).All(double.IsFinite), "the derivative buffers are finite");
        Assert.True(calc.DistanceBuffer.Take(W * H).Where((d, i) => got.Iter[i] < 3000).Count(d => d > 0) > W * H / 4, "distances are positive");
        Check($"EscapeTimeCalculator z^3 1e{zl}", got, f, v, 3000, e);
    }

    [Fact]
    public void The_Poster_Path_Hands_Over_The_Log_Zoom()
    {
        double zl = 400.5;
        var req = new PosterRequest
        {
            FractalType = FractalType.Julia, Width = 8, Height = 8, Zoom = FracturingFog.ViewState.ZoomLog.FromLog10(zl), ZoomLog10 = zl,
            MaxIterations = 64, ColorMap = ColorPalette.BuiltIns[0], Quality = QualityPreset.Standard, FractalParameters = new FractalParameters(),
        };
        var calc = PosterRenderer.BuildCaptureCalculator(req);
        var dz = Assert.IsAssignableFrom<IDeepZoom>(calc);
        Assert.Equal(zl, dz.ZoomLog10, 9);
    }

    [Fact]
    public void The_Covered_Families_Have_No_Limit()
    {
        foreach (var t in new[] { FractalType.Julia, FractalType.Multibrot, FractalType.Tricorn, FractalType.BurningShip,
                                  FractalType.Magnet1, FractalType.Magnet2, FractalType.Glynn,
                                  FractalType.Phoenix, FractalType.Spider })   // #1957
            Assert.Equal(QualityPreset.Deepest.ZoomMax, FamilyDepth.MaxZoomFor(t));
    }
}
