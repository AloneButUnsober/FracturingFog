// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Resolution guard for the fractalforums map  z -> log(sin(abs(1/z))) + c.
//
// The original report was "the DSL compiles but nothing renders". Root cause is
// NOT a broken transcendental — the interpreter already promotes log/sqrt of a
// negative real to the complex branch (log(-5) = ln5 + iπ, sqrt(-4) = 2i). Two
// real causes, both already addressed by shipped work:
//
//   1. Seeding. z0 = 0 makes 1/z a pole (1/0 -> ∞ -> sin(∞) = NaN), so every
//      pixel is NaN. The map must start at z0 = pixel — the #542 seed slot.
//   2. Escape radius. log∘sin has a tiny dynamic range; at the legacy bailout
//      r = 32 (|z|² > 1024) nothing escapes and the frame reads as solid
//      interior. Lowering the radius (#541 configurable EscapeRadius) makes the
//      orbits escape and the structure appear.
//
// This test locks that: seeded + small radius escapes; unseeded/large radius is
// blank; and the negative-real transcendental branch stays complex.

using System.Numerics;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Security;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class DslTranscendentalForumMapTests
{
    private const int W = 80, H = 60;
    private const string Map = "log(sin(abs(1/z))) + c";

    private static UserEquationCalculator Render(string? seed, double escapeRadius)
    {
        var calc = new UserEquationCalculator(W, H)
        {
            CenterX = 0.0, CenterY = 0.0, Zoom = 0.25, MaxIterations = 200,
            ColorMap = new HsvPalette(),
            FractalParameters = new FractalParameters
            {
                UserEquationSource = Map,
                UserEquationSeed = seed,
                EscapeRadius = escapeRadius,
                UserCodeOrigin = UserCodeOrigin.Interactive,
            },
        };
        calc.Calculate(default);
        return calc;
    }

    private static int Colored(UserEquationCalculator calc)
    {
        uint inset = calc.ColorMap.InSetColor;
        int n = 0;
        foreach (var px in calc.ColorBuffer) if (px != inset) n++;
        return n;
    }

    // Seeded at the pixel + a small escape radius: the transcendental map escapes
    // and paints real structure (the frame is not solid interior).
    [Fact]
    public void SeededSmallRadius_RendersStructure()
    {
        var calc = Render(seed: "c", escapeRadius: 2.0);
        Assert.Empty(calc.SeedError ?? "");
        int colored = Colored(calc);
        int total = W * H;
        Assert.InRange(colored, total / 20, total);   // a real fraction escapes
    }

    // Legacy default radius (32): the same seeded map stays bounded and reads as
    // solid interior — this is the "nothing renders" the user hit. Proves the
    // radius, not a transcendental bug, is what gated the render.
    [Fact]
    public void SeededLargeRadius_IsBlank()
    {
        var calc = Render(seed: "c", escapeRadius: 0.0);   // 0 = auto → r = 32
        Assert.True(Colored(calc) <= 4,
            $"expected near-blank at r=32 but {Colored(calc)} pixels escaped");
    }

    // Unseeded (z0 = 0) is a pole: 1/0 → ∞ → NaN. Distinct failure from the blank
    // above, fixed by the same seed. We only assert it differs from the seeded
    // render (the seed changes the image).
    [Fact]
    public void SeedChangesTheImage()
    {
        var seeded = Render(seed: "c", escapeRadius: 2.0).ColorBuffer;
        var unseeded = Render(seed: null, escapeRadius: 2.0).ColorBuffer;
        Assert.NotEqual(seeded, unseeded);
    }

    // The interpreter promotes log/sqrt of a negative real to the complex branch
    // rather than returning NaN — the property the "broken transcendental"
    // theory wrongly assumed was missing.
    [Theory]
    [InlineData("log(0 - 5)", 1.6094379124341003, 3.141592653589793)]
    [InlineData("sqrt(0 - 4)", 0.0, 2.0)]
    public void NegativeRealTranscendental_PromotesToComplex(string src, double re, double im)
    {
        var expr = SandboxExpression.Parse(src);
        var v = expr.EvalStep(Complex.Zero, Complex.Zero, 0, expr.NewEnv());
        Assert.False(double.IsNaN(v.Real) || double.IsNaN(v.Imaginary));
        Assert.Equal(re, v.Real, 12);
        Assert.Equal(im, v.Imaginary, 12);
    }
}
