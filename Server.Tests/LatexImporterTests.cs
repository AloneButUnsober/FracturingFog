// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #765 — LaTeX → DSL import. Reverse of AstLatexPrinter (#754). The primary test
// is a round-trip over the exporter's own output:
//   dsl0 --Parse--> ast0 --AstLatexPrinter--> latex --Import--> dsl1 --Parse--> ast1
// ast0 and ast1 must print identically (compared post-parse).

using FracturingFog.CalculatorGen;
using FracturingFog.CalculatorGen.Parser;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class LatexImporterTests
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
        string latex = AstLatexPrinter.Print(EquationParser.Parse(dsl));
        var r = LatexImporter.Import(latex);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(Canon(dsl), Canon(r.Dsl));
    }

    [Theory]
    [InlineData(@"z^2 + c", "z^2 + c")]
    [InlineData(@"2z + c", "2*z + c")]                       // implicit multiplication
    [InlineData(@"\frac{z}{z+c}", "z/(z+c)")]
    [InlineData(@"z \cdot z + c", "z*z + c")]
    [InlineData(@"\sin(z) + c", "sin(z) + c")]
    [InlineData(@"e^{z} + c", "exp(z) + c")]
    [InlineData(@"\left|z\right| + c", "abs(z) + c")]
    public void Imports_handwritten_latex(string latex, string equivalentDsl)
    {
        var r = LatexImporter.Import(latex);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(Canon(equivalentDsl), Canon(r.Dsl));
    }

    [Fact]
    public void Rejects_unknown_identifier()
    {
        var r = LatexImporter.Import("x + c");
        Assert.False(r.Ok);
        Assert.Contains("unknown identifier", r.Error);
    }

    [Fact]
    public void Rejects_unsupported_command()
    {
        var r = LatexImporter.Import(@"\sum_{n} z");
        Assert.False(r.Ok);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public void Rejects_empty()
        => Assert.False(LatexImporter.Import("   ").Ok);
}
