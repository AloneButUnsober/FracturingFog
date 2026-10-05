// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Imaging;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1123 (epic #1114 S9) — Relief split. Oracles: the split height equals the
// SmoothBuffer of an independent render that COLOURS that field; the colour
// buffer equals the unsplit render byte for byte; split off is the original
// SmoothBuffer (same array as the colour scalar); and through PosterRenderer the
// relief height field of (colour A, height B) is the field of (colour B).
public sealed class DualOrbitReliefSplitTests
{
    private sealed class RampMap : IColorMap
    {
        public ColorPaletteType Type => ColorPaletteType.GradientLinear;
        public int MaxIterations { get; set; }
        public uint InSetColor => 0xFF102030u;
        public int Map(float smooth, float distance, int iterations)
            => unchecked((int)(0xFF000000u | ((uint)(smooth * 997f) & 0x00FFFFFFu)));
    }

    private static DualOrbitEscapeCalculator Calc(FractalParameters p)
    {
        var c = new DualOrbitEscapeCalculator(64, 48)
        {
            CenterX = -0.6, CenterY = 0, Zoom = 1, MaxIterations = 300,
            FractalParameters = p, ColorMap = new RampMap(),
        };
        c.Calculate();
        return c;
    }

    private static FractalParameters P(DualOrbitColorMode mode, DualOrbitField colour, DualOrbitField? height)
        => new()
        {
            DualOrbitColorMode = mode, DualOrbitField = colour,
            DualOrbitSplitHeight = height.HasValue, DualOrbitHeightField = height ?? DualOrbitField.SecantLyapunov,
            DualOrbitCSeedX = 0.3, DualOrbitCSeedY = 0.2,
        };

    [Fact]
    public void SplitOff_SmoothBufferIsTheColourScalar()
    {
        var c = Calc(P(DualOrbitColorMode.Field, DualOrbitField.GreenRatio, null));
        Assert.False(c.HeightIsSplit);
        Assert.Same(c.ColorScalarBuffer, c.SmoothBuffer);
        Assert.Contains(c.SmoothBuffer, v => v > 0);
    }

    [Theory]
    [InlineData(DualOrbitColorMode.Field, DualOrbitField.ExternalAngleDelta, DualOrbitField.SecantLyapunov)]
    [InlineData(DualOrbitColorMode.Field, DualOrbitField.GreenRatio, DualOrbitField.EscapeTimeC)]
    [InlineData(DualOrbitColorMode.PerOrbitLayers, DualOrbitField.GreenRatio, DualOrbitField.GreenRatio)]
    [InlineData(DualOrbitColorMode.BoettcherDomain, DualOrbitField.GreenRatio, DualOrbitField.TrapC)]
    [InlineData(DualOrbitColorMode.Bivariate2D, DualOrbitField.GreenRatio, DualOrbitField.DistanceZ)]
    public void Split_HeightIsTheOtherField_ColourUnchanged(DualOrbitColorMode mode, DualOrbitField colour, DualOrbitField height)
    {
        var plain = Calc(P(mode, colour, null));
        var split = Calc(P(mode, colour, height));
        var heightRef = Calc(P(DualOrbitColorMode.Field, height, null));
        Assert.True(split.HeightIsSplit);
        Assert.Equal(plain.ColorBuffer, split.ColorBuffer);
        Assert.Equal(plain.ColorScalarBuffer, split.ColorScalarBuffer);
        Assert.Equal(heightRef.SmoothBuffer, split.SmoothBuffer);
        Assert.NotEqual(plain.SmoothBuffer, split.SmoothBuffer);
    }

    [Fact]
    public void HeightFieldChange_ReusesTheColourOrbits_AndOnlyMovesTheHeight()
    {
        var p = P(DualOrbitColorMode.Field, DualOrbitField.ExternalAngleDelta, null);
        var c = Calc(p);
        var colour = (uint[])c.ColorBuffer.Clone();
        var flat = (float[])c.SmoothBuffer.Clone();

        p.DualOrbitSplitHeight = true; p.DualOrbitHeightField = DualOrbitField.SecantLyapunov;
        c.Calculate();
        Assert.True(c.LastCalculateReusedOrbits);
        Assert.Equal(colour, c.ColorBuffer);
        var h1 = (float[])c.SmoothBuffer.Clone();
        Assert.NotEqual(flat, h1);

        p.DualOrbitHeightField = DualOrbitField.EscapeTimeZ;
        c.Recolor();
        Assert.True(c.LastCalculateReusedOrbits);
        Assert.Equal(colour, c.ColorBuffer);
        Assert.Equal(Calc(P(DualOrbitColorMode.Field, DualOrbitField.EscapeTimeZ, null)).SmoothBuffer, c.SmoothBuffer);

        p.DualOrbitSplitHeight = false;
        c.Recolor();
        Assert.Equal(flat, c.SmoothBuffer);
    }

