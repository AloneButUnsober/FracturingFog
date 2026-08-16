// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using FracturingFog;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Lighting;

namespace FracturingFog.Server.Tests;

// #333 (P2) — RandomTile relief / 3D pipeline. The calculator synthesises a
// sphere-cap dome per shape into SmoothBuffer, so it must (a) expose a real
// IHeightFieldSource, (b) get a non-degenerate hi-res relief twin from the
// render-host factory (same path as Apollonian), and (c) actually shade the
// domes when a normal-mapped 3D theme reads the per-pixel normal.
public class RandomTileReliefTests
{
    private static RandomTileCalculator Render(IColorMap map, double relief = 1.0)
    {
        var calc = new RandomTileCalculator(160, 160)
        {
            CenterX = 0, CenterY = 0, Zoom = 1.0,
            ColorMap = map,
            FractalParameters = new FractalParameters
            {
                RandomTileSeed = 3, RandomTileCount = 2500,
                RandomTileSizeExponent = 1.6, RandomTileRelief = relief,
            },
        };
        calc.Calculate();
        return calc;
    }

    [Fact]
    public void Exposes_Dome_HeightField()
    {
        var calc = Render(new HsvPalette());
        Assert.IsAssignableFrom<IHeightFieldSource>(calc);
        float max = calc.SmoothBuffer.Max();
        int distinct = calc.SmoothBuffer.Where(h => h > 0f)
                                        .Select(h => (int)(h * 64))
                                        .Distinct().Count();
        Assert.True(max > 0.5f, "dome height never rises");
        Assert.True(distinct > 1, "dome height flat (no cap gradient)");
    }

    [Fact]
    public void HiRes_Twin_Produces_NonDegenerate_Field()
    {
        var twin = FractalRenderHost.CreateReliefFieldCalc(FractalType.RandomTile, 96, 96);
        var rt = Assert.IsType<RandomTileCalculator>(twin);
        rt.CenterX = 0; rt.CenterY = 0; rt.Zoom = 1.0;
        rt.FractalParameters = new FractalParameters
        {
            RandomTileSeed = 3, RandomTileCount = 2000, RandomTileSizeExponent = 1.6,
        };
        rt.Calculate();
        var src = (IHeightFieldSource)rt;
        Assert.True(src.SmoothBuffer.Max() > 0f, "hi-res twin field all zero");
        Assert.Contains(src.SmoothBuffer, h => h > 0f && h < 1f); // cap gradient present
    }

    private static int CenterWindowDistinct(uint[] buf, int w, int h)
    {
        var window = new HashSet<uint>();
        for (int y = h / 2 - 20; y < h / 2 + 20; y++)
            for (int x = w / 2 - 20; x < w / 2 + 20; x++)
                window.Add(buf[y * w + x]);
        return window.Count;
    }

    [Fact]
    public void Relief3D_Theme_Shades_Domes()
    {
        // Normal-mapped 3D theme: intra-shape colour variance proves per-pixel
        // dome shading. Flat HSV theme (ignores normals) stays near-uniform.
        var lit = Render(new MarbleReliefMap(), relief: 1.0);
        var flat = Render(new HsvPalette(), relief: 1.0);

        int litDistinct = lit.ColorBuffer.Where(c => (c & 0xFFFFFF) != 0).Distinct().Count();
        int litCenter = CenterWindowDistinct(lit.ColorBuffer, 160, 160);
        int flatCenter = CenterWindowDistinct(flat.ColorBuffer, 160, 160);

        Assert.True(litDistinct > 20 && litCenter > flatCenter + 5,
            $"relief distinct(all)={litDistinct} center={litCenter} vs flatCenter={flatCenter}");
    }

    [Fact]
    public void ZeroRelief_Flattens_Each_Shape()
    {
        // relief = 0 → flat fast path: each shape is a single colour, so the
        // normal-mapped theme can't vary within a shape (far fewer distinct
        // colours in a centre window than the lit path).
        var flatRelief = Render(new MarbleReliefMap(), relief: 0.0);
        var litRelief = Render(new MarbleReliefMap(), relief: 1.0);
        Assert.True(CenterWindowDistinct(litRelief.ColorBuffer, 160, 160)
                    > CenterWindowDistinct(flatRelief.ColorBuffer, 160, 160));
    }

    [Theory]
    [InlineData(RandomTileShape.Square)]
    [InlineData(RandomTileShape.Triangle)]
    public void Polygon_SDF_Cap_Peaks_Interior_And_Tapers_To_Edges(RandomTileShape shape)
    {
        // #336 — shape-correct SDF cap: height domes to ~1 at the incentre and
        // tapers to 0 along the whole boundary. The sqrt profile rises steeply,
        // so the sub-0.3 rim is a thin band — assert it exists (a real taper)
        // plus the interior peak, which together characterise a cap (vs a flat
        // fill, which would have no interior peak and no graded rim).
        var calc = new RandomTileCalculator(160, 160)
        {
            CenterX = 0, CenterY = 0, Zoom = 1.0,
            FractalParameters = new FractalParameters
            {
                RandomTileSeed = 3, RandomTileCount = 1500,
                RandomTileSizeExponent = 1.4, RandomTileShape = shape,
            },
        };
        calc.Calculate();

        float max = 0f; int painted = 0, low = 0;
        foreach (float h in calc.SmoothBuffer)
        {
            if (h <= 0f) continue;
            painted++;
            if (h > max) max = h;
            if (h < 0.3f) low++;
        }
        Assert.True(max > 0.9f, $"cap never peaks interior (max={max})");
        Assert.True(low > painted / 40, $"no graded edge taper ({low}/{painted}) — not a shape cap");
    }

    [Fact]
    public void HeightField_Drives_Hillshade_Relief_Pipeline()
    {
        // The full render-host relief modulation (HeightfieldRelief2D.Apply) must
        // consume the dome height field: dome slopes both darken (away-light /
        // shadow) and brighten (toward-light / specular) the flat themed colour.
        var calc = Render(new MonoBandMap());
        int w = calc.Width, h = calc.Height;
        var flat = (uint[])calc.ColorBuffer.Clone();
        var lit = new uint[w * h];
        var p = new FractalParameters
        {
            Relief2DEnabled = true,
            Relief2DHeightScale = 1.8,
            Relief2DLightAzimuthDeg = 135,
            Relief2DLightElevationDeg = 22,
            Relief2DShadowStrength = 0.85,
            Relief2DStrength = 1.0,
        };
        HeightfieldRelief2D.Apply(flat, lit, calc.SmoothBuffer, w, h, p);

        int domePx = 0, changed = 0, darkened = 0, brightened = 0;
        for (int i = 0; i < w * h; i++)
        {
            if (calc.SmoothBuffer[i] > 0) domePx++;
            if (lit[i] != flat[i]) changed++;
            int lf = (int)(flat[i] & 0xFF) + (int)((flat[i] >> 8) & 0xFF) + (int)((flat[i] >> 16) & 0xFF);
            int ll = (int)(lit[i] & 0xFF) + (int)((lit[i] >> 8) & 0xFF) + (int)((lit[i] >> 16) & 0xFF);
            if (ll < lf - 20) darkened++;
            if (ll > lf + 20) brightened++;
        }

        Assert.True(domePx > w * h / 10, $"too little dome coverage: {domePx}");
        Assert.True(changed > domePx / 10, $"relief shaded too few pixels: {changed}/{domePx}");
        Assert.True(darkened > domePx / 40, $"no away-slope/shadow shading: {darkened}/{domePx}");
        Assert.True(brightened > domePx / 40, $"relief one-way (no lit slopes): {brightened}/{domePx}");
    }
}
