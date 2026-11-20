// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1915 (epic #1253, D10) — piecewise formulas past double: floor / round / ceil / trunc / fract / sign /
// min / max / clamp / `if`. A pixel on another piece than the reference switches exactly (the jump is
// an integer difference, or the branches' difference at the reference's values). A pixel whose
// argument (or comparison) is too close to a jump to decide, at the pixel or the reference, is flagged
// (PerturbationStats.PoleGlitches) and goes to another reference or to direct BigFloat. Checks against
// oracles outside the plan:
//   • BigFloatMath's exact floor / ceil / trunc / round (half to even) against System.Math, and a hair
//     off an integer at 600 bits, where double cannot tell;
//   • the switch (PwDiff) and the close-call tests (PwOk, CmpOk) on hand-made cases, and the BLA radius
//     bounded by the distance to a jump;
//   • user formulas against direct BigFloat iteration written out here, at Misiurewicz points (S1909's
//     Newton), 1e40 / 1e100:
//       – inside one piece: clamp(re z, −100, 100) never clamps (|z| < 32), so nothing may be flagged;
//       – across pieces: clamp(re z, −0.3, 0.3), if re z > 0 then z² + c else z² − c, and
//         0.05·floor(2·re z): orbits visit two or three pieces, and pixels leave the reference's
//         piece (counted from the oracle orbits), so the switch must be exact;
//       – a seeded close call: floor(2·re z) with re c = ½, so z₁ = c sits on a jump to the bit and
//         the pixels straddle it below double's resolution; the fallback must take them;
//   • the calculator: an `if` formula switches to the engine at 1e11 and agrees with the interpreter.
// Mutants (by hand), all caught:
//   • no switch for floor (δ′ = 0 across pieces);
//   • the if / min / max / clamp switch without the branches' difference;
//   • the pixel's outcome taken as the reference's;
//   • the close-call tests disabled (piecewise and comparison);
//   • the jump limit on the BLA removed (the radius test; the views' BLA radii never span a jump).

using System;
using System.Linq;
using System.Threading;
using FracturingFog;
using FracturingFog.FFMath;
using FracturingFog.Models;
using FracturingFog.Perturbation;
using Xunit;
using static FracturingFog.Server.Tests.S1909ConjR2Tests;

namespace FracturingFog.Server.Tests;

public sealed class S1915BranchAgreementTests(ITestOutputHelper output)
{
    // ── exact integer parts ──

    [Fact]
    public void BigFloat_Integer_Parts_Are_Exact()
    {
        var rng = new Random(1915);
        const int p = 128;
        for (int k = 0; k < 2000; k++)
        {
            double x = (rng.NextDouble() - 0.5) * Math.Pow(10, rng.Next(0, 6));
            if (k % 10 == 0) x = Math.Round(x) + (k % 20 == 0 ? 0.5 : 0);   // integers and ties
            var b = BigFloat.FromDouble(x, p);
            Assert.Equal(Math.Floor(x), BigFloatMath.Floor(b, p).ToDouble());
            Assert.Equal(Math.Ceiling(x), BigFloatMath.Ceil(b, p).ToDouble());
            Assert.Equal(Math.Truncate(x), BigFloatMath.Trunc(b, p).ToDouble());
            Assert.Equal(Math.Round(x), BigFloatMath.Round(b, p).ToDouble());
        }
        // 3 − 2^-200: double rounds it to 3; its floor is 2.
        const int q = 600;
        var hair = BigFloat.Sub(BigFloat.FromDouble(3, q), BigFloat.FromDouble(1, q).ScaleB(-200), q);
        Assert.Equal(3.0, hair.ToDouble());
        Assert.Equal(2.0, BigFloatMath.Floor(hair, q).ToDouble());
        Assert.Equal(3.0, BigFloatMath.Ceil(hair, q).ToDouble());
        Assert.Equal(3.0, BigFloatMath.Round(hair, q).ToDouble());
        Assert.Equal(-3.0, BigFloatMath.Floor(-hair, q).ToDouble());
    }

