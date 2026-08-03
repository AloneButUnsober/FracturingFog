// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #27 Phase 1a — coverage for the math functions added to the safe 2D DSL
// (SandboxExpression): asin acos atan asinh acosh atanh atan2 min max mod
// floor sign clamp. These close the gap versus Complex/Math so UserEquation
// can fold onto the DSL in 1b without losing expressive power.

using System;
using System.Numerics;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class SandboxExpressionMathTests
{
    private const double Tol = 1e-12;

    // Evaluate an expression with z bound to the given complex value (c = 0).
    private static Complex Eval(string expr, Complex z)
    {
        var e = SandboxExpression.Parse(expr);
        var env = e.NewEnv();
        return e.EvalStep(z, Complex.Zero, 0, env);
    }

    private static void AssertClose(Complex expected, Complex actual)
    {
        Assert.True(Complex.Abs(expected - actual) < Tol,
            $"expected {expected}, got {actual}");
    }

    // ── sqr (CalcGen-parity builtin) ────────────────────────────────────────

    [Fact] public void Sqr_Real()    => AssertClose(new Complex(9.0, 0.0), Eval("sqr(z)", 3.0));
    [Fact] public void Sqr_Complex() => AssertClose(new Complex(0.3, 0.7) * new Complex(0.3, 0.7), Eval("sqr(z)", new Complex(0.3, 0.7)));
    [Fact] public void Sqr_EqualsZTimesZ()
        => AssertClose(Eval("z*z", new Complex(1.2, -0.5)), Eval("sqr(z)", new Complex(1.2, -0.5)));
    [Fact] public void Sqr_CaseInsensitiveCall()
        => AssertClose(new Complex(9.0, 0.0), Eval("Sqr(z)", 3.0)); // ParseCall lowercases

    // ── fold + per-component builtins ───────────────────────────────────────

    [Fact] public void Fold_AbsBothComponents()
        => AssertClose(new Complex(1.2, 0.5), Eval("fold(z)", new Complex(-1.2, -0.5)));
    [Fact] public void Fract_PerComponent()
        => AssertClose(new Complex(0.25, 0.75), Eval("fract(z)", new Complex(1.25, -0.25)));
    [Fact] public void Round_PerComponent()
        => AssertClose(new Complex(2.0, -1.0), Eval("round(z)", new Complex(1.6, -0.6)));
    [Fact] public void Ceil_PerComponent()
        => AssertClose(new Complex(2.0, -1.0), Eval("ceil(z)", new Complex(1.1, -1.9)));
    [Fact] public void Trunc_PerComponent()
        => AssertClose(new Complex(1.0, -1.0), Eval("trunc(z)", new Complex(1.9, -1.9)));

    // ── inverse trig: real inside principal domain ──────────────────────────

    [Fact] public void Asin_RealDomain() => AssertClose(Math.Asin(0.5), Eval("asin(z)", 0.5));
    [Fact] public void Acos_RealDomain() => AssertClose(Math.Acos(0.5), Eval("acos(z)", 0.5));
    [Fact] public void Atan_Real()       => AssertClose(Math.Atan(2.0), Eval("atan(z)", 2.0));

    // ── inverse trig: complex continuation outside the real domain ──────────

    [Fact] public void Asin_OutOfDomain_GoesComplex()
        => AssertClose(Complex.Asin(new Complex(2.0, 0.0)), Eval("asin(z)", 2.0));

    [Fact] public void Acos_OutOfDomain_GoesComplex()
        => AssertClose(Complex.Acos(new Complex(2.0, 0.0)), Eval("acos(z)", 2.0));

    [Fact] public void Asin_ComplexInput()
        => AssertClose(Complex.Asin(new Complex(0.3, 0.7)), Eval("asin(z)", new Complex(0.3, 0.7)));

    // ── inverse hyperbolic ──────────────────────────────────────────────────

    [Fact] public void Asinh_Real() => AssertClose(Math.Asinh(1.3), Eval("asinh(z)", 1.3));
    [Fact] public void Acosh_Real() => AssertClose(Math.Acosh(2.0), Eval("acosh(z)", 2.0));
    [Fact] public void Atanh_Real() => AssertClose(Math.Atanh(0.5), Eval("atanh(z)", 0.5));

    [Fact] public void Acosh_BelowOne_GoesComplex()
    {
        // log(z + sqrt(z^2 - 1)) continuation
        var z = new Complex(0.5, 0.0);
        var expected = Complex.Log(z + Complex.Sqrt(z * z - Complex.One));
        AssertClose(expected, Eval("acosh(z)", 0.5));
    }

    [Fact] public void Atanh_AboveOne_GoesComplex()
    {
        var z = new Complex(2.0, 0.0);
        var expected = 0.5 * Complex.Log((Complex.One + z) / (Complex.One - z));
        AssertClose(expected, Eval("atanh(z)", 2.0));
    }

    [Fact] public void Asinh_ComplexInput()
    {
        var z = new Complex(0.4, 1.1);
        var expected = Complex.Log(z + Complex.Sqrt(z * z + Complex.One));
        AssertClose(expected, Eval("asinh(z)", z));
    }

    // Round-trip: sinh(asinh(z)) == z for a generic complex input.
    [Fact] public void Asinh_IsInverseOfSinh()
    {
        var z = new Complex(0.7, -0.9);
        AssertClose(z, Eval("sinh(asinh(z))", z));
    }

    // ── two-arg real functions ──────────────────────────────────────────────

    [Fact] public void Atan2_Quadrant() => AssertClose(Math.Atan2(1.0, 1.0), Eval("atan2(1, 1)", 0.0));

    // z arrives as a complex value (AsReal = magnitude), so signed comparisons
    // project with re() first — this is exactly how equations use min/max.
    [Fact] public void Min_PicksSmaller() => AssertClose(-3.0, Eval("min(re(z), 2)", -3.0));
    [Fact] public void Max_PicksLarger()  => AssertClose(2.0, Eval("max(re(z), 2)", -3.0));

    [Fact]
    public void Min_UsesMagnitudeForComplex()
    {
        // |3+4i| = 5, min(5, 2) = 2 — complex operands reduce to magnitude.
        AssertClose(2.0, Eval("min(z, 2)", new Complex(3.0, 4.0)));
    }

    // ── mod: centered modulo matching Vec3.Mod (x - p*floor(x/p + 0.5)) ──────

    [Fact]
    public void Mod_IsCentered()
    {
        double p = 3.0, x = 5.0;
        double expected = x - p * Math.Floor(x / p + 0.5); // = -1
        AssertClose(expected, Eval("mod(z, 3)", 5.0));
        Assert.Equal(-1.0, expected, 12);
    }

    [Fact]
    public void Mod_PerComponentForComplex()
    {
        var z = new Complex(5.0, 5.0);
        double p = 3.0;
        double m(double v) => v - p * Math.Floor(v / p + 0.5);
        AssertClose(new Complex(m(5.0), m(5.0)), Eval("mod(z, 3)", z));
    }

    // ── floor / sign: per-component ─────────────────────────────────────────

    [Fact] public void Floor_Real() => AssertClose(2.0, Eval("floor(z)", 2.7));

    [Fact]
    public void Floor_PerComponent()
        => AssertClose(new Complex(2.0, -4.0), Eval("floor(z)", new Complex(2.7, -3.2)));

    [Fact] public void Sign_Negative() => AssertClose(-1.0, Eval("sign(z)", -3.0));

    [Fact]
    public void Sign_PerComponent()
        => AssertClose(new Complex(1.0, -1.0), Eval("sign(z)", new Complex(4.0, -2.0)));

    // ── clamp ───────────────────────────────────────────────────────────────

    [Fact] public void Clamp_AboveHigh() => AssertClose(3.0, Eval("clamp(re(z), 0, 3)", 5.0));
    [Fact] public void Clamp_BelowLow()  => AssertClose(0.0, Eval("clamp(re(z), 0, 3)", -1.0));
    [Fact] public void Clamp_Inside()    => AssertClose(1.5, Eval("clamp(re(z), 0, 3)", 1.5));

    // ── arity enforcement (parser rejects wrong arg counts) ─────────────────

    [Fact]
    public void WrongArity_Throws()
    {
        Assert.ThrowsAny<Exception>(() => SandboxExpression.Parse("clamp(z, 0)"));
        Assert.ThrowsAny<Exception>(() => SandboxExpression.Parse("atan2(z)"));
        Assert.ThrowsAny<Exception>(() => SandboxExpression.Parse("asin(z, 1)"));
    }

    [Fact]
    public void UnknownFunction_Throws()
        => Assert.ThrowsAny<Exception>(() => SandboxExpression.Parse("frobnicate(z)"));
}
