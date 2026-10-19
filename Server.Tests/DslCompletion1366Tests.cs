// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// #1366 (S5 of #1360) — DSL editor completion and signature help: the
/// signature tables agree with the parsers, and the caret logic offers the
/// right things.
/// </summary>
public sealed class DslCompletion1366Tests
{
    // "|" marks the caret.
    private static (string Text, int Caret) At(string withCaret)
    {
        int c = withCaret.IndexOf('|');
        return (withCaret.Remove(c, 1), c);
    }

    private static string[] Offered(string withCaret, DslDialect d, params string[] host)
    {
        var (t, c) = At(withCaret);
        return DslCompletion.Items(t, c, d, host).Select(i => i.Text).ToArray();
    }

    [Fact]
    public void Signature_table_2D_matches_the_parser_exactly()
    {
        var table = DslSignatures.For(DslDialect.Complex2D);
        Assert.Equal(SandboxExpression.FunctionNames.OrderBy(x => x), table.Keys.OrderBy(x => x));
        foreach (var s in table.Values)
            Assert.Equal(SandboxExpression.FunctionArity(s.Name), s.Arity);
    }

    [Fact]
    public void Signature_table_Bulb_matches_the_parser_exactly()
    {
        var table = DslSignatures.For(DslDialect.Bulb3D);
        Assert.Equal(BulbVocabulary.FunctionNames.OrderBy(x => x), table.Keys.OrderBy(x => x));

        static string Call(string f, int n) => f + "(" + string.Join(", ", Enumerable.Repeat("z", n)) + ")";
        static string? ParseError(string src)
        {
            try { SandboxBulbExpression.Parse(src, Array.Empty<string>()); return null; }
            catch (SbxParseException ex) { return ex.Message; }
        }
        foreach (var s in table.Values)
        {
            // Precondition: the wrong count is rejected as an arity error, so
            // "no arity error" at the documented count is meaningful.
            Assert.Contains("arg(s)", ParseError(Call(s.Name, s.Arity + 1)) ?? "");
            string? atArity = ParseError(Call(s.Name, s.Arity));
            Assert.True(atArity == null || !atArity.Contains("arg(s)"), $"{s.Display}: {atArity}");
        }
    }

    [Fact]
    public void Context_skips_comments_numbers_and_2D_members()
    {
        var (t, c) = At("z*z + si|");
        Assert.Equal("si", DslCompletion.ContextAt(t, c, DslDialect.Complex2D)!.Value.Prefix);

        (t, c) = At("z // si|");
        Assert.Null(DslCompletion.ContextAt(t, c, DslDialect.Complex2D));
        (t, c) = At("z /* si| */");
        Assert.Null(DslCompletion.ContextAt(t, c, DslDialect.Complex2D));
        (t, c) = At("z /* x */|");
        Assert.NotNull(DslCompletion.ContextAt(t, c, DslDialect.Complex2D));   // right after a closed comment
        (t, c) = At("z + 12|");
        Assert.Null(DslCompletion.ContextAt(t, c, DslDialect.Complex2D));

        (t, c) = At("z.|");
        Assert.Null(DslCompletion.ContextAt(t, c, DslDialect.Complex2D));
        Assert.True(DslCompletion.ContextAt(t, c, DslDialect.Bulb3D)!.Value.IsMember);
        (t, c) = At("2.|");
        Assert.Null(DslCompletion.ContextAt(t, c, DslDialect.Bulb3D));          // a number, not a member
    }

    [Fact]
    public void Items_filter_by_prefix_per_dialect()
    {
        var si = Offered("z + si|", DslDialect.Complex2D);
        Assert.Contains("sin", si);
        Assert.Contains("sinh", si);
        Assert.Contains("sign", si);
        Assert.DoesNotContain("sqrt", si);
        Assert.All(si, x => Assert.StartsWith("si", x, StringComparison.OrdinalIgnoreCase));

        Assert.Contains("triplex", Offered("tri|", DslDialect.Bulb3D));
        Assert.DoesNotContain("triplex", Offered("tri|", DslDialect.Complex2D));
        Assert.Contains("Vec3", Offered("Ve|", DslDialect.Bulb3D));
        Assert.Contains("prev", Offered("pr|", DslDialect.Complex2D));
        Assert.Contains("dr", Offered("d|", DslDialect.Bulb3D));
        // Case-insensitive, and an exact match stays offered (smoke: z, c, i
        // were missing because typing the whole name removed them).
        Assert.Contains("sin", Offered("SI|", DslDialect.Complex2D));
        Assert.Contains("sin", Offered("sin|", DslDialect.Complex2D));
        foreach (var v in new[] { "z", "c", "i", "n" })
            Assert.Contains(v, Offered(v + "|", DslDialect.Complex2D));
        foreach (var v in new[] { "z", "c", "t" })
            Assert.Contains(v, Offered(v + "|", DslDialect.Bulb3D));
    }

