// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Linq;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// #1367 (S6 of #1360) — the DSL editor's Format (whitespace-only, token- and
/// tree-guarded) and Minimise brackets (tree-guarded) actions.
/// </summary>
public sealed class DslFormatter1367Tests
{
    public DslFormatter1367Tests() => BulbDslCanonical.Register();

    [Theory]
    [InlineData("z*z+c", "z*z + c")]
    [InlineData("let r=abs( z )in r*r+c", "let r = abs(z) in r*r + c")]
    [InlineData("-z^2+ -c", "-z^2 + -c")]
    [InlineData("norm(z)>4?z:z*z+c", "norm(z) > 4 ? z : z*z + c")]
    [InlineData("if  norm(z)>4  then z else sin( z )*c", "if norm(z) > 4 then z else sin(z)*c")]
    [InlineData("pow(z , 3)+c*  2", "pow(z, 3) + c*2")]
    [InlineData("z*z + c // keep  me", "z*z + c // keep  me")]
    [InlineData("z /* a */+c", "z /* a */ + c")]
    [InlineData("let r = abs(z) in\nr*r + c", "let r = abs(z) in\n  r*r + c")]
    [InlineData("z*z\n\n\n\n+ c", "z*z\n\n  + c")]
    public void Format_2D_golden(string src, string expected)
    {
        var r = DslFormatter.Format(src, DslDialect.Complex2D);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(expected, r.Text);
    }

    [Theory]
    [InlineData("vec(z.x,z.y , z.z)+c", "vec(z.x, z.y, z.z) + c")]
    [InlineData("v = triplex(z,8); return v+c;", "v = triplex(z, 8);\nreturn v + c;")]
    [InlineData("spherefold(boxfold(z,1.0),0.5,1.0)*2.0+c", "spherefold(boxfold(z, 1.0), 0.5, 1.0)*2.0 + c")]
    [InlineData("vec(\nz.x,\nz.y,\nz.z)", "vec(\n  z.x,\n  z.y,\n  z.z)")]
    public void Format_Bulb_golden(string src, string expected)
    {
        var r = DslFormatter.Format(src, DslDialect.Bulb3D);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(expected, r.Text);
    }

    public static IEnumerable<object[]> Corpus()
    {
        foreach (var e in FracturingFog.UI.Avalonia.ViewModels.EquationCookbook.Entries)
            yield return new object[] { e.DslSource, DslDialect.Complex2D };
        foreach (var f in typeof(UserBulbStore).GetFields(System.Reflection.BindingFlags.NonPublic
                     | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                 .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name.StartsWith("Dsl", StringComparison.Ordinal)))
            yield return new object[] { (string)f.GetRawConstantValue()!, DslDialect.Bulb3D };
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Format_keeps_the_tree_and_is_idempotent_on_shipped_sources(string src, DslDialect d)
    {
        // Squash all spacing first, so Format has real work to do.
        string squashed = string.Concat(DslTokenizer.Tokenize(src, d)
            .Select(t => src.Substring(t.Start, t.Length))
            .Aggregate(new List<string>(), (acc, s) =>
            {
                // Keep a space between word-like tokens so they don't merge.
                if (acc.Count > 0 && char.IsLetterOrDigit(acc[^1][^1]) && char.IsLetterOrDigit(s[0])) acc.Add(" ");
                acc.Add(s);
                // A line comment runs to the end of the line: keep that line break.
                if (s.StartsWith("//", StringComparison.Ordinal)) acc.Add(Environment.NewLine);
                return acc;
            }));

        var once = DslFormatter.Format(squashed, d);
        Assert.True(once.Ok, $"{once.Message}: {squashed}");
        var twice = DslFormatter.Format(once.Text, d);
        Assert.Equal(once.Text, twice.Text);

        // Independent of the guard's own check: the tree is unchanged versus
        // the ORIGINAL shipped source.
        string? reference = DslCanonical.Of(src, d);
        Assert.NotNull(reference);
        Assert.Equal(reference, DslCanonical.Of(once.Text, d));
    }

    [Fact]
    public void Format_refuses_text_with_rejected_characters()
    {
        var r = DslFormatter.Format("z*z + c @", DslDialect.Complex2D);
        Assert.False(r.Ok);
        Assert.Equal("z*z + c @", r.Text);
    }

    [Theory]
    [InlineData("((z*z)) + (c)", "z*z + c")]
    [InlineData("-(z^2) + c", "-z^2 + c")]          // current rules: -z^2 is -(z^2)
    [InlineData("(-z)^2 + c", "(-z)^2 + c")]        // needed: kept
    [InlineData("(z + c)*2", "(z + c)*2")]          // needed: kept
    [InlineData("sin((z)) + ((c))", "sin(z) + c")]  // call brackets stay, inner group goes
    public void MinimiseParens_2D(string src, string expected)
    {
        var r = DslFormatter.MinimiseParens(src, DslDialect.Complex2D);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(expected, r.Text);
        Assert.Equal(DslCanonical.Of(src, DslDialect.Complex2D), DslCanonical.Of(r.Text, DslDialect.Complex2D));
    }

    [Fact]
    public void MinimiseParens_Bulb_uses_the_engine_tree()
    {
        Assert.NotNull(DslCanonical.BulbCanonicalizer);
        var r = DslFormatter.MinimiseParens("(triplex(z, 8)) + (c*2)", DslDialect.Bulb3D);
        Assert.True(r.Ok, r.Message);
        Assert.Equal("triplex(z, 8) + c*2", r.Text);
        // Lenient names: a param / chain output not known to the parser still works.
        var p = DslFormatter.MinimiseParens("(scale*z) + (c)", DslDialect.Bulb3D);
        Assert.Equal("scale*z + c", p.Text);
    }

    [Fact]
    public void MinimiseParens_needs_a_parsing_source()
    {
        var r = DslFormatter.MinimiseParens("((z*z) + ", DslDialect.Complex2D);
        Assert.False(r.Ok);
        Assert.Equal("((z*z) + ", r.Text);
    }
}
