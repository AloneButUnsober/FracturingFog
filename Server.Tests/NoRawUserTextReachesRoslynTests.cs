// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #27 Phase 3c — audit: no user text reaches Roslyn except as a validated AST.
//
// After Phases 1–3, the two live user-code calculators (UserEquation, UserBulb)
// run on the safe interpreters only. The remaining runtime Roslyn compile sites
// are CalculatorGenHotLoad and ColorGenHotLoad, and both compile ONLY the source
// their generators emit — CalculatorGenApi.Generate / ColorGenApi.Generate parse
// the user text with a restricted-grammar parser (EquationParser / ColorGenParser)
// and, only on success, hand a machine-generated .cs string to Roslyn. The raw
// user text that does get embedded (an EQUATION_SOURCE token / a per-line comment
// block) is gated by that parse: a construct outside the DSL grammar — statements,
// member access, string/quote/brace/semicolon, a comment breakout — fails the
// parse, so generation returns Ok=false and Roslyn never runs.
//
// This test asserts that contract: a battery of injection-shaped inputs is
// rejected by both generators, while a benign DSL input still generates. If a
// payload ever slips through (Ok=true), that is a real hole and this test fails.

using CalcGen = FracturingFog.CalculatorGen.CalculatorGenApi;
using ColorGenApiT = global::FracturingFog.ColorGen.ColorGenApi;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class NoRawUserTextReachesRoslynTests
{
    // Each of these must be refused by the CalcGen parser (Ok=false), so the
    // string never reaches CSharpCompilation.
    public static TheoryData<string> CalcGenInjections() => new()
    {
        "\"; System.Environment.Exit(0)",
        "z*z + c; System.IO.File.Delete(\"x\")",
        "System.Diagnostics.Process.Start(\"calc\")",
        "z.GetType().Assembly",
        "z*z + c */ evil() /*",
        "}}} System.Console.Write(1)",
        "z + c\nclass Evil {}",
    };

    [Theory]
    [MemberData(nameof(CalcGenInjections))]
    public void CalcGen_RejectsInjection_BeforeRoslyn(string hostile)
    {
        var gen = CalcGen.Generate(hostile, "AuditInjection");
        Assert.False(gen.Ok, $"CalcGen accepted a hostile equation (would reach Roslyn): <{hostile}>");
    }

    [Fact]
    public void CalcGen_BenignEquation_GeneratesSource()
    {
        // Control: the generator is not merely rejecting everything — a valid
        // DSL equation produces machine-generated source (which is what Roslyn
        // then compiles).
        var gen = CalcGen.Generate("z*z + c", "AuditOk");
        Assert.True(gen.Ok, gen.Error);
        Assert.False(string.IsNullOrWhiteSpace(gen.Source));
    }

    public static TheoryData<string> ColorGenInjections() => new()
    {
        "return hsv(0,0,0); System.Environment.Exit(0);",
        "\"; }",
        "return File.ReadAllText(\"x\");",
        "return hsv(0,0,0); } class Evil { }",
        "return hsv(0,0,0); /*\n*/ System.Console.Write(1);",
    };

    [Theory]
    [MemberData(nameof(ColorGenInjections))]
    public void ColorGen_RejectsInjection_BeforeRoslyn(string hostile)
    {
        var gen = ColorGenApiT.Generate(hostile, "AuditInjection");
        Assert.False(gen.Ok, $"ColorGen accepted a hostile source (would reach Roslyn): <{hostile}>");
    }

    [Fact]
    public void ColorGen_BenignSource_GeneratesSource()
    {
        var gen = ColorGenApiT.Generate("return hsv(0.5, 1, 1);", "AuditOk");
        Assert.True(gen.Ok, gen.Error);
        Assert.False(string.IsNullOrWhiteSpace(gen.Source));
    }
}
