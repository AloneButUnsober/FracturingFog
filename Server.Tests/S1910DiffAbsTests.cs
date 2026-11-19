// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1910 (epic #1253, D5) — abs folds past double: R2Plan's diffabs differences (laser blaster's case
// analysis, as given by mathr 2021) and the BLA radius bounded by the folds' sign changes (mathr 2022's
// min(|X|, |Y|) / 2 for Burning Ship). Each check against an oracle outside the plan:
//   • the compiled δ of a fold against |X + x| − |X| in BigFloat, on inputs that hit all four cases;
//   • Burning Ship's level-0 radius against mathr 2022's min(|X|, |Y|) / 2;
//   • Burning Ship, Celtic and Perpendicular Burning Ship against direct BigFloat iteration written
//     out here (S1909's oracle and Misiurewicz views, z₄ = z₃ by Newton), with a precondition that
//     pixels' orbits cross the reference's sign (the fold's case path runs) and one that the view is
//     above the chaotic floor (direct counts stable under a 1e-12-pixel shift);
//   • the calculators: Burning Ship and a user Burning Ship switch to the engine ("PT") at 1e11 and
//     agree with their double paths; abs of a complex value (the modulus) stays outside the plan.
// Mutants (by hand), all caught:
//   • each of the four diffabs cases;
//   • the derivative's kink sign;
//   • the fold limit removed. Only the radius test catches this one: on these views the second-order
//     radius binds first.

using System;
using System.Linq;
using System.Threading;
using FracturingFog;
using FracturingFog.Abstractions.Explore;
using FracturingFog.FFMath;
using FracturingFog.Models;
using FracturingFog.Perturbation;
using Xunit;
using static FracturingFog.Server.Tests.S1909ConjR2Tests;

namespace FracturingFog.Server.Tests;

public sealed class S1910DiffAbsTests(ITestOutputHelper output)
{
    private static BigFloat Abs(BigFloat a) => a.Sign < 0 ? -a : a;

    // (|x| + i|y|)² + c
    private static readonly Step Ship = (a, b, cr, ci, p) =>
    {
        var x = Abs(a); var y = Abs(b);
        return (BigFloat.Add(BigFloat.Sub(Sq(x, p), Sq(y, p), p), cr, p), BigFloat.Add(BigFloat.Mul(x, y, p).ScaleB(1), ci, p));
    };

    // |Re z²| + i·Im z² + c
    private static readonly Step Celtic = (a, b, cr, ci, p) =>
        (BigFloat.Add(Abs(BigFloat.Sub(Sq(a, p), Sq(b, p), p)), cr, p), BigFloat.Add(BigFloat.Mul(a, b, p).ScaleB(1), ci, p));

    // (|x| − i·y)² + c = x² − y² + i·(−2|x|·y) + c
    private static readonly Step Perp = (a, b, cr, ci, p) =>
        (BigFloat.Add(BigFloat.Sub(Sq(a, p), Sq(b, p), p), cr, p), BigFloat.Add(-BigFloat.Mul(Abs(a), b, p).ScaleB(1), ci, p));

    private static R2Plan R2(string eq, double bailout2 = 1024.0)
    {
        var p = AstPlan.TryCreateAny(CalcGenLowering.ParseAndLower(eq), bailout2, out string? why);
        Assert.True(p is R2Plan, $"{eq}: {why}");
        return (R2Plan)p!;
    }

