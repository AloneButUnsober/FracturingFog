// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1911 (epic #1253, D6) — rational formulas past double: R2Plan's quotient difference
// (ΔP·Q − P·ΔQ) / (Q·(Q + ΔQ)), the pole test (a pixel whose Q + ΔQ cancels Q goes to another
// reference or to direct iteration), and the engine's convergence stop (Magnet's z = 1). Each check
// against an oracle outside the plan:
//   • the compiled δ of a quotient against f(Z + δ) − f(Z) in BigFloat, for δ down to 1e-14·|Z| (the
//     naive difference of two quotients loses those digits);
//   • the pole test against |Q + ΔQ|² / |Q|² computed by hand;
//   • Magnet 1 and 2 against direct BigFloat iteration written out here, with both stops (escape at
//     |z|² ≥ 100, convergence |z − 1| ≤ ε) and the kind of stop compared too. Views are Misiurewicz
//     points (S1909's Newton, generalised to a cycle of any period): Magnet 1 lands on a repelling
//     fixed point, Magnet 2 on a repelling 2-cycle. The view must be above the chaotic floor;
//   • a user "/" formula (Magnet 1 typed in) through the plan factory;
//   • the calculators: Magnet 1 / 2 switch to the engine ("PT") at 1e11 and agree with their double
//     paths, convergence colouring included.
// Mutants (by hand):
//   • caught: the naive difference, the quotient tangent, the convergence stop;
//   • survive:
//     – the engine's pole check: no render view here triggers it, and views near a pole escape on
//       the next step, so only the unit test above covers the test itself;
//     – the near-target BLA cutoff: Magnet's z = 1 is superattracting (A → 0), so the radius there
//       is already ~0.

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

public sealed class S1911RationalTests(ITestOutputHelper output)
{
    // ── BigFloat complex helpers and the Magnet steps written out ──

    private static (BigFloat, BigFloat) M(BigFloat a, BigFloat b, BigFloat c, BigFloat d, int p)
        => (BigFloat.Sub(BigFloat.Mul(a, c, p), BigFloat.Mul(b, d, p), p), BigFloat.Add(BigFloat.Mul(a, d, p), BigFloat.Mul(b, c, p), p));

    private static (BigFloat, BigFloat) Dv(BigFloat a, BigFloat b, BigFloat c, BigFloat d, int p)
    {
        var den = BigFloat.Add(BigFloat.Mul(c, c, p), BigFloat.Mul(d, d, p), p);
        return (BigFloat.Div(BigFloat.Add(BigFloat.Mul(a, c, p), BigFloat.Mul(b, d, p), p), den, p),
                BigFloat.Div(BigFloat.Sub(BigFloat.Mul(b, c, p), BigFloat.Mul(a, d, p), p), den, p));
    }

    private static BigFloat K(double v, int p) => BigFloat.FromDouble(v, p);

    // ((z² + c − 1) / (2z + c − 2))²
    private static readonly Step Magnet1 = (zr, zi, cr, ci, p) =>
    {
        var (sr, si) = M(zr, zi, zr, zi, p);
        var nr = BigFloat.Sub(BigFloat.Add(sr, cr, p), K(1, p), p); var ni = BigFloat.Add(si, ci, p);
        var dr = BigFloat.Sub(BigFloat.Add(zr.ScaleB(1), cr, p), K(2, p), p); var di = BigFloat.Add(zi.ScaleB(1), ci, p);
        var (gr, gi) = Dv(nr, ni, dr, di, p);
        return M(gr, gi, gr, gi, p);
    };

