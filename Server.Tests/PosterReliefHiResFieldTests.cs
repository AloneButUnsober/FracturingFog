// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Regression for #508 — poster / wallpaper export flattened Relief 3D because it
// re-derived the calculator's coarse SmoothBuffer instead of the interactive
// hi-res relief field. The fix threads a caller-supplied relief field through
// PosterRequest.ReliefField and prefers it on the raymarch path, and stops the
// poster mutating the live FractalParameters.Lighting (the on-screen "flip" during
// a save). These assert: (1) a supplied ReliefField changes the poster relief
// (it is honoured), (2) rendering a poster does not bake the volume palette onto
// the caller's Lighting, and (3) the new HeightfieldRaymarch2D.Render lighting
// override is byte-identical when null.

using System;
using FracturingFog;
using FracturingFog.Imaging;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class PosterReliefHiResFieldTests
{
    private static FractalParameters ReliefParams() => new()
    {
        Relief2DEnabled = true,
        Relief2DRaymarch = true,
        Relief2DHeightScale = 1.4,
        Relief2DCameraAzimuthDeg = 25,
        Relief2DCameraElevationDeg = 45,
        Relief2DCameraFovDeg = 55,
        Relief2DGroundPlane = false,
        Relief2DGpuRaymarch = false,   // CPU trace — deterministic, no device
    };

    private static PosterRequest MandelReq(FractalParameters fp, int w, int h,
        float[]? reliefField = null, int rfW = 0, int rfH = 0) => new()
    {
        FractalType = FractalType.Mandelbrot,
        CenterX = -0.5, CenterY = 0,
        Zoom = 1.0,
        MaxIterations = 200,
        Width = w, Height = h,
        ColorMap = ColorPalette.BuiltIns[0],
        Quality = QualityPreset.Standard,
        FractalParameters = fp,
        ReliefField = reliefField,
        ReliefFieldW = rfW,
        ReliefFieldH = rfH,
        Format = ImageFileFormat.Png,
    };

    // A synthetic centre-dome height field at an arbitrary (here higher) resolution
    // — non-degenerate and clearly distinct from the Mandelbrot boundary field.
    private static float[] DomeField(int fw, int fh)
    {
        var f = new float[fw * fh];
        for (int y = 0; y < fh; y++)
            for (int x = 0; x < fw; x++)
            {
                double dx = (x / (fw - 1.0)) * 2 - 1;
                double dy = (y / (fh - 1.0)) * 2 - 1;
                f[y * fw + x] = (float)Math.Max(0.0, 1.0 - Math.Sqrt(dx * dx + dy * dy));
            }
        return f;
    }

    // A supplied ReliefField is used for the raymarch (not the calc's SmoothBuffer):
    // the same poster with a distinct hi-res field differs from the SmoothBuffer one.
    [Fact]
    public void Supplied_ReliefField_Changes_Poster_Relief()
    {
        int w = 96, h = 72;
        var noField = PosterRenderer.RenderToPixels(MandelReq(ReliefParams(), w, h), default, out _, out _);
        var dome = DomeField(w * 2, h * 2);
        var withField = PosterRenderer.RenderToPixels(
            MandelReq(ReliefParams(), w, h, dome, w * 2, h * 2), default, out _, out _);

        Assert.Equal(noField.Length, withField.Length);
        Assert.False(System.Linq.Enumerable.SequenceEqual(noField, withField),
            "supplied ReliefField was ignored — poster still used the SmoothBuffer");
    }

    // Rendering a poster must NOT bake the volume-palette LUT onto the caller's
    // (live) Lighting — a background-thread mutation of the live params raced the
    // render loop (the on-screen flip during a save). VolumePaletteStrength > 0
    // makes Bake write a LUT; assert the caller's Lighting.VolumePalette stays null.
    [Fact]
    public void Poster_Does_Not_Bake_Onto_Caller_Lighting()
    {
        var fp = ReliefParams();
        var fx = fp.Lighting;
        fx.FogDensity = 0.5;
        fx.VolumePaletteStrength = 0.8;   // slice-D active → Bake would set VolumePalette
        fx.VolumePalette = null;
        fp.Lighting = fx;

        Assert.Null(fp.Lighting.VolumePalette);
        _ = PosterRenderer.RenderToPixels(MandelReq(fp, 96, 72), default, out _, out _);
        Assert.Null(fp.Lighting.VolumePalette);   // untouched — baked on a local copy
    }

    // The new lightingOverride arg on HeightfieldRaymarch2D.Render is byte-identical
    // when null (reads p.Lighting) and equal to passing p.Lighting explicitly; a
    // modified override changes the output.
    [Fact]
    public void Render_LightingOverride_Is_ByteIdentical_When_Null()
    {
        int w = 80, h = 60, fw = 80, fh = 60;
        var field = DomeField(fw, fh);
        var albedo = new uint[w * h];
        for (int i = 0; i < albedo.Length; i++) albedo[i] = 0xFF3366AAu;
        var p = ReliefParams();

        var a = new uint[w * h];
        var b = new uint[w * h];
        var c = new uint[w * h];
        HeightfieldRaymarch2D.Render(albedo, field, w, h, fw, fh, p, a, out _, null, null, null, null, null);
        HeightfieldRaymarch2D.Render(albedo, field, w, h, fw, fh, p, b, out _, null, null, null, null, p.Lighting);

        var lit = p.Lighting;
        lit.AmbientStrength = Math.Min(1.0, lit.AmbientStrength + 0.5);
        lit.ShadowSoftK = lit.ShadowSoftK + 4.0;
        HeightfieldRaymarch2D.Render(albedo, field, w, h, fw, fh, p, c, out _, null, null, null, null, lit);

        Assert.Equal(a, b);                                              // null == p.Lighting
        Assert.False(System.Linq.Enumerable.SequenceEqual(a, c),        // override takes effect
            "lightingOverride did not change the render");
    }
}
