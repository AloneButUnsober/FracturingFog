// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Saving a region now auto-captures the live Lighting & FX (VL / fog / AO /
// lights) into LightingOverride, so a region is a full look snapshot — not just
// geometry + relief. Previously LightingOverride was always null and volumetric
// settings were lost on recall. Runs under the test data-root redirect
// (TestDataRootIsolation), so it never touches real user regions.

using System;
using FracturingFog;
using FracturingFog.Hosting;
using FracturingFog.Models;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(FractalRegionLibraryCollection.Name)]
public sealed class RegionLightingCaptureTests
{
    [Fact]
    public void SaveCurrentAsRegion_Captures_Live_Volumetric_Lighting()
    {
        var svc = new HostColorThemeService();
        var lib = FractalRegionLibrary.Instance;
        string name = $"FF-VLCapture-{Guid.NewGuid():N}";

        var fp = new FractalParameters { Relief2DEnabled = true };
        var fx = fp.Lighting;
        fx.FogDensity = 0.7;
        fx.VolumeSteps = 24;
        fx.VolumeAnisotropy = 0.6;
        fp.Lighting = fx;

        var live = new FractalViewState
        {
            CenterX = -0.5, CenterY = 0, Zoom = 1.0,
            FractalType = FractalType.Mandelbrot,
            FractalParameters = fp,
        };

        try
        {
            Assert.True(svc.SaveCurrentAsRegion(name, live));

            var saved = lib.FindByName(name)!;
            Assert.NotNull(saved.LightingOverride);

            var back = saved.LightingOverride!.ToFx();
            Assert.Equal(0.7, back.FogDensity);
            Assert.Equal(24, back.VolumeSteps);
            Assert.Equal(0.6, back.VolumeAnisotropy);
        }
        finally { lib.RemoveUserRegion(name); }
    }
}
