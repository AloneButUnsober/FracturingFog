// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using FracturingFog.Calculators;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1010 (S3 of #1014) — autostereogram engine. The main checks DECODE the output
// with an independent method (brute-force period search: which horizontal shift
// makes a window of the row repeat) rather than re-running the encoder's own
// linking, then invert the separation formula back to depth.
public class AutostereogramTests
{
    // 16 distinct dot colours so an accidental repeat over a decode window is
    // vanishingly unlikely (16^-Window).
    private static readonly uint[] Palette16 =
        Enumerable.Range(0, 16).Select(i => 0xFF000000u | (uint)(i * 0x0F0F0F)).ToArray();

    private const int Window = 24;

    // Smallest period p in [pMin, pMax] for which row[x0+i] == row[x0+i+p] over
    // the decode window, or -1.
    private static int DecodePeriod(uint[] img, int w, int y, int x0, int pMin, int pMax)
    {
        for (int p = pMin; p <= pMax; p++)
        {
            bool ok = true;
            for (int i = 0; i < Window && ok; i++)
                ok = img[y * w + x0 + i] == img[y * w + x0 + i + p];
            if (ok) return p;
        }
        return -1;
    }

    // Best-matching period over the window (varying depth changes the period
    // pixel to pixel, so an exact full-window repeat is too strict): the p with
    // the most matches, provided at least minFraction of the window matches
    // (chance level with 16 colours is 1/16); else -1.
    private static int DecodePeriodBest(uint[] img, int w, int y, int x0, int pMin, int pMax, double minFraction)
    {
        int best = -1, bestHits = (int)Math.Ceiling(Window * minFraction) - 1;
        for (int p = pMin; p <= pMax; p++)
        {
            int hits = 0;
            for (int i = 0; i < Window; i++)
                if (img[y * w + x0 + i] == img[y * w + x0 + i + p]) hits++;
            if (hits > bestHits) { bestHits = hits; best = p; }
        }
        return best;
    }

    // Invert s = (1 - μz)E / (2 - μz)  ⇒  z = (E - 2s) / (μ(E - s)).
    private static double DepthFromSeparation(int s, double mu, int e) => (e - 2.0 * s) / (mu * (e - s));

    private static AutostereoOptions Opts(int e = 240, int seed = 7) =>
        new() { EyeSeparationPx = e, DotColors = Palette16, Seed = seed };

    [Fact]
    public void Separation_MatchesThePaperAtBothPlanes()
    {
        Assert.Equal(120, Autostereogram.Separation(0.0, 1.0 / 3.0, 240));   // far plane: E/2
        Assert.Equal(96, Autostereogram.Separation(1.0, 1.0 / 3.0, 240));    // near: (1-μ)E/(2-μ)
    }

    [Fact]
    public void FlatBackground_IsAPureRepeatAtTheFarSeparation()
    {
        const int w = 600, h = 6;
        var o = Opts();
        var img = Autostereogram.Render(new float[w * h], w, h, o);
        int s0 = Autostereogram.FarSeparation(o);
        for (int y = 0; y < h; y++)
            for (int x = 0; x + s0 < w; x++)
                Assert.Equal(img[y * w + x], img[y * w + x + s0]);
    }

    // Independent decode recovers the encoded depth of each band.
    [Fact]
    public void Decode_RecoversTheEncodedDepthPerBand()
    {
        const int w = 1500, h = 8, e = 240;
        const double mu = 1.0 / 3.0;
        // Four 300-px bands of ray distance plus sky, near = 1, far = 3.
        double[] bandDist = { double.PositiveInfinity, 3.0, 2.2, 1.6, 1.0 };
        var ray = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                ray[y * w + x] = (float)bandDist[x / 300];
        var dopt = new AutostereoDepthOptions { Near = 1.0, Far = 3.0, BackgroundGap = 0.15, BlurRadius = 0 };
        var z = Autostereogram.PrepareDepth(ray, w, h, dopt);
        var img = Autostereogram.Render(z, w, h, Opts(e));

        for (int b = 0; b < bandDist.Length; b++)
        {
            double expected = double.IsInfinity(bandDist[b]) ? 0.0 : 0.15 + 0.85 * (3.0 - bandDist[b]) / 2.0;
            int x0 = b * 300 + 150 - 55 - Window / 2;   // window midpoints sit mid-band
            for (int y = 0; y < h; y++)
            {
                int p = DecodePeriod(img, w, y, x0, 80, 130);
                Assert.True(p > 0, $"band {b} row {y}: no repeat found");
                Assert.InRange(DepthFromSeparation(p, mu, e), expected - 0.05, expected + 0.05);
            }
        }
    }

    [Fact]
    public void CrossEyed_ReversesTheDepthOrder()
    {
        const int w = 900, h = 4, e = 240;
        var ray = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                ray[y * w + x] = x < 450 ? 3.0f : 1.0f;   // left far, right near
        int Period(bool cross, int x0)
        {
            var z = Autostereogram.PrepareDepth(ray, w, h, new AutostereoDepthOptions { Near = 1, Far = 3, BlurRadius = 0, CrossEyed = cross });
            int p = DecodePeriod(Autostereogram.Render(z, w, h, Opts(e)), w, 1, x0, 60, 160);
            Assert.True(p > 0, $"no repeat decoded (cross={cross}, x0={x0})");
            return p;
        }
        // Nearer = smaller separation.
        Assert.True(Period(false, 700) < Period(false, 150), "wall-eyed: right band should read nearer");
        Assert.True(Period(true, 700) > Period(true, 150), "cross-eyed: depth order should flip");
    }

