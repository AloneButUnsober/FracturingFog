// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #745 — headless (poster / batch) rendering of a DSL-tab "Compile & Load" User Equation.
// (#1088: the DSL tab is now the UserEquationUseCalcGen flag on the single
// UserEquationSource.) The DSL tab's equation previously had NO interpreted path
// (nothing fed it to UserEquationCalculator), so it rendered only via
// the interactive CalcGen hot-load calc. PosterRenderer / BatchRenderer build a plain
// UserEquationCalculator, which read UserEquationSource (the C# tab) → wrong/empty for a
// DSL equation → no colour / no relief field. UserEquationCalculator now reads the one
// source, so the interpreter renders the DSL equation headlessly (colour + the
// SmoothBuffer relief field), no Roslyn / hot-load needed.

using System.Linq;
using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Security;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S745UserEquationDslHeadlessTests
{
    private static UserEquationCalculator Build(FractalParameters fp)
    {
        var c = new UserEquationCalculator(96, 72)
        {
            CenterX = -0.5, CenterY = 0.0, Zoom = 1.0, MaxIterations = 200,
            ColorMap = new MonoBandMap(),
            FractalParameters = fp,
        };
        c.Calculate(default);
        return c;
    }

    // A CalcGen equation (#1088: UserEquationUseCalcGen, the old DSL tab) renders —
    // colour + a non-empty relief height field — as poster/batch build it.
    [Fact]
    public void CalcGenEquation_Renders_Headless_With_Field()
    {
        var c = Build(new FractalParameters
        {
            UserEquationUseCalcGen = true,
            UserEquationSource = "z^2 + c",
            UserCodeOrigin = UserCodeOrigin.Interactive,
        });
        Assert.True(c.IsCompiled, c.LastError);
        Assert.IsAssignableFrom<IHeightFieldSource>(c);
        Assert.True(c.SmoothBuffer.Any(v => v > 0f), "CalcGen equation relief field is empty");
        Assert.True(c.ColorBuffer.Distinct().Count() > 4, "CalcGen equation colour is flat");
    }

    // #1088 — one source: the CalcGen flag routes the editor and never changes the
    // interpreted image (before #1088 a stale C#-tab source could be drawn instead).
    [Fact]
    public void CalcGenFlag_DoesNotChange_TheImage()
    {
        var on = Build(new FractalParameters
        {
            UserEquationUseCalcGen = true, UserEquationSource = "z^2 + c",
            UserCodeOrigin = UserCodeOrigin.Interactive,
        });
        var off = Build(new FractalParameters
        {
            UserEquationUseCalcGen = false, UserEquationSource = "z^2 + c",
            UserCodeOrigin = UserCodeOrigin.Interactive,
        });
        Assert.Equal(on.SmoothBuffer, off.SmoothBuffer);
        Assert.Equal(on.ColorBuffer, off.ColorBuffer);
    }

    // A C#-style source (flag off) is still translated and rendered.
    [Fact]
    public void CSharpStyleSource_Still_Renders()
    {
        var c = Build(new FractalParameters
        {
            UserEquationSource = "return Complex.Pow(z, 2) + c;",
            UserCodeOrigin = UserCodeOrigin.Interactive,
        });
        Assert.True(c.IsCompiled, c.LastError);
        Assert.True(c.SmoothBuffer.Any(v => v > 0f));
    }
}
