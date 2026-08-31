// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Regression — CalcGen deep-zoom (QD / DD "direct") emitters generated broken
// C# for a transcendental (or abs) nested inside another transcendental, e.g.
// the fractalforums map `log(sin(abs(1/z))) + c`.
//
// Root cause: QdEmitter / QdDirectEmitter / DdDirectEmitter's ScalarComplex
// (sin/cos/exp/log) and OpFold (abs) reached the high limb with a bare
// `{operand}.X0` / `{operand}.Hi`. When the operand text starts with a cast —
// which it does after abs (`(QD)Math.Sqrt(...)`) or a nested transcendental
// (`(QD)Math.Sin(...)`) — C# binds the cast looser than member access:
//
//     (QD)Math.Sqrt(...).X0   parses as   (QD)( Math.Sqrt(...).X0 )
//
// so `.X0` / `.Hi` lands on a `double`, yielding
//     CS1061 'double' does not contain a definition for 'X0'  (QD path)
//     CS1061 'double' does not contain a definition for 'Hi'  (DD path)
// and the whole hot-loaded calculator failed to compile. Fixed by wrapping the
// operand: `((QD)({operand})).X0` / `((DD)({operand})).Hi`, matching the
// already-correct sibling ops (OpArg / OpMin / OpMax / OpMod / PerComp).

using System;
using FracturingFog.CalculatorGen;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class CalcGenNestedTranscendentalRegressionTests
{
    private const int W = 64, H = 48;
    private const int MaxIter = 150;
    private const double Cx = -0.5, Cy = 0.0, Zoom = 1.0;

    // Each map nests a transcendental (or abs) inside a transcendental, so the
    // QD / DD direct emitters emit `.X0` / `.Hi` against a cast-prefixed
    // operand. All must generate + Roslyn-compile with NO CS1061.
    [Theory]
    [InlineData("log(sin(abs(1/z))) + c")]  // the fractalforums report
    [InlineData("sin(exp(z)) + c")]         // transcendental in transcendental
    [InlineData("cos(log(z)) + c")]
    [InlineData("exp(sin(z)) + c")]
    [InlineData("log(sin(z)) + c")]
    [InlineData("abs(exp(z)) + c")]         // OpFold (abs) wrapping a cast operand
    public void NestedTranscendental_CompilesAndLoads_NoCs1061(string dsl)
    {
        var hot = CalculatorGenHotLoad.TryCompileAndLoad(dsl, "CNestTx");
        Assert.True(hot.Ok, $"[{dsl}] generate+compile failed: {hot.Error}");
        // Guard against the specific defect re-appearing in any tier.
        Assert.DoesNotContain("CS1061", hot.Error ?? "");
        Assert.DoesNotContain("'X0'", hot.Error ?? "");
        Assert.DoesNotContain("'Hi'", hot.Error ?? "");
    }

    // A pole-free nested map seeds finite at z=0, so the compiled calculator
    // renders a real fractal (some pixels escape) rather than a degenerate
    // fill. Proves the fix produces working code, not just code that compiles.
    // (The `1/z` map above is intentionally excluded here — its z=0 Mandelbrot
    // seed hits 1/0 = Inf and fills in-set regardless of this bug.)
    [Fact]
    public void NestedTranscendental_RendersNonBlank()
    {
        const string dsl = "sin(exp(z)) + c";
        var hot = CalculatorGenHotLoad.TryCompileAndLoad(dsl, "CNestTxRender");
        Assert.True(hot.Ok, $"[{dsl}] generate+compile failed: {hot.Error}");

        var map = new HsvPalette();
        var calc = (IFractalCalculator)Activator.CreateInstance(hot.CalculatorType!, W, H)!;
        calc.CenterX = Cx; calc.CenterY = Cy; calc.Zoom = Zoom; calc.MaxIterations = MaxIter;
        calc.GetType().GetProperty("ColorMap")?.SetValue(calc, map);
        calc.Calculate(default);

        uint inset = ((IColorMap)map).InSetColor;
        int escCount = 0;
        foreach (var px in calc.ColorBuffer) if (px != inset) escCount++;
        Assert.True(escCount > 0, $"[{dsl}] rendered blank (escaped {escCount})");
    }
}