    [Fact]
    public void Clear_Crossings_Switch_Exactly_And_Close_Calls_Are_Flagged()
    {
        double ok = double.MaxValue;
        // The jump of floor / sign / fract between the reference's argument and the pixel's.
        Assert.Equal(0.0, PerturbFunctions.PwDiff(0, 2.4, 0.3));     // floor 2 → 2
        Assert.Equal(1.0, PerturbFunctions.PwDiff(0, 2.4, 0.7));     // floor 2 → 3
        Assert.Equal(2.0, PerturbFunctions.PwDiff(5, -0.2, 0.3));    // sign −1 → 1
        Assert.Equal(0.7 - 1.0, PerturbFunctions.PwDiff(4, 2.4, 0.7), 15);   // fract: d − the floor's jump
        // Clear calls are trusted, crossing or not; close ones are not.
        Assert.Equal(ok, PerturbFunctions.PwOk(0, 2.4, 0.7));
        Assert.Equal(0, PerturbFunctions.PwOk(0, 2.4, 0.6 - 1e-14));        // lands within the margin of 3
        Assert.Equal(0, PerturbFunctions.PwOk(0, 3 - 1e-15, 1e-17));        // the reference itself within the margin
        Assert.Equal(ok, PerturbFunctions.PwOk(0, 0, 0));                   // the same argument exactly (z₀ = 0 on a jump)
        Assert.Equal(ok, PerturbFunctions.CmpOk(1.0, 0.5, -0.6, 0));        // a clear crossing
        Assert.Equal(0, PerturbFunctions.CmpOk(1.0, 0.5, -0.5 + 1e-14, 0)); // lands within the margin
        Assert.Equal(ok, PerturbFunctions.CmpOk(0.25, 0.25, 0, 0));         // the same values exactly
    }

    [Fact]
    public void The_BLA_Stays_Off_The_Jumps()
    {
        // ε = 1: the second-order radius is loose, so the jump bounds it (half the distance, as the folds).
        var floorPlan = (R2Plan)AstPlan.TryCreateAny(CalcGenLowering.ParseAndLower("z^2 + c + 0.05*floor(2*re(z))"), 1024.0, out _)!;
        // 2·re Z = 1 − 2e-6: 2e-6 from floor's jump at 1, in a quantity with gradient 2 → δ ≤ 1e-6.
        double r = Math.Sqrt(floorPlan.Level0R2(0.5 - 1e-6, 0.3, -0.2, 0.1, 1.0, 0.0).R2);
        output.WriteLine($"floor: r = {r:E3}");
        Assert.True(r <= 0.5e-6 * (1 + 1e-9), $"floor: radius {r}");
        var ifPlan = (R2Plan)AstPlan.TryCreateAny(CalcGenLowering.ParseAndLower("if re(z) > 0 then z^2 + c else z^2 - c"), 1024.0, out _)!;
        double r2 = Math.Sqrt(ifPlan.Level0R2(1e-7, 0.4, -0.2, 0.1, 1.0, 0.0).R2);
        output.WriteLine($"if: r = {r2:E3}");
        Assert.True(r2 <= 0.5e-7 * (1 + 1e-9), $"if: radius {r2}");
        // Away from the jumps the radius is not limited by them.
        Assert.True(Math.Sqrt(floorPlan.Level0R2(0.25, 0.3, -0.2, 0.1, 1.0, 0.0).R2) > 1e-3);
    }

    // ── the formulas written out ──

    private static BigFloat K(double v, int p) => BigFloat.FromDouble(v, p);

    private static (BigFloat, BigFloat) Sq(BigFloat zr, BigFloat zi, int p)
        => (BigFloat.Sub(BigFloat.Mul(zr, zr, p), BigFloat.Mul(zi, zi, p), p), BigFloat.Mul(zr, zi, p).ScaleB(1));

    private static Step ClampStep(double lo, double hi) => (zr, zi, cr, ci, p) =>
    {
        var (sr, si) = Sq(zr, zi, p);
        var x = zr < K(lo, p) ? K(lo, p) : zr > K(hi, p) ? K(hi, p) : zr;
        return (BigFloat.Add(BigFloat.Add(sr, cr, p), BigFloat.Mul(K(0.1, p), x, p), p), BigFloat.Add(si, ci, p));
    };

    private static readonly Step IfRe = (zr, zi, cr, ci, p) =>
    {
        var (sr, si) = Sq(zr, zi, p);
        return zr.Sign > 0 ? (BigFloat.Add(sr, cr, p), BigFloat.Add(si, ci, p)) : (BigFloat.Sub(sr, cr, p), BigFloat.Sub(si, ci, p));
    };

    private static readonly Step FloorStep = (zr, zi, cr, ci, p) =>
    {
        var (sr, si) = Sq(zr, zi, p);
        var f = BigFloatMath.Floor(zr.ScaleB(1), p);
        return (BigFloat.Add(BigFloat.Add(sr, cr, p), BigFloat.Mul(K(0.05, p), f, p), p), BigFloat.Add(si, ci, p));
    };

