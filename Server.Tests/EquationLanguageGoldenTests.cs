// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1086 (#937 Phase 1) — the unified equation language must evaluate every
// existing equation exactly as the pre-refactor interpreter did. Independent
// oracle: GoldenFingerprint was captured by running THIS test against the
// unrefactored parser (main @ ec2d4af7, Engine/Models/SandboxExpression.cs)
// before any #1086 change, then frozen. It hashes, for every source in every
// corpus: whether it parses, and the exact bits of EvalStep (and, for holomorphic
// trees, EvalStepD) over a (z, c, n, prev) grid.
//
// #1088 changed two rules (-x^y now -(x^y); a condition abs(x) now |x|) and
// rewrites saved text so it keeps its meaning (EquationMigration). The SAME
// frozen fingerprint now proves that: every source is upgraded, then parsed
// with the current rules — it must still evaluate exactly as the original did
// under the old parser.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using FracturingFog.CalculatorGen;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class EquationLanguageGoldenTests
{
    // Captured from the pre-refactor parser; see the header.
    private const ulong GoldenFingerprint = 14060104663537180074UL;

    /// <summary>Grammar coverage the corpora below might not hit on their own.</summary>
    private static readonly string[] Coverage =
    {
        "z*z + c", "z^2 + c", "z^2^2 + c", "-z^2 + c", "z^-2 + c", "-(z^2) + c", "2*z^3 - z + c",
        "z^(1+i) + c", "z^2.5 + c", "pow(z, 3) + c", "pow(-2, 3) + z*c",
        "let w = z*z in w + c", "let w = z*z in let v = w*w in v + c",
        "re(z) > 0 ? z*z + c : conj(z)*conj(z) + c", "re(z) > 0 && im(z) < 0 || n == 3 ? z*z + c : z + c",
        "!(re(z) > 0) ? z : c", "abs(z) >= 1 ? z*z + c : z*z*z + c",
        "if re(z) > 0 then z*z + c else conj(z)*conj(z) + c", "if (re(z)) > 0 then z else c",
        "if abs(z) > 2 then z*z + c else z", "if norm(z) > 2 then z*z + c else z", "z + (if re(z) > 0 then c else -c)",
        "var w = z*z; return w + c;", "Complex w = z*z; w = w + c; return w;", "if (n == 0) z = c; return z*z + c;",
        "if (re(z) > 1) return z; return z*z + c;", "z*z + c; // comment", "/* block */ z*z + c",
        "z*z + prev*0.5 + c", "z*z + iter*0.01 + c", "z*z + n*0.01*i + c", "PI*z + E + c", "pi*z + e*i + c",
        "sin(z) + cos(z) + tan(z) + sinh(z) + cosh(z) + tanh(z) + c", "exp(z) + log(z) + sqrt(z) + sqr(z) + c",
        "asin(z) + acos(z) + atan(z) + asinh(z) + acosh(z) + atanh(z) + c",
        "floor(z) + round(z) + ceil(z) + trunc(z) + fract(z) + sign(z) + fold(z) + c",
        "abs(z) + norm(z) + conj(z) + re(z) + im(z) + arg(z) + c",
        "atan2(im(z), re(z)) + min(z, c) + max(z, c) + mod(z, 1.5) + clamp(z, -1, 1) + c",
        "z*z + 1.5e-3 + .25 + c", "(z - 1) / (z + 1) + c", "z*z + c / (z + 0.001)",
        // Expected failures (fingerprint records that they still fail).
        "z*z +", "foo(z)", "let = 3 in z", "if re(z) > 0 z else c", "z*z + c)", "sin()",
    };

    internal static IEnumerable<string> AllSources()
    {
        foreach (var s in Coverage) yield return s;
        foreach (var e in EquationCookbook.Entries)
        {
            yield return e.DslSource;
            if (!string.IsNullOrWhiteSpace(e.BailoutCondition)) yield return e.BailoutCondition!;
        }
        foreach (var row in CalcGenSandboxParityTests.Corpus()) yield return row.Data.Item2;
        foreach (var row in UserEquationDslParityTests.Corpus().Concat(UserEquationDslParityTests.StmtBlockCorpus()))
            yield return EquationPreprocessor.Preprocess(row.Data.Item2, out PreprocessDiagnostic? _);
    }

    private static readonly Complex[] Zs = { new(0, 0), new(0.3, -0.2), new(-1.4, 0.7), new(2.2, 1.9), new(-0.6, 0) };
    private static readonly Complex[] Cs = { new(-0.75, 0.1), new(0.28, 0.53) };
    private static readonly int[] Ns = { 0, 1, 7 };

    private static ulong Fingerprint(out int parsed, out int failed)
    {
        ulong h = 14695981039346656037UL;
        void Mix(ulong v) { h ^= v; h *= 1099511628211UL; }
        void MixD(double d) => Mix((ulong)BitConverter.DoubleToInt64Bits(d));
        parsed = failed = 0;
        foreach (var src in AllSources())
        {
            foreach (char ch in src) Mix(ch);
            SandboxExpression? e;
            string upgraded = EquationMigration.UpgradeFromVersion1(src).Source;   // #1088
            try { e = SandboxExpression.Parse(upgraded); parsed++; Mix(1); }
            catch (FormatException) { failed++; Mix(2); continue; }
            var env = e.NewEnv();
            foreach (var z in Zs) foreach (var c in Cs) foreach (var n in Ns)
            {
                Complex r;
                try { r = e.EvalStep(z, c, n, env, prev: z * 0.5); }
                catch (Exception ex) { Mix((ulong)ex.GetType().Name.Length); continue; }
                MixD(r.Real); MixD(r.Imaginary);
            }
            Mix(e.IsHolomorphic ? 3UL : 4UL);
            if (e.IsHolomorphic)
            {
                var denv = e.NewDualEnv();
                foreach (var z in Zs) foreach (var c in Cs)
                {
                    var (zz, dz) = e.EvalStepD(z, new Complex(0.5, 0.25), c, 2, denv, z * 0.5, Complex.One);
                    MixD(zz.Real); MixD(zz.Imaginary); MixD(dz.Real); MixD(dz.Imaginary);
                }
            }
        }
        return h;
    }

    [Fact]
    public void EveryCorpusEquation_EvaluatesExactlyAsBeforeTheRefactor()
    {
        ulong fp = Fingerprint(out int parsed, out int failed);
        Assert.True(parsed > 100, $"only {parsed} sources parsed ({failed} failed) — corpus too small");
        Assert.True(GoldenFingerprint == fp, $"fingerprint {fp}UL (parsed {parsed}, failed {failed}) != golden {GoldenFingerprint}UL");
    }
}
