// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using Xunit;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Imaging;
using FracturingFog.Interefaces;

namespace FracturingFog.Server.Tests;

// #629 (Renderer B) — escape-angle demo themes + 3-way compare poster preset.
public class EscapeAngleDemoTests
{
    // ── demo themes ────────────────────────────────────────────────────────

    [Fact]
    public void Demo_Themes_Are_Registered_In_BuiltIns()
    {
        var types = ColorPalette.BuiltIns.Select(m => m.GetType()).ToHashSet();
        Assert.Contains(typeof(EscapeAngleDemoMap), types);
        Assert.Contains(typeof(EscapeAngleIterShadedMap), types);
    }

    [Fact]
    public void EscapeAngleDemo_Interior_Is_InSetColor()
    {
        IColorMap m = new EscapeAngleDemoMap();
        // finalZ == 0 is the calculator's in-set sentinel.
        int c = m.Map(0f, 0f, 500, 0f, 0f, 0f, 0f, 0f, 0f);
        Assert.Equal(unchecked((int)m.InSetColor), c);
    }

    [Fact]
    public void EscapeAngleDemo_Hue_Tracks_Angle()
    {
        IColorMap m = new EscapeAngleDemoMap();
        // Two different escape angles must produce different colours.
        int east = m.Map(10f, 0f, 500, 0f, 0f, /*zr*/ 1f, /*zi*/ 0f, 0f, 0f);   // angle 0
        int north = m.Map(10f, 0f, 500, 0f, 0f, /*zr*/ 0f, /*zi*/ 1f, 0f, 0f);  // angle π/2
        Assert.NotEqual(east, north);
    }

    [Fact]
    public void EscapeAngleIterShaded_Brightness_Rises_With_Iteration_Depth()
    {
        var m = new EscapeAngleIterShadedMap { MaxIterations = 1000 };
        // Same escape angle, deeper smooth iteration → brighter (larger value).
        int shallow = m.Map(50f, 0f, 500, 0f, 0f, 1f, 0.2f, 0f, 0f);
        int deep = m.Map(900f, 0f, 500, 0f, 0f, 1f, 0.2f, 0f, 0f);
        Assert.True(Luma(deep) > Luma(shallow),
            $"deep-iter pixel (luma {Luma(deep)}) should outshine shallow ({Luma(shallow)})");
    }

    [Fact]
    public void EscapeAngleIterShaded_Interior_Is_InSetColor()
    {
        IColorMap m = new EscapeAngleIterShadedMap();
        int c = m.Map(0f, 0f, 500, 0f, 0f, 0f, 0f, 0f, 0f);
        Assert.Equal(unchecked((int)m.InSetColor), c);
    }

    // ── compare poster preset ──────────────────────────────────────────────

    [Fact]
    public void ComparePoster_Has_Expected_Layout_And_Gutters()
    {
        const int pw = 64, ph = 64, gap = 8;
        var px = EscapeAngleComparePoster.RenderComposite(
            centerX: -0.5, centerY: 0.0, zoom: 1.0, maxIterations: 200,
            fractalType: FractalType.Mandelbrot, fractalParameters: new FractalParameters(),
            quality: QualityPreset.Standard,
            panelWidth: pw, panelHeight: ph,
            token: default, out int w, out int h,
            gap: gap, gapColor: 0xFF202020u);

        int n = EscapeAngleComparePoster.DefaultPanels().Count;
        Assert.Equal(n * pw + (n - 1) * gap, w);
        Assert.Equal(ph, h);
        Assert.Equal(w * h, px.Length);

        // A pixel in the first gutter column must be the gap colour.
        int gx = pw + gap / 2;
        Assert.Equal(0xFF202020u, px[(h / 2) * w + gx]);
    }

    [Fact]
    public void ComparePoster_Panels_Differ()
    {
        const int pw = 64, ph = 64, gap = 8;
        var px = EscapeAngleComparePoster.RenderComposite(
            centerX: -0.5, centerY: 0.0, zoom: 1.0, maxIterations: 200,
            fractalType: FractalType.Mandelbrot, fractalParameters: new FractalParameters(),
            quality: QualityPreset.Standard,
            panelWidth: pw, panelHeight: ph,
            token: default, out int w, out int h,
            gap: gap);

        // Same local coordinate in each of the three panels — the three colorings
        // must not all agree (they render the region three different ways).
        int lx = pw / 4, ly = ph / 3;
        uint p0 = px[ly * w + (0 * (pw + gap) + lx)];
        uint p1 = px[ly * w + (1 * (pw + gap) + lx)];
        uint p2 = px[ly * w + (2 * (pw + gap) + lx)];
        Assert.False(p0 == p1 && p1 == p2, "all three panels rendered identically");
    }

    [Fact]
    public void ComparePoster_Is_Deterministic()
    {
        uint[] Run() => EscapeAngleComparePoster.RenderComposite(
            -0.5, 0.0, 1.0, 200, FractalType.Mandelbrot, new FractalParameters(),
            QualityPreset.Standard, 48, 48, default, out _, out _);
        Assert.Equal(Run(), Run());
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static int Luma(int argb)
    {
        int r = (argb >> 16) & 0xFF, g = (argb >> 8) & 0xFF, b = argb & 0xFF;
        return (r * 299 + g * 587 + b * 114) / 1000;
    }
}
