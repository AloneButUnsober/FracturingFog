// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1078 — DE-shadowed 3D froxel fog for UserBulb and Kifs (the two raymarchers
// without a concrete DE struct; they now route their DE delegate through
// DelegateDeAdapter). Checked against a physical invariant, not the code's own
// output: a shadow can only take light away. Extinction does not depend on
// visibility, so with shadows on every pixel's channels are ≤ the unshadowed
// frame's (± 1 rounding), and the object casts at least some shadow. 0 steps is
// the unshadowed frame exactly.

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class FroxelShadowUserBulbKifsTests
{
    private const int W = 48, H = 36;

    public enum Kind { Kifs, KifsSierpinski, UserBulb }

    private static uint[] Render(Kind kind, int steps, out long ms, double legacyBulbFog = 0,
        double bulbDist = 3.0, double zoom = 1.0)
    {
        var fx = LightingFxData.CreateDefault();
        fx.FogDensity = 0.05; fx.Froxel3D = true; fx.Froxel3DShadowSteps = steps;
        fx.SsaoSamples = 0; fx.EdgeStrength = 0;
        var fp = new FractalParameters
        {
            Lighting = fx,
            KifsFold = kind == Kind.KifsSierpinski ? KifsFoldKind.Sierpinski : KifsFoldKind.Menger,
            UserBulbSource = "z^8 + c",
            UserBulbCompiler = UserBulbCompilerKind.Sandbox,
            UserBulbFogDensity = legacyBulbFog,
            UserBulbCameraDistance = bulbDist,
        };
        dynamic calc = kind == Kind.UserBulb ? new UserBulbCalculator(W, H) : (object)new KifsCalculator(W, H);
        calc.ColorMap = ColorPalette.BuiltIns[0];
        calc.FractalParameters = fp;
        calc.Zoom = zoom;
        var sw = Stopwatch.StartNew();
        calc.Calculate(CancellationToken.None);
        ms = sw.ElapsedMilliseconds;
        return (uint[])((uint[])calc.ColorBuffer).Clone();
    }

    private static int Ch(uint p, int s) => (int)((p >> s) & 0xFF);

    [Theory]
    [InlineData(Kind.Kifs)]
    [InlineData(Kind.KifsSierpinski)]
    [InlineData(Kind.UserBulb)]
    public void Shadows_OnlyTakeLightAway_AndZeroStepsIsUnshadowed(Kind kind)
    {
        var open = Render(kind, 0, out _);
        Assert.True(open.Distinct().Count() > 10, $"{kind}: blank render");
        Assert.Equal(open, Render(kind, 0, out _));
        var shadowed = Render(kind, 24, out _);

        int darker = 0;
        for (int i = 0; i < W * H; i++)
        {
            foreach (int s in new[] { 16, 8, 0 })
                Assert.True(Ch(shadowed[i], s) <= Ch(open[i], s) + 1,
                    $"{kind}: pixel {i} channel {s} got brighter ({Ch(open[i], s)} -> {Ch(shadowed[i], s)})");
            if (Ch(shadowed[i], 16) + Ch(shadowed[i], 8) + Ch(shadowed[i], 0)
                < Ch(open[i], 16) + Ch(open[i], 8) + Ch(open[i], 0) - 3) darker++;
        }
        Assert.True(darker > W * H / 50, $"{kind}: only {darker} pixels in shadow");
    }

    [Fact]
    public void UserBulb_LegacyFogKnob_DoesNotStackOnTheFroxelVolume()
    {
        // The froxel volume replaces the surface fog; the legacy UserBulbFogDensity
        // fallback must not re-add it underneath.
        Assert.Equal(Render(Kind.UserBulb, 0, out _), Render(Kind.UserBulb, 0, out _, legacyBulbFog: 0.3));
    }

    [Fact]
    public void UserBulb_ZoomIsTheSameEye_SameShadowedFrame()
    {
        // #1079 for UserBulb: (distance 6, Zoom 2) is the same eye as (distance 3,
        // Zoom 1), so the froxel volume — shafts included — must frame identically.
        var a = Render(Kind.UserBulb, 16, out _, bulbDist: 3.0, zoom: 1.0);
        Assert.Equal(a, Render(Kind.UserBulb, 16, out _, bulbDist: 6.0, zoom: 2.0));
        Assert.NotEqual(a, Render(Kind.UserBulb, 16, out _, bulbDist: 3.6, zoom: 1.0));
    }
}
