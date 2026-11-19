// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1908 (epic #1253, D3) — a User Equation past double: AstPlan derives the δ recurrence, the
// derivative, BLA's A / B and the radius term from the equation's AST, and PerturbationEngine renders
// it. Each check against an oracle that does not go through the AST:
//   • "z^2 + c" as an AstPlan against MandelbrotCalculator at p448 2e33, minibrot centred and 0.3
//     views off-centre (the rebase path: 0 is the critical point);
//   • "z^3 + c" against direct BigFloat iteration (written out here) at the Misiurewicz point of S1907,
//     1e40;
//   • "z^2 + z + c", whose critical point is −½ (no rebase: the generalised glitch test and secondary
//     references), at c* = (−3 − √5)/2: 0 → c* → φ, the golden ratio, a repelling fixed point of
//     z² + z + c* (f′(φ) = 2φ + 1 ≈ 4.24), so the view has structure at every depth. A third of a pixel
//     off c* the reference escapes early, so secondary references must run. 1e30 and 1e100;
//   • through UserEquationCalculator: past 1e11 a polynomial renders on the engine ("PT") and agrees
//     with the double interpreter at the switch; a transcendental equation, a z₀ seed or a bailout
//     condition keeps the interpreter, with the reason.
// Mutants (by hand): ε bound to 0 in the compiled δ — caught (the z² + c views; the direct-oracle
// views take ε through BLA's B·ε after the first skip); the BLA radius ×1e6 — caught; the
// derivative's ∂f/∂c dropped — caught by the BLA-off z³ + c run (with BLA on, the skips add + B).

