// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1256 — export guard. An 8-hour scene export silently rendered a solid block
// (#1255). On a relief shot's first frame the scene renderer now re-traces the frame
// at a small size and compares how much of each frame is the terrain box wall; a
// large excess asks (UI) or warns (headless) before the rest of the render.
//
// The positive case reproduces the #1255 block by switching the display-res trace cap
// off on the test thread (HeightfieldRaymarch2D.SetTestDisableLegacyCap) — the guard
// must fire and stop the render. With the cap (the fix) and on a floor-field plate
// (Mandelbrot's established look, collapsed the same at every size) it must not.
//
// Runs under the test data-root redirect (FractalRegionLibraryCollection).

using System;
using System.IO;
using System.Threading;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Export;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

[Collection(FractalRegionLibraryCollection.Name)]
public sealed class SceneReliefCollapseGuardTests
{
    // The #1255 failure: "Tricorn (Generated) - The Horn" — a family with no hi-res
    // twin (display-res field), seen from a low camera. Fine at a small size; the
    // uncapped 720p trace collapses into the solid block.
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

    // A Mandelbrot floor-field plate: collapsed the same at every size (the
    // established default look).
    private static FractalRegion MandelbrotPlate(string name) => new()
    {
        Name = name,
        FractalType = FractalType.Mandelbrot,
        CenterX = -0.75,
        CenterY = 0.0,
        Zoom = 1,
        Iterations = 200,
        Relief3D = new Relief3DSettings
        {
            Enabled = true,
            Raymarch = true,
            HeightScale = 1.0,
            Supersample = 1,
            HiResField = true,
        },
    };

    private static SceneData Scene(FractalRegion region) => new()
    {
        Name = "FF-1256-CollapseGuard",
        Shots =
        {
            new SceneShot
            {
                Name = "Guard",
                RegionName = region.Name,
                FractalType = region.FractalType,
                DurationSeconds = 0.5,
                Transition = SceneTransitionKind.Cut,
            },
        },
    };

    // Render the scene at 1280×720 (short axis above the check's floor), stopping when
    // the guard asks. Returns the guard's warning (null = never asked) and the result.
    private static (string? Warning, SceneVideoResult Result) Export(
        Func<string, FractalRegion> makeRegion, bool disableCap)
    {
        var region = makeRegion("FF-1256-" + Guid.NewGuid().ToString("N"));
        string name = region.Name;
        var lib = FractalRegionLibrary.Instance;
        string outDir = Path.Combine(Path.GetTempPath(), "FracturingFog", "1256-guard-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        string? warning = null;
        try
        {
            Assert.True(lib.AddUserRegion(region));
            HeightfieldRaymarch2D.SetTestDisableLegacyCap(disableCap);
            var opts = new SceneVideoOptions
            {
                Width = 1280, Height = 720,
                Settings = new SceneRenderSettings { Fps = 2, MotionBlurSubframes = 1 },
                OutputPath = outDir,
                KeepFrames = true,
                ReliefCollapseConfirm = w => { warning = w; return false; },   // stop
            };
            var result = SceneVideoRenderer.Render(Scene(region), opts, null, CancellationToken.None);
            return (warning, result);
        }
        finally
        {
            HeightfieldRaymarch2D.SetTestDisableLegacyCap(false);
            lib.RemoveUserRegion(name);
            try { Directory.Delete(outDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void CollapsedExport_IsCaughtOnTheFirstFrame_AndStops()
    {
        var (warning, result) = Export(Horn, disableCap: true);
        Assert.NotNull(warning);
        Assert.False(result.Ok);
        Assert.Equal(0, result.FramesWritten);   // stopped before writing the frame
        Assert.StartsWith("Scene export stopped:", result.Message);
    }

    [Fact]
    public void CappedDisplayResExport_PassesTheGuard()
    {
        var (warning, result) = Export(Horn, disableCap: false);
        Assert.Null(warning);
        Assert.True(result.FramesWritten > 0, result.Message);
    }

    [Fact]
    public void FloorFieldPlate_PassesTheGuard()
    {
        // Collapsed the same at every size — the established look, not a regression.
        var (warning, result) = Export(MandelbrotPlate, disableCap: false);
        Assert.Null(warning);
        Assert.True(result.FramesWritten > 0, result.Message);
    }

    [Fact]
    public void Warning_OnlyForAMarginAboveTheSmallTrace()
    {
        static HeightfieldRaymarch2D.ReliefTraceStats S(double wall) => new(1_000_000, (long)(wall * 1_000_000));
        Assert.Null(SceneVideoRenderer.ReliefCollapseWarning(S(0.214), S(0.214), 1920, 1080, "shot 1"));   // plate
        Assert.Null(SceneVideoRenderer.ReliefCollapseWarning(S(0.055), S(0.038), 1920, 1080, "shot 1"));   // fixed
        Assert.NotNull(SceneVideoRenderer.ReliefCollapseWarning(S(0.27), S(0.038), 1920, 1080, "shot 1")); // block
        // No CPU trace on either side → nothing to judge.
        Assert.Null(SceneVideoRenderer.ReliefCollapseWarning(default, S(0.0), 1920, 1080, "shot 1"));
        Assert.Null(SceneVideoRenderer.ReliefCollapseWarning(S(0.5), default, 1920, 1080, "shot 1"));
    }
}
