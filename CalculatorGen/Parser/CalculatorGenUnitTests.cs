// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// CalculatorGenUnitTests.cs
//
// Golden-output unit tests for the CalculatorGen AST pipeline. No
// xUnit/NUnit — runs as a static `Run()` method invoked from the host
// CLI via `--calcgen-test`, mirroring the other self-test entry points
// (--ubtest, --gentest, --legacycmp).
//
// Scope: parser, lexer diagnostics, differentiator, simplifier, SA
// detector. Covers the per-AST-rule contract so a regression introduced
// while adding Phase D-3 ops (sin/cos/exp/log) fails loudly rather than
// silently emitting a wrong calculator.

using System;
using System.Collections.Generic;
using System.Text;

namespace FracturingFog.CalculatorGen.Parser;

public static class CalculatorGenUnitTests
{
    /// <summary>Runs every test case. Returns true when all pass.
    /// <paramref name="report"/> contains a per-case PASS/FAIL line plus
    /// a final summary.</summary>
    public static bool Run(out string report)
    {
        var sb = new StringBuilder();
        int passed = 0, failed = 0;

        void Check(string name, Func<bool> body)
        {
            bool ok;
            string detail = "";
            try { ok = body(); }
            catch (Exception ex)
            {
                ok = false;
                detail = $" — threw {ex.GetType().Name}: {ex.Message}";
            }
            if (ok) { passed++; sb.AppendLine($"  PASS  {name}"); }
            else    { failed++; sb.AppendLine($"  FAIL  {name}{detail}"); }
        }

        // Helpers — round-trip through the simplifier so semantically
        // equivalent forms (e.g. 1*z vs z) compare equal.
        string PrintSimplified(string eq)
            => AstPrinter.Print(AstSimplifier.Simplify(EquationParser.Parse(eq)));

        string DpDzOf(string eq)
            => AstPrinter.Print(AstDifferentiator.DpDz(EquationParser.Parse(eq)));

        string DpDcOf(string eq)
            => AstPrinter.Print(AstDifferentiator.DpDc(EquationParser.Parse(eq)));

        // ── parser round-trip ────────────────────────────────────────
        Check("parser: z*z + c round-trips",
            () => PrintSimplified("z*z + c") == "z*z + c");
        Check("parser: z^3 + c round-trips",
            () => PrintSimplified("z^3 + c") == "z^3 + c");
        Check("parser: sqr(z) desugars to z*z",
            () => PrintSimplified("sqr(z) + c") == "z*z + c");
        Check("parser: precedence — 2*z^2 is (2)*(z^2)",
            () => PrintSimplified("2*z^2") == "2*z^2");
        Check("parser: negative literal",
            () => PrintSimplified("-1.5 + c") == "-1.5 + c");

        // ── lexer / parser diagnostics ───────────────────────────────
        Check("lexer: misspell suggests 'conj'",
            () => TryParseError("z + congj(c)", out var msg)
                  && msg.Contains("Did you mean 'conj'?"));
        Check("lexer: unknown ident emits col",
            () => TryParseError("z + xyzzy", out var msg) && msg.Contains("col "));
        Check("parser: trailing operator → unexpected end",
            () => TryParseError("z*z +", out var msg) && msg.Contains("end of input"));
        Check("parser: exponent > 64 rejected",
            () => TryParseError("z^100 + c", out var msg)
                  && msg.Contains("≤ 64"));

        // ── differentiator: ∂p/∂z ────────────────────────────────────
        Check("diff: ∂(z*z+c)/∂z = z + z",
            () => DpDzOf("z*z + c") == "z + z");
        Check("diff: ∂(z^3+c)/∂z = 3*z^2",
            () => DpDzOf("z^3 + c") == "3*z^2");
        Check("diff: ∂(z^2+c)/∂z = 2*z",
            () => DpDzOf("z^2 + c") == "2*z");
        Check("diff: ∂(c+c*z)/∂z = c",
            () => DpDzOf("c + c*z") == "c");

        // ── differentiator: ∂p/∂c ────────────────────────────────────
        Check("diff: ∂(z*z+c)/∂c = 1",
            () => DpDcOf("z*z + c") == "1");
        Check("diff: ∂(z^3+c)/∂c = 1",
            () => DpDcOf("z^3 + c") == "1");
        Check("diff: ∂(z*c)/∂c = z",
            () => DpDcOf("z*c") == "z");

        // ── simplifier: identity rules ───────────────────────────────
        Check("simplify: 0 + z → z",
            () => PrintSimplified("0 + z") == "z");
        Check("simplify: z + 0 → z",
            () => PrintSimplified("z + 0") == "z");
        Check("simplify: 1*z → z",
            () => PrintSimplified("1*z") == "z");
        Check("simplify: z*1 → z",
            () => PrintSimplified("z*1") == "z");
        Check("simplify: z*0 → 0",
            () => PrintSimplified("z*0") == "0");
        Check("simplify: z^1 → z",
            () => PrintSimplified("z^1") == "z");
        Check("simplify: z^0 → 1",
            () => PrintSimplified("z^0") == "1");

        // ── SA detector ──────────────────────────────────────────────
        Check("SA: z*z + c → degree 2",
            () => AstSaDetector.DetectZdPlusC(EquationParser.Parse("z*z + c")) == 2);
        Check("SA: z^3 + c → degree 3",
            () => AstSaDetector.DetectZdPlusC(EquationParser.Parse("z^3 + c")) == 3);
        Check("SA: z^4 + c → degree 4",
            () => AstSaDetector.DetectZdPlusC(EquationParser.Parse("z^4 + c")) == 4);
        Check("SA: z^5 + c → degree 5",
            () => AstSaDetector.DetectZdPlusC(EquationParser.Parse("z^5 + c")) == 5);
        Check("SA: c + z^2 (commuted) → degree 2",
            () => AstSaDetector.DetectZdPlusC(EquationParser.Parse("c + z^2")) == 2);
        Check("SA: sqr(z) + c → degree 2",
            () => AstSaDetector.DetectZdPlusC(EquationParser.Parse("sqr(z) + c")) == 2);
        Check("SA: z^6 + c → 0 (out of range)",
            () => AstSaDetector.DetectZdPlusC(EquationParser.Parse("z^6 + c")) == 0);
        Check("SA: z*z + 0.5*c → 0 (not pure z^d+c)",
            () => AstSaDetector.DetectZdPlusC(EquationParser.Parse("z*z + 0.5*c")) == 0);
        Check("SA: conj(z) + c → 0 (anti-holomorphic)",
            () => AstSaDetector.DetectZdPlusC(EquationParser.Parse("conj(z) + c")) == 0);

        // ── SA generic detector (item 10) ────────────────────────────
        Check("SA-generic: z*z + 0.1*z + c → degree 2",
            () => AstSaDetector.DetectPolyInZPlusC(
                    EquationParser.Parse("z*z + 0.1*z + c")).degree == 2);
        Check("SA-generic: 2*z^3 - z + c → degree 3",
            () => AstSaDetector.DetectPolyInZPlusC(
                    EquationParser.Parse("2*z^3 - z + c")).degree == 3);
        Check("SA-generic: z^6 + c → degree 6 (beyond fast-path)",
            () => AstSaDetector.DetectPolyInZPlusC(
                    EquationParser.Parse("z^6 + c")).degree == 6);
        Check("SA-generic: z^4 - z^2 + c → degree 4",
            () => AstSaDetector.DetectPolyInZPlusC(
                    EquationParser.Parse("z^4 - z^2 + c")).degree == 4);
        Check("SA-generic: z*c + c → null (c multiplicative)",
            () => AstSaDetector.DetectPolyInZPlusC(
                    EquationParser.Parse("z*c + c")).polyZ == null);
        Check("SA-generic: conj(z) + c → null (anti-holomorphic)",
            () => AstSaDetector.DetectPolyInZPlusC(
                    EquationParser.Parse("conj(z) + c")).polyZ == null);

        // ── transcendentals (item 14) ────────────────────────────────
        Check("parser: sin(z) + c round-trips",
            () => PrintSimplified("sin(z) + c") == "sin(z) + c");
        Check("parser: cos(z) + c round-trips",
            () => PrintSimplified("cos(z) + c") == "cos(z) + c");
        Check("parser: exp(z) + c round-trips",
            () => PrintSimplified("exp(z) + c") == "exp(z) + c");
        Check("parser: log(z) + c round-trips",
            () => PrintSimplified("log(z) + c") == "log(z) + c");
        Check("parser: exp(z*z) + c — nested arg",
            () => PrintSimplified("exp(z*z) + c") == "exp(z*z) + c");
        Check("diff: ∂(sin(z)+c)/∂z = cos(z)",
            () => DpDzOf("sin(z) + c") == "cos(z)");
        Check("diff: ∂(cos(z)+c)/∂z = -sin(z)",
            () => DpDzOf("cos(z) + c") == "-sin(z)");
        Check("diff: ∂(exp(z)+c)/∂z = exp(z)",
            () => DpDzOf("exp(z) + c") == "exp(z)");
        Check("diff: ∂(log(z)+c)/∂z = 1/z",
            () => DpDzOf("log(z) + c") == "1/z");
        Check("diff: ∂(exp(z*z)+c)/∂z = exp(z*z)*(z+z) — chain rule",
            () =>
            {
                string s = DpDzOf("exp(z*z) + c");
                return s == "exp(z*z)*(z + z)" || s == "(z + z)*exp(z*z)";
            });
        // ── inverse trig / hyperbolic: analytic-DE chain rules (#215) ──
        Check("diff: ∂(asin(z)+c)/∂z = 1/sqrt(1 - z*z)",
            () => DpDzOf("asin(z) + c") == "1/sqrt(1 - z*z)");
        Check("diff: ∂(acos(z)+c)/∂z = -(1/sqrt(1 - z*z))",
            () => DpDzOf("acos(z) + c") == "-(1/sqrt(1 - z*z))");
        Check("diff: ∂(atan(z)+c)/∂z = 1/(1 + z*z)",
            () => DpDzOf("atan(z) + c") == "1/(1 + z*z)");
        Check("diff: ∂(asinh(z)+c)/∂z = 1/sqrt(z*z + 1)",
            () => DpDzOf("asinh(z) + c") == "1/sqrt(z*z + 1)");
        Check("diff: ∂(acosh(z)+c)/∂z = 1/sqrt(z*z - 1)",
            () => DpDzOf("acosh(z) + c") == "1/sqrt(z*z - 1)");
        Check("diff: ∂(atanh(z)+c)/∂z = 1/(1 - z*z)",
            () => DpDzOf("atanh(z) + c") == "1/(1 - z*z)");
        Check("diff: ∂(asin(z*z)+c)/∂z = (z+z)/sqrt(1 - z*z*z*z) — chain rule",
            () => DpDzOf("asin(z*z) + c") == "(z + z)/sqrt(1 - z*z*z*z)");
        Check("diff: ∂(asin(z)+c)/∂c = 1 (operand real-diff to 0 in numerator)",
            () => DpDcOf("asin(z) + c") == "1");

        Check("SA: sin(z) + c → 0 (transcendental)",
            () => AstSaDetector.DetectZdPlusC(EquationParser.Parse("sin(z) + c")) == 0
               && AstSaDetector.DetectPolyInZPlusC(
                    EquationParser.Parse("sin(z) + c")).polyZ == null);
        Check("lexer: 'sine' suggests 'sin'",
            () => TryParseError("sine(z) + c", out var msg)
                  && msg.Contains("Did you mean 'sin'?"));

        // ── conditional / piecewise ───────────────────────────────────
        Check("parser: if re(z)>0 then z*z+c else z*z*z+c round-trips",
            () => PrintSimplified("if re(z) > 0 then z*z + c else z*z*z + c")
                == "if re(z) > 0 then z*z + c else z*z*z + c");
        Check("parser: if abs(z)>4 supports abs (squared mag)",
            () => PrintSimplified("if abs(z) > 4 then z else z*z + c")
                == "if abs(z) > 4 then z else z*z + c");
        Check("parser: if im(z) <= 0 then ... else",
            () => PrintSimplified("if im(z) <= 0 then z + c else z*z + c")
                == "if im(z) <= 0 then z + c else z*z + c");
        Check("parser: all six cmp ops accepted",
            () =>
            {
                foreach (var op in new[] { ">", "<", ">=", "<=", "==", "!=" })
                    EquationParser.Parse($"if re(z) {op} 0 then z else c");
                return true;
            });
        Check("diff: if-branches differentiate independently",
            () =>
            {
                var dz = DpDzOf("if re(z) > 0 then z*z + c else z*z*z + c");
                // ∂(z*z+c)/∂z = z+z; ∂(z*z*z+c)/∂z = (z+z)*z + z*z
                return dz.Contains("if re(z) > 0 then z + z else")
                    && dz.Contains("(z + z)*z + z*z");
            });
        Check("flags: If detected via Contains<If>",
            () => AstHelpers.Contains<If>(
                EquationParser.Parse("if re(z) > 0 then z*z + c else c")));
        Check("SA: if-branches → 0 (piecewise rejected)",
            () => AstSaDetector.DetectZdPlusC(
                EquationParser.Parse("if re(z) > 0 then z*z + c else z*z*z + c")) == 0
               && AstSaDetector.DetectPolyInZPlusC(
                    EquationParser.Parse("if re(z) > 0 then z*z + c else z*z*z + c")).polyZ == null);
        Check("lexer: '=' alone errors with '==' hint",
            () => TryParseError("if re(z) = 0 then z else c", out var msg)
                  && msg.Contains("'=='"));
        Check("lexer: '!' alone errors with '!=' hint",
            () => TryParseError("if re(z) ! 0 then z else c", out var msg)
                  && msg.Contains("'!='"));

        // ── Phoenix prev ──────────────────────────────────────────────
        Check("parser: z*z + c + 0.5*prev round-trips",
            () => PrintSimplified("z*z + c + 0.5*prev") == "z*z + c + 0.5*prev");
        Check("flags: PrevRef detected via Contains<PrevRef>",
            () => AstHelpers.Contains<PrevRef>(
                EquationParser.Parse("z*z + c + 0.3*prev")));
        Check("diff: prev opaque — ∂(z*z+0.5*prev)/∂z = z+z",
            () => DpDzOf("z*z + 0.5*prev + c") == "z + z");
        Check("SA: z*z + 0.5*prev + c → 0 (Phoenix rejected)",
            () => AstSaDetector.DetectZdPlusC(
                EquationParser.Parse("z*z + 0.5*prev + c")) == 0
               && AstSaDetector.DetectPolyInZPlusC(
                    EquationParser.Parse("z*z + 0.5*prev + c")).polyZ == null);
        Check("lexer: 'prv' suggests 'prev'",
            () => TryParseError("z*z + prv + c", out var msg)
                  && msg.Contains("Did you mean 'prev'?"));

        // ── EquationPreprocessor: C# Complex.* → DSL ──────────────────
        string Pre(string src) => EquationPreprocessor.Preprocess(src, out string? _);
        bool PreErr(string src, string contains)
        {
            EquationPreprocessor.Preprocess(src, out string? err);
            return err != null && err.Contains(contains);
        }

        Check("preproc: strips 'return' + trailing ';'",
            () => Pre("return z * z + c;") == "z * z + c");
        Check("preproc: Complex.Pow(z, 2) → z^2",
            () => Pre("Complex.Pow(z, 2)") == "(z)^2");
        // #27 Phase 5a: negative int exponent → pow() (Complex.Pow), NOT
        // 1/(z)^3 — the latter is NaN at z=0 where Complex.Pow(0,-3)=0.
        Check("preproc: Complex.Pow(z, -3) → pow(z, -3)",
            () => Pre("Complex.Pow(z, -3)") == "pow(z, -3)");
        Check("preproc: Complex.Pow(z, 1) collapses to z",
            () => Pre("Complex.Pow(z, 1)") == "(z)");
        Check("preproc: Complex.Pow(z, 0) → 1",
            () => Pre("Complex.Pow(z, 0)") == "1");
        Check("preproc: Complex.Sin(z) → sin(z)",
            () => Pre("Complex.Sin(z)") == "sin(z)");
        Check("preproc: Complex.Cos(z*z + c) → cos(z*z + c)",
            () => Pre("Complex.Cos(z*z + c)") == "cos(z*z + c)");
        Check("preproc: Complex.Exp + Complex.Log + Complex.Conjugate",
            () => Pre("Complex.Exp(z) + Complex.Log(c) + Complex.Conjugate(z)")
                == "exp(z) + log(c) + conj(z)");
        Check("preproc: Complex.Zero / Complex.One literals",
            () => Pre("z + Complex.Zero + Complex.One*c") == "z + 0 + 1*c");
        Check("preproc: nested Pow translates outer-first",
            () => Pre("Complex.Pow(Complex.Pow(z, 2), 3)") == "((z)^2)^3");
        Check("preproc: user's actual reported equation translates",
            () => Pre("return z * Complex.Pow(z,-3) + c * Complex.Pow(c,-2);")
                == "z * pow(z, -3) + c * pow(c, -2)");
        Check("preproc: Complex.Pow with non-int exponent → pow()",
            () => Pre("Complex.Pow(z, c)") == "pow(z, c)");

        // Reject paths
        // PR8: ImaginaryOne / new Complex are now first-class — rewrites to 'i'.
        Check("preproc: Complex.ImaginaryOne → i",
            () => Pre("z + Complex.ImaginaryOne") == "z + i");
        Check("preproc: new Complex(0, 1) → i",
            () => Pre("z + new Complex(0, 1)") == "z + i");
        Check("preproc: new Complex(0.1, 0.2) → ((0.1) + (0.2)*i)",
            () => Pre("z + new Complex(0.1, 0.2)") == "z + ((0.1) + (0.2)*i)");
        Check("preproc: new Complex(a, 0) drops to (a)",
            () => Pre("new Complex(c, 0) + z") == "(c) + z");
        Check("preproc: new Complex(0, b) → ((b)*i)",
            () => Pre("z + new Complex(0, c)") == "z + ((c)*i)");
        Check("preproc: rejects Complex.Abs",
            () => PreErr("Complex.Abs(z) + c", "abs(x)"));
        Check("preproc: rejects unknown Complex member",
            () => PreErr("Complex.Asin(z) + c", "Complex.Asin"));
        Check("preproc: Complex.Sinh → sinh (DSL widening)",
            () => Pre("Complex.Sinh(z)") == "sinh(z)");
        Check("preproc: Complex.Sqrt → sqrt (DSL widening)",
            () => Pre("Complex.Sqrt(z*z + c)") == "sqrt(z*z + c)");

        // ── DSL widening (tan, sinh, cosh, tanh, sqrt, pi, e) ─────────
        Check("parser: tan(z) parses and round-trips via desugar",
            () => EquationParser.Parse("tan(z)") is Div { Left: Sin, Right: Cos });
        Check("parser: sinh(z) desugars to (exp(z)-exp(-z))/2",
            () => EquationParser.Parse("sinh(z)") is Div
            { Left: Sub { Left: Exp, Right: Exp { Operand: Neg } }, Right: RealConst { Value: 2.0 } });
        Check("parser: cosh(z) desugars to (exp(z)+exp(-z))/2",
            () => EquationParser.Parse("cosh(z)") is Div
            { Left: Add { Left: Exp, Right: Exp { Operand: Neg } }, Right: RealConst { Value: 2.0 } });
        Check("parser: tanh(z) is Div(sinh, cosh)",
            () => EquationParser.Parse("tanh(z)") is Div
            { Left: Div { Left: Sub }, Right: Div { Left: Add } });
        Check("parser: sqrt(z) ≡ exp(0.5*log(z))",
            () => EquationParser.Parse("sqrt(z)") is Exp
            { Operand: Mul { Left: RealConst { Value: 0.5 }, Right: Log } });
        Check("parser: pi → RealConst(Math.PI)",
            () => EquationParser.Parse("pi") is RealConst r && Math.Abs(r.Value - Math.PI) < 1e-15);
        Check("parser: e → RealConst(Math.E)",
            () => EquationParser.Parse("e") is RealConst r2 && Math.Abs(r2.Value - Math.E) < 1e-15);
        Check("parser: pi inside expression composes correctly",
            () => EquationParser.Parse("z*z + pi*c") is Add { Right: Mul { Left: RealConst } });
        Check("parser: e^z (e is a constant, ^ requires integer) → throws on caret with e?",
            () =>
            {
                try { EquationParser.Parse("e + z*z + c"); return true; }
                catch { return false; }
            });
        Check("lexer: '1.5e-3' still lexes as a single number (e inside number lexer wins)",
            () => EquationParser.Parse("1.5e-3") is RealConst rc && Math.Abs(rc.Value - 0.0015) < 1e-9);
        Check("lexer: 'tnh' suggests 'tanh'",
            () => TryParseError("tnh(z) + c", out var msg)
                  && msg.Contains("Did you mean 'tanh'?"));

        // ── arg / atan2 ───────────────────────────────────────────────
        Check("parser: arg(z) parses as Arg node",
            () => EquationParser.Parse("arg(z)") is Arg);
        Check("parser: atan2(im(z), re(z)) requires comma",
            () => EquationParser.Parse("atan2(z, c)") is Atan2);
        Check("parser: atan2 without comma errors",
            () => TryParseError("atan2(z c)", out _));
        Check("flags: Arg detected via Contains<Arg>",
            () => AstHelpers.Contains<Arg>(EquationParser.Parse("z*z + arg(z) + c")));
        Check("flags: Atan2 detected via Contains<Atan2>",
            () => AstHelpers.Contains<Atan2>(EquationParser.Parse("z*z + atan2(z, c) + c")));
        Check("SA: arg → 0 (rejected, non-holomorphic)",
            () => AstSaDetector.DetectPolyInZPlusC(EquationParser.Parse("z*z + arg(z) + c")).polyZ == null);
        Check("diff: ∂(z*z + arg(z))/∂z = z+z (arg opaque)",
            () => DpDzOf("z*z + arg(z) + c") == "z + z");
        Check("preproc: Complex.Phase → arg",
            () => Pre("Complex.Phase(z)") == "arg(z)");
        Check("preproc: Math.Atan2(a, b) → atan2(a, b)",
            () => Pre("Math.Atan2(z, c)") == "atan2(z, c)");
        Check("lexer: 'arg' suggests itself on typo 'rg'",
            () => TryParseError("rg(z) + c", out var msg2)
                  && msg2.Contains("Did you mean 'arg'?"));

        // ── min / max / mod ──────────────────────────────────────────
        Check("parser: min(z, c) parses as Min node",
            () => EquationParser.Parse("min(z, c)") is Min);
        Check("parser: max(z, c) parses as Max node",
            () => EquationParser.Parse("max(z, c)") is Max);
        Check("parser: mod(z, c) parses as Mod node",
            () => EquationParser.Parse("mod(z, c)") is Mod);
        Check("flags: Min/Max/Mod detected via Contains<T>",
            () => AstHelpers.Contains<Min>(EquationParser.Parse("min(z, c) + c"))
               && AstHelpers.Contains<Max>(EquationParser.Parse("max(z, c) + c"))
               && AstHelpers.Contains<Mod>(EquationParser.Parse("mod(z, c) + c")));
        Check("SA: min(z, c) → 0 (rejected, non-holomorphic)",
            () => AstSaDetector.DetectPolyInZPlusC(EquationParser.Parse("z*z + min(z, c) + c")).polyZ == null);
        Check("diff: ∂(z*z + min(z, c))/∂z = z+z (min opaque)",
            () => DpDzOf("z*z + min(z, c) + c") == "z + z");
        Check("preproc: Math.Min → min",
            () => Pre("Math.Min(z, c)") == "min(z, c)");
        Check("preproc: Math.Max → max",
            () => Pre("Math.Max(z, c)") == "max(z, c)");
        Check("preproc: Math.IEEERemainder → mod",
            () => Pre("Math.IEEERemainder(z, c)") == "mod(z, c)");
        Check("parser: min without comma errors",
            () => TryParseError("min(z c)", out _));
        Check("preproc: leaves DSL syntax untouched",
            () => Pre("sin(z) + c") == "sin(z) + c");

        // ── IterRef (n / iter keyword) ────────────────────────────────
        Check("parser: 'n' lexes as iter keyword",
            () => PrintSimplified("z*z + c + 0.001*n") == "z*z + c + 0.001*n");
        Check("parser: 'iter' lexes as iter keyword",
            () => PrintSimplified("z*z + c + 0.001*iter") == "z*z + c + 0.001*n");
        Check("flags: IterRef detected",
            () => AstHelpers.Contains<IterRef>(
                EquationParser.Parse("z*z + c + 0.001*n")));
        Check("diff: ∂(z*z+0.001*n)/∂z = z+z (n opaque)",
            () => DpDzOf("z*z + 0.001*n + c") == "z + z");
        Check("SA: iter-dependent → 0 (rejected)",
            () => AstSaDetector.DetectZdPlusC(
                EquationParser.Parse("z*z + c + 0.001*n")) == 0);

        // ── ImagUnit ('i' literal) ───────────────────────────────────
        Check("parser: bare 'i' parses as ImagUnit",
            () => EquationParser.Parse("i") is ImagUnit);
        Check("parser: 'if' still parses as if-expression keyword (not ImagUnit)",
            () => EquationParser.Parse("if abs(z) > 4 then z else c") is If);
        Check("parser: 'iter' still lexes as iter (not ImagUnit)",
            () => EquationParser.Parse("iter") is IterRef);
        Check("printer: i round-trips",
            () => PrintSimplified("i*z + c") == "i*z + c");
        Check("printer: i*c round-trips",
            () => AstPrinter.Print(EquationParser.Parse("z + i*c")) == "z + i*c");
        Check("diff: ∂(i*z + c)/∂z = i",
            () => DpDzOf("i*z + c") == "i");
        Check("diff: ∂(z*z + i*c)/∂c = i",
            () => DpDcOf("z*z + i*c") == "i");
        Check("diff: ∂(i)/∂z = 0 (constant)",
            () => DpDzOf("i + z*z + c") == "z + z");
        Check("flags: ImagUnit detected via Contains<ImagUnit>",
            () => AstHelpers.Contains<ImagUnit>(EquationParser.Parse("z*z + i*c")));
        Check("flags: plain poly has no ImagUnit",
            () => !AstHelpers.Contains<ImagUnit>(EquationParser.Parse("z^4 + c")));
        Check("SA: i*z*z + c → degree 2 (i counts as degree-0 complex const)",
            () => AstSaDetector.DetectPolyInZPlusC(EquationParser.Parse("i*z*z + c")).degree == 2);
        Check("SA: z*z + i + c → polynomial (i is degree-0 const)",
            () => AstSaDetector.DetectPolyInZPlusC(EquationParser.Parse("z*z + i + c")).degree == 2);
        Check("simplify: i + 0 → i",
            () => PrintSimplified("i + 0") == "i");
        Check("simplify: 1*i → i",
            () => PrintSimplified("1*i") == "i");
        Check("simplify: 0*i → 0",
            () => PrintSimplified("0*i") == "0");
        Check("codegen: i*z + c emits without throwing",
            () =>
            {
                var r = CalculatorGenApi.Generate("i*z + c", "ImagTest");
                return r.Ok && r.Source.Contains("zr_new") && r.Source.Contains("zi_new");
            });
        Check("codegen: z*z + i emits without throwing",
            () =>
            {
                var r = CalculatorGenApi.Generate("z*z + i", "ImagSqTest");
                return r.Ok;
            });
        Check("lexer: 'I' (uppercase) also lexes as ImagUnit",
            () => EquationParser.Parse("I") is ImagUnit);

        // ── CondArg (arg inside if conditions) ───────────────────────
        Check("parser: if arg(z) > 0 then z*z + c else c parses",
            () => EquationParser.Parse("if arg(z) > 0 then z*z + c else c") is If
            { Cond: Cmp { Left: CondArg, Right: CondConst { Value: 0.0 } } });
        Check("parser: arg in cond accepts nested expression",
            () => EquationParser.Parse("if arg(z*z + c) >= 1.5 then z else c") is If
            { Cond: Cmp { Left: CondArg } });
        Check("printer: arg in cond round-trips",
            () => AstPrinter.Print(EquationParser.Parse("if arg(z) > 0 then z else c"))
                  == "if arg(z) > 0 then z else c");
        Check("codegen: if arg(z) > 0 emits without throwing",
            () =>
            {
                var r = CalculatorGenApi.Generate("if arg(z) > 0 then z*z + c else z*z - c", "CondArgSmoke");
                return r.Ok;
            });
        Check("codegen: if arg(z*z + c) < 1 routes through every emitter",
            () =>
            {
                var r = CalculatorGenApi.Generate("if arg(z*z + c) < 1 then z*z + c else z + c", "CondArgNestedSmoke");
                return r.Ok && r.Source.Contains("Math.Atan2");
            });
        Check("codegen: atan2(z, c) vectorises on AVX2 per-lane (no throw)",
            () =>
            {
                var r = CalculatorGenApi.Generate("z*z + atan2(z, c) + c", "Atan2VectorSmoke");
                // Per-lane atan2 prelude emits 4× Math.Atan2 calls plus
                // Vector256.Create assembly — both fingerprints prove the
                // AVX2 emitter took the per-lane path instead of throwing.
                return r.Ok
                    && r.Source.Contains("Math.Atan2")
                    && System.Text.RegularExpressions.Regex.Matches(r.Source, @"Math\.Atan2").Count >= 4;
            });

        // ── feature detection ────────────────────────────────────────
        Check("flags: conj(c) → hasConj true",
            () => AstHelpers.Contains<Conj>(EquationParser.Parse("z*z + conj(c)")));
        Check("flags: fold(c) → hasFolded true",
            () => AstHelpers.Contains<Folded>(EquationParser.Parse("fold(z) + c")));
        Check("flags: plain poly → no conj/fold",
            () => !AstHelpers.Contains<Conj>(EquationParser.Parse("z^4 + c"))
               && !AstHelpers.Contains<Folded>(EquationParser.Parse("z^4 + c")));

        // ── BuildDerivativeUpdate end-to-end ─────────────────────────
        Check("BuildDerivativeUpdate: z*z+c yields (z+z)*D + 1",
            () =>
            {
                var u = AstDifferentiator.BuildDerivativeUpdate(
                    EquationParser.Parse("z*z + c"));
                string s = AstPrinter.Print(u);
                // Either ordering of the (z+z)*D term is acceptable.
                return s == "(z + z)*D + 1" || s == "D*(z + z) + 1";
            });

        sb.AppendLine();
        sb.AppendLine($"Total: {passed + failed}  Passed: {passed}  Failed: {failed}");
        report = sb.ToString();
        return failed == 0;
    }

    // Captures the FormatException message thrown by the parser/lexer
    // and returns true when the parse failed (the expected outcome for
    // error-message tests).
    private static bool TryParseError(string source, out string message)
    {
        try
        {
            EquationParser.Parse(source);
            message = "(parse unexpectedly succeeded)";
            return false;
        }
        catch (FormatException ex)
        {
            message = ex.Message;
            return true;
        }
    }
}
