// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1909 (epic #1253, D4) — formulas that are not complex-analytic past double: R2Plan (difference
// arithmetic over conj / re / im, the 2×2 matrix BLA, the orbit's 2×2 Jacobian) on PerturbationEngine.
// Each check against an oracle that does not go through the AST or the plan:
//   • RealMat2's norms against the singular values computed another way, and a merged Bla2 against
//     applying its two halves in turn;
//   • "z^2 + c" forced onto an R2Plan against MandelbrotCalculator at p448 2e33 (the matrix BLA and the
//     R² pixel loop on a view whose answer is known);
//   • Tricorn against direct BigFloat iteration written out here, at 1 + i√3, the tip of an arm: 0 → c
//     → −c, a repelling fixed point (conj(c)² = −2c there), so the view has structure at any depth;
//     1e40 and 1e120, BLA on and off. The derivative against a finite difference of the direct orbit
//     (∂z/∂Re c, the column the sink gets);
//   • Tricorn and user formulas through the plan factory, conj(z)³ + c and z² + 0.3·conj(z) + c
//     (∂f/∂z ≠ 0 at z = 0: no rebase, the glitch test), each at a Misiurewicz point (z₄ = z₃, by
//     Newton in BigFloat); re / im in a formula in place of conj;
//   • the calculators: Tricorn and a user conj formula switch to the engine ("PT") at 1e11 and agree
//     with their double paths there.
// Mutants (by hand), all caught except the last:
//   • a product's D₁·D₂ term dropped;
//   • the BLA radius ×1e6;
//   • the merge's + B dropped, and its A in the wrong order;
//   • the skip's Jacobian + B dropped;
//   • the Jacobian step's ∂f/∂c dropped;
//   • the rebase test removed;
//   • the glitch test removed. It survives, as in S1907 / S1908: the glitch regions are sub-pixel here,
//     and the pixels where the reference runs out still go to secondary references.