    [Fact]
    public void SameSeed_IsDeterministic_DifferentSeed_Differs()
    {
        const int w = 300, h = 20;
        var z = Enumerable.Range(0, w * h).Select(i => (float)((i % w) / (double)w)).ToArray();
        var a = Autostereogram.Render(z, w, h, Opts(seed: 3));
        var b = Autostereogram.Render(z, w, h, Opts(seed: 3));
        var c = Autostereogram.Render(z, w, h, Opts(seed: 4));
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void DefaultDots_AreBlackAndWhite_AndOpaque()
    {
        const int w = 200, h = 10;
        var img = Autostereogram.Render(new float[w * h], w, h, new AutostereoOptions { EyeSeparationPx = 80 });
        Assert.All(img, p => Assert.True(p == 0xFF000000u || p == 0xFFFFFFFFu));
        Assert.Contains(0xFF000000u, img);
        Assert.Contains(0xFFFFFFFFu, img);
    }

    [Fact]
    public void Texture_FillsFromTheTile_AndStillRepeatsAtTheFarSeparation()
    {
        const int w = 500, h = 6;
        var tile = new AutostereoTexture(Palette16, 8, 2);
        var o = new AutostereoOptions { EyeSeparationPx = 200, Texture = tile };
        var img = Autostereogram.Render(new float[w * h], w, h, o);
        Assert.All(img, p => Assert.Contains(p, Palette16));
        int s0 = Autostereogram.FarSeparation(o);
        for (int x = 0; x + s0 < w; x++) Assert.Equal(img[2 * w + x], img[2 * w + x + s0]);
    }

    [Fact]
    public void GuideDots_SitAtTheFarSeparation()
    {
        const int w = 400, h = 200;
        var o = Opts(e: 160) with { GuideDots = true };
        var img = Autostereogram.Render(new float[w * h], w, h, o);
        int s0 = Autostereogram.FarSeparation(o);
        int cy = Math.Max(4 + 2, h / 20);
        int cx0 = w / 2 - s0 / 2;
        Assert.Equal(0xFF000000u, img[cy * w + cx0]);
        Assert.Equal(0xFF000000u, img[cy * w + cx0 + s0]);
    }

    [Fact]
    public void PrepareDepth_MapsSkyToFar_NearestToOne_AndQuantises()
    {
        const int w = 4, h = 1;
        var ray = new[] { float.PositiveInfinity, 3f, 2f, 1f };
        var z = Autostereogram.PrepareDepth(ray, w, h, new AutostereoDepthOptions { Near = 1, Far = 3, BlurRadius = 0 });
        Assert.Equal(0f, z[0]);
        Assert.Equal(0.15f, z[1], 4);                 // farthest object stands off the background
        Assert.Equal(0.575f, z[2], 4);
        Assert.Equal(1f, z[3], 4);

        var q = Autostereogram.PrepareDepth(ray, w, h, new AutostereoDepthOptions { Near = 1, Far = 3, BlurRadius = 0, Levels = 3 });
        Assert.All(q, v => Assert.Contains(v, new[] { 0f, 0.5f, 1f }));

        var auto = Autostereogram.PrepareDepth(new[] { float.PositiveInfinity, float.PositiveInfinity }, 2, 1, new AutostereoDepthOptions());
        Assert.All(auto, v => Assert.Equal(0f, v));   // all sky: flat background, no throw
    }

    [Fact]
    public void CutStrip_TakesTheCentredColumns()
    {
        var img = Enumerable.Range(0, 10 * 2).Select(i => (uint)i).ToArray();   // 10 × 2
        var strip = Autostereogram.CutStrip(img, 10, 2, 4);
        Assert.Equal((4, 2), (strip.Width, strip.Height));
        Assert.Equal(new uint[] { 3, 4, 5, 6, 13, 14, 15, 16 }, strip.Pixels);
    }

    // End to end on a real depth map: the Mandelbulb's centre decodes nearer
    // (smaller period) than the sky at the edge. A fractal surface is rough, so
    // the depth is smoothed and terraced (as a user would) and the decode takes
    // the best period at ≥ 1/3 agreement — a window may straddle two terraces
    // and split its matches between their periods.
    [Fact]
    public void Mandelbulb_Depth_DecodesNearerAtTheCentreThanTheSky()
    {
        const int w = 480, h = 120, e = 120;
        var fx = LightingFxData.CreateDefault();
        fx.StereoMode = StereoMode.Fake; fx.StereoEyeSeparation = 0.1;   // asks the calc to publish depth
        var calc = new MandelbulbCalculator(w, h) { ColorMap = ColorPalette.BuiltIns[0], Zoom = 1.0 };
        calc.FractalParameters = new FractalParameters { BulbCameraDistance = 2.6, Lighting = fx };
        calc.Calculate(default);

        var img = Autostereogram.FromRayDistance(calc.DepthBuffer!, w, h,
            new AutostereoDepthOptions { BlurRadius = 6, Levels = 5 }, Opts(e));
        int y = h / 2;
        int sky = DecodePeriod(img, w, 2, 10, 30, 70);             // top-left corner: background
        int centre = DecodePeriodBest(img, w, y, w / 2 - 25 - Window / 2, 30, 70, 1.0 / 3.0);
        Assert.Equal(Autostereogram.FarSeparation(Opts(e)), sky);
        Assert.True(centre > 0 && centre < sky, $"centre period {centre} should be below the sky's {sky}");
    }
}
