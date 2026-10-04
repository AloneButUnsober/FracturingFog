// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1087 (#937 Phase 2) — CalcGen compiles the unified equation language by
// lowering its AST (CalcGenLowering). Checked against independent oracles:
//   1. Every CalcGen-dialect equation in the repo (the compile-time built-ins,
//      CalcGen's own unit-test sources, the cookbook, the parity corpus) lowers
//      to the SAME CalcGen AST its old parser produced (records compare
//      structurally) — except the documented meaning fixes (mod, and
//      min/max/clamp/atan2 reading complex values by magnitude).
//   2. Parity — the definition of "same rendered result" for #937: on a
//      corpus covering every construct, the CalcGen-compiled calculator and the
//      live interpreter (SandboxCalculator) agree at shallow zoom on the in-set
//      mask (≥ 98 % of pixels) and on the exact iteration count of pixels both
//      engines escape (≥ 95 %). Same bailout radius on both. FP association
//      (AVX vs scalar, Pow by multiplication vs Math.Pow) only moves pixels on
//      the set boundary.
//   3. Refusals are explicit (value-dependent real/complex reads; size cap).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using FracturingFog.CalculatorGen;
using FracturingFog.CalculatorGen.Parser;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class CalcGenLoweringTests
{
    // ── 1. CalcGen dialect: same AST as CalcGen's own parser ──────────────

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FracturingFogCLD.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray());
    }

    public static IEnumerable<string> DialectCorpus()
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(RepoFile("Engine"), "*.cs", SearchOption.AllDirectories))
            foreach (Match m in Regex.Matches(File.ReadAllText(f), "GeneratedCalculator\\(\\s*\"([^\"]+)\""))
                set.Add(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(File.ReadAllText(RepoFile("CalculatorGen", "Parser", "CalculatorGenUnitTests.cs")),
                     "EquationParser\\.Parse\\(\"([^\"]+)\"\\)"))
            set.Add(m.Groups[1].Value);
        foreach (var e in EquationCookbook.Entries) set.Add(e.DslSource);
        foreach (var row in CalcGenSandboxParityTests.Corpus()) set.Add(row.Data.Item2);
        return set;
    }

    // Constructs whose CalcGen meaning changes to the interpreter's (documented
    // in the PR / spec): mod is centred per-component, and min / max / clamp /
    // atan2 read a complex value by its magnitude.
    private static bool HasMeaningFix(string src) =>
        Regex.IsMatch(src, @"\b(mod|min|max|clamp|atan2)\s*\(");

    [Fact]
    public void CalcGenDialect_LowersToTheSameAst_AsCalcGensOwnParser()
    {
        int same = 0, fixedMeaning = 0;
        var failures = new List<string>();
        foreach (var src in DialectCorpus())
        {
            AstNode old;
            try { old = EquationParser.Parse(src); } catch (FormatException) { continue; }   // negative unit-test inputs
            AstNode lowered;
            try { lowered = CalcGenLowering.ParseAndLower(src); }
            catch (FormatException ex) { failures.Add($"{src}: {ex.Message}"); continue; }
            if (old.Equals(lowered)) { same++; continue; }
            if (HasMeaningFix(src)) { fixedMeaning++; continue; }
            failures.Add($"{src}:\n  old {AstPrinter.Print(old)}\n  new {AstPrinter.Print(lowered)}");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
        Assert.True(same >= 40, $"only {same} identical lowerings (corpus too small?)");
    }

    [Theory]
    [InlineData("z*z + mod(z, 1.5) + c")]
    [InlineData("z*z + min(z, c) + c")]
    public void MeaningFixes_NowMatchTheInterpreter(string src)
    {
        // The interpreter's value, not CalcGen's old one: compiled vs live parity.
        AssertParity("fix_" + Math.Abs(src.GetHashCode()), src);
    }

    // ── 2. Parity on the whole language ───────────────────────────────────

    public static TheoryData<string, string> LanguageCorpus() => new()
    {
        { "let",           "let w = z*z in w + c" },
        { "let_chain",     "let a = z*z in let b = a + c in b" },
        { "stmt_block",    "var w = z*z; w = w + c; return w;" },
        { "if_seed",       "if (n == 0) z = c; return z*z + c;" },
        { "if_return",     "if (norm(c) > 4) return z; return z*z + c;" },
        { "ternary_c",     "re(c) < -0.75 ? z*z*z + c : z*z + c" },
        { "and_c",         "re(c) < 0 && im(c) > 0 ? z*z*z + c : z*z + c" },
        { "or_c",          "re(c) < -0.75 || im(c) > 0.5 ? z*z*z + c : z*z + c" },
        { "not_c",         "!(re(c) < 0) ? z*z*z + c : z*z + c" },
        { "if_and_c",      "if re(c) < 0 && abs(c) > 0.5 then z*z*z + c else z*z + c" },
        { "cmp_complex",   "c > 0.6 ? z*z*z + c : z*z + c" },
        { "abs_ternary",   "abs(c) > 0.6 ? z*z*z + c : z*z + c" },
        { "truthy",        "(re(c) > 0) ? z*z + c : z*z*z + c" },
        { "bool_value",    "z*z + c + (re(c) > 0) * 0.1" },
        { "pow_frac_zero", "z*z + c + (z^2.5 + z^(1+i)) * 0.0" },
        { "pow_int_neg",   "z*z + c + z^-2 * 0.0" },
        { "pow_c",         "z^2 + c" },
        { "trig_c",        "z*z + tan(c)*0.2 + sinh(c)*0.1 + tanh(c)*0.1 + c" },
        { "norm_c",        "z*z + norm(c) * 0.2 + c" },
        { "sqrt_c",        "z*z + sqrt(c) * 0.2 + c" },
        { "mod_c",         "z*z + mod(c, 0.5) + c" },
        { "minmax_c",      "z*z + min(c, 0.5) * 0.3 + max(re(c), -1) * 0.1 + c" },
        { "clamp_cpx_c",   "z*z + clamp(c, 0, 0.5) * 0.3 + c" },
        { "atan2_c",       "z*z + atan2(im(c), re(c)) * 0.05 + c" },
        { "iter",          "z*z + c + n*0.0005" },
        { "comment_semi",  "z*z + c; // classic" },
        { "neg_pow_today", "-z^2 + c" },
    };

    [Theory]
    [MemberData(nameof(LanguageCorpus))]
    public void CompiledAndLive_Agree(string label, string src) => AssertParity(label, src);

    private const int W = 96, H = 72, MaxIter = 200;

    // The generated calculator takes its AVX2 path when Width >= 4; a 3-px strip
    // runs the scalar emitter instead, so compound conditions are checked there
    // too. The strips sit where the conditions actually switch branch.
    [Theory]
    [InlineData("and_c",    "re(c) < 0 && im(c) > 0 ? z*z*z + c : z*z + c", -0.3)]
    [InlineData("or_c",     "re(c) < -0.75 || im(c) > 0.5 ? z*z*z + c : z*z + c", -0.3)]
    [InlineData("not_c",    "!(im(c) < 0) ? z*z*z + c : z*z + c", -0.3)]
    [InlineData("if_and_c", "if re(c) < 0 && abs(c) > 0.5 then z*z*z + c else z*z + c", -0.3)]
    [InlineData("cmp_cpx",  "c > 0.6 ? z*z*z + c : z*z + c", -0.3)]
    public void ScalarPath_CompiledAndLive_Agree(string label, string src, double centerX)
        => AssertParity("S" + label, src, w: 3, h: 160, centerX: centerX);

    private static void AssertParity(string label, string src, int w = W, int h = H, double centerX = -0.5)
    {
        var hot = CalculatorGenHotLoad.TryCompileAndLoad(src, "L" + label);
        Assert.True(hot.Ok, $"[{label}] generate+compile failed: {hot.Error}");
        var map1 = new HsvPalette();
        var gen = (IFractalCalculator)Activator.CreateInstance(hot.CalculatorType!, w, h)!;
        gen.CenterX = centerX; gen.CenterY = 0; gen.Zoom = 1.0; gen.MaxIterations = MaxIter;
        gen.GetType().GetProperty("ColorMap")?.SetValue(gen, map1);
        gen.Calculate(default);

        var map2 = new HsvPalette();
        var sbx = new SandboxCalculator(w, h)
        {
            CenterX = centerX, CenterY = 0, Zoom = 1.0, MaxIterations = MaxIter, ColorMap = map2,
            FractalParameters = new FractalParameters { SandboxSource = src, EscapeRadius = 512 },
        };
        sbx.Calculate(default);

        var genIt = (int[])gen.GetType().GetProperty("IterationBuffer")!.GetValue(gen)!;
        uint in1 = ((IColorMap)map1).InSetColor, in2 = ((IColorMap)map2).InSetColor;
        int n = w * h, maskMatch = 0, bothEsc = 0, iterMatch = 0, inSet = 0;
        for (int i = 0; i < n; i++)
        {
            bool gIn = gen.ColorBuffer[i] == in1, sIn = sbx.ColorBuffer[i] == in2;
            if (gIn == sIn) maskMatch++;
            if (sIn) inSet++;
            if (!gIn && !sIn) { bothEsc++; if (genIt[i] == sbx.IterationBuffer[i]) iterMatch++; }
        }
        double mask = (double)maskMatch / n, iters = bothEsc == 0 ? 1 : (double)iterMatch / bothEsc;
        Assert.True(inSet > 0 && inSet < n, $"[{label}] degenerate frame (in-set {inSet}/{n}) — choose another view");
        Assert.True(mask >= 0.98, $"[{label}] in-set mask match {mask:P2} < 98% ({src})");
        Assert.True(iters >= 0.95, $"[{label}] escape-iteration match {iters:P2} < 95% over {bothEsc} px ({src})");
    }

    // ── 3. Refusals, size cap, preview ────────────────────────────────────

    [Theory]
    [InlineData("log(re(c)) > 0 ? z*z + c : z")]                 // log of a real: real or complex by value
    [InlineData("min(sqrt(re(c)), 1) + z*z")]
    [InlineData("(re(c) > 0 ? 1 : c) > 0.5 ? z : c")]           // ?: mixing kinds, then compared
    public void ValueDependentKinds_AreRefused_WithAReason(string src)
    {
        var r = CalcGenLowering.Lower(EquationLanguage.Parse(src));
        Assert.False(r.Ok);
        Assert.Contains("real or complex depending on", r.Refusal);
        Assert.Contains("re(), im(), abs() or norm()", r.Refusal);
        Assert.True(CalcGenLowering.Lower(EquationLanguage.Parse(src.Replace("log(re(c))", "re(log(re(c)))")
            .Replace("sqrt(re(c))", "abs(sqrt(re(c)))").Replace("(re(c) > 0 ? 1 : c) > 0.5", "abs(re(c) > 0 ? 1 : c) > 0.5"))).Ok);
    }

    [Fact]
    public void Inlining_IsCapped_AndReported()
    {
        // Each binding doubles the expanded size: 2^13 terms.
        string src = "let a0 = z*z + c in " + string.Concat(Enumerable.Range(1, 12).Select(k => $"let a{k} = a{k - 1}*a{k - 1} in ")) + "a12";
        var r = CalcGenLowering.Lower(EquationLanguage.Parse(src));
        Assert.False(r.Ok);
        Assert.Contains($"limit {CalcGenLowering.MaxNodes}", r.Refusal);
        // The live interpreter still renders it.
        Assert.NotNull(SandboxExpression.Parse(src));

        var small = CalcGenLowering.Lower(EquationLanguage.Parse("let a = z*z in let b = a*a in b + c"));
        Assert.True(small.Ok);
        Assert.InRange(small.NodeCount, 5, 40);
    }

    [Fact]
    public void Preview_AcceptsTheFullLanguage_AndExplainsARefusal()
    {
        var ok = CalculatorGenApi.Preview("let w = z*z in re(c) < 0 && im(c) > 0 ? w*z + c : w + c");
        Assert.True(string.IsNullOrEmpty(ok.Error), ok.Error);
        var refused = CalculatorGenApi.Preview("log(re(c)) > 0 ? z*z + c : z");
        Assert.Contains("real or complex depending on", refused.Error);
    }
}