    [Fact]
    public void The_Fold_Difference_Is_Exact_In_All_Four_Cases()
    {
        // δ′ of "abs(re(z)) + c" at reference Z = (X, 0) is |X + x| − |X| (+ ε, here 0).
        var plan = R2("abs(re(z)) + c");
        var rng = new Random(1910);
        var cases = new int[4];
        for (int k = 0; k < 4000; k++)
        {
            double X = (rng.NextDouble() - 0.5) * Math.Pow(10, rng.Next(-8, 4));
            double x = (rng.NextDouble() - 0.5) * Math.Abs(X) * (rng.Next(2) == 0 ? 0.5 : 5.0);
            cases[(X >= 0 ? 0 : 2) + (X + x >= 0 ? 0 : 1)]++;
            double dr = x, di = 0;
            plan.DeltaStep(X, 0, 0, 0, ref dr, ref di, 0, 0);
            var bx = BigFloat.FromDouble(X, 200); var bxx = BigFloat.Add(bx, BigFloat.FromDouble(x, 200), 200);
            double want = BigFloat.Sub(Abs(bxx), Abs(bx), 200).ToDouble();
            Assert.True(Math.Abs(dr - want) <= 4e-16 * Math.Max(Math.Abs(x), Math.Abs(X)), $"X {X}, x {x}: {dr} vs {want}");
            Assert.Equal(0.0, di);
        }
        output.WriteLine($"cases (X≥0, X+x≥0), (X≥0, <0), (X<0, ≥0), (X<0, <0): {string.Join(", ", cases)}");
        Assert.All(cases, n => Assert.True(n > 100, "precondition: every case hit"));
    }

    [Fact]
    public void Burning_Ships_Radius_Is_Half_The_Distance_To_An_Axis()
    {
        var plan = R2Plan.BurningShip();
        Assert.True(plan.CanRebase);
        var rng = new Random(5);
        for (int k = 0; k < 500; k++)
        {
            double X = (rng.NextDouble() - 0.5) * 4, Y = (rng.NextDouble() - 0.5) * 4;
            // ε = 1: the second-order bound is far looser than the folds' (mathr 2022's rule binds).
            var b = plan.Level0R2(X, Y, 0.1, -0.2, 1.0, 0.0);
            double want = 0.5 * Math.Min(Math.Abs(X), Math.Abs(Y));
            Assert.True(Math.Abs(Math.Sqrt(b.R2) - want) <= 1e-12 * Math.Max(1, want), $"Z ({X}, {Y}): r {Math.Sqrt(b.R2)} vs {want}");
            // The pixel's |ε| does not move z's own components at level 0 (c is added after the folds).
            Assert.Equal(b.R2, plan.Level0R2(X, Y, 0.1, -0.2, 1.0, 1e-3).R2);
        }
        // A fold of a c-dependent value, |Re(z + c)| (the i·Im z term keeps A invertible); its room
        // shrinks by |ε|max. At ε = 1 the second-order radius here is 0.5, so the fold binds.
        var q = R2("abs(re(z + c))^2 + i*im(z) + c");
        double r0 = Math.Sqrt(q.Level0R2(0.3, 0.1, 0.2, 0, 1.0, 0.0).R2), r1 = Math.Sqrt(q.Level0R2(0.3, 0.1, 0.2, 0, 1.0, 0.1).R2);
        Assert.Equal(0.25, r0, 12);   // ½·|0.3 + 0.2|
        Assert.Equal(0.20, r1, 12);   // ½·(0.5 − 0.1)
    }

    // Pixels whose orbit has a component of the other sign from the centre's at the same iteration
    // (before either escapes): the fold's case analysis is what keeps them right.
    private static int SignCrossers(Step f, ExactCentre centre, double scale, int maxIter, double bailout2)
    {
        int p = centre.Precision, crossers = 0;
        double[] cR = new double[maxIter + 1], cI = new double[maxIter + 1];
        BigFloat zr = BigFloat.Zero(p), zi = BigFloat.Zero(p);
        int cl = 0;
        for (; cl < maxIter; cl++)
        {
            cR[cl] = zr.ToDouble(); cI[cl] = zi.ToDouble();
            if (cR[cl] * cR[cl] + cI[cl] * cI[cl] >= bailout2) break;
            (zr, zi) = f(zr, zi, centre.Re, centre.Im, p);
        }
        for (int y = 1; y < H; y += 3)
            for (int x = 1; x < W; x += 3)
            {
                var cr = BigFloat.Add(centre.Re, BigFloat.FromDouble((x - W * 0.5) * scale, p), p);
                var ci = BigFloat.Add(centre.Im, BigFloat.FromDouble((y - H * 0.5) * scale, p), p);
                BigFloat ar = BigFloat.Zero(p), ai = BigFloat.Zero(p);
                for (int n = 0; n < cl; n++)
                {
                    double a = ar.ToDouble(), b = ai.ToDouble();
                    if (a * a + b * b >= bailout2) break;
                    if (Math.Sign(a) * Math.Sign(cR[n]) < 0 || Math.Sign(b) * Math.Sign(cI[n]) < 0) { crossers++; break; }
                    (ar, ai) = f(ar, ai, cr, ci, p);
                }
            }
        return crossers;
    }