    [Fact]
    public void Resize_KeepsTheSplitHeightInStep()
    {
        var p = P(DualOrbitColorMode.Field, DualOrbitField.GreenRatio, DualOrbitField.SecantLyapunov);
        var c = Calc(p);
        c.Resize(40, 30);
        c.Calculate();
        Assert.Equal(40 * 30, c.SmoothBuffer.Length);
        Assert.Equal(40 * 30, c.ColorBuffer.Length);
    }

    private static PosterRequest Req(FractalParameters fp) => new()
    {
        FractalType = FractalType.DualOrbitEscape,
        CenterX = -0.6, CenterY = 0, Zoom = 1.0, MaxIterations = 300,
        Width = 96, Height = 72,
        ColorMap = ColorPalette.BuiltIns[0], Quality = QualityPreset.Standard,
        FractalParameters = fp, Path = "unused.png", Format = ImageFileFormat.Png,
    };

    private static FractalParameters Relief(FractalParameters p, bool raymarch)
    {
        p.Relief2DEnabled = true; p.Relief2DRaymarch = raymarch; p.Relief2DGroundPlane = false;
        return p;
    }

    // Through the headless renderer: the relief height field of (colour A,
    // height B) is the field of an unsplit render colouring B.
    [Fact]
    public void PosterRenderer_ReliefField_FollowsTheHeightField()
    {
        var split = Relief(P(DualOrbitColorMode.Field, DualOrbitField.ExternalAngleDelta, DualOrbitField.SecantLyapunov), true);
        var asColour = Relief(P(DualOrbitColorMode.Field, DualOrbitField.SecantLyapunov, null), true);
        var colourOnly = Relief(P(DualOrbitColorMode.Field, DualOrbitField.ExternalAngleDelta, null), true);
        var fs = PosterRenderer.ResolveReliefField(Req(split), 96, 72, default, out _, out _);
        var fb = PosterRenderer.ResolveReliefField(Req(asColour), 96, 72, default, out _, out _);
        var fa = PosterRenderer.ResolveReliefField(Req(colourOnly), 96, 72, default, out _, out _);
        Assert.NotNull(fs);
        Assert.Equal(fb, fs);
        Assert.NotEqual(fa, fs);
    }

    // Emboss: no relief → split is invisible; with relief → it changes the image.
    [Fact]
    public void PosterRenderer_Emboss_SplitOnlyChangesTheReliefImage()
    {
        FractalParameters A(bool split) => P(DualOrbitColorMode.Field, DualOrbitField.ExternalAngleDelta, split ? DualOrbitField.SecantLyapunov : null);
        var flatOff = PosterRenderer.RenderToPixels(Req(A(false)), default, out _, out _);
        var flatOn = PosterRenderer.RenderToPixels(Req(A(true)), default, out _, out _);
        Assert.Equal(flatOff, flatOn);
        var relOff = PosterRenderer.RenderToPixels(Req(Relief(A(false), false)), default, out _, out _);
        var relOn = PosterRenderer.RenderToPixels(Req(Relief(A(true), false)), default, out _, out _);
        Assert.NotEqual(relOff, relOn);
    }

    [Fact]
    public void LiveView_RoundTripsThroughTheCommandBuilder()
    {
        var p = new FractalParameters
        {
            DualOrbitField = DualOrbitField.ExternalAngleDelta,
            DualOrbitSplitHeight = true, DualOrbitHeightField = DualOrbitField.GreenRatio,
        };
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.DualOrbitEscape, Parameters = p,
            FamilyParams = RegionFractalParams.Snapshot(FractalType.DualOrbitEscape, p)!.ToKeyValues(),
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var fresh = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.DualOrbitEscape, out _)!.ApplyTo(fresh);
        Assert.True(fresh.DualOrbitSplitHeight);
        Assert.Equal(DualOrbitField.GreenRatio, fresh.DualOrbitHeightField);
    }
}