using System;
using System.Linq;
using System.Threading;
using FracturingFog;
using FracturingFog.FFMath;
using FracturingFog.Models;
using FracturingFog.Perturbation;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1908UserEquationDeepTests(ITestOutputHelper output)
{
    private const int W = 48, H = 32;

    private static AstPlan Plan(string eq, out bool canRebase, double bailout2 = 1024.0)
    {
        var p = AstPlan.TryCreate(CalcGenLowering.ParseAndLower(eq), bailout2, out string? why);
        Assert.True(p != null, $"{eq}: {why}");
        canRebase = p!.CanRebase;
        return p;
    }

    private sealed record Result(int[] Iter, double[] Dv, PerturbationStats Stats);

    private static Result Engine(IPerturbationPlan plan, ExactCentre centre, double scale, int maxIter, bool useBla = true)
    {
        var it = new int[W * H]; var dv = new double[W * H];
        var st = PerturbationEngine.Render(plan, centre, scale, W, H, maxIter,
            (idx, n, zr, zi, a, b) => { it[idx] = n; dv[idx] = Math.Sqrt(a * a + b * b); }, CancellationToken.None,
            useBla: useBla);
        return new Result(it, dv, st);
    }

    // ── the oracle: the formula written out, iterated directly in BigFloat (z₀ = 0, c = the pixel) ──

    private static (BigFloat, BigFloat) M(BigFloat a, BigFloat b, BigFloat c, BigFloat d, int p)
        => (BigFloat.Sub(BigFloat.Mul(a, c, p), BigFloat.Mul(b, d, p), p), BigFloat.Add(BigFloat.Mul(a, d, p), BigFloat.Mul(b, c, p), p));

    private delegate (BigFloat, BigFloat) Step(BigFloat zr, BigFloat zi, BigFloat cr, BigFloat ci, int p);
    private delegate (double, double) Deriv(double zr, double zi, double vr, double vi);   // dz/dc update at z

    private static readonly Step Cube = (zr, zi, cr, ci, p) =>
    {
        var (sr, si) = M(zr, zi, zr, zi, p); var (tr, ti) = M(sr, si, zr, zi, p);
        return (BigFloat.Add(tr, cr, p), BigFloat.Add(ti, ci, p));
    };
    private static readonly Deriv CubeD = (zr, zi, vr, vi) =>
    {
        double ar = 3 * (zr * zr - zi * zi), ai = 6 * zr * zi;
        return (ar * vr - ai * vi + 1, ar * vi + ai * vr);
    };
    private static readonly Step QuadPlusZ = (zr, zi, cr, ci, p) =>
    {
        var (sr, si) = M(zr, zi, zr, zi, p);
        return (BigFloat.Add(BigFloat.Add(sr, zr, p), cr, p), BigFloat.Add(BigFloat.Add(si, zi, p), ci, p));
    };
    private static readonly Deriv QuadPlusZD = (zr, zi, vr, vi) =>
    {
        double ar = 2 * zr + 1, ai = 2 * zi;
        return (ar * vr - ai * vi + 1, ar * vi + ai * vr);
    };

    private static (int N, double Dv) Direct(Step f, Deriv df, BigFloat cr, BigFloat ci, int p, int maxIter)
    {
        BigFloat zr = BigFloat.Zero(p), zi = BigFloat.Zero(p);
        double vr = 0, vi = 0;
        for (int n = 0; n < maxIter; n++)
        {
            double a = zr.ToDouble(), b = zi.ToDouble();
            if (a * a + b * b >= 1024.0) return (n, Math.Sqrt(vr * vr + vi * vi));
            (vr, vi) = df(a, b, vr, vi);
            (zr, zi) = f(zr, zi, cr, ci, p);
        }
        return (maxIter, double.NaN);
    }

    private void Check(string name, Result got, Step f, Deriv df, ExactCentre centre, double scale, int maxIter)
    {
        int p = centre.Precision, total = 0, eq = 0, dvBad = 0;
        var seen = new System.Collections.Generic.HashSet<int>();
        string ex = "";
        for (int y = 1; y < H; y += 3)
            for (int x = 1; x < W; x += 3)
            {
                var cr = BigFloat.Add(centre.Re, BigFloat.FromDouble((x - W * 0.5) * scale, p), p);
                var ci = BigFloat.Add(centre.Im, BigFloat.FromDouble((y - H * 0.5) * scale, p), p);
                var (n, dvw) = Direct(f, df, cr, ci, p, maxIter);
                int idx = y * W + x;
                total++; seen.Add(n);
                if (got.Iter[idx] == n) eq++;
                else if (ex.Length < 160) ex += $"({x},{y}) {got.Iter[idx]} vs {n}; ";
                if (n < maxIter && got.Iter[idx] == n && double.IsFinite(dvw) && !(Math.Abs(got.Dv[idx] / dvw - 1) < 1e-2)) dvBad++;
            }
        output.WriteLine($"{name}: {eq}/{total} counts equal, {seen.Count} distinct, derivative off on {dvBad}; {got.Stats}");
        Assert.True(seen.Count >= 5, $"{name}: precondition, structure ({seen.Count} distinct counts)");
        Assert.True(eq >= total * 0.97, $"{name}: {eq}/{total} counts equal. {ex}");
        Assert.True(dvBad <= total / 50, $"{name}: derivative off by > 1 % on {dvBad} of {total}");   // S1782 / S1907's standard
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.3)]
    public void ZSquared_As_An_Ast_Plan_Matches_MandelbrotCalculator(double offset)
    {
        const double zoom = 2e33; const int maxIter = 13440;
        // MandelbrotCalculator escapes at |z|² ≥ 512²; User Equation's default is 1024 (a different count).
        var plan = Plan("z^2 + c", out bool canRebase, 512.0 * 512.0);
        Assert.True(canRebase, "z² + c: 0 is the critical point");
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
        output.WriteLine($"AstPlan z²+c vs MandelbrotCalculator, offset {offset}: {eq}/{W * H} equal; {got.Stats}");
        Assert.True(eq >= W * H * 0.98, $"{eq}/{W * H} equal counts");
    }

    // c = √(ω − 1), ω = e^{2πi/3} (S1907): z³ + c's critical orbit lands on a fixed point.
    private static ExactCentre CubeMisiurewicz(int p)
    {
        var three = BigFloat.FromDouble(3, p);
        var s3 = BigFloat.FromDouble(Math.Sqrt(3), p);
        for (int i = 0; i < 12; i++) s3 = BigFloat.Add(s3, BigFloat.Div(three, s3, p), p).ScaleB(-1);
        BigFloat ar = BigFloat.FromDouble(-1.5, p), ai = s3.ScaleB(-1);
        var seed = System.Numerics.Complex.Sqrt(new System.Numerics.Complex(-1.5, Math.Sqrt(3) / 2));
        BigFloat zr = BigFloat.FromDouble(seed.Real, p), zi = BigFloat.FromDouble(seed.Imaginary, p);
        for (int i = 0; i < 12; i++)
        {
            var den = BigFloat.Add(BigFloat.Mul(zr, zr, p), BigFloat.Mul(zi, zi, p), p);
            var qr = BigFloat.Div(BigFloat.Add(BigFloat.Mul(ar, zr, p), BigFloat.Mul(ai, zi, p), p), den, p);
            var qi = BigFloat.Div(BigFloat.Sub(BigFloat.Mul(ai, zr, p), BigFloat.Mul(ar, zi, p), p), den, p);
            zr = BigFloat.Add(zr, qr, p).ScaleB(-1); zi = BigFloat.Add(zi, qi, p).ScaleB(-1);
        }
        return new ExactCentre(zr, zi);
    }

    [Fact]
    public void ZCubed_Plus_C_Matches_Direct_Iteration()
    {
        const int zl = 40, maxIter = 1500;
        int p = BigFloat.PrecisionForZoom(zl);
        double scale = 3.5 / W / Math.Pow(10, zl);
        var centre = CubeMisiurewicz(p);
        var plan = Plan("z^3 + c", out bool canRebase);
        Assert.True(canRebase);
        Check("z^3 + c 1e40", Engine(plan, centre, scale, maxIter), Cube, CubeD, centre, scale, maxIter);
        // BLA off: every derivative step is the plan's (BLA's skip adds its own + B).
        Check("z^3 + c 1e40, no BLA", Engine(plan, centre, scale, maxIter, useBla: false), Cube, CubeD, centre, scale, maxIter);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(100)]
    public void ZSquared_Plus_Z_Plus_C_Matches_Direct_Iteration_Without_Rebasing(int zl)
    {
        int p = BigFloat.PrecisionForZoom(zl), maxIter = 400 + 10 * zl;
        double scale = 3.5 / W / Math.Pow(10, zl);
        // c* = (−3 − √5) / 2, by Newton for √5.
        var five = BigFloat.FromDouble(5, p);
        var s5 = BigFloat.FromDouble(Math.Sqrt(5), p);
        for (int i = 0; i < 12; i++) s5 = BigFloat.Add(s5, BigFloat.Div(five, s5, p), p).ScaleB(-1);
        var cs = BigFloat.Add(BigFloat.FromDouble(-3, p), -s5, p).ScaleB(-1);
        var centre = new ExactCentre(BigFloat.Add(cs, BigFloat.FromDouble(0.37 * scale, p), p), BigFloat.FromDouble(0.21 * scale, p));
        var plan = Plan("z^2 + z + c", out bool canRebase);
        Assert.False(canRebase, "z² + z + c: the critical point is −½, not z₀ = 0");
        var got = Engine(plan, centre, scale, maxIter);
        Assert.True(got.Stats.References > 1, $"precondition: secondary references ran ({got.Stats})");
        Check($"z^2 + z + c 1e{zl}", got, QuadPlusZ, QuadPlusZD, centre, scale, maxIter);
    }

    private static UserEquationCalculator Calc(string eq, double zoom, ExactCentre? at, bool engine, string? seed = null, string? cond = null)
    {
        var c = new UserEquationCalculator(W, H)
        {
            Zoom = zoom, MaxIterations = 1500, ColorMap = ColorPalette.BuiltIns[0], AllowPerturbation = engine,
            FractalParameters = new FractalParameters { UserEquationSource = eq, UserEquationSeed = seed, UserEquationBailoutCondition = cond },
        };
        var centre = at ?? ExactCentre.FromDeep(new DeepComplex(new OD(-0.7436447860), new OD(0.1318252536)), 64);
        c.SetDeepCentre(centre.ToDeep(), at);
        c.Calculate(CancellationToken.None);
        return c;
    }

    [Theory]
    [InlineData("z^2 + c")]
    [InlineData("z^2 + z + c")]
    public void Through_The_Calculator_The_Engine_Takes_Over_At_The_Switch(string eq)
    {
        double zoom = EscapeTimeCalculator.DeepZoomThreshold * 1.0001;
        // Structure at any depth: Mandelbrot's Misiurewicz point c = i / z² + z + c's c* = (−3 − √5)/2.
        ExactCentre? at = ExactCentre.FromDeep(eq == "z^2 + c"
            ? new DeepComplex(new OD(0), new OD(1))
            : new DeepComplex(new OD(-(3 + Math.Sqrt(5)) / 2), new OD(0)), 64);
        var interp = Calc(eq, zoom, at, engine: false);
        var pt = Calc(eq, zoom, at, engine: true);
        Assert.Equal("SP", interp.LastPrecisionLabel);
        Assert.True(pt.LastPrecisionLabel == "PT", $"{eq}: stayed on the interpreter ({pt.DeepRefusal})");
        int eqc = Enumerable.Range(0, W * H).Count(i => interp.IterationBuffer[i] == pt.IterationBuffer[i]);
        int distinct = interp.IterationBuffer.Take(W * H).Distinct().Count();
        output.WriteLine($"{eq} at 1e11: engine vs interpreter {eqc}/{W * H} equal, {distinct} distinct; {pt.LastPerturbationStats}");
        Assert.True(distinct >= 5, $"precondition: structure ({distinct})");
        Assert.True(eqc >= W * H * 0.9, $"{eq}: {eqc}/{W * H} equal at the switch");
    }

    [Theory]
    [InlineData("atan(z) + c", null, null, "Atan")]   // #1913: sin, cos, exp, log, pow now go deep; inverse trig does not
    [InlineData("z^2 + c", "c", null, "seed")]
    [InlineData("z^2 + c", null, "abs(z) < 1e-8", "condition")]
    public void Outside_The_Slice_The_Interpreter_Keeps_The_Frame(string eq, string? seed, string? cond, string reason)
    {
        var c = Calc(eq, 1e20, null, engine: true, seed, cond);
        Assert.Equal("SP", c.LastPrecisionLabel);
        Assert.Contains(reason, c.DeepRefusal ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
