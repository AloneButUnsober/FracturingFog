// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using Xunit;
using FracturingFog;
using FracturingFog.Models;

namespace FracturingFog.Server.Tests;

// #112 — UserBulb mesh export sampler. Locks the null-guard and (when the
// kernel compiles headless) that the snapshot sampler yields a finite DE and
// is independent of later FractalParameters mutation.
public class UserBulbExportSamplerTests
{
    [Fact]
    public void MakeExportSampler_NotCompiled_ReturnsNull()
    {
        var calc = new UserBulbCalculator(16, 16) { FractalParameters = new FractalParameters() };
        Assert.Null(calc.MakeExportSampler(12, 1e-5));
    }

    [Fact]
    public void MakeExportSampler_Compiled_FiniteAndSnapshotStable()
    {
        var calc = new UserBulbCalculator(16, 16)
        {
            FractalParameters = new FractalParameters
            {
                UserBulbIterations = 8,
                UserBulbBailout = 16.0,
                UserBulbJacobianH = 1e-4,
            },
        };
        // Triplex square (the default bulb-lite step) as the safe DSL — #27
        // Phase 3 removed the raw-C# compile path, so the body must be DSL.
        calc.Compile("vec(z.x*z.x - z.y*z.y - z.z*z.z, 2*z.x*z.y, 2*z.x*z.z) + c");
        Assert.True(calc.IsCompiled, $"DSL triplex-square should compile: {calc.LastError}");

        var sampler = calc.MakeExportSampler(12, 1e-5);
        Assert.NotNull(sampler);
        double d = sampler!(0.4, 0.1, 0.2);
        Assert.True(double.IsFinite(d), "export DE should be finite at a sampled point");

        // Snapshot independence: mutating the live params must not change the
        // captured sampler's output.
        double before = sampler(0.4, 0.1, 0.2);
        calc.FractalParameters.UserBulbIterations = 2;
        calc.FractalParameters.UserBulbJacobianH = 1e-2;
        double after = sampler(0.4, 0.1, 0.2);
        Assert.Equal(before, after);
    }
}
