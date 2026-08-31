// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #545 — exact forward-mode dz/dc in the User Equation interpreter.
//
// The interpreter used to estimate the Hubbard-Douady surface normal from a
// finite-difference second trajectory (dz/dc ≈ (zP − z)/h, h = 1e-6). This adds
// a dual-number forward-mode derivative so holomorphic maps carry an EXACT
// dz/dc in a single trajectory. Non-holomorphic maps (abs/conj/re/im/arg/
// ternary/…) keep the numeric Jacobian, so they are unchanged.

using System.Numerics;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Security;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class DslAnalyticDerivativeTests
{
    // ── holomorphy detection ────────────────────────────────────────────────
    [Theory]
    [InlineData("z*z + c", true)]
    [InlineData("z*z*z + c", true)]
    [InlineData("sin(z) + c", true)]
    [InlineData("log(sin(z)) + c", true)]
    [InlineData("exp(z) + c*z", true)]
    [InlineData("pow(z, 3) + c", true)]
    [InlineData("z*z + c + 0.5*prev", true)]     // prev is holomorphic (carried)
    [InlineData("abs(z) + c", false)]            // |z| — non-holomorphic
    [InlineData("conj(z) + c", false)]
    [InlineData("re(z) + c", false)]
    [InlineData("im(z) + c", false)]
    [InlineData("arg(z) + c", false)]
    [InlineData("fold(z) + c", false)]
    [InlineData("n == 0 ? c : z*z + c", false)]  // ternary branch
    public void IsHolomorphic_Classifies(string src, bool holo)
        => Assert.Equal(holo, SandboxExpression.Parse(src).IsHolomorphic);

    // ── exactness vs the closed form for z² + c ─────────────────────────────
    // z_{n+1} = z² + c ⇒ dz_{n+1}/dc = 2 z_n dz_n + 1, dz_0 = 0.
    [Fact]
    public void EvalStepD_MatchesClosedForm_ZSquaredPlusC()
    {
        var expr = SandboxExpression.Parse("z*z + c");
        var denv = expr.NewDualEnv();
        var c = new Complex(-0.4, 0.3);
        Complex z = Complex.Zero, dz = Complex.Zero;
        Complex zRef = Complex.Zero, dzRef = Complex.Zero;   // closed form
        for (int it = 0; it < 12; it++)
        {
            (z, dz) = expr.EvalStepD(z, dz, c, it, denv);
            dzRef = 2.0 * zRef * dzRef + Complex.One;
            zRef = zRef * zRef + c;
            Assert.True((z - zRef).Magnitude < 1e-12, $"value drift at {it}");
            Assert.True((dz - dzRef).Magnitude < 1e-12, $"deriv drift at {it}");
        }
    }

    // ── exactness vs a central finite difference (transcendental map) ────────
    [Fact]
    public void EvalStepD_MatchesFiniteDifference_Transcendental()
    {
        const string src = "sin(z) + c*c";
        var expr = SandboxExpression.Parse(src);
        var c = new Complex(0.2, -0.15);
        const double h = 1e-6;

        // Analytic dz/dc over a few steps.
        var denv = expr.NewDualEnv();
        Complex z = Complex.Zero, dz = Complex.Zero;
        for (int it = 0; it < 6; it++) (z, dz) = expr.EvalStepD(z, dz, c, it, denv);

        // Central difference of the same 6-step trajectory w.r.t. Re(c).
        Complex Run(Complex cc)
        {
            var env = expr.NewEnv();
            Complex zz = Complex.Zero;
            for (int it = 0; it < 6; it++) zz = expr.EvalStep(zz, cc, it, env);
            return zz;
        }
        var fd = (Run(c + new Complex(h, 0)) - Run(c - new Complex(h, 0))) / (2 * h);
        Assert.True((dz - fd).Magnitude < 1e-4, $"analytic {dz} vs fd {fd}");
    }

    // prev derivative is carried (CalcGen treats it opaque → wrong; we don't).
    [Fact]
    public void EvalStepD_CarriesPrevDerivative()
    {
        const string src = "z*z + c + 0.3*prev";
        var expr = SandboxExpression.Parse(src);
        var c = new Complex(0.1, 0.2);
        const double h = 1e-6;

        Complex RunAnalytic()
        {
            var denv = expr.NewDualEnv();
            Complex z = Complex.Zero, dz = Complex.Zero, pz = Complex.Zero, pdz = Complex.Zero;
            for (int it = 0; it < 8; it++)
            {
                var (zn, dzn) = expr.EvalStepD(z, dz, c, it, denv, pz, pdz);
                pz = z; pdz = dz; z = zn; dz = dzn;
            }
            return dz;
        }
        Complex Run(Complex cc)
        {
            var env = expr.NewEnv();
            Complex z = Complex.Zero, pz = Complex.Zero;
            for (int it = 0; it < 8; it++) { var zn = expr.EvalStep(z, cc, it, env, pz); pz = z; z = zn; }
            return z;
        }
        var fd = (Run(c + new Complex(h, 0)) - Run(c - new Complex(h, 0))) / (2 * h);
        Assert.True((RunAnalytic() - fd).Magnitude < 1e-5, $"analytic {RunAnalytic()} vs fd {fd}");
    }

    // ── calculator level ────────────────────────────────────────────────────
    private static UserEquationCalculator Render(string src, bool skip)
    {
        var calc = new UserEquationCalculator(96, 72)
        {
            CenterX = -0.5, CenterY = 0.0, Zoom = 0.7, MaxIterations = 120,
            ColorMap = new HsvPalette(),
            FractalParameters = new FractalParameters
            {
                UserEquationSource = src,
                UserEquationSkipJacobian = skip,
                UserCodeOrigin = UserCodeOrigin.Interactive,
            },
        };
        calc.Calculate(default);
        return calc;
    }

    // Analytic normals are real (some pixel has a non-zero normal) — the exact
    // path actually ran and produced a surface.
    [Fact]
    public void HolomorphicMap_ProducesNonZeroNormals()
    {
        var calc = Render("z*z + c", skip: false);
        bool any = false;
        for (int i = 0; i < calc.NormalXBuffer.Length && !any; i++)
            if (calc.NormalXBuffer[i] != 0f || calc.NormalYBuffer[i] != 0f) any = true;
        Assert.True(any, "analytic path produced an all-flat normal buffer");
    }

    // The colour map ignores the normal for 2D palettes, so switching the
    // derivative method must not move a single colour pixel: the analytic render
    // is byte-identical to the skip-Jacobian render for a 2D palette.
    [Fact]
    public void AnalyticDerivative_LeavesColorBufferByteIdentical()
    {
        var analytic = Render("z*z + c", skip: false).ColorBuffer;   // analytic path
        var skipped = Render("z*z + c", skip: true).ColorBuffer;    // no derivative
        Assert.Equal(skipped, analytic);
    }
}
