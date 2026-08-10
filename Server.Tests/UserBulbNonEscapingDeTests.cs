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

    private static UserBulbCalculator MakeCalc(
        UserBulbDEModeKind de, double deMult = 0.5,
        string step = Step, string? deBody = null, int iter = 12)
    {
        var calc = new UserBulbCalculator(16, 16)
        {
            FractalParameters = new FractalParameters
            {
                UserBulbAxisMode = UserBulbAxisModeKind.Vec3,
                UserBulbDEMode = de,
                UserBulbIterations = iter,
                UserBulbBailout = 16.0,
                UserBulbJacobianH = 1e-4,
                UserBulbNonEscDEMultiplier = deMult,
                UserBulbNonEscStabilityAxis = 1,   // clamp on y (cosh/sinh axis)
                UserBulbNonEscStabilityLimit = 8.0,
                UserBulbDeBody = deBody,
            },
        };
        calc.Compile(step);
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

    // ── #281 — user-authored dr body ─────────────────────────────────────

    [Fact]
    public void UserDrBody_drives_the_recurrence_exactly()
    {
        // Step "c" holds z = sample point (bounded, no clamp). DE body "dr*2"
        // doubles dr each iteration independent of z: dr_i = 2^i, so
        // de = min(1/dr) = 1/2^Iter, and SampleDE = DEMultiplier * that.
        const int iter = 6;
        var calc = MakeCalc(UserBulbDEModeKind.NonEscaping, deMult: 1.0,
            step: "c", deBody: "dr*2", iter: iter);
        double de = calc.SampleDE(0.1, 0.2, 0.1);
        double expected = 1.0 / Math.Pow(2.0, iter);   // = 1/64
        Assert.Equal(expected, de, 9);
    }

    [Fact]
    public void UserDrBody_differs_from_the_numerical_tangent()
    {
        // Same step, one calc with the analytic dr body and one without (empty
        // body → numerical tangent). With step "c" the tangent separation is 0,
        // so its dr degenerates to the offset (1) → de = 1, DE = deMult*1 = 0.5;
        // the "dr*2" body yields 1/64. They must diverge — proving the body is
        // consulted, not ignored.
        var withBody = MakeCalc(UserBulbDEModeKind.NonEscaping, deMult: 0.5,
            step: "c", deBody: "dr*2", iter: 6);
        var tangent = MakeCalc(UserBulbDEModeKind.NonEscaping, deMult: 0.5,
            step: "c", deBody: null, iter: 6);
        double a = withBody.SampleDE(0.1, 0.2, 0.1);
        double b = tangent.SampleDE(0.1, 0.2, 0.1);
        Assert.True(Math.Abs(a - b) > 1e-4, $"body ignored: withBody={a}, tangent={b}");
    }

    // ── #282 — shipped Amoser preset ────────────────────────────────────

    [Fact]
    public void Amoser_preset_body_engages_with_its_params()
    {
        // The shipped preset's dr body reads StretchScale/StretchMax/drScale/
        // drOffset. With those params defined it must compile (no DE-body error)
        // and drive a finite, spatially-varying field that differs from the
        // numerical tangent — proving the params resolve and the body is used.
        var calc = MakeAmoser(withParams: true);
        Assert.True(calc.IsCompiled, $"preset step failed: {calc.LastError}");
        Assert.DoesNotContain("DE body", calc.LastError ?? string.Empty);

        double min = double.PositiveInfinity, max = 0.0;
        foreach (var (x, y, z) in Pts)
        {
            double de = calc.SampleDE(x, y, z);
            Assert.True(double.IsFinite(de) && de >= 0.0, $"bad DE at ({x},{y},{z}): {de}");
            if (de < min) min = de;
            if (de > max) max = de;
        }
        Assert.True(max > min * 1.5, $"field too flat: min={min}, max={max}");

        // Same step/body but params absent → body fails to compile (unknown
        // identifiers) → tangent fallback. The two fields must diverge.
        var noParams = MakeAmoser(withParams: false);
        Assert.Contains("DE body", noParams.LastError ?? string.Empty);
        int differ = 0;
        foreach (var (x, y, z) in Pts)
            if (Math.Abs(calc.SampleDE(x, y, z) - noParams.SampleDE(x, y, z)) > 1e-6) differ++;
        Assert.True(differ > 0, "preset body identical to tangent — params/body not engaged");
    }

    private static UserBulbCalculator MakeAmoser(bool withParams)
    {
        var fp = new FractalParameters
        {
            UserBulbAxisMode = UserBulbAxisModeKind.Vec3,
            UserBulbDEMode = UserBulbDEModeKind.NonEscaping,
            UserBulbIterations = 8,
            UserBulbBailout = 16.0,
            UserBulbJacobianH = 1e-4,
            UserBulbNonEscDEMultiplier = 0.5,
            UserBulbNonEscStabilityAxis = 1,
            UserBulbNonEscStabilityLimit = 8.0,
            UserBulbDeBody = UserBulbStore.DslAmoserDeBody,
        };
        if (withParams)
        {
            fp.UserBulbParams.Add(new UserBulbParam { Name = "StretchScale", Value = 0.81 });
            fp.UserBulbParams.Add(new UserBulbParam { Name = "StretchMax",   Value = 1.04 });
            fp.UserBulbParams.Add(new UserBulbParam { Name = "drScale",      Value = 1.0 });
            fp.UserBulbParams.Add(new UserBulbParam { Name = "drOffset",     Value = 1.0 });
        }
        var calc = new UserBulbCalculator(16, 16) { FractalParameters = fp };
        calc.Compile(UserBulbStore.DslAmoserStep);
        return calc;
    }

    [Fact]
    public void BadDrBody_falls_back_to_tangent_without_breaking_the_step()
    {
        // A DE body that does not parse must NOT invalidate the step compile:
        // the calculator stays compiled, surfaces the DE-body error, and the
        // NonEscaping runner falls back to the numerical tangent (finite field).
        var calc = MakeCalc(UserBulbDEModeKind.NonEscaping, deMult: 0.5,
            step: "c", deBody: "dr +", iter: 6);
        Assert.True(calc.IsCompiled, "bad DE body wrongly invalidated the step compile");
        Assert.Contains("DE body", calc.LastError);
        double de = calc.SampleDE(0.1, 0.2, 0.1);
        Assert.True(double.IsFinite(de) && de >= 0.0, $"fallback field not finite/non-negative: {de}");
    }
}
