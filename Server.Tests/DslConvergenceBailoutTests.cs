// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #544 — convergence bailout for the User Equation interpreter.
//
// FractalParameters.UserEquationBailoutCondition is a boolean DSL condition
// over z / prev / c / n / iter. When it fires the orbit stops early and the
// pixel is classified "converged" (coloured by convergence speed) instead of
// running to the iteration cap. This unblocks Newton / Magnet / Nova maps
// whose interesting region converges rather than escapes. Combined with the
// #542 seed (Newton must start at z0 = pixel, not 0) and the #543 `prev` slot.

using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Security;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class DslConvergenceBailoutTests
{
    private const int W = 64, H = 48;

    // Newton's method for z^3 - 1: z - f/f' = z - (z^3-1)/(3 z^2).
    private const string Newton = "z - (z*z*z - 1)/(3*z*z)";
    private const string Converge = "abs(z - prev) < 0.0001";

    private static UserEquationCalculator RenderCalc(string source, string? seed, string? cond)
    {
        var calc = new UserEquationCalculator(W, H)
        {
            CenterX = 0.0, CenterY = 0.0, Zoom = 1.0, MaxIterations = 150,
            ColorMap = new HsvPalette(),
            FractalParameters = new FractalParameters
            {
                UserEquationSource = source,
                UserEquationSeed = seed,
                UserEquationBailoutCondition = cond,
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

    // No / empty condition ⇒ escape-radius test only, byte-identical to legacy.
    [Fact]
    public void EmptyCondition_IsByteIdenticalToNone()
    {
        var none = RenderCalc("z*z + c", null, null).ColorBuffer;
        var empty = RenderCalc("z*z + c", null, "   ").ColorBuffer;
        Assert.Equal(none, empty);
    }

    // Newton basins converge (they never escape), so WITHOUT a convergence
    // bailout they run to the cap and read as interior; WITH it they stop early
    // and paint convergence-speed bands. The two images must differ and the
    // conditioned one must colour more pixels.
    [Fact]
    public void ConvergenceBailout_RendersNewtonBasins()
    {
        var noCond = RenderCalc(Newton, seed: "c", cond: null);
        var withCond = RenderCalc(Newton, seed: "c", cond: Converge);

        Assert.NotEqual(noCond.ColorBuffer, withCond.ColorBuffer);
        Assert.True(Colored(withCond) > Colored(noCond),
            $"convergence bailout coloured no more pixels (with {Colored(withCond)} vs {Colored(noCond)})");
    }

    // A malformed condition is reported and dropped (renders as if unset).
    [Fact]
    public void MalformedCondition_SurfacesError_AndFallsBack()
    {
        var calc = RenderCalc("z*z + c", null, "@@bad@@");
        Assert.False(string.IsNullOrEmpty(calc.BailoutConditionError));
        var baseline = RenderCalc("z*z + c", null, null).ColorBuffer;
        Assert.Equal(baseline, calc.ColorBuffer);
    }

    [Fact]
    public void BailoutCondition_SurvivesClone()
    {
        var p = new FractalParameters { UserEquationBailoutCondition = Converge };
        Assert.Equal(Converge, p.Clone().UserEquationBailoutCondition);
    }
}
