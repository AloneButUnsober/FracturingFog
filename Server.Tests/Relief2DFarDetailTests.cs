// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #520 — far-detail. The factor tightens the raymarch's distance cone (pixelAngle)
// and raises the step budget so distant filaments resolve; both the CPU trace and
// the GPU uniforms derive these from BuildObliqueCamera, so a single change there
// drives both. 1.0 = off (camera unchanged, byte-identical).

using System;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class Relief2DFarDetailTests
{
    private static FractalParameters P(double farDetail) => new()
    {
        Relief2DEnabled = true,
        Relief2DRaymarch = true,
        Relief2DCameraElevationDeg = 30,
        Relief2DCameraFovDeg = 55,
        Relief2DFarDetail = farDetail,
    };

    [Fact]
    public void FarDetail_1_Leaves_Camera_Unchanged()
    {
        var off = HeightfieldRaymarch2D.BuildObliqueCamera(1000, 750, 1000.0 / 750.0, 1.0, 1.0, P(1.0));
        // A fresh default-param camera at the same framing must match exactly.
        var baseline = HeightfieldRaymarch2D.BuildObliqueCamera(1000, 750, 1000.0 / 750.0, 1.0, 1.0,
            new FractalParameters { Relief2DEnabled = true, Relief2DRaymarch = true,
                                    Relief2DCameraElevationDeg = 30, Relief2DCameraFovDeg = 55 });
        Assert.Equal(baseline.PixelAngle, off.PixelAngle, 12);
        Assert.Equal(baseline.MaxSteps, off.MaxSteps);
    }

    [Fact]
    public void FarDetail_Below1_Tightens_Cone_And_Raises_StepBudget()
    {
        var off = HeightfieldRaymarch2D.BuildObliqueCamera(1000, 750, 1000.0 / 750.0, 1.0, 1.0, P(1.0));
        var on  = HeightfieldRaymarch2D.BuildObliqueCamera(1000, 750, 1000.0 / 750.0, 1.0, 1.0, P(0.4));

        Assert.True(on.PixelAngle < off.PixelAngle,
            $"cone should tighten: {off.PixelAngle} -> {on.PixelAngle}");
        Assert.Equal(off.PixelAngle * 0.4, on.PixelAngle, 12);   // exact factor
        Assert.True(on.MaxSteps > off.MaxSteps,
            $"step budget should rise: {off.MaxSteps} -> {on.MaxSteps}");
    }

    [Fact]
    public void FarDetail_Clamped_To_Floor()
    {
        // Below 0.15 clamps to 0.15 (never zero — a zero cone never converges).
        var a = HeightfieldRaymarch2D.BuildObliqueCamera(800, 600, 800.0 / 600.0, 1.0, 1.0, P(0.01));
        var b = HeightfieldRaymarch2D.BuildObliqueCamera(800, 600, 800.0 / 600.0, 1.0, 1.0, P(0.15));
        Assert.Equal(b.PixelAngle, a.PixelAngle, 12);
    }

    [Fact]
    public void Batch_FarDetail_Parses_And_RoundTrips()
    {
        string[] argv =
        {
            "FracturingFog", "--batch", "--fractal", "Mandelbrot",
            "--x", "-0.5", "--y", "0", "--zoom", "1",
            "--relief-far-detail", "0.35", "--out", "out.png",
        };
        Assert.True(BatchOptions.TryParse(argv, startIndex: 2, out var opts, out var err), err);
        Assert.Equal(0.35, opts.ReliefFarDetail!.Value, 6);
        Assert.True(opts.Relief);

        var snap = new BatchCommandSnapshot
        {
            Fractal = FractalType.Mandelbrot, CenterX = -0.5, CenterY = 0, Zoom = 1,
            ReliefEnabled = true, ReliefRaymarch = true, ReliefFarDetail = 0.35,
        };
        Assert.Contains("--relief-far-detail", BatchCommandBuilder.Build(snap));
    }

    [Fact]
    public void Batch_FarDetail_OutOfRange_Rejected()
    {
        string[] argv =
        {
            "FracturingFog", "--batch", "--fractal", "Mandelbrot",
            "--x", "-0.5", "--y", "0", "--zoom", "1",
            "--relief-far-detail", "0.05", "--out", "out.png",
        };
        Assert.False(BatchOptions.TryParse(argv, startIndex: 2, out _, out var err));
        Assert.Contains("relief-far-detail", err);
    }

    [Fact]
    public void Builder_Omits_FarDetail_At_Default()
    {
        var snap = new BatchCommandSnapshot
        {
            Fractal = FractalType.Mandelbrot, CenterX = -0.5, CenterY = 0, Zoom = 1,
            ReliefEnabled = true, ReliefRaymarch = true,   // ReliefFarDetail default 1.0
        };
        Assert.DoesNotContain("--relief-far-detail", BatchCommandBuilder.Build(snap));
    }
}
