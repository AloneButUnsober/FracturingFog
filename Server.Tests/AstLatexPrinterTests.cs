// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #754 — LaTeX rendering of the DSL engine's interpreted equation.
//
// AstLatexPrinter walks the SAME parsed AstNode tree the generator compiles
// (via CalculatorGenApi.Preview), so the LaTeX shows the math exactly as the
// engine interpreted it. These guard the node→LaTeX mappings and the
// portability contract (standard macros only, no custom commands).

using FracturingFog.CalculatorGen;
using FracturingFog.CalculatorGen.Parser;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class AstLatexPrinterTests
{
    private static string Latex(string equation) => AstLatexPrinter.Print(EquationParser.Parse(equation));

    [Theory]
    [InlineData("z*z + c", "z \\cdot z + c")]
    [InlineData("z^2 + c", "{z}^{2} + c")]
    [InlineData("z^3 + c", "{z}^{3} + c")]
    [InlineData("1/z", "\\frac{1}{z}")]
    [InlineData("conj(z) + c", "\\overline{z} + c")]
    [InlineData("abs(z)", "\\left|z\\right|")]
    // `sqrt` has no surface node — the parser desugars it to exp(0.5*log(z)).
    // The printer shows that INTERPRETED form, which is the whole point:
    // the user sees what the engine actually computes, not their raw text.
    [InlineData("sqrt(z) + c", "e^{0.5 \\cdot \\ln\\left(z\\right)} + c")]
    public void Renders_expected_latex(string equation, string expected)
        => Assert.Equal(expected, Latex(equation));

    [Fact]
    public void Sum_base_of_power_is_parenthesised()
    {
        // (z + c)^2 must bracket the base so the exponent binds correctly.
        string tex = Latex("(z + c)^2");
        Assert.Equal("{\\left(z + c\\right)}^{2}", tex);
    }

    [Fact]
    public void Preview_api_exposes_latex_text()
    {
        var p = CalculatorGenApi.Preview("z^2 + c");
        Assert.True(p.Ok);
        Assert.Equal("{z}^{2} + c", p.LatexText);
    }

    [Theory]
    [InlineData("z*z + c")]
    [InlineData("sin(z) + c")]
    [InlineData("z^2 + c")]
    [InlineData("conj(z)*conj(z) + c")]
    public void Latex_uses_only_portable_macros(string equation)
    {
        // No preamble/custom-macro tokens — output must paste into
        // Overleaf / KaTeX / MathJax / Word without a definitions block.
        string tex = Latex(equation);
        Assert.DoesNotContain("\\newcommand", tex);
        Assert.DoesNotContain("\\def", tex);
        Assert.DoesNotContain("\\usepackage", tex);
        Assert.False(string.IsNullOrWhiteSpace(tex));
    }
}
