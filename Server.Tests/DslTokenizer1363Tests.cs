// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.Controls;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// #1363 (S2 of #1360) — the editor's lenient tokenizer: golden classification
/// per dialect, the coverage invariant on any input (including garbage), and
/// the Bulb word list agreeing with the Bulb parser.
/// </summary>
public sealed class DslTokenizer1363Tests
{
    private static List<(DslTokenKind Kind, string Text)> Lex(string src, DslDialect d)
        => DslTokenizer.Tokenize(src, d).Select(t => (t.Kind, src.Substring(t.Start, t.Length))).ToList();

    [Fact]
    public void Complex2D_classifies_words()
    {
        var toks = Lex("let r = abs(z) in r*r + c + 2.5e-3 // done", DslDialect.Complex2D);
        Assert.Equal(new (DslTokenKind, string)[]
        {
            (DslTokenKind.Keyword, "let"), (DslTokenKind.Identifier, "r"), (DslTokenKind.Operator, "="),
            (DslTokenKind.Function, "abs"), (DslTokenKind.Bracket, "("), (DslTokenKind.Variable, "z"),
            (DslTokenKind.Bracket, ")"), (DslTokenKind.Keyword, "in"), (DslTokenKind.Identifier, "r"),
            (DslTokenKind.Operator, "*"), (DslTokenKind.Identifier, "r"), (DslTokenKind.Operator, "+"),
            (DslTokenKind.Variable, "c"), (DslTokenKind.Operator, "+"), (DslTokenKind.Number, "2.5e-3"),
            (DslTokenKind.Comment, "// done"),
        }, toks);
    }

    [Fact]
    public void Function_needs_a_call_and_matches_case_insensitively()
    {
        // The parsers lower-case call names, so Sin(z) is a call to sin.
        Assert.Equal(DslTokenKind.Function, Lex("Sin(z)", DslDialect.Complex2D)[0].Kind);
        Assert.Equal(DslTokenKind.Function, Lex("sin (z)", DslDialect.Complex2D)[0].Kind);
        // A bare function name with no '(' is just a name (e.g. a local called max).
        Assert.Equal(DslTokenKind.Identifier, Lex("max + 1", DslDialect.Complex2D)[0].Kind);
        // Bulb-only functions are not functions in 2D, and vice versa.
        Assert.Equal(DslTokenKind.Identifier, Lex("triplex(z)", DslDialect.Complex2D)[0].Kind);
        Assert.Equal(DslTokenKind.Function, Lex("triplex(z)", DslDialect.Bulb3D)[0].Kind);
        Assert.Equal(DslTokenKind.Identifier, Lex("conj(z)", DslDialect.Bulb3D)[0].Kind);
    }

    [Fact]
    public void Bulb3D_handles_csharp_style_forms()
    {
        var toks = Lex("return new Vec3(Math.Sqrt(z.X), Math.PI, v.y);", DslDialect.Bulb3D);
        Assert.Equal(new (DslTokenKind, string)[]
        {
            (DslTokenKind.Keyword, "return"), (DslTokenKind.Keyword, "new"), (DslTokenKind.Type, "Vec3"),
            (DslTokenKind.Bracket, "("), (DslTokenKind.Type, "Math"), (DslTokenKind.Operator, "."),
            (DslTokenKind.Function, "Sqrt"), (DslTokenKind.Bracket, "("), (DslTokenKind.Variable, "z"),
            (DslTokenKind.Operator, "."), (DslTokenKind.Member, "X"), (DslTokenKind.Bracket, ")"),
            (DslTokenKind.Operator, ","), (DslTokenKind.Type, "Math"), (DslTokenKind.Operator, "."),
            (DslTokenKind.Variable, "PI"), (DslTokenKind.Operator, ","), (DslTokenKind.Identifier, "v"),
            (DslTokenKind.Operator, "."), (DslTokenKind.Member, "y"), (DslTokenKind.Bracket, ")"),
            (DslTokenKind.Operator, ";"),
        }, toks);
    }

    [Fact]
    public void Broken_input_degrades_instead_of_throwing()
    {
        // Unterminated block comment runs to the end.
        var c = Lex("z*z /* half", DslDialect.Complex2D);
        Assert.Equal((DslTokenKind.Comment, "/* half"), c[^1]);
        // A character no dialect accepts is an Error token, and lexing continues.
        var e = Lex("z @ c", DslDialect.Bulb3D);
        Assert.Equal(new[] { DslTokenKind.Variable, DslTokenKind.Error, DslTokenKind.Variable }, e.Select(t => t.Kind));
        // `2e` is the number 2 then the constant e (no exponent digits).
        Assert.Equal(new[] { (DslTokenKind.Number, "2"), (DslTokenKind.Variable, "e") }, Lex("2e", DslDialect.Complex2D));
        // Two-char operators stay whole.
        Assert.Equal((DslTokenKind.Operator, "<="), Lex("z <= c", DslDialect.Complex2D)[1]);
    }

