// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1907 (epic #1253, D2) — the generic perturbation engine (Engine/Perturbation) with the z^d + c
// plan, and Julia / Multibrot on it past 1e11. Each check against an independent oracle:
//   • the engine with z² + c (parameter plane) against MandelbrotCalculator, a separate engine, at
//     the p448 view (2e33), centred on the minibrot and with it 0.3 views off-centre: equal counts;
//   • Julia (c = −0.8 + 0.156i) at 1e30 and 1e120, centred a fraction of a pixel off its repelling
//     fixed point (found by Newton in BigFloat), so the view has structure at every depth and the
//     reference escapes early: secondary references must run. Against direct BigFloat iteration of
//     each sampled pixel, written here: counts equal, and the derivative's size within 1 % on 98 %;
//   • Multibrot d = 3 at a Misiurewicz point (c = √(ω − 1), Newton in BigFloat) at 1e40 and 1e200: the same
//     oracle (rebasing is exercised by the z² + c case: thousands of rebases);
//   • at the switch (1e11) the engine agrees with the SP core it replaces above it.
// Mutants (by hand): the |z| < |δ| rebase removed — caught by the off-centre z² + c view; ε dropped
// from the δ step — caught (5 cases); the BLA skip's derivative without + B — caught (Multibrot
// 1e40). Survives: the dynamic plane's Pauldelbrot test removed. A Julia glitch needs |z| below
// ~1e-8·|Z|, which on these views is sub-pixel; the reference running out (which the views do
// exercise) is caught separately. The test stays as the published safety net.

