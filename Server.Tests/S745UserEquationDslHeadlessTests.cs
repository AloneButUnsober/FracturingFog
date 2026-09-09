// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #745 — headless (poster / batch) rendering of a DSL-tab "Compile & Load" User Equation.
// The DSL tab stores its equation in UserEquationDslSource and previously had NO
// interpreted path (nothing fed it to UserEquationCalculator), so it rendered only via
// the interactive CalcGen hot-load calc. PosterRenderer / BatchRenderer build a plain
// UserEquationCalculator, which read UserEquationSource (the C# tab) → wrong/empty for a
// DSL equation → no colour / no relief field. UserEquationCalculator now picks the ACTIVE
// tab's source, so the interpreter renders the DSL equation headlessly (colour + the
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

    // DSL tab (ActiveTab = 1) with the equation ONLY in UserEquationDslSource renders —
    // colour + a non-empty relief height field — as poster/batch build it.
    [Fact]
    public void DslTab_Equation_Renders_Headless_With_Field()
    {
        var c = Build(new FractalParameters
        {
            UserEquationActiveTab = 1,
            UserEquationDslSource = "z^2 + c",
            UserCodeOrigin = UserCodeOrigin.Interactive,
        });
        Assert.True(c.IsCompiled, c.LastError);
        Assert.IsAssignableFrom<IHeightFieldSource>(c);
        Assert.True(c.SmoothBuffer.Any(v => v > 0f), "DSL-tab relief field is empty");
        Assert.True(c.ColorBuffer.Distinct().Count() > 4, "DSL-tab colour is flat");
    }

    // A stale C# UserEquationSource must NOT hijack a DSL-tab render — the active tab wins.
    [Fact]
    public void DslTab_Ignores_Stale_CSharp_Source()
    {
        var dslOnly = Build(new FractalParameters
        {
            UserEquationActiveTab = 1, UserEquationDslSource = "z^2 + c",
            UserCodeOrigin = UserCodeOrigin.Interactive,
        });
        var dslWithStale = Build(new FractalParameters
        {
            UserEquationActiveTab = 1, UserEquationDslSource = "z^2 + c",
            UserEquationSource = "z*z*z*z + c",   // stale leftover from the C# tab
            UserCodeOrigin = UserCodeOrigin.Interactive,
        });
        // The DSL equation is what renders in both → identical field (stale C# ignored).
        Assert.Equal(dslOnly.SmoothBuffer, dslWithStale.SmoothBuffer);
    }

    // C# tab (ActiveTab = 0, default) still uses UserEquationSource — unchanged.
    [Fact]
    public void CSharpTab_Still_Uses_UserEquationSource()
    {
        var c = Build(new FractalParameters
        {
            UserEquationActiveTab = 0,
            UserEquationSource = "z*z + c",
            UserEquationDslSource = "z^2 + c",   // present but not the active tab
            UserCodeOrigin = UserCodeOrigin.Interactive,
        });
        Assert.True(c.IsCompiled, c.LastError);
        Assert.True(c.SmoothBuffer.Any(v => v > 0f));
    }
}
