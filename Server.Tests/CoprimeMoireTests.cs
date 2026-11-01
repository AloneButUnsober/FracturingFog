// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1488 — coprime moiré post layer. The oracle is System.Numerics.BigInteger's
// gcd on each pixel's lattice point, worked out here from the documented mapping
// (pixel centre − frame centre, ⌊·/cell⌋), not from CoprimeMoire's own code.

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

public sealed class CoprimeMoireTests
{
    private static int G(long a, long b) => (int)BigInteger.GreatestCommonDivisor(a, b);

    private static uint[] Flat(int w, int h, uint argb) => Enumerable.Repeat(argb, w * h).ToArray();

    private static LightingFxData Fx()
    {
        var fx = LightingFxData.CreateDefault();
        fx.MoireStrength = 1.0;
        return fx;
    }

    private static long Lat(int x, int w, double cell) => (long)Math.Floor((x + 0.5 - w * 0.5) / cell);

    [Fact]
    public void DefaultIsOff_AndLeavesTheFrameUntouched()
    {
        var fx = LightingFxData.CreateDefault();
        Assert.Equal(0.0, fx.MoireStrength);
        Assert.False(CoprimeMoire.Active(fx));
        var buf = Enumerable.Range(0, 64 * 48).Select(i => 0xFF000000u | (uint)(i * 2654435761u >> 8)).ToArray();
        var before = (uint[])buf.Clone();
        CoprimeMoire.Apply(buf, 64, 48, in fx);
        Assert.Equal(before, buf);
    }

