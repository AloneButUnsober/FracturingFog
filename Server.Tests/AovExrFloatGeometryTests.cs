// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S1/S7 deep tail (3D-Rendering-Roadmap.md, #389 / #398 / #394):
// feed the REAL float AOV layers into the --aov-exr orchestrator. The relief
// raymarch already captures a ReliefAovBuffers (world-space unit normal +
// world-units depth) from the primary hit in the beauty pass (#416); this wires
// that capture through PosterRenderer into the multi-layer EXR, so normal.* / Z
// carry full precision and REPLACE the 8-bit Normals/Depth passes (which are
// n·0.5+0.5 / normalized-grey). These lock: (1) supplying float planes drops the
// 8-bit geometry passes and emits the raw values; (2) without them the 8-bit
// passes are kept (byte-compatible); (3) the PosterRenderer capture overload fills
// world-units depth on the relief-raymarch path.

using System;
using System.Collections.Generic;
using System.Linq;
using FracturingFog;
using FracturingFog.Imaging;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class AovExrFloatGeometryTests
{
    private static uint[] Fill(int n, uint v)
    {
        var b = new uint[n];
        for (int i = 0; i < n; i++) b[i] = v;
        return b;
    }

    private static PosterRequest ReliefRequest()
    {
        var fp = new FractalParameters
        {
            Relief2DEnabled = true,
            Relief2DRaymarch = true,
            Relief2DHeightScale = 1.4,
            Relief2DCameraAzimuthDeg = 25,
            Relief2DCameraElevationDeg = 45,
            Relief2DCameraFovDeg = 55,
            Relief2DGroundPlane = false,   // sky background → real silhouette
        };
        return new PosterRequest
        {
            FractalType = FractalType.Mandelbrot,
            CenterX = -0.5, CenterY = 0, Zoom = 1.0,
            MaxIterations = 150,
            Width = 96, Height = 72,
            ColorMap = ColorPalette.BuiltIns[0],
            Quality = QualityPreset.Standard,
            FractalParameters = fp,
            Path = "unused.exr",
            Format = ImageFileFormat.Exr,
        };
    }

    // Float normal + depth planes win: the 8-bit Normals/Depth passes are dropped,
    // the emitted geometry carries the raw values, and the lighting-component AOVs
    // are untouched.
    [Fact]
    public void FloatPlanes_Replace_EightBit_Normals_And_Depth()
    {
        int w = 2, h = 2, n = w * h;
        var beauty = Fill(n, 0xFF000000u);
        var normal = new float[] { 0, 1, 0,  1, 0, 0,  0, 0, -1,  0.6f, 0.8f, 0 };
        var depth = new float[] { 3.5f, 12.25f, 1e6f, 0.75f };

        // 8-bit Normals/Depth are present in the dict but must be superseded, while
        // Diffuse (a component pass) survives.
        var aovs = new Dictionary<AovView, uint[]>
        {
            [AovView.Normals] = Fill(n, 0xFF8080FFu),
            [AovView.Depth] = Fill(n, 0xFF606060u),
            [AovView.Diffuse] = Fill(n, 0xFF204060u),
        };

        var ch = AovExrExporter.BuildChannels(w, h, beauty, aovs, normal, depth);

        // Exactly one Z and one normal.* trio — the float ones.
        Assert.Single(ch, c => c.Name == "Z");
        Assert.Single(ch, c => c.Name == "normal.R");

        // Raw float values, NOT the 8-bit remap (0x80→0 for normal, grey→0..1 for Z).
        Assert.Equal(1f, ch.First(c => c.Name == "normal.G").Data[0], 5);   // +Y up, raw
        Assert.Equal(12.25f, ch.First(c => c.Name == "Z").Data[1], 5);      // world depth
        Assert.Equal(1e6f, ch.First(c => c.Name == "Z").Data[2], 0);        // far sentinel

        // Component AOV kept.
        Assert.Contains(ch, c => c.Name == "diffuse.R");
    }

    // Without float planes the 8-bit geometry passes are emitted as before — the
    // new optional args default to the legacy behaviour.
    [Fact]
    public void Without_Float_Planes_EightBit_Geometry_Is_Kept()
    {
        var aovs = new Dictionary<AovView, uint[]>
        {
            [AovView.Normals] = Fill(1, 0xFFFF8000u),  // +1,~0,-1
            [AovView.Depth] = Fill(1, 0xFF404040u),
        };
        var ch = AovExrExporter.BuildChannels(1, 1, Fill(1, 0xFF000000u), aovs);
        Assert.Equal(1f, ch.First(c => c.Name == "normal.R").Data[0], 2);   // 8-bit decode
        Assert.Equal(0x40 / 255f, ch.First(c => c.Name == "Z").Data[0], 4); // normalized grey
    }

    // The PosterRenderer capture overload fills the ReliefAovBuffers with world-units
    // depth (not a 0..1 remap) on the relief-raymarch path.
    [Fact]
    public void RenderToPixels_Capture_Fills_World_Units_Depth()
    {
        var req = ReliefRequest();
        var geo = new HeightfieldRaymarch2D.ReliefAovBuffers(req.Width, req.Height);
        var beauty = PosterRenderer.RenderToPixels(req, default, out int w, out int h, geo);

        Assert.Equal(96, w);
        Assert.Equal(72, h);
        Assert.True(new HashSet<uint>(beauty).Count > 1, "beauty non-blank");

        int farDepth = 0, nearDepth = 0;
        float minHit = float.MaxValue;
        foreach (var d in geo.Depth)
        {
            if (d >= 1e6f) farDepth++;
            else if (d > 0f) { nearDepth++; if (d < minHit) minHit = d; }
        }
        Assert.True(nearDepth > 0, "terrain pixels carry a finite depth");
        Assert.True(farDepth > 0, "sky pixels carry the far sentinel");
        Assert.True(minHit > 0.1f && minHit < 100f, $"world-units depth expected, got {minHit}");
    }

    // End-to-end: a relief-raymarch scene writes a readable multi-layer EXR through
    // the orchestrator with the float-geometry path active (floatGeo == true).
    [Fact]
    public void RenderToFile_Relief_Writes_Readable_Exr()
    {
        var req = ReliefRequest();
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ff-faov-orch-{Guid.NewGuid():N}.exr");
        try
        {
            var (w, h) = AovExrRenderer.RenderToFile(req, path, default);
            Assert.Equal(96, w);
            Assert.Equal(72, h);
            Assert.True(HdriRegistry.TryLoadFromFile(path, out var img) && img != null);
            Assert.Equal(96, img!.Width);
            Assert.Equal(72, img.Height);
        }
        finally { try { System.IO.File.Delete(path); } catch { } }
    }
}
