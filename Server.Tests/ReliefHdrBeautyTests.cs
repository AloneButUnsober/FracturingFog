// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S2 CORE (3D-Rendering-Roadmap.md, #389 / #396) — the producer wiring
// for the true-linear intermediate. The relief RAYMARCH now captures its PRE-CLAMP
// HDR beauty (byte-scale 0..∞) into ReliefAovBuffers.HdrBeauty at the primary hit,
// so the S2 view transform can tonemap real highlight headroom instead of the
// clamped 8-bit buffer. Contract: terrain hits carry a finite HDR sample, sky/miss
// pixels stay NaN (the "use the 8-bit beauty" sentinel), and feeding the captured
// buffer through LinearFloatImage.FromHdrByteScale + a view transform reprocesses
// terrain from the captured linear value while leaving sky byte-identical to the
// plain 8-bit path.
//
// Single-render only: the height-field prepass uses process-global static scratch
// (see FloatAovCaptureTests) so a two-render comparison is flaky under parallel
// xUnit; these compare two POST-process paths over one captured frame instead.

using System;
using FracturingFog;
using FracturingFog.Imaging;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ReliefHdrBeautyTests
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
        Relief2DGroundPlane = false,   // sky background → real silhouette (NaN HDR sky)
        Relief2DSupersample = 2,
    };

    [Fact]
    public void Capture_Fills_Terrain_Hdr_And_Leaves_Sky_NaN()
    {
        int w = 160, h = 120;
        var (albedo, height) = Mandelbrot(w, h);
        var dst = new uint[w * h];
        var aov = new HeightfieldRaymarch2D.ReliefAovBuffers(w, h, false, false, captureHdr: true);
        HeightfieldRaymarch2D.Render(albedo, height, w, h, w, h, Relief(), dst, out double hitFrac, null, aov);

        Assert.NotNull(aov.HdrBeauty);
        Assert.True(hitFrac > 0.05 && hitFrac < 0.95, $"need a mixed hit/sky frame (hitFrac={hitFrac})");

        int finite = 0, nan = 0;
        for (int i = 0; i < w * h; i++)
        {
            float hr = aov.HdrBeauty![i * 3];
            if (float.IsNaN(hr)) nan++;
            else { finite++; Assert.True(hr >= 0f, $"HDR byte-scale must be non-negative, got {hr}"); }
        }
        Assert.True(finite > 0, "terrain/ground pixels should carry a finite HDR sample");
        Assert.True(nan > 0, "sky/miss pixels should stay NaN (the fallback sentinel)");
    }

    // The live path (FractalRenderHost) arms the HDR plane through the same
    // MakeCapture the denoise uses — the captureHdr flag ORs an HDR-beauty plane in.
    [Fact]
    public void MakeCapture_Hdr_Flag_Allocates_The_Hdr_Plane_Even_With_Denoise_Off()
    {
        var noDenoise = new FractalParameters { Relief2DEnabled = true, Relief2DRaymarch = true };
        // No denoise, no HDR → null (byte-identical, keeps the GPU fast path).
        Assert.Null(ReliefDenoisePass.MakeCapture(noDenoise, 32, 24));
        Assert.Null(ReliefDenoisePass.MakeCapture(noDenoise, 32, 24, captureHdr: false));
        // No denoise, HDR wanted → an HDR-only capture (no normal-denoise motive).
        var hdrOnly = ReliefDenoisePass.MakeCapture(noDenoise, 32, 24, captureHdr: true);
        Assert.NotNull(hdrOnly);
        Assert.NotNull(hdrOnly!.HdrBeauty);
        Assert.Null(hdrOnly.Motion);
        Assert.Null(hdrOnly.Components);
    }

    [Fact]
    public void MakeCapture_Hdr_Flag_Coexists_With_Denoise_Capture()
    {
        var denoise = new FractalParameters
        {
            Relief2DEnabled = true, Relief2DRaymarch = true, Relief2DDenoiseIterations = 3,
        };
        var aov = ReliefDenoisePass.MakeCapture(denoise, 32, 24, captureHdr: true);
        Assert.NotNull(aov);
        Assert.NotNull(aov!.HdrBeauty);          // HDR plane present
        Assert.NotEmpty(aov.NormalXyz);          // denoise guides still allocated
        Assert.NotEmpty(aov.Depth);
    }

    [Fact]
    public void Hdr_Path_Reprocesses_Terrain_But_Leaves_Sky_Identical_To_8bit()
    {
        int w = 160, h = 120;
        var (albedo, height) = Mandelbrot(w, h);
        var dst = new uint[w * h];
        var aov = new HeightfieldRaymarch2D.ReliefAovBuffers(w, h, false, false, captureHdr: true);
        HeightfieldRaymarch2D.Render(albedo, height, w, h, w, h, Relief(), dst, out double hitFrac, null, aov);
        Assert.True(hitFrac > 0.05 && hitFrac < 0.95, $"need a mixed frame (hitFrac={hitFrac})");

        const ViewTransform op = ViewTransform.AcesFilmic;

        // HDR intermediate path (the S2 core producer→consumer).
        var hdrOut = LinearFloatImage.FromHdrByteScale(aov.HdrBeauty!, dst, w, h)
            .ApplyViewTransform(op, 0f).ToBgra();

        // Plain 8-bit view-transform path (the pre-S2-core behaviour).
        var plainOut = (uint[])dst.Clone();
        ViewTransformOps.Apply(plainOut, w * h, op, 0f);

        int terrainDiff = 0, skyChecked = 0, skyMismatch = 0;
        for (int i = 0; i < w * h; i++)
        {
            bool sky = float.IsNaN(aov.HdrBeauty![i * 3]);
            if (sky)
            {
                skyChecked++;
                if (hdrOut[i] != plainOut[i]) skyMismatch++;
            }
            else if (hdrOut[i] != plainOut[i]) terrainDiff++;
        }

        Assert.True(skyChecked > 0, "expected some sky pixels");
        Assert.Equal(0, skyMismatch);                 // sky decodes the fallback → identical to 8-bit path
        Assert.True(terrainDiff > 0, "terrain should tonemap from the captured linear HDR, not the 8-bit clip");
    }
}
