// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #215 — analytic distance-estimate (dz/dc) for the inverse trig / hyperbolic
// functions. Before this change asin/acos/atan/asinh/acosh/atanh folded into
// the DE gate (SupportsDe=false → flat-shaded normals). The differentiator now
// carries their first-order chain rules (∂asin(u)/∂v = u'/√(1−u²), etc.), so:
//
//   • SupportsDe is ON for these maps (perturbation/SA stay OFF — transcendental).
//   • The generated calculator still COMPILES: the derivative trees now contain
//     the internal √ node, which every derivative-walking emitter (direct scalar
//     / AVX2 + the three perturbation-deriv emitters) must lower without throwing.
//
// The symbolic correctness of the rules is covered by the CalculatorGen unit
// tests (CalculatorGenUnitTests, `--calcgen-test`); these tests guard the
// end-to-end codegen: flag state + Roslyn compile + non-blank render.

using System;
using FracturingFog.CalculatorGen;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class CalcGenInverseTrigDeTests
{
    private const int W = 64, H = 48;
    private const int MaxIter = 120;

    // Every inverse trig / hyperbolic map: analytic DE ON, perturbation/SA OFF.
    [Theory]
    [InlineData("asin(z) + c")]
    [InlineData("acos(z) + c")]
    [InlineData("atan(z) + c")]
    [InlineData("asinh(z) + c")]
    [InlineData("acosh(z) + c")]
    [InlineData("atanh(z) + c")]
    public void Preview_InverseTrig_SupportsDe_ButNotPerturbation(string dsl)
    {
        var p = CalculatorGenApi.Preview(dsl);
        Assert.True(p.Ok, $"[{dsl}] preview failed: {p.Error}");
        Assert.True(p.SupportsDe, $"[{dsl}] expected SupportsDe=true (#215 analytic DE)");
        Assert.False(p.SupportsPerturbation,
            $"[{dsl}] expected SupportsPerturbation=false (transcendental — no closed-form δ-Taylor)");
    }

    // Codegen regression: with SupportsDe=true the derivative body (containing
    // the internal √ node) is emitted through the direct scalar / AVX2 emitters
    // AND the three perturbation-deriv emitters. Each must lower √ without
    // throwing at generation and the whole calculator must Roslyn-compile.
    [Theory]
    [InlineData("asin(z) + c")]
    [InlineData("acos(z) + c")]
    [InlineData("atan(z) + c")]
    [InlineData("asinh(z) + c")]
    [InlineData("acosh(z) + c")]
    [InlineData("atanh(z) + c")]
    [InlineData("asin(z*z) + c")]          // chain rule: √(1 − (z²)²) in the deriv
    [InlineData("z*z + atan(z) + c")]      // deriv mixes polynomial + rational DE term
    public void CompileAndLoad_InverseTrigDe_Succeeds(string dsl)
    {
        var hot = CalculatorGenHotLoad.TryCompileAndLoad(dsl, "CInvTrigDe");
        Assert.True(hot.Ok, $"[{dsl}] generate+compile failed (#215 DE codegen): {hot.Error}");
        Assert.DoesNotContain("OpSqrt", hot.Error ?? "");   // emitter never fell through
    }

    // End-to-end proof the DE-enabled kernel actually RUNS: `z*z + asin(c)`
    // escapes (the z² term) so it yields a mixed image, and its ∂p/∂c carries
    // the inverse-trig DE term 1/√(1−c²) — so the √-bearing derivative body
    // executes every iteration on the shallow direct path without crashing.
    // (Pure asin/atanh maps are bounded — they never exceed the bailout, so an
    // all-in-set image is correct, not blank; hence the z² driver here.)
    [Fact]
    public void EscapingInverseTrigMap_WithDe_CompilesAndRendersMixed()
    {
        const string dsl = "z*z + asin(c)";
        Assert.True(CalculatorGenApi.Preview(dsl).SupportsDe, $"[{dsl}] expected SupportsDe=true");

        var hot = CalculatorGenHotLoad.TryCompileAndLoad(dsl, "CInvTrigRender");
        Assert.True(hot.Ok, $"[{dsl}] generate+compile failed: {hot.Error}");

        var map = new HsvPalette();
        var calc = (IFractalCalculator)Activator.CreateInstance(hot.CalculatorType!, W, H)!;
        calc.CenterX = -0.5; calc.CenterY = 0.0; calc.Zoom = 0.6; calc.MaxIterations = MaxIter;
        calc.GetType().GetProperty("ColorMap")?.SetValue(calc, map);
        calc.Calculate(default);

        uint inset = ((IColorMap)map).InSetColor;
        int inCount = 0, escCount = 0;
        foreach (var px in calc.ColorBuffer) { if (px == inset) inCount++; else escCount++; }
        Assert.True(inCount > 0 && escCount > 0,
            $"[{dsl}] expected a mixed image (in-set {inCount}, escaped {escCount})");
    }
}
