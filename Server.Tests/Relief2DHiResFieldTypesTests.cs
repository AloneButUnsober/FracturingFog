// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using Xunit;
using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;

namespace FracturingFog.Server.Tests;

// #328 — the hi-res relief FIELD supersample (#143) was Mandelbrot-only because
// the dedicated field calc hardcoded MandelbrotCalculator. These lock in the
// generalisation: the type policy now covers every 2D height-field type
// (Mandelbrot + the alt escape-time / Newton / Halley / Apollonian families),
// and the alt factory builds a working IHeightFieldSource twin for an alt type.
// (Mandelbrot itself is not built by the factory — it uses a dedicated
// MandelbrotCalculator twin, since MandelbrotCalculator is not an
// IFractalCalculator. The factory returns null for it.)
public class Relief2DHiResFieldTypesTests
{
    [Theory]
    [InlineData(FractalType.Mandelbrot)]
    [InlineData(FractalType.Julia)]
    [InlineData(FractalType.BurningShip)]
    [InlineData(FractalType.Tricorn)]
    [InlineData(FractalType.Multibrot)]
    [InlineData(FractalType.Phoenix)]
    [InlineData(FractalType.Magnet1)]
    [InlineData(FractalType.Magnet2)]
    [InlineData(FractalType.Glynn)]
    [InlineData(FractalType.Spider)]
    [InlineData(FractalType.Newton)]
    [InlineData(FractalType.Nova)]
    [InlineData(FractalType.Halley)]
    [InlineData(FractalType.Apollonian)]
    [InlineData(FractalType.RandomTile)]
    [InlineData(FractalType.ChaoticBilliard)]
    public void Supported_Types_Report_HiRes_Support(FractalType type)
    {
        Assert.True(FractalRenderHost.SupportsHiResReliefField(type),
            $"{type} should support a hi-res relief field");
    }

    [Theory]
    // ALT supported types get a factory twin that exposes a height field.
    [InlineData(FractalType.Julia)]
    [InlineData(FractalType.BurningShip)]
    [InlineData(FractalType.Newton)]
    [InlineData(FractalType.Nova)]
    [InlineData(FractalType.Halley)]
    [InlineData(FractalType.Apollonian)]
    [InlineData(FractalType.RandomTile)]
    [InlineData(FractalType.ChaoticBilliard)]
    public void Alt_Supported_Types_Get_A_HeightField_Twin(FractalType type)
    {
        var twin = FractalRenderHost.CreateReliefFieldCalc(type, 64, 64);
        Assert.NotNull(twin);
        Assert.IsAssignableFrom<IHeightFieldSource>(twin);
        Assert.Equal(64, twin!.Width);
        Assert.Equal(64, twin.Height);
    }

    [Fact]
    // Mandelbrot is supported but uses a dedicated MandelbrotCalculator twin, not
    // the alt factory (MandelbrotCalculator is not an IFractalCalculator).
    public void Mandelbrot_Is_Supported_But_Not_Built_By_The_Alt_Factory()
    {
        Assert.True(FractalRenderHost.SupportsHiResReliefField(FractalType.Mandelbrot));
        Assert.Null(FractalRenderHost.CreateReliefFieldCalc(FractalType.Mandelbrot, 64, 64));
    }

    [Theory]
    // Non-height-field or Monte-Carlo types: no supersamplable field, so relief
    // is skipped for them by design and the factory returns null.
    [InlineData(FractalType.BuddhaBrot)]
    [InlineData(FractalType.IFS)]
    [InlineData(FractalType.LSystem)]
    [InlineData(FractalType.StrangeAttractor)]
    [InlineData(FractalType.Mandelbulb)]
    [InlineData(FractalType.Mandelbox)]
    [InlineData(FractalType.Kifs)]
    [InlineData(FractalType.Kleinian)]
    [InlineData(FractalType.Plasma)]
    [InlineData(FractalType.Secant)]
    public void Unsupported_Types_Have_No_Twin(FractalType type)
    {
        Assert.False(FractalRenderHost.SupportsHiResReliefField(type),
            $"{type} should not support a hi-res relief field");
        Assert.Null(FractalRenderHost.CreateReliefFieldCalc(type, 64, 64));
    }

    [Theory]
    [InlineData(FractalType.BurningShip, typeof(EscapeTimeCalculator))]
    [InlineData(FractalType.Julia, typeof(EscapeTimeCalculator))]
    [InlineData(FractalType.Newton, typeof(NewtonCalculator))]
    [InlineData(FractalType.Halley, typeof(HalleyCalculator))]
    [InlineData(FractalType.Apollonian, typeof(ApollonianCalculator))]
    [InlineData(FractalType.RandomTile, typeof(RandomTileCalculator))]
    [InlineData(FractalType.ChaoticBilliard, typeof(ChaoticBilliardCalculator))]
    public void Factory_Builds_The_Right_Concrete_Type(FractalType type, Type expected)
    {
        var twin = FractalRenderHost.CreateReliefFieldCalc(type, 32, 32);
        Assert.NotNull(twin);
        Assert.IsType(expected, twin);
    }

    // The crux of #328: an ALT height-field type (Burning Ship) built through the
    // factory produces a real, non-degenerate smooth-count field — a base plane
    // plus raised boundary structure — exactly what the relief path consumes.
    [Fact]
    public void Alt_Type_Twin_Produces_NonDegenerate_Field()
    {
        var twin = FractalRenderHost.CreateReliefFieldCalc(FractalType.BurningShip, 96, 96);
        var e = Assert.IsType<EscapeTimeCalculator>(twin);
        // Mirror how SyncAltStateFromMandel configures the live alt calc.
        e.FractalType = FractalType.BurningShip;
        e.FractalParameters = new FractalParameters();
        e.CenterX = -0.5; e.CenterY = -0.5; e.Zoom = 0.4; e.MaxIterations = 200;
        e.Calculate(default);

        var field = ((IHeightFieldSource)e).SmoothBuffer;
        Assert.Equal(96 * 96, field.Length);

        double max = 0; int exterior = 0, interior = 0;
        foreach (float v in field)
        {
            if (v > max) max = v;
            if (v > 0f) exterior++; else interior++;
        }
        Assert.True(max > 0, "alt-type height field is all zero");
        Assert.True(exterior > 0, "no exterior (raised) pixels");
        Assert.True(interior > 0, "no interior (base-plane) pixels — view missed the set");
    }
}
