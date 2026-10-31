// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1553 (Math Plane S6) — complex maps: the scene's map / phase / probe
// statements, the frame (1 - t) z + t f(z), mapped grid lines, f'(z), the phase
// portrait, and the params' region + --param round trip. Expected values come
// from System.Numerics.Complex evaluated here, not from the map's own code.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using FracturingFog;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MathPlaneComplexTests
{
    private const int W = 320, H = 180;
    private static readonly Complex C0 = new(-0.5, 0.5);

    private static MathPlaneComplexMap Map(string src, double t = 1.0)
        => MathPlaneComplexMap.Create(src, C0, t) ?? throw new InvalidOperationException(src);

    private static IEnumerable<Complex> Samples()
    {
        for (double x = -2.25; x <= 2.3; x += 0.75)
            for (double y = -1.8; y <= 1.9; y += 0.6)
                yield return new Complex(x, y);
    }

    private static void Near(Complex want, Complex got, double rel = 1e-12)
    {
        double tol = rel * Math.Max(1.0, Complex.Abs(want));
        Assert.True(Complex.Abs(got - want) <= tol, $"want {want}, got {got}");
    }

    // ── Scene text ──

    [Fact]
    public void MapPhaseProbe_RoundTrip()
    {
        const string text = "map \"z^2 + c\"; phase contours; probe 1.25 -0.5; vector i 1 0";
        Assert.True(MathPlaneScene.TryParse(text, out var s, out var e), e?.ToString());
        Assert.Equal("z^2 + c", s.MapSource);
        Assert.Equal(MathPlanePhase.Contours, s.Phase);
        Assert.Equal((1.25, -0.5), s.Probe);
        Assert.True(MathPlaneScene.TryParse(s.Serialize(), out var back, out _));
        Assert.Equal(s, back);
        Assert.NotEqual(s, s with { Phase = MathPlanePhase.On });
        Assert.NotEqual(s, s with { MapSource = "z^3" });
        Assert.NotEqual(s, s with { Probe = null });
    }

    [Theory]
    [InlineData("map \"x + z\"", "reads only z, c")]
    [InlineData("map \"z\"; map \"z^2\"", "twice")]
    [InlineData("map z^2", "quoted")]
    [InlineData("map \"z^\"", "expression")]
    [InlineData("phase maybe", "off, on or contours")]
    [InlineData("probe 1", "x y")]
    [InlineData("probe 1 2; probe 0 0", "twice")]
    public void BadStatements_AreErrors(string text, string expect)
    {
        Assert.False(MathPlaneScene.TryParse(text, out _, out var e));
        Assert.Contains(expect, e!.Message);
    }

    // ── The frame ──

    public static IEnumerable<object[]> Presets() => new[]
    {
        new object[] { "z^2" }, new object[] { "z^2 + c" }, new object[] { "1/z" }, new object[] { "exp(z)" }, new object[] { "i*z" },
    };

    private static Complex Independent(string src, Complex z) => src switch
    {
        "z^2" => z * z,
        "z^2 + c" => z * z + C0,
        "1/z" => Complex.One / z,
        "exp(z)" => Complex.Exp(z),
        "i*z" => Complex.ImaginaryOne * z,
        _ => throw new ArgumentException(src),
    };

    [Theory]
    [MemberData(nameof(Presets))]
    public void AtTOne_EachSampleIsF(string src)
    {
        var m = Map(src);
        foreach (var z in Samples())
        {
            var (x, y) = m.Apply(z.Real, z.Imaginary);
            Near(Independent(src, z), new Complex(x, y), 1e-12);
        }
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void PartWay_IsTheStraightLineMorph(string src)
    {
        var m = Map(src, 0.3);
        foreach (var z in Samples())
            Near(0.7 * z + 0.3 * Independent(src, z), m.At(z), 1e-12);
        var id = Map(src, 0);
        foreach (var z in Samples()) Assert.Equal(z, id.At(z));
    }

    [Fact]
    public void Iz_TurnsIHatToJHat()
    {
        var m = Map("i*z");
        var (x, y) = m.Apply(1, 0);
        Assert.Equal(0.0, x, 14);
        Assert.Equal(1.0, y, 14);

        // Drawn: the vector's tip lands on the y-axis, not the x-axis.
        const uint orange = 0xFFE69F00;
        var calc = Render("axes off; grid off; ticks off; map \"i*z\"; vector i 1 0 #E69F00 nolabel", 1);
        Assert.Equal(orange, Pixel(calc, 0, 0.7));
        Assert.NotEqual(orange, Pixel(calc, 0.7, 0));
    }

    [Fact]
    public void MappedGridLine_LiesOnTheImage()
    {
        // z^2 sends the line x = a to u = a^2 - y^2, v = 2 a y: every sampled point
        // (u, v) must satisfy u = a^2 - (v / 2a)^2.
        var m = Map("z^2");
        var view = new MathPlaneViewBox(-4, 4, -2.25, 2.25, 0.025);
        const double a = 1.5;
        var lines = m.MapSegment((a, -3), (a, 3), view);
        Assert.NotEmpty(lines);
        int n = 0;
        foreach (var poly in lines)
            foreach (var (u, v) in poly)
            {
                double y = v / (2 * a);
                Assert.InRange(u - (a * a - y * y), -1e-9, 1e-9);
                n++;
            }
        Assert.True(n > 50);
    }

    [Fact]
    public void MappedGridLine_BreaksAtThePole()
    {
        // 1/z of the line y = 0 through 0: the image runs off to infinity at z = 0,
        // so the polyline breaks there instead of drawing a connector across.
        var m = Map("1/z");
        var view = new MathPlaneViewBox(-4, 4, -2.25, 2.25, 0.025);
        var lines = m.MapSegment((-3, 0), (3, 0), view);
        Assert.True(lines.Count >= 2);
        foreach (var poly in lines)
            for (int i = 1; i < poly.Count; i++)
            {
                // Off-view chords on one side are fine (nothing there to draw); one from
                // the far left to the far right is the connector a pole must not get.
                var (a, b) = (poly[i - 1].X, poly[i].X);
                Assert.False(a * b < 0 && Math.Abs(b - a) > view.Width, $"connector across the pole: {a} to {b}");
            }
    }

    // ── f'(z) ──

    [Fact]
    public void Derivative_MatchesTheAnalyticOne()
    {
        foreach (var z in Samples())
        {
            Near(2 * z, Map("z^2").Derivative(z), 1e-8);
            Near(Complex.Exp(z), Map("exp(z)").Derivative(z), 1e-8);
            Near(Complex.ImaginaryOne, Map("i*z").Derivative(z), 1e-8);
        }
        // i z: a quarter turn, no scaling.
        var (l1, l2) = MathPlaneCalculator.ProbeText((1, 0), Mat2.Identity, Map("i*z"));
        Assert.Contains("= 0 + 1i", l1);
        Assert.Contains("|f'| = 1 (scale), arg f' = 90 deg", l2);
    }

    [Fact]
    public void Preimage_SolvesTheFrame()
    {
        var m = Map("z^2");
        var z = m.Preimage(new Complex(-3, 4), new Complex(1, 1));
        Assert.NotNull(z);
        Near(new Complex(1, 2), z!.Value, 1e-10);
        // The other branch from the other side.
        Near(new Complex(-1, -2), m.Preimage(new Complex(-3, 4), new Complex(-1, -1))!.Value, 1e-10);
    }

    // ── Phase portrait ──

    [Fact]
    public void PhaseWheel_StopsAndPeriod()
    {
        Assert.Equal(MathPlaneComplexMap.PhaseStops[0], MathPlaneComplexMap.PhaseColor(0));
        Assert.Equal(MathPlaneComplexMap.PhaseStops[3], MathPlaneComplexMap.PhaseColor(Math.PI));
        Assert.Equal(MathPlaneComplexMap.PhaseStops[3], MathPlaneComplexMap.PhaseColor(-Math.PI));
        for (double a = -7; a < 7; a += 0.37)
            Assert.Equal(MathPlaneComplexMap.PhaseColor(a), MathPlaneComplexMap.PhaseColor(a + 2 * Math.PI));
    }

    [Fact]
    public void PhasePixel_IsTheArgOfAnIndependentF()
    {
        var calc = Render("axes off; grid off; ticks off; phase on; map \"z^2 + c\"", mode: null);
        var view = calc.ViewBox(W, H);
        int checkedPx = 0;
        for (int py = 3; py < H; py += 17)
            for (int px = 5; px < W; px += 23)
            {
                var z = new Complex(view.XMin + (px + 0.5) * view.Pitch, view.YMax - (py + 0.5) * view.Pitch);
                uint want = MathPlaneComplexMap.PhasePixel(z * z + C0, contours: false, MathPlaneCalculator.BackgroundColor);
                uint got = calc.ColorBuffer[py * W + px];
                // One unit per channel: an ulp in arg can round a blend the other way.
                for (int sh = 0; sh <= 16; sh += 8)
                    Assert.InRange((int)((got >> sh) & 0xFF) - (int)((want >> sh) & 0xFF), -1, 1);
                checkedPx++;
            }
        Assert.True(checkedPx > 100);
    }

    [Fact]
    public void Contours_StepAtPowersOfTwo()
    {
        uint below = MathPlaneComplexMap.PhasePixel(new Complex(1.999, 0), contours: true, 0);
        uint above = MathPlaneComplexMap.PhasePixel(new Complex(2.001, 0), contours: true, 0);
        uint plain = MathPlaneComplexMap.PhasePixel(new Complex(2.001, 0), contours: false, 0);
        Assert.True(((below >> 16) & 0xFF) > ((above >> 16) & 0xFF) + 40, "no step at |f| = 2");
        Assert.True(((plain >> 16) & 0xFF) > ((above >> 16) & 0xFF), "contours should darken just past the step");
        Assert.Equal(0xFF000000u, MathPlaneComplexMap.PhasePixel(Complex.Zero, true, 0xFF000000u));
    }

    [Fact]
    public void Phase_IsDeterministic()
    {
        var a = Render("phase contours; map \"exp(z) + c\"; probe 1 0.5; vector v 1 1", 1);
        var b = Render("phase contours; map \"exp(z) + c\"; probe 1 0.5; vector v 1 1", 1);
        Assert.Equal(a.ColorBuffer, b.ColorBuffer);
    }

    // ── Params ──

    [Fact]
    public void ModeAndC_RoundTripThroughTheRegionSnapshotAndParam()
    {
        var p = new FractalParameters
        {
            MathPlaneScene = "map \"z^2 + c\"", MathPlaneMapMode = MathPlaneMapMode.Complex,
            MathPlaneCRe = 0.25, MathPlaneCIm = -0.75, MathPlaneT = 0.4,
        };
        var snap = RegionFractalParams.Snapshot(FractalType.MathPlane, p);
        var q = new FractalParameters();
        snap!.ApplyTo(q);
        Assert.Equal(MathPlaneMapMode.Complex, q.MathPlaneMapMode);
        Assert.Equal(0.25, q.MathPlaneCRe);
        Assert.Equal(-0.75, q.MathPlaneCIm);

        var kv = new[]
        {
            new KeyValuePair<string, string>("MathPlaneMapMode", "2"),
            new KeyValuePair<string, string>("MathPlaneCRe", "0.125"),
            new KeyValuePair<string, string>("MathPlaneCIm", "-1"),
        };
        var r = new FractalParameters();
        RegionFractalParams.FromKeyValues(kv, FractalType.MathPlane, out var err)!.ApplyTo(r);
        Assert.Null(err);
        Assert.Equal(MathPlaneMapMode.Complex, r.MathPlaneMapMode);
        Assert.Equal(0.125, r.MathPlaneCRe);
        Assert.Equal(-1.0, r.MathPlaneCIm);
        Assert.Equal(new[] { "MathPlaneCRe", "MathPlaneCIm" },
            new[] { "MathPlaneCRe", "MathPlaneCIm" }.Where(k => FracturingFog.Abstractions.Animation.FractalAnimatableParamsMap.For(FractalType.MathPlane).Any(d => d.ParamName == k)));
    }

    [Fact]
    public void ComplexModeWithoutMap_DrawsThePlainPlane()
    {
        var plain = Render("vector v 1 1", mode: null);
        var noMap = Render("vector v 1 1", 1);
        // Same plane apart from the note at the top left.
        int diff = 0;
        for (int i = 0; i < plain.ColorBuffer.Length; i++)
            if (plain.ColorBuffer[i] != noMap.ColorBuffer[i] && i / W > 40) diff++;
        Assert.Equal(0, diff);
    }

    // ── Helpers ──

    /// <param name="mode">null: map off (the phase portrait still draws); 1: complex at t = 1.</param>
    private static MathPlaneCalculator Render(string scene, double? mode)
    {
        var p = new FractalParameters
        {
            MathPlaneScene = scene,
            MathPlaneMapMode = mode is null ? MathPlaneMapMode.None : MathPlaneMapMode.Complex,
            MathPlaneT = mode ?? 1, MathPlaneCRe = C0.Real, MathPlaneCIm = C0.Imaginary,
        };
        var calc = new MathPlaneCalculator(W, H) { Zoom = 0.5, FractalParameters = p };
        calc.Calculate(CancellationToken.None);
        return calc;
    }

    private static uint Pixel(MathPlaneCalculator calc, double wx, double wy)
    {
        var (px, py) = MathPlaneCalculator.ToPixel(wx, wy, W, H, 0, 0, 0.5);
        return calc.ColorBuffer[(int)py * W + (int)px];
    }
}