    // Sampled pixels whose direct count is unchanged by a 1e-12-pixel shift. A view where many change is
    // at the chaotic floor: no double-δ method can match it (#1910 found one, −0.3637 − 1.0501i, 144/176).
    private static int Stable(Step f, ExactCentre centre, double scale, int maxIter, double bailout2)
    {
        int p = centre.Precision + 96, same = 0;
        for (int y = 1; y < H; y += 3)
            for (int x = 1; x < W; x += 3)
            {
                var cr = BigFloat.Add(centre.Re.WithPrecision(p), BigFloat.FromDouble((x - W * 0.5) * scale, p), p);
                var ci = BigFloat.Add(centre.Im.WithPrecision(p), BigFloat.FromDouble((y - H * 0.5) * scale, p), p);
                int n1 = Orbit(f, cr, ci, p, maxIter, bailout2).N;
                int n2 = Orbit(f, BigFloat.Add(cr, BigFloat.FromDouble(scale * 1e-12, p), p), ci, p, maxIter, bailout2).N;
                if (n1 == n2) same++;
            }
        return same;
    }

    private void Run(string name, IPerturbationPlan plan, Step f, double bailout2, double gr, double gi, int zl, int maxIter, bool blaOff = false)
    {
        double scale = 3.5 / W / Math.Pow(10, zl);
        var centre = Off(Misiurewicz(f, gr, gi, zl), scale, zl);
        int stable = Stable(f, centre, scale, maxIter, bailout2);
        // All 176: at −0.2282 − 1.1151i (1e30) 2 pixels moved even under 1e-15 px, BLA-off reached that floor
        // (174/176) and BLA at ε = 1e-10 lost 6 more (ε 1e-14: back to 174) — the #1613 / #1275 tolerance trade
        // on a chaotic view, not the folds (a 10x tighter fold limit changed nothing).
        Assert.True(stable == 176, $"{name}: precondition, the view is above the chaotic floor ({stable}/176 stable)");
        int crossers = SignCrossers(f, centre, scale, maxIter, bailout2);
        output.WriteLine($"{name}: {crossers} of 176 sampled pixels cross the centre orbit's sign");
        Assert.True(crossers >= 20, $"{name}: precondition, pixels on the fold's other side ({crossers})");
        var got = Engine(plan, centre, scale, maxIter);
        Assert.True(got.Stats.BlaSkips > 0, $"{name}: precondition, the matrix BLA skipped");
        Check(output, name, got, f, bailout2, centre, scale, maxIter);
        if (blaOff) Check(output, name + ", no BLA", Engine(plan, centre, scale, maxIter, useBla: false), f, bailout2, centre, scale, maxIter);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(120)]
    public void Burning_Ship_Matches_Direct_Iteration(int zl)
        => Run($"Burning Ship 1e{zl}", R2Plan.BurningShip(), Ship, 512.0 * 512.0, 0.3014809547257814, 0.4519892606922896, zl, 2000, blaOff: true);

    [Fact]
    public void A_User_Burning_Ship_Matches_Direct_Iteration()
        => Run("user Burning Ship 1e40", R2("(abs(re(z)) + i*abs(im(z)))^2 + c"), Ship, 1024.0, 0.3014809547257814, 0.4519892606922896, 40, 2000);

    [Fact]
    public void Celtic_Matches_Direct_Iteration()
        => Run("Celtic 1e40", R2("abs(re(z^2)) + i*im(z^2) + c"), Celtic, 1024.0, -1.306015631763545, -0.7677848115491253, 40, 2000);

