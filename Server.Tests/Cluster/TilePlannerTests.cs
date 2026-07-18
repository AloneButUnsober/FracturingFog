// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;

using FracturingFog.Server.Cluster;
using FracturingFog.Server.Protocol;
using Xunit;

namespace FracturingFog.Server.Tests.Cluster;

public sealed class TilePlannerTests
{
    private static RenderRequestDto Req(int w = 2048, int h = 1024) => new()
    {
        Mode        = "image",
        FractalType = "Mandelbrot",
        Width       = w,
        Height      = h,
        CenterX     = -0.75,
        CenterY     = 0.0,
        Zoom        = 1.0,
    };

    [Fact]
    public void Plan_Splits_Image_Into_Grid_Of_Target_Tiles()
    {
        var plan = TilePlanner.PlanImage(Req(2048, 1024), tilePixelsHint: 512);
        Assert.Equal(2048, plan.ImageWidth);
        Assert.Equal(1024, plan.ImageHeight);
        Assert.Equal(4, plan.Columns);    // 2048 / 512
        Assert.Equal(2, plan.Rows);       // 1024 / 512
        Assert.Equal(8, plan.TileCount);
    }

    [Fact]
    public void Plan_Edge_Tiles_Get_Remainder_Pixels()
    {
        var plan = TilePlanner.PlanImage(Req(513, 513), tilePixelsHint: 256);
        Assert.Equal(3, plan.Columns);  // ceil(513/256)
        Assert.Equal(3, plan.Rows);

        // Last col + last row should be 1px wide/tall (513 - 256*2 = 1).
        var lastTile = plan.Tiles[plan.TileCount - 1];
        Assert.Equal(512, lastTile.OffsetX);
        Assert.Equal(512, lastTile.OffsetY);
        Assert.Equal(1, lastTile.Render.Width);
        Assert.Equal(1, lastTile.Render.Height);
    }

    [Fact]
    public void Plan_Tile_Ids_Are_Dense_And_Ordered()
    {
        var plan = TilePlanner.PlanImage(Req(1024, 1024), tilePixelsHint: 256);
        Assert.Equal(16, plan.TileCount);
        for (int i = 0; i < plan.TileCount; i++)
            Assert.Equal(i, plan.Tiles[i].TileId);
    }

    [Fact]
    public void Plan_Per_Tile_Render_Has_Translated_Center_Same_Scale()
    {
        // Full image: 1024×512, Zoom=1.0 → scale = 3.5 / 1024 / 1.0
        // Tile (col=1, row=0, offX=512, offY=0, tW=512, tH=512). Wait —
        // tH should be min(512, 512) = 512 only if Height >= 512. Use
        // Height=512 to keep this clean.
        var req = Req(1024, 512);
        req.Zoom = 1.0;
        req.CenterX = 10.0;
        req.CenterY = 20.0;

        var plan = TilePlanner.PlanImage(req, tilePixelsHint: 512);
        Assert.Equal(2, plan.TileCount);

        double expectedScale = 3.5 / 1024.0 / 1.0;

        // Tile 0: offX=0, offY=0, tW=512, tH=512.
        var t0 = plan.Tiles[0];
        double expScale0 = 3.5 / Math.Max(t0.Render.Width, t0.Render.Height) / t0.Render.Zoom!.Value;
        Assert.True(Math.Abs(expScale0 - expectedScale) < 1e-12,
            $"tile0 scale {expScale0} != full scale {expectedScale}");

        // Tile 1: offX=512, offY=0, tW=512, tH=512. Center translated
        // by (offX + tW/2 - W/2) * scale = (512 + 256 - 512) * scale = 256 * scale.
        var t1 = plan.Tiles[1];
        double expCx1 = 10.0 + 256.0 * expectedScale;
        Assert.True(Math.Abs(t1.Render.CenterX!.Value - expCx1) < 1e-12,
            $"tile1 centerX {t1.Render.CenterX} != expected {expCx1}");
    }

    [Fact]
    public void Plan_Refuses_Zero_Zoom()
    {
        var req = Req();
        req.Zoom = 0;
        var ex = Assert.Throws<ArgumentException>(() => TilePlanner.PlanImage(req, 512));
        Assert.Contains("Zoom", ex.Message);
    }

