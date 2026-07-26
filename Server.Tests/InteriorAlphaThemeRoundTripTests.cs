// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// Regression tests for issue #96 per-theme interior alpha. The bug was that
/// <see cref="DataDrivenColorThemes.Export"/> wrote every field EXCEPT
/// InSetColor, so an edited interior (colour + alpha) was silently dropped when
/// the editor reloaded a theme (LoadTheme → GetPaletteByName → Export → ToDef).
/// </summary>
public sealed class InteriorAlphaThemeRoundTripTests
{
    private static ColorThemeData GradientWithInSet(byte r, byte g, byte b, byte a)
        => new()
        {
            Name = "RoundTripProbe",
            Category = "User",
            Kind = ColorThemeKind.Gradient,
            Stops =
            {
                new ColorStopData { Position = 0f, R = 0,   G = 0,   B = 0,   A = 255 },
                new ColorStopData { Position = 1f, R = 255, G = 255, B = 255, A = 255 },
            },
            InSetColor = new InSetColorData(r, g, b) { A = a },
        };

    [Fact]
    public void Export_PreservesInteriorColourAndAlpha()
    {
        var data = GradientWithInSet(10, 20, 30, 128);
        var map = DataDrivenColorThemes.Create(data);
        Assert.NotNull(map);

        var exported = DataDrivenColorThemes.Export(map!);
        Assert.NotNull(exported);
        Assert.NotNull(exported!.InSetColor);
        Assert.Equal(10, exported.InSetColor!.R);
        Assert.Equal(20, exported.InSetColor.G);
        Assert.Equal(30, exported.InSetColor.B);
        Assert.Equal(128, exported.InSetColor.A);
    }

    [Fact]
    public void Export_DefaultOpaqueBlackInterior_RoundTripsAsNoOverride()
    {
        // A theme without an interior override keeps the historical null (opaque
        // black) so existing themes serialise byte-for-byte as before.
        var data = new ColorThemeData
        {
            Name = "NoOverride",
            Kind = ColorThemeKind.Gradient,
            Stops =
            {
                new ColorStopData { Position = 0f, R = 0,   G = 0,   B = 0,   A = 255 },
                new ColorStopData { Position = 1f, R = 255, G = 255, B = 255, A = 255 },
            },
            InSetColor = null,
        };
        var map = DataDrivenColorThemes.Create(data);
        var exported = DataDrivenColorThemes.Export(map!);
        Assert.NotNull(exported);
        Assert.Null(exported!.InSetColor);
    }

    [Fact]
    public void InSetColorData_PacksAlphaIntoArgb()
    {
        var packed = new InSetColorData(0x11, 0x22, 0x33) { A = 0x80 }.ToPackedArgb();
        Assert.Equal(0x80112233u, packed);
    }
}
