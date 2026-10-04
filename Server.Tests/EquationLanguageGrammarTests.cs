// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1086 (#937 Phase 1) — the unified equation language's grammar, pinned as
// parse trees. Each expected tree is written from the precedence / associativity
// table in Docs/Technical/Equation-Language.md, not copied from parser output.
// Slots: z c n prev iter are 0..4, so the first let / statement local is $5.

using System;
using System.Linq;

using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class EquationLanguageGrammarTests
{
    private static string Tree(string src) => EquationLanguage.ToSExpression(EquationLanguage.Parse(src));

    [Theory]
    // Arithmetic precedence + left associativity.
    [InlineData("z*z + c",                 "(+ (* z z) c)")]
    [InlineData("z - c - n",               "(- (- z c) n)")]
    [InlineData("z / c * n",               "(* (/ z c) n)")]
    [InlineData("2*z^3",                   "(* 2 (^ z 3))")]
    [InlineData("z + c > 2*n",             "(> (+ z c) (* 2 n))")]
    // ^ is right-associative over a full exponent; unary minus binds tighter than
    // ^ TODAY (both engines read -x^y as (-x)^y) — #1088 switches to -(x^y).
    [InlineData("z^2^3",                   "(^ z (^ 2 3))")]
    [InlineData("-z^2",                    "(^ (neg z) 2)")]
    [InlineData("z^-2",                    "(^ z (neg 2))")]
    [InlineData("-(z^2)",                  "(neg (^ z 2))")]
    [InlineData("+z",                      "z")]
    // Logic: ! > comparison > && > ||; ternary is right-associative.
    [InlineData("re(z) > 0 && im(z) < 0 || n == 3", "(|| (&& (> (re z) 0) (< (im z) 0)) (== n 3))")]
    [InlineData("!(re(z) > 0)",            "(! (> (re z) 0))")]
    [InlineData("re(z) > 0 ? z : im(z) > 0 ? c : n", "(? (> (re z) 0) z (? (> (im z) 0) c n))")]
    // CalcGen conditional = ternary; condition-operand abs means |x|² (#1085).
    [InlineData("if re(z) > 0 then z else c", "(? (> (re z) 0) z c)")]
    [InlineData("if abs(z) > 2 then z else c", "(? (> (norm z) 2) z c)")]
    [InlineData("if abs(z) + 0 > 2 then z else c", "(? (> (+ (abs z) 0) 2) z c)")]
    [InlineData("if re(z) > 0 then z else if im(z) > 0 then c else n", "(? (> (re z) 0) z (? (> (im z) 0) c n))")]
    // Bindings: let, C# declarations, reassignment shadowing, if-seed, if-return.
    [InlineData("let w = z*z in w + c",    "(let $5 (* z z) (+ $5 c))")]
    [InlineData("var w = z*z; return w + c;", "(let $5 (* z z) (+ $5 c))")]
    [InlineData("z = z*z; z + c",          "(let $5 (* z z) (+ $5 c))")]
    [InlineData("if (n == 0) z = c; return z*z + c;", "(let $5 (? (== n 0) c z) (+ (* $5 $5) c))")]
    [InlineData("if (re(z) > 1) return z; return z*z + c;", "(? (> (re z) 1) z (+ (* z z) c))")]
    // Atoms, constants, calls, comments.
    [InlineData("i",                       "(cx 0 1)")]
    [InlineData("PI",                      "3.141592653589793")]
    [InlineData("2.5e-1 + .5",             "(+ 0.25 0.5)")]
    [InlineData("pow(z, 2) + mod(z, 1)",   "(+ (pow z 2) (mod z 1))")]
    [InlineData("CLAMP(z, -1, 1)",         "(clamp z (neg 1) 1)")]
    [InlineData("prev + iter",             "(+ prev iter)")]
    [InlineData("/* x */ z // y",          "z")]
    [InlineData("z*z + c;",                "(+ (* z z) c)")]
    public void ParsesToTheSpecifiedTree(string source, string expected)
    {
        Assert.Equal(expected, Tree(source));
    }

    [Theory]
    [InlineData("z*z + sni(z)", "Unknown function 'sni'", "line 1, col 7", "Did you mean 'sin'?")]
    [InlineData("z*z + cc",     "Unknown identifier 'cc'", "line 1, col 7", "Did you mean 'c'?")]
    [InlineData("z*z\n  + prve", "Unknown identifier 'prve'", "line 2, col 5", "Did you mean 'prev'?")]
    [InlineData("pow(z)",       "takes 2 arg(s), got 1", "line 1, col 1", null)]
    [InlineData("z*z + c)",     "Unexpected ')'", "line 1, col 8", null)]
    [InlineData("if re(z) > 0 then z", "Expected 'else'", "line 1, col 20", null)]
    [InlineData("let w = z in w + qq", "Unknown identifier 'qq'", "line 1, col 18", null)]
    [InlineData("z < c < n",    "Unexpected '<'", "line 1, col 7", null)]
    public void Errors_NameTheProblem_AndItsLineAndColumn(string source, string what, string where, string? hint)
    {
        var ok = EquationLanguage.TryParse(source, out var expr, out string? error);
        Assert.False(ok);
        Assert.Null(expr);
        Assert.Contains(what, error);
        Assert.Contains(where, error);
        if (hint != null) Assert.Contains(hint, error);
    }

    [Fact]
    public void LocalNames_AreSuggestedToo()
    {
        Assert.False(EquationLanguage.TryParse("let radius = abs(z) in z*z + radus", out _, out string? error));
        Assert.Contains("Did you mean 'radius'?", error);
    }

    [Fact]
    public void FunctionTable_MatchesTheParser()
    {
        foreach (var f in SandboxExpression.FunctionNames)
        {
            int arity = SandboxExpression.FunctionArity(f);
            Assert.InRange(arity, 1, 3);
            string call = f + "(" + string.Join(", ", new string[arity].Select(_ => "z")) + ")";
            Assert.True(EquationLanguage.TryParse(call, out _, out string? e), $"{call}: {e}");
        }
        Assert.Equal(-1, SandboxExpression.FunctionArity("nope"));
    }
}
