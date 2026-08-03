// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #213 regression — CalcGen "Compile & Load" codegen defects.
//
// Bug 1 (CS0103 Cr_v / Ci_v): the AVX perturbation δ body binds CRef → Cr_v /
// Ci_v, but those SIMD c-broadcast locals used to be declared ONLY inside the
// generated Derivative block. For any perturbation-eligible map whose δ update
// references c (a c-COEFFICIENT map — δ_{n+1} = c·(2Zδ+δ²) for c·z²), the δ
// body referenced undeclared locals → Roslyn CS0103 → the whole calculator
// failed to compile. Fixed by hoisting the Cr_v / Ci_v declaration to the
// shared per-iteration scope so both the derivative and δ bodies see it.
//
// Bug 2 (negative-power maps render blank): the old DSL translation of a
// negative power to 1/(z)^n produced 0·(1/0) = NaN at the z=0 Mandelbrot seed,
// blanking the image. The Phase 6 (tranche 2) general pow() is zero-guarded
// (pow(0,-3)=0), so the same maps expressed with pow() seed finite and render.

using System;
using FracturingFog.CalculatorGen;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class CalcGenHotLoad213RegressionTests
{
    private const int W = 64, H = 48;
    private const int MaxIter = 150;
    private const double Cx = -0.5, Cy = 0.0, Zoom = 1.0;

    // Bug 1 — c-coefficient maps are perturbation-eligible (holomorphic
    // polynomials in z), so CalcGen emits the AVX perturbation δ body, and that
    // body references c. Each must now generate + Roslyn-compile with NO errors.
    [Theory]
    [InlineData("c*z*z + c")]
    [InlineData("c*sqr(z) + c")]
    [InlineData("c*z*z*z + c")]
    [InlineData("c*z*z + z + c")]
    public void CompileAndLoad_CInDeltaBody_Succeeds_NoCs0103(string dsl)
    {
        var hot = CalculatorGenHotLoad.TryCompileAndLoad(dsl, "C213b1");
        Assert.True(hot.Ok, $"[{dsl}] generate+compile failed (regression #213 bug 1): {hot.Error}");
        Assert.DoesNotContain("Cr_v", hot.Error ?? "");
        Assert.DoesNotContain("Ci_v", hot.Error ?? "");
    }

    // Bug 2 — Movie Reel / Donut Star style negative-power maps, expressed with
    // the zero-guarded pow(), compile AND render a real fractal (both in-set and
    // escaped pixels), not the old all-blank NaN image.
    [Theory]
    [InlineData("z*pow(z, -3) + c*pow(c, -2)")]   // Movie Reel
    [InlineData("pow(z, -2) + c")]                 // Donut-Star-ish
    public void NegativePowerMap_ViaPow_CompilesAndRendersNonBlank(string dsl)
    {
        var hot = CalculatorGenHotLoad.TryCompileAndLoad(dsl, "C213b2");
        Assert.True(hot.Ok, $"[{dsl}] generate+compile failed: {hot.Error}");

        var map = new HsvPalette();
        var calc = (IFractalCalculator)Activator.CreateInstance(hot.CalculatorType!, W, H)!;
        calc.CenterX = Cx; calc.CenterY = Cy; calc.Zoom = Zoom; calc.MaxIterations = MaxIter;
        var prop = calc.GetType().GetProperty("ColorMap");
        prop?.SetValue(calc, map);
        calc.Calculate(default);

        uint inset = ((IColorMap)map).InSetColor;
        int inCount = 0, escCount = 0;
        foreach (var px in calc.ColorBuffer) { if (px == inset) inCount++; else escCount++; }
        // Non-blank = at least SOME pixels escaped (the old NaN-seed image was
        // uniformly in-set/black). We don't require in-set pixels — a pure
        // pole map may escape everywhere — only that it isn't a degenerate fill.
        Assert.True(escCount > 0, $"[{dsl}] rendered blank (in-set {inCount}, escaped {escCount}) — #213 bug 2");
    }
}
