// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

    // ── Froxel volumetrics on the region snapshot (#408, S6) ──────────────────
    // The relief snapshot now also carries froxel fog + its cross-frame temporal
    // reprojection so a region-sourced scene / batch / slideshow render can turn
    // froxel on without a live UI (previously froxel was UI/CLI-only, so the whole
    // #562/#563/#565 temporal path was unreachable offline).

    [Fact]
    public void Snapshot_CapturesFroxel_AndRoundTrips()
    {
        var src = new FractalParameters
        {
            Relief2DEnabled = true,
            Relief2DRaymarch = true,
            Relief2DFroxelVolumetrics = true,
            Relief2DFroxelTemporal = true,
            Relief2DFroxelTemporalFeedback = 0.72,
            Relief2DFroxelQuality = FroxelQuality.High,
        };

        var snap = Relief3DSettings.Snapshot(src);
        Assert.NotNull(snap);
        Assert.True(snap!.FroxelVolumetrics);
        Assert.True(snap.FroxelTemporal);
        Assert.Equal(0.72, snap.FroxelTemporalFeedback, 12);
        Assert.Equal(FroxelQuality.High, snap.FroxelQuality);

        var dst = new FractalParameters();
        snap.ApplyTo(dst);
        Assert.True(dst.Relief2DFroxelVolumetrics);
        Assert.True(dst.Relief2DFroxelTemporal);
        Assert.Equal(0.72, dst.Relief2DFroxelTemporalFeedback, 12);
        Assert.Equal(FroxelQuality.High, dst.Relief2DFroxelQuality);
    }

    [Fact]
    public void Region_SerializesFroxel_AndRoundTrips()
    {
        var region = new FractalRegion
        {
            Name = "Foggy", FractalType = FractalType.Mandelbrot,
            CenterX = -0.75, CenterY = 0.0, Zoom = 1.0,
            Relief3D = Relief3DSettings.Snapshot(new FractalParameters
            {
                Relief2DEnabled = true, Relief2DRaymarch = true,
                Relief2DFroxelVolumetrics = true,
                Relief2DFroxelTemporal = true,
                Relief2DFroxelTemporalFeedback = 0.65,
                Relief2DFroxelQuality = FroxelQuality.High,
            }),
        };

        string json = JsonSerializer.Serialize(region, new JsonSerializerOptions { WriteIndented = true });
        Assert.Contains("\"FroxelQuality\": \"High\"", json);   // enum-as-string

        var back = JsonSerializer.Deserialize<FractalRegion>(json);
        var applied = new FractalParameters();
        back!.ApplyRelief3DTo(applied);
        Assert.True(applied.Relief2DFroxelVolumetrics);
        Assert.True(applied.Relief2DFroxelTemporal);
        Assert.Equal(0.65, applied.Relief2DFroxelTemporalFeedback, 12);
        Assert.Equal(FroxelQuality.High, applied.Relief2DFroxelQuality);
    }

    [Fact]
    public void Froxel_DefaultsOff_WhenReliefSnapshotHasNoFroxel()
    {
        // A relief snapshot from froxel-off params leaves froxel off on apply — the
        // byte-identical default, so plain relief regions don't silently gain fog.
        var snap = Relief3DSettings.Snapshot(new FractalParameters
        {
            Relief2DEnabled = true, Relief2DRaymarch = true,
        });
        var dst = new FractalParameters
        {
            Relief2DFroxelVolumetrics = true,   // pre-armed; apply must clear it
            Relief2DFroxelTemporal = true,
        };
        snap!.ApplyTo(dst);
        Assert.False(dst.Relief2DFroxelVolumetrics);
        Assert.False(dst.Relief2DFroxelTemporal);
    }

    [Fact]
    public void Authoritative_PlainRegion_ClearsFroxel()
    {
        var plain = new FractalRegion { Name = "Plain", FractalType = FractalType.Mandelbrot };
        var live = new FractalParameters
        {
            Relief2DEnabled = true, Relief2DRaymarch = true,
            Relief2DFroxelVolumetrics = true, Relief2DFroxelTemporal = true,
        };
        plain.ApplyRelief3DAuthoritative(live);
        Assert.False(live.Relief2DFroxelVolumetrics);
        Assert.False(live.Relief2DFroxelTemporal);
    }

    // ── Guided denoise + SVGF temporal on the region snapshot (#402, S4) ──────
    // The relief snapshot now also carries the À-Trous denoise + its SVGF temporal
    // accumulation / variance guiding, so a region-sourced scene / batch render can
    // turn SVGF on without a live UI (the offline sequence seam is already wired).

    [Fact]
    public void Snapshot_CapturesDenoiseSvgf_AndRoundTrips()
    {
        var src = new FractalParameters
        {
            Relief2DEnabled = true,
            Relief2DRaymarch = true,
            Relief2DDenoiseIterations = 4,
            Relief2DDenoiseColorSigma = 0.08,
            Relief2DDenoiseNormalSigma = 0.25,
            Relief2DDenoiseDepthSigma = 0.15,
            Relief2DDenoiseAdaptiveSupersample = true,
            Relief2DDenoiseTemporal = true,
            Relief2DDenoiseTemporalFeedback = 0.72,
            Relief2DDenoiseVarianceScale = 6.0,
        };

        var snap = Relief3DSettings.Snapshot(src);
        Assert.NotNull(snap);
        Assert.Equal(4, snap!.DenoiseIterations);
        Assert.True(snap.DenoiseTemporal);
        Assert.Equal(0.72, snap.DenoiseTemporalFeedback, 12);
        Assert.Equal(6.0, snap.DenoiseVarianceScale, 12);

        var dst = new FractalParameters();
        snap.ApplyTo(dst);
        Assert.Equal(4, dst.Relief2DDenoiseIterations);
        Assert.Equal(0.08, dst.Relief2DDenoiseColorSigma, 12);
        Assert.True(dst.Relief2DDenoiseAdaptiveSupersample);
        Assert.True(dst.Relief2DDenoiseTemporal);
        Assert.Equal(0.72, dst.Relief2DDenoiseTemporalFeedback, 12);
        Assert.Equal(6.0, dst.Relief2DDenoiseVarianceScale, 12);
    }

    [Fact]
    public void Region_SerializesDenoiseSvgf_AndRoundTrips()
    {
        var region = new FractalRegion
        {
            Name = "Denoised", FractalType = FractalType.Mandelbrot,
            CenterX = -0.75, CenterY = 0.0, Zoom = 1.0,
            Relief3D = Relief3DSettings.Snapshot(new FractalParameters
            {
                Relief2DEnabled = true, Relief2DRaymarch = true,
                Relief2DDenoiseIterations = 3,
                Relief2DDenoiseTemporal = true,
                Relief2DDenoiseTemporalFeedback = 0.6,
                Relief2DDenoiseVarianceScale = 5.0,
            }),
        };

        string json = JsonSerializer.Serialize(region, new JsonSerializerOptions { WriteIndented = true });
        var back = JsonSerializer.Deserialize<FractalRegion>(json);
        var applied = new FractalParameters();
        back!.ApplyRelief3DTo(applied);
        Assert.Equal(3, applied.Relief2DDenoiseIterations);
        Assert.True(applied.Relief2DDenoiseTemporal);
        Assert.Equal(0.6, applied.Relief2DDenoiseTemporalFeedback, 12);
        Assert.Equal(5.0, applied.Relief2DDenoiseVarianceScale, 12);
    }

    [Fact]
    public void Denoise_DefaultsOff_WhenReliefSnapshotHasNoDenoise()
    {
        var snap = Relief3DSettings.Snapshot(new FractalParameters
        {
            Relief2DEnabled = true, Relief2DRaymarch = true,
        });
        var dst = new FractalParameters
        {
            Relief2DDenoiseIterations = 5,   // pre-armed; apply must clear it
            Relief2DDenoiseTemporal = true,
        };
        snap!.ApplyTo(dst);
        Assert.Equal(0, dst.Relief2DDenoiseIterations);
        Assert.False(dst.Relief2DDenoiseTemporal);
    }

    [Fact]
    public void Authoritative_PlainRegion_ClearsDenoise()
    {
        var plain = new FractalRegion { Name = "Plain", FractalType = FractalType.Mandelbrot };
        var live = new FractalParameters
        {
            Relief2DEnabled = true, Relief2DRaymarch = true,
            Relief2DDenoiseIterations = 4, Relief2DDenoiseTemporal = true,
        };
        plain.ApplyRelief3DAuthoritative(live);
        Assert.Equal(0, live.Relief2DDenoiseIterations);
        Assert.False(live.Relief2DDenoiseTemporal);
    }

    // ── Guard: every Relief2D* property is captured or explicitly excluded ─────
    // The snapshot silently lagged FractalParameters for a long time (#518 detail,
    // #520 far detail, #592 height source, S1/S3/S6 camera + relight knobs), so a
    // saved relief view recalled with different terrain. These tests reflect over
    // the live Relief2D* family and check the ROUND TRIP (Snapshot → JSON →
    // ApplyTo), not a hand-kept field list, so a new property fails here until it
    // is either captured or added to the exclusion list with a reason.

    /// <summary>Relief2D* properties deliberately NOT saved on a region.</summary>
    private static readonly Dictionary<string, string> NotCaptured = new()
    {
        [nameof(FractalParameters.Relief2DGpuRaymarch)] =
            "CPU vs GPU dispatch preference (host/backend choice at CPU-twin parity), not part of the view",
        [nameof(FractalParameters.Relief2DEmptySkip)] =
            "conservative step-count acceleration over the same surface; speed only, never the image",
    };

    private static PropertyInfo[] Relief2DProperties() =>
        typeof(FractalParameters)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(pi => pi.Name.StartsWith("Relief2D", StringComparison.Ordinal)
                         && pi.CanRead && pi.CanWrite && pi.GetIndexParameters().Length == 0)
            .OrderBy(pi => pi.Name, StringComparer.Ordinal)
            .ToArray();

    /// <summary>A value guaranteed different from <paramref name="current"/>.</summary>
    private static object Perturb(Type t, object? current)
    {
        if (t == typeof(bool))   return !(bool)current!;
        if (t == typeof(int))    return (int)current! + 3;
        if (t == typeof(uint))   return (uint)current! ^ 0x00FF00FFu;
        if (t == typeof(double)) return (double)current! + 0.375;
        if (t == typeof(string)) return (string?)current + "FF102030";
        if (t.IsEnum)
        {
            var values = Enum.GetValues(t);
            Assert.True(values.Length > 1, $"enum {t.Name} has one value; can't perturb");
            int i = Array.IndexOf(values, current);
            return values.GetValue((i + 1) % values.Length)!;
        }
        throw new InvalidOperationException(
            $"Relief2D property type {t.Name} has no perturbation — extend {nameof(Perturb)}.");
    }

    private static Relief3DSettings JsonRoundTrip(Relief3DSettings s)
        => JsonSerializer.Deserialize<Relief3DSettings>(JsonSerializer.Serialize(s))!;

    [Fact]
    public void Guard_EveryRelief2DProperty_IsCapturedOrExplicitlyExcluded()
    {
        var props = Relief2DProperties();
        Assert.NotEmpty(props);

        // Exclusion list must name real properties (no stale entries).
        var names = props.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var excluded in NotCaptured.Keys)
            Assert.True(names.Contains(excluded), $"exclusion '{excluded}' is not a Relief2D* property");

        // Perturb every property away from its default at once; Enabled must be on
        // for Snapshot to produce anything, and perturbing a false default gives that.
        var src = new FractalParameters();
        foreach (var pi in props)
            pi.SetValue(src, Perturb(pi.PropertyType, pi.GetValue(src)));
        Assert.True(src.Relief2DEnabled);

        var snap = Relief3DSettings.Snapshot(src);
        Assert.NotNull(snap);
        var dst = new FractalParameters();
        JsonRoundTrip(snap!).ApplyTo(dst);

        var dropped = new List<string>();
        var staleExclusions = new List<string>();
        foreach (var pi in props)
        {
            bool roundTrips = Equals(pi.GetValue(src), pi.GetValue(dst));
            bool excluded = NotCaptured.ContainsKey(pi.Name);
            if (!roundTrips && !excluded) dropped.Add(pi.Name);
            if (roundTrips && excluded) staleExclusions.Add(pi.Name);
        }

        Assert.True(dropped.Count == 0,
            "Relief2D* properties dropped by Relief3DSettings (capture them in Snapshot/ApplyTo " +
            "with a FractalParameters-matching default, or add to NotCaptured with a reason): " +
            string.Join(", ", dropped));
        Assert.True(staleExclusions.Count == 0,
            "Relief2D* properties listed in NotCaptured but actually captured (remove from the list): " +
            string.Join(", ", staleExclusions));
    }

    [Fact]
    public void Guard_LegacySnapshot_RecallsEveryCapturedFieldAtFractalParametersDefault()
    {
        // A region saved before a field was captured has no key for it; recall must
        // land that field on the FractalParameters default (i.e. the value the old
        // region rendered with), not leave whatever the live view had.
        var legacy = JsonSerializer.Deserialize<Relief3DSettings>("{\"Enabled\":true}")!;
        var defaults = new FractalParameters();

        var live = new FractalParameters();
        var props = Relief2DProperties();
        foreach (var pi in props)
            pi.SetValue(live, Perturb(pi.PropertyType, pi.GetValue(live)));
        legacy.ApplyTo(live);

        var wrong = new List<string>();
        foreach (var pi in props)
        {
            if (NotCaptured.ContainsKey(pi.Name) || pi.Name == nameof(FractalParameters.Relief2DEnabled))
                continue;
            object? want = pi.GetValue(defaults), got = pi.GetValue(live);
            if (!Equals(want, got)) wrong.Add($"{pi.Name} (default {want}, recalled {got})");
        }
        Assert.True(wrong.Count == 0,
            "Relief3DSettings defaults disagree with FractalParameters: " + string.Join("; ", wrong));
        Assert.True(live.Relief2DEnabled);
    }

    [Fact]
    public void Region_SerializesHeightSource_AsString()
    {
        var region = new FractalRegion
        {
            Name = "Trap relief", FractalType = FractalType.Mandelbrot,
            Relief3D = Relief3DSettings.Snapshot(new FractalParameters
            {
                Relief2DEnabled = true,
                Relief2DHeightSource = ReliefHeightSource.Blend,
                Relief2DHeightBlend = 0.3,
                Relief2DDetailGain = 2.5,
            }),
        };
        string json = JsonSerializer.Serialize(region, new JsonSerializerOptions { WriteIndented = true });
        Assert.Contains("\"HeightSource\": \"Blend\"", json);   // enum-as-string

        var applied = new FractalParameters();
        JsonSerializer.Deserialize<FractalRegion>(json)!.ApplyRelief3DTo(applied);
        Assert.Equal(ReliefHeightSource.Blend, applied.Relief2DHeightSource);
        Assert.Equal(0.3, applied.Relief2DHeightBlend, 12);
        Assert.Equal(2.5, applied.Relief2DDetailGain, 12);
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
