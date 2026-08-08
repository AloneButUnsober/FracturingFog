// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.Numerics;
using System.Text.Json;
using FracturingFog;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class RegionFractalParamsTests
{
    // Families that need nothing beyond their defaults never carry a block.
    [Theory]
    [InlineData(FractalType.Mandelbrot)]
    [InlineData(FractalType.Tricorn)]
    [InlineData(FractalType.BurningShip)]
    [InlineData(FractalType.Magnet1)]
    [InlineData(FractalType.TearDrop)]
    [InlineData(FractalType.GeneratedMandelbrotZ2)]
    public void Snapshot_ReturnsNull_ForDefaultSufficesFamilies(FractalType t)
        => Assert.Null(RegionFractalParams.Snapshot(t, new FractalParameters()));

    [Fact]
    public void Snapshot_Null_WhenParamsNull()
        => Assert.Null(RegionFractalParams.Snapshot(FractalType.Julia, null));

    [Fact]
    public void JuliaSnapshot_RoundTripsConstant()
    {
        var src = new FractalParameters { JuliaC = new Complex(0.285, 0.01) };
        var snap = RegionFractalParams.Snapshot(FractalType.Julia, src);
        Assert.NotNull(snap);

        var dst = new FractalParameters();          // holds a different default c
        snap!.ApplyTo(dst);
        Assert.Equal(0.285, dst.JuliaC.Real, 12);
        Assert.Equal(0.01, dst.JuliaC.Imaginary, 12);
    }

    [Fact]
    public void NewtonSnapshot_CapturesExponentAndRelaxation()
    {
        var src = new FractalParameters { NewtonExponent = 5, NewtonRelaxation = 0.75 };
        var snap = RegionFractalParams.Snapshot(FractalType.Newton, src);
        var dst = new FractalParameters();
        snap!.ApplyTo(dst);
        Assert.Equal(5, dst.NewtonExponent);
        Assert.Equal(0.75, dst.NewtonRelaxation, 12);
    }

    [Fact]
    public void ApplyTo_LeavesUnrelatedParamsUntouched()
    {
        var snap = RegionFractalParams.Snapshot(FractalType.Julia,
            new FractalParameters { JuliaC = new Complex(-0.4, 0.6) });

        var dst = new FractalParameters { MultibrotExponent = 7, SpiderCDecay = 0.9 };
        snap!.ApplyTo(dst);
        // Only Julia c changed; other family knobs stay as they were.
        Assert.Equal(7, dst.MultibrotExponent);
        Assert.Equal(0.9, dst.SpiderCDecay, 12);
    }

    [Fact]
    public void Region_SerializesParams_AndRoundTrips()
    {
        var region = new FractalRegion
        {
            Name = "Julia Custom",
            FractalType = FractalType.Julia,
            CenterX = 0.0, CenterY = 0.0, Zoom = 1.5,
            Params = RegionFractalParams.Snapshot(FractalType.Julia,
                new FractalParameters { JuliaC = new Complex(0.355, 0.337) }),
        };

        string json = JsonSerializer.Serialize(region, new JsonSerializerOptions { WriteIndented = true });
        Assert.Contains("\"Params\"", json);
        Assert.Contains("JuliaCRe", json);
        // Irrelevant nullable fields are omitted from JSON, keeping it lean.
        Assert.DoesNotContain("NewtonExponent", json);

        var back = JsonSerializer.Deserialize<FractalRegion>(json);
        Assert.NotNull(back?.Params);
        var applied = new FractalParameters();
        back!.Params!.ApplyTo(applied);
        Assert.Equal(0.355, applied.JuliaC.Real, 12);
        Assert.Equal(0.337, applied.JuliaC.Imaginary, 12);
    }

    // ── #253 cross-fractal domain warp round-trip ─────────────────────────

    [Fact]
    public void DomainWarp_NotCaptured_WhenDisabled()
    {
        // Default (warp off) → no block for Julia beyond its constant, and none
        // at all for a defaults-suffice type like Burning Ship.
        var julia = RegionFractalParams.Snapshot(FractalType.Julia,
            new FractalParameters { JuliaC = new Complex(0.1, 0.2) });
        Assert.Null(julia!.DomainWarpEnabled);

        Assert.Null(RegionFractalParams.Snapshot(FractalType.BurningShip, new FractalParameters()));
    }

    [Theory]
    [InlineData(FractalType.Julia)]
    [InlineData(FractalType.BurningShip)]   // base block null — warp rides alone
    [InlineData(FractalType.Magnet1)]
    [InlineData(FractalType.Spider)]
    public void DomainWarp_RoundTrips_WhenEnabled(FractalType t)
    {
        var src = new FractalParameters
        {
            DomainWarpEnabled = true,
            DomainWarpStrength = 0.23,
            DomainWarpFrequency = 2.5,
        };
        var snap = RegionFractalParams.Snapshot(t, src);
        Assert.NotNull(snap);
        Assert.True(snap!.DomainWarpEnabled);

        var dst = new FractalParameters();      // warp off by default
        snap.ApplyTo(dst);
        Assert.True(dst.DomainWarpEnabled);
        Assert.Equal(0.23, dst.DomainWarpStrength, 12);
        Assert.Equal(2.5, dst.DomainWarpFrequency, 12);
    }

    [Fact]
    public void DomainWarp_NotCaptured_ForUnsupportedType()
    {
        // Mandelbrot runs on the deep-zoom calc; warp isn't carried even if the
        // flag is somehow set, so it never leaks onto that path.
        var snap = RegionFractalParams.Snapshot(FractalType.Mandelbrot,
            new FractalParameters { DomainWarpEnabled = true, DomainWarpStrength = 0.3 });
        Assert.Null(snap);
    }

    [Fact]
    public void DomainWarp_Serializes_AndRoundTrips()
    {
        var region = new FractalRegion
        {
            Name = "Warped Ship",
            FractalType = FractalType.BurningShip,
            Zoom = 1.0,
            Params = RegionFractalParams.Snapshot(FractalType.BurningShip,
                new FractalParameters { DomainWarpEnabled = true, DomainWarpStrength = 0.4, DomainWarpFrequency = 1.5 }),
        };
        string json = JsonSerializer.Serialize(region);
        Assert.Contains("DomainWarpEnabled", json);

        var back = JsonSerializer.Deserialize<FractalRegion>(json);
        var applied = new FractalParameters();
        back!.Params!.ApplyTo(applied);
        Assert.True(applied.DomainWarpEnabled);
        Assert.Equal(0.4, applied.DomainWarpStrength, 12);
    }

    [Fact]
    public void LegacyRegion_WithoutParams_DeserializesToNull()
    {
        // A pre-P1 region JSON has no "Params" key at all.
        const string legacy =
            "{\"Name\":\"Old\",\"CenterX\":-0.5,\"CenterY\":0.0,\"Zoom\":0.5," +
            "\"Iterations\":256,\"FractalType\":\"Mandelbrot\"}";
        var back = JsonSerializer.Deserialize<FractalRegion>(legacy);
        Assert.NotNull(back);
        Assert.Null(back!.Params);
    }
}
