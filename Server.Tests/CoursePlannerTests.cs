// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System.Collections.Generic;
using System.Linq;

using FracturingFog;                 // FractalType
using FracturingFog.Slideshow;       // CoursePlanner
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// #789 slice A — CoursePlanner route ordering. Pure geometry, so these run
/// headless with hand-built RegionWaypoint sets.
/// </summary>
public sealed class CoursePlannerTests
{
    private static RegionWaypoint Wp(string name, double x, double y, double zoom,
        FractalType type = FractalType.Mandelbrot)
        => new(name, x, y, zoom, type);

    private static string[] Names(CoursePlanResult r) => r.Ordered.Select(w => w.Name).ToArray();

    [Fact]
    public void CollinearRegions_OrderedMonotonically_EndPinnedLast()
    {
        // Points on the x-axis at 0..4, equal zoom, handed in shuffled order.
        var regions = new[]
        {
            Wp("A", 0, 0, 1),
            Wp("C", 2, 0, 1),
            Wp("E", 4, 0, 1),
            Wp("B", 1, 0, 1),
            Wp("D", 3, 0, 1),
        };
        var result = CoursePlanner.Plan(regions, "A", "E");
        Assert.Null(result.Warning);
        Assert.Equal(new[] { "A", "B", "C", "D", "E" }, Names(result));
    }

    [Fact]
    public void FiltersToStartEndFractalType()
    {
        var regions = new[]
        {
            Wp("A", 0, 0, 1, FractalType.Mandelbrot),
            Wp("J1", 1, 0, 1, FractalType.Julia),      // wrong type — excluded
            Wp("B", 2, 0, 1, FractalType.Mandelbrot),
            Wp("E", 4, 0, 1, FractalType.Mandelbrot),
        };
        var result = CoursePlanner.Plan(regions, "A", "E");
        Assert.DoesNotContain("J1", Names(result));
        Assert.Equal(new[] { "A", "B", "E" }, Names(result));
    }

    [Fact]
    public void DifferentEndpointTypes_DirectHopWithWarning()
    {
        var regions = new[]
        {
            Wp("A", 0, 0, 1, FractalType.Mandelbrot),
            Wp("Z", 4, 0, 1, FractalType.Julia),
        };
        var result = CoursePlanner.Plan(regions, "A", "Z");
        Assert.Equal(new[] { "A", "Z" }, Names(result));
        Assert.NotNull(result.Warning);
    }

    [Fact]
    public void StartEqualsEnd_SingleStop()
    {
        var regions = new[] { Wp("A", 0, 0, 1), Wp("B", 1, 0, 1) };
        var result = CoursePlanner.Plan(regions, "A", "A");
        Assert.Equal(new[] { "A" }, Names(result));
    }

    [Fact]
    public void MissingEndpoint_WarnsAndReturnsEmpty()
    {
        var regions = new[] { Wp("A", 0, 0, 1), Wp("B", 1, 0, 1) };
        var result = CoursePlanner.Plan(regions, "A", "Nope");
        Assert.Empty(result.Ordered);
        Assert.NotNull(result.Warning);
    }

    [Fact]
    public void Corridor_ExcludesOffPathRegions()
    {
        // Start→end runs along x; "Off" sits far off the line in y.
        var regions = new[]
        {
            Wp("S", 0, 0, 1),
            Wp("Mid", 5, 0, 1),
            Wp("Off", 5, 100, 1),
            Wp("T", 10, 0, 1),
        };
        var opts = new CoursePlanOptions { CorridorRadius = 0.1 };
        var result = CoursePlanner.Plan(regions, "S", "T", opts);
        Assert.Contains("Mid", Names(result));
        Assert.DoesNotContain("Off", Names(result));
        Assert.Equal("S", Names(result).First());
        Assert.Equal("T", Names(result).Last());
    }

    [Fact]
    public void ZoomWeight_ChangesOrderingVsPlaneOnly()
    {
        // A is near S in the plane but very deep; B is a bit further in the plane
        // but at S's depth. With default weights B (plane-near) is visited first;
        // with WeightZoom = 0 (plane only) A (plane-nearest) wins.
        var regions = new[]
        {
            Wp("S", 0, 0, 1),
            Wp("A", 1, 0, 1_000_000),
            Wp("B", 2, 0, 1),
            Wp("T", 10, 0, 1),
        };

        var withZoom = Names(CoursePlanner.Plan(regions, "S", "T"));
        Assert.Equal(new[] { "S", "B", "A", "T" }, withZoom);

        var planeOnly = Names(CoursePlanner.Plan(regions, "S", "T",
            new CoursePlanOptions { WeightZoom = 0.0 }));
        Assert.Equal(new[] { "S", "A", "B", "T" }, planeOnly);
    }

    [Fact]
    public void TwoOpt_RemovesBacktracking()
    {
        // A zig-zag input set that a naive order would cross; the planner should
        // return the monotone x-order for these collinear points.
        var regions = new[]
        {
            Wp("S", 0, 0, 1),
            Wp("P3", 3, 0, 1),
            Wp("P1", 1, 0, 1),
            Wp("P4", 4, 0, 1),
            Wp("P2", 2, 0, 1),
            Wp("T", 5, 0, 1),
        };
        var result = CoursePlanner.Plan(regions, "S", "T");
        Assert.Equal(new[] { "S", "P1", "P2", "P3", "P4", "T" }, Names(result));
    }
}
