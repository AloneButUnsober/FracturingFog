// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #764 — MathML → DSL import. The importer is the reverse of AstMathmlPrinter
// (#756); the strongest test is a round-trip over the exporter's own output:
//   dsl0 --Parse--> ast0 --AstMathmlPrinter--> mathml --Import--> dsl1 --Parse--> ast1
// ast0 and ast1 must print identically (compared post-parse, since the parser
// desugars e.g. sqrt/sqr consistently on both sides).

using FracturingFog.CalculatorGen;
using FracturingFog.CalculatorGen.Parser;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MathmlImporterTests
{
    private static string Canon(string dsl) => AstPrinter.Print(EquationParser.Parse(dsl));

    [Theory]
    [InlineData("z*z + c")]
    [InlineData("z^2 + c")]
    [InlineData("z^3 + c")]
    [InlineData("1/z")]
    [InlineData("z^2/(z + c) + c")]
    [InlineData("conj(z)*conj(z) + c")]
    [InlineData("abs(z) + c")]
    [InlineData("sin(z)/(z^2 + c) + c")]
    [InlineData("exp(z) + c")]
    [InlineData("(z + c)^2 + c")]
    [InlineData("min(z, c) + c")]
    [InlineData("clamp(z, 0, 1) + c")]
    [InlineData("if abs(z) > 2 then z^2 + c else z*z - c")]
    public void Roundtrips_through_exporter(string dsl)
    {
        string mathml = AstMathmlPrinter.Print(EquationParser.Parse(dsl));
        var r = MathmlImporter.Import(mathml);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(Canon(dsl), Canon(r.Dsl));
    }

    [Fact]
    public void Imports_external_style_mathml_with_named_entities_and_no_namespace()
    {
        // Word/LibreOffice-flavoured: no xmlns, InvisibleTimes, mfenced.
        const string mathml =
            "<math><mn>2</mn><mo>&InvisibleTimes;</mo><mi>z</mi><mo>+</mo><mi>c</mi></math>";
        var r = MathmlImporter.Import(mathml);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(Canon("2*z + c"), Canon(r.Dsl));
    }

    [Fact]
    public void Rejects_unknown_identifier_with_message()
    {
        var r = MathmlImporter.Import("<math><mi>x</mi><mo>+</mo><mi>c</mi></math>");
        Assert.False(r.Ok);
        Assert.Contains("unknown identifier", r.Error);
    }

    [Fact]
    public void Rejects_unsupported_element_with_message()
    {
        // A summation has no DSL analogue.
        var r = MathmlImporter.Import("<math><munderover><mo>∑</mo><mi>n</mi><mn>9</mn></munderover><mi>z</mi></math>");
        Assert.False(r.Ok);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public void Rejects_empty()
    {
        var r = MathmlImporter.Import("   ");
        Assert.False(r.Ok);
    }
}
