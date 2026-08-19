// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S1 integration follow-up (3D-Rendering-Roadmap.md, #389 / #398):
// the AOV-EXR render ORCHESTRATION — render a scene once per DebugAov and pack
// the passes into one multi-layer OpenEXR (AovExrRenderer), plus the --aov-exr
// batch flag. The packer (AovExrExporter) + per-pass shade (AovView) were already
// tested; these lock the loop that drives them: PosterRenderer.RenderToPixels
// honours DebugAov, the orchestrator writes a readable multi-layer EXR at the
// scene dimensions, and it restores DebugAov afterwards.

using System.IO;
using System.Linq;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Imaging;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class AovExrOrchestrationTests
{
    private static string[] BaseArgs(params string[] extra)
    {
        var head = new[] { "app.exe", "--batch", "--x", "-0.5", "--y", "0", "--zoom", "1", "--out", "o.png" };
        return head.Concat(extra).ToArray();
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
            Relief2DGroundPlane = false,
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

    [Fact]
    public void Parser_Sets_AovExr()
    {
        Assert.True(BatchOptions.TryParse(BaseArgs("--aov-exr"), 2, out var opts, out var err), err);
        Assert.True(opts.AovExr);
    }

    [Fact]
    public void Parser_Rejects_AovExr_In_Video_Mode()
    {
        Assert.False(BatchOptions.TryParse(BaseArgs("--mode", "video", "--aov-exr"), 2, out _, out var err));
        Assert.Contains("image mode", err);
    }

    // The new render-to-pixels entry actually reflects DebugAov: the beauty pass
    // and the normals pass of the same relief scene must differ.
    // LightingFxData is a struct property — reassign the whole struct to change it.
    private static void SetAov(FractalParameters fp, AovView v)
    {
        var l = fp.Lighting; l.DebugAov = v; fp.Lighting = l;
    }

    [Fact]
    public void RenderToPixels_Honors_DebugAov()
    {
        var req = ReliefRequest();

        SetAov(req.FractalParameters, AovView.Beauty);
        var beauty = PosterRenderer.RenderToPixels(req, default, out int w, out int h);

        SetAov(req.FractalParameters, AovView.Normals);
        var normals = PosterRenderer.RenderToPixels(req, default, out _, out _);

        Assert.Equal(96, w);
        Assert.Equal(72, h);
        Assert.True(new System.Collections.Generic.HashSet<uint>(beauty).Count > 1, "beauty non-blank");
        Assert.False(beauty.SequenceEqual(normals), "normals AOV should differ from beauty");
    }

    [Fact]
    public void RenderToFile_Writes_Readable_Exr_And_Restores_Aov()
    {
        var req = ReliefRequest();
        SetAov(req.FractalParameters, AovView.Beauty);   // baseline to restore

        string path = Path.Combine(Path.GetTempPath(), $"ff-aov-orch-{System.Guid.NewGuid():N}.exr");
        try
        {
            var (w, h) = AovExrRenderer.RenderToFile(req, path, default);
            Assert.Equal(96, w);
            Assert.Equal(72, h);

            // The DebugAov toggle is restored to what the caller set.
            Assert.Equal(AovView.Beauty, req.FractalParameters.Lighting.DebugAov);

            // The file is a valid EXR the HDRI reader can load at scene dimensions.
            Assert.True(HdriRegistry.TryLoadFromFile(path, out var img) && img != null);
            Assert.Equal(96, img!.Width);
            Assert.Equal(72, img.Height);
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
