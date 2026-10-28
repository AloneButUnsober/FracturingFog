// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using FracturingFog;
using FracturingFog.Imaging;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1512 — the Billiard Rays presets. Each one is checked against what its description
// promises (independently of the renderer where possible), then rendered at app defaults
// through the poster renderer, the path --batch --region uses.
public class BilliardRaysPresetTests
{
    private const string GateFlow = "Billiard Rays - Three-Disk Gate Flow";
    private const string RingCrossings = "Billiard Rays - Ring Crossings";
    private const string CustomDivergence = "Billiard Rays - Custom Five-Disk Divergence";

    private static (FractalRegion Region, FractalParameters P) Preset(string name)
    {
        var region = FractalRegionLibrary.Instance.BuiltIns.Single(r => r.Name == name);
        Assert.Equal(FractalType.BilliardRays, region.FractalType);
        Assert.Equal(RegionType.BuiltIn, region.RegionType);
        var p = new FractalParameters();
        region.Params!.ApplyTo(p);
        return (region, p);
    }

    private static BilliardRaysCalculator Render(FractalRegion region, FractalParameters p)
    {
        var c = new BilliardRaysCalculator(160, 120)
        {
            FractalParameters = p, CenterX = region.CenterX, CenterY = region.CenterY, Zoom = region.Zoom,
            MaxIterations = region.Iterations,
        };
        c.Calculate(TestContext.Current.CancellationToken);
        return c;
    }

    [Fact]
    public void Gate_Flow_Colours_Three_Beams_By_Exit_Gap()
    {
        var (region, p) = Preset(GateFlow);
        Assert.Equal(BilliardGeometry.ThreeDisk, p.BilliardGeometry);
        Assert.Equal(BilliardRaySweep.BSweep, p.BilliardRaysSweep);
        Assert.Equal(3, p.BilliardRaysAngleCount);
        Assert.Equal(BilliardRayColorSource.Gate, p.BilliardRaysColorSource);
        Assert.Equal(BilliardGateMode.ExitGap, p.BilliardGateMode);
        var c = Render(region, p);
        Assert.True(c.LastRaysDrawn > 1000);
        Assert.True(c.TintWeight.Sum() > 0);
    }

    [Fact]
    public void Ring_Crossings_Uses_An_Open_Ring_With_Relief()
    {
        var (region, p) = Preset(RingCrossings);
        Assert.Equal(BilliardGeometry.Ring, p.BilliardGeometry);
        Assert.Empty(BilliardLayout.Warning(p.BilliardGeometry, p.BilliardDiskCount, p.BilliardDiskRadius, p.BilliardSeparation));
        Assert.True(BilliardLayout.RingNeighbourGap(p.BilliardDiskCount, p.BilliardDiskRadius, p.BilliardSeparation) > 0);
        Assert.Equal(BilliardRayMode.Crossings, p.BilliardRaysMode);
        Assert.Equal(BilliardCrossingValue.DeltaN, p.BilliardRaysCrossingValue);
        Assert.True(region.Relief3D is { Enabled: true });
        var c = Render(region, p);
        Assert.True(c.LastCrossings > 1000);
        Assert.Contains(c.SmoothBuffer, s => s > 0);   // the relief height field
    }

    [Fact]
    public void Custom_Divergence_Layout_Is_Valid_And_Draws_The_Boundary()
    {
        var (region, p) = Preset(CustomDivergence);
        Assert.Equal(BilliardGeometry.Custom, p.BilliardGeometry);
        var disks = BilliardLayout.ParseCustomDisks(p.BilliardCustomDisks);
        Assert.Equal(5, disks.Count);
        for (int i = 0; i < disks.Count; i++)
            Assert.True(BilliardLayout.CanPlace(disks, i, disks[i]), $"disk {i} breaks the editor's layout rules");
        Assert.Empty(BilliardLayout.Warning(p.BilliardGeometry, 0, 0, 0, p.BilliardCustomDisks));
        Assert.Equal(BilliardRayMode.DivergenceOverPaths, p.BilliardRaysMode);
        Assert.Equal(BilliardDivergenceValue.GateChange, p.BilliardRaysDivergenceValue);
        var c = Render(region, p);
        Assert.True(c.LastDivergences > 10);
        Assert.True(c.Density.Sum() > 0);
    }

    [Theory]
    [InlineData(GateFlow)]
    [InlineData(RingCrossings)]
    [InlineData(CustomDivergence)]
    public void Preset_Renders_Through_The_Poster_Renderer(string name)
    {
        var (region, p) = Preset(name);
        var req = new PosterRequest
        {
            FractalType = region.FractalType, Width = 160, Height = 120,
            CenterX = region.CenterX, CenterY = region.CenterY, Zoom = region.Zoom,
            MaxIterations = region.Iterations, Quality = region.QualityPreset,
            FractalParameters = p, ColorMap = new HsvPalette(),
        };
        var pixels = PosterRenderer.RenderToPixels(req, TestContext.Current.CancellationToken, out int w, out int h);
        Assert.Equal(160 * 120, w * h);
        int distinct = pixels.Distinct().Count();
        Assert.True(distinct > 20, $"{name} rendered only {distinct} colours");
    }
}
