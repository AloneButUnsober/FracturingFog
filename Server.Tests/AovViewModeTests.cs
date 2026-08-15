// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #317 — AOV / render-buffer view modes. A non-Beauty LightingFxData.DebugAov
// makes ShadingPipeline.Shade<TDe> return the chosen diagnostic buffer for each
// surface hit instead of the beauty pass. Beauty stays the normal shaded output
// (default). These lock the per-mode encoding on the deterministic CPU path.

using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class AovViewModeTests
{
    // Hit facing up (+Y), 5 units down the ray, step 48.
    private static ShadingInputs Hit(double nx, double ny, double nz)
        => new(px: 0, py: 0, pz: 0, nx: nx, ny: ny, nz: nz,
               rdx: 0, rdy: 0, rdz: 1, totalT: 5.0, hitDist: 1e-4, hitStep: 48, epsilon: 1e-4);

    private static uint Shade(AovView view, ShadingInputs i)
    {
        var fx = LightingFxData.CreateDefault();
        fx.DebugAov = view;
        var nd = default(NullDe);
        return ShadingPipeline.Shade<NullDe>(in i, 0xFF808080u, in fx, in nd, hasDe: false);
    }

    private static (byte r, byte g, byte b) Rgb(uint c)
        => ((byte)((c >> 16) & 0xFF), (byte)((c >> 8) & 0xFF), (byte)(c & 0xFF));

    [Fact]
    public void Beauty_Is_Default_And_Differs_From_Aov_Views()
    {
        var i = Hit(0, 1, 0);
        uint beauty = Shade(AovView.Beauty, i);
        // Default DebugAov is Beauty — same as the explicit call.
        var fx = LightingFxData.CreateDefault();
        Assert.Equal(AovView.Beauty, fx.DebugAov);
        Assert.NotEqual(beauty, Shade(AovView.Normals, i));
    }

    [Fact]
    public void Normals_Encode_Unit_Normal_As_Rgb()
    {
        // n=(0,1,0) -> (0.5,1.0,0.5) -> (128,255,128).
        Assert.Equal((byte)128, Rgb(Shade(AovView.Normals, Hit(0, 1, 0))).r);
        var up = Rgb(Shade(AovView.Normals, Hit(0, 1, 0)));
        Assert.Equal(((byte)128, (byte)255, (byte)128), up);
        // +X normal -> red channel maxes.
        Assert.Equal((byte)255, Rgb(Shade(AovView.Normals, Hit(1, 0, 0))).r);
    }

    [Fact]
    public void Depth_Is_Monotone_Grayscale()
    {
        var near = Rgb(Shade(AovView.Depth, new ShadingInputs(0,0,0, 0,1,0, 0,0,1, 1.0, 1e-4, 10, 1e-4)));
        var far  = Rgb(Shade(AovView.Depth, new ShadingInputs(0,0,0, 0,1,0, 0,0,1, 20.0, 1e-4, 10, 1e-4)));
        Assert.Equal(near.r, near.g); Assert.Equal(near.g, near.b);   // grayscale
        Assert.True(far.r > near.r, $"farther hit should be lighter (near={near.r}, far={far.r})");
    }

    [Fact]
    public void StepCount_Heat_Blue_To_Yellow()
    {
        // step 48 / 96 = 0.5 -> PackRgb(0.5, 0.4, 0.5) ~ (128,102,128).
        var c = Rgb(Shade(AovView.StepCount, Hit(0, 1, 0)));
        Assert.InRange(c.r, 126, 130);
        Assert.InRange(c.g, 100, 104);
        Assert.InRange(c.b, 126, 130);
        // Cheap hit (low step) is bluer than an expensive one.
        var cheap = Rgb(Shade(AovView.StepCount, new ShadingInputs(0,0,0, 0,1,0, 0,0,1, 5, 1e-4, 4, 1e-4)));
        Assert.True(cheap.b > cheap.r, "cheap hit should read blue");
    }

    [Fact]
    public void AO_And_Shadow_Are_White_With_No_Estimator()
    {
        // NullDe + default (AoSamples=0, ShadowSteps=0) -> full visibility -> white.
        Assert.Equal(0xFFFFFFFFu, Shade(AovView.AmbientOcclusion, Hit(0, 1, 0)));
        Assert.Equal(0xFFFFFFFFu, Shade(AovView.Shadow, Hit(0, 1, 0)));
    }

    [Fact]
    public void Diffuse_Is_Neutral_Under_White_Light()
    {
        // Default key light is white, so the diffuse AOV is grayscale.
        var c = Rgb(Shade(AovView.Diffuse, Hit(0, 1, 0)));
        Assert.Equal(c.r, c.g);
        Assert.Equal(c.g, c.b);
    }
}