    [Fact]
    public void Perpendicular_Burning_Ship_Matches_Direct_Iteration()
        => Run("Perpendicular 1e40", R2("(abs(re(z)) - i*im(z))^2 + c"), Perp, 1024.0, 0.3126962509968717, 1.1158877876331998, 40, 2000);

    [Fact]
    public void Abs_Of_A_Complex_Value_Is_The_Modulus_And_Stays_Outside()
    {
        Assert.Null(AstPlan.TryCreateAny(CalcGenLowering.ParseAndLower("abs(z)^2 + c"), 1024.0, out string? why));
        Assert.Contains("modulus", why);
        Assert.IsType<R2Plan>(AstPlan.TryCreateAny(CalcGenLowering.ParseAndLower("z^2 + abs(re(z)) + c"), 1024.0, out _));
    }

    // ── through the calculators ──

    [Fact]
    public void Burning_Ship_Switches_To_The_Engine_And_Agrees_At_The_Switch()
    {
        EscapeTimeCalculator Go(bool engine)
        {
            var c = new EscapeTimeCalculator(W, H)
            {
                FractalType = FractalType.BurningShip, Zoom = EscapeTimeCalculator.DeepZoomThreshold * 1.0001, MaxIterations = 1500,
                ColorMap = ColorPalette.BuiltIns[0], AllowPerturbation = engine, FractalParameters = new FractalParameters(),
            };
            c.SetDeepCentre(new DeepComplex(new OD(0.3014809547257814 + 0.37e-13), new OD(0.4519892606922896)), null);
            c.Calculate(CancellationToken.None);
            return c;
        }
        var sp = Go(false); var pt = Go(true);
        Assert.Equal("SP", sp.LastPrecisionLabel);
        Assert.Equal("PT", pt.LastPrecisionLabel);
        int eq = Enumerable.Range(0, W * H).Count(i => sp.IterationBuffer[i] == pt.IterationBuffer[i]);
        int distinct = sp.IterationBuffer.Take(W * H).Distinct().Count();
        output.WriteLine($"Burning Ship at 1e11: engine vs SP {eq}/{W * H} equal, {distinct} distinct; {pt.LastPerturbationStats}");
        Assert.True(distinct >= 5, $"precondition: structure ({distinct})");
        Assert.True(eq >= W * H * 0.9, $"{eq}/{W * H} equal at the switch");
        Assert.Equal(FamilyDepth.EngineDoubleDeltaMaxZoom, FamilyDepth.MaxZoomFor(FractalType.BurningShip));
    }

    [Fact]
    public void A_User_Burning_Ship_Switches_To_The_Engine_And_Agrees_At_The_Switch()
    {
        UserEquationCalculator Go(bool engine)
        {
            var c = new UserEquationCalculator(W, H)
            {
                Zoom = EscapeTimeCalculator.DeepZoomThreshold * 1.0001, MaxIterations = 1500, ColorMap = ColorPalette.BuiltIns[0],
                AllowPerturbation = engine,
                FractalParameters = new FractalParameters { UserEquationSource = "(abs(re(z)) + i*abs(im(z)))^2 + c" },
            };
            var at = ExactCentre.FromDeep(new DeepComplex(new OD(0.3014809547257814 + 0.37e-13), new OD(0.4519892606922896)), 64);
            c.SetDeepCentre(at.ToDeep(), at);
            c.Calculate(CancellationToken.None);
            return c;
        }
        var interp = Go(false); var pt = Go(true);
        Assert.Equal("SP", interp.LastPrecisionLabel);
        Assert.True(pt.LastPrecisionLabel == "PT", $"stayed on the interpreter ({pt.DeepRefusal})");
        int eq = Enumerable.Range(0, W * H).Count(i => interp.IterationBuffer[i] == pt.IterationBuffer[i]);
        int distinct = interp.IterationBuffer.Take(W * H).Distinct().Count();
        output.WriteLine($"user Burning Ship at 1e11: engine vs interpreter {eq}/{W * H} equal, {distinct} distinct; {pt.LastPerturbationStats}");
        Assert.True(distinct >= 5, $"precondition: structure ({distinct})");
        Assert.True(eq >= W * H * 0.9, $"{eq}/{W * H} equal at the switch");
    }
}