    [Fact]
    public void Items_include_source_locals_and_host_names_but_not_the_word_itself()
    {
        var offered = Offered("let radius = abs(z) in z*z + ra|", DslDialect.Complex2D);
        Assert.Contains("radius", offered);
        Assert.DoesNotContain("ra", offered);

        var host = Offered("vec(z.x, z.y, z.z) * sc|", DslDialect.Bulb3D, "scale", "s0", "offset");
        Assert.Contains("scale", host);
        Assert.DoesNotContain("offset", host);

        var members = Offered("z.|", DslDialect.Bulb3D);
        Assert.Equal(new[] { "x", "y", "z", "w" }, members);
    }

    [Theory]
    [InlineData("pow(z, |", "pow", 1)]
    [InlineData("pow(|", "pow", 0)]
    [InlineData("sin(pow(z, |", "pow", 1)]
    [InlineData("max(min(a, b), |", "max", 1)]
    [InlineData("clamp(z, (1 + 2), |", "clamp", 2)]
    public void SignatureAt_finds_the_innermost_call_and_argument(string src, string fn, int arg)
    {
        var (t, c) = At(src);
        var sig = DslCompletion.SignatureAt(t, c, DslDialect.Complex2D);
        Assert.NotNull(sig);
        Assert.Equal(fn, sig!.Value.Signature.Name);
        Assert.Equal(arg, sig.Value.ArgIndex);
    }

    [Theory]
    [InlineData("sin(z) + |")]       // call closed
    [InlineData("(z, |")]            // plain group
    [InlineData("foo(z, |")]         // not a built-in
    [InlineData("|")]
    public void SignatureAt_is_null_outside_built_in_calls(string src)
    {
        var (t, c) = At(src);
        Assert.Null(DslCompletion.SignatureAt(t, c, DslDialect.Complex2D));
    }

    [Fact]
    public void DocFor_describes_functions_and_inputs()
    {
        Assert.StartsWith("spherefold(v, rMin, rMax)", DslCompletion.DocFor("spherefold", DslDialect.Bulb3D));
        Assert.StartsWith("prev —", DslCompletion.DocFor("prev", DslDialect.Complex2D));
        Assert.Null(DslCompletion.DocFor("prev", DslDialect.Bulb3D));
        Assert.Null(DslCompletion.DocFor("radius", DslDialect.Complex2D));
    }

    [Fact]
    public void Text_input_filter_drops_ctrl_chords_and_control_chars()
    {
        // Smoke: Ctrl+- (U+001F) / Ctrl+] (U+001D) / Ctrl+Space typed into the
        // code view; the UI-scale Ctrl+= / Ctrl+- / Ctrl+0 keys must not edit text.
        Assert.True(FracturingFog.UI.Avalonia.Controls.DslCodeEditor.ShouldDropTextInput("\u001f", ctrlChord: false));
        Assert.True(FracturingFog.UI.Avalonia.Controls.DslCodeEditor.ShouldDropTextInput("\u001d", ctrlChord: false));
        Assert.True(FracturingFog.UI.Avalonia.Controls.DslCodeEditor.ShouldDropTextInput(" ", ctrlChord: true));
        Assert.True(FracturingFog.UI.Avalonia.Controls.DslCodeEditor.ShouldDropTextInput("=", ctrlChord: true));
        Assert.False(FracturingFog.UI.Avalonia.Controls.DslCodeEditor.ShouldDropTextInput("z", ctrlChord: false));
        Assert.False(FracturingFog.UI.Avalonia.Controls.DslCodeEditor.ShouldDropTextInput("\t", ctrlChord: false));
        Assert.False(FracturingFog.UI.Avalonia.Controls.DslCodeEditor.ShouldDropTextInput("\n", ctrlChord: false));
        Assert.False(FracturingFog.UI.Avalonia.Controls.DslCodeEditor.ShouldDropTextInput(null, ctrlChord: true));
    }
}
