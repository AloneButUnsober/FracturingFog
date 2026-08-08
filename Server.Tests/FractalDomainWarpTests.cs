// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using Xunit;
using FracturingFog;
using FracturingFog.Models;

namespace FracturingFog.Server.Tests;

// #253 / IDEA-3 — cross-fractal domain warp on the EscapeTimeCalculator family.
// The warp displaces each pixel's sampling coordinate by a sine-interference
// field before the fractal iterates. Contract:
//   • disabled / strength 0 → BYTE-IDENTICAL to the un-warped fractal
//   • enabled with strength > 0 → the image changes
//   • above MaxWarpZoom the warp is inactive (deep-zoom path excluded)
//   • the shared warp helper is a no-op at strength 0 and symmetric on span
public class FractalDomainWarpTests
{
    private static uint[] Render(FractalType type, bool warpEnabled, double strength,
                                 double zoom = 1.0, double freq = 1.0)
    {
        var calc = new EscapeTimeCalculator(80, 60)
        {
            ColorMap = new GrayscalePalette(),
            FractalType = type,
            MaxIterations = 200,
            CenterX = type == FractalType.Julia ? 0.0 : -0.5,
            CenterY = 0.0,
            Zoom = zoom,
            FractalParameters = new FractalParameters
            {
                JuliaC = new System.Numerics.Complex(-0.8, 0.156),
                MultibrotExponent = 3,
                PhoenixP = new System.Numerics.Complex(0.5666, 0.0),
                SpiderCDecay = 0.5,
                DomainWarpEnabled = warpEnabled,
                DomainWarpStrength = strength,
                DomainWarpFrequency = freq,
            },
        };
        calc.Calculate(default);
        return (uint[])calc.ColorBuffer.Clone();
    }

    public static TheoryData<FractalType> Family => new()
    {
        FractalType.Julia,
        FractalType.BurningShip,
        FractalType.Tricorn,
        FractalType.Multibrot,
        FractalType.Phoenix,
        FractalType.Spider,
        FractalType.Glynn,
        FractalType.Magnet1,
    };

    [Theory]
    [MemberData(nameof(Family))]
    public void Disabled_Is_ByteIdentical_To_Unwarped(FractalType type)
    {
        // Toggle off, any strength → must match a calc that never mentions warp.
        var baseline = Render(type, warpEnabled: false, strength: 0.0);
        var toggledOff = Render(type, warpEnabled: false, strength: 0.5);
        Assert.Equal(baseline, toggledOff);
    }

    [Theory]
    [MemberData(nameof(Family))]
    public void ZeroStrength_Is_ByteIdentical_To_Unwarped(FractalType type)
    {
        // Enabled but strength 0 → inactive → byte-identical (SIMD path kept).
        var baseline = Render(type, warpEnabled: false, strength: 0.0);
        var zeroStrength = Render(type, warpEnabled: true, strength: 0.0);
        Assert.Equal(baseline, zeroStrength);
    }

    [Theory]
    [MemberData(nameof(Family))]
    public void NonZeroStrength_Changes_The_Image(FractalType type)
    {
        var baseline = Render(type, warpEnabled: false, strength: 0.0);
        var warped = Render(type, warpEnabled: true, strength: 0.25);
        Assert.NotEqual(baseline, warped);
    }

    [Fact]
    public void Warp_Is_Inactive_Above_MaxWarpZoom()
    {
        // Beyond the shallow-zoom gate the warp must not fire, so the toggle +
        // strength are ignored and the image is byte-identical to un-warped.
        double deepZoom = EscapeTimeCalculator.MaxWarpZoom * 10.0;
        var baseline = Render(FractalType.Julia, warpEnabled: false, strength: 0.0, zoom: deepZoom);
        var warped   = Render(FractalType.Julia, warpEnabled: true,  strength: 0.3, zoom: deepZoom);
        Assert.Equal(baseline, warped);
    }

    [Fact]
    public void Helper_Is_NoOp_At_Zero_Strength()
    {
        double ox = 0.37, oy = -0.21;
        double ox0 = ox, oy0 = oy;
        FractalDomainWarp.Apply(ref ox, ref oy, halfSpan: 1.75, strength: 0.0, frequency: 1.0);
        Assert.Equal(ox0, ox);
        Assert.Equal(oy0, oy);
    }

    [Fact]
    public void Helper_Displaces_At_Nonzero_Strength()
    {
        double ox = 0.37, oy = -0.21;
        double ox0 = ox, oy0 = oy;
        FractalDomainWarp.Apply(ref ox, ref oy, halfSpan: 1.75, strength: 0.3, frequency: 1.0);
        Assert.True(ox != ox0 || oy != oy0);
    }
}
