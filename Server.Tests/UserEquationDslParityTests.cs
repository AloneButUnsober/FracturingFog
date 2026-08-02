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
