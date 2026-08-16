using System;
using System.Linq;
using Xunit;
using FracturingFog;
using FracturingFog.Models;

namespace FracturingFog.Server.Tests;

// #332 — RandomTile (Paul Bourke, random space filling of the plane) core
// calculator contract: determinism, monotonic fill, sub-pixel floor, relief.
public class RandomTileTests
{
    private const int W = 256, H = 192;

    private static RandomTileCalculator Render(
        int seed = 1, int count = 3000, double alpha = 1.6,
        double gap = 0.0, double minPx = 0.75, double relief = 1.0,
        RandomTileShape shape = RandomTileShape.Circle)
    {
        var calc = new RandomTileCalculator(W, H)
        {
            CenterX = 0, CenterY = 0, Zoom = 1.0,
            FractalParameters = new FractalParameters
            {
                RandomTileSeed = seed,
                RandomTileCount = count,
                RandomTileSizeExponent = alpha,
                RandomTileGap = gap,
                RandomTileMinPixelRadius = minPx,
                RandomTileRelief = relief,
                RandomTileShape = shape,
            },
        };
        calc.Calculate();
        return calc;
    }

    private static int PaintedPixels(uint[] buf) =>
        buf.Count(p => (p & 0x00FFFFFFu) != 0);

    [Fact]
    public void SameSeed_Produces_ByteIdentical_Buffer()
    {
        var a = Render(seed: 42);
        var b = Render(seed: 42);
        Assert.True(a.ColorBuffer.AsSpan().SequenceEqual(b.ColorBuffer));
    }

    [Fact]
    public void DifferentSeed_Produces_Different_Buffer()
    {
        var a = Render(seed: 42);
        var b = Render(seed: 43);
        Assert.False(a.ColorBuffer.AsSpan().SequenceEqual(b.ColorBuffer));
    }

    [Fact]
    public void MoreShapes_Paints_More_Pixels()
    {
        int few = PaintedPixels(Render(seed: 1, count: 200).ColorBuffer);
        int many = PaintedPixels(Render(seed: 1, count: 6000).ColorBuffer);
        Assert.True(few > 0, "some pixels painted at low count");
        Assert.True(many > few, $"more shapes should paint more pixels ({few} -> {many})");
    }

    [Fact]
    public void Fills_Plane_But_Leaves_Gaps()
    {
        // A random packing covers a large fraction of the frame yet never the
        // whole thing — background always shows between shapes.
        int painted = PaintedPixels(Render(seed: 1, count: 8000).ColorBuffer);
        int total = W * H;
        Assert.InRange(painted, total / 10, total - 1);
    }

    [Fact]
    public void Relief_Writes_NonTrivial_HeightField()
    {
        var calc = Render(seed: 1, count: 3000, relief: 1.0);
        int nonZero = calc.SmoothBuffer.Count(h => h > 0f);
        Assert.True(nonZero > 0, "dome relief height field should be non-empty");
        // Dome peaks near 1.0 at shape centres.
        Assert.True(calc.SmoothBuffer.Max() > 0.5f);
    }

    [Fact]
    public void HigherMinPixelRadius_Paints_Fewer_Or_Equal_Shapes()
    {
        // Raising the sub-pixel floor stops placement earlier → fewer tiny
        // shapes → no more painted pixels than the permissive floor.
        int fine = PaintedPixels(Render(seed: 1, count: 8000, minPx: 0.75).ColorBuffer);
        int coarse = PaintedPixels(Render(seed: 1, count: 8000, minPx: 8.0).ColorBuffer);
        Assert.True(coarse <= fine, $"coarse floor should not paint more ({coarse} vs {fine})");
    }

    [Fact]
    public void ZeroRelief_FlatPath_Still_Paints()
    {
        var calc = Render(seed: 1, count: 2000, relief: 0.0);
        Assert.True(PaintedPixels(calc.ColorBuffer) > 0);
    }

    // ── P3: shapes ──

