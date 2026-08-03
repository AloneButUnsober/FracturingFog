// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #27 Phase 5b — the safe 2D DSL (SandboxExpression) accepts a statement-block
// front-end that desugars to the existing let/ternary AST:
//   TYPE? ident = expr ;          -> let ident = expr in <rest>
//   ident = expr ;   (reassign)   -> let ident = expr in <rest> (shadowing)
//   if (cond) ident = expr ;      -> let ident = (cond ? expr : ident) in <rest>
//   if (cond) return expr ;       -> cond ? expr : <rest-of-block>
//   return expr ; | bare expr     -> the block's value
// These cover the saved C# equations Phase 5a could not fold (they were
// statement blocks, not single expressions). No BCL, no loops, no braces — the
// same pure interpreter runs; assignment is let-binding, so evaluation stays
// terminating and side-effect free.

using System;
using System.Numerics;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class SandboxExpressionStmtBlockTests
{
    private const double Tol = 1e-12;

    private static Complex Eval(string src, Complex z, Complex c = default, int n = 0)
    {
        var e = SandboxExpression.Parse(src);
        return e.EvalStep(z, c, n, e.NewEnv());
    }

    private static void AssertClose(Complex expected, Complex actual)
        => Assert.True(Complex.Abs(expected - actual) < Tol, $"expected {expected}, got {actual}");

    // ── declarations ────────────────────────────────────────────────────────

    [Fact]
    public void TypedDecl_BindsAndReturns()
        => AssertClose(new Complex(4, 0), Eval("Complex t = z*z; return t;", new Complex(2, 0)));

    [Fact]
    public void VarDecl_Works()
        => AssertClose(new Complex(4, 0), Eval("var t = z*z; return t;", new Complex(2, 0)));

    [Fact]
    public void DoubleDecl_RealLocal()
        => AssertClose(new Complex(6, 0), Eval("double k = 3; return z*k;", new Complex(2, 0)));

    [Fact]
    public void MultipleDecls_ChainAsLets()
        // a = z*z = 4; b = a + z = 6; return b + z = 8.
        => AssertClose(new Complex(8, 0),
            Eval("Complex a = z*z; Complex b = a + z; return b + z;", new Complex(2, 0)));

    // ── reassignment (shadowing) ────────────────────────────────────────────

    [Fact]
    public void Reassignment_ShadowsPriorBinding()
    {
        // t = z*z; t = t + c; return t;  ->  z*z + c
        var got = Eval("Complex t = z*z; t = t + c; return t;", new Complex(2, 0), new Complex(1, 0));
        AssertClose(new Complex(5, 0), got); // 4 + 1
    }

    [Fact]
    public void ReassignZ_UsesInputOnRhs_ThenShadows()
    {
        // z = z*z + c; return z;  ->  RHS z is the INPUT z (slot0), body z is the new binding.
        var got = Eval("z = z*z + c; return z;", new Complex(2, 0), new Complex(1, 0));
        AssertClose(new Complex(5, 0), got);
    }

    // ── if-seed ─────────────────────────────────────────────────────────────

    [Fact]
    public void IfSeed_TakesThenBranch_WhenConditionTrue()
    {
        // if (n == 0) z = c; return z*z + c;   at n=0 -> z:=c -> c*c + c
        var got = Eval("if (n == 0) z = c; return z*z + c;", new Complex(9, 9), new Complex(2, 0), n: 0);
        AssertClose(new Complex(6, 0), got); // 2*2 + 2
    }

    [Fact]
    public void IfSeed_KeepsPriorValue_WhenConditionFalse()
    {
        // at n=5 -> z unchanged (input) -> z*z + c
        var got = Eval("if (n == 0) z = c; return z*z + c;", new Complex(2, 0), new Complex(1, 0), n: 5);
        AssertClose(new Complex(5, 0), got); // 4 + 1
    }

    // ── if-return guard ─────────────────────────────────────────────────────

    [Fact]
    public void IfReturn_EarlyReturnsThen_WhenTrue()
    {
        var got = Eval("if (n == 0) return c; return z*z + c;", new Complex(2, 0), new Complex(7, 0), n: 0);
        AssertClose(new Complex(7, 0), got); // returns c
    }

    [Fact]
    public void IfReturn_FallsThroughToRest_WhenFalse()
    {
        var got = Eval("if (n == 0) return c; return z*z + c;", new Complex(2, 0), new Complex(1, 0), n: 3);
        AssertClose(new Complex(5, 0), got); // 4 + 1
    }

    // ── trailing / interior semicolons + comments ───────────────────────────

    [Fact]
    public void BareExpressionStatement_StillParses()
        => AssertClose(new Complex(5, 0), Eval("z*z + c;", new Complex(2, 0), new Complex(1, 0)));

    [Fact]
    public void CommentInsideBlock_Skipped()
        => AssertClose(new Complex(5, 0),
            Eval("Complex t = z*z; // square\n return t + c;", new Complex(2, 0), new Complex(1, 0)));

    // ── existing single-expression forms still parse (no regression) ────────

    [Fact]
    public void PlainExpression_NoStatements_Unchanged()
        => AssertClose(new Complex(5, 0), Eval("z*z + c", new Complex(2, 0), new Complex(1, 0)));

    [Fact]
    public void LetExpression_StillWorks()
        => AssertClose(new Complex(8, 0), Eval("let a = z*z in a + z*z", new Complex(2, 0)));

    [Fact]
    public void LocalNamedE_ShadowsEulerConstant()
    {
        // A statement-block local may reuse a constant's spelling; scope wins.
        var got = Eval("Complex e = z*z; return e + c;", new Complex(2, 0), new Complex(1, 0));
        AssertClose(new Complex(5, 0), got); // 4 + 1, NOT Euler's e
    }

    // ── errors ──────────────────────────────────────────────────────────────

    [Fact]
    public void IfAssignToUnboundName_Throws()
        => Assert.ThrowsAny<Exception>(() => SandboxExpression.Parse("if (n == 0) w = c; return z;"));

    [Fact]
    public void DanglingStatementsAfterReturn_Throw()
        => Assert.ThrowsAny<Exception>(() => SandboxExpression.Parse("return z; return c;"));
}