using System;
using System.Linq;
using System.Threading;
using FracturingFog;
using FracturingFog.FFMath;
using FracturingFog.Models;
using FracturingFog.Perturbation;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1907PerturbationEngineTests(ITestOutputHelper output)
{
    private const int W = 48, H = 32;

    // ── an independent oracle: z^d + c iterated directly in BigFloat ─────────────────────

    private static (BigFloat R, BigFloat I) Mul(BigFloat ar, BigFloat ai, BigFloat br, BigFloat bi, int p)
        => (BigFloat.Sub(BigFloat.Mul(ar, br, p), BigFloat.Mul(ai, bi, p), p),
            BigFloat.Add(BigFloat.Mul(ar, bi, p), BigFloat.Mul(ai, br, p), p));

    private static (int N, double Dv) Direct(int d, bool parameterPlane, BigFloat pr, BigFloat pi, BigFloat jr, BigFloat ji, int p, int maxIter)
    {
        BigFloat zr, zi, cr, ci;
        if (parameterPlane) { zr = BigFloat.Zero(p); zi = BigFloat.Zero(p); cr = pr; ci = pi; }
        else { zr = pr; zi = pi; cr = jr; ci = ji; }
        double vr = 1, vi = 0;
        for (int n = 0; n < maxIter; n++)
        {
            double a = zr.ToDouble(), b = zi.ToDouble();
            if (a * a + b * b >= 512.0 * 512.0) return (n, Math.Sqrt(vr * vr + vi * vi));
            // derivative d·z^{d−1}·v (+1 in the parameter plane), on the rounded z
            double qr = 1, qi = 0;
            for (int k = 1; k < d; k++) { double t = qr * a - qi * b; qi = qr * b + qi * a; qr = t; }
            double nr = d * (qr * vr - qi * vi) + (parameterPlane ? 1 : 0), ni = d * (qr * vi + qi * vr);
            vr = nr; vi = ni;
            BigFloat wr = zr, wi = zi;
            for (int k = 1; k < d; k++) (wr, wi) = Mul(wr, wi, zr, zi, p);
            zr = BigFloat.Add(wr, cr, p); zi = BigFloat.Add(wi, ci, p);
        }
        return (maxIter, double.NaN);
    }

    private sealed record Result(int[] Iter, double[] Dv, PerturbationStats Stats);

    private static Result Engine(IPerturbationPlan plan, ExactCentre centre, double scale, int maxIter)
    {
        var it = new int[W * H];
        var dv = new double[W * H];
        var st = PerturbationEngine.Render(plan, centre, scale, W, H, maxIter,
            (idx, n, zr, zi, a, b) => { it[idx] = n; dv[idx] = Math.Sqrt(a * a + b * b); }, CancellationToken.None);
        return new Result(it, dv, st);
    }

    // Compare the engine with the oracle on every 3rd pixel; returns (sampled, count equal, varied).
    private void Check(string name, Result got, int d, bool pp, ExactCentre centre, double scale, BigFloat jr, BigFloat ji, int maxIter)
    {
        int p = centre.Precision, total = 0, eq = 0, dvBad = 0;
        var seen = new System.Collections.Generic.HashSet<int>();
        string ex = "";
        for (int y = 1; y < H; y += 3)
            for (int x = 1; x < W; x += 3)
            {
                var pr = BigFloat.Add(centre.Re, BigFloat.FromDouble((x - W * 0.5) * scale, p), p);
                var pi = BigFloat.Add(centre.Im, BigFloat.FromDouble((y - H * 0.5) * scale, p), p);
                var (n, dvw) = Direct(d, pp, pr, pi, jr, ji, p, maxIter);
                int idx = y * W + x;
                total++; seen.Add(n);
                if (got.Iter[idx] == n) eq++;
                else if (ex.Length < 160) ex += $"({x},{y}) {got.Iter[idx]} vs {n}; ";
                // The derivative runs in double on both sides: past ~1e308 (Multibrot d = 3 at 1e200)
                // both overflow to ∞ and there is nothing to compare (the distance unit, #1783, comes
                // with the float-exp tier).
                if (n < maxIter && got.Iter[idx] == n && double.IsFinite(dvw) && !(Math.Abs(got.Dv[idx] / dvw - 1) < 1e-2))
                {
                    dvBad++;
                    if (dvBad <= 5) output.WriteLine($"  dv ({x},{y}) n={n}: {got.Dv[idx]:G6} vs {dvw:G6}");
                }
            }
        output.WriteLine($"{name}: {eq}/{total} counts equal, {seen.Count} distinct, derivative off on {dvBad}; {got.Stats}");
        Assert.True(seen.Count >= 5, $"{name}: precondition, structure in the view ({seen.Count} distinct counts)");
        Assert.True(eq >= total * 0.97, $"{name}: {eq}/{total} counts equal. {ex}");
        // S1782's standard for the BLA derivative: within 1 % on ≥ 98 % of pixels. The misses are
        // pixels whose orbit passes near the critical point, where the derivative is ill-conditioned:
        // BLA's ε-sized δ error becomes a large relative error in z there (at 1e30, ε 1e-10: 3 of 176
        // over 1 %; at ε 1e-14 and with BLA off, none).
        Assert.True(dvBad <= total / 50, $"{name}: derivative off by > 1 % on {dvBad} of {total}");
    }

    // Julia's repelling fixed point z = (1 + sqrt(1 − 4c)) / 2, by Newton on z² − z + c in BigFloat.
    private static (BigFloat R, BigFloat I) JuliaFixedPoint(double cr, double ci, int p)
    {
        BigFloat zr = BigFloat.FromDouble(1.2, p), zi = BigFloat.FromDouble(-0.1, p);
        var kr = BigFloat.FromDouble(cr, p); var ki = BigFloat.FromDouble(ci, p);
        var one = BigFloat.FromDouble(1, p);
        for (int i = 0; i < 200; i++)
        {
            var (sr, si) = Mul(zr, zi, zr, zi, p);
            var fr = BigFloat.Add(BigFloat.Sub(sr, zr, p), kr, p); var fi = BigFloat.Add(BigFloat.Sub(si, zi, p), ki, p);
            var gr = BigFloat.Sub(zr.ScaleB(1), one, p); var gi = zi.ScaleB(1);
            var den = BigFloat.Add(BigFloat.Mul(gr, gr, p), BigFloat.Mul(gi, gi, p), p);
            var qr = BigFloat.Div(BigFloat.Add(BigFloat.Mul(fr, gr, p), BigFloat.Mul(fi, gi, p), p), den, p);
            var qi = BigFloat.Div(BigFloat.Sub(BigFloat.Mul(fi, gr, p), BigFloat.Mul(fr, gi, p), p), den, p);
            zr = BigFloat.Sub(zr, qr, p); zi = BigFloat.Sub(zi, qi, p);
        }
        return (zr, zi);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(120)]
    public void Julia_Matches_Direct_Iteration_With_Secondary_References(int zl)
    {
        const double cr = -0.8, ci = 0.156;
        int p = BigFloat.PrecisionForZoom(zl);
        double scale = 3.5 / W / Math.Pow(10, zl);
        var (fr, fi) = JuliaFixedPoint(cr, ci, p);
        // A third of a pixel off the fixed point: the reference escapes early, the pixels around it don't.
        var centre = new ExactCentre(BigFloat.Add(fr, BigFloat.FromDouble(0.37 * scale, p), p), BigFloat.Add(fi, BigFloat.FromDouble(0.21 * scale, p), p));
        int maxIter = 400 + 12 * zl;
        var plan = new PowerPlan(2, parameterPlane: false, cr, ci);
        var got = Engine(plan, centre, scale, maxIter);
        Assert.True(got.Stats.References > 1, $"precondition: secondary references ran ({got.Stats})");
        Check($"Julia 1e{zl}", got, 2, false, centre, scale, BigFloat.FromDouble(cr, p), BigFloat.FromDouble(ci, p), maxIter);
    }

    // A Misiurewicz point of z³ + c (structure at every depth): c² = ω − 1, ω = e^{2πi/3}, so
    // c³ + c = c·ω and (cω)³ + c = c·ω: the critical orbit 0 → c → cω → cω lands on a fixed point.
    // c = √(ω − 1) by Newton in BigFloat (√3 first, then the complex root).
    private static (BigFloat R, BigFloat I) MultibrotMisiurewicz(int p)
    {
        var three = BigFloat.FromDouble(3, p);
        var s3 = BigFloat.FromDouble(Math.Sqrt(3), p);
        for (int i = 0; i < 12; i++) s3 = BigFloat.Add(s3, BigFloat.Div(three, s3, p), p).ScaleB(-1);
        BigFloat ar = BigFloat.FromDouble(-1.5, p), ai = s3.ScaleB(-1);
        var seed = System.Numerics.Complex.Sqrt(new System.Numerics.Complex(-1.5, Math.Sqrt(3) / 2));
        BigFloat zr = BigFloat.FromDouble(seed.Real, p), zi = BigFloat.FromDouble(seed.Imaginary, p);
        for (int i = 0; i < 12; i++)
        {
            // z ← (z + a/z) / 2
            var den = BigFloat.Add(BigFloat.Mul(zr, zr, p), BigFloat.Mul(zi, zi, p), p);
            var qr = BigFloat.Div(BigFloat.Add(BigFloat.Mul(ar, zr, p), BigFloat.Mul(ai, zi, p), p), den, p);
            var qi = BigFloat.Div(BigFloat.Sub(BigFloat.Mul(ai, zr, p), BigFloat.Mul(ar, zi, p), p), den, p);
            zr = BigFloat.Add(zr, qr, p).ScaleB(-1); zi = BigFloat.Add(zi, qi, p).ScaleB(-1);
        }
        return (zr, zi);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(200)]
    public void Multibrot_Matches_Direct_Iteration(int zl)
    {
        const int d = 3;
        int p = BigFloat.PrecisionForZoom(zl);
        double scale = 3.5 / W / Math.Pow(10, zl);
        int maxIter = 1500;
        var (br, bi) = MultibrotMisiurewicz(p);
        var centre = new ExactCentre(br, bi);
        var got = Engine(new PowerPlan(d, parameterPlane: true), centre, scale, maxIter);
        Assert.True(got.Stats.ReferenceLength <= maxIter, $"precondition: the boundary point escapes ({got.Stats})");
        Check($"Multibrot d=3 1e{zl}", got, d, true, centre, scale, BigFloat.Zero(p), BigFloat.Zero(p), maxIter);
    }

    // offset: the view centre moved by that many view widths from the p448 minibrot. At 0 the
    // reference sits on the minibrot; at 0.3 the minibrot is off-centre, the reference is elsewhere,
    // and the minibrot's pixels pass near 0 where the reference does not: Zhuoran's rebase carries them.
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.3)]
    public void The_Engine_On_ZSquared_Plus_C_Matches_MandelbrotCalculator(double offset)
    {
        const double zoom = 2e33; const int maxIter = 13440;   // the p448 minibrot fills pixels here
        double[] re = [-0.7145519151951202, -2.0790192891565988e-17, 1.1753166641509792e-33, -8.102457136962753e-50];
        double[] im = [-0.35402073332318235, 2.5477179573089096e-17, -6.072997541261718e-34, -1.8911088656798612e-50];
        var od = OD.FromCenterOffset(new OD(re[0], re[1], re[2], re[3], 0, 0, 0, 0), offset * W, 3.5 / W / zoom);
        var m = new MandelbrotCalculator(W, H)
        {
            Quality = QualityPreset.Ultra, Zoom = zoom, MaxIterations = maxIter, ColorMap = ColorPalette.BuiltIns[0],
            CenterX = od.X0, CenterXLo = od.X1, CenterX2 = od.X2, CenterX3 = od.X3, CenterX4 = od.X4, CenterX5 = od.X5,
            CenterY = im[0], CenterYLo = im[1], CenterY2 = im[2], CenterY3 = im[3],
        };
        m.Calculate(CancellationToken.None);
        var centre = ExactCentre.FromDeep(new DeepComplex(od, new OD(im[0], im[1], im[2], im[3], 0, 0, 0, 0)),
                                          BigFloat.PrecisionForZoom(34));
        var got = Engine(new PowerPlan(2, parameterPlane: true), centre, 3.5 / W / zoom, maxIter);
        int eq = Enumerable.Range(0, W * H).Count(i => got.Iter[i] == m.IterationBuffer[i]);
        int inSet = m.IterationBuffer.Take(W * H).Count(n => n >= maxIter);
        output.WriteLine($"z²+c vs MandelbrotCalculator at 2e33, offset {offset}: {eq}/{W * H} equal, {inSet} in set; {got.Stats}");
        Assert.True(inSet > 20, $"precondition: the minibrot is in view ({inSet} in-set pixels)");
        // Both against direct iteration on the sampled pixels.
        var mres = new Result(m.IterationBuffer.Take(W * H).ToArray(), new double[W * H], default);
        int mEq = 0, eEq = 0, tot = 0;
        for (int y = 1; y < H; y += 3)
            for (int x = 1; x < W; x += 3)
            {
                int p2 = centre.Precision;
                var pr = BigFloat.Add(centre.Re, BigFloat.FromDouble((x - W * 0.5) * (3.5 / W / zoom), p2), p2);
                var pi = BigFloat.Add(centre.Im, BigFloat.FromDouble((y - H * 0.5) * (3.5 / W / zoom), p2), p2);
                int n = Direct(2, true, pr, pi, BigFloat.Zero(p2), BigFloat.Zero(p2), p2, maxIter).N;
                tot++; if (mres.Iter[y * W + x] == n) mEq++; if (got.Iter[y * W + x] == n) eEq++;
            }
        output.WriteLine($"  vs direct: Mandelbrot {mEq}/{tot}, engine {eEq}/{tot}");
        Assert.True(eq >= W * H * 0.98, $"{eq}/{W * H} equal counts");
    }

    private static System.Numerics.Complex MultibrotBoundaryDouble()
    {
        double lo = 0, hi = 1;
        for (int s = 0; s < 60; s++)
        {
            double mid = 0.5 * (lo + hi);
            var c = new System.Numerics.Complex(mid, mid);
            var z = System.Numerics.Complex.Zero;
            int n = 0;
            for (; n < 1500 && z.Magnitude < 512; n++) z = z * z * z + c;
            if (n < 1500) hi = mid; else lo = mid;
        }
        return new System.Numerics.Complex(hi, hi);
    }

    [Theory]
    [InlineData(FractalType.Julia)]
    [InlineData(FractalType.Multibrot)]
    public void At_The_Switch_The_Engine_Agrees_With_The_Sp_Core(FractalType t)
    {
        EscapeTimeCalculator Calc(bool engine)
        {
            var c = new EscapeTimeCalculator(W, H)
            {
                FractalType = t, Zoom = EscapeTimeCalculator.DeepZoomThreshold * 1.0001, MaxIterations = 1500,
                ColorMap = ColorPalette.BuiltIns[0], AllowPerturbation = engine,
                FractalParameters = new FractalParameters { JuliaC = new System.Numerics.Complex(-0.8, 0.156), MultibrotExponent = 3 },
            };
            // Structure at any depth: Julia's repelling fixed point (1 + sqrt(1 − 4c)) / 2, and a
            // Multibrot d = 3 boundary point (bisection in double along 0 → 1 + i).
            var at = t == FractalType.Julia
                ? (System.Numerics.Complex.One + System.Numerics.Complex.Sqrt(1 - 4 * new System.Numerics.Complex(-0.8, 0.156))) / 2
                : MultibrotBoundaryDouble();
            c.SetDeepCentre(new DeepComplex(new OD(at.Real), new OD(at.Imaginary)), null);
            c.Calculate(CancellationToken.None);
            return c;
        }
        var sp = Calc(false); var pt = Calc(true);
        Assert.Equal("PT", pt.LastPrecisionLabel);
        Assert.Equal("SP", sp.LastPrecisionLabel);
        int eq = Enumerable.Range(0, W * H).Count(i => sp.IterationBuffer[i] == pt.IterationBuffer[i]);
        int distinct = sp.IterationBuffer.Take(W * H).Distinct().Count();
        output.WriteLine($"{t} at 1e11: engine vs SP {eq}/{W * H} equal, {distinct} distinct counts; {pt.LastPerturbationStats}");
        Assert.True(distinct >= 5, $"precondition: structure ({distinct} distinct)");
        Assert.True(eq >= W * H * 0.9, $"{t}: {eq}/{W * H} equal at the switch");
    }
}
