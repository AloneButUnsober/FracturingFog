// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1085 — the CalcGen DSL's `if <cond> then <a> else <b>` on the interpreter
// (SandboxExpression), plus `norm(x)` = |x|² in both grammars.
//
// Before: a DSL-tab equation using the CalcGen conditional failed to parse on the
// interpreter, so the headless poster / batch path (a plain
// UserEquationCalculator) had nothing to run and filled the frame with the
// in-set colour. Checked here against independent facts:
//   - the CalcGen form evaluates exactly like the hand-written ternary;
//   - a condition `abs(x)` means |x|² (CalcGen's meaning) — at |z| = 1.5 the
//     threshold 2 separates |z| (1.5) from |z|² (2.25);
//   - the DSL-tab equation renders the same pixels headless as its ternary twin.
// Engine-to-engine parity (interpreter vs CalcGen-compiled) for the same text is
// in CalcGenSandboxParityTests (if_abs_c / if_norm_c / if_re_c / norm_c).

using System;
using System.Linq;
using System.Numerics;

using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class EquationIfThenElse1085Tests
{
    private static Complex Eval(string src, Complex z, Complex c, int n = 3)
    {
        var e = SandboxExpression.Parse(src);
        return e.EvalStep(z, c, n, e.NewEnv());
    }

    private static readonly (Complex Z, Complex C)[] Grid =
        (from zr in new[] { -1.7, -0.4, 0.0, 0.3, 1.2 }
         from zi in new[] { -0.9, 0.0, 0.6 }
         from cr in new[] { -0.8, 0.25 }
         select (new Complex(zr, zi), new Complex(cr, 0.3))).ToArray();

    [Theory]
    [InlineData("if re(z) > 0 then z*z + c else conj(z)*conj(z) + c",       "re(z) > 0 ? z*z + c : conj(z)*conj(z) + c")]
    [InlineData("if (re(z) > 0) then z*z + c else conj(z)*conj(z) + c",     "re(z) > 0 ? z*z + c : conj(z)*conj(z) + c")]
    [InlineData("if (re(z)) > 0 then z*z + c else conj(z)*conj(z) + c",     "re(z) > 0 ? z*z + c : conj(z)*conj(z) + c")]
    [InlineData("if im(z) <= 0 then z*z + c else if re(z) < 0 then z*z*z + c else c", "im(z) <= 0 ? z*z + c : (re(z) < 0 ? z*z*z + c : c)")]
    [InlineData("z + (if re(z) > 0 then c else -c)",                        "z + (re(z) > 0 ? c : -c)")]
    [InlineData("if re(z) > 0 && im(z) > 0 then z*z + c else z + c",        "re(z) > 0 && im(z) > 0 ? z*z + c : z + c")]
    [InlineData("if re(z) > 0 then z*z + c else z + c;",                    "re(z) > 0 ? z*z + c : z + c")]
    public void CalcGenForm_EvaluatesLikeTheTernary(string calcGenForm, string ternary)
    {
        foreach (var (z, c) in Grid)
            Assert.Equal(Eval(ternary, z, c), Eval(calcGenForm, z, c));
    }

    [Fact]
    public void ConditionAbs_IsMagnitude_NormIsSquared_SinceTheMigration()
    {
        // #1088 retired the #1085 condition rule: abs is |x| everywhere now.
        var z = new Complex(1.5, 0);                                   // |z| = 1.5, |z|² = 2.25
        Assert.Equal(0.0, Eval("if abs(z) > 2 then 1 else 0", z, 0).Real);    // |z| > 2 is false
        Assert.Equal(1.0, Eval("if norm(z) > 2 then 1 else 0", z, 0).Real);   // |z|² > 2
        var legacy = SandboxExpression.ParseLegacy("if abs(z) > 2 then 1 else 0");   // version 1: |z|²
        Assert.Equal(1.0, legacy.EvalStep(z, 0, 3, legacy.NewEnv()).Real);
        Assert.Equal(0.0, Eval("abs(z) > 2 ? 1 : 0", z, 0).Real);              // ternary: |z| (unchanged)
        Assert.Equal(1.5, Eval("abs(z)", z, 0).Real);                          // expression: |z|
        Assert.Equal(2.25, Eval("norm(z)", z, 0).Real);
        Assert.Equal(3.25, Eval("norm(z)", new Complex(1.5, 1.0), 0).Real);    // 2.25 + 1
        // Only a comparison operand that IS abs(..) is rewritten; abs inside an
        // arithmetic operand keeps |x| (1.5 + 0 > 2 is false).
        Assert.Equal(0.0, Eval("if abs(z) + 0 > 2 then 1 else 0", z, 0).Real);
    }

    [Theory]
    [InlineData("if (n == 0) z = c; return z*z + c;")]
    [InlineData("if (re(z) > 1) return z; return z*z + c;")]
    public void CSharpStatementForms_StillParse(string src)
    {
        Assert.NotNull(SandboxExpression.Parse(src));
    }

    [Theory]
    [InlineData("if re(z) > 0 z*z + c else z", "then")]
    [InlineData("if re(z) > 0 then z*z + c", "else")]
    public void Malformed_ReportsTheMissingKeyword(string src, string keyword)
    {
        var ex = Assert.Throws<FormatException>(() => SandboxExpression.Parse(src));
        Assert.Contains(keyword, ex.Message);
    }

    [Fact]
    public void Norm_ParsesInCalcGen_AsExpressionAndConditionTerm()
    {
        Assert.NotNull(FracturingFog.CalculatorGen.Parser.EquationParser.Parse("z*z + norm(c)"));
        Assert.NotNull(FracturingFog.CalculatorGen.Parser.EquationParser.Parse("if norm(z) > 4 then z else z*z + c"));
    }

    // ── The bug: a DSL-tab `if` equation rendered blank headless ──────────

    private static uint[] HeadlessDslTab(string dsl)
    {
        var calc = new UserEquationCalculator(64, 48)
        {
            ColorMap = ColorPalette.BuiltIns[0], CenterX = -0.5, CenterY = 0, Zoom = 1.0, MaxIterations = 120,
            FractalParameters = new FractalParameters { UserEquationActiveTab = 1, UserEquationDslSource = dsl },
        };
        calc.Calculate(default);
        return (uint[])calc.ColorBuffer.Clone();
    }

    [Fact]
    public void DslTabIfEquation_RendersHeadless_LikeItsTernaryTwin()
    {
        const string ite = "if norm(c) > 0.5 then z*z*z + c else z*z + c";
        var frame = HeadlessDslTab(ite);
        Assert.True(frame.Distinct().Count() > 10, "DSL-tab if/then/else equation rendered blank headless");
        // Same pixels as the hand-written ternary.
        Assert.Equal(HeadlessDslTab("norm(c) > 0.5 ? z*z*z + c : z*z + c"), frame);
    }
}
