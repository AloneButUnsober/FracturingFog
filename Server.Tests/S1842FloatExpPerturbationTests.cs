// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1842 (epic #1428, C5a) — floatexp per-pixel perturbation past double's range. Checked:
//   • FloatExp arithmetic against BigFloat over exponents far past double (±1e5 decades):
//     each operation within 2^-50 relative.
//   • Rendered iteration counts against the ground truth: every sampled pixel iterated
//     directly in BigFloat (z ← z² + c at the zoom's precision, same bailout), at 1e310,
//     1e400 and 1e1000 around the Misiurewicz point c = i, and at 1e600 around a centre that
//     needs digits OD can't hold. Before #1842, 52 of 64 pixels were wrong at 1e310 and 63
//     of 64 at 1e400 (δ, dc and the pixel scale underflow double).
//   • The floatexp loop forced at 1e150 gives the same counts as the truth (it is the same
//     algorithm), and up to the threshold (1e290) the double path is kept and still right.

using System;
using System.Threading;
using FracturingFog.FFMath;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1842FloatExpPerturbationTests
{
    // ── FloatExp against BigFloat ─────────────────────────────────────────────

    private static BigFloat Big(FloatExp v) => v.IsZero ? BigFloat.Zero(256) : BigFloat.FromDouble(v.M, 256).ScaleB(v.E);

    private static void AssertClose(BigFloat want, FloatExp got)
    {
        if (want.IsZero) { Assert.True(got.IsZero); return; }
        var err = BigFloat.Sub(Big(got), want, 256).Abs();
        Assert.True(err.ScaleB(50) <= want.Abs(), $"{got} vs {want}");
    }

    [Fact]
    public void FloatExp_Arithmetic_Matches_BigFloat_Far_Past_Double()
    {
        var r = new Random(18421);
        for (int i = 0; i < 2000; i++)
        {
            var a = FloatExp.FromDouble((r.NextDouble() * 2 - 1) * 1.5).ScaleB(r.Next(-330000, 330000));
            // b mostly within ±70 bits of a (sums that partly cancel, or partly vanish), a
            // third of the time anywhere.
            var b = FloatExp.FromDouble((r.NextDouble() * 2 - 1) * 1.5).ScaleB(a.E + r.Next(-70, 70) + (i % 3 == 0 ? r.Next(-330000, 330000) : 0));
            var A = Big(a); var B = Big(b);
            var p = a * b; var s = a + b; var d = a - b;
            AssertClose(BigFloat.Mul(A, B, 256), p);
            AssertClose(BigFloat.Add(A, B, 256), s);
            AssertClose(BigFloat.Sub(A, B, 256), d);
            Assert.Equal(A.Abs() < B.Abs(), FloatExp.AbsLess(a, b));
            // Results are normalised (1 ≤ |M| < 2), so they chain: a product compared with
            // and added to a value near it.
            foreach (var v in new[] { p, s, d })
                Assert.True(v.IsZero || (Math.Abs(v.M) >= 1 && Math.Abs(v.M) < 2), $"{v}");
            var q = FloatExp.FromDouble((r.NextDouble() * 2 - 1) * 1.5).ScaleB(p.E + r.Next(-2, 3));
            var P = Big(p); var Q = Big(q);
            Assert.Equal(P.Abs() < Q.Abs(), FloatExp.AbsLess(p, q));
            AssertClose(BigFloat.Add(P, Q, 256), p + q);
        }
        // Round trips with double, and the ends of double's range.
        foreach (double d in new[] { 0.0, 1.0, -3.75, 1e-300, 4.9e-324, 1.7e308 })
            Assert.Equal(d, FloatExp.FromDouble(d).ToDouble());
        Assert.Equal(0.0, FloatExp.FromLog10(-400).ToDouble());
        Assert.Equal(double.PositiveInfinity, FloatExp.FromLog10(400).ToDouble());
        Assert.Equal(-400 * 3.3219280948873622, FloatExp.FromLog10(-400).Log2Abs(), 9);
    }

    // ── rendered counts against direct BigFloat iteration ─────────────────────

    // Escape count of c = centre + (ox, oy)·pixel, iterated directly in BigFloat (bailout 512,
    // the calculator's). The pixel is 3.5 / max(W, H) · 10^-zl, built from decimal text.
    private static int Direct(ExactCentre centre, double ox, double oy, BigFloat pixel, int prec, int maxIter)
    {
        var cr = BigFloat.Add(centre.Re, BigFloat.Mul(pixel, BigFloat.FromDouble(ox, prec), prec), prec);
        var ci = BigFloat.Add(centre.Im, BigFloat.Mul(pixel, BigFloat.FromDouble(oy, prec), prec), prec);
        BigFloat zr = BigFloat.Zero(prec), zi = BigFloat.Zero(prec);
        for (int n = 0; n < maxIter; n++)
        {
            double a = zr.ToDouble(), b = zi.ToDouble();
            if (a * a + b * b >= 512.0 * 512.0) return n;
            var zr2 = BigFloat.Mul(zr, zr, prec); var zi2 = BigFloat.Mul(zi, zi, prec); var zri = BigFloat.Mul(zr, zi, prec);
            zi = BigFloat.Add(zri.ScaleB(1), ci, prec);
            zr = BigFloat.Add(BigFloat.Sub(zr2, zi2, prec), cr, prec);
        }
        return maxIter;
    }

    private static (MandelbrotCalculator Calc, ExactCentre Centre, int Prec) Render(int zl, bool tail, double fxAt = double.NaN)
    {
        const int W = 24, H = 24;
        int prec = BigFloat.PrecisionForZoom(zl);
        // c = i (a Misiurewicz point: structure at every depth), or a centre near it whose
        // offsets (≈1e-(zl-40)) OD can't hold next to i.
        var re = tail ? BigFloat.Parse("3.1415926535e-" + (zl - 40), prec) : BigFloat.Zero(prec);
        var im = tail ? BigFloat.Add(BigFloat.FromDouble(1, prec), BigFloat.Parse("2.7182818284e-" + (zl - 45), prec), prec)
                      : BigFloat.FromDouble(1, prec);
        var centre = new ExactCentre(re, im);
        var d = centre.ToDeep();
        var calc = new MandelbrotCalculator(W, H)
        {
            MaxIterations = MaxIt(zl), ColorMap = new HsvPalette(),
            CenterX = d.Re.X0, CenterXLo = d.Re.X1, CenterX2 = d.Re.X2, CenterX3 = d.Re.X3,
            CenterX4 = d.Re.X4, CenterX5 = d.Re.X5, CenterX6 = d.Re.X6, CenterX7 = d.Re.X7,
            CenterY = d.Im.X0, CenterYLo = d.Im.X1, CenterY2 = d.Im.X2, CenterY3 = d.Im.X3,
            CenterY4 = d.Im.X4, CenterY5 = d.Im.X5, CenterY6 = d.Im.X6, CenterY7 = d.Im.X7,
        };
        calc.SetZoomLog10(zl);
        calc.ApReferenceZoomLog10 = 100;   // the C4 orbit from 1e100 (the default 110 is the cap's)
        if (!double.IsNaN(fxAt)) calc.FloatExpZoomLog10 = fxAt;   // NaN: the default
        calc.SetDepth(centre);
        calc.Calculate(CancellationToken.None);
        return (calc, centre, prec);
    }

    // Pixels near c = i escape after ≈ zl / log10(4√2) iterations; room to spare.
    private static int MaxIt(int zl) => 600 + 2 * zl;

    private static void AssertMatchesDirect(int zl, bool tail, int stride, double fxAt = double.NaN)
    {
        var (calc, centre, prec) = Render(zl, tail, fxAt);
        var pixel = BigFloat.Div(BigFloat.Parse($"3.5e-{zl}", prec), BigFloat.FromDouble(24, prec), prec);
        int bad = 0, total = 0; string ex = "";
        for (int y = 0; y < 24; y += stride)
        for (int x = 0; x < 24; x += stride)
        {
            int want = Direct(centre, x - 12, y - 12, pixel, prec, calc.MaxIterations);
            int got = calc.IterationBuffer[y * 24 + x];
            total++;
            if (got != want) { bad++; if (ex.Length < 200) ex += $"({x},{y}) {got} vs {want}; "; }
        }
        Assert.True(bad == 0, $"1e{zl}{(tail ? " (tail)" : "")}: {bad}/{total} wrong [{calc.LastPrecisionLabel}] {ex}");
    }

    [Theory]
    [InlineData(310, false, 3)]
    [InlineData(400, false, 3)]
    [InlineData(1000, false, 6)]
    [InlineData(600, true, 4)]
    public void Past_Double_Every_Sampled_Pixel_Is_The_Direct_Count(int zl, bool tail, int stride)
    {
        AssertMatchesDirect(zl, tail, stride);
        var calc = Render(zl, tail).Calc;
        Assert.EndsWith(" FX", calc.LastPrecisionLabel);
        // Escaped pixels reach the colouring with a finite smooth value n + 1 − log2(log2|z|), |z| in [512, 512²): within [n − 3.17, n − 2.17].
        // (Normals and FinalDr/Di overflow at this depth on the double path too: #1843.)
        for (int i = 0; i < 24 * 24; i++)
        {
            int n = calc.IterationBuffer[i];
            if (n >= calc.MaxIterations) continue;
            float sm = calc.SmoothBuffer[i];
            Assert.True(float.IsFinite(sm) && sm >= n - 3.2 && sm <= n - 2.1, $"smooth {i}: {sm} for {n}");
            Assert.False(float.IsNaN(calc.NormalXBuffer[i]) || float.IsNaN(calc.NormalYBuffer[i]), $"normal {i}");
        }
    }

    // A frame with a minibrot off-centre, forced onto the floatexp loop, against direct
    // iteration. The reference (the view centre) is outside the minibrot, so the pixels
    // inside it have orbits that return near 0 away from the reference's: without the
    // |z| < |δ| rebase (Zhuoran) they glitch as a block.
    [Fact]
    public void Forced_With_A_Minibrot_In_View_The_Floatexp_Loop_Rebases()
    {
        const int W = 32, H = 32;
        var seed = DeepComplex.FromLimbs(-0.743643887037158704752191506114774, 0, 0, 0, 0, 0, 0, 0,
                                          0.131825904205311970493132056385139, 0, 0, 0, 0, 0, 0, 0);
        var found = FracturingFog.Abstractions.Explore.NucleusFinder.FindMinibrot(seed, 1e-13);
        Assert.True(found.Found, found.Status.ToString());
        double size = found.Size.Magnitude;
        int maxIt = Math.Min(20 * found.Period, 20000);
        // View: 5 atom sizes wide, the nucleus 1.5 sizes left of centre.
        double zoom = 3.5 / (5 * size);
        int prec = BigFloat.PrecisionForZoom(Math.Log10(zoom)) + 64;
        var nuc = ExactCentre.FromDeep(found.Nucleus, prec);
        var centre = new ExactCentre(BigFloat.Add(nuc.Re, BigFloat.FromDouble(1.5 * size, prec), prec), nuc.Im);
        var d = centre.ToDeep();
        MandelbrotCalculator Run(double fxAt)
        {
            var c = new MandelbrotCalculator(W, H)
            {
                MaxIterations = maxIt, ColorMap = new HsvPalette(), Zoom = zoom,
                CenterX = d.Re.X0, CenterXLo = d.Re.X1, CenterX2 = d.Re.X2, CenterX3 = d.Re.X3,
                CenterX4 = d.Re.X4, CenterX5 = d.Re.X5, CenterX6 = d.Re.X6, CenterX7 = d.Re.X7,
                CenterY = d.Im.X0, CenterYLo = d.Im.X1, CenterY2 = d.Im.X2, CenterY3 = d.Im.X3,
                CenterY4 = d.Im.X4, CenterY5 = d.Im.X5, CenterY6 = d.Im.X6, CenterY7 = d.Im.X7,
            };
            c.FloatExpZoomLog10 = fxAt;
            c.Calculate(CancellationToken.None);
            return c;
        }
        var fx = Run(0);
        Assert.EndsWith(" FX", fx.LastPrecisionLabel);
        var pixel = BigFloat.Div(BigFloat.FromDouble(3.5 / zoom, prec), BigFloat.FromDouble(W, prec), prec);
        int bad = 0, total = 0, inside = 0;
        for (int y = 0; y < H; y += 2)
        for (int x = 0; x < W; x += 2)
        {
            total++;
            int want = Direct(centre, x - W / 2, y - H / 2, pixel, prec, maxIt);
            if (want >= maxIt) inside++;
            if (fx.IterationBuffer[y * W + x] != want) bad++;
        }
        Assert.True(inside >= 4, $"minibrot pixels {inside}/{total} (period {found.Period}, size {size:E2})");
        // Chaotic boundary pixels may differ by rounding (measured 4 / 256; without the
        // rebase 30 / 256). (The default double path, with BLA, is further off here — ±1 to
        // ±370 iterations on 48 / 256: BLA's approximation, #1346 — so it is not the oracle.)
        Assert.True(bad <= total / 25, $"{bad}/{total} differ from direct iteration (period {found.Period}, inside {inside})");
    }

    [Fact]
    public void Forced_At_1e150_The_Floatexp_Loop_Is_Right_And_Up_To_The_Threshold_The_Double_Path_Stays()
    {
        AssertMatchesDirect(150, false, 3, fxAt: 100);
        Assert.EndsWith(" FX", Render(150, false, fxAt: 100).Calc.LastPrecisionLabel);

        AssertMatchesDirect(290, true, 3);
        Assert.DoesNotContain("FX", Render(290, true).Calc.LastPrecisionLabel);
    }
}
