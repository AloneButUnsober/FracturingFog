// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.Linq;

using FracturingFog.Calculators;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// #987 — the slideshow's live palette fade wraps both themes in a
/// <see cref="BlendedColorMap"/>. Calculators type-check colour maps for
/// extra capabilities (orbit / interior / post-process / in-set …) that the
/// wrapper hides, so <see cref="FractalRenderHost.CanBlendLive"/> must refuse
/// those and the engine falls back to the static fade.
/// </summary>
public sealed class LiveColorFadeTests
{
    [Fact]
    public void Orbit_aware_theme_is_not_blendable()
        => Assert.False(FractalRenderHost.CanBlendLive(new CurvatureAverageMap()));

    [Fact]
    public void Every_built_in_verdict_matches_its_capability_interfaces()
    {
        foreach (var map in ColorPalette.BuiltIns)
        {
            bool hasExtraCapability = map.GetType().GetInterfaces().Any(i =>
                typeof(IColorMap).IsAssignableFrom(i)
                && i != typeof(IColorMap)
                && i != typeof(IColorMapWithPixelScale));
            Assert.Equal(!hasExtraCapability, FractalRenderHost.CanBlendLive(map));
            if (map is IOrbitAwareColorMap or IInteriorAwareColorMap or IPostProcessColorMap or IColorMapHandlesInSet)
                Assert.False(FractalRenderHost.CanBlendLive(map));
        }
    }

    [Fact]
    public void Plain_built_ins_exist_so_the_live_path_is_reachable()
        => Assert.Contains(ColorPalette.BuiltIns, FractalRenderHost.CanBlendLive);

    [Fact]
    public void Blend_endpoints_reproduce_the_source_maps()
    {
        var plain = ColorPalette.BuiltIns.Where(FractalRenderHost.CanBlendLive).Take(2).ToArray();
        Assert.Equal(2, plain.Length);
        foreach (var m in plain) m.MaxIterations = 500;
        var blended = new BlendedColorMap(plain[0], plain[1], 0f) { MaxIterations = 500 };

        for (int it = 1; it < 500; it += 37)
        {
            float smooth = it + 0.25f;
            blended.T = 0f;
            Assert.Equal(plain[0].Map(smooth, 0f, it), blended.Map(smooth, 0f, it));
            blended.T = 1f;
            Assert.Equal(plain[1].Map(smooth, 0f, it), blended.Map(smooth, 0f, it));
        }
    }
}
