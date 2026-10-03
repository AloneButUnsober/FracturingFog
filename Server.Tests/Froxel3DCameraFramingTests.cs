// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1079 — the 3D froxel volume must be framed from the camera the calculator
// actually traced (zoom / distance floor, pan, eye offset), not the raw camera
// parameters. Checked by physical equivalences, not against the code's own output:
//   - zoom: (distance 2d, Zoom 2) places the same eye as (distance d, Zoom 1), so
//     the shadowed-fog frames must be byte-identical;
//   - pan is a lens shift: panning by exactly one froxel column (Low = 16 columns,
//     64 px wide → 4 px) must reproduce the unpanned frame shifted by 4 px, fog
//     shafts included.

using System;
using System.Threading;

using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class Froxel3DCameraFramingTests
{
    private const int W = 64, H = 48;

    private static LightingFxData Fx()
    {
        var fx = LightingFxData.CreateDefault();
        fx.FogDensity = 0.05;
        fx.Froxel3D = true;
        fx.Froxel3DQuality = FroxelQuality.Low;
        fx.Froxel3DShadowSteps = 16;
        fx.SsaoSamples = 0;
        fx.EdgeStrength = 0;
        return fx;
    }

    private static uint[] Bulb(double dist, double zoom, double panU)
    {
        var calc = new MandelbulbCalculator(W, H) { ColorMap = ColorPalette.BuiltIns[0], Zoom = zoom, CenterX = panU };
        calc.FractalParameters = new FractalParameters { BulbCameraDistance = dist, Lighting = Fx() };
        calc.Calculate(CancellationToken.None);
        return (uint[])calc.ColorBuffer.Clone();
    }

    private static uint[] Box(double dist, double zoom)
    {
        var calc = new MandelboxCalculator(W, H) { ColorMap = ColorPalette.BuiltIns[0], Zoom = zoom };
        calc.FractalParameters = new FractalParameters { MandelboxCameraDistance = dist, Lighting = Fx() };
        calc.Calculate(CancellationToken.None);
        return (uint[])calc.ColorBuffer.Clone();
    }

    [Fact]
    public void Mandelbulb_ZoomIsTheSameEye_SameFrame()
    {
        var baseline = Bulb(2.6, 1.0, 0);
        Assert.Equal(baseline, Bulb(5.2, 2.0, 0));
        Assert.NotEqual(baseline, Bulb(3.4, 1.0, 0));   // the frame does depend on the eye
    }

    [Fact]
    public void Mandelbox_ZoomAboveTheFloorIsTheSameEye_SameFrame()
    {
        Assert.Equal(Box(12.0, 1.0), Box(24.0, 2.0));
    }

    private static int MaxChannelDiff(uint a, uint b)
        => Math.Max(Math.Abs((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF)),
           Math.Max(Math.Abs((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF)),
                    Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF))));

    [Fact]
    public void Mandelbulb_PanIsALensShift_FogFollows()
    {
        const int shift = W / 16;                                       // one Low froxel column
        double tan = Math.Tan(Math.PI / 6), aspect = (double)W / H;
        double panU = 2.0 * shift / W * tan * aspect;                   // u(x) + panU == u(x + shift)

        var a = Bulb(2.6, 1.0, 0);
        var b = Bulb(2.6, 1.0, panU);

        // Interior pixels (bilinear froxel taps clamp at the grid border).
        int n = 0, close = 0;
        for (int y = 0; y < H; y++)
            for (int x = shift; x < W - 3 * shift; x++)
            {
                n++;
                if (MaxChannelDiff(b[y * W + x], a[y * W + x + shift]) <= 2) close++;
            }
        Assert.True(close >= n * 0.98, $"{close}/{n} pixels match the shifted frame");
    }
}
