// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using Xunit;
using FracturingFog;
using FracturingFog.Models;

namespace FracturingFog.Server.Tests;

// #280 — NonEscaping running-derivative DE for UserBulb. The Amoser complex-sine
// map never escapes, so the escape-time estimators do not apply; the NonEscaping
// path seeds at the sample point, accumulates de = min(1/dr) with a stability
// clamp, and returns DEMultiplier*de. These tests exercise the public SampleDE
// entry (which routes to the NonEscaping runner when DEMode == NonEscaping).
public class UserBulbNonEscapingDeTests
{
    // Amoser complex-sine step, Scale = 1 (no param needed).
    private const string Step =
        "vec(sin(z.x)*cosh(z.y), cos(z.x)*cos(z.z)*sinh(z.y), sin(z.z)*cosh(z.y)) + c";

    private static UserBulbCalculator MakeCalc(UserBulbDEModeKind de, double deMult = 0.5)
    {
        var calc = new UserBulbCalculator(16, 16)
        {
            FractalParameters = new FractalParameters
            {
                UserBulbAxisMode = UserBulbAxisModeKind.Vec3,
                UserBulbDEMode = de,
                UserBulbIterations = 12,
                UserBulbBailout = 16.0,
                UserBulbJacobianH = 1e-4,
                UserBulbNonEscDEMultiplier = deMult,
                UserBulbNonEscStabilityAxis = 1,   // clamp on y (cosh/sinh axis)
                UserBulbNonEscStabilityLimit = 8.0,
            },
        };
        calc.Compile(Step);
        Assert.True(calc.IsCompiled, $"step failed to compile: {calc.LastError}");
        return calc;
    }

    // A spread of interior/near-surface sample points.
    private static readonly (double X, double Y, double Z)[] Pts =
    {
        (0.0, 0.0, 0.0), (0.3, 0.1, -0.2), (-0.4, 0.25, 0.15),
        (0.7, -0.3, 0.5), (1.1, 0.05, -0.6), (-0.9, -0.15, 0.35),
    };

    [Fact]
    public void NonEscaping_field_is_finite_and_nonnegative()
    {
        var calc = MakeCalc(UserBulbDEModeKind.NonEscaping);
        foreach (var (x, y, z) in Pts)
        {
            double de = calc.SampleDE(x, y, z);
            Assert.True(double.IsFinite(de), $"non-finite DE at ({x},{y},{z}): {de}");
            Assert.True(de >= 0.0, $"negative DE at ({x},{y},{z}): {de}");
        }
    }

    [Fact]
    public void NonEscaping_field_varies_across_space()
    {
        // A real distance field, not a near-constant "ball".
        var calc = MakeCalc(UserBulbDEModeKind.NonEscaping);
        double min = double.PositiveInfinity, max = 0.0;
        foreach (var (x, y, z) in Pts)
        {
            double de = calc.SampleDE(x, y, z);
            if (de < min) min = de;
            if (de > max) max = de;
        }
        Assert.True(max > min * 1.5, $"field too flat: min={min}, max={max}");
    }

    [Fact]
    public void DEMultiplier_scales_the_field_linearly()
    {
        // Two calcs, DEMultiplier 0.5 vs 1.0; the ratio must be exactly 2 at any
        // point whose de is finite and non-zero (the multiplier is a pure gain).
        var half = MakeCalc(UserBulbDEModeKind.NonEscaping, deMult: 0.5);
        var full = MakeCalc(UserBulbDEModeKind.NonEscaping, deMult: 1.0);
        int matched = 0;
        foreach (var (x, y, z) in Pts)
        {
            double a = half.SampleDE(x, y, z);
            double b = full.SampleDE(x, y, z);
            if (a > 1e-9 && a < 1e18 && b < 1e18)
            {
                Assert.Equal(2.0, b / a, 6);
                matched++;
            }
        }
        Assert.True(matched > 0, "no finite sample points to compare");
    }

    [Fact]
    public void NonEscaping_differs_from_numerical_Jacobian()
    {
        // Proves the NonEscaping path actually engages: it must produce a
        // materially different field than the escape-time numerical Jacobian.
        var ne = MakeCalc(UserBulbDEModeKind.NonEscaping);
        var num = MakeCalc(UserBulbDEModeKind.Numerical);
        int differ = 0;
        foreach (var (x, y, z) in Pts)
        {
            double a = ne.SampleDE(x, y, z);
            double b = num.SampleDE(x, y, z);
            if (double.IsFinite(a) && double.IsFinite(b) && Math.Abs(a - b) > 1e-6) differ++;
        }
        Assert.True(differ > 0, "NonEscaping field identical to numerical Jacobian — path not engaged");
    }
}
