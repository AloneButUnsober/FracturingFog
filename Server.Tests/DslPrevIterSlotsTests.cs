// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #543 — prev + iter slots in the SandboxExpression interpreter (CalcGen parity).
//   prev = previous iterate z_{n-1} (0 before the first step)
//   iter = iteration index as a real (alias of n)
// Engine-level tests pin the slot binding; render-level tests prove the
// harnesses (UserEquation / Sandbox) thread prev across iterations, and that
// equations not referencing the new slots stay byte-identical.

using System;
using System.Numerics;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Security;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class DslPrevIterSlotsTests
{
    private const int W = 64, H = 48;

    private static uint[] RenderUserEq(string source, string? seed = null)
    {
        var calc = new UserEquationCalculator(W, H)
        {
            CenterX = -0.5, CenterY = 0.0, Zoom = 1.0, MaxIterations = 120,
            ColorMap = new HsvPalette(),
            FractalParameters = new FractalParameters
            {
                UserEquationSource = source,
                UserEquationSeed = seed,
                UserCodeOrigin = UserCodeOrigin.Interactive,
            },
        };
        calc.Calculate(default);
        return calc.ColorBuffer;
    }

    // ── Engine: slot binding ─────────────────────────────────────────────

    [Fact]
    public void PrevSlot_ReturnsSuppliedPreviousIterate()
    {
        var e = SandboxExpression.Parse("prev");
        var r = e.EvalStep(new Complex(5, 5), Complex.Zero, 1, e.NewEnv(), new Complex(3, -4));
        Assert.Equal(3.0, r.Real, 12);
        Assert.Equal(-4.0, r.Imaginary, 12);
    }

    [Fact]
    public void PrevSlot_DefaultsToZero_WhenNotSupplied()
    {
        var e = SandboxExpression.Parse("prev");
        var r = e.EvalStep(new Complex(9, 9), Complex.Zero, 0, e.NewEnv());
        Assert.Equal(0.0, r.Real, 12);
        Assert.Equal(0.0, r.Imaginary, 12);
    }

    [Fact]
    public void IterSlot_IsAliasOfN()
    {
        var e = SandboxExpression.Parse("iter");
        var r = e.EvalStep(Complex.Zero, Complex.Zero, 7, e.NewEnv());
        Assert.Equal(7.0, r.Real, 12);
    }

    [Fact]
    public void PrevAndIter_AreReserved_CannotBeRebound()
    {
        Assert.Throws<FormatException>(() => SandboxExpression.Parse("let prev = 1 in prev"));
        Assert.Throws<FormatException>(() => SandboxExpression.Parse("let iter = 1 in iter"));
    }

    // ── Render: harness threads prev, and unused slots are neutral ────────

    [Fact]
    public void EquationsNotUsingNewSlots_AreByteIdentical()
    {
        // prev cancels to 0 and iter*0 vanishes → identical to the plain map.
        var plain = RenderUserEq("z*z + c");
        var withPrev = RenderUserEq("z*z + c + (prev - prev)");
        var withIter = RenderUserEq("z*z + c + iter*0");
        Assert.Equal(plain, withPrev);
        Assert.Equal(plain, withIter);
    }

    [Fact]
    public void Harness_ThreadsPrevAcrossIterations()
    {
        // prev is 0 on the first step then tracks z_{n-1}, so a prev-weighted map
        // must diverge from the same map without the prev term.
        var withPrev = RenderUserEq("z*z + c + 0.1*prev");
        var without  = RenderUserEq("z*z + c");
        Assert.NotEqual(withPrev, without);
    }
}
