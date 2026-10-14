// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1294 — Nova rendered byte-identical to Newton: it shared NewtonCalculator
// with no +c term. Nova now iterates z := z − R·f(z)/f′(z) + c (f = z^d − 1),
// in the parameter plane (c = pixel, z0 = NovaZ0) or the Julia plane
// (z0 = pixel, c = NovaJuliaC). These tests check the maths through invariants
// that do not depend on the renderer's own colouring:
//   • a converged parameter-plane pixel ends on a fixed point of its own map,
//     i.e. R·f(z)/f′(z) = c with c the pixel coordinate;
//   • the Julia plane at c = 0 is Newton: same root for every pixel;
//   • Newton ignores every Nova knob (byte-identical to before);
//   • HE at strength 0 is an identity recolour for Nova's palette path;
//   • the Nova knobs round-trip through the region snapshot and --param.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1294NovaTests
{
    private const int W = 96, H = 96;

    private static NewtonCalculator Render(FractalType type, FractalParameters fp,
        double cx = -0.35, double cy = 0.0, double zoom = 1.6, IColorMap? map = null)
    {
        var c = new NewtonCalculator(W, H)
        {
            FractalType = type,
            FractalParameters = fp,
            CenterX = cx, CenterY = cy, Zoom = zoom, MaxIterations = 200,
            ColorMap = map ?? new HsvPalette(),
        };
        c.Calculate(default);
        return c;
    }

    [Fact]
    public void Nova_Differs_From_Newton_At_The_Same_View()
    {
        var fp = new FractalParameters();
        var newton = Render(FractalType.Newton, fp).ColorBuffer;
        var nova = Render(FractalType.Nova, fp).ColorBuffer;
        Assert.NotEqual(newton, nova);
    }

    [Fact]
    public void ParameterPlane_Converged_Pixels_Sit_On_A_Fixed_Point_Of_Their_Own_Map()
    {
        // Fixed point of z − R·f/f′ + c  ⇔  R·f(z)/f′(z) = c, with c = the pixel.
        var fp = new FractalParameters { NewtonExponent = 3, NewtonRelaxation = 1.0 };
        const double cx0 = -0.35, cy0 = 0.0, zoom = 1.6;
        var calc = Render(FractalType.Nova, fp, cx0, cy0, zoom);
        double scale = (3.5 / Math.Max(W, H)) / zoom;
        int d = fp.NewtonExponent, checkedPx = 0;
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            int i = y * W + x;
            if (calc.BasinBuffer[i] < 0) continue;
            var c = new Complex(cx0 + (x - W * 0.5) * scale, cy0 + (y - H * 0.5) * scale);
            var z = new Complex(calc.FinalZrBuffer[i], calc.FinalZiBuffer[i]);
            var step = fp.NewtonRelaxation * (Complex.Pow(z, d) - 1) / (d * Complex.Pow(z, d - 1));
            // Final z is stored as float (~1e-7 relative), so allow a loose bound.
            Assert.True(Complex.Abs(step - c) < 1e-4,
                $"pixel ({x},{y}): R·f/f′ = {step}, c = {c}");
            checkedPx++;
        }
        Assert.True(checkedPx > W * H / 2, $"only {checkedPx} converged pixels");
    }

    [Fact]
    public void Interior_Exists_In_The_Parameter_Plane()
    {
        // The embedded Mandelbrot copies are non-convergent (attracting cycles).
        var calc = Render(FractalType.Nova, new FractalParameters(), -0.43, 0.0, 12.0);
        Assert.Contains(calc.BasinBuffer, b => b < 0);
        Assert.Contains(calc.BasinBuffer, b => b >= 0);
    }

    [Fact]
    public void JuliaPlane_At_C_Zero_Finds_The_Same_Roots_As_Newton()
    {
        var fp = new FractalParameters { NovaJuliaMode = true, NovaJuliaC = Complex.Zero };
        var newton = Render(FractalType.Newton, fp, 0, 0, 0.5);
        var nova = Render(FractalType.Nova, fp, 0, 0, 0.5);
        int both = 0;
        for (int i = 0; i < W * H; i++)
        {
            if (newton.BasinBuffer[i] < 0 || nova.BasinBuffer[i] < 0) continue;
            Assert.Equal(newton.BasinBuffer[i], nova.BasinBuffer[i]);
            both++;
        }
        Assert.True(both > W * H * 9 / 10, $"only {both} pixels converged in both");
    }

    [Fact]
    public void JuliaPlane_Differs_From_ParameterPlane()
    {
        var p = Render(FractalType.Nova, new FractalParameters()).ColorBuffer;
        var j = Render(FractalType.Nova, new FractalParameters { NovaJuliaMode = true }).ColorBuffer;
        Assert.NotEqual(p, j);
    }

    [Fact]
    public void Newton_Ignores_Every_Nova_Knob()
    {
        var plain = Render(FractalType.Newton, new FractalParameters(), 0, 0, 0.5).ColorBuffer;
        var knobs = Render(FractalType.Newton, new FractalParameters
        {
            NovaJuliaMode = true, NovaZ0 = new Complex(0.3, 0.2), NovaJuliaC = new Complex(0.1, -0.4),
        }, 0, 0, 0.5).ColorBuffer;
        Assert.Equal(plain, knobs);
    }

    [Fact]
    public void Z0_Changes_The_ParameterPlane_Image()
    {
        var a = Render(FractalType.Nova, new FractalParameters()).ColorBuffer;
        var b = Render(FractalType.Nova, new FractalParameters { NovaZ0 = new Complex(0.8, 0.3) }).ColorBuffer;
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Nova_Palette_Path_Uses_More_Than_The_Basin_Hues()
    {
        // The basin-hue fallback gives d hues; the palette path shades by the
        // smooth convergence count, so a plain palette shows many colours.
        var buf = Render(FractalType.Nova, new FractalParameters()).ColorBuffer;
        Assert.True(buf.Distinct().Count() > 64);
    }

    [Fact]
    public void HE_Strength0_Is_Identity_For_Nova()
    {
        var calc = Render(FractalType.Nova, new FractalParameters());
        var before = (uint[])calc.ColorBuffer.Clone();
        calc.ApplyHistogramEqualization(0.0);
        Assert.Equal(before, calc.ColorBuffer);
    }

    [Fact]
    public void HE_Full_Strength_Changes_Nova()
    {
        var calc = Render(FractalType.Nova, new FractalParameters());
        var before = (uint[])calc.ColorBuffer.Clone();
        calc.ApplyHistogramEqualization(1.0);
        Assert.NotEqual(before, calc.ColorBuffer);
    }

    [Fact]
    public void Region_Snapshot_Round_Trips_The_Nova_Knobs()
    {
        var live = new FractalParameters
        {
            NovaJuliaMode = true, NovaZ0 = new Complex(0.7, -0.2), NovaJuliaC = new Complex(-0.31, 0.44),
        };
        var snap = RegionFractalParams.Snapshot(FractalType.Nova, live)!;
        var recalled = new FractalParameters();
        snap.ApplyTo(recalled);
        Assert.True(recalled.NovaJuliaMode);
        Assert.Equal(live.NovaZ0, recalled.NovaZ0);
        Assert.Equal(live.NovaJuliaC, recalled.NovaJuliaC);
    }

    [Fact]
    public void Param_Flags_Set_The_Nova_Knobs()
    {
        var rp = RegionFractalParams.FromKeyValues(new[]
        {
            new KeyValuePair<string, string>("NovaJuliaMode", "true"),
            new KeyValuePair<string, string>("NovaJuliaCRe", "-0.2"),
            new KeyValuePair<string, string>("NovaZ0Im", "0.5"),
        }, null, out string? error);
        Assert.Null(error);
        var p = new FractalParameters();
        rp!.ApplyTo(p);
        Assert.True(p.NovaJuliaMode);
        Assert.Equal(new Complex(-0.2, 0.0), p.NovaJuliaC);   // missing half keeps its default
        Assert.Equal(new Complex(1.0, 0.5), p.NovaZ0);
    }
}