    [Theory]
    [InlineData(RandomTileShape.Circle)]
    [InlineData(RandomTileShape.Square)]
    [InlineData(RandomTileShape.Triangle)]
    public void EveryShape_Paints_And_Leaves_Gaps(RandomTileShape shape)
    {
        int painted = PaintedPixels(Render(seed: 2, count: 4000, shape: shape).ColorBuffer);
        Assert.InRange(painted, W * H / 20, W * H - 1);
    }

    [Fact]
    public void Shape_Changes_The_Tiling()
    {
        var circle = Render(seed: 2, count: 2000, shape: RandomTileShape.Circle);
        var square = Render(seed: 2, count: 2000, shape: RandomTileShape.Square);
        var tri = Render(seed: 2, count: 2000, shape: RandomTileShape.Triangle);
        Assert.False(circle.ColorBuffer.AsSpan().SequenceEqual(square.ColorBuffer));
        Assert.False(square.ColorBuffer.AsSpan().SequenceEqual(tri.ColorBuffer));
    }

    [Theory]
    [InlineData(RandomTileShape.Square)]
    [InlineData(RandomTileShape.Triangle)]
    public void Polygon_Placement_Is_Deterministic(RandomTileShape shape)
    {
        var a = Render(seed: 5, count: 2500, shape: shape);
        var b = Render(seed: 5, count: 2500, shape: shape);
        Assert.True(a.ColorBuffer.AsSpan().SequenceEqual(b.ColorBuffer));
    }

    // ── #338: placement cache ──

    [Fact]
    public void PlacementCache_Is_Transparent_To_Output()
    {
        // Warm instance: render once (builds the cache), then flip a shading-only
        // param (relief) and render again — this exercises the cache-reuse path.
        var warm = new RandomTileCalculator(W, H)
        {
            CenterX = 0, CenterY = 0, Zoom = 1.0,
            FractalParameters = new FractalParameters
            { RandomTileSeed = 4, RandomTileCount = 3000, RandomTileRelief = 1.0 },
        };
        warm.Calculate();
        warm.FractalParameters.RandomTileRelief = 0.0;
        warm.Calculate();

        // Cold instance built directly at relief 0 (no cache reuse).
        var cold = new RandomTileCalculator(W, H)
        {
            CenterX = 0, CenterY = 0, Zoom = 1.0,
            FractalParameters = new FractalParameters
            { RandomTileSeed = 4, RandomTileCount = 3000, RandomTileRelief = 0.0 },
        };
        cold.Calculate();

        // Reused placement must yield byte-identical output to a cold render.
        Assert.True(warm.ColorBuffer.AsSpan().SequenceEqual(cold.ColorBuffer));
    }

    [Fact]
    public void PlacementCache_Rebuilds_When_Placement_Param_Changes()
    {
        var calc = new RandomTileCalculator(W, H)
        {
            CenterX = 0, CenterY = 0, Zoom = 1.0,
            FractalParameters = new FractalParameters
            { RandomTileSeed = 4, RandomTileCount = 3000, RandomTileSizeExponent = 1.6 },
        };
        calc.Calculate();
        var first = (uint[])calc.ColorBuffer.Clone();

        // A placement-determining change must invalidate the cache → new tiling.
        calc.FractalParameters.RandomTileSizeExponent = 2.4;
        calc.Calculate();
        Assert.False(first.AsSpan().SequenceEqual(calc.ColorBuffer));
    }

    [Fact]
    public void Circle_Path_Unchanged_By_Shape_Feature()
    {
        // The Circle branch must stay byte-identical to the shape-less behaviour:
        // no rotation draw, inside ⇔ dd ≤ r². Two circle renders match exactly.
        var a = Render(seed: 9, count: 3000, shape: RandomTileShape.Circle);
        var b = Render(seed: 9, count: 3000);   // default shape == Circle
        Assert.True(a.ColorBuffer.AsSpan().SequenceEqual(b.ColorBuffer));
    }
}
