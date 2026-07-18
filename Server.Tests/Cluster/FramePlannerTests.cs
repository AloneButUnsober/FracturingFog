// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server.Tests/Cluster/FramePlannerTests.cs
// D-4a — FramePlanner produces a frame-range tile plan for a video
// RenderRequestDto. Per-frame zoom math is verified against the same
// smoothstep used by BatchRenderer.RenderVideo so a cluster video is
// frame-for-frame identical to a single-server video.

using System;
using System.Linq;

using FracturingFog.Server.Cluster;
using FracturingFog.Server.Protocol;
using Xunit;

namespace FracturingFog.Server.Tests.Cluster;

public sealed class FramePlannerTests
{
    private static RenderRequestDto VideoReq(
        double seconds = 2.0, int fps = 30,
        double startZoom = 0.5, double endZoom = 8.0,
        int width = 320, int height = 180,
        bool reverse = false)
    => new()
    {
        Mode            = "video",
        FractalType     = "Mandelbrot",
        Width           = width,
        Height          = height,
        CenterX         = -0.75,
        CenterY         = 0.0,
        Zoom            = endZoom,
        VideoSeconds    = seconds,
        VideoFps        = fps,
        VideoStartZoom  = startZoom,
        VideoReverse    = reverse,
    };

    [Fact]
    public void PlanVideo_Computes_Total_Frames_From_Seconds_Times_Fps()
    {
        var plan = FramePlanner.PlanVideo(VideoReq(seconds: 2.0, fps: 30));
        Assert.Equal("video", plan.Mode);
        Assert.Equal(60, plan.TotalFrames);
    }

    [Fact]
    public void PlanVideo_Splits_Into_Frame_Range_Tiles()
    {
        var plan = FramePlanner.PlanVideo(VideoReq(seconds: 2.0, fps: 30), framesPerTileHint: 20);
        // 60 frames / 20 per tile = 3 tiles
        Assert.Equal(3, plan.TileCount);
        Assert.All(plan.Tiles, t => Assert.NotNull(t.FrameRange));
        Assert.Equal(0,  plan.Tiles[0].FrameRange!.StartFrame);
        Assert.Equal(20, plan.Tiles[0].FrameRange!.EndFrame);
        Assert.Equal(20, plan.Tiles[1].FrameRange!.StartFrame);
        Assert.Equal(40, plan.Tiles[1].FrameRange!.EndFrame);
        Assert.Equal(40, plan.Tiles[2].FrameRange!.StartFrame);
        Assert.Equal(60, plan.Tiles[2].FrameRange!.EndFrame);
    }

    [Fact]
    public void PlanVideo_Last_Tile_Carries_Remainder_Frames()
    {
        // 70 frames, 30 per tile → tiles 30+30+10.
        var plan = FramePlanner.PlanVideo(
            VideoReq(seconds: 70.0 / 30.0, fps: 30),
            framesPerTileHint: 30);
        Assert.Equal(3, plan.TileCount);
        Assert.Equal(30, plan.Tiles[0].FrameRange!.EndFrame - plan.Tiles[0].FrameRange!.StartFrame);
        Assert.Equal(30, plan.Tiles[1].FrameRange!.EndFrame - plan.Tiles[1].FrameRange!.StartFrame);
        Assert.Equal(10, plan.Tiles[2].FrameRange!.EndFrame - plan.Tiles[2].FrameRange!.StartFrame);
    }

    [Fact]
    public void PlanVideo_All_Tiles_Carry_Identical_Total_Frames_And_LogZoom_Constants()
    {
        var plan = FramePlanner.PlanVideo(VideoReq(seconds: 2.0, fps: 30), framesPerTileHint: 20);
        var first = plan.Tiles[0].FrameRange!;
        foreach (var t in plan.Tiles)
        {
            var fr = t.FrameRange!;
            Assert.Equal(first.TotalFrames, fr.TotalFrames);
            Assert.Equal(first.Fps, fr.Fps);
            Assert.Equal(first.LogStartZoom, fr.LogStartZoom);
            Assert.Equal(first.LogZoomDelta, fr.LogZoomDelta);
        }
    }

    [Fact]
    public void PlanVideo_LogZoom_Mirrors_BatchRenderer_Forward()
    {
        var plan = FramePlanner.PlanVideo(VideoReq(startZoom: 0.5, endZoom: 8.0));
        var fr = plan.Tiles[0].FrameRange!;
        Assert.Equal(Math.Log(0.5), fr.LogStartZoom, 12);
        Assert.Equal(Math.Log(8.0) - Math.Log(0.5), fr.LogZoomDelta, 12);
    }

