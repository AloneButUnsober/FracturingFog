// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1849 (epic #1428, C5d) — the orbit-aware colouring path (orbit palettes) past OD and past
// double. The oracle: each sampled pixel iterated directly in BigFloat (z ← z² + c, bailout
// 512), with the same palette fed that orbit (z as double, c as double) — its iteration count
// and its colour. Checked:
//   • 1e150 around a centre whose offsets OD can't hold (it builds the AP orbit, C4): every
//     sampled count is the direct count and every colour within 2 per channel of the direct
//     colour. Before #1849 this path built the OD orbit at the limbs (centred ~1e22 pixels off).
//   • 1e400 at c = i (the floatexp orbit loop): the same, and the label says FX.
//   • 1e60 (below every threshold) is unchanged: the OD orbit and the double loop.

using System;
using System.Threading;
using FracturingFog.FFMath;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1849OrbitPathDeepTests
{
    private const int W = 24, H = 24;

    // Direct iteration with the palette sampling as ComputePixelOrbitPerturbation does
    // (iter > 0, z before the step), then the colour as FinalizeOrbitPixel computes it.
    private static (int Iter, uint Color) Direct(IOrbitAwareColorMap map, ExactCentre centre, double ox, double oy,
                                                 BigFloat pixel, int prec, int maxIter)
    {
        var cr = BigFloat.Add(centre.Re, BigFloat.Mul(pixel, BigFloat.FromDouble(ox, prec), prec), prec);
        var ci = BigFloat.Add(centre.Im, BigFloat.Mul(pixel, BigFloat.FromDouble(oy, prec), prec), prec);
        double cxD = cr.ToDouble(), cyD = ci.ToDouble();
        map.InitOrbit(out var acc);
        BigFloat zr = BigFloat.Zero(prec), zi = BigFloat.Zero(prec);
        double a = 0, b = 0;
        int n;
        for (n = 0; n < maxIter; n++)
        {
            a = zr.ToDouble(); b = zi.ToDouble();
            if (a * a + b * b >= 512.0 * 512.0) break;
            if (n > 0) map.Sample(ref acc, a, b, cxD, cyD, n);
            var zr2 = BigFloat.Mul(zr, zr, prec); var zi2 = BigFloat.Mul(zi, zi, prec); var zri = BigFloat.Mul(zr, zi, prec);
            zi = BigFloat.Add(zri.ScaleB(1), ci, prec);
            zr = BigFloat.Add(BigFloat.Sub(zr2, zi2, prec), cr, prec);
        }
        if (n >= maxIter) return (maxIter, 0);
        double mag = Math.Sqrt(a * a + b * b);
        float smooth = (float)(n + 1.0 - Math.Log2(Math.Log2(mag)));
        return (n, (uint)map.MapWithOrbit(smooth, 0f, maxIter, 0f, 0f, in acc));
    }

    private static void AssertMatchesDirect(int zl, ExactCentre centre, string expectLabelEnd)
    {
        int prec = BigFloat.PrecisionForZoom(zl);
        var d = centre.ToDeep();
        var map = new CurvatureAverageMap();
        var calc = new MandelbrotCalculator(W, H)
        {
            MaxIterations = 600 + 2 * zl, ColorMap = map,
            CenterX = d.Re.X0, CenterXLo = d.Re.X1, CenterX2 = d.Re.X2, CenterX3 = d.Re.X3,
            CenterX4 = d.Re.X4, CenterX5 = d.Re.X5, CenterX6 = d.Re.X6, CenterX7 = d.Re.X7,
            CenterY = d.Im.X0, CenterYLo = d.Im.X1, CenterY2 = d.Im.X2, CenterY3 = d.Im.X3,
            CenterY4 = d.Im.X4, CenterY5 = d.Im.X5, CenterY6 = d.Im.X6, CenterY7 = d.Im.X7,
        };
        calc.SetZoomLog10(zl);
        calc.ApReferenceZoomLog10 = 100;
        calc.SetDepth(centre);
        calc.Calculate(CancellationToken.None);
        Assert.EndsWith(expectLabelEnd, calc.LastPrecisionLabel);
        var pixel = BigFloat.Div(BigFloat.Parse($"3.5e-{zl}", prec), BigFloat.FromDouble(W, prec), prec);
        int bad = 0, badColour = 0, total = 0; string ex = "";
        for (int y = 0; y < H; y += 3)
        for (int x = 0; x < W; x += 3)
        {
            var (want, col) = Direct(new CurvatureAverageMap(), centre, x - W / 2, y - H / 2, pixel, prec, calc.MaxIterations);
            int i = y * W + x;
            total++;
            if (calc.IterationBuffer[i] != want) { bad++; if (ex.Length < 200) ex += $"({x},{y}) {calc.IterationBuffer[i]} vs {want}; "; continue; }
            if (want >= calc.MaxIterations) continue;
            uint got = calc.ColorBuffer[i];
            for (int sh = 0; sh < 24; sh += 8)
                if (Math.Abs((int)((got >> sh) & 0xFF) - (int)((col >> sh) & 0xFF)) > 2) { badColour++; break; }
        }
        Assert.True(bad == 0, $"1e{zl}: {bad}/{total} counts wrong [{calc.LastPrecisionLabel}] {ex}");
        Assert.True(badColour == 0, $"1e{zl}: {badColour}/{total} colours off");
    }

    [Fact]
    public void Past_OD_The_Orbit_Path_Uses_The_AP_Orbit_At_The_Exact_Centre()
    {
        int prec = BigFloat.PrecisionForZoom(150);
        var re = BigFloat.Parse("3.1415926535e-110", prec);
        var im = BigFloat.Add(BigFloat.FromDouble(1, prec), BigFloat.Parse("2.7182818284e-105", prec), prec);
        AssertMatchesDirect(150, new ExactCentre(re, im), "AP-PT");
    }

    [Fact]
    public void Past_Double_The_Orbit_Path_Runs_The_Floatexp_Loop()
    {
        int prec = BigFloat.PrecisionForZoom(400);
        AssertMatchesDirect(400, new ExactCentre(BigFloat.Zero(prec), BigFloat.FromDouble(1, prec)), "AP-PT FX");
    }

    [Fact]
    public void Below_The_Thresholds_The_Orbit_Path_Is_Unchanged()
    {
        int prec = BigFloat.PrecisionForZoom(60);
        AssertMatchesDirect(60, new ExactCentre(BigFloat.Zero(prec), BigFloat.FromDouble(1, prec)), "OD-PT");
    }
}