    // ((z³ + 3(c − 1)z + (c − 1)(c − 2)) / (3z² + 3(c − 2)z + c² − 3c + 3))²
    private static readonly Step Magnet2 = (zr, zi, cr, ci, p) =>
    {
        var ar = BigFloat.Sub(cr, K(1, p), p); var br = BigFloat.Sub(cr, K(2, p), p);
        var (z2r, z2i) = M(zr, zi, zr, zi, p); var (z3r, z3i) = M(z2r, z2i, zr, zi, p);
        var (azr, azi) = M(ar, ci, zr, zi, p); var (bzr, bzi) = M(br, ci, zr, zi, p);
        var (abr, abi) = M(ar, ci, br, ci, p);
        var (c2r, c2i) = M(cr, ci, cr, ci, p);
        var three = K(3, p);
        var nr = BigFloat.Add(BigFloat.Add(z3r, BigFloat.Mul(three, azr, p), p), abr, p);
        var ni = BigFloat.Add(BigFloat.Add(z3i, BigFloat.Mul(three, azi, p), p), abi, p);
        var dr = BigFloat.Add(BigFloat.Add(BigFloat.Mul(three, z2r, p), BigFloat.Mul(three, bzr, p), p),
                              BigFloat.Add(BigFloat.Sub(c2r, BigFloat.Mul(three, cr, p), p), three, p), p);
        var di = BigFloat.Add(BigFloat.Add(BigFloat.Mul(three, z2i, p), BigFloat.Mul(three, bzi, p), p),
                              BigFloat.Sub(c2i, BigFloat.Mul(three, ci, p), p), p);
        var (gr, gi) = Dv(nr, ni, dr, di, p);
        return M(gr, gi, gr, gi, p);
    };

    // Iterations to a stop: escape (|z|² ≥ bailout2) → kind 1, convergence (|z − 1| ≤ eps) → kind 2,
    // neither by maxIter → kind 0.
    private static (int N, int Kind) Orbit2(Step f, BigFloat cr, BigFloat ci, int p, int maxIter, double bailout2, double eps)
    {
        BigFloat zr = BigFloat.Zero(p), zi = BigFloat.Zero(p);
        for (int n = 0; n < maxIter; n++)
        {
            double a = zr.ToDouble(), b = zi.ToDouble();
            if (a * a + b * b >= bailout2) return (n, 1);
            if (eps > 0 && (a - 1) * (a - 1) + b * b <= eps * eps) return (n, 2);
            (zr, zi) = f(zr, zi, cr, ci, p);
        }
        return (maxIter, 0);
    }

    private sealed record Got(int[] Iter, int[] Kind, PerturbationStats Stats);

    private static Got Engine2(IPerturbationPlan plan, ExactCentre centre, double scale, int maxIter, double eps, bool useBla = true)
    {
        var it = new int[W * H]; var kind = new int[W * H];
        var conv = eps > 0 ? new PerturbationEngine.Convergence(1, 0, eps, (idx, n, zr, zi, dp) => { it[idx] = n; kind[idx] = 2; }) : null;
        var st = PerturbationEngine.Render(plan, centre, scale, W, H, maxIter,
            (idx, n, zr, zi, a, b) => { it[idx] = n; kind[idx] = n >= maxIter ? 0 : 1; }, CancellationToken.None,
            useBla: useBla, converge: conv);
        return new Got(it, kind, st);
    }

    private void CheckStops(string name, Got got, Step f, ExactCentre centre, double scale, int maxIter, double bailout2, double eps)
    {
        int p = centre.Precision + 96, total = 0, eq = 0, stable = 0;
        var seen = new System.Collections.Generic.HashSet<(int, int)>();
        var kinds = new int[3];
        string ex = "";
        for (int y = 1; y < H; y += 3)
            for (int x = 1; x < W; x += 3)
            {
                var cr = BigFloat.Add(centre.Re.WithPrecision(p), BigFloat.FromDouble((x - W * 0.5) * scale, p), p);
                var ci = BigFloat.Add(centre.Im.WithPrecision(p), BigFloat.FromDouble((y - H * 0.5) * scale, p), p);
                var o = Orbit2(f, cr, ci, p, maxIter, bailout2, eps);
                var o2 = Orbit2(f, BigFloat.Add(cr, BigFloat.FromDouble(scale * 1e-12, p), p), ci, p, maxIter, bailout2, eps);
                if (o == o2) stable++;
                int idx = y * W + x;
                total++; seen.Add(o); kinds[o.Kind]++;
                if (got.Iter[idx] == o.N && got.Kind[idx] == o.Kind) eq++;
                else if (ex.Length < 200) ex += $"({x},{y}) {got.Iter[idx]}/{got.Kind[idx]} vs {o.N}/{o.Kind}; ";
            }
        output.WriteLine($"{name}: {eq}/{total} equal (count and stop), {seen.Count} distinct, kinds in/esc/conv {string.Join("/", kinds)}, {stable} stable; {got.Stats}");
        Assert.True(stable == total, $"{name}: precondition, the view is above the chaotic floor ({stable}/{total})");
        Assert.True(seen.Count >= 5, $"{name}: precondition, structure ({seen.Count})");
        Assert.True(eq >= total * 0.97, $"{name}: {eq}/{total}. {ex}");
    }

