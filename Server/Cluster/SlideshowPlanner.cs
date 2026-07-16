// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Server/Cluster/SlideshowPlanner.cs
// D-4c — Per-slide sharding for slideshow jobs. Each slide is an
// independent image render; the planner emits one image-mode tile per
// slide so the dispatcher fans them out across the worker pool in
// parallel. No sub-rect math: a slide's tile renders the full slide
// PNG at the configured Width/Height. The merger path is NOT involved
// — slideshow tiles deliver complete PNGs that the master writes
// straight to disk under <jobdir>/slides/, and the finaliser produces
// a slides-manifest.json describing the result.
//
// Per the dev plan §4 Slideshow subsection:
//   "Each slide is an independent render job. Trivial map/reduce —
//    one tile per slide is fine for v1; subdivide per-slide later if a
//    single slide is the long pole."
//
// Future v2: a long-pole slide can be sub-tiled with the standard
// TilePlanner and merged before being slotted into the slideshow.
// Not in scope for v1.

using System;
using System.Collections.Generic;

using FracturingFog.Server.Cluster.Protocol;
using FracturingFog.Server.Protocol;

namespace FracturingFog.Server.Cluster;

public static class SlideshowPlanner
{
    /// <summary>Lower bound on slide count. A 0- or 1-slide slideshow
    /// is degenerate — bounce the caller back to image mode.</summary>
    public const int MinSlides = 2;

    /// <summary>Upper bound on slide count. 2000 slides at 1920×1080 ≈
    /// 1.5 GB of PNG on disk worst-case; protect the master from a
    /// runaway client that would otherwise fan out tens of thousands of
    /// independent renders.</summary>
    public const int MaxSlides = 2000;

    public static bool ValidateForSlideshow(
        JobSubmitDto submit, out string? reason)
    {
        if (submit.Slides is null || submit.Slides.Count == 0)
        {
            reason = "slideshow submit needs at least one slide in 'slides'";
            return false;
        }
        if (submit.Slides.Count < MinSlides)
        {
            reason = $"slideshow needs >= {MinSlides} slides, got {submit.Slides.Count}";
            return false;
        }
        if (submit.Slides.Count > MaxSlides)
        {
            reason = $"slideshow has {submit.Slides.Count} slides, > max {MaxSlides}";
            return false;
        }

        // The parent request's FractalType is a sane default, but each
        // slide may override; enforce the tileable allowlist per slide
        // because that's what the worker will actually render.
        for (int i = 0; i < submit.Slides.Count; i++)
        {
            var slide = submit.Slides[i];
            string ft = !string.IsNullOrEmpty(slide.FractalType)
                ? slide.FractalType
                : submit.Request.FractalType;
            if (!TilePlanner.ValidateForTiling(ft, out string? why))
            {
                reason = $"slide #{i}: {why}";
                return false;
            }
        }

        if (submit.SlideDisplayMs != null
            && submit.SlideDisplayMs.Count != submit.Slides.Count)
        {
            reason = $"slideDisplayMs length ({submit.SlideDisplayMs.Count}) " +
                     $"must equal slides length ({submit.Slides.Count})";
            return false;
        }
        reason = null;
        return true;
    }

    public static TilePlanner.Plan PlanSlideshow(JobSubmitDto submit)
    {
        if (!ValidateForSlideshow(submit, out string? reason))
            throw new ArgumentException(reason);

        var template = submit.Request;
        var slides = submit.Slides!;

        // Default per-slide dims to the parent template so a caller can
        // pass a list of regions/themes without re-specifying width/height
        // every entry. Slide values win when non-zero.
        int defW = template.Width  > 0 ? template.Width  : 1920;
        int defH = template.Height > 0 ? template.Height : 1080;

        var tiles = new List<TileJobDto>(slides.Count);
        int maxW = 0, maxH = 0;
        for (int i = 0; i < slides.Count; i++)
        {
            var slide = slides[i];
            int w = slide.Width  > 0 ? slide.Width  : defW;
            int h = slide.Height > 0 ? slide.Height : defH;
            if (w > maxW) maxW = w;
            if (h > maxH) maxH = h;

            var tileReq = CloneSlideRender(slide, template, w, h);

            tiles.Add(new TileJobDto
            {
                TileId      = i,
                OffsetX     = 0,
                OffsetY     = 0,
                ImageWidth  = w,
                ImageHeight = h,
                Render      = tileReq,
                // No FrameRange — slideshow tiles are image-mode renders.
            });
        }

        return new TilePlanner.Plan
        {
            ImageWidth       = maxW,
            ImageHeight      = maxH,
            TileTargetPixels = 0,         // n/a — each tile is whole-slide
            Columns          = 1,
            Rows             = tiles.Count,
            Tiles            = tiles,
            Mode             = "slideshow",
            TotalFrames      = 0,
        };
    }

    /// <summary>Build the per-slide render template. Inherits any field
    /// the slide left at its default from the parent template; pins
    /// Mode="image", ReturnMode="inline", OutputName=null,
    /// SuppressDecorations=true so workers behave the same as for
    /// image tiles (no per-tile artifacts on disk, no decorations).</summary>
    private static RenderRequestDto CloneSlideRender(
        RenderRequestDto slide, RenderRequestDto template, int w, int h)
    {
        return new RenderRequestDto
        {
            Mode                 = "image",
            RegionName           = !string.IsNullOrEmpty(slide.RegionName)
                                     ? slide.RegionName
                                     : template.RegionName,
            FractalType          = !string.IsNullOrEmpty(slide.FractalType)
                                     ? slide.FractalType
                                     : template.FractalType,
            CenterX              = slide.CenterX  ?? template.CenterX,
            CenterY              = slide.CenterY  ?? template.CenterY,
            Zoom                 = slide.Zoom     ?? template.Zoom,
            Iterations           = slide.Iterations ?? template.Iterations,
            CenterXLo            = slide.CenterXLo != 0 ? slide.CenterXLo : template.CenterXLo,
            CenterX2             = slide.CenterX2  != 0 ? slide.CenterX2  : template.CenterX2,
            CenterX3             = slide.CenterX3  != 0 ? slide.CenterX3  : template.CenterX3,
            CenterYLo            = slide.CenterYLo != 0 ? slide.CenterYLo : template.CenterYLo,
            CenterY2             = slide.CenterY2  != 0 ? slide.CenterY2  : template.CenterY2,
            CenterY3             = slide.CenterY3  != 0 ? slide.CenterY3  : template.CenterY3,
            ThemeName            = !string.IsNullOrEmpty(slide.ThemeName)
                                     ? slide.ThemeName
                                     : template.ThemeName,
            QualityName          = !string.IsNullOrEmpty(slide.QualityName)
                                     ? slide.QualityName
                                     : template.QualityName,
            ThemeJson            = slide.ThemeJson  ?? template.ThemeJson,
            RegionJson           = slide.RegionJson ?? template.RegionJson,
            Width                = w,
            Height               = h,
            OutputName           = null,
            ReturnMode           = "inline",
            RequestedMaxMinutes  = slide.RequestedMaxMinutes ?? template.RequestedMaxMinutes,
            SuppressDecorations  = true,
        };
    }
}
