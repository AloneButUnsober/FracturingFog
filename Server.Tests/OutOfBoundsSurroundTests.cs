// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #615 Phase 1 — the beyond-escape-radius surround colour. When a 2D escape-time
// fractal is zoomed out far enough that the set shrinks to a dot, the screen
// fills with a flat disk of pixels whose plane coordinate already lies outside
// the escape disk (|c| >= escapeRadius). IColorMap.OutOfBoundsColor lets a theme
// paint that surround independently of the fractal. These tests assert:
//   • the geometric IsOutOfBounds predicate,
//   • the CPU calculators (Mandelbrot / EscapeTime / UserEquation) paint the
//     surround with the theme's colour and leave every other pixel byte-identical
//     to the null-override baseline,
//   • a null override is byte-identical (no surround pixels written),
//   • the colour survives the DataDrivenColorThemes Create -> Export round-trip
//     (the #96-class silent-drop bug).

using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class OutOfBoundsSurroundTests
{
    private const int W = 48, H = 48;
    private const uint Oob = 0xFF3366CCu;   // distinctive sentinel surround colour

    private static ColorThemeData GradientTheme(InSetColorData? oob)
        => new()
        {
            Name = "OobProbe",
            Category = "User",
            Kind = ColorThemeKind.Gradient,
            Stops =
            {
                new ColorStopData { Position = 0f, R = 0,   G = 0,   B = 0,   A = 255 },
                new ColorStopData { Position = 1f, R = 255, G = 255, B = 255, A = 255 },
            },
            OutOfBoundsColor = oob,
        };

    private static InSetColorData OobData => new(0x33, 0x66, 0xCC) { A = 0xFF };

    public enum Kind { Mandelbrot, EscapeTime, UserEquation }

    private static uint[] Render(Kind kind, double zoom, IColorMap map)
    {
        switch (kind)
        {
            case Kind.Mandelbrot:
            {
                var c = new MandelbrotCalculator(W, H)
                {
                    CenterX = 0.0, CenterY = 0.0, Zoom = zoom, MaxIterations = 150,
                    ColorMap = map,
                };
                c.Calculate(default);
                return (uint[])c.ColorBuffer.Clone();
            }
            case Kind.EscapeTime:
            {
                var c = new EscapeTimeCalculator(W, H)
                {
                    FractalType = FractalType.Mandelbrot,
                    CenterX = 0.0, CenterY = 0.0, Zoom = zoom, MaxIterations = 150,
                    ColorMap = map,
                };
                c.Calculate(default);
                return (uint[])c.ColorBuffer.Clone();
            }
            default:
            {
                const string z2 = "z*z + c";
                var c = new UserEquationCalculator(W, H)
                {
                    CenterX = 0.0, CenterY = 0.0, Zoom = zoom, MaxIterations = 150,
                    ColorMap = map,
                    FractalParameters = new FractalParameters { UserEquationSource = z2 },
                };
                c.Compile(z2);
                c.Calculate(default);
                return (uint[])c.ColorBuffer.Clone();
            }
        }
    }

    // ── Geometric predicate ──────────────────────────────────────────────────

    [Fact]
    public void IsOutOfBounds_UsesSquaredMagnitudeVsRadius()
    {
        // |(3,4)| = 5 exactly.
        Assert.True(IColorMap.IsOutOfBounds(3, 4, 5.0));       // on the circle counts as out
        Assert.True(IColorMap.IsOutOfBounds(3, 4, 4.999));     // inside the radius -> out
        Assert.False(IColorMap.IsOutOfBounds(3, 4, 5.001));    // just beyond -> in bounds
        Assert.False(IColorMap.IsOutOfBounds(0, 0, 1.0));      // centre is never out
        Assert.True(IColorMap.IsOutOfBounds(-100, 0, 2.0));    // far corner -> out
    }

    // ── Calculator paint ─────────────────────────────────────────────────────

    // Zoom chosen per family so the escape disk (Mandelbrot/EscapeTime R = 512,
    // UserEquation default R = 32) sits well inside the viewport: the corners are
    // out of bounds, the centre pixel is in the set.
    [Theory]
    [InlineData(Kind.Mandelbrot, 0.0015)]
    [InlineData(Kind.EscapeTime, 0.0015)]
    [InlineData(Kind.UserEquation, 0.02)]
    public void Surround_PaintsOverride_LeavesRestByteIdentical(Kind kind, double zoom)
    {
        uint[] baseline = Render(kind, zoom, DataDrivenColorThemes.Create(GradientTheme(null))!);
        uint[] painted  = Render(kind, zoom, DataDrivenColorThemes.Create(GradientTheme(OobData))!);

        Assert.Equal(baseline.Length, painted.Length);

        int corner = 0;                       // top-left: |c| far beyond the radius
        int centre = (H / 2) * W + (W / 2);   // c ~ (0,0): inside the set

        Assert.Equal(Oob, painted[corner]);            // surround painted
        Assert.NotEqual(Oob, painted[centre]);         // fractal centre untouched
        Assert.Equal(baseline[centre], painted[centre]);

        int surroundCount = 0, keptCount = 0;
        for (int i = 0; i < painted.Length; i++)
        {
            // The override only ever writes the sentinel; every other pixel is
            // byte-identical to the null-override baseline.
            if (painted[i] != baseline[i])
            {
                Assert.Equal(Oob, painted[i]);
                surroundCount++;
            }
            else keptCount++;
        }

        Assert.True(surroundCount > 0, "zoomed-out frame should have a surround region");
        Assert.True(keptCount > 0, "frame should retain non-surround pixels");
    }

    [Theory]
    [InlineData(Kind.Mandelbrot, 0.0015)]
    [InlineData(Kind.EscapeTime, 0.0015)]
    [InlineData(Kind.UserEquation, 0.02)]
    public void NullOverride_IsByteIdentical(Kind kind, double zoom)
    {
        // Two independent null-override renders agree, and no pixel is the
        // sentinel colour — the feature is inert unless a colour is set.
        uint[] a = Render(kind, zoom, DataDrivenColorThemes.Create(GradientTheme(null))!);
        uint[] b = Render(kind, zoom, DataDrivenColorThemes.Create(GradientTheme(null))!);
        Assert.Equal(a, b);
        Assert.DoesNotContain(Oob, a);
    }

    // ── Export round-trip (guards the #96-class silent-drop bug) ─────────────

    [Fact]
    public void Export_PreservesOutOfBoundsColour()
    {
        var map = DataDrivenColorThemes.Create(GradientTheme(OobData));
        Assert.NotNull(map);
        Assert.Equal(Oob, ((IColorMap)map!).OutOfBoundsColor);

        var exported = DataDrivenColorThemes.Export(map!);
        Assert.NotNull(exported!.OutOfBoundsColor);
        Assert.Equal(0x33, exported.OutOfBoundsColor!.R);
        Assert.Equal(0x66, exported.OutOfBoundsColor.G);
        Assert.Equal(0xCC, exported.OutOfBoundsColor.B);
        Assert.Equal(0xFF, exported.OutOfBoundsColor.A);
    }

    [Fact]
    public void Export_NoOverride_RoundTripsAsNull()
    {
        var map = DataDrivenColorThemes.Create(GradientTheme(null));
        Assert.Null(((IColorMap)map!).OutOfBoundsColor);
        var exported = DataDrivenColorThemes.Export(map!);
        Assert.Null(exported!.OutOfBoundsColor);
    }
}
