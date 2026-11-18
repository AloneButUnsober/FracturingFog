// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1783 — the exterior distance estimate |z| ln|z| / |dz/dc| was stored as a float in
// complex-plane units, about the pixel size: subnormal past zoom ~1e38, 0 past ~1e45. Every
// consumer reads distance / pixel scale (the DE palettes, ColorGen's dist / pxScale, the
// Distance relief source), so all of them went flat. Both are now in a power-of-two unit,
// 2^-DistanceUnitExp2 complex-plane units, which puts the pixel scale in [1, 2) past 2^-100.
//   • The unit: exactly a power of two (the ratio distance / scale is unchanged), 0 above
//     2^-100, scale in [1, 2) below it, from the double scale where that is normal and from
//     log10 past it.
//   • Against direct iteration: at 1e40 … 1e280 (c near i, a Misiurewicz point), z and
//     dz/dc iterated in BigFloat at sampled pixels give the distance in pixels; the frame's
//     DistanceBuffer / DistancePixelScale matches it.
//   • Colour maps: the DE palettes and ColorGen themes that read pxScale opt in
//     (IColorMapDistanceUnit) and colour from the direct distance; HSV and raw-dist
//     themes keep complex-plane units (their old look; the 1e150 HSV visual-regression
//     case is unchanged).
//   • A shallow frame keeps unit 0 and the plain pixel scale (bit-identical output: the
//     visual regression baseline covers that too).
// Mutants (scripted): caught — the unit always 0; |dz/dc| squared before the scaling;
// DistancePixelScale in complex-plane units; the unit off by one; every map given the
// unit; opted-in maps given the complex-plane pixel scale; DE palettes / ColorGen not
// opting in.

