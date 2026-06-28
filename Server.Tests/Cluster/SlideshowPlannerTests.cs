// Server.Tests/Cluster/SlideshowPlannerTests.cs
// D-4c — SlideshowPlanner produces a one-tile-per-slide plan with
// image-mode tile renders. Each tile carries the slide's render
// template; the parent JobSubmitDto's request acts as a default
// inheritance source for fields the slide leaves blank.

using System.Collections.Generic;

using FracturingFog.Server.Cluster;
using FracturingFog.Server.Cluster.Protocol;
using FracturingFog.Server.Protocol;
using Xunit;

namespace FracturingFog.Server.Tests.Cluster;

public sealed class SlideshowPlannerTests
{
    private static RenderRequestDto Slide(
        string? region = null, string? theme = null,
        string fractal = "Mandelbrot",
        double? cx = -0.75, double? cy = 0.0, double? zoom = 1.0,
        int w = 0, int h = 0)
    => new()
    {
        Mode        = "image",
        RegionName  = region,
        FractalType = fractal,
        ThemeName   = theme ?? "HSV",
        CenterX     = cx,
        CenterY     = cy,
        Zoom        = zoom,
        Width       = w,
        Height      = h,
    };

    private static JobSubmitDto Submit(
        List<RenderRequestDto> slides,
        int defW = 640, int defH = 480,
        int defaultDisplayMs = 5000)
    => new()
    {
        Request = new RenderRequestDto
        {
            Mode        = "slideshow",
            FractalType = "Mandelbrot",
            Width       = defW,
            Height      = defH,
            CenterX     = 0,
            CenterY     = 0,
            Zoom        = 1.0,
        },
        Slides = slides,
        SlideshowDefaultDisplayMs = defaultDisplayMs,
    };

    [Fact]
    public void PlanSlideshow_Emits_One_Tile_Per_Slide()
    {
        var s = Submit(new()
        {
            Slide(region: "A"),
            Slide(region: "B"),
            Slide(region: "C"),
            Slide(region: "D"),
        });

        var plan = SlideshowPlanner.PlanSlideshow(s);

        Assert.Equal("slideshow", plan.Mode);
        Assert.Equal(4, plan.TileCount);
        Assert.Equal(0, plan.TotalFrames);
        Assert.All(plan.Tiles, t => Assert.Null(t.FrameRange));
        for (int i = 0; i < plan.Tiles.Count; i++)
            Assert.Equal(i, plan.Tiles[i].TileId);
    }

    [Fact]
    public void PlanSlideshow_Tile_Render_Is_Image_Mode_With_Inherited_Dims()
    {
        var s = Submit(new()
        {
            Slide(region: "A"),       // w/h = 0 → inherit from template
            Slide(region: "B", w: 800, h: 600),
        }, defW: 1280, defH: 720);

        var plan = SlideshowPlanner.PlanSlideshow(s);

        Assert.Equal("image",    plan.Tiles[0].Render.Mode);
        Assert.Equal(1280,       plan.Tiles[0].Render.Width);
        Assert.Equal(720,        plan.Tiles[0].Render.Height);
        Assert.Equal(800,        plan.Tiles[1].Render.Width);
        Assert.Equal(600,        plan.Tiles[1].Render.Height);
        Assert.True(plan.Tiles[0].Render.SuppressDecorations);
        Assert.True(plan.Tiles[1].Render.SuppressDecorations);
        Assert.Equal("inline",   plan.Tiles[0].Render.ReturnMode);
        Assert.Null(plan.Tiles[0].Render.OutputName);
    }

    [Fact]
    public void PlanSlideshow_OffsetXY_Are_Zero_And_ImageDims_Match_Slide()
    {
        var s = Submit(new()
        {
            Slide(region: "A", w: 320, h: 200),
            Slide(region: "B", w: 320, h: 200),
        });

        var plan = SlideshowPlanner.PlanSlideshow(s);

        foreach (var t in plan.Tiles)
        {
            Assert.Equal(0,   t.OffsetX);
            Assert.Equal(0,   t.OffsetY);
            Assert.Equal(320, t.ImageWidth);
            Assert.Equal(200, t.ImageHeight);
        }
    }

    [Fact]
    public void PlanSlideshow_Slide_Field_Overrides_Template_Field()
    {
        var s = Submit(new()
        {
            Slide(region: "A", theme: "Inferno", fractal: "Julia"),
            Slide(region: null, theme: null),   // both unspecified → inherit
        });

        var plan = SlideshowPlanner.PlanSlideshow(s);

        Assert.Equal("Julia",   plan.Tiles[0].Render.FractalType);
        Assert.Equal("Inferno", plan.Tiles[0].Render.ThemeName);
        Assert.Equal("A",       plan.Tiles[0].Render.RegionName);

        Assert.Equal("Mandelbrot", plan.Tiles[1].Render.FractalType);
        Assert.Equal("HSV",        plan.Tiles[1].Render.ThemeName);
        // ThemeName is non-null on the slide (default "HSV") so the
        // template's "HSV" survives — verify region inherits null when
        // both sides are null.
        Assert.Null(plan.Tiles[1].Render.RegionName);
    }

    [Fact]
    public void ValidateForSlideshow_Refuses_Empty_Slide_List()
    {
        var s = new JobSubmitDto { Request = new RenderRequestDto { Mode = "slideshow" } };
        Assert.False(SlideshowPlanner.ValidateForSlideshow(s, out string? why));
        Assert.NotNull(why);
        Assert.Contains("slides", why);
    }

    [Fact]
    public void ValidateForSlideshow_Refuses_Single_Slide()
    {
        var s = Submit(new() { Slide(region: "A") });
        Assert.False(SlideshowPlanner.ValidateForSlideshow(s, out string? why));
        Assert.NotNull(why);
        Assert.Contains(">=", why);
    }

    [Fact]
    public void ValidateForSlideshow_Refuses_Untileable_Fractal_On_Any_Slide()
    {
        var s = Submit(new()
        {
            Slide(region: "A", fractal: "Mandelbrot"),
            Slide(region: "B", fractal: "LSystem"),   // not tileable
        });
        Assert.False(SlideshowPlanner.ValidateForSlideshow(s, out string? why));
        Assert.NotNull(why);
        Assert.Contains("slide #1", why);
    }

    [Fact]
    public void ValidateForSlideshow_Requires_Matching_DisplayMs_Length()
    {
        var s = Submit(new()
        {
            Slide(region: "A"),
            Slide(region: "B"),
            Slide(region: "C"),
        });
        s.SlideDisplayMs = new() { 100, 200 };  // length 2, slides 3

        Assert.False(SlideshowPlanner.ValidateForSlideshow(s, out string? why));
        Assert.NotNull(why);
        Assert.Contains("slideDisplayMs", why);
    }

    [Fact]
    public void PlanSlideshow_Picks_Image_Dims_From_Max_Slide_Dims()
    {
        var s = Submit(new()
        {
            Slide(region: "A", w: 640, h: 480),
            Slide(region: "B", w: 1280, h: 720),    // max
            Slide(region: "C", w: 320, h: 240),
        });

        var plan = SlideshowPlanner.PlanSlideshow(s);

        Assert.Equal(1280, plan.ImageWidth);
        Assert.Equal(720,  plan.ImageHeight);
    }
}