    [Theory]
    [InlineData(64, 48, 1.0)]
    [InlineData(63, 47, 1.0)]
    [InlineData(64, 48, 2.0)]
    [InlineData(80, 60, 1.5)]
    public void Coprime_LightsExactlyTheVisibleLatticePoints(int w, int h, double cell)
    {
        var fx = Fx(); fx.MoireCellSize = cell;
        var buf = Flat(w, h, 0xFFFFFFFFu);
        CoprimeMoire.Apply(buf, w, h, in fx);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool lit = G(Math.Abs(Lat(x, w, cell)), Math.Abs(Lat(y, h, cell))) == 1;
                Assert.Equal(lit ? 0xFFFFFFFFu : 0xFF000000u, buf[y * w + x]);
            }
    }

    [Theory]
    [InlineData(MoireCondition.GcdEquals, 2)]
    [InlineData(MoireCondition.GcdAtMost, 3)]
    [InlineData(MoireCondition.GcdIntensity, 1)]
    public void Conditions_FollowTheGcd(MoireCondition cond, int k)
    {
        const int w = 50, h = 40;
        var fx = Fx(); fx.MoireCondition = cond; fx.MoireConditionK = k;
        var buf = Flat(w, h, 0xFFFFFFFFu);
        CoprimeMoire.Apply(buf, w, h, in fx);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int g = G(Math.Abs(Lat(x, w, 1)), Math.Abs(Lat(y, h, 1)));
                double want = cond switch
                {
                    MoireCondition.GcdEquals => g == k ? 1 : 0,
                    MoireCondition.GcdAtMost => g >= 1 && g <= k ? 1 : 0,
                    _ => g >= 1 ? 1.0 / g : 0,
                };
                Assert.Equal((uint)Math.Round(want * 255, MidpointRounding.AwayFromZero), buf[y * w + x] & 0xFF);
            }
    }

    [Fact]
    public void Invert_SwapsLitAndUnlit()
    {
        const int w = 40, h = 30;
        var fx = Fx(); fx.MoireInvert = true;
        var buf = Flat(w, h, 0xFFFFFFFFu);
        CoprimeMoire.Apply(buf, w, h, in fx);
        for (int i = 0; i < buf.Length; i++)
        {
            bool lit = G(Math.Abs(Lat(i % w, w, 1)), Math.Abs(Lat(i / w, h, 1))) == 1;
            Assert.Equal(lit ? 0xFF000000u : 0xFFFFFFFFu, buf[i]);
        }
    }

    [Fact]
    public void Rotation90_MapsTheLatticeOntoTheOtherAxis()
    {
        const int w = 40, h = 40;
        var fx = Fx(); fx.MoireRotation = 90;
        var buf = Flat(w, h, 0xFFFFFFFFu);
        CoprimeMoire.Apply(buf, w, h, in fx);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                // (u, v) rotated by +90°: u′ = v, v′ = −u.
                long i = (long)Math.Floor(y + 0.5 - h * 0.5), j = (long)Math.Floor(-(x + 0.5 - w * 0.5));
                bool lit = G(Math.Abs(i), Math.Abs(j)) == 1;
                Assert.Equal(lit ? 0xFFFFFFFFu : 0xFF000000u, buf[y * w + x]);
            }
    }

    [Theory]
    [InlineData(MoireBlendMode.Multiply)]
    [InlineData(MoireBlendMode.Screen)]
    [InlineData(MoireBlendMode.Overlay)]
    public void Blends_MatchTheirFormulas_AtHalfStrength(MoireBlendMode mode)
    {
        const int w = 30, h = 20;
        foreach (uint grey in new uint[] { 64, 200 })
        {
            var fx = Fx(); fx.MoireBlend = mode; fx.MoireStrength = 0.5;
            var buf = Flat(w, h, 0xFF000000u | grey << 16 | grey << 8 | grey);
            CoprimeMoire.Apply(buf, w, h, in fx);
            double v = grey / 255.0;
            for (int i = 0; i < buf.Length; i++)
            {
                double c = G(Math.Abs(Lat(i % w, w, 1)), Math.Abs(Lat(i / w, h, 1))) == 1 ? 1 : 0;
                double b = mode switch
                {
                    MoireBlendMode.Multiply => v * c,
                    MoireBlendMode.Screen => 1 - (1 - v) * (1 - c),
                    _ => v < 0.5 ? 2 * v * c : 1 - 2 * (1 - v) * (1 - c),
                };
                uint want = (uint)Math.Round((v + (b - v) * 0.5) * 255, MidpointRounding.AwayFromZero);
                // ±1: a value exactly on a half may round either way.
                Assert.True(Math.Abs((int)want - (int)(buf[i] & 0xFF)) <= 1, $"{mode} grey {grey}: want {want}, got {buf[i] & 0xFF}");
            }
        }
    }

    [Fact]
    public void Supersampling_FadesTheMoire_AndKeepsAlpha()
    {
        const int w = 96, h = 72;
        double Std(int samples)
        {
            var fx = Fx(); fx.MoireCellSize = 0.7; fx.MoireSamples = samples;
            var buf = Flat(w, h, 0x80FFFFFFu);
            CoprimeMoire.Apply(buf, w, h, in fx);
            Assert.All(buf, p => Assert.Equal(0x80u, p >> 24));
            var v = buf.Select(p => (double)(p & 0xFF)).ToArray();
            double m = v.Average();
            return Math.Sqrt(v.Average(x => (x - m) * (x - m)));
        }
        double raw = Std(1), smooth = Std(4);
        Assert.True(smooth < raw * 0.7, $"std at 4 samples {smooth:F1} vs 1 sample {raw:F1}");
    }

    [Fact]
    public void Preset_RoundTripsEveryMoireField()
    {
        var fx = LightingFxData.CreateDefault();
        fx.MoireStrength = 0.6; fx.MoireCellSize = 1.7; fx.MoireRotation = 12.5;
        fx.MoireOffsetX = 3.25; fx.MoireOffsetY = -2; fx.MoireAspect = 1.4;
        fx.MoireCondition = MoireCondition.GcdAtMost; fx.MoireConditionK = 5;
        fx.MoireBlend = MoireBlendMode.Overlay; fx.MoireInvert = true; fx.MoireSamples = 3;
        string json = JsonSerializer.Serialize(LightingFxPresetData.FromFx(fx));
        var back = JsonSerializer.Deserialize<LightingFxPresetData>(json)!.ToFx();
        Assert.Equal(fx.MoireStrength, back.MoireStrength);
        Assert.Equal(fx.MoireCellSize, back.MoireCellSize);
        Assert.Equal(fx.MoireRotation, back.MoireRotation);
        Assert.Equal(fx.MoireOffsetX, back.MoireOffsetX);
        Assert.Equal(fx.MoireOffsetY, back.MoireOffsetY);
        Assert.Equal(fx.MoireAspect, back.MoireAspect);
        Assert.Equal(fx.MoireCondition, back.MoireCondition);
        Assert.Equal(fx.MoireConditionK, back.MoireConditionK);
        Assert.Equal(fx.MoireBlend, back.MoireBlend);
        Assert.Equal(fx.MoireInvert, back.MoireInvert);
        Assert.Equal(fx.MoireSamples, back.MoireSamples);
        // A preset saved before #1488 (no moiré keys) loads as off.
        var legacy = JsonSerializer.Deserialize<LightingFxPresetData>("{}")!.ToFx();
        Assert.False(CoprimeMoire.Active(legacy));
        Assert.Equal(1.0, legacy.MoireCellSize);
    }

    [Fact]
    public void CommandBuilder_ReportsALiveMoireWithoutAPreset()
    {
        var fx = LightingFxData.CreateDefault();
        Assert.DoesNotContain(LightingFidelity.UnexpressedFields(in fx), n => n.StartsWith("Moire"));
        fx.MoireStrength = 0.5;
        Assert.Contains("MoireStrength", LightingFidelity.UnexpressedFields(in fx));
    }

    // The export / batch-still display buffer (PosterRenderer.RenderDisplayBuffer —
    // what the saved file gets) applies the layer: every pixel
    // is either the unlayered pixel (lit) or black (unlit, multiply at full strength).
    [Fact]
    public void PosterPath_AppliesTheLayer()
    {
        const int w = 80, h = 60;
        uint[] Render(bool on)
        {
            var fp = new FractalParameters();
            if (on) { var fx = fp.Lighting; fx.MoireStrength = 1.0; fp.Lighting = fx; }
            return PosterRenderer.RenderDisplayPixels(new PosterRequest
            {
                FractalType = FractalType.Mandelbrot, Width = w, Height = h,
                CenterX = -0.5, CenterY = 0.0, Zoom = 1.0, MaxIterations = 128,
                Quality = QualityPreset.Draft, ColorMap = new HsvPalette(),
                FractalParameters = fp, Path = string.Empty, Format = ImageFileFormat.Png,
            }, CancellationToken.None, out _, out _);
        }
        var off = Render(false);
        var on = Render(true);
        Assert.NotEqual(off, on);
        for (int i = 0; i < on.Length; i++)
        {
            bool lit = G(Math.Abs(Lat(i % w, w, 1)), Math.Abs(Lat(i / w, h, 1))) == 1;
            Assert.Equal(lit ? off[i] & 0xFFFFFFu : 0u, on[i] & 0xFFFFFFu);
        }
    }
}
