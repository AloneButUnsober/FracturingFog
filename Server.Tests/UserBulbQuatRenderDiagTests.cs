// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using Xunit;
using FracturingFog;
using FracturingFog.Models;

namespace FracturingFog.Server.Tests;

// #114 regression: in Auto DE mode (the default) a detected quaternion power map
// must use the analytic DE, not fall back to the numerical Jacobian. The
// numerical quat DE over-smooths a quaternion Julia into a featureless ball;
// Auto was rejecting the (exact) analytic DE and rendering that ball.
public class UserBulbQuatRenderDiagTests
{
    private static UserBulbCalculator Make(UserBulbDEModeKind de)
    {
        var calc = new UserBulbCalculator(72, 48)
        {
            FractalParameters = new FractalParameters
            {
                UserBulbAxisMode = UserBulbAxisModeKind.Quat,
                UserBulbCompiler = UserBulbCompilerKind.Roslyn,
                UserBulbBackend = UserBulbBackendKind.CPU,
                UserBulbDEMode = de,
                UserBulbJuliaMode = true,
                UserBulbQuatSliceW = 0.0,
                UserBulbJuliaCW = -0.2, UserBulbJuliaCX = 0.4, UserBulbJuliaCY = -0.4, UserBulbJuliaCZ = -0.4,
                UserBulbIterations = 12, UserBulbBailout = 16.0, UserBulbJacobianH = 1e-4,
                UserBulbMaxSteps = 128, UserBulbEpsilon = 1e-4, UserBulbCullRadius = 4.0,
                UserBulbCameraDistance = 3.0,
            },
        };
        calc.Compile("return z*z + c;");
        return calc;
    }

    private static int Hits(UserBulbCalculator calc)
    {
        calc.Calculate(default);
        var buf = calc.ColorBuffer;
        uint corner = buf[0];
        int hits = 0;
        for (int i = 0; i < calc.Width * calc.Height; i++)
            if (buf[i] != corner) hits++;
        return hits;
    }

    [Fact]
    public void QuatJulia_AutoMode_UsesAnalytic_NotNumericBall()
    {
        var analytic = Make(UserBulbDEModeKind.Analytic);
        var auto = Make(UserBulbDEModeKind.Auto);
        var numeric = Make(UserBulbDEModeKind.Numerical);
        if (!analytic.IsCompiled) return; // headless compile unavailable

        int ha = Hits(analytic), hAuto = Hits(auto), hn = Hits(numeric);

        Assert.True(ha > 0 && hn > 0);
        // Auto must track Analytic (structured), not Numerical (the over-smoothed
        // ball). The two DE forms give different silhouettes; Auto picking the
        // numerical one is the reported "quaternion Julia is just a sphere" bug.
        Assert.Equal(ha, hAuto);
        Assert.NotEqual(hn, hAuto);
    }
}
