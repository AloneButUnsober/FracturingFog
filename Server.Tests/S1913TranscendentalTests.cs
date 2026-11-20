// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1913 (epic #1253, D8) — elementary functions under perturbation: R2Plan's exp / log / sin / cos /
// sqrt / pow differences (PerturbFunctions) with a BigFloatMath reference (#1914). Each check against
// an oracle outside the plan:
//   • each difference against f(V + D) − f(V) in BigFloat, for |D| from 1e-14 to 1e-1 of |V|, including
//     D that carries V across log's cut (the principal branch, kept exactly by the 2πk);
//   • complex expm1 / log1p against BigFloat;
//   • user formulas exp(z) + c and sin(z) + z² + c against direct BigFloat iteration written out here
//     (BigFloatMath) at Misiurewicz points, 1e40 / 1e100;
//   • Glynn (z^1.5 + c, the dynamic plane) at its Julia set's repelling fixed point, 1e40 / 1e100;
//   • a seeded branch-cut view: Glynn at e^{2πi/3}, whose first iterate is −1.2 — on the cut — so the
//     pixels split across it and step 2 sends the two halves to the two branches;
//   • the calculators at 1e11.
// Mutants (by hand), all caught: naive exp / sin differences, the cos sign, the 2πk shift removed, the
// cut / zero BLA limit removed (only the branch-cut view sees it), the tangent using f for f′.

using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using FracturingFog;
using FracturingFog.Abstractions.Explore;
using FracturingFog.FFMath;
using FracturingFog.Models;
using FracturingFog.Perturbation;
using Xunit;
using static FracturingFog.Server.Tests.S1909ConjR2Tests;

namespace FracturingFog.Server.Tests;

public sealed class S1913TranscendentalTests(ITestOutputHelper output)
{
    private const int P = 300;
    private static BigFloat B(double v, int p = P) => BigFloat.FromDouble(v, p);

    // ── the differences against BigFloat ──

