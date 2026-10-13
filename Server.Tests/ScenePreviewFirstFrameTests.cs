// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1257 — scene export "Preview first frame". The preview must BE the export's first
// frame (same SceneVideoRenderer frame path), so it is checked against the PNG the
// real export writes, pixel for pixel. It writes no output, and a relief collapse
// (#1256) comes back as a warning instead of a question.
//
// Runs under the test data-root redirect (FractalRegionLibraryCollection).

using System;
using System.IO;
using System.Threading;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Export;
using FracturingFog.Models;
using FracturingFog.Render;
using FracturingFog.Rendering.Lighting;
using SkiaSharp;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(FractalRegionLibraryCollection.Name)]
public sealed class ScenePreviewFirstFrameTests
{
    // The #1255 region (display-res field, low camera).
    private static FractalRegion Horn(string name) => new()
    {
        Name = name,
        FractalType = FractalType.GeneratedTricorn,
        CenterX = 0.48662691702578137,
        CenterY = -0.9033120340702473,
        Zoom = 2.4014227121108815,
        Iterations = 304,
        Relief3D = new Relief3DSettings
        {
            Enabled = true,
            Raymarch = true,
            HeightScale = 1.0,
            CameraAzimuthDeg = 1.88,
            CameraElevationDeg = 17.19,
            CameraZoom = 0.386,
            EdgeFade = 0.1,
            Supersample = 1,
        },
    };

    private static SceneData Scene(FractalRegion r) => new()
    {
        Name = "FF-1257-Preview",
        Shots =
        {
            new SceneShot
            {
                Name = "Preview",
                RegionName = r.Name,
                FractalType = r.FractalType,
                DurationSeconds = 1.0,
                Transition = SceneTransitionKind.Cut,
                // A moving camera, so frame 1 is a specific pose, not any pose.
                Camera = new CameraTrack
                {
                    Keys =
                    {
                        new CameraKey(0, 2.6, 0, 0.3),
                        new CameraKey(1, 1.2, 2, 0.3),
                    },
                },
            },
        },
    };

    private static SceneVideoOptions Options(string outDir, bool preview) => new()
    {
        Width = 1280, Height = 720,
        Settings = new SceneRenderSettings { Fps = 4, MotionBlurSubframes = 1 },
        OutputPath = outDir,
        KeepFrames = true,
        PreviewFirstFrame = preview,
    };

    private static uint[] ReadPng(string path, int w, int h)
    {
        using var bmp = SKBitmap.Decode(path);
        Assert.Equal((w, h), (bmp.Width, bmp.Height));
        var px = new uint[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = bmp.GetPixel(x, y);
                px[y * w + x] = 0xFF000000u | ((uint)c.Red << 16) | ((uint)c.Green << 8) | c.Blue;
            }
        return px;
    }

    private static T WithRegion<T>(Func<FractalRegion, string, T> body)
    {
        var region = Horn("FF-1257-" + Guid.NewGuid().ToString("N"));
        var lib = FractalRegionLibrary.Instance;
        string outDir = Path.Combine(Path.GetTempPath(), "FracturingFog", "1257-preview-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        try
        {
            Assert.True(lib.AddUserRegion(region));
            return body(region, outDir);
        }
        finally
        {
            lib.RemoveUserRegion(region.Name);
            try { Directory.Delete(outDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Preview_IsTheExportsFirstFrame_AndWritesNothing()
    {
        WithRegion((region, outDir) =>
        {
            var scene = Scene(region);
            var preview = SceneVideoRenderer.Render(scene, Options(outDir, preview: true), null, CancellationToken.None);
            Assert.True(preview.Ok, preview.Message);
            Assert.NotNull(preview.PreviewPixels);
            Assert.Equal((1280, 720), (preview.PreviewWidth, preview.PreviewHeight));
            Assert.Null(preview.VideoPath);
            Assert.Null(preview.FrameFolder);
            Assert.Empty(Directory.GetFileSystemEntries(outDir));   // no output file

            var export = SceneVideoRenderer.Render(scene, Options(outDir, preview: false), null, CancellationToken.None);
            Assert.True(export.FramesWritten > 1, export.Message);
            Assert.NotNull(export.FrameFolder);
            var first = ReadPng(Path.Combine(export.FrameFolder!, "frame_000001.png"), 1280, 720);
            var second = ReadPng(Path.Combine(export.FrameFolder!, "frame_000002.png"), 1280, 720);
            Assert.Equal(first, preview.PreviewPixels);
            // Sensitivity: the camera moves, so a different frame would not match.
            Assert.NotEqual(second, preview.PreviewPixels);
            try { Directory.Delete(export.FrameFolder!, recursive: true); } catch { /* best effort */ }
            return 0;
        });
    }

    [Fact]
    public void Preview_ReportsACollapse_WithoutAsking()
    {
        WithRegion((region, outDir) =>
        {
            bool asked = false;
            var opts = Options(outDir, preview: true);
            opts.ReliefCollapseConfirm = _ => { asked = true; return false; };
            HeightfieldRaymarch2D.SetTestDisableLegacyCap(true);   // reproduce the #1255 block
            try
            {
                var r = SceneVideoRenderer.Render(Scene(region), opts, null, CancellationToken.None);
                Assert.True(r.Ok);
                Assert.NotNull(r.PreviewPixels);
                Assert.False(asked);
                Assert.Contains("collapsed", r.Message);
            }
            finally { HeightfieldRaymarch2D.SetTestDisableLegacyCap(false); }
            return 0;
        });
    }
}
