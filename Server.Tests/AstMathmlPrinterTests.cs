// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #756 — presentation-MathML rendering of the DSL engine's interpreted equation.
//
// AstMathmlPrinter walks the SAME parsed AstNode tree as AstPrinter /
// AstLatexPrinter (via CalculatorGenApi.Preview), so the MathML shows the math
// exactly as the engine interpreted it. These guard well-formedness (must be
// valid XML in the MathML namespace) and the key node → element mappings.

using System.Xml.Linq;
using FracturingFog.CalculatorGen;
using FracturingFog.CalculatorGen.Parser;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class AstMathmlPrinterTests
{
    private const string Ns = "http://www.w3.org/1998/Math/MathML";

    private static string Mathml(string equation) => AstMathmlPrinter.Print(EquationParser.Parse(equation));

    [Theory]
    [InlineData("z*z + c")]
    [InlineData("z^2 + c")]
    [InlineData("1/z")]
    [InlineData("conj(z)^2 + c")]
    [InlineData("abs(z) + c")]
    [InlineData("sin(z)/(z^2 + c) + c")]
    [InlineData("if abs(z) > 2 then z^2 + c else z*z - c")]   // '>' must be XML-escaped
    [InlineData("(z + c)^2 + c")]
    [InlineData("clamp(z, 0, 1) + c")]
    public void Output_is_well_formed_mathml(string equation)
    {
        var doc = XDocument.Parse(Mathml(equation));   // throws if not well-formed XML
        Assert.NotNull(doc.Root);
        Assert.Equal("math", doc.Root!.Name.LocalName);
        Assert.Equal(Ns, doc.Root.Name.NamespaceName);
    }

    [Fact]
    public void Power_uses_msup()
    {
        string x = Mathml("z^2 + c");
        Assert.Contains("<msup>", x);
        Assert.Contains("<mn>2</mn>", x);
    }

    [Fact]
    public void Division_uses_mfrac()
        => Assert.Contains("<mfrac>", Mathml("1/z"));

    [Fact]
    public void Conjugate_uses_mover()
        => Assert.Contains("<mover", Mathml("conj(z) + c"));

    [Fact]
    public void Comparison_operator_is_escaped_not_raw()
    {
        string x = Mathml("if abs(z) > 2 then z else c");
        Assert.Contains("&gt;", x);            // escaped in the serialized string
        Assert.DoesNotContain("<mo>></mo>", x); // never a raw, unescaped '>'
        Assert.NotNull(XDocument.Parse(x));     // and it parses
    }

    [Fact]
    public void Preview_api_exposes_mathml_text()
    {
        var p = CalculatorGenApi.Preview("z^2 + c");
        Assert.True(p.Ok);
        Assert.False(string.IsNullOrEmpty(p.MathmlText));
        Assert.NotNull(XDocument.Parse(p.MathmlText));
    }
}
