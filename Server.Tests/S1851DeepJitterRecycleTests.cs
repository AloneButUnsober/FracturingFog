// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1851 (epic #1428, C5e) — AA jitter and orbit recycling past OD. The oracle (as S1842):
// each sampled pixel iterated directly in BigFloat at its true position. Checked:
//   • A jittered AA sample (CentreJitter.Apply + Calculate, as MSAA / TAA run it) at 1e150
//     around a centre past OD, and at 1e400 (floatexp rows), renders the pixels at their
//     jittered positions: every sampled count is the direct count there. The exact centre
//     survives the sample (before #1851 the limb jitter dropped it, and the sample rendered
//     the OD-rounded centre, a couple of pixels off here), and Restore clears the jitter.
//     The view is about two pixels from a Misiurewicz point (dense digits, computed by
//     Newton in BigFloat), so it has structure at pixel scale and the counts and smooth
//     values see sub-pixel shifts.
//   • A one-pixel exact pan at 1e150 (a step the dense centre's OD limbs can't hold) recycles the reference orbit (tier 3: the exact shift,
//     C4) and every sampled count is the direct count of the moved view.
//   • Below the AP threshold the jitter still moves the limbs (unchanged).

using System;
using System.Threading;
using FracturingFog.FFMath;
using FracturingFog.Models;
using FracturingFog.Rendering;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1851DeepJitterRecycleTests
{
    private const int W = 24, H = 24;

    // (count, smooth value as FinalizeOrbitPixel / FillAuxAndColorHP compute it).
    private static (int N, double Smooth) Direct(ExactCentre centre, double ox, double oy, BigFloat pixel, int prec, int maxIter)
    {
        var cr = BigFloat.Add(centre.Re, BigFloat.Mul(pixel, BigFloat.FromDouble(ox, prec), prec), prec);
        var ci = BigFloat.Add(centre.Im, BigFloat.Mul(pixel, BigFloat.FromDouble(oy, prec), prec), prec);
        BigFloat zr = BigFloat.Zero(prec), zi = BigFloat.Zero(prec);
        for (int n = 0; n < maxIter; n++)
        {
            double a = zr.ToDouble(), b = zi.ToDouble();
            if (a * a + b * b >= 512.0 * 512.0) return (n, n + 1.0 - Math.Log2(Math.Log2(Math.Sqrt(a * a + b * b))));
            var zr2 = BigFloat.Mul(zr, zr, prec); var zi2 = BigFloat.Mul(zi, zi, prec); var zri = BigFloat.Mul(zr, zi, prec);
            zi = BigFloat.Add(zri.ScaleB(1), ci, prec);
            zr = BigFloat.Add(BigFloat.Sub(zr2, zi2, prec), cr, prec);
        }
        return (maxIter, 0);
    }

    // The Misiurewicz point near -0.1011 + 0.9563i (z_4 = −z_3, so z_4 is the fixed point:
    // preperiod 4, period 1; its multiplier 2·z_4 ≈ 1.33), to full precision by Newton in
    // BigFloat on g(c) = z_3(c) + z_4(c). (z_4 − z_3 also vanishes at c = 0.) Its digits are dense,
    // so OD's limbs hold only its first ~424 bits (a sparse value like 1 + 1e-151 would fit
    // OD exactly), and it has structure at every scale, so sub-pixel shifts show.
    private static ExactCentre Misiurewicz(int prec)
    {
        var cr = BigFloat.Parse("-0.10109636384562", prec);
        var ci = BigFloat.Parse("0.95628651080914", prec);
        for (int step = 0; step < 40; step++)
        {
            BigFloat zr = BigFloat.Zero(prec), zi = BigFloat.Zero(prec), dr = BigFloat.Zero(prec), di = BigFloat.Zero(prec);
            BigFloat z3r = zr, z3i = zi, d3r = dr, d3i = di;
            for (int n = 1; n <= 4; n++)
            {
                // dz' = 2·z·dz + 1, z' = z² + c
                var ndr = BigFloat.Add(BigFloat.Sub(BigFloat.Mul(zr, dr, prec), BigFloat.Mul(zi, di, prec), prec).ScaleB(1), BigFloat.FromDouble(1, prec), prec);
                var ndi = BigFloat.Add(BigFloat.Mul(zr, di, prec), BigFloat.Mul(zi, dr, prec), prec).ScaleB(1);
                var nzr = BigFloat.Add(BigFloat.Sub(BigFloat.Mul(zr, zr, prec), BigFloat.Mul(zi, zi, prec), prec), cr, prec);
                var nzi = BigFloat.Add(BigFloat.Mul(zr, zi, prec).ScaleB(1), ci, prec);
                zr = nzr; zi = nzi; dr = ndr; di = ndi;
                if (n == 3) { z3r = zr; z3i = zi; d3r = dr; d3i = di; }
            }
            var gr = BigFloat.Add(zr, z3r, prec); var gi = BigFloat.Add(zi, z3i, prec);
            var hr = BigFloat.Add(dr, d3r, prec); var hi = BigFloat.Add(di, d3i, prec);
            var den = BigFloat.Add(BigFloat.Mul(hr, hr, prec), BigFloat.Mul(hi, hi, prec), prec);
            var qr = BigFloat.Div(BigFloat.Add(BigFloat.Mul(gr, hr, prec), BigFloat.Mul(gi, hi, prec), prec), den, prec);
            var qi = BigFloat.Div(BigFloat.Sub(BigFloat.Mul(gi, hr, prec), BigFloat.Mul(gr, hi, prec), prec), den, prec);
            cr = BigFloat.Sub(cr, qr, prec); ci = BigFloat.Sub(ci, qi, prec);
        }
        return new ExactCentre(cr, ci);
    }

    // About two pixels from that Misiurewicz point.
    private static ExactCentre Centre(int zl)
    {
        int prec = BigFloat.PrecisionForZoom(zl) + 64;
        var m = Misiurewicz(prec);
        return new ExactCentre(BigFloat.Add(m.Re, BigFloat.Parse($"3.1415926535e-{zl + 1}", prec), prec),
                               BigFloat.Add(m.Im, BigFloat.Parse($"2.7182818284e-{zl + 1}", prec), prec));
    }

    private static MandelbrotCalculator Calc(ExactCentre c, int zl)
    {
        var d = c.ToDeep();
        var calc = new MandelbrotCalculator(W, H)
        {
            // Pixels near the point escape after ≈ zl / log10(1.33) ≈ 8·zl iterations.
            MaxIterations = 1000 + 9 * zl, ColorMap = new HsvPalette(),
            CenterX = d.Re.X0, CenterXLo = d.Re.X1, CenterX2 = d.Re.X2, CenterX3 = d.Re.X3,
            CenterX4 = d.Re.X4, CenterX5 = d.Re.X5, CenterX6 = d.Re.X6, CenterX7 = d.Re.X7,
            CenterY = d.Im.X0, CenterYLo = d.Im.X1, CenterY2 = d.Im.X2, CenterY3 = d.Im.X3,
            CenterY4 = d.Im.X4, CenterY5 = d.Im.X5, CenterY6 = d.Im.X6, CenterY7 = d.Im.X7,
        };
        calc.SetZoomLog10(zl);
        calc.SetDepth(c);
        return calc;
    }

    private static void AssertDirect(MandelbrotCalculator calc, ExactCentre centre, int zl, double jx, double jy)
    {
        int prec = BigFloat.PrecisionForZoom(zl);
        var pixel = BigFloat.Div(BigFloat.Parse($"3.5e-{zl}", prec), BigFloat.FromDouble(W, prec), prec);
        int bad = 0, total = 0, escaped = 0; string ex = "";
        for (int y = 0; y < H; y += 3)
        for (int x = 0; x < W; x += 3)
        {
            total++;
            // The smooth value too: a sub-pixel jitter moves it continuously (the integer
            // count can be the same for neighbouring pixels).
            var (want, sm) = Direct(centre, x - W / 2 + jx, y - H / 2 + jy, pixel, prec, calc.MaxIterations);
            int got = calc.IterationBuffer[y * W + x];
            float gotSm = calc.SmoothBuffer[y * W + x];
            if (want < calc.MaxIterations) escaped++;
            // 1e-2: LA / BLA approximate (measured 2.6e-3 at worst); half a pixel moves the
            // smooth value by ~0.1 here.
            if (got != want || (want < calc.MaxIterations && Math.Abs(gotSm - sm) > 1e-2))
            { bad++; if (ex.Length < 200) ex += $"({x},{y}) {got}/{gotSm:F4} vs {want}/{sm:F4}; "; }
        }
        Assert.True(bad == 0, $"1e{zl} jitter ({jx},{jy}): {bad}/{total} wrong [{calc.LastPrecisionLabel}] {ex}");
        Assert.True(escaped >= total / 2, $"{escaped}/{total} escaped: the frame must have structure");
    }

    [Theory]
    [InlineData(150)]
    [InlineData(400)]
    public void A_Jittered_Sample_Past_OD_Renders_The_Jittered_Pixels(int zl)
    {
        var c = Centre(zl);
        var calc = Calc(c, zl);
        Assert.True(calc.JittersByPixel);
        Assert.True(CentreJitter.IsResolvable(calc, 0));   // TAA keeps refining past OD's floor
        calc.Calculate(CancellationToken.None);   // the unjittered sample (builds the orbit)
        var orig = CentreLimbs.Capture(calc);
        CentreJitter.Apply(calc, in orig, 0.5, -0.25, 3.5 / W / calc.Zoom);
        Assert.Equal(c, calc.CenterExact);         // the limbs stayed: the exact centre too
        Assert.Equal((0.5, -0.25), calc.PixelJitter);
        CentreJitter.Calculate(calc, CancellationToken.None);
        AssertDirect(calc, c, zl, 0.5, -0.25);
        orig.Restore(calc);
        Assert.Equal(default, calc.PixelJitter);
    }

    [Fact]
    public void A_One_Pixel_Exact_Pan_Past_OD_Recycles_The_Orbit_And_Renders_The_Moved_View()
    {
        const int zl = 150;
        int prec = BigFloat.PrecisionForZoom(zl);
        var c = Centre(zl);
        var calc = Calc(c, zl);
        calc.AllowRecycleThisRender = true;
        calc.Calculate(CancellationToken.None);
        long hits = calc.RefRecycleHits;
        // Move the view one pixel down, exactly (ViewCamera's exact move, C3). The centre's
        // digits are dense, so its OD limbs can't hold a 1e-151 step (the moved centre has
        // the same limbs): only the exact difference (tier 3) gives the shift.
        var pixel = BigFloat.Div(BigFloat.Parse($"3.5e-{zl}", prec), BigFloat.FromDouble(W, prec), prec);
        var moved = new ExactCentre(c.Re, BigFloat.Add(c.Im, pixel, prec));
        Assert.True(ExactCentre.SameLimbs(c.ToDeep(), moved.ToDeep()));   // OD can't see the move
        var d = moved.ToDeep();
        calc.CenterX = d.Re.X0; calc.CenterXLo = d.Re.X1; calc.CenterX2 = d.Re.X2; calc.CenterX3 = d.Re.X3;
        calc.CenterX4 = d.Re.X4; calc.CenterX5 = d.Re.X5; calc.CenterX6 = d.Re.X6; calc.CenterX7 = d.Re.X7;
        calc.CenterY = d.Im.X0; calc.CenterYLo = d.Im.X1; calc.CenterY2 = d.Im.X2; calc.CenterY3 = d.Im.X3;
        calc.CenterY4 = d.Im.X4; calc.CenterY5 = d.Im.X5; calc.CenterY6 = d.Im.X6; calc.CenterY7 = d.Im.X7;
        calc.SetDepth(moved);
        calc.Calculate(CancellationToken.None);
        Assert.Equal(hits + 1, calc.RefRecycleHits);   // the cached (tier-3) orbit served the moved view
        AssertDirect(calc, moved, zl, 0, 0);
    }

    [Fact]
    public void Below_The_AP_Threshold_The_Jitter_Still_Moves_The_Limbs()
    {
        var calc = new MandelbrotCalculator(W, H) { CenterX = -0.75, CenterY = 0.1, Zoom = 1e60, MaxIterations = 500, ColorMap = new HsvPalette() };
        Assert.False(calc.JittersByPixel);
        var orig = CentreLimbs.Capture(calc);
        CentreJitter.Apply(calc, in orig, 0.5, 0.5, 3.5 / W / calc.Zoom);
        Assert.Equal(default, calc.PixelJitter);
        Assert.NotEqual(orig, CentreLimbs.Capture(calc));
        orig.Restore(calc);
        Assert.Equal(orig, CentreLimbs.Capture(calc));
    }
}
