// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #542 — seed expression (z0) for the User Equation interpreter.
//
// FractalParameters.UserEquationSeed is a bare-DSL expression over `c` (the
// pixel), evaluated once per pixel before iteration:
//   * blank ⇒ z0 = 0 (Mandelbrot orbit; legacy, byte-identical)
//   * `c`   ⇒ z0 = pixel (Julia — pair with a literal constant in the step)
// This unblocks the fractalforums map whose z0=0 orbit hits 1/0 = Inf and
// fills the whole image; seeding z0 = pixel renders the real Julia set.

using System;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Security;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class DslSeedExpressionTests
{
    private const int W = 64, H = 48;

    private static UserEquationCalculator Render(string source, string? seed,
                                                 double cx = -0.5, double cy = 0.0)
    {
        var calc = new UserEquationCalculator(W, H)
        {
            CenterX = cx, CenterY = cy, Zoom = 1.0, MaxIterations = 150,
            ColorMap = new HsvPalette(),
            FractalParameters = new FractalParameters
            {
                UserEquationSource = source,
                UserEquationSeed = seed,
                UserCodeOrigin = UserCodeOrigin.Interactive,
            },
        };
        calc.Calculate(default);
        return calc;
    }

    private static int Escaped(UserEquationCalculator calc)
    {
        uint inset = calc.ColorMap.InSetColor;
        int n = 0;
        foreach (var px in calc.ColorBuffer) if (px != inset) n++;
        return n;
    }

    // Blank seed ⇒ z0 = 0, byte-identical to the pre-#542 default.
    [Fact]
    public void EmptySeed_IsByteIdenticalToNoSeed()
    {
        var none = Render("z*z + c", null).ColorBuffer;
        var blank = Render("z*z + c", "   ").ColorBuffer;
        Assert.Equal(none, blank);
    }

    // Headline: with a fixed-constant map (no `c` in the step), z0 = pixel is the
    // ONLY thing that varies the pixel — so the seed turns a degenerate uniform
    // image (every pixel the same orbit of 0) into the real Julia set.
    [Fact]
    public void SeedPixel_TurnsFixedConstantMap_IntoJuliaSet()
    {
        const string map = "z*z + (-0.8 + 0.156*i)";   // literal K, no `c`

        var unseeded = Render(map, null, cx: 0.0, cy: 0.0);
        // z0 = 0 and the map ignores the pixel → every pixel is the same orbit.
        uint first = unseeded.ColorBuffer[0];
        Assert.All(unseeded.ColorBuffer, px => Assert.Equal(first, px));

        var seeded = Render(map, "c", cx: 0.0, cy: 0.0);
        // z0 = pixel → a real Julia set: both escaped and in-set pixels present.
        int esc = Escaped(seeded);
        Assert.True(esc > 0 && esc < seeded.ColorBuffer.Length,
            $"seeded Julia not a real fractal (escaped {esc}/{seeded.ColorBuffer.Length})");
    }

    // Documents the user's original symptom: the fractalforums map with z0 = 0
    // renders blank — 1/z = 1/0 = Inf → sin(Inf) = NaN → nothing trips bailout,
    // whole image is in-set. (Seeding z0 = pixel is required to explore it; the
    // interpreter's real-valued log limits this particular map further — see the
    // DSL-robustness follow-ups.)
    [Fact]
    public void ForumMap_Z0Zero_RendersBlank()
    {
        var calc = Render("log(sin(abs(1/z))) + c", null, cx: 0.0, cy: 0.0);
        Assert.Equal(0, Escaped(calc));   // all in-set
    }

    // A malformed seed is reported and falls back to z0 = 0 (no crash, not blank
    // for a well-behaved map).
    [Fact]
    public void MalformedSeed_SurfacesError_AndFallsBackToZero()
    {
        var calc = Render("z*z + c", "@@bad@@");
        Assert.False(string.IsNullOrEmpty(calc.SeedError));
        var baseline = Render("z*z + c", null).ColorBuffer;
        Assert.Equal(baseline, calc.ColorBuffer);   // fell back to z0 = 0
    }

    [Fact]
    public void Seed_SurvivesClone()
    {
        var p = new FractalParameters { UserEquationSeed = "c" };
        Assert.Equal("c", p.Clone().UserEquationSeed);
    }
}
