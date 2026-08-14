// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// SceneShot.LightingRegionName — per-shot lighting override by name. A shot can
// borrow another region's captured Lighting & FX, overriding its own region's
// lighting, so a scene re-lights a shot without editing the source region.
// Precedence: shot lighting source > shot region > default. Runs under the test
// data-root redirect (TestDataRootIsolation).

using System;
using System.Reflection;
using FracturingFog;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Export;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(FractalRegionLibraryCollection.Name)]
public sealed class SceneShotLightingOverrideTests
{
    private static readonly MethodInfo ResolveShot =
        typeof(SceneVideoRenderer).GetMethod(
            "ResolveShot", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static FractalRegion RegionWithFog(string name, double fog)
    {
        var fx = LightingFxData.CreateDefault();
        fx.FogDensity = fog;
        return new FractalRegion
        {
            Name = name,
            FractalType = FractalType.Mandelbrot,
            Zoom = 1.0,
            LightingOverride = LightingFxPresetData.FromFx(in fx),
        };
    }

    private static double ResolvedFog(SceneShot shot)
    {
        object resolved = ResolveShot.Invoke(null, new object[] { shot })!;
        var baseParams = (FractalParameters)resolved.GetType()
            .GetField("BaseParams")!.GetValue(resolved)!;
        return baseParams.Lighting.FogDensity;
    }

    [Fact]
    public void Shot_Without_Override_Uses_Its_Region_Lighting()
    {
        var lib = FractalRegionLibrary.Instance;
        string a = $"FF-ShotLight-A-{Guid.NewGuid():N}";
        try
        {
            Assert.True(lib.AddUserRegion(RegionWithFog(a, 0.3)));
            var shot = new SceneShot { RegionName = a };
            Assert.Equal(0.3, ResolvedFog(shot));
        }
        finally { lib.RemoveUserRegion(a); }
    }

    [Fact]
    public void Shot_LightingRegionName_Overrides_Its_Region_Lighting()
    {
        var lib = FractalRegionLibrary.Instance;
        string a = $"FF-ShotLight-A-{Guid.NewGuid():N}";
        string b = $"FF-ShotLight-B-{Guid.NewGuid():N}";
        try
        {
            Assert.True(lib.AddUserRegion(RegionWithFog(a, 0.3)));
            Assert.True(lib.AddUserRegion(RegionWithFog(b, 0.8)));
            var shot = new SceneShot { RegionName = a, LightingRegionName = b };
            // Borrowed region b's lighting wins over shot region a's.
            Assert.Equal(0.8, ResolvedFog(shot));
        }
        finally { lib.RemoveUserRegion(a); lib.RemoveUserRegion(b); }
    }
}
