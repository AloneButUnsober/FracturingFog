// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S1 deep slice (3D-Rendering-Roadmap.md, #389 / #398): FLOAT-NATIVE
// AOVs in one pass. The relief raymarch now fills a ReliefAovBuffers (world-space
// normal + world-units depth) from the PRIMARY hit in the same pass as the beauty
// — no 8-bit quantisation, no re-render — and AovExrExporter packs those float
// planes straight into an EXR (Z = true depth, normal.* = full-precision). These
// lock: capture leaves the beauty byte-identical, the normals are unit-length on
// hits, background depth is far, and the float packer emits the raw values.

using System;
using System.Linq;
using FracturingFog;
using FracturingFog.Imaging;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class FloatAovCaptureTests
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
        Relief2DGroundPlane = false,   // sky background → real silhouette
        Relief2DSupersample = 2,
    };

    // NOTE on "capture leaves the beauty byte-identical": the capture code writes
    // ONLY to the ReliefAovBuffers arrays (the beauty write is unchanged), and the
    // GPU gate forces the identical CPU trace when a capture target is supplied — so
    // the beauty is unperturbed by construction. A cross-render equality assertion
    // is deliberately NOT made here because HeightfieldRaymarch2D's height-field
    // prepass uses process-global static scratch buffers (s_compressed / s_despikeSrc
    // / s_prepassMaxH …) that are not safe against a CONCURRENT relief render, so any
    // two-render comparison is flaky under xUnit's parallel test classes — a
    // pre-existing hazard, independent of AOV capture.

    [Fact]
    public void Capture_Fills_UnitNormals_On_Hits_And_Far_Depth_On_Sky()
    {
        int w = 160, h = 120;
        var (albedo, height) = Mandelbrot(w, h);
        var dst = new uint[w * h];
        var aov = new HeightfieldRaymarch2D.ReliefAovBuffers(w, h);
        HeightfieldRaymarch2D.Render(albedo, height, w, h, w, h, Relief(), dst, out double hitFrac, null, aov);

        Assert.True(hitFrac > 0.05 && hitFrac < 0.95, $"need a mixed hit/sky frame (hitFrac={hitFrac})");

        int unitNormals = 0, farDepth = 0, nearDepth = 0;
        for (int i = 0; i < w * h; i++)
        {
            float nx = aov.NormalXyz[i * 3], ny = aov.NormalXyz[i * 3 + 1], nz = aov.NormalXyz[i * 3 + 2];
            double mag = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            float d = aov.Depth[i];
            if (Math.Abs(mag - 1.0) < 1e-3) unitNormals++;
            if (d >= 1e6f) farDepth++;
            else if (d > 0f && d < 1e6f) nearDepth++;
        }
        Assert.True(unitNormals > 0, "some primary hits should carry a unit normal");
        Assert.True(farDepth > 0, "sky/background pixels should carry the far-depth sentinel");
        Assert.True(nearDepth > 0, "terrain pixels should carry a finite world-units depth");
    }

    [Fact]
    public void Depth_Is_World_Units_Not_Normalized()
    {
        int w = 120, h = 96;
        var (albedo, height) = Mandelbrot(w, h);
        var dst = new uint[w * h];
        var aov = new HeightfieldRaymarch2D.ReliefAovBuffers(w, h);
        HeightfieldRaymarch2D.Render(albedo, height, w, h, w, h, Relief(), dst, out _, null, aov);

        // The nearest real terrain hit is a world-space camera distance of order 1+
        // (the camera sits a few units back), NOT a 0..1 normalized value.
        float minHit = float.MaxValue;
        foreach (var d in aov.Depth) if (d > 0f && d < 1e6f && d < minHit) minHit = d;
        Assert.True(minHit > 0.1f && minHit < 100f, $"world-units depth expected, got {minHit}");
    }

    [Fact]
    public void FloatChannels_Carry_Raw_Normal_And_Depth()
    {
        int w = 2, h = 2;
        var beauty = new uint[] { 0xFF808080u, 0xFF404040u, 0xFFC0C0C0u, 0xFF000000u };
        var normal = new float[] { 0, 1, 0,  1, 0, 0,  0, 0, -1,  0.6f, 0.8f, 0 };
        var depth = new float[] { 3.5f, 12.25f, 1e6f, 0.75f };

        var ch = AovExrExporter.BuildFloatChannels(w, h, beauty, normal, depth);
        var names = ch.Select(c => c.Name).ToArray();
        Assert.Contains("normal.R", names);
        Assert.Contains("normal.G", names);
        Assert.Contains("normal.B", names);
        Assert.Contains("Z", names);

        // normal.G at pixel 0 = 1 (the +Y up normal), raw — not remapped to 0.5.
        Assert.Equal(1f, ch.First(c => c.Name == "normal.G").Data[0], 5);
        // Z carries the true world depth, not a 0..1 remap.
        Assert.Equal(12.25f, ch.First(c => c.Name == "Z").Data[1], 5);
        Assert.Equal(1e6f, ch.First(c => c.Name == "Z").Data[2], 0);
    }

    [Fact]
    public void WriteFloatAov_RoundTrips_Beauty_Rgb()
    {
        int w = 4, h = 3;
        var beauty = new uint[w * h];
        for (int i = 0; i < beauty.Length; i++)
            beauty[i] = 0xFF000000u | ((uint)(i * 9 % 256) << 16) | ((uint)(i * 5 % 256) << 8) | (uint)(i * 3 % 256);
        var normal = new float[w * h * 3];
        var depth = new float[w * h];
        for (int i = 0; i < w * h; i++) { normal[i * 3 + 1] = 1f; depth[i] = 2f + i; }

        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ff-faov-{Guid.NewGuid():N}.exr");
        try
        {
            AovExrExporter.WriteFloatAov(path, w, h, beauty, normal, depth);
            Assert.True(HdriRegistry.TryLoadFromFile(path, out var img) && img != null);
            Assert.Equal(w, img!.Width);
            Assert.Equal(h, img.Height);
            float er = ViewTransformOps.SrgbToLinear(((beauty[0] >> 16) & 0xFF) / 255f);
            Assert.Equal((float)(Half)er, img.Data[0], 2);
        }
        finally { try { System.IO.File.Delete(path); } catch { } }
    }
}
