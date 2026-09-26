// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #949 — batch image (PosterRenderer, the path `--batch --mode image` renders
// through) for every fractal type: each renders at the requested size without
// throwing, and every type that needs no user-authored source produces a real
// image (not a single flat colour). Before #947/#949 seven types silently
// rendered Mandelbrot and the user-code types rendered blank.

using System;
using System.Linq;
using System.Threading;

using FracturingFog.Imaging;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class BatchImageAllTypesTests
{
    private const int W = 40, H = 30;

    private static uint[] Render(FractalType t, FractalParameters? fp = null)
        => PosterRenderer.RenderToPixels(new PosterRequest
        {
            FractalType = t, Width = W, Height = H,
            CenterX = -0.5, CenterY = 0.0, Zoom = 1.0, MaxIterations = 128,
            Quality = QualityPreset.Draft, ColorMap = new HsvPalette(),
            FractalParameters = fp ?? new FractalParameters(),
            Path = string.Empty, Format = ImageFileFormat.Png,
        }, CancellationToken.None, out _, out _);

    public static TheoryData<FractalType> AllTypes()
    {
        var d = new TheoryData<FractalType>();
        foreach (var t in Enum.GetValues<FractalType>()) d.Add(t);
        return d;
    }

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void Every_type_renders_a_real_image(FractalType t)
    {
        var px = Render(t);
        Assert.Equal(W * H, px.Length);
        // User-code types have no source here → the documented InSetColor fill.
        if (FractalMotionCapabilities.IsUserCode(t)) return;
        Assert.True(px.Distinct().Count() > 1, $"{t} rendered a single flat colour");
    }

    [Fact]
    public void Generated_and_TearDrop_are_not_Mandelbrot()
    {
        var mandel = Render(FractalType.Mandelbrot);
        foreach (var t in new[] { FractalType.TearDrop, FractalType.GeneratedMandelbrotZ3,
                                  FractalType.GeneratedTricorn, FractalType.GeneratedBurningShip })
            Assert.False(mandel.SequenceEqual(Render(t)), $"{t} rendered as Mandelbrot");
    }

    [Fact]
    public void User_equation_source_in_params_renders()
    {
        var fp = new FractalParameters { UserEquationSource = "z*z*z + c" };
        var px = Render(FractalType.UserEquation, fp);
        Assert.True(px.Distinct().Count() > 1, "UserEquation with source rendered flat");
    }
}
