using System;
using System.Collections.Generic;
using Xunit;
using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Imaging;

namespace FracturingFog.Server.Tests;

// #627 — chaotic billiard scatterer. Locks in that the calculator renders a
// non-degenerate outcome image (multiple escape gates → fractal basins), exposes
// a usable relief height field (bounce count), is deterministic, and routes
// colour through IBilliardColorMap when the active theme implements it.
public class ChaoticBilliardTests
{
    private static ChaoticBilliardCalculator Make(int size = 96, FractalParameters? p = null)
        => new(size, size)
        {
            CenterX = 0,
            CenterY = 0,
            Zoom = 1.0,
            MaxIterations = 256,
            FractalParameters = p ?? new FractalParameters(),
        };

    [Fact]
    public void Renders_Opaque_NonUniform_Image()
    {
        var calc = Make();
        calc.Calculate(default);

        var colours = new HashSet<uint>();
        foreach (uint c in calc.ColorBuffer)
        {
            Assert.Equal(0xFFu, c >> 24);          // always fully opaque
            colours.Add(c);
        }
        // Three-disk scatter with 6 gates must produce more than one outcome.
        Assert.True(colours.Count > 1, "billiard image is uniform (no basins)");
    }

    [Fact]
    public void Exposes_NonDegenerate_HeightField()
    {
        var calc = Make();
        calc.Calculate(default);

        Assert.IsAssignableFrom<IHeightFieldSource>(calc);
        float max = 0;
        var distinct = new HashSet<int>();
        foreach (float v in calc.SmoothBuffer)
        {
            if (v > max) max = v;
            distinct.Add((int)v);
        }
        Assert.True(max > 0, "billiard height field all zero (no bounces anywhere)");
        Assert.True(distinct.Count > 1, "billiard height field flat");
    }

    [Fact]
    public void Is_Deterministic()
    {
        var a = Make(); a.Calculate(default);
        var b = Make(); b.Calculate(default);
        Assert.Equal(a.ColorBuffer, b.ColorBuffer);
        Assert.Equal(a.SmoothBuffer, b.SmoothBuffer);
    }

    [Theory]
    [InlineData(BilliardGeometry.ThreeDisk)]
    [InlineData(BilliardGeometry.Ring)]
    [InlineData(BilliardGeometry.NDisk)]
    public void All_Geometries_Render(BilliardGeometry geom)
    {
        var calc = Make(p: new FractalParameters
        {
            BilliardGeometry = geom,
            BilliardDiskCount = 5,
        });
        calc.Calculate(default);
        var colours = new HashSet<uint>();
        foreach (uint c in calc.ColorBuffer) colours.Add(c);
        Assert.True(colours.Count > 1, $"{geom} produced a uniform image");
    }

    // Minimal IBilliardColorMap stub: paints gateId into the blue channel so we
    // can assert the calculator dispatched through the interface and passed a
    // valid gate range.
    private sealed class GateProbeTheme : IBilliardColorMap
    {
        public HashSet<int> SeenGates { get; } = new();
        public ColorPaletteType Type => ColorPaletteType.Algorithmic;
        public int MaxIterations { get; set; } = 256;
        public int Map(float smooth, float distance, int iterations) => 0;
        public int MapBilliard(int gateId, int gateCount, int bounces, int maxBounces, float pathLength)
        {
            lock (SeenGates) SeenGates.Add(gateId);
            Assert.InRange(gateId, -1, gateCount - 1);
            Assert.InRange(pathLength, 0f, 1f);
            Assert.InRange(bounces, 0, maxBounces);
            int g = gateId < 0 ? 0 : gateId + 1;
            return unchecked((int)0xFF000000 | (g & 0xFF));
        }
    }

    // Regression for the param-sync bug: the render/poster paths assign the live
    // FractalParameters onto the active calculator by concrete type. Billiard was
    // missing from both switches, so changing any geometry param did nothing on
    // screen. Assert the poster path (RenderToPixels) honours a geometry change.
    private static PosterRequest BilliardRequest(FractalParameters fp) => new()
    {
        FractalType = FractalType.ChaoticBilliard,
        CenterX = 0, CenterY = 0, Zoom = 1.0,
        MaxIterations = 256,
        Width = 96, Height = 96,
        ColorMap = new BilliardGatesMap(),
        Quality = QualityPreset.Standard,
        FractalParameters = fp,
        Path = "unused.png",
        Format = ImageFileFormat.Png,
    };

    [Fact]
    public void PosterPath_Honors_Billiard_Params()
    {
        var a = PosterRenderer.RenderToPixels(
            BilliardRequest(new FractalParameters { BilliardDiskRadius = 0.4, BilliardSeparation = 1.0 }),
            default, out _, out _);
        var b = PosterRenderer.RenderToPixels(
            BilliardRequest(new FractalParameters { BilliardDiskRadius = 0.9, BilliardSeparation = 1.6 }),
            default, out _, out _);

        Assert.Equal(a.Length, b.Length);
        Assert.NotEqual(a, b);   // geometry change must alter the image
    }

    [Fact]
    public void Dispatches_Through_IBilliardColorMap()
    {
        var theme = new GateProbeTheme();
        var calc = Make();
        calc.ColorMap = theme;
        calc.Calculate(default);

        // Three-disk parameter space should exercise multiple distinct gates.
        Assert.True(theme.SeenGates.Count > 1,
            "IBilliardColorMap saw only one outcome — no basin structure");
    }
}