using System;
using System.Linq;
using System.Threading;
using FracturingFog;
using FracturingFog.Abstractions.Explore;
using FracturingFog.FFMath;
using FracturingFog.Models;
using FracturingFog.Perturbation;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1909ConjR2Tests(ITestOutputHelper output)
{
    private const int W = 48, H = 32;

    // ── the matrix algebra ──

    [Fact]
    public void Mat2_Norms_Are_The_Singular_Values()
    {
        var rng = new Random(1909);
        for (int k = 0; k < 200; k++)
        {
            var m = new RealMat2(rng.NextDouble() * 4 - 2, rng.NextDouble() * 4 - 2, rng.NextDouble() * 4 - 2, rng.NextDouble() * 4 - 2);
            // Oracle: the eigenvalues of the symmetric MᵀM = [[p, r], [r, q]].
            double pp = m.M11 * m.M11 + m.M21 * m.M21, qq = m.M12 * m.M12 + m.M22 * m.M22, rr = m.M11 * m.M12 + m.M21 * m.M22;
            double mid = 0.5 * (pp + qq), rad = Math.Sqrt(0.25 * (pp - qq) * (pp - qq) + rr * rr);
            double hi = Math.Sqrt(mid + rad), lo = Math.Sqrt(Math.Max(0, mid - rad));
            Assert.True(Math.Abs(m.Norm - hi) <= 1e-9 * Math.Max(1, hi), $"norm {m.Norm} vs {hi}");
            Assert.True(Math.Abs(m.MinGain - lo) <= 1e-6 * Math.Max(1, hi), $"min gain {m.MinGain} vs {lo}");
            // And neither bound is beaten by a sampled direction.
            for (int s = 0; s < 360; s++)
            {
                double t = Math.PI * s / 360, x = Math.Cos(t), y = Math.Sin(t);
                double g = Math.Sqrt(Math.Pow(m.M11 * x + m.M12 * y, 2) + Math.Pow(m.M21 * x + m.M22 * y, 2));
                Assert.True(g <= m.Norm * (1 + 1e-12) && g >= m.MinGain * (1 - 1e-9) - 1e-15, $"direction {t}: {g}");
            }
        }
        // A complex number as a matrix: both singular values are its modulus.
        var c = RealMat2.OfComplex(3, -4);
        Assert.Equal(5, c.Norm, 12); Assert.Equal(5, c.MinGain, 12);
    }

    [Fact]
    public void A_Merged_Bla2_Is_Its_Two_Halves_In_Turn()
    {
        var rng = new Random(7);
        RealMat2 R() => new(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5);
        var x = new Bla2(R(), R(), 4.0, 1); var y = new Bla2(R(), R(), 9.0, 1);
        var m = Bla2Table.Merge(x, y, 0.1, 2);
        (double, double) Apply(Bla2 b, (double R, double I) d, (double R, double I) e)
            => (b.A.M11 * d.R + b.A.M12 * d.I + b.B.M11 * e.R + b.B.M12 * e.I,
                b.A.M21 * d.R + b.A.M22 * d.I + b.B.M21 * e.R + b.B.M22 * e.I);
        for (int k = 0; k < 50; k++)
        {
            var d = (rng.NextDouble() - 0.5, rng.NextDouble() - 0.5); var e = (rng.NextDouble() - 0.5, rng.NextDouble() - 0.5);
            var two = Apply(y, Apply(x, d, e), e); var one = Apply(m, d, e);
            Assert.Equal(two.Item1, one.Item1, 12); Assert.Equal(two.Item2, one.Item2, 12);
        }
        // The radius keeps x's output inside y's radius for every |ε| ≤ 0.1.
        double r = Math.Sqrt(m.R2);
        Assert.True(r <= 2.0 && x.A.Norm * r + x.B.Norm * 0.1 <= 3.0 + 1e-12, $"merged radius {r}");
    }

    // ── engine helpers ──

    private sealed record Result(int[] Iter, double[] Dr, double[] Di, PerturbationStats Stats);

    private static Result Engine(IPerturbationPlan plan, ExactCentre centre, double scale, int maxIter, bool useBla = true)
    {
        var it = new int[W * H]; var dr = new double[W * H]; var di = new double[W * H];
        var st = PerturbationEngine.Render(plan, centre, scale, W, H, maxIter,
            (idx, n, zr, zi, a, b) => { it[idx] = n; dr[idx] = a; di[idx] = b; }, CancellationToken.None, useBla: useBla);
        return new Result(it, dr, di, st);
    }

    private static R2Plan R2(string eq, double bailout2)
    {
        var p = R2Plan.TryCreate(CalcGenLowering.ParseAndLower(eq), bailout2, out string? why);
        Assert.True(p != null, $"{eq}: {why}");
        return p!;
    }

    // ── the oracle: each formula written out in BigFloat ──

    private delegate (BigFloat, BigFloat) Step(BigFloat zr, BigFloat zi, BigFloat cr, BigFloat ci, int p);

    private static BigFloat Sq(BigFloat a, int p) => BigFloat.Mul(a, a, p);

    private static readonly Step TricornStep = (a, b, cr, ci, p) =>
        (BigFloat.Add(BigFloat.Sub(Sq(a, p), Sq(b, p), p), cr, p),
         BigFloat.Add(-BigFloat.Mul(a, b, p).ScaleB(1), ci, p));

    // conj(z)³ = a³ − 3ab² + i(b³ − 3a²b)
    private static readonly Step ConjCube = (a, b, cr, ci, p) =>
    {
        var a2 = Sq(a, p); var b2 = Sq(b, p);
        var re = BigFloat.Sub(BigFloat.Mul(a2, a, p), BigFloat.Mul(BigFloat.FromDouble(3, p), BigFloat.Mul(a, b2, p), p), p);
        var im = BigFloat.Sub(BigFloat.Mul(b2, b, p), BigFloat.Mul(BigFloat.FromDouble(3, p), BigFloat.Mul(a2, b, p), p), p);
        return (BigFloat.Add(re, cr, p), BigFloat.Add(im, ci, p));
    };

    // z² + 0.3·conj(z) + c = a² − b² + 0.3a + i(2ab − 0.3b)
    private static readonly Step QuadConj = (a, b, cr, ci, p) =>
    {
        var k = BigFloat.FromDouble(0.3, p);
        var re = BigFloat.Add(BigFloat.Sub(Sq(a, p), Sq(b, p), p), BigFloat.Mul(k, a, p), p);
        var im = BigFloat.Sub(BigFloat.Mul(a, b, p).ScaleB(1), BigFloat.Mul(k, b, p), p);
        return (BigFloat.Add(re, cr, p), BigFloat.Add(im, ci, p));
    };

    // Iterations to |z|² ≥ bailout2 (maxIter: none), and z at that iteration.
    private static (int N, BigFloat Zr, BigFloat Zi) Orbit(Step f, BigFloat cr, BigFloat ci, int p, int maxIter, double bailout2, int stopAt = -1)
    {
        BigFloat zr = BigFloat.Zero(p), zi = BigFloat.Zero(p);
        for (int n = 0; n < maxIter; n++)
        {
            if (n == stopAt) return (n, zr, zi);
            double a = zr.ToDouble(), b = zi.ToDouble();
            if (stopAt < 0 && a * a + b * b >= bailout2) return (n, zr, zi);
            (zr, zi) = f(zr, zi, cr, ci, p);
        }
        return (maxIter, zr, zi);
    }

    private void Check(string name, Result got, Step f, double bailout2, ExactCentre centre, double scale, int maxIter, bool derivative = true)
    {
        int p = centre.Precision, total = 0, eq = 0, dvBad = 0, dvChecked = 0;
        var seen = new System.Collections.Generic.HashSet<int>();
        string ex = "";
        int pf = p + 96;   // the finite difference's step is 1e-9 of a pixel
        for (int y = 1; y < H; y += 3)
            for (int x = 1; x < W; x += 3)
            {
                var cr = BigFloat.Add(centre.Re.WithPrecision(pf), BigFloat.FromDouble((x - W * 0.5) * scale, pf), pf);
                var ci = BigFloat.Add(centre.Im.WithPrecision(pf), BigFloat.FromDouble((y - H * 0.5) * scale, pf), pf);
                var (n, zr, zi) = Orbit(f, cr, ci, pf, maxIter, bailout2);
                int idx = y * W + x;
                total++; seen.Add(n);
                if (got.Iter[idx] == n) eq++;
                else if (ex.Length < 160) ex += $"({x},{y}) {got.Iter[idx]} vs {n}; ";
                if (derivative && n < maxIter && got.Iter[idx] == n)
                {
                    // ∂z_n/∂Re(c) by a forward difference of the direct orbit, h = 1e-9 pixel.
                    double h = scale * 1e-9;
                    var (_, zr2, zi2) = Orbit(f, BigFloat.Add(cr, BigFloat.FromDouble(h, pf), pf), ci, pf, maxIter, bailout2, stopAt: n);
                    double fr = BigFloat.Sub(zr2, zr, pf).ToDouble() / h, fi = BigFloat.Sub(zi2, zi, pf).ToDouble() / h;
                    double err = Math.Sqrt(Math.Pow(got.Dr[idx] - fr, 2) + Math.Pow(got.Di[idx] - fi, 2));
                    dvChecked++;
                    if (!(err < 1e-2 * Math.Sqrt(fr * fr + fi * fi))) dvBad++;
                }
            }
        output.WriteLine($"{name}: {eq}/{total} counts equal, {seen.Count} distinct, derivative off on {dvBad}/{dvChecked}; {got.Stats}");
        Assert.True(seen.Count >= 5, $"{name}: precondition, structure ({seen.Count} distinct counts)");
        Assert.True(eq >= total * 0.97, $"{name}: {eq}/{total} counts equal. {ex}");
        if (derivative)
        {
            Assert.True(dvChecked >= total / 2, $"{name}: precondition, escaped pixels to check ({dvChecked})");
            Assert.True(dvBad <= total / 50, $"{name}: derivative off by > 1 % on {dvBad} of {dvChecked}");
        }
    }

    // The tip of Tricorn's arm at 1 + i√3, a third and a fifth of a pixel off.
    private static ExactCentre TricornTip(int p, double scale)
    {
        var three = BigFloat.FromDouble(3, p);
        var s3 = BigFloat.FromDouble(Math.Sqrt(3), p);
        for (int i = 0; i < 12; i++) s3 = BigFloat.Add(s3, BigFloat.Div(three, s3, p), p).ScaleB(-1);
        return new ExactCentre(BigFloat.FromDouble(1 + 0.37 * scale, p), BigFloat.Add(s3, BigFloat.FromDouble(0.21 * scale, p), p));
    }

    // A Misiurewicz point: c with z₄ = z₃ (preperiod 3, period 1, a repelling fixed point), so the
    // view has structure at every depth and pixels escape early (not the chaotic floor of a level-set
    // boundary). Newton in R² from a double guess (found by a grid search in double); the Jacobian is a
    // finite difference in BigFloat, solved in double: each step gains ~16 digits.
    private static ExactCentre Misiurewicz(Step f, double gr, double gi, int zl)
    {
        int p = BigFloat.PrecisionForZoom(zl) + 32;
        (BigFloat, BigFloat) G(BigFloat cr, BigFloat ci)
        {
            BigFloat zr = BigFloat.Zero(p), zi = BigFloat.Zero(p);
            for (int k = 0; k < 3; k++) (zr, zi) = f(zr, zi, cr, ci, p);
            var (nr, ni) = f(zr, zi, cr, ci, p);
            return (BigFloat.Sub(nr, zr, p), BigFloat.Sub(ni, zi, p));
        }
        BigFloat r = BigFloat.FromDouble(gr, p), i = BigFloat.FromDouble(gi, p);
        var h = BigFloat.FromDouble(1, p).ScaleB(-p / 2);
        double hd = Math.Pow(2, -p / 2);
        for (int it = 0; it < 40; it++)
        {
            var (g1, g2) = G(r, i);
            double a = g1.ToDouble(), b = g2.ToDouble();
            if (Math.Abs(a) + Math.Abs(b) < Math.Pow(2, -(p - 16))) break;
            var (x1, x2) = G(BigFloat.Add(r, h, p), i);
            var (y1, y2) = G(r, BigFloat.Add(i, h, p));
            double j11 = BigFloat.Sub(x1, g1, p).ToDouble() / hd, j21 = BigFloat.Sub(x2, g2, p).ToDouble() / hd;
            double j12 = BigFloat.Sub(y1, g1, p).ToDouble() / hd, j22 = BigFloat.Sub(y2, g2, p).ToDouble() / hd;
            double det = j11 * j22 - j12 * j21;
            r = BigFloat.Sub(r, BigFloat.FromDouble((j22 * a - j12 * b) / det, p), p);
            i = BigFloat.Sub(i, BigFloat.FromDouble((-j21 * a + j11 * b) / det, p), p);
        }
        var (e1, e2) = G(r, i);
        Assert.True(Math.Abs(e1.ToDouble()) + Math.Abs(e2.ToDouble()) < Math.Pow(10, -zl - 8), "precondition: Newton converged");
        return new ExactCentre(r, i);
    }

    // The Misiurewicz point, a third and a fifth of a pixel off.
    private static ExactCentre Off(ExactCentre m, double scale, int zl)
    {
        int p = BigFloat.PrecisionForZoom(zl);
        return new ExactCentre(BigFloat.Add(m.Re.WithPrecision(p), BigFloat.FromDouble(0.37 * scale, p), p),
                               BigFloat.Add(m.Im.WithPrecision(p), BigFloat.FromDouble(0.21 * scale, p), p));
    }

    // ── engine vs oracles ──

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.3)]
    public void ZSquared_On_An_R2Plan_Matches_MandelbrotCalculator(double offset)
    {
        const double zoom = 2e33; const int maxIter = 13440;
        var plan = R2("z^2 + c", 512.0 * 512.0);
        Assert.True(plan.CanRebase);
        var re = new OD(-0.7145519151951202, -2.0790192891565988e-17, 1.1753166641509792e-33, -8.102457136962753e-50, 0, 0, 0, 0);
        var im = new OD(-0.35402073332318235, 2.5477179573089096e-17, -6.072997541261718e-34, -1.8911088656798612e-50, 0, 0, 0, 0);
        var od = OD.FromCenterOffset(re, offset * W, 3.5 / W / zoom);
        var m = new MandelbrotCalculator(W, H)
        {
            Quality = QualityPreset.Ultra, Zoom = zoom, MaxIterations = maxIter, ColorMap = ColorPalette.BuiltIns[0],
            CenterX = od.X0, CenterXLo = od.X1, CenterX2 = od.X2, CenterX3 = od.X3, CenterX4 = od.X4, CenterX5 = od.X5,
            CenterY = im.X0, CenterYLo = im.X1, CenterY2 = im.X2, CenterY3 = im.X3,
        };
        m.Calculate(CancellationToken.None);
        var got = Engine(plan, ExactCentre.FromDeep(new DeepComplex(od, im), BigFloat.PrecisionForZoom(34)), 3.5 / W / zoom, maxIter);
        int eq = Enumerable.Range(0, W * H).Count(i => got.Iter[i] == m.IterationBuffer[i]);
        output.WriteLine($"R2Plan z²+c vs MandelbrotCalculator, offset {offset}: {eq}/{W * H} equal; {got.Stats}");
        Assert.True(got.Stats.BlaSkips > 0, "precondition: the matrix BLA skipped");
        Assert.True(eq >= W * H * 0.98, $"{eq}/{W * H} equal counts");
    }

    [Theory]
    [InlineData(40)]
    [InlineData(120)]
    public void Tricorn_Matches_Direct_Iteration(int zl)
    {
        int p = BigFloat.PrecisionForZoom(zl), maxIter = 2000;
        double scale = 3.5 / W / Math.Pow(10, zl);
        var centre = TricornTip(p, scale);
        var plan = R2Plan.Tricorn();
        Assert.True(plan.CanRebase, "Tricorn: 0 is the critical point");
        var got = Engine(plan, centre, scale, maxIter);
        Assert.True(got.Stats.BlaSkips > 0, "precondition: the matrix BLA skipped");
        Check($"Tricorn 1e{zl}", got, TricornStep, 512.0 * 512.0, centre, scale, maxIter);
        Check($"Tricorn 1e{zl}, no BLA", Engine(plan, centre, scale, maxIter, useBla: false), TricornStep, 512.0 * 512.0, centre, scale, maxIter);
    }

    [Fact]
    public void Tricorn_At_A_Misiurewicz_Point_Matches_Direct_Iteration()
    {
        const int zl = 40, maxIter = 1500;
        double scale = 3.5 / W / Math.Pow(10, zl);
        var centre = Off(Misiurewicz(TricornStep, 0.7718445063460382, 1.336873900534257, zl), scale, zl);
        Check("Tricorn Misiurewicz 1e40", Engine(R2Plan.Tricorn(), centre, scale, maxIter), TricornStep, 512.0 * 512.0, centre, scale, maxIter);
    }

    [Fact]
    public void A_User_Conj_Cube_Matches_Direct_Iteration()
    {
        const int zl = 40, maxIter = 1500;
        double scale = 3.5 / W / Math.Pow(10, zl);
        var plan = AstPlan.TryCreateAny(CalcGenLowering.ParseAndLower("conj(z)^3 + c"), 1024.0, out string? why);
        var r2 = Assert.IsType<R2Plan>(plan);
        Assert.True(r2.CanRebase, why);
        var centre = Off(Misiurewicz(ConjCube, 0.9085351254697399, 0.6756301960123685, zl), scale, zl);
        Check("conj(z)^3 + c 1e40", Engine(r2, centre, scale, maxIter), ConjCube, 1024.0, centre, scale, maxIter);
    }

    [Theory]
    [InlineData("z^2 + 0.3*conj(z) + c")]
    [InlineData("z^2 + 0.3*(re(z) - i*im(z)) + c")]   // conj written with re / im
    public void A_Formula_Without_Rebasing_Matches_Direct_Iteration(string eq)
    {
        const int zl = 30, maxIter = 1500;
        double scale = 3.5 / W / Math.Pow(10, zl);
        var plan = R2(eq, 1024.0);
        Assert.False(plan.CanRebase, $"{eq}: ∂f/∂z = 0.3·conj at z = 0, so 0 is not critical");
        var centre = Off(Misiurewicz(QuadConj, -0.08741060814135457, 1.1811711640537794, zl), scale, zl);
        var got = Engine(plan, centre, scale, maxIter);
        Assert.True(got.Stats.References > 1, $"precondition: the glitch test sent pixels to secondary references ({got.Stats})");
        Check($"{eq} 1e{zl}", got, QuadConj, 1024.0, centre, scale, maxIter);
    }

    [Fact]
    public void Analytic_Formulas_Keep_The_Complex_Plan_And_Others_Are_Refused()
    {
        Assert.IsType<AstPlan>(AstPlan.TryCreateAny(CalcGenLowering.ParseAndLower("z^2 + c"), 1024.0, out _));
        Assert.IsType<R2Plan>(AstPlan.TryCreateAny(CalcGenLowering.ParseAndLower("conj(z)^2 + c"), 1024.0, out _));
        Assert.Null(AstPlan.TryCreateAny(CalcGenLowering.ParseAndLower("abs(z)^2 + c"), 1024.0, out string? why));
        Assert.False(string.IsNullOrEmpty(why));
    }

    // ── through the calculators ──

    [Fact]
    public void Tricorn_Switches_To_The_Engine_And_Agrees_At_The_Switch()
    {
        EscapeTimeCalculator Run(bool engine)
        {
            var c = new EscapeTimeCalculator(W, H)
            {
                FractalType = FractalType.Tricorn, Zoom = EscapeTimeCalculator.DeepZoomThreshold * 1.0001, MaxIterations = 1500,
                ColorMap = ColorPalette.BuiltIns[0], AllowPerturbation = engine, FractalParameters = new FractalParameters(),
            };
            c.SetDeepCentre(new DeepComplex(new OD(1 + 0.37e-13), new OD(Math.Sqrt(3))), null);
            c.Calculate(CancellationToken.None);
            return c;
        }
        var sp = Run(false); var pt = Run(true);
        Assert.Equal("SP", sp.LastPrecisionLabel);
        Assert.Equal("PT", pt.LastPrecisionLabel);
        int eq = Enumerable.Range(0, W * H).Count(i => sp.IterationBuffer[i] == pt.IterationBuffer[i]);
        int distinct = sp.IterationBuffer.Take(W * H).Distinct().Count();
        output.WriteLine($"Tricorn at 1e11: engine vs SP {eq}/{W * H} equal, {distinct} distinct; {pt.LastPerturbationStats}");
        Assert.True(distinct >= 5, $"precondition: structure ({distinct})");
        Assert.True(eq >= W * H * 0.9, $"{eq}/{W * H} equal at the switch");
        Assert.Equal(FamilyDepth.EngineDoubleDeltaMaxZoom, FamilyDepth.MaxZoomFor(FractalType.Tricorn));
    }

    [Fact]
    public void A_User_Conj_Formula_Switches_To_The_Engine_And_Agrees_At_The_Switch()
    {
        UserEquationCalculator Run(bool engine)
        {
            var c = new UserEquationCalculator(W, H)
            {
                Zoom = EscapeTimeCalculator.DeepZoomThreshold * 1.0001, MaxIterations = 1500, ColorMap = ColorPalette.BuiltIns[0],
                AllowPerturbation = engine, FractalParameters = new FractalParameters { UserEquationSource = "conj(z)^2 + c" },
            };
            var at = ExactCentre.FromDeep(new DeepComplex(new OD(1 + 0.37e-13), new OD(Math.Sqrt(3))), 64);
            c.SetDeepCentre(at.ToDeep(), at);
            c.Calculate(CancellationToken.None);
            return c;
        }
        var interp = Run(false); var pt = Run(true);
        Assert.Equal("SP", interp.LastPrecisionLabel);
        Assert.True(pt.LastPrecisionLabel == "PT", $"stayed on the interpreter ({pt.DeepRefusal})");
        int eq = Enumerable.Range(0, W * H).Count(i => interp.IterationBuffer[i] == pt.IterationBuffer[i]);
        int distinct = interp.IterationBuffer.Take(W * H).Distinct().Count();
        output.WriteLine($"conj(z)^2 + c at 1e11: engine vs interpreter {eq}/{W * H} equal, {distinct} distinct; {pt.LastPerturbationStats}");
        Assert.True(distinct >= 5, $"precondition: structure ({distinct})");
        Assert.True(eq >= W * H * 0.9, $"{eq}/{W * H} equal at the switch");
        // The derivative is checked against a finite difference of the direct orbit above, not against
        // the interpreter here: its numeric Jacobian steps c by an absolute 1e-6, ~1e5 pixels at 1e11.
    }
}