    // ── the quotient difference and the pole test ──

    [Fact]
    public void The_Quotient_Difference_Keeps_Small_Deltas()
    {
        // f = (z² + c) / (z + 2): δ′ = f(Z + δ, C) − f(Z, C) against BigFloat, for |δ| from 1e-2 to
        // 1e-14 of |Z|. The naive (P + ΔP)/(Q + ΔQ) − P/Q in double keeps ~16 − k digits at 10^-k.
        var plan = (R2Plan)AstPlan.TryCreateAny(CalcGenLowering.ParseAndLower("(z^2 + c)/(z + 2)"), 1e6, out string? why)!;
        Assert.True(plan.HasPoles, why);
        var rng = new Random(1911);
        double worst = 0;
        for (int k = 0; k < 600; k++)
        {
            double Zr = rng.NextDouble() * 2 - 1, Zi = rng.NextDouble() * 2 - 1, Cr = 0.3, Ci = -0.2;
            double mag = Math.Pow(10, -2 - 12 * rng.NextDouble());
            double dr0 = (rng.NextDouble() - 0.5) * mag, di0 = (rng.NextDouble() - 0.5) * mag;
            double dr = dr0, di = di0;
            plan.DeltaStep(Zr, Zi, Cr, Ci, ref dr, ref di, 0, 0);
            const int p = 300;
            BigFloat F(double a, double b, out BigFloat im)
            {
                var (sr, si) = M(K(a, p), K(b, p), K(a, p), K(b, p), p);
                var (qr, qi) = Dv(BigFloat.Add(sr, K(Cr, p), p), BigFloat.Add(si, K(Ci, p), p), BigFloat.Add(K(a, p), K(2, p), p), K(b, p), p);
                im = qi; return qr;
            }
            var w1r = F(Zr, Zi, out var w1i);
            var zr2 = BigFloat.Add(K(Zr, p), K(dr0, p), p); var zi2 = BigFloat.Add(K(Zi, p), K(di0, p), p);
            var (sr2, si2) = M(zr2, zi2, zr2, zi2, p);
            var (w2r, w2i) = Dv(BigFloat.Add(sr2, K(Cr, p), p), BigFloat.Add(si2, K(Ci, p), p), BigFloat.Add(zr2, K(2, p), p), zi2, p);
            double wr = BigFloat.Sub(w2r, w1r, p).ToDouble(), wi = BigFloat.Sub(w2i, w1i, p).ToDouble();
            double rel = Math.Sqrt((dr - wr) * (dr - wr) + (di - wi) * (di - wi)) / Math.Sqrt(wr * wr + wi * wi);
            worst = Math.Max(worst, rel);
        }
        output.WriteLine($"quotient difference: worst relative error {worst:E2} over |δ| in [1e-14, 1e-2]·|Z|");
        Assert.True(worst < 1e-13, $"worst relative error {worst}");
    }

    [Fact]
    public void A_Pixel_By_A_Pole_The_Reference_Is_Not_By_Is_Flagged()
    {
        // Magnet 1's denominator Q = 2z + c − 2 (pole at z = 0.5 for c = 1): at Z = 0.25, Q = −0.5; a
        // pixel δ = 0.2499 has Q + ΔQ = −2e-4, so the ratio² is (2e-4 / 0.5)² = 1.6e-7.
        var plan = R2Plan.Magnet1();
        Assert.True(plan.HasPoles);
        double far = plan.PoleRatio2(0.25, 0, 1, 0, 0.1, 0, 0, 0);
        double near = plan.PoleRatio2(0.25, 0, 1, 0, 0.2499, 0, 0, 0);
        Assert.Equal(Math.Pow((2 * 0.35 + 1 - 2) / (2 * 0.25 + 1 - 2), 2), far, 12);
        Assert.Equal(Math.Pow(2e-4 / 0.5, 2), near, 9);
        Assert.True(near < PerturbationEngine.GlitchTolerance2 && far > PerturbationEngine.GlitchTolerance2);
        Assert.False(R2Plan.Tricorn().HasPoles);
    }

    // ── the families against direct iteration ──

