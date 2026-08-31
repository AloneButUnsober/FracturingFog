// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #541 — configurable escape radius for the DSL escape-time interpreter paths.
//
// FractalParameters.EscapeRadius (|z| bailout, 0 = auto) is honoured by the
// User Equation interpreter and the Sandbox calculator. These tests pin the
// contract:
//   * 0 == the legacy default (|z|² = 1024, i.e. r = 32) — byte-identical.
//   * a different radius changes which pixels escape → a different image.

using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Security;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class DslConfigurableEscapeRadiusTests
{
    private const int W = 64, H = 48;

    private static UserEquationCalculator RenderUserEq(string dsl, double escapeRadius) =>
        RenderAndReturn(new UserEquationCalculator(W, H)
        {
            CenterX = -0.5, CenterY = 0.0, Zoom = 1.0, MaxIterations = 120,
            FractalParameters = new FractalParameters
            {
                UserEquationSource = dsl,
                UserCodeOrigin = UserCodeOrigin.Interactive,
                EscapeRadius = escapeRadius,
            },
        });

    private static SandboxCalculator RenderSandbox(string dsl, double escapeRadius) =>
        RenderAndReturn(new SandboxCalculator(W, H)
        {
            CenterX = -0.5, CenterY = 0.0, Zoom = 1.0, MaxIterations = 120,
            FractalParameters = new FractalParameters { SandboxSource = dsl, EscapeRadius = escapeRadius },
        });

    private static T RenderAndReturn<T>(T calc) where T : IFractalCalculator
    {
        calc.Calculate(default);
        return calc;
    }

    // 0 (auto) must equal an explicit r = 32 (|z|² = 1024 legacy default), and a
    // large radius must change the image. Proves the field is wired and the
    // sentinel preserves legacy output.
    [Theory]
    [InlineData("z*z + c")]
    [InlineData("sin(z) + c")]
    public void UserEquation_EscapeRadius_AutoEqualsLegacy_AndOverrideChangesImage(string dsl)
    {
        var auto = RenderUserEq(dsl, 0.0).ColorBuffer;
        var r32  = RenderUserEq(dsl, 32.0).ColorBuffer;
        var r1000 = RenderUserEq(dsl, 1000.0).ColorBuffer;

        Assert.Equal(auto, r32);          // 0 == legacy r=32
        Assert.NotEqual(auto, r1000);     // override takes effect
    }

    [Fact]
    public void Sandbox_EscapeRadius_AutoEqualsLegacy_AndOverrideChangesImage()
    {
        const string dsl = "z*z + c";
        var auto = RenderSandbox(dsl, 0.0).ColorBuffer;
        var r32  = RenderSandbox(dsl, 32.0).ColorBuffer;
        var r1000 = RenderSandbox(dsl, 1000.0).ColorBuffer;

        Assert.Equal(auto, r32);
        Assert.NotEqual(auto, r1000);
    }

    // The two interpreter paths stay in lockstep under a shared non-default
    // radius (guards against wiring one path but not the other).
    [Fact]
    public void UserEquation_And_Sandbox_AgreeUnderSharedRadius()
    {
        const string dsl = "z*z + c";
        var ueq = RenderUserEq(dsl, 1000.0).ColorBuffer;
        var sbx = RenderSandbox(dsl, 1000.0).ColorBuffer;
        Assert.Equal(sbx, ueq);
    }

    // EscapeRadius round-trips through Clone.
    [Fact]
    public void EscapeRadius_SurvivesClone()
    {
        var p = new FractalParameters { EscapeRadius = 128.0 };
        Assert.Equal(128.0, p.Clone().EscapeRadius);
    }
}
