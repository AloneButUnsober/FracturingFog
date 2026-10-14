// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1201 — a User Bulb primary ray that missed the bounding (cull) sphere was
// painted with UserBulb's own bg gradient, while rays that entered the sphere
// and missed the surface used ShadingPipeline.MissColor. So the sky toggle,
// the HDRI and the background fog stopped at the sphere's silhouette (a disc
// of the real backdrop inside a legacy gradient). Every miss now goes through
// MissColor, as in every other 3D family. Zoomed out so the frame corners miss
// the cull sphere; the sky colours are uniform (top == bottom) so the expected
// miss colour doesn't depend on the ray direction.

using System.Threading;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1201UserBulbCullMissTests
{
    private const int W = 96, H = 72;
    private const uint Sky = 0xFF3060A0u;

    private static (uint[] buf, LightingFxData fx, uint inSet) Render(bool backdrop, bool fog)
    {
        var fx = LightingFxData.CreateDefault();
        fx.ShowSkyBackdrop = backdrop;
        fx.BgTopColor = Sky;
        fx.BgBottomColor = Sky;
        if (fog)
        {
            fx.FogDensity = 0.05;
            fx.FogBackground = true;
            fx.FogBackgroundDistance = 20;
        }
        var fp = new FractalParameters { Lighting = fx, UserBulbSource = "z^8 + c" };
        var calc = new UserBulbCalculator(W, H)
        {
            ColorMap = ColorPalette.BuiltIns[0],
            FractalParameters = fp,
            Zoom = 0.25,   // corners miss the cull sphere
        };
        calc.Calculate(CancellationToken.None);
        return ((uint[])calc.ColorBuffer.Clone(), fx, calc.ColorMap.InSetColor);
    }

    private static uint[] Corners(uint[] b) => new[] { b[0], b[W - 1], b[(H - 1) * W], b[H * W - 1] };

    [Fact]
    public void Backdrop_Off_Cull_Misses_Use_The_InSet_Colour()
    {
        var (buf, _, inSet) = Render(backdrop: false, fog: false);
        Assert.All(Corners(buf), c => Assert.Equal(inSet, c));
    }

    [Fact]
    public void Backdrop_On_Cull_Misses_Show_The_Sky()
    {
        var (buf, _, _) = Render(backdrop: true, fog: false);
        Assert.All(Corners(buf), c => Assert.Equal(Sky, c));
    }

    [Fact]
    public void Background_Fog_Reaches_Cull_Misses()
    {
        var (buf, fx, inSet) = Render(backdrop: false, fog: true);
        uint expected = ShadingPipeline.MissColor(0, 0, 1, in fx, inSet);   // uniform sky → any direction
        Assert.NotEqual(inSet, expected);   // the fog does something here
        Assert.All(Corners(buf), c => Assert.Equal(expected, c));
    }
}