    [Fact]
    public void PlanVideo_Reverse_Swaps_Start_And_End_Zoom()
    {
        var plan = FramePlanner.PlanVideo(
            VideoReq(startZoom: 0.5, endZoom: 8.0, reverse: true));
        var fr = plan.Tiles[0].FrameRange!;
        // Reverse: start at end, end at start.
        Assert.Equal(Math.Log(8.0), fr.LogStartZoom, 12);
        Assert.Equal(Math.Log(0.5) - Math.Log(8.0), fr.LogZoomDelta, 12);
    }

    [Fact]
    public void PlanVideo_Even_Snaps_Output_Dims()
    {
        // 321 -> 320, 181 -> 180
        var plan = FramePlanner.PlanVideo(VideoReq(width: 321, height: 181));
        Assert.Equal(320, plan.ImageWidth);
        Assert.Equal(180, plan.ImageHeight);
        // Each tile's Render dims match the snapped output.
        Assert.All(plan.Tiles, t =>
        {
            Assert.Equal(320, t.Render.Width);
            Assert.Equal(180, t.Render.Height);
        });
    }

    [Fact]
    public void PlanVideo_Per_Frame_Template_Suppresses_Decorations()
    {
        var plan = FramePlanner.PlanVideo(VideoReq());
        Assert.All(plan.Tiles, t => Assert.True(t.Render.SuppressDecorations));
        Assert.All(plan.Tiles, t => Assert.Equal("image", t.Render.Mode));
        Assert.All(plan.Tiles, t => Assert.Equal("inline", t.Render.ReturnMode));
    }

    [Fact]
    public void PlanVideo_Refuses_Non_Video_Mode()
    {
        var req = VideoReq();
        req.Mode = "image";
        Assert.Throws<ArgumentException>(() => FramePlanner.PlanVideo(req));
    }

    [Fact]
    public void PlanVideo_Refuses_Too_Few_Total_Frames()
    {
        // 0.01s * 30fps = 0 frames (rounded) — below MinTotalFrames.
        var req = VideoReq(seconds: 0.01, fps: 30);
        Assert.Throws<ArgumentException>(() => FramePlanner.PlanVideo(req));
    }

    [Fact]
    public void PlanVideo_Refuses_Too_Many_Total_Frames()
    {
        // 700s * 30fps = 21000 > MaxTotalFrames (18000).
        var req = VideoReq(seconds: 700.0, fps: 30);
        Assert.Throws<ArgumentException>(() => FramePlanner.PlanVideo(req));
    }

    [Fact]
    public void ResolveFramesPerTile_Defaults_When_Hint_Zero()
        => Assert.Equal(FramePlanner.DefaultFramesPerTile, FramePlanner.ResolveFramesPerTile(0));

    [Fact]
    public void ResolveFramesPerTile_Treats_Non_Positive_As_Default()
        => Assert.Equal(FramePlanner.DefaultFramesPerTile, FramePlanner.ResolveFramesPerTile(-5));

    [Fact]
    public void ResolveFramesPerTile_Clamps_Above_Max()
        => Assert.Equal(FramePlanner.MaxFramesPerTile, FramePlanner.ResolveFramesPerTile(int.MaxValue));

    [Fact]
    public void ResolveFramesPerTile_Clamps_Below_Min()
    {
        // Hint of 0 would be "default" — anything > 0 below min clamps up.
        // (No "0" case for "use min" — default is sentinel.)
        Assert.Equal(FramePlanner.MinFramesPerTile, FramePlanner.ResolveFramesPerTile(1));
    }

    [Fact]
    public void PlanVideo_Frame_Zoom_Matches_BatchRenderer_Smoothstep()
    {
        // Reproduce the exact math from BatchRenderer.RenderVideo and
        // compare against what a worker would compute from the planner's
        // tile metadata.
        int totalFrames = 60;
        double startZoom = 0.5;
        double endZoom   = 8.0;
        var plan = FramePlanner.PlanVideo(
            VideoReq(seconds: 2.0, fps: 30, startZoom: startZoom, endZoom: endZoom));

        Assert.Equal(totalFrames, plan.TotalFrames);

        double logZ0 = Math.Log(startZoom);
        double logZ1 = Math.Log(endZoom);

        // Pick a middle frame from the second tile and verify both sides
        // compute the same zoom.
        var midTile = plan.Tiles[plan.TileCount / 2];
        int sampleFrame = midTile.FrameRange!.StartFrame + 5;

        double t  = (double)sampleFrame / (totalFrames - 1);
        double te = t * t * (3.0 - 2.0 * t);
        double expectedZoom = Math.Exp(logZ0 + (logZ1 - logZ0) * te);

        // Worker recomputes from FrameRange-provided log constants.
        var fr = midTile.FrameRange!;
        double workerZoom = Math.Exp(fr.LogStartZoom + fr.LogZoomDelta * te);

        Assert.Equal(expectedZoom, workerZoom, 12);
    }
}
