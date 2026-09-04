// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S12.1 (#652) — Tone Map + Exposure + Bloom on Relief 3D. Relief
// renders through HeightfieldRaymarch2D (not a calculator), so it never ran the
// stage-2 whole-buffer post chain the true-3D calculators do; the FX-dialog Tone Map
// / Bloom were silent no-ops. FractalRenderHost + PosterRenderer now run the SAME
// ScreenSpacePost.ApplyToneMapBloom over the captured relief HDR beauty. These lock
// the engine contract that wiring depends on (single-render, per the prepass-static
// note in ReliefHdrBeautyTests):
//   * with an FX tonemap set, terrain pixels tonemap from the captured HDR while sky
//     (NaN) pixels keep their 8-bit byte;
//   * ToneMap None + Bloom 0 is a no-op (default look preserved);
//   * bloom alone changes bright pixels.

using System;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ReliefStage2ToneMapTests
{
    private static (uint[] albedo, float[] height) Mandelbrot(int w, int h)
    {
        var calc = new MandelbrotCalculator(w, h)
        {
            CenterX = -0.75, CenterY = 0.0, Zoom = 1.0, MaxIterations = 400,
            ColorMap = new MonoBandMap(),
        };
        calc.Calculate(default);
        return ((uint[])calc.ColorBuffer.Clone(), (float[])calc.SmoothBuffer.Clone());
    }

    private static FractalParameters Relief() => new()
    {
        Relief2DEnabled = true,
        Relief2DRaymarch = true,
        Relief2DHeightScale = 1.4,
        Relief2DCameraAzimuthDeg = 25,
        Relief2DCameraElevationDeg = 45,
        Relief2DCameraFovDeg = 55,
        Relief2DGroundPlane = false,   // sky background → NaN HDR sky sentinel
        Relief2DSupersample = 2,
    };

    private static (uint[] dst, HeightfieldRaymarch2D.ReliefAovBuffers aov, int w, int h) RenderRelief()
    {
        int w = 160, h = 120;
        var (albedo, height) = Mandelbrot(w, h);
        var dst = new uint[w * h];
        var aov = new HeightfieldRaymarch2D.ReliefAovBuffers(w, h, false, false, captureHdr: true);
        HeightfieldRaymarch2D.Render(albedo, height, w, h, w, h, Relief(), dst, out double hitFrac, null, aov);
        Assert.True(hitFrac > 0.05 && hitFrac < 0.95, $"need a mixed hit/sky frame (hitFrac={hitFrac})");
        return (dst, aov, w, h);
    }

    [Fact]
    public void ToneMap_Tonemaps_Terrain_And_Keeps_Sky_Byte()
    {
        var (dst, aov, w, h) = RenderRelief();
        var before = (uint[])dst.Clone();

        var fx = LightingFxData.CreateDefault();
        fx.ToneMap = ToneMapOperator.Aces;   // the FX-dialog Tone Map operator

        ScreenSpacePost.ApplyToneMapBloom(dst, aov.HdrBeauty!, w, h, in fx);

        int terrainChanged = 0, skyChecked = 0, skyChanged = 0;
        for (int i = 0; i < w * h; i++)
        {
            bool sky = float.IsNaN(aov.HdrBeauty![i * 3]);
            if (sky) { skyChecked++; if (dst[i] != before[i]) skyChanged++; }
            else if (dst[i] != before[i]) terrainChanged++;
        }
        Assert.True(skyChecked > 0, "expected sky pixels");
        Assert.Equal(0, skyChanged);              // NaN sky keeps its byte
        Assert.True(terrainChanged > 0, "terrain must tonemap from the captured HDR beauty");
    }

    [Fact]
    public void NoToneMap_NoBloom_IsNoOp()
    {
        var (dst, aov, w, h) = RenderRelief();
        var before = (uint[])dst.Clone();

        var fx = LightingFxData.CreateDefault();   // ToneMap None, Bloom 0
        Assert.Equal(ToneMapOperator.None, fx.ToneMap);
        Assert.Equal(0.0, fx.BloomStrength);

        ScreenSpacePost.ApplyToneMapBloom(dst, aov.HdrBeauty!, w, h, in fx);

        Assert.Equal(before, dst);                 // default look preserved on relief
    }

    [Fact]
    public void Bloom_Alone_Changes_Bright_Pixels()
    {
        var (dst, aov, w, h) = RenderRelief();
        var before = (uint[])dst.Clone();

        var fx = LightingFxData.CreateDefault();
        fx.ToneMap = ToneMapOperator.None;
        fx.BloomThreshold = 0.0;    // bloom every lit pixel
        fx.BloomStrength = 0.8;

        ScreenSpacePost.ApplyToneMapBloom(dst, aov.HdrBeauty!, w, h, in fx);

        int changed = 0;
        for (int i = 0; i < w * h; i++) if (dst[i] != before[i]) changed++;
        Assert.True(changed > 0, "bloom must brighten some terrain pixels");
    }
}