    [Fact]
    public void Plan_Uses_Median_Worker_Hint_When_No_Explicit_Hint()
    {
        var hints = new List<int> { 128, 256, 512, 1024, 8192 };
        var plan = TilePlanner.PlanImage(Req(1024, 1024), tilePixelsHint: 0, workerPrefHints: hints);
        Assert.Equal(512, plan.TileTargetPixels);  // median
    }

    [Fact]
    public void Plan_Defaults_To_512_When_No_Hint_Available()
    {
        var plan = TilePlanner.PlanImage(Req(), tilePixelsHint: 0, workerPrefHints: null);
        Assert.Equal(512, plan.TileTargetPixels);
    }

    // ── D-3b adaptive sizing ───────────────────────────────────────────

    [Fact]
    public void Adaptive_Sizing_Picks_Side_For_Median_Worker()
    {
        // 1 ms/kpx workers, 2000 ms target → 2000 kpx = 2_000_000 px → ~1414 side.
        int side = TilePlanner.ComputeAdaptiveTilePixels(medianMsPerKilopixel: 1.0, targetTileMs: 2000.0);
        Assert.InRange(side, 1400, 1430);
    }

    [Fact]
    public void Adaptive_Sizing_Falls_Back_When_No_Data()
    {
        Assert.Equal(0, TilePlanner.ComputeAdaptiveTilePixels(0, 2000));
        Assert.Equal(0, TilePlanner.ComputeAdaptiveTilePixels(1.0, 0));
    }

    [Fact]
    public void Plan_Uses_Adaptive_Tile_Size_When_Median_Provided()
    {
        // Fast workers: 0.1 ms/kpx, 200 ms target → 200/0.1 = 2000 kpx → side ≈ 1414.
        var plan = TilePlanner.PlanImage(
            Req(8192, 8192),
            tilePixelsHint: 0,
            workerPrefHints: null,
            medianMsPerKilopixel: 0.1,
            targetTileMs: 200);
        Assert.InRange(plan.TileTargetPixels, 1400, 1430);
    }

    [Fact]
    public void Plan_Explicit_Hint_Overrides_Adaptive()
    {
        var plan = TilePlanner.PlanImage(
            Req(2048, 2048),
            tilePixelsHint: 256,
            workerPrefHints: new List<int> { 1024 },
            medianMsPerKilopixel: 0.5,
            targetTileMs: 2000);
        Assert.Equal(256, plan.TileTargetPixels);
    }

    [Theory]
    [InlineData("Mandelbrot",   true)]
    [InlineData("BurningShip",  true)]
    [InlineData("Julia",        true)]
    [InlineData("LSystem",      false)]
    [InlineData("Mandelbulb",   false)]
    [InlineData("IFS",          false)]
    [InlineData("UserEquation", false)]
    public void ValidateForTiling_Allows_Cartesian_Refuses_Others(string type, bool ok)
    {
        bool got = TilePlanner.ValidateForTiling(type, out string? why);
        Assert.Equal(ok, got);
        if (!ok) Assert.NotNull(why);
    }

    [Fact]
    public void Plan_Clones_Theme_And_Region_Fields_Into_Each_Tile()
    {
        var req = Req();
        req.ThemeName  = "InkyOcean";
        req.RegionName = "Bird of Paradise";
        req.Iterations = 4096;

        var plan = TilePlanner.PlanImage(req, 512);
        foreach (var t in plan.Tiles)
        {
            Assert.Equal("InkyOcean", t.Render.ThemeName);
            Assert.Equal("Bird of Paradise", t.Render.RegionName);
            Assert.Equal(4096, t.Render.Iterations);
            Assert.Equal("Mandelbrot", t.Render.FractalType);
        }
    }

    [Fact]
    public void Plan_Forces_Inline_ReturnMode_On_Every_Tile()
    {
        var req = Req();
        req.ReturnMode = "saved-path";  // intentionally wrong for tiles

        var plan = TilePlanner.PlanImage(req, 512);
        foreach (var t in plan.Tiles)
            Assert.Equal("inline", t.Render.ReturnMode);
    }
}
