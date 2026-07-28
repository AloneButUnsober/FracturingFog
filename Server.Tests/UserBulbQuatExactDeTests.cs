// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using Xunit;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Calculators;

namespace FracturingFog.Server.Tests;

// #115 — exact full-derivative quaternion DE for q^2 + c. Verifies:
//  (1) a z*z+c quat map is detected as Square (so the exact path engages),
//  (2) with DE Mode = Analytic the export sampler is the exact concrete-parity
//      DE — proven by matching an independent re-implementation of the same
//      dq := 2*q*dq / 0.5*|q|*ln|q|/|dq| recurrence to floating-point precision,
//  (3) the field varies over orders of magnitude across space (a real distance
//      field, NOT the near-constant "ball" the numerical Jacobian collapsed to).
public class UserBulbQuatExactDeTests
{
    private const double SliceW = 0.0, Bailout = 16.0;
    private const double JcW = -0.2, JcX = 0.6, JcY = 0.0, JcZ = 0.0;
    private const int Iter = 14;

    private static UserBulbCalculator QuatSquare(UserBulbDEModeKind de, bool julia)
    {
        var calc = new UserBulbCalculator(16, 16)
        {
            FractalParameters = new FractalParameters
            {
                UserBulbAxisMode = UserBulbAxisModeKind.Quat,
                UserBulbCompiler = UserBulbCompilerKind.Roslyn,
                UserBulbDEMode = de,
                UserBulbJuliaMode = julia,
                UserBulbQuatSliceW = SliceW,
                UserBulbJuliaCW = JcW, UserBulbJuliaCX = JcX, UserBulbJuliaCY = JcY, UserBulbJuliaCZ = JcZ,
                UserBulbIterations = Iter, UserBulbBailout = Bailout, UserBulbJacobianH = 1e-4,
            },
        };
        calc.Compile("return z*z + c;");
        return calc;
    }

    // Independent reference: the exact Hubbard-Douady quaternion square DE in
    // UserBulb's Quat(W,X,Y,Z) convention. Mirrors QuatJuliaCalculator's
    // recurrence via the same Quat.operator* the compiled kernel uses.
    private static double Ref(double x, double y, double z, bool julia)
    {
        Quat q, c, dq;
        if (julia) { q = new Quat(SliceW, x, y, z); c = new Quat(JcW, JcX, JcY, JcZ); dq = Quat.Identity; }
        else { q = Quat.Zero; c = new Quat(SliceW, x, y, z); dq = Quat.Zero; }

        double bail2 = Bailout * Bailout;
        double q2 = q.LengthSquared;
        for (int i = 0; i < Iter; i++)
        {
            dq = 2.0 * (q * dq);
            if (!julia) dq += Quat.Identity;
            q = q * q + c;
            q2 = q.LengthSquared;
            if (!double.IsFinite(q2) || q2 > bail2) break;
        }
        double d2 = dq.LengthSquared;
        if (!double.IsFinite(q2) || !double.IsFinite(d2) || d2 < 1e-30) return 0.0;
        if (q2 < 1.0) return 0.0;
        double qMag = Math.Sqrt(q2);
        return 0.5 * qMag * Math.Log(qMag) / Math.Sqrt(d2);
    }

    [Fact]
    public void QuatSquare_DetectsSquare_ForExactPath()
    {
        var calc = QuatSquare(UserBulbDEModeKind.Analytic, julia: true);
        if (!calc.IsCompiled) return; // headless compile unavailable — skip
        Assert.Equal(AnalyticDEKind.Square, calc.AnalyticPattern.Kind);
        Assert.Equal(2.0, calc.AnalyticPattern.Power);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExactSampler_MatchesConcreteRecurrence(bool julia)
    {
        var calc = QuatSquare(UserBulbDEModeKind.Analytic, julia);
        if (!calc.IsCompiled) return;

        var sampler = calc.MakeExportSampler(Iter, 1e-5);
        Assert.NotNull(sampler);

        foreach (var (x, y, z) in new[]
        {
            (0.3, 0.2, 0.1), (0.9, 0.0, 0.0), (1.4, 0.7, 0.2),
            (0.0, 0.0, 0.0), (0.5, -0.3, 0.6), (2.5, 0.0, 0.0),
        })
        {
            double got = sampler!(x, y, z);
            double want = Ref(x, y, z, julia);
            Assert.True(double.IsFinite(got) && got >= 0.0, $"non-finite/neg at ({x},{y},{z})");
            Assert.Equal(want, got, 9); // exact recurrence — match to 1e-9
        }
    }

    [Fact]
    public void ExactField_VariesAcrossSpace_NotABall()
    {
        var calc = QuatSquare(UserBulbDEModeKind.Analytic, julia: true);
        if (!calc.IsCompiled) return;
        var sampler = calc.MakeExportSampler(Iter, 1e-5);
        Assert.NotNull(sampler);

        double min = double.PositiveInfinity, max = 0.0;
        foreach (var (x, y, z) in new[]
        {
            (0.6, 0.0, 0.0), (0.0, 0.6, 0.0), (1.0, 0.5, 0.2),
            (1.5, 1.5, 0.0), (0.4, 0.4, 0.4), (2.5, 0.0, 0.0),
        })
        {
            double d = sampler!(x, y, z);
            Assert.True(double.IsFinite(d) && d >= 0.0);
            if (d > 0.0 && d < min) min = d;
            if (d > max) max = d;
        }
        // A collapsed ball reads near-constant; a true DE field spreads wide.
        Assert.True(max > min * 3.0, $"DE field too flat (min={min}, max={max}) — ball regression");
    }

    // Numerical (default) and exact DE give different fields — confirms the
    // exact path is actually distinct and engaged only under Analytic.
    [Fact]
    public void ExactAndNumerical_Differ()
    {
        var exact = QuatSquare(UserBulbDEModeKind.Analytic, julia: true);
        var numeric = QuatSquare(UserBulbDEModeKind.Numerical, julia: true);
        if (!exact.IsCompiled || !numeric.IsCompiled) return;

        var se = exact.MakeExportSampler(Iter, 1e-5);
        var sn = numeric.MakeExportSampler(Iter, 1e-5);
        Assert.NotNull(se);
        Assert.NotNull(sn);

        // At least one probe point should differ materially between the exact
        // ln-form DE and the numerical Jacobian's linear estimate.
        bool anyDiff = false;
        foreach (var (x, y, z) in new[] { (0.9, 0.3, 0.1), (1.3, 0.6, 0.2), (0.7, 0.7, 0.0) })
        {
            double de = se!(x, y, z), dn = sn!(x, y, z);
            if (Math.Abs(de - dn) > 1e-6) { anyDiff = true; break; }
        }
        Assert.True(anyDiff, "exact and numerical DE unexpectedly identical");
    }
}
