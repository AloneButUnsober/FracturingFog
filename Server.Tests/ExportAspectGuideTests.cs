// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #511 (C) — the export-aspect frame guide fit geometry. A target aspect wider
// than the window letterboxes (bars top/bottom, full width); a taller one
// pillarboxes (bars left/right, full height); an equal aspect fills exactly; and
// the rect is always centred and within the window.

using FracturingFog.Rendering;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ExportAspectGuideTests
{
    [Fact]
    public void Wider_Than_Window_Letterboxes_Full_Width()
    {
        // Window 1000x1000 (aspect 1), export 2.5 (ultrawide) → full width, short height.
        var (x, y, w, h) = ExportAspectGuide.Fit(1000, 1000, 2.5);
        Assert.Equal(0.0, x, 6);
        Assert.Equal(1000.0, w, 6);
        Assert.Equal(400.0, h, 6);                 // 1000 / 2.5
        Assert.Equal((1000 - 400) / 2.0, y, 6);    // centred vertically
    }

    [Fact]
    public void Taller_Than_Window_Pillarboxes_Full_Height()
    {
        // Window 1000x1000, export 0.5 (portrait) → full height, narrow width.
        var (x, y, w, h) = ExportAspectGuide.Fit(1000, 1000, 0.5);
        Assert.Equal(0.0, y, 6);
        Assert.Equal(1000.0, h, 6);
        Assert.Equal(500.0, w, 6);                 // 1000 * 0.5
        Assert.Equal((1000 - 500) / 2.0, x, 6);    // centred horizontally
    }

    [Fact]
    public void Equal_Aspect_Fills_Window()
    {
        var (x, y, w, h) = ExportAspectGuide.Fit(1600, 900, 1600.0 / 900.0);
        Assert.Equal(0.0, x, 6);
        Assert.Equal(0.0, y, 6);
        Assert.Equal(1600.0, w, 6);
        Assert.Equal(900.0, h, 6);
    }

    [Fact]
    public void Fit_Is_Centred_And_Within_Window()
    {
        var (x, y, w, h) = ExportAspectGuide.Fit(1280, 720, 21.0 / 9.0);
        Assert.True(x >= -1e-6 && y >= -1e-6);
        Assert.True(x + w <= 1280 + 1e-6);
        Assert.True(y + h <= 720 + 1e-6);
        Assert.Equal(1280 - (x + w), x, 6);        // symmetric horizontally
        Assert.Equal(720 - (y + h), y, 6);         // symmetric vertically
    }

    [Fact]
    public void Degenerate_Inputs_Return_Full_Window()
    {
        var (x, y, w, h) = ExportAspectGuide.Fit(800, 600, 0.0);
        Assert.Equal(0.0, x, 6);
        Assert.Equal(0.0, y, 6);
        Assert.Equal(800.0, w, 6);
        Assert.Equal(600.0, h, 6);
    }
}
