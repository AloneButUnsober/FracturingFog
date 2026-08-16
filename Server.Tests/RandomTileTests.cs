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
        double gap = 0.0, double minPx = 0.75, double relief = 1.0)
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
}
