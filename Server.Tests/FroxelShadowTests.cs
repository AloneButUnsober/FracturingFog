// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1069 (froxel F3) — DE-shadowed froxel populate (light shafts around 3D
// fractals). Checked against an analytic sphere, not against the code's own
// output: a point inside the sphere sees no light, a ray into it is blocked, a ray
// away is fully lit, and a froxel column whose cells sit in the sphere's shadow
// gathers no more in-scatter than an open column.

using System;
using System.Linq;

using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class FroxelShadowTests
{
    private readonly struct Sphere : IDistanceEstimator
    {
        public double Evaluate(double x, double y, double z) => Math.Sqrt(x * x + y * y + z * z) - 1.0;
    }

    [Fact]
    public void Shadow_InsideBlocked_OutsideLit_GrazingSoft()
    {
        var s = new Sphere();
        Assert.Equal(0.0, ScreenSpacePost.FroxelShadow(in s, 0, 0, 0.5, 0, 0, 1, 10, 32));      // inside
        Assert.Equal(0.0, ScreenSpacePost.FroxelShadow(in s, 0, 0, 3, 0, 0, -1, 10, 32));       // toward the sphere
        Assert.Equal(1.0, ScreenSpacePost.FroxelShadow(in s, 0, 0, 3, 0, 0, 1, 10, 32));        // away from it
        double graze = ScreenSpacePost.FroxelShadow(in s, 1.05, 0, 3, 0, 0, -1, 10, 32);        // just misses
        Assert.InRange(graze, 0.0001, 0.9999);
    }

    [Fact]
    public void Populate_ColumnInTheShadow_GathersLessLight()
    {
        // Camera on +Z looking at the unit sphere; one directional light from +Z
        // (behind the camera, so the sphere shadows the space BEHIND it). The centre
        // column passes through the sphere's shadow; a corner column stays open.
        var cam = FroxelCamera.LookAt((0, 0, 4), (0, 0, 0), 50 * Math.PI / 180, 0.05, 12, 4);
        var grid = new FroxelGrid(16, 16, 32, cam.Near, cam.Far);
        FroxelMedium Medium(FroxelVisibility? vis) => new()
        {
            BaseDensity = 0.1, Extinction = 1.0, ViewDx = cam.Fx, ViewDy = cam.Fy, ViewDz = cam.Fz,
            WorldExtent = 4,
            Lights = new[] { new FroxelLight { Type = 0, Color = 0xFFFFFFFFu, Intensity = 1, Lx = 0, Ly = 0, Lz = 1 } },
            WorldFrustum = true, Frustum = cam, Aspect = 1.0, Visibility = vis,
        };
        var sphere = new Sphere();
        var open = new FroxelVolumePass(grid);
        open.Populate(Medium(null));
        var shadowed = new FroxelVolumePass(grid);
        shadowed.Populate(Medium((x, y, z, lx, ly, lz, d) => ScreenSpacePost.FroxelShadow(in sphere, x, y, z, lx, ly, lz, d, 48)));

        double far = 31;
        var centreOpen = open.SampleColumn(8, 8, far);
        var centreShadow = shadowed.SampleColumn(8, 8, far);
        var cornerOpen = open.SampleColumn(0, 0, far);
        var cornerShadow = shadowed.SampleColumn(0, 0, far);

        Assert.True(centreShadow.inR < centreOpen.inR * 0.8, $"centre {centreShadow.inR} vs open {centreOpen.inR}");
        Assert.Equal(cornerOpen.inR, cornerShadow.inR, 6);                 // corner never in shadow
        Assert.True(centreShadow.inR <= cornerShadow.inR + 1e-9);
    }

    [Fact]
    public void Mandelbulb_ShadowStepsChangeTheFog_ZeroStepsDoNot()
    {
        uint[] Render(int steps)
        {
            var fx = LightingFxData.CreateDefault();
            fx.FogDensity = 0.05; fx.Froxel3D = true; fx.Froxel3DShadowSteps = steps;
            fx.SsaoSamples = 0; fx.EdgeStrength = 0;
            var calc = new MandelbulbCalculator(48, 36) { ColorMap = ColorPalette.BuiltIns[0], Zoom = 1.0 };
            calc.FractalParameters = new FractalParameters { BulbCameraDistance = 2.6, Lighting = fx };
            calc.Calculate(default);
            return (uint[])calc.ColorBuffer.Clone();
        }
        var none = Render(0);
        Assert.Equal(none, Render(0));
        Assert.NotEqual(none, Render(24));
    }

    // ── Batch parity ──────────────────────────────────────────────────────

    [Fact]
    public void ShadowStepsFlag_ParsesImpliesValidatesAndRoundTrips()
    {
        var baseArgs = new[] { "--x", "0", "--y", "0", "--zoom", "1", "--out", "o.png" };
        Assert.True(BatchOptions.TryParse(baseArgs.Concat(new[] { "--froxel-3d-shadow-steps", "24" }).ToArray(), 0, out var o, out var e), e);
        Assert.True(o.Froxel3D);
        Assert.Equal(24, o.Froxel3DShadowSteps);
        Assert.False(BatchOptions.TryParse(baseArgs.Concat(new[] { "--froxel-3d-shadow-steps", "500" }).ToArray(), 0, out _, out _));

        Assert.Contains("--froxel-3d-shadow-steps 24",
            BatchCommandBuilder.Build(new BatchCommandSnapshot { FogDensity = 0.1, Froxel3D = true, Froxel3DShadowSteps = 24 }));

        var fx = LightingFxData.CreateDefault();
        fx.Froxel3D = true; fx.Froxel3DShadowSteps = 24;
        Assert.DoesNotContain("Froxel3DShadowSteps", LightingFidelity.UnexpressedFields(fx));
        Assert.Equal(24, LightingFxPresetData.FromFx(fx).ToFx().Froxel3DShadowSteps);
    }
}
