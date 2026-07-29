// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.Text.Json;
using FracturingFog;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class RegionRelief3DTests
{
    [Fact]
    public void Snapshot_Null_WhenReliefOff()
        => Assert.Null(Relief3DSettings.Snapshot(new FractalParameters { Relief2DEnabled = false }));

    [Fact]
    public void Snapshot_Null_WhenParamsNull()
        => Assert.Null(Relief3DSettings.Snapshot(null));

    [Fact]
    public void Snapshot_CapturesFullReliefBlock_WhenEnabled()
    {
        var src = new FractalParameters
        {
            Relief2DEnabled = true,
            Relief2DRaymarch = true,
            Relief2DHeightScale = 1.7,
            Relief2DCameraAzimuthDeg = 25.0,
            Relief2DCameraElevationDeg = 62.0,
            Relief2DCameraFovDeg = 48.0,
            Relief2DCameraZoom = 1.3,
            Relief2DCameraOrthographic = true,
            Relief2DSupersample = 3,
            Relief2DHeightCurve = HeightCurve2D.Sqrt,
            Relief2DGroundPlane = false,
            Relief2DIsolate = true,
            Relief2DDetailThreshold = 0.72,
            Relief2DDropColorsCsv = "FF102030, 405060",
            Relief2DMeshHeight = 0.22,
            Relief2DMeshSmoothing = 0.8,
            Relief2DMeshGrid = 768,
            Relief2DMeshMaxMB = 4.0,
            Relief2DMeshUnderside = 0.4,
        };

        var snap = Relief3DSettings.Snapshot(src);
        Assert.NotNull(snap);

        // Apply onto fresh defaults and confirm every field round-trips.
        var dst = new FractalParameters();
        snap!.ApplyTo(dst);
        Assert.True(dst.Relief2DEnabled);
        Assert.True(dst.Relief2DRaymarch);
        Assert.Equal(1.7, dst.Relief2DHeightScale, 12);
        Assert.Equal(25.0, dst.Relief2DCameraAzimuthDeg, 12);
        Assert.Equal(62.0, dst.Relief2DCameraElevationDeg, 12);
        Assert.Equal(48.0, dst.Relief2DCameraFovDeg, 12);
        Assert.Equal(1.3, dst.Relief2DCameraZoom, 12);
        Assert.True(dst.Relief2DCameraOrthographic);
        Assert.Equal(3, dst.Relief2DSupersample);
        Assert.Equal(HeightCurve2D.Sqrt, dst.Relief2DHeightCurve);
        Assert.False(dst.Relief2DGroundPlane);
        Assert.True(dst.Relief2DIsolate);
        Assert.Equal(0.72, dst.Relief2DDetailThreshold, 12);
        Assert.Equal("FF102030, 405060", dst.Relief2DDropColorsCsv);
        Assert.Equal(0.22, dst.Relief2DMeshHeight, 12);
        Assert.Equal(0.8, dst.Relief2DMeshSmoothing, 12);
        Assert.Equal(768, dst.Relief2DMeshGrid);
        Assert.Equal(4.0, dst.Relief2DMeshMaxMB, 12);
        Assert.Equal(0.4, dst.Relief2DMeshUnderside, 12);
    }

    [Fact]
    public void Region_SerializesRelief3D_AndRoundTrips()
    {
        var region = new FractalRegion
        {
            Name = "Relief View",
            FractalType = FractalType.Mandelbrot,
            CenterX = -0.75, CenterY = 0.0, Zoom = 1.0,
            Relief3D = Relief3DSettings.Snapshot(new FractalParameters
            {
                Relief2DEnabled = true,
                Relief2DRaymarch = true,
                Relief2DCameraElevationDeg = 55.0,
                Relief2DHeightCurve = HeightCurve2D.Log,
            }),
        };

        string json = JsonSerializer.Serialize(region, new JsonSerializerOptions { WriteIndented = true });
        Assert.Contains("\"Relief3D\"", json);
        Assert.Contains("\"HeightCurve\": \"Log\"", json);   // enum-as-string

        var back = JsonSerializer.Deserialize<FractalRegion>(json);
        Assert.NotNull(back?.Relief3D);
        var applied = new FractalParameters();
        back!.ApplyRelief3DTo(applied);
        Assert.True(applied.Relief2DEnabled);
        Assert.True(applied.Relief2DRaymarch);
        Assert.Equal(55.0, applied.Relief2DCameraElevationDeg, 12);
        Assert.Equal(HeightCurve2D.Log, applied.Relief2DHeightCurve);
    }

    [Fact]
    public void PlainRegion_OmitsRelief3D_FromJson()
    {
        var region = new FractalRegion
        {
            Name = "Plain", FractalType = FractalType.Mandelbrot,
            CenterX = -0.5, CenterY = 0.0, Zoom = 0.5,
            Relief3D = Relief3DSettings.Snapshot(new FractalParameters { Relief2DEnabled = false }),
        };
        string json = JsonSerializer.Serialize(region, new JsonSerializerOptions { WriteIndented = true });
        Assert.DoesNotContain("Relief3D", json);
    }

    [Fact]
    public void Authoritative_PlainRegion_TurnsReliefOff()
    {
        // A region with no relief snapshot must CLEAR relief on recall so
        // selecting a plain region after a relief one turns the effect off.
        var plain = new FractalRegion { Name = "Plain", FractalType = FractalType.Mandelbrot };
        Assert.Null(plain.Relief3D);

        var live = new FractalParameters { Relief2DEnabled = true, Relief2DRaymarch = true };
        plain.ApplyRelief3DAuthoritative(live);
        Assert.False(live.Relief2DEnabled);
        Assert.False(live.Relief2DRaymarch);
    }

    [Fact]
    public void Authoritative_ReliefRegion_TurnsReliefOn()
    {
        var relief = new FractalRegion
        {
            Name = "Relief", FractalType = FractalType.Mandelbrot,
            Relief3D = Relief3DSettings.Snapshot(new FractalParameters
            {
                Relief2DEnabled = true, Relief2DRaymarch = true,
                Relief2DCameraElevationDeg = 60.0,
            }),
        };
        var live = new FractalParameters();   // relief off by default
        relief.ApplyRelief3DAuthoritative(live);
        Assert.True(live.Relief2DEnabled);
        Assert.True(live.Relief2DRaymarch);
        Assert.Equal(60.0, live.Relief2DCameraElevationDeg, 12);
    }

    [Fact]
    public void ApplyOrDisable_NullDisables_NonNullApplies()
    {
        var on = new FractalParameters { Relief2DEnabled = true, Relief2DRaymarch = true };
        Relief3DSettings.ApplyOrDisable(null, on);
        Assert.False(on.Relief2DEnabled);
        Assert.False(on.Relief2DRaymarch);

        var off = new FractalParameters();
        Relief3DSettings.ApplyOrDisable(
            Relief3DSettings.Snapshot(new FractalParameters { Relief2DEnabled = true }), off);
        Assert.True(off.Relief2DEnabled);
    }

    [Fact]
    public void LegacyRegion_WithoutRelief3D_DeserializesToNull_AndApplyIsNoOp()
    {
        const string legacy =
            "{\"Name\":\"Old\",\"CenterX\":-0.5,\"CenterY\":0.0,\"Zoom\":0.5," +
            "\"Iterations\":256,\"FractalType\":\"Mandelbrot\"}";
        var back = JsonSerializer.Deserialize<FractalRegion>(legacy);
        Assert.NotNull(back);
        Assert.Null(back!.Relief3D);

        // Recall of a legacy region leaves current relief state alone.
        var live = new FractalParameters { Relief2DEnabled = true, Relief2DRaymarch = true };
        back.ApplyRelief3DTo(live);
        Assert.True(live.Relief2DEnabled);
        Assert.True(live.Relief2DRaymarch);
    }
}
