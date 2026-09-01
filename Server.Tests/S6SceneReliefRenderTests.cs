// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// S6 (#408) — Relief 3D on the offline SceneVideoRenderer. The scene renderer
// built a PosterRequest but rendered it through the flat capture calculator
// (PosterRenderer.BuildCaptureCalculator), so a shot whose region carried a
// Relief 3D snapshot rendered FLAT — the oblique raymarch was dropped. It now
// diverts to PosterRenderer.RenderToPixels (the composed relief+froxel buffer)
// when the resolved shot params enable the raymarch.
//
// Two locks:
//   1. ResolveShot on a relief region yields raymarch-enabled base params (the
//      exact condition RenderShotFrame keys the new branch on).
//   2. A one-shot scene rendered with that region produces a frame that DIFFERS
//      from the same scene rendered flat — i.e. relief is actually applied.
//
// Runs under the test data-root redirect (FractalRegionLibraryCollection).

using System;
using System.IO;
using System.Reflection;
using System.Threading;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Export;
using FracturingFog.Models;
using SkiaSharp;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(FractalRegionLibraryCollection.Name)]
public sealed class S6SceneReliefRenderTests
{
    private static readonly MethodInfo ResolveShot =
        typeof(SceneVideoRenderer).GetMethod(
            "ResolveShot", BindingFlags.NonPublic | BindingFlags.Static)!;

    // A shallow Mandelbrot region; relief == a raymarch Relief3D snapshot when on.
    private static FractalRegion Region(string name, bool relief)
    {
        var r = new FractalRegion
        {
            Name = name,
            FractalType = FractalType.Mandelbrot,
            CenterX = -0.75,
            CenterY = 0.0,
            Zoom = 1.0,
            Iterations = 200,
        };
        if (relief)
            r.Relief3D = new Relief3DSettings
            {
                Enabled = true,
                Raymarch = true,          // oblique 3D — the path the scene dropped
                HeightScale = 1.0,
                CameraElevationDeg = 45.0,
                Supersample = 1,          // keep the test render cheap
                HiResField = false,       // ditto — skip the hi-res field pass
            };
        return r;
    }

    private static SceneData OneShotScene(string regionName) => new()
    {
        Name = "FF-S6-ReliefScene",
        Shots =
        {
            new SceneShot
            {
                RegionName = regionName,
                FractalType = FractalType.Mandelbrot,
                DurationSeconds = 1.0,
                Transition = SceneTransitionKind.Cut,
            },
        },
    };

    private static uint[] RenderFirstFrame(SceneData scene, int w, int h)
    {
        string outDir = Path.Combine(Path.GetTempPath(), "FracturingFog",
            "s6-scene-relief-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        var opts = new SceneVideoOptions
        {
            Width = w, Height = h,
            Settings = new SceneRenderSettings { Fps = 2, MotionBlurSubframes = 1 },
            OutputPath = outDir,
            KeepFrames = true,            // frame folder survives regardless of ffmpeg
        };

        var result = SceneVideoRenderer.Render(scene, opts, null, CancellationToken.None);
        Assert.True(result.FramesWritten > 0, result.Message);
        Assert.NotNull(result.FrameFolder);

        string frame = Path.Combine(result.FrameFolder!, "frame_000001.png");
        Assert.True(File.Exists(frame), $"missing {frame}");

        using var bmp = SKBitmap.Decode(frame);
        Assert.NotNull(bmp);
        var px = new uint[bmp.Width * bmp.Height];
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                px[y * bmp.Width + x] =
                    0xFF000000u | ((uint)c.Red << 16) | ((uint)c.Green << 8) | c.Blue;
            }
        return px;
    }

    [Fact]
    public void Relief_Region_Resolves_To_Raymarch_Enabled_Params()
    {
        var lib = FractalRegionLibrary.Instance;
        string name = $"FF-S6ReliefResolve-{Guid.NewGuid():N}";
        try
        {
            Assert.True(lib.AddUserRegion(Region(name, relief: true)));
            var shot = new SceneShot { RegionName = name };
            object resolved = ResolveShot.Invoke(null, new object[] { shot })!;
            var bp = (FractalParameters)resolved.GetType()
                .GetField("BaseParams")!.GetValue(resolved)!;
            Assert.True(bp.Relief2DEnabled);
            Assert.True(bp.Relief2DRaymarch);
        }
        finally { lib.RemoveUserRegion(name); }
    }

    [Fact]
    public void Scene_With_Relief_Region_Renders_Differently_Than_Flat()
    {
        const int W = 96, H = 64;
        var lib = FractalRegionLibrary.Instance;
        string flatName = $"FF-S6Flat-{Guid.NewGuid():N}";
        string reliefName = $"FF-S6Relief-{Guid.NewGuid():N}";
        try
        {
            Assert.True(lib.AddUserRegion(Region(flatName, relief: false)));
            Assert.True(lib.AddUserRegion(Region(reliefName, relief: true)));

            var flat = RenderFirstFrame(OneShotScene(flatName), W, H);
            var relief = RenderFirstFrame(OneShotScene(reliefName), W, H);

            Assert.Equal(flat.Length, relief.Length);

            // Relief is actually applied → the frames differ, and the relief
            // frame is not a black wash.
            int diff = 0, reliefNonBlack = 0;
            for (int i = 0; i < flat.Length; i++)
            {
                if (flat[i] != relief[i]) diff++;
                if ((relief[i] & 0x00FFFFFF) != 0) reliefNonBlack++;
            }
            Assert.True(diff > flat.Length / 20,
                $"relief frame barely differs from flat ({diff}/{flat.Length} px)");
            Assert.True(reliefNonBlack > 0, "relief frame is all black");
        }
        finally { lib.RemoveUserRegion(flatName); lib.RemoveUserRegion(reliefName); }
    }
}