using System;
using System.Threading;
using FracturingFog.FFMath;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1783DeepDistanceUnitTests(ITestOutputHelper output)
{
    // ── The unit ─────────────────────────────────────────────────────────────

    [Fact]
    public void The_Unit_Is_A_Power_Of_Two_That_Puts_The_Deep_Scale_In_One_To_Two()
    {
        var rng = new Random(1783);
        int deep = 0, shallow = 0;
        for (int i = 0; i < 20000; i++)
        {
            double log10 = -1 - rng.NextDouble() * 306;   // pixel 1e-1 … 1e-307
            double ps = Math.Pow(10, log10);
            var (s, e) = MandelbrotCalculator.DistanceUnit(ps, Math.Log10(ps));
            if (ps >= Math.ScaleB(1.0, MandelbrotCalculator.DistanceUnitFromExp2))
            {
                shallow++;
                Assert.Equal(0, e);
                Assert.Equal(ps, s);
            }
            else
            {
                deep++;
                Assert.True(s >= 1 && s < 2, $"scale {s:R} for {ps:R}");
                Assert.Equal(ps, Math.ScaleB(s, -e));   // exact: only the exponent moved
            }
        }
        Assert.True(deep > 1000 && shallow > 1000, $"precondition: both sides ({deep} / {shallow})");

        // Past double (the scale is 0 there): from log10, the same [1, 2) and exponent.
        foreach (double log10 in new[] { -320.3, -1000.7, -123456.2 })
        {
            var (s, e) = MandelbrotCalculator.DistanceUnit(0, log10);
            Assert.True(s >= 1 && s < 2, $"scale {s:R} at 1e{log10}");
            double back = Math.Log10(s) - e * Math.Log10(2);
            Assert.True(Math.Abs(back - log10) < 1e-9, $"{back:R} vs {log10:R}");
        }
    }

    // ── Against direct iteration ─────────────────────────────────────────────

    private const int W = 24, H = 24;

    // c = centre + (ox, oy)·pixel iterated in BigFloat with dz/dc (bailout 512, the
    // calculator's): the distance estimate in pixels, or NaN if it did not escape.
    private static double DirectDistancePixels(ExactCentre centre, double ox, double oy, BigFloat pixel, int prec, int maxIter)
    {
        var cr = BigFloat.Add(centre.Re, BigFloat.Mul(pixel, BigFloat.FromDouble(ox, prec), prec), prec);
        var ci = BigFloat.Add(centre.Im, BigFloat.Mul(pixel, BigFloat.FromDouble(oy, prec), prec), prec);
        BigFloat zr = BigFloat.Zero(prec), zi = BigFloat.Zero(prec);
        BigFloat dr = BigFloat.Zero(prec), di = BigFloat.Zero(prec);
        var one = BigFloat.FromDouble(1, prec);
        for (int n = 0; n < maxIter; n++)
        {
            double a = zr.ToDouble(), b = zi.ToDouble();
            if (a * a + b * b >= 512.0 * 512.0)
            {
                // |dz/dc| · pixel is O(1 / distance in pixels): a double after the product.
                double dpr = BigFloat.Mul(dr, pixel, prec).ToDouble(), dpi = BigFloat.Mul(di, pixel, prec).ToDouble();
                double mag = Math.Sqrt(a * a + b * b);
                return mag * Math.Log(mag) / Math.Sqrt(dpr * dpr + dpi * dpi);
            }
            // dz ← 2·z·dz + 1, then z ← z² + c.
            var ndr = BigFloat.Add(BigFloat.Sub(BigFloat.Mul(zr, dr, prec), BigFloat.Mul(zi, di, prec), prec).ScaleB(1), one, prec);
            var ndi = BigFloat.Add(BigFloat.Mul(zr, di, prec), BigFloat.Mul(zi, dr, prec), prec).ScaleB(1);
            dr = ndr; di = ndi;
            var zr2 = BigFloat.Mul(zr, zr, prec); var zi2 = BigFloat.Mul(zi, zi, prec); var zri = BigFloat.Mul(zr, zi, prec);
            zi = BigFloat.Add(zri.ScaleB(1), ci, prec);
            zr = BigFloat.Add(BigFloat.Sub(zr2, zi2, prec), cr, prec);
        }
        return double.NaN;
    }

    // As S1842's Render: c = i plus a small offset (structure at every depth).
    private static (MandelbrotCalculator Calc, ExactCentre Centre, int Prec) Render(int zl)
    {
        int prec = BigFloat.PrecisionForZoom(zl);
        var centre = new ExactCentre(BigFloat.Parse("3.1415926535e-" + (zl + 1), prec),
            BigFloat.Add(BigFloat.FromDouble(1, prec), BigFloat.Parse("2.7182818284e-" + (zl + 1), prec), prec));
        var d = centre.ToDeep();
        var calc = new MandelbrotCalculator(W, H)
        {
            MaxIterations = 600 + 2 * zl, ColorMap = new HsvPalette(),
            CenterX = d.Re.X0, CenterXLo = d.Re.X1, CenterX2 = d.Re.X2, CenterX3 = d.Re.X3,
            CenterX4 = d.Re.X4, CenterX5 = d.Re.X5, CenterX6 = d.Re.X6, CenterX7 = d.Re.X7,
            CenterY = d.Im.X0, CenterYLo = d.Im.X1, CenterY2 = d.Im.X2, CenterY3 = d.Im.X3,
            CenterY4 = d.Im.X4, CenterY5 = d.Im.X5, CenterY6 = d.Im.X6, CenterY7 = d.Im.X7,
        };
        calc.SetZoomLog10(zl);
        calc.SetDepth(centre);
        calc.Calculate(CancellationToken.None);
        return (calc, centre, prec);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(150)]
    [InlineData(280)]
    public void The_Distance_In_Pixels_Matches_Direct_Iteration(int zl)
    {
        var (calc, centre, prec) = Render(zl);
        var pixel = BigFloat.Div(BigFloat.Parse($"3.5e-{zl}", prec), BigFloat.FromDouble(W, prec), prec);
        Assert.NotEqual(0, calc.DistanceUnitExp2);   // precondition: past 2^-100
        double ps = calc.DistancePixelScale;
        Assert.True(ps >= 1 && ps < 2, $"pixel scale in the distance unit: {ps:R}");

        int total = 0, bad = 0; double worst = 0; string ex = "";
        for (int y = 0; y < H; y += 3)
        for (int x = 0; x < W; x += 3)
        {
            double want = DirectDistancePixels(centre, x - W / 2, y - H / 2, pixel, prec, calc.MaxIterations);
            int i = y * W + x;
            if (double.IsNaN(want) || calc.IterationBuffer[i] >= calc.MaxIterations) continue;
            total++;
            float d = calc.DistanceBuffer[i];
            Assert.True(float.IsNormal(d), $"1e{zl} ({x},{y}): distance {d} is not a normal float");
            double got = d / ps;
            double rel = Math.Abs(got / want - 1);
            worst = Math.Max(worst, rel);
            if (!(rel < 1e-3)) { bad++; if (ex.Length < 200) ex += $"({x},{y}) {got:G6} vs {want:G6}; "; }
        }
        output.WriteLine($"1e{zl} [{calc.LastPrecisionLabel}]: unit 2^-{calc.DistanceUnitExp2}, {total} escaped px, worst {worst:E2}");
        Assert.True(total >= 30, $"precondition: escaped pixels ({total})");
        Assert.True(bad == 0, $"1e{zl}: {bad}/{total} off by > 0.1 % {ex}");
    }

    // End to end through a DE palette (it divides by the pixel scale the calculator pushes):
    // each sampled pixel's colour is the palette's colour for the direct distance in pixels,
    // and the frame is not flat (before #1783 every escaped pixel read distance 0 here).
    [Fact]
    public void A_Distance_Palette_Colours_From_The_Direct_Distance_At_1e150()
    {
        const int zl = 150;
        var (calc, centre, prec) = Render(zl);
        var theme = new DistanceFieldGlowMap();
        calc.ColorMap = theme;
        calc.Calculate(CancellationToken.None);
        var reference = new DistanceFieldGlowMap { PixelScale = 1.0 };   // distances given in pixels
        var pixel = BigFloat.Div(BigFloat.Parse($"3.5e-{zl}", prec), BigFloat.FromDouble(W, prec), prec);
        int total = 0, bad = 0;
        var colours = new System.Collections.Generic.HashSet<uint>();
        for (int y = 0; y < H; y += 3)
        for (int x = 0; x < W; x += 3)
        {
            int i = y * W + x;
            double want = DirectDistancePixels(centre, x - W / 2, y - H / 2, pixel, prec, calc.MaxIterations);
            if (double.IsNaN(want) || calc.IterationBuffer[i] >= calc.MaxIterations) continue;
            total++;
            colours.Add(calc.ColorBuffer[i]);
            uint expect = (uint)reference.Map(calc.SmoothBuffer[i], (float)want, calc.MaxIterations);
            if (calc.ColorBuffer[i] != expect) bad++;
        }
        output.WriteLine($"1e{zl} glow: {total} escaped px, {colours.Count} colours, {bad} differ from the direct-distance colour");
        Assert.True(total >= 30, $"precondition: escaped pixels ({total})");
        Assert.True(colours.Count >= 10, $"the palette is flat: {colours.Count} colours");
        Assert.True(bad <= total / 20, $"{bad}/{total} colours differ from the direct distance's");
    }

    // Maps that read the raw distance (HSV's lightness, many built-ins' in_dist) don't opt
    // in: they keep complex-plane units, ~1e-150 here, so they colour as before (a float 0).
    // A ColorGen theme opts in exactly when it reads pxScale (the dist / pxScale idiom).
    [Fact]
    public void Maps_That_Do_Not_Opt_In_Keep_Complex_Plane_Units_And_ColorGen_Opts_In_Via_PxScale()
    {
        const int zl = 150;
        var ratio = InterpretedColorMap.TryCreate("let d = tanh(dist / pxScale * 0.5);\nreturn rgb(d, d, 1.0 - d);", null, out var e1);
        var raw = InterpretedColorMap.TryCreate("let d = min(dist * 0.08, 1.0);\nreturn rgb(d, d, 1.0 - d);", null, out var e2);
        Assert.True(ratio != null && raw != null, $"{e1} {e2}");
        Assert.True(((FracturingFog.Interefaces.IColorMapDistanceUnit)ratio!).TakesDistanceUnit);
        Assert.False(((FracturingFog.Interefaces.IColorMapDistanceUnit)raw!).TakesDistanceUnit);
        Assert.False(new HsvPalette() is FracturingFog.Interefaces.IColorMapDistanceUnit);

        var (calc, centre, prec) = Render(zl);
        var pixel = BigFloat.Div(BigFloat.Parse($"3.5e-{zl}", prec), BigFloat.FromDouble(W, prec), prec);
        double worldPixel = 3.5 / W * 1e-150;
        var cases = new (string Name, FracturingFog.Interefaces.IColorMap Map)[]
        {
            ("HSV", new HsvPalette()), ("ColorGen dist/pxScale", ratio!), ("ColorGen raw dist", raw!),
        };
        foreach (var (name, map) in cases)
        {
            calc.ColorMap = map;
            calc.Calculate(CancellationToken.None);
            // The reference: a fresh instance of the same map fed the direct distance in the
            // units it asked for (pixels with scale 1, or complex-plane units with the world
            // pixel scale).
            var reference = name switch
            {
                "HSV" => (FracturingFog.Interefaces.IColorMap)new HsvPalette(),
                "ColorGen dist/pxScale" => InterpretedColorMap.TryCreate("let d = tanh(dist / pxScale * 0.5);\nreturn rgb(d, d, 1.0 - d);", null, out _)!,
                _ => InterpretedColorMap.TryCreate("let d = min(dist * 0.08, 1.0);\nreturn rgb(d, d, 1.0 - d);", null, out _)!,
            };
            bool unit = reference is FracturingFog.Interefaces.IColorMapDistanceUnit u && u.TakesDistanceUnit;
            if (reference is FracturingFog.Interefaces.IColorMapWithPixelScale ps) ps.PixelScale = unit ? 1.0 : worldPixel;
            reference.MaxIterations = calc.MaxIterations;
            int total = 0, bad = 0;
            var colours = new System.Collections.Generic.HashSet<uint>();
            for (int y = 0; y < H; y += 3)
            for (int x = 0; x < W; x += 3)
            {
                int i = y * W + x;
                double want = DirectDistancePixels(centre, x - W / 2, y - H / 2, pixel, prec, calc.MaxIterations);
                if (double.IsNaN(want) || calc.IterationBuffer[i] >= calc.MaxIterations) continue;
                total++;
                colours.Add(calc.ColorBuffer[i]);
                float d = unit ? (float)want : (float)(want * worldPixel);
                uint expect = (uint)reference.Map(calc.SmoothBuffer[i], d, calc.MaxIterations,
                    calc.NormalXBuffer[i], calc.NormalYBuffer[i],
                    calc.FinalZrBuffer[i], calc.FinalZiBuffer[i], calc.FinalDrBuffer[i], calc.FinalDiBuffer[i]);
                if (calc.ColorBuffer[i] != expect) bad++;
            }
            output.WriteLine($"1e{zl} {name}: {(unit ? "distance unit" : "complex-plane units")}, {total} px, {colours.Count} colours, {bad} differ");
            Assert.True(total >= 30, $"precondition: escaped pixels ({total})");
            Assert.True(bad <= total / 20, $"{name}: {bad}/{total} colours differ from the direct distance's");
            if (unit) Assert.True(colours.Count >= 10, $"{name}: flat ({colours.Count} colours)");
        }
    }

    [Fact]
    public void A_Shallow_Frame_Keeps_Complex_Plane_Units()
    {
        var c = new MandelbrotCalculator(W, H) { CenterX = -0.75, CenterY = 0.1, Zoom = 1e10, MaxIterations = 2000, ColorMap = new HsvPalette() };
        c.Calculate(CancellationToken.None);
        Assert.Equal(0, c.DistanceUnitExp2);
        Assert.Equal(3.5 / W / 1e10, c.DistancePixelScale);
        Assert.Equal(3.5 / W / 1e10, MandelbrotCalculator.LastPixelScale);
    }
}