    private static (BigFloat, BigFloat) BF(int kind, BigFloat r, BigFloat i, double pr, int p) => kind switch
    {
        0 => BigFloatMath.CExp(r, i, p),
        1 => BigFloatMath.CLog(r, i, p),
        2 => BigFloatMath.CSin(r, i, p),
        3 => BigFloatMath.CCos(r, i, p),
        _ => BigFloatMath.CPow(r, i, BigFloat.FromDouble(pr, p), BigFloat.Zero(p), p),
    };

    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(1, 0.0)]
    [InlineData(2, 0.0)]
    [InlineData(3, 0.0)]
    [InlineData(4, 1.5)]
    [InlineData(4, 0.5)]
    [InlineData(4, -0.7)]
    public void Each_Difference_Matches_BigFloat_Without_Cancellation(int kind, double p)
    {
        var rng = new Random(1913 + kind);
        double worst = 0; int crossings = 0;
        for (int k = 0; k < 600; k++)
        {
            double vr = rng.NextDouble() * 4 - 2, vi = rng.NextDouble() * 4 - 2;
            double mag = Math.Pow(10, -14 + 13 * rng.NextDouble()) * Math.Sqrt(vr * vr + vi * vi);
            double dr = (rng.NextDouble() - 0.5) * mag, di = (rng.NextDouble() - 0.5) * mag;
            if (k % 4 == 0 && kind is 1 or 4)
            {
                // Straddle the cut: V just above the negative real axis, V + D just below.
                // Every other one a hair off the axis (arg V rounds to π in double).
                vr = -(0.2 + rng.NextDouble()); vi = (k % 8 == 0 ? 1e-60 : 1e-9) * rng.NextDouble(); dr = 0; di = -2 * vi - (k % 8 == 0 ? 1e-45 : 1e-12);
                crossings++;
            }
            var q = kind switch
            {
                0 => PerturbFunctions.ExpD(vr, vi, dr, di),
                1 => PerturbFunctions.LogD(vr, vi, dr, di),
                2 => PerturbFunctions.SinD(vr, vi, dr, di),
                3 => PerturbFunctions.CosD(vr, vi, dr, di),
                _ => PerturbFunctions.PowD(vr, vi, dr, di, p, 0),
            };
            var (ar, ai) = BF(kind, B(vr), B(vi), p, P);
            var (br, bi) = BF(kind, BigFloat.Add(B(vr), B(dr), P), BigFloat.Add(B(vi), B(di), P), p, P);
            double wr = BigFloat.Sub(br, ar, P).ToDouble(), wi = BigFloat.Sub(bi, ai, P).ToDouble();
            double rel = Math.Sqrt((q.C - wr) * (q.C - wr) + (q.D - wi) * (q.D - wi)) / Math.Sqrt(wr * wr + wi * wi);
            Assert.True(Math.Abs(q.A - ar.ToDouble()) <= 1e-13 * Math.Max(1, Math.Abs(q.A)), "f(V) re");
            worst = Math.Max(worst, rel);
        }
        output.WriteLine($"kind {kind} p {p}: worst relative error {worst:E2} ({crossings} cut crossings)");
        Assert.True(worst < 1e-12, $"kind {kind}: {worst}");
    }

    [Fact]
    public void Complex_Expm1_And_Log1p_Are_Accurate_For_Small_Arguments()
    {
        var rng = new Random(5);
        double worst = 0;
        for (int k = 0; k < 500; k++)
        {
            double mag = Math.Pow(10, -15 + 15 * rng.NextDouble());
            var w = new Complex((rng.NextDouble() - 0.5) * mag, (rng.NextDouble() - 0.5) * mag);
            var e = PerturbFunctions.Expm1(w);
            var (er, ei) = BigFloatMath.CExp(B(w.Real), B(w.Imaginary), P);
            double xr = BigFloat.Sub(er, B(1), P).ToDouble(), xi = ei.ToDouble();
            worst = Math.Max(worst, Complex.Abs(e - new Complex(xr, xi)) / Complex.Abs(new Complex(xr, xi)));
            var l = PerturbFunctions.Log1p(w);
            var (lr, li) = BigFloatMath.CLog(BigFloat.Add(B(1), B(w.Real), P), B(w.Imaginary), P);
            worst = Math.Max(worst, Complex.Abs(l - new Complex(lr.ToDouble(), li.ToDouble())) / Complex.Abs(new Complex(lr.ToDouble(), li.ToDouble())));
        }
        output.WriteLine($"expm1 / log1p worst relative error {worst:E2}");
        Assert.True(worst < 1e-14, $"{worst}");
    }

    // ── the formulas written out ──

    private static readonly Step ExpPlusC = (zr, zi, cr, ci, p) =>
    {
        var (a, b) = BigFloatMath.CExp(zr, zi, p);
        return (BigFloat.Add(a, cr, p), BigFloat.Add(b, ci, p));
    };

    private static readonly Step SinZ2 = (zr, zi, cr, ci, p) =>
    {
        var (a, b) = BigFloatMath.CSin(zr, zi, p);
        var sr = BigFloat.Sub(BigFloat.Mul(zr, zr, p), BigFloat.Mul(zi, zi, p), p);
        var si = BigFloat.Mul(zr, zi, p).ScaleB(1);
        return (BigFloat.Add(BigFloat.Add(a, sr, p), cr, p), BigFloat.Add(BigFloat.Add(b, si, p), ci, p));
    };

    private const double GlynnC = -0.2;   // FractalParameters' default

    // Glynn's map: z^1.5 + c (principal) — the "c" argument is unused (the dynamic plane).
    private static (BigFloat, BigFloat) GlynnStep(BigFloat zr, BigFloat zi, int p)
    {
        var (a, b) = BigFloatMath.CPow(zr, zi, BigFloat.FromDouble(1.5, p), BigFloat.Zero(p), p);
        return (BigFloat.Add(a, BigFloat.FromDouble(GlynnC, p), p), b);
    }

    private static R2Plan Plan(string eq)
    {
        var plan = AstPlan.TryCreateAny(CalcGenLowering.ParseAndLower(eq), 1024.0, out string? why);
        Assert.True(plan is R2Plan, $"{eq}: {why}");
        return (R2Plan)plan!;
    }

    private void Stable(string name, Step f, ExactCentre centre, double scale, int maxIter, double bailout2)
    {
        int p = centre.Precision + 96, same = 0, total = 0;
        for (int y = 1; y < H; y += 3)
            for (int x = 1; x < W; x += 3)
            {
                var cr = BigFloat.Add(centre.Re.WithPrecision(p), BigFloat.FromDouble((x - W * 0.5) * scale, p), p);
                var ci = BigFloat.Add(centre.Im.WithPrecision(p), BigFloat.FromDouble((y - H * 0.5) * scale, p), p);
                total++;
                if (Orbit(f, cr, ci, p, maxIter, bailout2).N == Orbit(f, BigFloat.Add(cr, BigFloat.FromDouble(scale * 1e-12, p), p), ci, p, maxIter, bailout2).N) same++;
            }
        Assert.True(same == total, $"{name}: precondition, above the chaotic floor ({same}/{total})");
    }

    [Theory]
    [InlineData("exp(z) + c", 40)]
    [InlineData("exp(z) + c", 100)]
    [InlineData("sin(z) + z^2 + c", 40)]
    [InlineData("sin(z) + z^2 + c", 100)]
    public void A_User_Transcendental_Matches_Direct_Iteration(string eq, int zl)
    {
        bool isExp = eq.StartsWith("exp");
        Step f = isExp ? ExpPlusC : SinZ2;
        double scale = 3.5 / W / Math.Pow(10, zl);
        var m = isExp ? Misiurewicz(f, 1.3476187436596399, -1.7628052365785276, zl, 4, 1)
                      : Misiurewicz(f, -1.5658794250159749, -0.879359431247305, zl, 4, 1);
        var centre = Off(m, scale, zl);
        const int maxIter = 2000;
        Stable(eq, f, centre, scale, maxIter, 1024.0);
        var got = Engine(Plan(eq), centre, scale, maxIter);
        Assert.True(got.Stats.BlaSkips > 0, $"{eq}: precondition, the BLA skipped");
        Check(output, $"{eq} 1e{zl}", got, f, 1024.0, centre, scale, maxIter);
        Check(output, $"{eq} 1e{zl}, no BLA", Engine(Plan(eq), centre, scale, maxIter, useBla: false), f, 1024.0, centre, scale, maxIter);
    }

    // ── Glynn (the dynamic plane): its own oracle loop (z₀ = the pixel) ──

    private static int GlynnCount(BigFloat zr, BigFloat zi, int p, int maxIter)
    {
        for (int n = 0; n < maxIter; n++)
        {
            double a = zr.ToDouble(), b = zi.ToDouble();
            if (a * a + b * b >= 4.0) return n;
            (zr, zi) = GlynnStep(zr, zi, p);
        }
        return maxIter;
    }

    private void CheckGlynn(string name, ExactCentre centre, double scale, int maxIter, int minDistinct)
    {
        var plan = R2Plan.Glynn(GlynnC, 0);
        Assert.False(plan.ParameterPlane);
        var got = Engine(plan, centre, scale, maxIter);
        int p = centre.Precision + 96, total = 0, eq = 0, same = 0;
        var seen = new System.Collections.Generic.HashSet<int>();
        string ex = "";
        for (int y = 1; y < H; y += 3)
            for (int x = 1; x < W; x += 3)
            {
                var zr = BigFloat.Add(centre.Re.WithPrecision(p), BigFloat.FromDouble((x - W * 0.5) * scale, p), p);
                var zi = BigFloat.Add(centre.Im.WithPrecision(p), BigFloat.FromDouble((y - H * 0.5) * scale, p), p);
                int n = GlynnCount(zr, zi, p, maxIter);
                if (n == GlynnCount(BigFloat.Add(zr, BigFloat.FromDouble(scale * 1e-12, p), p), zi, p, maxIter)) same++;
                total++; seen.Add(n);
                if (got.Iter[y * W + x] == n) eq++;
                else if (ex.Length < 160) ex += $"({x},{y}) {got.Iter[y * W + x]} vs {n}; ";
            }
        output.WriteLine($"{name}: {eq}/{total} counts equal, {seen.Count} distinct, {same} stable; {got.Stats}");
        Assert.True(same == total, $"{name}: precondition, above the chaotic floor ({same}/{total})");
        Assert.True(seen.Count >= minDistinct, $"{name}: precondition, structure ({seen.Count})");
        Assert.True(eq >= total * 0.97, $"{name}: {eq}/{total}. {ex}");
    }

    // The repelling fixed point of z^1.5 − 0.2 (by Newton in BigFloat), a third / a fifth of a pixel off.
    private static ExactCentre GlynnFixed(int zl, double scale)
    {
        int p = BigFloat.PrecisionForZoom(zl) + 32;
        BigFloat z = B(1.3247391249176088, p);
        for (int it = 0; it < 12; it++)
        {
            var (fr, _) = GlynnStep(z, BigFloat.Zero(p), p);
            var g = BigFloat.Sub(fr, z, p);                                   // z^1.5 − 0.2 − z (real on z > 0)
            var d = BigFloat.Sub(BigFloat.Mul(B(1.5, p), BigFloatMath.Sqrt(z, p), p), B(1, p), p);
            z = BigFloat.Sub(z, BigFloat.Div(g, d, p), p);
        }
        int q = BigFloat.PrecisionForZoom(zl);
        return new ExactCentre(BigFloat.Add(z, B(0.37 * scale, p), p).WithPrecision(q), B(0.21 * scale, q));
    }

    [Theory]
    [InlineData(40)]
    [InlineData(100)]
    public void Glynn_Matches_Direct_Iteration_At_Its_Fixed_Point(int zl)
    {
        double scale = 3.5 / W / Math.Pow(10, zl);
        CheckGlynn($"Glynn 1e{zl}", GlynnFixed(zl, scale), scale, 2000, 5);
    }

    [Fact]
    public void Glynn_On_A_Seeded_Branch_Cut_Matches_Direct_Iteration()
    {
        // z₀ = e^{2πi/3}: z₀^1.5 = e^{iπ} = −1, so z₁ = −1.2 on the cut. Pixels a hair above / below it go
        // to z₂ = 1.2^1.5·e^{∓1.5πi} − 0.2 = −0.2 ± 1.315i: two branches, a difference of order 1.
        const int zl = 40;
        int p = BigFloat.PrecisionForZoom(zl);
        double scale = 3.5 / W / Math.Pow(10, zl);
        var three = BigFloatMath.Sqrt(B(3, p + 32), p + 32);
        var centre = new ExactCentre(B(-0.5, p), three.ScaleB(-1).WithPrecision(p));
        // Precondition: the sampled pixels' z₂ lie on both branches.
        int up = 0, down = 0;
        for (int y = 1; y < H; y += 3)
            for (int x = 1; x < W; x += 3)
            {
                var zr = BigFloat.Add(centre.Re, BigFloat.FromDouble((x - W * 0.5) * scale, p), p);
                var zi = BigFloat.Add(centre.Im, BigFloat.FromDouble((y - H * 0.5) * scale, p), p);
                var (ar, ai) = GlynnStep(zr, zi, p); var (_, bi) = GlynnStep(ar, ai, p);
                if (bi.Sign > 0) up++; else down++;
            }
        output.WriteLine($"branch-cut view: z₂ above {up}, below {down}");
        Assert.True(up > 20 && down > 20, "precondition: the view straddles the cut");
        // Every pixel escapes at the same count here, so compare the escaped z itself: the two branches
        // give different values, and a lost 2πk would put a pixel on the wrong one.
        var ezr = new double[W * H]; var ezi = new double[W * H]; var it = new int[W * H];
        var st = PerturbationEngine.Render(R2Plan.Glynn(GlynnC, 0), centre, scale, W, H, 200,
            (idx, n, a, b, _, _) => { it[idx] = n; ezr[idx] = a; ezi[idx] = b; }, CancellationToken.None);
        int q = p + 96, ok = 0, total = 0, imUp = 0, imDown = 0;
        string ex = "";
        for (int y = 1; y < H; y += 3)
            for (int x = 1; x < W; x += 3)
            {
                BigFloat a = BigFloat.Add(centre.Re.WithPrecision(q), BigFloat.FromDouble((x - W * 0.5) * scale, q), q);
                BigFloat b = BigFloat.Add(centre.Im.WithPrecision(q), BigFloat.FromDouble((y - H * 0.5) * scale, q), q);
                int n = 0;
                for (; n < 200; n++)
                {
                    double u = a.ToDouble(), v = b.ToDouble();
                    if (u * u + v * v >= 4.0) break;
                    (a, b) = GlynnStep(a, b, q);
                }
                double wr = a.ToDouble(), wi = b.ToDouble();
                int idx = y * W + x;
                total++;
                if (wi > 0) imUp++; else imDown++;
                double err = Math.Sqrt((ezr[idx] - wr) * (ezr[idx] - wr) + (ezi[idx] - wi) * (ezi[idx] - wi));
                if (it[idx] == n && err < 1e-9 * Math.Sqrt(wr * wr + wi * wi)) ok++;
                else if (ex.Length < 200) ex += $"({x},{y}) {it[idx]} ({ezr[idx]:G6},{ezi[idx]:G6}) vs {n} ({wr:G6},{wi:G6}); ";
            }
        output.WriteLine($"Glynn branch cut 1e40: {ok}/{total} escaped z equal; escaped z above {imUp}, below {imDown}; {st}");
        Assert.True(imUp > 20 && imDown > 20, "precondition: the escaped values differ between the branches");
        Assert.True(ok == total, $"{ok}/{total}. {ex}");
    }

    // ── through the calculators ──

    [Fact]
    public void Glynn_Switches_To_The_Engine_And_Agrees_With_SP()
    {
        double scale = 3.5 / W / EscapeTimeCalculator.DeepZoomThreshold;
        var at = GlynnFixed(20, scale);
        EscapeTimeCalculator Go(bool engine)
        {
            var c = new EscapeTimeCalculator(W, H)
            {
                FractalType = FractalType.Glynn, Zoom = EscapeTimeCalculator.DeepZoomThreshold * 1.0001, MaxIterations = 1500,
                ColorMap = ColorPalette.BuiltIns[0], AllowPerturbation = engine, FractalParameters = new FractalParameters(),
            };
            c.SetDeepCentre(at.ToDeep(), at);
            c.Calculate(CancellationToken.None);
            return c;
        }
        var sp = Go(false); var pt = Go(true);
        Assert.Equal(GlynnC, new FractalParameters().GlynnC.Real);
        Assert.Equal("SP", sp.LastPrecisionLabel);
        Assert.Equal("PT", pt.LastPrecisionLabel);
        int eq = Enumerable.Range(0, W * H).Count(i => sp.IterationBuffer[i] == pt.IterationBuffer[i]);
        int distinct = sp.IterationBuffer.Take(W * H).Distinct().Count();
        output.WriteLine($"Glynn at 1e11: engine vs SP {eq}/{W * H}, {distinct} distinct; {pt.LastPerturbationStats}");
        Assert.True(distinct >= 5, $"precondition: structure ({distinct})");
        Assert.True(eq >= W * H * 0.9, $"{eq}/{W * H} equal at the switch");
        Assert.Equal(QualityPreset.Deepest.ZoomMax, FamilyDepth.MaxZoomFor(FractalType.Glynn));   // #1921: unlimited
    }

    [Fact]
    public void A_User_Transcendental_Switches_To_The_Engine_At_1e11()
    {
        double scale = 3.5 / W / EscapeTimeCalculator.DeepZoomThreshold;
        var at = Off(Misiurewicz(ExpPlusC, 1.3476187436596399, -1.7628052365785276, 20, 4, 1), scale, 20);
        UserEquationCalculator Go(bool engine)
        {
            var c = new UserEquationCalculator(W, H)
            {
                Zoom = EscapeTimeCalculator.DeepZoomThreshold * 1.0001, MaxIterations = 1500, ColorMap = ColorPalette.BuiltIns[0],
                AllowPerturbation = engine, FractalParameters = new FractalParameters { UserEquationSource = "exp(z) + c" },
            };
            c.SetDeepCentre(at.ToDeep(), at);
            c.Calculate(CancellationToken.None);
            return c;
        }
        var interp = Go(false); var pt = Go(true);
        Assert.True(pt.LastPrecisionLabel == "PT", $"stayed on the interpreter ({pt.DeepRefusal})");
        int eq = Enumerable.Range(0, W * H).Count(i => interp.IterationBuffer[i] == pt.IterationBuffer[i]);
        int distinct = interp.IterationBuffer.Take(W * H).Distinct().Count();
        output.WriteLine($"exp(z) + c at 1e11: engine vs interpreter {eq}/{W * H}, {distinct} distinct; {pt.LastPerturbationStats}");
        Assert.True(distinct >= 5, $"precondition: structure ({distinct})");
        Assert.True(eq >= W * H * 0.9, $"{eq}/{W * H}");
    }
}
