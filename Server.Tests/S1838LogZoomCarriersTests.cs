// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1838 (epic #1428, C3b) — log10 zoom (C3, #1836) through the server request and the
// cluster planners. Checked:
//   • The request round-trips the log past double, writes no new key below it, drops a
//     damaged log on load, and a frame's zoom from its natural log is Exp up to double
//     (the old expression) and the log past it.
//   • Image tiles at 1e500: each tile's zoom is the image's log plus its size ratio, and its
//     exact centre is the image's plus the tile's offset, measured in pixels against a pixel
//     built from decimal text (not the planner's log).
//   • Video frames at 1e500: the planner's natural-log range ends at the target's log
//     (reverse too); up to double it is the old Math.Log expression.
//   • Slides take the log with the zoom they take (slide, else template).

using System;
using System.Collections.Generic;
using System.Text.Json;
using FracturingFog.FFMath;
using FracturingFog.Server.Cluster;
using FracturingFog.Server.Cluster.Protocol;
using FracturingFog.Server.Protocol;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1838LogZoomCarriersTests
{
    private static readonly double Ln10 = Math.Log(10);

    private static RenderRequestDto Deep(double log10, ExactCentre? c = null)
    {
        var r = new RenderRequestDto { Mode = "image", FractalType = "Mandelbrot", Width = 1024, Height = 512, CenterX = -0.5, CenterY = 0 };
        r.SetZoomLog10(log10);
        if (c is { } e) r.SetCentreExact(e);
        return r;
    }

    [Fact]
    public void The_Request_Carries_The_Log_Past_Double_And_Nothing_New_Below()
    {
        var r = Deep(612.5);
        Assert.Equal(double.MaxValue, r.Zoom);
        string json = JsonSerializer.Serialize(r);
        Assert.Contains("\"zoomLog10\":612.5", json);
        var back = JsonSerializer.Deserialize<RenderRequestDto>(json)!;
        Assert.Equal(612.5, back.ZoomLog10Value());
        Assert.Equal(double.MaxValue, back.Zoom);

        var shallow = Deep(50);
        Assert.Null(shallow.ZoomLog10);
        Assert.Equal(Math.Pow(10, 50), shallow.Zoom);
        Assert.DoesNotContain("zoomLog10", JsonSerializer.Serialize(shallow));

        // Damaged: a log not past double is dropped, zoom rules; a valid one rewrites zoom.
        back = JsonSerializer.Deserialize<RenderRequestDto>("{\"zoom\":1e20,\"zoomLog10\":200}")!;
        Assert.Null(back.ZoomLog10);
        Assert.Equal(1e20, back.Zoom);
        back = JsonSerializer.Deserialize<RenderRequestDto>("{\"zoom\":5,\"zoomLog10\":400}")!;
        Assert.Equal(double.MaxValue, back.Zoom);
        Assert.Equal(400, back.ZoomLog10Value());
    }

    [Fact]
    public void A_Frame_Zoom_From_Its_Natural_Log_Is_Exp_Up_To_Double_And_The_Log_Past_It()
    {
        var r = new RenderRequestDto();
        foreach (double ln in new[] { Math.Log(0.5), 3.0, Math.Log(1e50), 700.0 })
        {
            r.SetZoomLn(ln);
            Assert.Equal(Math.Exp(ln), r.Zoom);   // the old per-frame expression
            Assert.Null(r.ZoomLog10);
        }
        r.SetZoomLn(1000 * Ln10);
        Assert.Equal(double.MaxValue, r.Zoom);
        Assert.Equal(1000, r.ZoomLog10Value(), 10);
        r.SetZoomLn(5.0);   // back in range: the log goes
        Assert.Null(r.ZoomLog10);
    }

    [Fact]
    public void Image_Tiles_Past_Double_Get_The_Log_Zoom_And_Their_Own_Exact_Centre()
    {
        int prec = BigFloat.PrecisionForZoom(500);
        Assert.True(ExactCentre.TryParse(
            "-1.7400623825793399052208" + new string('7', 480) + "3",
            "0.0281858209101077" + new string('4', 480) + "9", out var c, prec));
        var req = Deep(500, c);
        var plan = TilePlanner.PlanImage(req, 256);
        Assert.True(plan.TileCount > 1);

        int p = prec + 64;
        var pixel = BigFloat.Parse("3.5e-500", p) / BigFloat.FromDouble(1024, p);
        foreach (var t in plan.Tiles)
        {
            double tileMax = Math.Max(t.Render.Width, t.Render.Height);
            Assert.Equal(500 + Math.Log10(1024 / tileMax), t.Render.ZoomLog10Value(), 12);
            Assert.Equal(double.MaxValue, t.Render.Zoom);

            var e = t.Render.CentreExact()!.Value;
            double ox = t.OffsetX + t.Render.Width * 0.5 - 512;
            double oy = t.OffsetY + t.Render.Height * 0.5 - 256;
            Assert.Equal(ox, BigFloat.Div(BigFloat.Sub(e.Re, c.Re, p), pixel, p).ToDouble(), 6);
            Assert.Equal(oy, BigFloat.Div(BigFloat.Sub(e.Im, c.Im, p), pixel, p).ToDouble(), 6);
        }

        // Shallow, no exact centre: the tile zoom is the old double expression, no log.
        var shallow = Deep(20);
        foreach (var t in TilePlanner.PlanImage(shallow, 256).Tiles)
        {
            Assert.Equal(shallow.Zoom!.Value * (1024 / (double)Math.Max(t.Render.Width, t.Render.Height)), t.Render.Zoom);
            Assert.Null(t.Render.ZoomLog10);
            Assert.Null(t.Render.CenterExactX);
        }
    }

    [Fact]
    public void Video_Frames_Range_To_The_Target_Log_Past_Double()
    {
        var v = Deep(500);
        v.Mode = "video"; v.VideoFps = 10; v.VideoSeconds = 1; v.VideoStartZoom = 0.5;
        var plan = FramePlanner.PlanVideo(v, framesPerTileHint: 2);
        foreach (var t in plan.Tiles)
        {
            Assert.Equal(Math.Log(0.5), t.FrameRange!.LogStartZoom);
            Assert.Equal(500 * Ln10, t.FrameRange!.LogStartZoom + t.FrameRange.LogZoomDelta, 9);
        }
        v.VideoReverse = true;
        var rev = FramePlanner.PlanVideo(v, framesPerTileHint: 2).Tiles[0].FrameRange!;
        Assert.Equal(500 * Ln10, rev.LogStartZoom, 9);
        Assert.Equal(Math.Log(0.5), rev.LogStartZoom + rev.LogZoomDelta, 9);

        // Up to double: the old Math.Log expressions, exactly.
        var s = Deep(50);
        s.Mode = "video"; s.VideoFps = 10; s.VideoSeconds = 1; s.VideoStartZoom = 0.5;
        var r = FramePlanner.PlanVideo(s, framesPerTileHint: 2).Tiles[0].FrameRange!;
        Assert.Equal(Math.Log(0.5), r.LogStartZoom);
        Assert.Equal(Math.Log(s.Zoom!.Value) - Math.Log(0.5), r.LogZoomDelta);
    }

    [Fact]
    public void Slides_Take_The_Log_With_The_Zoom_They_Take()
    {
        var template = Deep(700);
        template.Mode = "slideshow";
        var deepSlide = Deep(450);
        var plainSlide = new RenderRequestDto { Zoom = 1e30 };
        var noZoom = new RenderRequestDto();
        var submit = new JobSubmitDto
        {
            Request = template,
            Slides = new List<RenderRequestDto> { deepSlide, plainSlide, noZoom },
        };
        var plan = SlideshowPlanner.PlanSlideshow(submit);
        Assert.Equal(450, plan.Tiles[0].Render.ZoomLog10Value());
        Assert.Null(plan.Tiles[1].Render.ZoomLog10);           // the slide's plain zoom, not the template's log
        Assert.Equal(1e30, plan.Tiles[1].Render.Zoom);
        Assert.Equal(700, plan.Tiles[2].Render.ZoomLog10Value()); // the template's, with its zoom
    }
}