    [Theory]
    [InlineData(DslDialect.Complex2D)]
    [InlineData(DslDialect.Bulb3D)]
    public void Tokens_cover_every_non_space_char_exactly_once(DslDialect dialect)
    {
        // Invariant the highlighter relies on: tokens are in order, never
        // overlap, and every non-whitespace character is inside exactly one
        // token — for ANY text. Fuzz with the characters equations use plus
        // junk, and include the hand-picked edge cases above.
        var rng = new Random(1363);
        const string alphabet = "zcnxyXYZ_09.e+-*/^%<>=!?:,;&|()[]{}@#$\"'\\ \t\n\r/*abcdefghijklmnopqrstuvwxyzMVQ";
        var inputs = new List<string> { "", " ", "/*", "/", "//", "*/", "1e", "1e+", ".5", "5.", "a.b.c", "z.", "@@" };
        for (int k = 0; k < 2000; k++)
        {
            var sb = new StringBuilder();
            int len = rng.Next(0, 40);
            for (int j = 0; j < len; j++) sb.Append(alphabet[rng.Next(alphabet.Length)]);
            inputs.Add(sb.ToString());
        }

        foreach (string src in inputs)
        {
            var toks = DslTokenizer.Tokenize(src, dialect);
            var covered = new bool[src.Length];
            int prevEnd = 0;
            foreach (var t in toks)
            {
                Assert.True(t.Length > 0, $"empty token in {src}");
                Assert.True(t.Start >= prevEnd, $"overlap/out of order in {src}");
                for (int p = t.Start; p < t.End; p++) covered[p] = true;
                prevEnd = t.End;
            }
            for (int p = 0; p < src.Length; p++)
                Assert.True(covered[p] || char.IsWhiteSpace(src[p]), $"char {p} ({src[p]}) uncovered in {src}");
        }
    }

    [Fact]
    public void Bulb_function_list_matches_the_bulb_parser()
    {
        // Precondition: the probe can tell a missing function apart, otherwise
        // the loop below would pass vacuously.
        var miss = Assert.Throws<SbxParseException>(
            () => SandboxBulbExpression.Parse("nosuchfn(z)", Array.Empty<string>()));
        Assert.Contains("Unknown function", miss.Message);

        foreach (string f in BulbVocabulary.FunctionNames)
        {
            try
            {
                SandboxBulbExpression.Parse(f + "(z)", Array.Empty<string>());
            }
            catch (SbxParseException ex)
            {
                // Wrong arity is fine (the name is known); unknown is not.
                Assert.DoesNotContain("Unknown function", ex.Message);
            }
        }
        Assert.Same(BulbVocabulary.FunctionNames, SandboxBulbExpression.FunctionNames);
    }

    [Fact]
    public void Built_in_examples_have_no_error_tokens_or_unknown_calls()
    {
        // Real sources the app ships: Equation Cookbook (2D) and the User Bulb
        // seed presets (3D, internal consts read by reflection). In a source the
        // parser accepts, no character may lex as Error, and no name directly
        // followed by '(' may be a plain Identifier — that would mean a
        // function missing from the highlighter's list.
        var sources2D = FracturingFog.UI.Avalonia.ViewModels.EquationCookbook.Entries
            .Select(e => e.DslSource).ToList();
        var sourcesBulb = typeof(UserBulbStore)
            .GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public
                       | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name.StartsWith("Dsl", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
        Assert.NotEmpty(sources2D);
        Assert.True(sourcesBulb.Count >= 8, $"found {sourcesBulb.Count} bulb seeds");

        void Check(string src, DslDialect d)
        {
            var toks = DslTokenizer.Tokenize(src, d);
            for (int k = 0; k < toks.Count; k++)
            {
                Assert.True(toks[k].Kind != DslTokenKind.Error, $"Error token in {d} source: {src}");
                if (toks[k].Kind == DslTokenKind.Identifier && k + 1 < toks.Count
                    && toks[k + 1].Kind == DslTokenKind.Bracket && src[toks[k + 1].Start] == '(')
                    Assert.Fail($"'{src.Substring(toks[k].Start, toks[k].Length)}(' not a known function in {d}: {src}");
            }
        }
        foreach (var s in sources2D) Check(s, DslDialect.Complex2D);
        foreach (var s in sourcesBulb) Check(s, DslDialect.Bulb3D);
    }

    [Fact]
    public void Editor_language_maps_to_dialect()
    {
        Assert.Equal(DslDialect.Complex2D, DslCodeEditor.DialectOf(DslLanguage.Equation));
        Assert.Equal(DslDialect.Complex2D, DslCodeEditor.DialectOf(DslLanguage.Sandbox));
        Assert.Equal(DslDialect.Bulb3D, DslCodeEditor.DialectOf(DslLanguage.Bulb));
    }
}
