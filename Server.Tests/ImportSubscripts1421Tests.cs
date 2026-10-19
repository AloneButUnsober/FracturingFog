// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1421 — Paste LaTeX / Paste MathML accept the sequence notation fractal
// formulas are published in: z_n -> z, z_{n-1} -> prev, c_n -> c, MathML
// <msubsup>, and a recurrence left-hand side (z_{n+1} =, z \mapsto, f_c(z) =)
// that is dropped. What the DSL cannot express (z_{n+1} on the right, z_0,
// a second sequence such as w_n) fails with a message naming it.

using FracturingFog.CalculatorGen;
using FracturingFog.CalculatorGen.Parser;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ImportSubscripts1421Tests
{
    private static string Canon(string dsl) => AstPrinter.Print(EquationParser.Parse(dsl));

    private static string Math(string body) => $"<math xmlns=\"http://www.w3.org/1998/Math/MathML\">{body}</math>";

    // z_{n+1} =  as MathJax writes it.
    private const string MjxLhs =
        "<msub><mi>z</mi><mrow data-mjx-texclass=\"ORD\"><mi>n</mi><mo>+</mo><mn>1</mn></mrow></msub><mo>=</mo>";

    [Theory]
    [InlineData(@"z_{n+1} = z_n^2 + c", "z^2 + c")]
    [InlineData(@"z_{n+1}=z_{n}^{2}+c_{n}", "z^2 + c")]
    [InlineData(@"z_{k+1} = z_k^3 + c_0", "z^3 + c")]
    [InlineData(@"z_{n+1} = z_n^2 + c + 0.5 z_{n-1}", "z^2 + c + 0.5*prev")]
    [InlineData(@"z_n^2 + c_n", "z^2 + c")]
    [InlineData(@"z \mapsto z^2 + c", "z^2 + c")]
    [InlineData(@"z \to z^2 + c", "z^2 + c")]
    [InlineData(@"z := z^2 + c", "z^2 + c")]
    [InlineData(@"z = z^2 + c", "z^2 + c")]
    [InlineData(@"f_c(z) = z^2 + c", "z^2 + c")]
    [InlineData(@"f(z) = \sin(z) + c", "sin(z) + c")]
    [InlineData(@"z_{n+1} = \frac{z_n^2}{z_n + c_n}", "z^2/(z + c)")]
    public void Latex_sequence_notation_imports(string latex, string equivalentDsl)
    {
        var r = LatexImporter.Import(latex);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(Canon(equivalentDsl), Canon(r.Dsl));
    }

    [Theory]
    [InlineData(@"z_{n+1}^{2p}+c_n", "next iterate")]           // user example: z_{n+1} on the right
    [InlineData(@"z_{n+1} = z_n^2 + w_n^2 + c", "w_{n}")]       // a second sequence
    [InlineData(@"z_{n+1} = z_n^2 + z_0", "starting value")]
    [InlineData(@"z_{n+1} = z_n^p + c", "unknown identifier 'p'")]
    [InlineData(@"w \mapsto w^2 + c", "left-hand side")]
    public void Latex_inexpressible_parts_name_the_problem(string latex, string expected)
    {
        var r = LatexImporter.Import(latex);
        Assert.False(r.Ok);
        Assert.Contains(expected, r.Error);
        Assert.DoesNotContain("only z_{n-1}", r.Error);
    }

    [Fact]
    public void Latex_equality_comparison_is_not_a_recurrence()
    {
        // '=' after something that is not a left-hand side stays a comparison.
        var r = LatexImporter.Import(@"\begin{cases} z^2 + c & |z| = 1 \\ z & \text{otherwise} \end{cases}");
        Assert.DoesNotContain("left-hand side", r.Error ?? string.Empty);
        if (r.Ok) Assert.Contains("==", r.Dsl);
    }

    [Fact]
    public void Mathml_recurrence_with_msubsup_imports()
    {
        string m = Math(MjxLhs +
            "<msubsup><mi>z</mi><mi>n</mi><mn>2</mn></msubsup><mo>+</mo><msub><mi>c</mi><mi>n</mi></msub>");
        var r = MathmlImporter.Import(m);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(Canon("z^2 + c"), Canon(r.Dsl));
    }

    [Fact]
    public void Mathml_prev_and_wrapped_row()
    {
        string m = Math("<mrow>" + MjxLhs +
            "<msubsup><mi>z</mi><mi>n</mi><mn>2</mn></msubsup><mo>+</mo><mi>c</mi><mo>−</mo>" +
            "<msub><mi>z</mi><mrow><mi>n</mi><mo>−</mo><mn>1</mn></mrow></msub></mrow>");
        var r = MathmlImporter.Import(m);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(Canon("z^2 + c - prev"), Canon(r.Dsl));
    }

    [Fact]
    public void Mathml_function_lhs_and_mapsto()
    {
        var f = MathmlImporter.Import(Math(
            "<msub><mi>f</mi><mi>c</mi></msub><mo>⁡</mo><mo stretchy=\"false\">(</mo><mi>z</mi><mo stretchy=\"false\">)</mo>" +
            "<mo>=</mo><msup><mi>z</mi><mn>3</mn></msup><mo>+</mo><mi>c</mi>"));
        Assert.True(f.Ok, f.Error);
        Assert.Equal(Canon("z^3 + c"), Canon(f.Dsl));

        var arrow = MathmlImporter.Import(Math("<mi>z</mi><mo>↦</mo><msup><mi>z</mi><mn>2</mn></msup><mo>+</mo><mi>c</mi>"));
        Assert.True(arrow.Ok, arrow.Error);
        Assert.Equal(Canon("z^2 + c"), Canon(arrow.Dsl));
    }

    [Fact]
    public void Mathml_user_example_names_the_second_sequence()
    {
        // The pasted MathJax example: z_{n+1} = z_n^2 + w_n^2 + c.
        string m = Math(MjxLhs +
            "<msubsup><mi>z</mi><mi>n</mi><mn>2</mn></msubsup><mo>+</mo>" +
            "<msubsup><mi>w</mi><mi>n</mi><mn>2</mn></msubsup><mo>+</mo><mi>c</mi>");
        var r = MathmlImporter.Import(m);
        Assert.False(r.Ok);
        Assert.Contains("w_{n}", r.Error);
        Assert.Contains("single z", r.Error);
    }

    [Fact]
    public void Mathml_next_iterate_on_the_right_is_explained()
    {
        var r = MathmlImporter.Import(Math(
            "<msub><mi>z</mi><mrow><mi>n</mi><mo>+</mo><mn>1</mn></mrow></msub><mo>+</mo><mi>c</mi>"));
        Assert.False(r.Ok);
        Assert.Contains("next iterate", r.Error);
    }
}
