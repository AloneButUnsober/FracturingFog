// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1633 — the coprime moiré anchor. The invariant: with the lattice fixed to the
// fractal, panning the view by k pixels shifts the mask by exactly k pixels. Views
// use a dyadic pixel scale (3.5/256/zoom with zoom = 7/8·2^m) so the plane
// arithmetic is exact. Plus the Zoom anchor's octave layers and continuity, the
// Screen default staying byte-identical, preset / fidelity carriage, and the export
// path passing the view.

using System;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading;

using FracturingFog.Cli;
using FracturingFog.Imaging;
using FracturingFog.Models;
using FracturingFog.Rendering;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class CoprimeMoireAnchorTests
{
    private const int W = 256, H = 192;

    private static LightingFxData Fx(MoireAnchor anchor, double cell = 1.3)
    {
        var fx = LightingFxData.CreateDefault();
        fx.MoireStrength = 1.0; fx.MoireCellSize = cell; fx.MoireAnchor = anchor;
        return fx;
    }

    private static uint[] Mask(in LightingFxData fx, double cx, double cy, double zoom)
    {
        var buf = Enumerable.Repeat(0xFFFFFFFFu, W * H).ToArray();
        CoprimeMoire.Apply(buf, W, H, in fx, cx, cy, zoom);
        return buf;
    }

    private static double Match(uint[] a, uint[] b, int shift)
    {
        int same = 0, n = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W - shift; x++) { n++; if (b[y * W + x] == a[y * W + x + shift]) same++; }
        return same / (double)n;
    }

    [Fact]
    public void Screen_IsUnchanged_WithOrWithoutTheView_AndAnchorsNeedAView()
    {
        var fx = Fx(MoireAnchor.Screen);
        fx.MoireOffsetX = 0.37; fx.MoireRotation = 17; fx.MoireSamples = 2;
        var noView = Enumerable.Repeat(0xFF808080u, W * H).ToArray();
        CoprimeMoire.Apply(noView, W, H, in fx);
        Assert.Equal(noView, MaskGrey(fx, -0.6, 0.1, 3.0));
        // An anchored mode without a view falls back to Screen.
        var pan = fx; pan.MoireAnchor = MoireAnchor.Pan;
        var panNoView = Enumerable.Repeat(0xFF808080u, W * H).ToArray();
        CoprimeMoire.Apply(panNoView, W, H, in pan);
        Assert.Equal(noView, panNoView);
    }

    private static uint[] MaskGrey(in LightingFxData fx, double cx, double cy, double zoom)
    {
        var buf = Enumerable.Repeat(0xFF808080u, W * H).ToArray();
        CoprimeMoire.Apply(buf, W, H, in fx, cx, cy, zoom);
        return buf;
    }

    [Fact]
    public void Pan_ShiftsWithTheFractal_Exactly_AndMatchesTheOracle()
    {
        double zoom = 7.0 / 8.0 * 4, s = 3.5 / W / zoom;            // s = 2^-8
        var fx = Fx(MoireAnchor.Pan);
        var a = Mask(fx, 0.25, 0.125, zoom);
        var b = Mask(fx, 0.25 + 7 * s, 0.125, zoom);               // fractal moves 7 px left
        Assert.Equal(1.0, Match(a, b, 7));
        var screen = Fx(MoireAnchor.Screen);
        Assert.True(Match(Mask(screen, 0.25, 0.125, zoom), Mask(screen, 0.25 + 7 * s, 0.125, zoom), 7) < 0.9);

        // Oracle: lattice coordinate = plane / (cell · s), floor, gcd = 1.
        for (int y = 0; y < H; y += 5)
            for (int x = 0; x < W; x += 3)
            {
                double px = 0.25 + (x + 0.5 - W / 2.0) * s, py = 0.125 + (y + 0.5 - H / 2.0) * s;
                long i = (long)Math.Floor(px / s / 1.3), j = (long)Math.Floor(py / s / 1.3);
                bool lit = BigInteger.GreatestCommonDivisor(Math.Abs(i), Math.Abs(j)) == 1;
                Assert.Equal(lit ? 0xFFFFFFFFu : 0xFF000000u, a[y * W + x]);
            }
    }

    [Fact]
    public void Zoom_FollowsPans_AndCrossFadesTheOctaves()
    {
        double zoom = 7.0 / 8.0 * 4, s = 3.5 / W / zoom;
        var fx = Fx(MoireAnchor.Zoom);
        Assert.True(Match(Mask(fx, 0.25, 0.125, zoom), Mask(fx, 0.25 + 7 * s, 0.125, zoom), 7) > 0.995);

        // Layers: one at an exact power of two, two equal halves at √2 between.
        var one = CoprimeMoire.Layers(MoireAnchor.Zoom, W, H, 0, 0, 8.0);
        Assert.Single(one);
        Assert.Equal(1.0, one[0].Mul);
        var two = CoprimeMoire.Layers(MoireAnchor.Zoom, W, H, 0, 0, 8.0 * Math.Sqrt(2));
        Assert.Equal(2, two.Length);
        Assert.Equal(0.5, two[0].Weight, 9); Assert.Equal(0.5, two[1].Weight, 9);
        Assert.Equal(1 / Math.Sqrt(2), two[0].Mul, 9); Assert.Equal(Math.Sqrt(2), two[1].Mul, 9);

        // Continuous across the doubling: just below zoom 8 ≈ exactly 8.
        var below = MaskGrey(fx, 0.3, 0.1, 8.0 * (1 - 1e-9));
        var at = MaskGrey(fx, 0.3, 0.1, 8.0);
        double diff = below.Zip(at, (p, q) => Math.Abs((int)(p & 0xFF) - (int)(q & 0xFF))).Average();
        Assert.True(diff < 0.5, $"mean channel jump {diff:F3} across the doubling");
    }

    [Fact]
    public void Anchor_TravelsInThePreset_AndTheFidelityCheck()
    {
        var fx = Fx(MoireAnchor.Zoom);
        var back = JsonSerializer.Deserialize<LightingFxPresetData>(JsonSerializer.Serialize(LightingFxPresetData.FromFx(fx)))!.ToFx();
        Assert.Equal(MoireAnchor.Zoom, back.MoireAnchor);
        Assert.Equal(MoireAnchor.Screen, JsonSerializer.Deserialize<LightingFxPresetData>("{}")!.ToFx().MoireAnchor);
        var only = LightingFxData.CreateDefault(); only.MoireAnchor = MoireAnchor.Pan;
        Assert.Contains("MoireAnchor", LightingFidelity.UnexpressedFields(in only));
    }

    // The export display buffer gets the view: under Pan the whole image (fractal and
    // mask) shifts together; under Screen the mask stays put while the fractal moves.
    [Fact]
    public void PosterPath_PassesTheView()
    {
        double zoom = 7.0 / 8.0 * 2, s = 3.5 / W / zoom;
        uint[] Render(MoireAnchor anchor, double cx)
        {
            var fp = new FractalParameters(); var fx = fp.Lighting;
            fx.MoireStrength = 1.0; fx.MoireCellSize = 1.3; fx.MoireAnchor = anchor; fp.Lighting = fx;
            return PosterRenderer.RenderDisplayPixels(new PosterRequest
            {
                FractalType = FractalType.Mandelbrot, Width = W, Height = H, CenterX = cx, CenterY = 0.0, Zoom = zoom,
                MaxIterations = 128, Quality = QualityPreset.Draft, ColorMap = new HsvPalette(),
                FractalParameters = fp, Path = string.Empty, Format = ImageFileFormat.Png,
            }, CancellationToken.None, out _, out _);
        }
        double pan = Match(Render(MoireAnchor.Pan, -0.5), Render(MoireAnchor.Pan, -0.5 + 7 * s), 7);
        double screen = Match(Render(MoireAnchor.Screen, -0.5), Render(MoireAnchor.Screen, -0.5 + 7 * s), 7);
        Assert.True(pan > 0.98, $"Pan: shifted image matches {pan:P1}");
        Assert.True(screen < pan - 0.1, $"Screen {screen:P1} vs Pan {pan:P1}");
    }
}
