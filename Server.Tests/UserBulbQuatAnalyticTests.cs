// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using Xunit;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Calculators;

namespace FracturingFog.Server.Tests;

// #114 — quaternion analytic DE. UserBulb Quat mode previously always used the
// numerical Jacobian (noisy render / blobby mesh). A detected q^2+c power map
// should now report an analytic pattern and expose a finite smooth export DE.
public class UserBulbQuatAnalyticTests
{
    private static UserBulbCalculator QuatSquare(bool julia)
    {
        var calc = new UserBulbCalculator(16, 16)
        {
            FractalParameters = new FractalParameters
            {
                UserBulbAxisMode = UserBulbAxisModeKind.Quat,
                UserBulbCompiler = UserBulbCompilerKind.Roslyn,
                UserBulbIterations = 8,
                UserBulbBailout = 16.0,
                UserBulbJacobianH = 1e-4,
                UserBulbJuliaMode = julia,
                UserBulbQuatSliceW = 0.0,
                UserBulbJuliaCW = -0.2, UserBulbJuliaCX = 0.6,
                UserBulbJuliaCY = 0.0, UserBulbJuliaCZ = 0.0,
                UserBulbDEMode = UserBulbDEModeKind.Analytic,
            },
        };
        // z*z is the Hamilton quaternion square → the quaternion Julia/Mandelbrot.
        calc.Compile("return z*z + c;");
        return calc;
    }

    [Fact]
    public void QuatSquare_DetectsAnalyticPattern()
    {
        var calc = QuatSquare(julia: true);
        if (!calc.IsCompiled) return; // headless compile unavailable — skip
        Assert.NotEqual(AnalyticDEKind.None, calc.AnalyticPattern.Kind);
        Assert.Equal(2.0, calc.AnalyticPattern.Power);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void QuatSquare_ExportSampler_FiniteSmoothDE(bool julia)
    {
        var calc = QuatSquare(julia);
        if (!calc.IsCompiled) return;

        var sampler = calc.MakeExportSampler(14, 1e-5);
        Assert.NotNull(sampler);
        // Sample a few points around the set — all finite (a smooth analytic
        // field, not the numerical Jacobian's occasional non-finite spikes).
        foreach (var (x, y, z) in new[] { (0.3, 0.2, 0.1), (0.8, 0.0, 0.0), (0.1, 0.5, 0.3) })
        {
            double d = sampler!(x, y, z);
            Assert.True(double.IsFinite(d), $"DE non-finite at ({x},{y},{z})");
        }
    }
}
