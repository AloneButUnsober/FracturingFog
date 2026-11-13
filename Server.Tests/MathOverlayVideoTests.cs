// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1746 — the pinned math overlay on every video frame. The video loop drives
// MathOverlayFrames around each frame's calculator; these tests drive it the same way and
// check each frame against the still renderer for that frame's view (pixels identical, CPU).

using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using FracturingFog.Batch;
using FracturingFog.Imaging;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Lighting;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MathOverlayVideoTests
{
    private const int W = 96, H = 64;

    private static PosterRequest Still(FractalType t, FractalParameters fp, MathOverlay3DSpec? spec) => new()
    {
        FractalType = t, Width = W, Height = H, CenterX = 0, CenterY = 0, Zoom = 1, MaxIterations = 64,
        ColorMap = ColorPalette.BuiltIns[0], FractalParameters = fp, MathOverlay3D = spec,
    };

    /// <summary>One video frame as RenderOneFrame makes it: the capture calculator, the overlay
    /// hooks around its Calculate, then the draw on the finished frame.</summary>
    private static uint[] VideoFrame(MathOverlayFrames ov, FractalType t, FractalParameters fp)
    {
        var alt = PosterRenderer.BuildCaptureCalculator(Still(t, fp, null))!;
        ov.BeforeCalculate(alt);
        alt.Calculate(CancellationToken.None);
        ov.AfterCalculate(alt);
        var frame = (uint[])alt.ColorBuffer.Clone();
        ov.Draw(frame, W, H, t, fp, 0, 0, 1);
        return frame;
    }

    [Theory]
    [InlineData(FractalType.Mandelbulb)]
    [InlineData(FractalType.Kifs)]
    public void AnOrbitVideosFrames_EachEqualTheStillOfTheirCamera(FractalType t)
    {
        var spec = new MathOverlay3DSpec(0.6, 0.4, 0.5, 20);
        var ov = new MathOverlayFrames(null, spec);
        uint[]? previous = null;
        foreach (double theta in new[] { 0.2, 1.1, 2.4 })   // --orbit turns theta frame by frame
        {
            var fp = new FractalParameters { Lighting = LightingFxData.CreateDefault() };
            RegionFractalParams.FromKeyValues(new[] { new System.Collections.Generic.KeyValuePair<string, string>(
                "Cam3DTheta", theta.ToString("R", System.Globalization.CultureInfo.InvariantCulture)) }, t, out _)!.ApplyTo(fp);
            var frame = VideoFrame(ov, t, fp);
            var still = PosterRenderer.RenderDisplayPixels(Still(t, fp, spec), CancellationToken.None, out _, out _);
            Assert.True(still.SequenceEqual(frame), $"{t} theta {theta}: {still.Zip(frame).Count(p => p.First != p.Second)} px differ");
            var bare = PosterRenderer.RenderDisplayPixels(Still(t, fp, null), CancellationToken.None, out _, out _);
            Assert.True(frame.Zip(bare).Count(p => p.First != p.Second) > 50, "nothing drawn");
            if (previous != null) Assert.False(previous.SequenceEqual(frame));   // the view moved
            previous = frame;
        }
        Assert.Equal(3, ov.FramesDrawn);
        Assert.Null(ov.SkipReason);
    }

    [Fact]
    public void A2DZoomVideo_DrawsEachFrameWithItsOwnZoom()
    {
        var spec = new MathOverlaySpec(-0.1226, 0.7449, 30);
        var ov = new MathOverlayFrames(spec, null);
        foreach (double zoom in new[] { 1.0, 3.0, 9.0 })
        {
            var frame = new uint[W * H];
            ov.Draw(frame, W, H, FractalType.Mandelbrot, new FractalParameters(), -0.5, 0, zoom);
            var expect = new uint[W * H];
            MathOverlayRenderer.Draw(expect, W, H, -0.5, 0, zoom, FractalType.Mandelbrot, Complex.Zero, spec, pinned: true);
            Assert.Equal(expect, frame);
        }
        var z1 = new uint[W * H]; ov.Draw(z1, W, H, FractalType.Mandelbrot, new FractalParameters(), -0.5, 0, 1);
        var z9 = new uint[W * H]; ov.Draw(z9, W, H, FractalType.Mandelbrot, new FractalParameters(), -0.5, 0, 9);
        Assert.NotEqual(z1, z9);
        Assert.Equal(5, ov.FramesDrawn);
    }

    [Fact]
    public void ACalculatorWithoutACamera_ClearsTheCapture_SoAStaleCameraIsNeverUsed()
    {
        var ov = new MathOverlayFrames(null, new MathOverlay3DSpec(0.1, 0.2, 0.3));
        var fp = new FractalParameters { Lighting = LightingFxData.CreateDefault() };
        VideoFrame(ov, FractalType.Mandelbulb, fp);
        Assert.NotNull(ov.Capture3D!.Camera);
        Assert.NotNull(ov.Capture3D.Depth);   // depth was requested
        ov.AfterCalculate(null);
        Assert.Null(ov.Capture3D.Camera);
        Assert.Null(ov.Capture3D.Depth);
        var frame = new uint[W * H];
        ov.Draw(frame, W, H, FractalType.Mandelbulb, fp, 0, 0, 1);
        Assert.All(frame, v => Assert.Equal(0u, v));
        Assert.Contains("no camera", ov.SkipReason);
    }

    [Fact]
    public void AStereoFrame_IsSkipped_WithAReason()
    {
        var ov = new MathOverlayFrames(new MathOverlaySpec(0, 0), null);
        ov.Skip("stereo frames are not drawn on");
        Assert.Equal("stereo frames are not drawn on", ov.SkipReason);
        var off = new MathOverlayFrames(null, null);
        off.Skip("x");
        Assert.Null(off.SkipReason);
        Assert.False(off.Active);
    }

    private static string[] Args(params string[] extra)
        => new[] { "app.exe", "--batch", "--x", "0", "--y", "0", "--zoom", "1", "--out", "o.mp4" }.Concat(extra).ToArray();

    [Fact]
    public void VideoTakesBothFlags_SlideshowAndSceneRefuseThem()
    {
        Assert.True(BatchOptions.TryParse(Args("--fractal", "Mandelbulb", "--mode", "video", BatchFlags.MathOverlay3D, "0.1,0.2,0.3"), 2, out var v3, out var e3), e3);
        Assert.NotNull(v3.MathOverlay3D);
        Assert.True(BatchOptions.TryParse(Args("--mode", "video", BatchFlags.MathOverlay, "-0.1,0.7"), 2, out var v2, out var e2), e2);
        Assert.NotNull(v2.MathOverlay);
        foreach (var flag in new[] { (BatchFlags.MathOverlay, "-0.1,0.7"), (BatchFlags.MathOverlay3D, "0.1,0.2,0.3") })
        {
            Assert.False(BatchOptions.TryParse(Args("--mode", "slideshow", flag.Item1, flag.Item2), 2, out _, out var err), flag.Item1);
            Assert.Contains(flag.Item1, err);
            Assert.Contains("slideshow or scene", err);
        }
        foreach (var name in new[] { BatchFlags.MathOverlay, BatchFlags.MathOverlay3D })
        {
            var s = BatchFlagCatalog.Find(name)!;
            Assert.Equal(BatchModes.Image | BatchModes.Video, s.Modes);
            Assert.True(BatchFlagCatalog.AppliesIn(s, BatchMode.Video));
            Assert.False(BatchFlagCatalog.AppliesIn(s, BatchMode.Slideshow));
        }
    }
}
