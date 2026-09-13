// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Regression for #610 — a Wallpaper / Poster render at an aspect different from the
// on-screen window cropped the short axis (top/bottom for an ultrawide wallpaper,
// sides for a portrait poster). The calculator maps the complex plane by the longest
// pixel axis (scale = 3.5/max(W,H)/Zoom), so a cross-aspect output covers a different
// complex rectangle than the screen unless Zoom is reconciled.
//
// The fix adds a "Maintain" mode:
//   * Aspect    — carry Zoom through unchanged (crops; byte-identical to pre-#610).
//   * View      — rescale Zoom (PosterRenderer.ViewZoomFactor) so the whole on-screen
//                 view stays inside the output; the wide axis reveals more fractal.
//   * Letterbox — render the on-screen view into an inner rect (PosterRenderer.
//                 LetterboxInner) and pad the surround bars (PosterRenderer.PadCentred).
//
// These assert the geometry helpers so the Zoom rescale preserves the on-screen span
// and the letterbox inner rect keeps the on-screen aspect.

using FracturingFog.Imaging;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class PosterViewAspectTests
{
    // Same-aspect output → factor is exactly 1 (byte-identical to Aspect mode).
    [Fact]
    public void ViewZoomFactor_SameAspect_Is_One()
    {
        // Output is a 2× scale of the on-screen window → identical aspect.
        double f = PosterRenderer.ViewZoomFactor(546, 356, 1092, 712);
        Assert.Equal(1.0, f, 12);
    }

    // Non-positive dimensions degrade to the identity factor (never throws / NaN).
    [Fact]
    public void ViewZoomFactor_Degenerate_Is_One()
    {
        Assert.Equal(1.0, PosterRenderer.ViewZoomFactor(0, 356, 5760, 1080), 12);
        Assert.Equal(1.0, PosterRenderer.ViewZoomFactor(546, 356, 5760, 0), 12);
    }

    // Ultrawide wallpaper (the reported case): View mode preserves the on-screen
    // SHORT-axis (vertical) complex span exactly, instead of cropping it.
    [Fact]
    public void ViewZoomFactor_Ultrawide_Preserves_ShortAxisSpan()
    {
        int sw = 546, sh = 356;        // on-screen window
        int ow = 5760, oh = 1080;      // ultrawide virtual-screen union
        const double zoom = 1.0;

        double f = PosterRenderer.ViewZoomFactor(sw, sh, ow, oh);

        // Complex units per pixel = 3.5 / max(W,H) / Zoom.
        double onVerticalSpan = sh * (3.5 / System.Math.Max(sw, sh)) / zoom;
        double posterVerticalSpan = oh * (3.5 / System.Math.Max(ow, oh)) / (zoom * f);

        Assert.Equal(onVerticalSpan, posterVerticalSpan, 9);

        // And the poster reveals MORE horizontally (never crops): its horizontal
        // span exceeds the on-screen horizontal span.
        double onHorizontalSpan = sw * (3.5 / System.Math.Max(sw, sh)) / zoom;
        double posterHorizontalSpan = ow * (3.5 / System.Math.Max(ow, oh)) / (zoom * f);
        Assert.True(posterHorizontalSpan > onHorizontalSpan);
    }

    // Portrait poster (sides otherwise cropped): View preserves the on-screen
    // LONG-axis (horizontal) span and reveals more vertically.
    [Fact]
    public void ViewZoomFactor_Portrait_Preserves_LongAxisSpan()
    {
        int sw = 546, sh = 356;        // landscape on-screen window
        int ow = 7200, oh = 10800;     // portrait poster (render dims before rotate)
        const double zoom = 1.0;

        double f = PosterRenderer.ViewZoomFactor(sw, sh, ow, oh);

        double onHorizontalSpan = sw * (3.5 / System.Math.Max(sw, sh)) / zoom;
        double posterHorizontalSpan = ow * (3.5 / System.Math.Max(ow, oh)) / (zoom * f);
        Assert.Equal(onHorizontalSpan, posterHorizontalSpan, 9);
    }

    // Letterbox inner rect keeps the on-screen aspect and touches one output axis.
    [Fact]
    public void LetterboxInner_KeepsOnScreenAspect()
    {
        int sw = 546, sh = 356;
        int ow = 5760, oh = 1080;

        var (iw, ih) = PosterRenderer.LetterboxInner(sw, sh, ow, oh);

        // On-screen is wider than ultrawide? No — on-screen 1.53 < 5.33 out, so the
        // inner rect is height-bound: height == output, width reduced.
        Assert.Equal(oh, ih);
        Assert.True(iw < ow);
        // Aspect preserved.
        Assert.Equal((double)sw / sh, (double)iw / ih, 2);
    }

    [Fact]
    public void LetterboxInner_Portrait_WidthBound()
    {
        int sw = 546, sh = 356;        // landscape window, aspect 1.53
        int ow = 7200, oh = 10800;     // portrait output, aspect 0.667

        var (iw, ih) = PosterRenderer.LetterboxInner(sw, sh, ow, oh);

        // On-screen aspect > output aspect → width-bound.
        Assert.Equal(ow, iw);
        Assert.True(ih < oh);
        Assert.Equal((double)sw / sh, (double)iw / ih, 2);
    }

    // Padding centres the source and fills the surround with the given colour.
    [Fact]
    public void PadCentred_Centers_And_Fills()
    {
        // 2×2 source of 7s into a 4×4 canvas filled with 9s → 1px border of 9, centre 7s.
        var src = new uint[] { 7, 7, 7, 7 };
        var dst = PosterRenderer.PadCentred(src, 2, 2, 4, 4, 9u);

        Assert.Equal(16, dst.Length);
        // ox = oy = 1. Centre block = rows 1..2, cols 1..2.
        Assert.Equal(7u, dst[1 * 4 + 1]);
        Assert.Equal(7u, dst[1 * 4 + 2]);
        Assert.Equal(7u, dst[2 * 4 + 1]);
        Assert.Equal(7u, dst[2 * 4 + 2]);
        // Corners are surround fill.
        Assert.Equal(9u, dst[0]);
        Assert.Equal(9u, dst[3]);
        Assert.Equal(9u, dst[12]);
        Assert.Equal(9u, dst[15]);
    }
}