    private void Run(string name, IPerturbationPlan plan, Step f, double gr, double gi, int pre, int period, int zl, double bailout2, double eps)
    {
        double scale = 3.5 / W / Math.Pow(10, zl);
        var centre = Off(Misiurewicz(f, gr, gi, zl, pre, period), scale, zl);
        const int maxIter = 2000;
        var got = Engine2(plan, centre, scale, maxIter, eps);
        Assert.True(got.Stats.BlaSkips > 0, $"{name}: precondition, the matrix BLA skipped");
        CheckStops(name, got, f, centre, scale, maxIter, bailout2, eps);
        CheckStops(name + ", no BLA", Engine2(plan, centre, scale, maxIter, eps, useBla: false), f, centre, scale, maxIter, bailout2, eps);
    }

    [Theory]
    [InlineData(40, 1e-4)]
    [InlineData(100, 1e-4)]
    [InlineData(40, 0.0)]   // escape only (MagnetConvergence off)
    public void Magnet1_Matches_Direct_Iteration(int zl, double eps)
        => Run($"Magnet 1 1e{zl} eps {eps}", R2Plan.Magnet1(), Magnet1, -0.095089, -1.496534, 3, 1, zl, 100.0, eps);

    [Theory]
    [InlineData(40)]
    [InlineData(100)]
    public void Magnet2_Matches_Direct_Iteration(int zl)
        => Run($"Magnet 2 1e{zl}", R2Plan.Magnet2(), Magnet2, -0.19515756577670856, -0.5295859425867369, 2, 2, zl, 100.0, 1e-4);

    [Fact]
    public void A_User_Quotient_Matches_Direct_Iteration()
    {
        var plan = AstPlan.TryCreateAny(CalcGenLowering.ParseAndLower("((z^2 + c - 1)/(2*z + c - 2))^2"), 100.0, out string? why);
        Assert.True(plan is R2Plan, why);
        Run("user Magnet 1 1e40", plan!, Magnet1, -0.095089, -1.496534, 3, 1, 40, 100.0, 0.0);
    }

    // ── through the calculator ──

    [Theory]
    [InlineData(FractalType.Magnet1)]
    [InlineData(FractalType.Magnet2)]
    public void Magnet_Switches_To_The_Engine_And_Agrees_At_The_Switch(FractalType t)
    {
        // The refined Misiurewicz point (a 6-digit guess is a flat view at 1e11), a third of a pixel off.
        double sc = 3.5 / W / EscapeTimeCalculator.DeepZoomThreshold;
        var at = Off(t == FractalType.Magnet1 ? Misiurewicz(Magnet1, -0.095089, -1.496534, 20)
                                              : Misiurewicz(Magnet2, -0.19515756577670856, -0.5295859425867369, 20, 2, 2), sc, 20);
        EscapeTimeCalculator Go(bool engine)
        {
            var c = new EscapeTimeCalculator(W, H)
            {
                FractalType = t, Zoom = EscapeTimeCalculator.DeepZoomThreshold * 1.0001, MaxIterations = 1500,
                ColorMap = ColorPalette.BuiltIns[0], AllowPerturbation = engine, FractalParameters = new FractalParameters(),
            };
            c.SetDeepCentre(at.ToDeep(), at);
            c.Calculate(CancellationToken.None);
            return c;
        }
        var sp = Go(false); var pt = Go(true);
        Assert.True(new FractalParameters().MagnetConvergence, "precondition: the default colours convergence");
        Assert.Equal("SP", sp.LastPrecisionLabel);
        Assert.Equal("PT", pt.LastPrecisionLabel);
        int eq = Enumerable.Range(0, W * H).Count(i => sp.IterationBuffer[i] == pt.IterationBuffer[i]);
        int col = Enumerable.Range(0, W * H).Count(i => sp.ColorBuffer[i] == pt.ColorBuffer[i]);
        int distinct = sp.IterationBuffer.Take(W * H).Distinct().Count();
        output.WriteLine($"{t} at 1e11: engine vs SP {eq}/{W * H} counts, {col} colours equal, {distinct} distinct; {pt.LastPerturbationStats}");
        Assert.True(distinct >= 5, $"precondition: structure ({distinct})");
        Assert.True(eq >= W * H * 0.9, $"{eq}/{W * H} equal at the switch");
        Assert.True(col >= W * H * 0.9, $"{col}/{W * H} colours equal at the switch");
        Assert.Equal(FamilyDepth.EngineDoubleDeltaMaxZoom, FamilyDepth.MaxZoomFor(t));
    }
}