    private void Stable(string name, Step f, ExactCentre centre, double scale, int maxIter)
    {
        int p = centre.Precision + 96, same = 0, total = 0;
        for (int y = 1; y < H; y += 3)
            for (int x = 1; x < W; x += 3)
            {
                var cr = BigFloat.Add(centre.Re.WithPrecision(p), BigFloat.FromDouble((x - W * 0.5) * scale, p), p);
                var ci = BigFloat.Add(centre.Im.WithPrecision(p), BigFloat.FromDouble((y - H * 0.5) * scale, p), p);
                total++;
                if (Orbit(f, cr, ci, p, maxIter, 1024.0).N == Orbit(f, BigFloat.Add(cr, BigFloat.FromDouble(scale * 1e-12, p), p), ci, p, maxIter, 1024.0).N) same++;
            }
        Assert.True(same == total, $"{name}: precondition, above the chaotic floor ({same}/{total})");
    }

    // Sampled pixels whose orbit is, at some step before either escapes, on another piece than the
    // view centre's orbit (piece: the function's selector at re z).
    private static int Switchers(Step f, Func<double, int> piece, ExactCentre centre, double scale, int maxIter)
    {
        int p = centre.Precision + 32, n0 = 0, switchers = 0;
        var cen = new System.Collections.Generic.List<int>();
        BigFloat zr = BigFloat.Zero(p), zi = BigFloat.Zero(p);
        for (; n0 < maxIter; n0++)
        {
            double a = zr.ToDouble(), b = zi.ToDouble();
            if (a * a + b * b >= 1024.0) break;
            cen.Add(piece(a));
            (zr, zi) = f(zr, zi, centre.Re, centre.Im, p);
        }
        for (int y = 1; y < H; y += 3)
            for (int x = 1; x < W; x += 3)
            {
                var cr = BigFloat.Add(centre.Re.WithPrecision(p), BigFloat.FromDouble((x - W * 0.5) * scale, p), p);
                var ci = BigFloat.Add(centre.Im.WithPrecision(p), BigFloat.FromDouble((y - H * 0.5) * scale, p), p);
                BigFloat ar = BigFloat.Zero(p), ai = BigFloat.Zero(p);
                for (int n = 0; n < cen.Count; n++)
                {
                    double a = ar.ToDouble(), b = ai.ToDouble();
                    if (a * a + b * b >= 1024.0) break;
                    if (piece(a) != cen[n]) { switchers++; break; }
                    (ar, ai) = f(ar, ai, cr, ci, p);
                }
            }
        return switchers;
    }

    private Result Run(string name, string eq, Step f, double gr, double gi, int zl, Func<double, int>? piece = null)
    {
        var plan = AstPlan.TryCreateAny(CalcGenLowering.ParseAndLower(eq), 1024.0, out string? why);
        Assert.True(plan is R2Plan, $"{eq}: {why}");
        double scale = 3.5 / W / Math.Pow(10, zl);
        var centre = Off(Misiurewicz(f, gr, gi, zl, 3, 1), scale, zl);
        const int maxIter = 2000;
        Stable(name, f, centre, scale, maxIter);
        if (piece != null)
        {
            int sw = Switchers(f, piece, centre, scale, maxIter);
            output.WriteLine($"{name}: {sw} of 176 sampled pixels leave the centre orbit's piece");
            Assert.True(sw >= 20, $"{name}: precondition, pixels switch pieces ({sw})");
        }
        var got = Engine(plan!, centre, scale, maxIter);
        Check(output, name, got, f, 1024.0, centre, scale, maxIter);
        return got;
    }

    [Theory]
    [InlineData(40)]
    [InlineData(100)]
    public void Inside_One_Piece_Nothing_Is_Flagged(int zl)
    {
        var got = Run($"clamp(re z, −100, 100) 1e{zl}", "z^2 + c + 0.1*clamp(re(z), -100, 100)", ClampStep(-100, 100),
                      -0.22909883178293733, -1.1197498104147685, zl);
        Assert.Equal(0, got.Stats.PoleGlitches);
        Assert.True(got.Stats.BlaSkips > 0, "precondition: the BLA skipped (no jump limits it)");
    }

