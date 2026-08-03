// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #27 Phase 1c — parity harness for folding UserEquation onto the safe DSL.
//
// Part A proves the DSL reproduces the exact System.Numerics.Complex math the
// old Roslyn path ran: for a corpus of equations, the translated DSL step
// (EquationPreprocessor → SandboxExpression) is compared against the identical
// C# expression evaluated natively over a (z, c, n) grid. The Roslyn path just
// runs that C# expression, so native evaluation IS its semantics — matching it
// is the correctness guarantee ("any and all fractal math").
//
// Part B is a render-level cross-check: UserEquationCalculator (now DSL-first)
// and the reference SandboxCalculator, fed the same expression, must produce
// identical pixel buffers — verifying the Phase 1b pixel-loop wiring (per-row
// env, step dispatch) matches the established interpreter loop.

using System;
using System.Numerics;
using FracturingFog;
using FracturingFog.CalculatorGen;
using FracturingFog.Models;
using FracturingFog.Security;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class UserEquationDslParityTests
{
    private const double Tol = 1e-10;

    // (label, C# source as the user would type it, native reference of the
    // same math). The reference is deliberately the plain C# expression.
    public static TheoryData<string, string, Func<Complex, Complex, int, Complex>> Corpus() => new()
    {
        { "z^2+c",        "return z*z + c;",                 (z, c, n) => z * z + c },
        { "z^3+c",        "return z*z*z + c;",               (z, c, n) => z * z * z + c },
        { "pow_int",      "return Complex.Pow(z, 4) + c;",   (z, c, n) => Complex.Pow(z, 4) + c },
        { "pow_frac",     "return Complex.Pow(z, 2.5) + c;", (z, c, n) => Complex.Pow(z, 2.5) + c },
        { "sin",          "return Complex.Sin(z) + c;",      (z, c, n) => Complex.Sin(z) + c },
        { "cos",          "return Complex.Cos(z) + c;",      (z, c, n) => Complex.Cos(z) + c },
        { "sinh",         "return Complex.Sinh(z) + c;",     (z, c, n) => Complex.Sinh(z) + c },
        { "cosh",         "return Complex.Cosh(z) + c;",     (z, c, n) => Complex.Cosh(z) + c },
        { "tanh",         "return Complex.Tanh(z) + c;",     (z, c, n) => Complex.Tanh(z) + c },
        { "exp",          "return Complex.Exp(z) + c;",      (z, c, n) => Complex.Exp(z) + c },
        { "log",          "return Complex.Log(z) + c;",      (z, c, n) => Complex.Log(z) + c },
        { "conj",         "return Complex.Conjugate(z) + c;",(z, c, n) => Complex.Conjugate(z) + c },
        { "julia_const",  "return z*z + new Complex(-0.4, 0.6);", (z, c, n) => z * z + new Complex(-0.4, 0.6) },
        { "sin_of_sq",    "return Complex.Sin(z*z) + c;",    (z, c, n) => Complex.Sin(z * z) + c },
        { "iter_scaled",  "return z*z + c*n;",               (z, c, n) => z * z + c * n },
        { "nested_pow",   "return Complex.Pow(Complex.Pow(z, 2), 3) + c;", (z, c, n) => Complex.Pow(Complex.Pow(z, 2), 3) + c },
        // #27 Phase 5a — member-access forms the preprocessor now translates.
        { "real",         "return z.Real + c;",              (z, c, n) => z.Real + c },
        { "imag",         "return z.Imaginary + c;",         (z, c, n) => z.Imaginary + c },
        { "magnitude",    "return z.Magnitude + c;",         (z, c, n) => z.Magnitude + c },
        { "phase",        "return z.Phase + c;",             (z, c, n) => z.Phase + c },
        { "paren_real",   "return (z*z).Real + c;",          (z, c, n) => (z * z).Real + c },
        { "call_real",    "return Complex.Sin(z).Real + c;", (z, c, n) => Complex.Sin(z).Real + c },
        { "real_imag_mix","return z.Real*z.Real - z.Imaginary*z.Imaginary + c;",
                          (z, c, n) => z.Real * z.Real - z.Imaginary * z.Imaginary + c },
        // #27 Phase 5a near-misses — Complex.Divide, bare Math statics (E / PI /
        // Pow via `using static System.Math`), and comments.
        { "divide",       "return Complex.Divide(z, 5) + c;", (z, c, n) => Complex.Divide(z, 5) + c },
        { "const_e",      "return z + E;",                    (z, c, n) => z + Math.E },
        { "const_pi",     "return z*PI + c;",                 (z, c, n) => z * Math.PI + c },
        { "bare_pow",     "return Pow(z, 3) + c;",            (z, c, n) => Complex.Pow(z, 3) + c },
        { "line_comment", "return z*z + c; // classic",       (z, c, n) => z * z + c },
        { "block_comment","return z*z /* squared */ + c;",    (z, c, n) => z * z + c },
        // #27 Phase 5a — negative / non-integer powers now translate to pow()
        // (Complex.Pow-exact) rather than 1/x^n or exp(y·log x).
        { "neg_pow_int",  "return z * Complex.Pow(z, -3) + c;", (z, c, n) => z * Complex.Pow(z, -3) + c },
        { "neg_pow_c",    "return z + c * Complex.Pow(c, -2);", (z, c, n) => z + c * Complex.Pow(c, -2) },
    };

    // z samples avoid the origin and the negative-real axis so log's branch
    // cut (identical in both engines, but numerically touchy exactly on it)
    // never coincides with a sample.
    private static readonly Complex[] ZSamples =
    {
        new(0.5, 0.3), new(1.2, -0.7), new(0.1, 1.1),
        new(2.0, 1.5), new(0.8, 0.9),  new(1.5, -1.3),
    };
    private static readonly Complex[] CSamples =
    {
        new(-0.5, 0.0), new(0.285, 0.01), new(-0.8, 0.156),
    };
    private static readonly int[] NSamples = { 0, 3, 7 };

    [Theory]
    [MemberData(nameof(Corpus))]
    public void DslStep_MatchesNativeCSharp(string label, string csharp, Func<Complex, Complex, int, Complex> reference)
    {
        // Same translation the calculator performs on Compile.
        string dsl = EquationPreprocessor.Preprocess(csharp, out PreprocessDiagnostic? diag);
        Assert.True(diag == null, $"[{label}] unexpected translation diagnostic: {diag?.Message}");
        var expr = SandboxExpression.Parse(dsl);
        var env = expr.NewEnv();

        foreach (var z in ZSamples)
        foreach (var c in CSamples)
        foreach (var n in NSamples)
        {
            Complex want = reference(z, c, n);
            Complex got = expr.EvalStep(z, c, n, env);
            double err = Complex.Abs(want - got);
            // Relative tolerance for large magnitudes (exp/sinh blow up fast).
            double scale = Math.Max(1.0, Complex.Abs(want));
            Assert.True(err <= Tol * scale,
                $"[{label}] z={z} c={c} n={n}: want {want}, got {got}, err {err}");
        }
    }

    // ── #27 Phase 5a near-misses — real saved equations that used to fail ────
    // Exact sources pulled from a real userequations.json. Each was left as C#
    // (Kind=UserEquation, erroring) before the near-miss fixes (Complex.Divide,
    // bare Math statics E/PI/Pow, `//` `/* */` comments, trailing `;`). They must
    // now translate + parse on the safe interpreter so the migration converts them.
    [Theory]
    [InlineData("return Complex.Divide(z,5)*z + Complex.Log(c) + c;")]
    [InlineData("return Complex.Sin(z * Pow(n,2)) + Complex.Pow(E,Complex.Pow(c,2));")]
    [InlineData("return Complex.Sin(z * Pow(n,1)) + Complex.Sqrt(Complex.Pow(E,Complex.Pow(c,4)));")]
    [InlineData("return \nComplex.Pow(Complex.Pow(Complex.Sin(z),2 + Sin(Pow(n,2.55))) ,2)\n//* \n//Complex.Pow(Complex.Sin(z),2 + Cos(Pow(n,2.5))) \n+ \nComplex.Pow(c,-2);")]
    [InlineData("return \nComplex.Pow(Complex.Sin(z * 2),2 * Sin(Pow(n,5))) \n//* \n//Complex.Pow(Complex.Sin(z),2.1 + Cos(n)) \n+ \nComplex.Pow(c,-2);")]
    public void RealSavedEquation_NowTranslatesAndParses(string csharp)
    {
        string dsl = EquationPreprocessor.Preprocess(csharp, out PreprocessDiagnostic? diag);
        Assert.True(diag == null, $"unexpected translation diagnostic: {diag?.Message}\nsource: {csharp}");

        var expr = SandboxExpression.Parse(dsl); // must not throw
        var env = expr.NewEnv();
        // Evaluating must not throw (values may be non-finite for these maps).
        expr.EvalStep(new Complex(0.3, 0.2), new Complex(-0.5, 0.1), 5, env);
    }

    // #27 Phase 5a — the z=0 seed is exactly where the old 1/x^n / exp(y·log x)
    // translations produced NaN (blank render) while Complex.Pow returns a finite
    // value. pow() must match Complex.Pow's zero guards.
    [Theory]
    [InlineData("return z * Complex.Pow(z, -3) + c;")]  // z*Pow(0,-3)+c = 0*0+c = c
    [InlineData("return Complex.Pow(Complex.Sin(z), n) + c;")] // Pow(sin(0),0)=Pow(0,0)=1
    public void Power_AtZeroSeed_IsFinite_MatchingComplexPow(string csharp)
    {
        string dsl = EquationPreprocessor.Preprocess(csharp, out PreprocessDiagnostic? diag);
        Assert.True(diag == null, diag?.Message);
        Assert.Contains("pow(", dsl); // routed through Complex.Pow, not 1/x^n or exp/log
        var expr = SandboxExpression.Parse(dsl);
        var env = expr.NewEnv();
        Complex got = expr.EvalStep(Complex.Zero, new Complex(0.3, 0.1), 0, env);
        Assert.False(double.IsNaN(got.Real) || double.IsNaN(got.Imaginary),
            $"z=0 seed produced NaN (blank render): {got}");
    }

    // ── #27 Phase 5b — statement-block corpus ────────────────────────────────
    // Saved equations authored as a C# statement block (typed / var decls,
    // reassignment, an if-seed / if-return guard, interior `;`, a final return)
    // must translate (EquationPreprocessor pass-through) + parse (SandboxExpression
    // statement front-end) + evaluate to the SAME math as the native C# body.
    // Reference lambdas execute the identical statement semantics.
    public static TheoryData<string, string, Func<Complex, Complex, int, Complex>> StmtBlockCorpus() => new()
    {
        { "decl_pow",
          "Complex z2 = Complex.Pow(z, 2); return z2 + c;",
          (z, c, n) => Complex.Pow(z, 2) + c },

        { "var_decl",
          "var t = z*z + c; return t;",
          (z, c, n) => z * z + c },

        { "double_local",
          "double k = 2; return z*k + c;",
          (z, c, n) => z * 2 + c },

        { "reassign_chain",
          "Complex t = z*z; t = t + c; return t;",
          (z, c, n) => { var t = z * z; t = t + c; return t; } },

        { "newton_z3",
          "Complex f = z*z*z - 1; Complex d = 3*z*z; return z - f/d;",
          (z, c, n) => { var f = z * z * z - 1; var d = 3 * z * z; return z - f / d; } },

        { "nova_z3",
          "Complex f = Complex.Pow(z,3) - 1; Complex fp = 3*Complex.Pow(z,2); return z - f/fp + c;",
          (z, c, n) => { var f = Complex.Pow(z, 3) - 1; var fp = 3 * Complex.Pow(z, 2); return z - f / fp + c; } },

        { "tricorn_decl",
          "Complex w = Complex.Conjugate(z); return w*w + c;",
          (z, c, n) => { var w = Complex.Conjugate(z); return w * w + c; } },

        { "if_seed",
          "if (n == 0) z = c; return z*z + c;",
          (z, c, n) => { var zz = (n == 0) ? c : z; return zz * zz + c; } },

        { "if_return_guard",
          "if (n == 0) return c; return z*z + c;",
          (z, c, n) => (n == 0) ? c : z * z + c },

        { "burning_ship",
          "Complex zf = new Complex(Math.Abs(z.Real), Math.Abs(z.Imaginary)); return zf*zf + c;",
          (z, c, n) => { var zf = new Complex(Math.Abs(z.Real), Math.Abs(z.Imaginary)); return zf * zf + c; } },

        { "comment_in_block",
          "Complex t = z*z; // square\n return t + c;",
          (z, c, n) => z * z + c },
    };

    [Theory]
    [MemberData(nameof(StmtBlockCorpus))]
    public void StmtBlockDslStep_MatchesNativeCSharp(string label, string csharp, Func<Complex, Complex, int, Complex> reference)
    {
        string dsl = EquationPreprocessor.Preprocess(csharp, out PreprocessDiagnostic? diag);
        Assert.True(diag == null, $"[{label}] unexpected translation diagnostic: {diag?.Message}");
        var expr = SandboxExpression.Parse(dsl);
        var env = expr.NewEnv();

        foreach (var z in ZSamples)
        foreach (var c in CSamples)
        foreach (var n in NSamples)
        {
            Complex want = reference(z, c, n);
            Complex got = expr.EvalStep(z, c, n, env);
            double err = Complex.Abs(want - got);
            double scale = Math.Max(1.0, Complex.Abs(want));
            Assert.True(err <= Tol * scale,
                $"[{label}] z={z} c={c} n={n}: want {want}, got {got}, err {err}");
        }
    }

    // ── Part B — render parity: UserEquation(DSL) ≡ SandboxCalculator ────────

    [Theory]
    [InlineData("z*z + c")]
    [InlineData("z*z*z + c")]
    [InlineData("sin(z) + c")]
    public void Render_UserEquationDsl_MatchesSandboxCalculator(string dslSource)
    {
        const int W = 64, H = 48;

        var sandbox = new SandboxCalculator(W, H)
        {
            CenterX = -0.5, CenterY = 0.0, Zoom = 1.0, MaxIterations = 120,
            FractalParameters = new FractalParameters { SandboxSource = dslSource },
        };
        sandbox.Calculate();

        var userEq = new UserEquationCalculator(W, H)
        {
            CenterX = -0.5, CenterY = 0.0, Zoom = 1.0, MaxIterations = 120,
            FractalParameters = new FractalParameters
            {
                // Bare DSL passes through the preprocessor unchanged, so the
                // UserEquation path parses the very same expression.
                UserEquationSource = dslSource,
                UserCodeOrigin = UserCodeOrigin.Interactive,
            },
        };
        userEq.Calculate();

        Assert.True(userEq.UsingDsl, "UserEquation should run the DSL for a translatable source");

        // Same interpreter, same loop constants (bailout, smooth, normals) and
        // no rotation / QD-DD by default → the pixel buffers match exactly.
        Assert.Equal(sandbox.ColorBuffer.Length, userEq.ColorBuffer.Length);
        Assert.Equal(sandbox.ColorBuffer, userEq.ColorBuffer);

        // Sanity: the render is a real fractal (both in-set and escaped pixels),
        // not a degenerate all-background fill.
        var first = userEq.ColorBuffer[0];
        bool anyDifferent = false;
        foreach (var px in userEq.ColorBuffer)
            if (px != first) { anyDifferent = true; break; }
        Assert.True(anyDifferent, "expected a non-uniform image");
    }
}
