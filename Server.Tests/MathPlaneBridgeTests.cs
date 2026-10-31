// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1554 (Math Plane S7) — the fractal bridge: orbit, cobweb, fractal backdrop and
// the params' region + --param round trip. Expected values come from loops written
// here with System.Numerics.Complex, not from the bridge's own helpers.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using FracturingFog;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MathPlaneBridgeTests
{
    private const int W = 320, H = 180;

    // The centre of the period-3 bulb above the main cardioid: a root of
    // c^3 + 2c^2 + c + 1 = 0, so z3 = c (c^3 + 2c^2 + c + 1) = 0 exactly.
    private static readonly Complex Rabbit = new(-0.12256116687665361, 0.74486176661974423);

    // ── Orbit ──

    [Fact]
    public void Orbit_EqualsAnIndependentLoop()
    {
        var rng = new Random(1554);
        for (int k = 0; k < 40; k++)
        {
            var c = new Complex(rng.NextDouble() * 3 - 2, rng.NextDouble() * 3 - 1.5);
            var orbit = MathPlaneDynamics.Orbit(MathPlaneDynamics.OrbitMap(null, c), 60);
            var z = Complex.Zero;
            Assert.Equal(z, orbit[0]);
            for (int n = 1; n < orbit.Count; n++)
            {
                z = z * z + c;
                Assert.Equal(z, orbit[n]);
            }
            // It stops only at the step count or once past the escape limit.
            Assert.True(orbit.Count == 61 || Complex.Abs(orbit[^1]) > MathPlaneDynamics.EscapeLimit);
        }
    }

    [Fact]
    public void PeriodThreeC_ClosesATriangle()
    {
        var orbit = MathPlaneDynamics.Orbit(MathPlaneDynamics.OrbitMap(null, Rabbit), 30);
        Assert.True(Complex.Abs(orbit[3]) < 1e-12, $"z3 = {orbit[3]}");
        for (int n = 0; n + 3 < orbit.Count; n++)
            Assert.True(Complex.Abs(orbit[n + 3] - orbit[n]) < 1e-9);
        Assert.Equal(3, MathPlaneDynamics.Period(orbit));
        Assert.Null(MathPlaneDynamics.EscapeStep(orbit));
        Assert.Contains("settles to period 3",
            MathPlaneCalculator.OrbitReadout(new MathPlaneScene { OrbitSteps = 30 }, Rabbit, orbit));
    }

    [Fact]
    public void OutsideC_EscapesAtTheRightStep()
    {
        var c = new Complex(0.5, 0);
        var orbit = MathPlaneDynamics.Orbit(MathPlaneDynamics.OrbitMap(null, c), 50);
        int step = 0; var z = Complex.Zero;
        while (Complex.Abs(z) <= 2) { z = z * z + c; step++; }
        Assert.Equal(step, MathPlaneDynamics.EscapeStep(orbit));
    }

    [Fact]
    public void Orbit_UsesTheScenesMap()
    {
        var c = new Complex(0.3, -0.2);
        var orbit = MathPlaneDynamics.Orbit(MathPlaneDynamics.OrbitMap("z^3 + c", c), 10);
        var z = Complex.Zero;
        for (int n = 1; n < orbit.Count; n++)
        {
            z = z * z * z + c;
            Assert.True(Complex.Abs(orbit[n] - z) <= 1e-12 * Math.Max(1, Complex.Abs(z)));
        }
    }

    // ── Cobweb ──

    [Fact]
    public void Cobweb_AlternatesBetweenTheCurveAndTheDiagonal()
    {
        const double c = -1.3;
        var f = MathPlaneDynamics.CobwebMap(MathPlaneCobweb.DefaultSource, c)!;
        var v = MathPlaneDynamics.Cobweb(f, 0.1, 25);
        Assert.Equal(51, v.Count);
        double x = 0.1;
        Assert.Equal((x, x), v[0]);
        for (int n = 0; n < 25; n++)
        {
            double y = x * x + c;                  // independent
            var onCurve = v[2 * n + 1];
            var onDiag = v[2 * n + 2];
            Assert.Equal(x, onCurve.X);            // vertical move
            Assert.InRange(onCurve.Y - y, -1e-14, 1e-14);
            Assert.Equal(onCurve.Y, onDiag.Y);     // horizontal move
            Assert.Equal(onDiag.X, onDiag.Y);      // back on y = x
            x = onDiag.X;
        }
    }

    [Fact]
    public void Cobweb_NonRealValuesStopIt()
    {
        var f = MathPlaneDynamics.CobwebMap("sqrt(x) - 2", 0)!;
        var v = MathPlaneDynamics.Cobweb(f, 1, 10);   // 1 -> -1 -> sqrt(-1) is not real
        Assert.Equal(3, v.Count);
    }

    // ── Scene text ──

    [Fact]
    public void OrbitAndCobweb_RoundTrip()
    {
        foreach (var text in new[] { "orbit 40; cobweb 0.25 12", "cobweb -0.5 7 \"4*c*x*(1 - x)\"", "orbit 1" })
        {
            Assert.True(MathPlaneScene.TryParse(text, out var s, out var e), e?.ToString());
            Assert.True(MathPlaneScene.TryParse(s.Serialize(), out var back, out var e2), e2?.ToString());
            Assert.Equal(s, back);
        }
        Assert.True(MathPlaneScene.TryParse("cobweb 0.3", out var d, out _));
        Assert.Equal(new MathPlaneCobweb(0.3, MathPlaneCobweb.DefaultSteps, MathPlaneCobweb.DefaultSource), d.Cobweb);
    }

    [Theory]
    [InlineData("orbit", "step count")]
    [InlineData("orbit 0", "step count")]
    [InlineData("orbit 20000", "step count")]
    [InlineData("orbit 5; orbit 6", "twice")]
    [InlineData("cobweb", "x0")]
    [InlineData("cobweb 0.1 0", "steps")]
    [InlineData("cobweb 0.1 5 \"y + x\"", "reads only x, c")]
    [InlineData("cobweb 0.1 5 \"x\" extra", "unexpected")]
    public void BadStatements_AreErrors(string text, string expect)
    {
        Assert.False(MathPlaneScene.TryParse(text, out _, out var e));
        Assert.Contains(expect, e!.Message);
    }

    // ── Backdrop ──

    private static MathPlaneCalculator Render(string scene, Action<FractalParameters> set)
    {
        var p = new FractalParameters { MathPlaneScene = scene };
        set(p);
        var calc = new MathPlaneCalculator(W, H) { Zoom = 0.5, MaxIterations = 300, FractalParameters = p };
        calc.Calculate(CancellationToken.None);
        return calc;
    }

    /// <summary>Bounded for 2000 steps: 1; escapes within 30: 0; else unknown (-1).</summary>
    private static int Classify(Complex z, Complex c)
    {
        for (int n = 0; n < 2000; n++)
        {
            if (Complex.Abs(z) > 2) return n < 30 ? 0 : -1;
            z = z * z + c;
        }
        return 1;
    }

    [Fact]
    public void JuliaBackdrop_IsTheFilledJuliaSetOfC_UpTheRightWay()
    {
        // The rabbit is not symmetric under conjugation, so a flipped or shifted
        // backdrop shows. Each pixel is checked at its own centre.
        var calc = Render("axes off; grid off; ticks off", p =>
        {
            p.MathPlaneBackdrop = MathPlaneBackdrop.Julia;
            p.MathPlaneCRe = Rabbit.Real; p.MathPlaneCIm = Rabbit.Imaginary;
        });
        var view = calc.ViewBox(W, H);
        uint Px(int px, int py) => calc.ColorBuffer[py * W + px];
        // The interior colour: the pixel holding c itself (c is in its own filled Julia set).
        var (cx, cy) = MathPlaneCalculator.ToPixel(Rabbit.Real, Rabbit.Imaginary, W, H, 0, 0, 0.5);
        uint inside = Px((int)cx, (int)cy);
        int inN = 0, outN = 0;
        for (int py = 1; py < H; py += 3)
            for (int px = 1; px < W; px += 3)
            {
                var z = new Complex(view.XMin + (px + 0.5) * view.Pitch, view.YMax - (py + 0.5) * view.Pitch);
                switch (Classify(z, Rabbit))
                {
                    case 1: Assert.Equal(inside, Px(px, py)); inN++; break;
                    case 0: Assert.NotEqual(inside, Px(px, py)); outN++; break;
                }
            }
        Assert.True(inN > 100 && outN > 100, $"in {inN}, out {outN}");
    }

    [Fact]
    public void MandelbrotBackdrop_HasThePeriodThreeBulbAboveTheAxis()
    {
        var calc = Render("axes off; grid off; ticks off", p => p.MathPlaneBackdrop = MathPlaneBackdrop.Mandelbrot);
        var view = calc.ViewBox(W, H);
        var (ix, iy) = MathPlaneCalculator.ToPixel(-0.2, 0, W, H, 0, 0, 0.5);     // in the cardioid
        uint inside = calc.ColorBuffer[(int)iy * W + (int)ix];
        int checkedN = 0;
        for (int py = 1; py < H; py += 2)
            for (int px = 1; px < W; px += 2)
            {
                var c = new Complex(view.XMin + (px + 0.5) * view.Pitch, view.YMax - (py + 0.5) * view.Pitch);
                int k = Classify(Complex.Zero, c);
                if (k == 1) { Assert.Equal(inside, calc.ColorBuffer[py * W + px]); checkedN++; }
                if (k == 0) Assert.NotEqual(inside, calc.ColorBuffer[py * W + px]);
            }
        Assert.True(checkedN > 100);
    }

    [Fact]
    public void Backdrop_ViaParamEqualsDirect_AndIsDeterministic()
    {
        const string scene = "orbit 40; cobweb 0.1 10; vector v 1 1";
        var direct = Render(scene, p =>
        {
            p.MathPlaneBackdrop = MathPlaneBackdrop.Multibrot; p.MultibrotExponent = 3;
            p.MathPlaneCRe = -0.4; p.MathPlaneCIm = 0.6;
        });
        var kv = new Dictionary<string, string>
        {
            ["MathPlaneScene"] = scene, ["MathPlaneBackdrop"] = ((int)MathPlaneBackdrop.Multibrot).ToString(), ["MultibrotExponent"] = "3",
            ["MathPlaneCRe"] = "-0.4", ["MathPlaneCIm"] = "0.6",
        };
        var viaParam = Render("", p =>
        {
            var rp = RegionFractalParams.FromKeyValues(kv, FractalType.MathPlane, out var err);
            Assert.Null(err);
            rp!.ApplyTo(p);
        });
        Assert.Equal(direct.ColorBuffer, viaParam.ColorBuffer);
        var again = Render(scene, p =>
        {
            p.MathPlaneBackdrop = MathPlaneBackdrop.Multibrot; p.MultibrotExponent = 3;
            p.MathPlaneCRe = -0.4; p.MathPlaneCIm = 0.6;
        });
        Assert.Equal(direct.ColorBuffer, again.ColorBuffer);
    }

    [Fact]
    public void Snapshot_CarriesTheBackdropAndItsFamilyParams()
    {
        var p = new FractalParameters { MathPlaneBackdrop = MathPlaneBackdrop.Multibrot, MultibrotExponent = 5 };
        var rp = RegionFractalParams.Snapshot(FractalType.MathPlane, p)!;
        var q = new FractalParameters();
        rp.ApplyTo(q);
        Assert.Equal(MathPlaneBackdrop.Multibrot, q.MathPlaneBackdrop);
        Assert.Equal(5, q.MultibrotExponent);

        // Julia uses the plane's c, so JuliaC stays out of the block.
        var j = new FractalParameters { MathPlaneBackdrop = MathPlaneBackdrop.Julia, JuliaC = new Complex(0.3, 0.1) };
        var jr = RegionFractalParams.Snapshot(FractalType.MathPlane, j)!;
        Assert.Null(jr.JuliaCRe);
        Assert.Null(RegionFractalParams.Snapshot(FractalType.MathPlane, new FractalParameters())!.MultibrotExponent);
    }

    [Fact]
    public void BackdropView_MatchesTheEscapeTimeGrid()
    {
        // Independently: the escape-time grid samples real = cx + (x - w/2) s, with
        // s = 3.5 / max(w, h) / zoom; the plane's pixel centre is
        // CenterX + (x + 0.5 - w/2) * 4 / w / Zoom. They must agree in every column.
        const int w = 300, h = 200; const double zoom = 0.8, centerX = -0.3, centerY = 0.2;
        var (bx, by, bz) = MathPlaneCalculator.BackdropView(w, h, centerX, centerY, zoom);
        double s = 3.5 / Math.Max(w, h) / bz, pitch = 4.0 / w / zoom;
        Assert.Equal(pitch, s, 15);
        for (int x = 0; x < w; x += 37)
            Assert.Equal(centerX + (x + 0.5 - w / 2.0) * pitch, bx + (x - w / 2.0) * s, 12);
        // Row y of the backdrop lands on plane row h - 1 - y, whose centre is world
        // y = (h/2 - (h - 1 - y) - 0.5) pitch - centerY.
        for (int y = 0; y < h; y += 23)
            Assert.Equal((h / 2.0 - (h - 1 - y) - 0.5) * pitch - centerY, by + (y - h / 2.0) * s, 12);
    }
}