    [Theory]
    [InlineData("z^2 + c + 0.1*clamp(re(z), -0.3, 0.3)", "clamp", -0.22751908265690907, -1.1069586147220611, 40)]
    [InlineData("z^2 + c + 0.1*clamp(re(z), -0.3, 0.3)", "clamp", -0.22751908265690907, -1.1069586147220611, 100)]
    [InlineData("if re(z) > 0 then z^2 + c else z^2 - c", "if", -0.6319118527949839, -0.8607956818031647, 40)]
    [InlineData("if re(z) > 0 then z^2 + c else z^2 - c", "if", -0.6319118527949839, -0.8607956818031647, 100)]
    [InlineData("z^2 + c + 0.05*floor(2*re(z))", "floor", -0.23071760024448326, -1.1080602974961, 40)]
    [InlineData("z^2 + c + 0.05*floor(2*re(z))", "floor", -0.23071760024448326, -1.1080602974961, 100)]
    public void Across_Pieces_The_Fallback_Keeps_It_Exact(string eq, string kind, double gr, double gi, int zl)
    {
        Step f = kind switch { "clamp" => ClampStep(-0.3, 0.3), "if" => IfRe, _ => FloorStep };
        Func<double, int> piece = kind switch
        {
            "clamp" => x => x < -0.3 ? -1 : x > 0.3 ? 1 : 0,
            "if" => x => x > 0 ? 1 : 0,
            _ => x => (int)Math.Floor(2 * x),
        };
        var got = Run($"{eq} 1e{zl}", eq, f, gr, gi, zl, piece);
        output.WriteLine($"{eq}: {got.Stats.PoleGlitches} close calls flagged");
    }

    [Fact]
    public void A_Seeded_Close_Call_Falls_Back_And_Stays_Exact()
    {
        // re c = ½ exactly: z₁ = c, so 2·re z₁ = 1 sits on floor's jump, and pixels a 1e-40 either side
        // of it are below double's resolution there (1 + 1e-40 = 1). The close-call test must send them on.
        const int zl = 40;
        int p = BigFloat.PrecisionForZoom(zl);
        double scale = 3.5 / W / Math.Pow(10, zl);
        var centre = new ExactCentre(BigFloat.FromDouble(0.5, p), BigFloat.FromDouble(0.3, p));
        var plan = (R2Plan)AstPlan.TryCreateAny(CalcGenLowering.ParseAndLower("z^2 + c + 0.05*floor(2*re(z))"), 1024.0, out _)!;
        var got = Engine(plan, centre, scale, 2000);
        output.WriteLine($"seeded close call: {got.Stats}");
        Assert.True(got.Stats.PoleGlitches > 0, $"precondition: close calls flagged ({got.Stats})");
        // Counts against direct iteration (no structure is required here: the pixels split into two groups).
        int q = p + 96, eq = 0, total = 0;
        var seen = new System.Collections.Generic.HashSet<int>();
        for (int y = 1; y < H; y += 3)
            for (int x = 1; x < W; x += 3)
            {
                var cr = BigFloat.Add(centre.Re.WithPrecision(q), BigFloat.FromDouble((x - W * 0.5) * scale, q), q);
                var ci = BigFloat.Add(centre.Im.WithPrecision(q), BigFloat.FromDouble((y - H * 0.5) * scale, q), q);
                int n = Orbit(FloorStep, cr, ci, q, 2000, 1024.0).N;
                total++; seen.Add(n);
                if (got.Iter[y * W + x] == n) eq++;
            }
        output.WriteLine($"seeded close call: {eq}/{total} equal, {seen.Count} distinct counts");
        Assert.Equal(total, eq);
    }

    [Fact]
    public void An_If_Formula_Switches_To_The_Engine_At_1e11()
    {
        double scale = 3.5 / W / EscapeTimeCalculator.DeepZoomThreshold;
        var at = Off(Misiurewicz(IfRe, -0.6319118527949839, -0.8607956818031647, 20, 3, 1), scale, 20);
        UserEquationCalculator Go(bool engine)
        {
            var c = new UserEquationCalculator(W, H)
            {
                Zoom = EscapeTimeCalculator.DeepZoomThreshold * 1.0001, MaxIterations = 1500, ColorMap = ColorPalette.BuiltIns[0],
                AllowPerturbation = engine,
                FractalParameters = new FractalParameters { UserEquationSource = "if re(z) > 0 then z^2 + c else z^2 - c" },
            };
            c.SetDeepCentre(at.ToDeep(), at);
            c.Calculate(CancellationToken.None);
            return c;
        }
        var interp = Go(false); var pt = Go(true);
        Assert.True(pt.LastPrecisionLabel == "PT", $"stayed on the interpreter ({pt.DeepRefusal})");
        int eq = Enumerable.Range(0, W * H).Count(i => interp.IterationBuffer[i] == pt.IterationBuffer[i]);
        int distinct = interp.IterationBuffer.Take(W * H).Distinct().Count();
        output.WriteLine($"if formula at 1e11: engine vs interpreter {eq}/{W * H}, {distinct} distinct; {pt.LastPerturbationStats}");
        Assert.True(distinct >= 5, $"precondition: structure ({distinct})");
        Assert.True(eq >= W * H * 0.9, $"{eq}/{W * H}");
    }
}
