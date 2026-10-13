// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;

using FracturingFog.Abstractions.Explore;
using FracturingFog.Explore;
using FracturingFog.FFMath;
using FracturingFog.Imaging;
using FracturingFog.Models;

using Xunit;

namespace FracturingFog.Server.Tests;

// Finder S3b (#1235): the Misiurewicz self-similar loop. The invariant is the
// one the video relies on: the last frame of a loop (t = 1) is the first frame
// again — zoomed ×|λ|^n, turned by −n·arg λ, palette turned back n·p
// iterations. A wrong turn direction, zoom factor or colour shift breaks it.
public sealed class MisiurewiczLoopTests
{
    private readonly ITestOutputHelper _out;
    public MisiurewiczLoopTests(ITestOutputHelper o) => _out = o;

    private const int W = 192, H = 108;

    private static PosterRequest Frame(DeepComplex c, double zoom, int iter) => new()
    {
        FractalType = FractalType.Mandelbrot,
        Width = W, Height = H,
        CenterX = c.Re.X0, CenterXLo = c.Re.X1, CenterY = c.Im.X0, CenterYLo = c.Im.X1,
        Zoom = zoom, MaxIterations = iter,
        ColorMap = new HsvPalette(),   // cyclic in the escape count
        Quality = QualityPreset.Standard,
        FractalParameters = new FractalParameters(),
        Path = "unused.png", Format = ImageFileFormat.Png,
    };

    // Mean absolute RGB difference, 0..1, of 4×4 block means over the central
    // half of the frame. Block means compare structure, not pixel-level
    // aliasing (a turned frame of dense filaments resamples differently even
    // when it is geometrically right); the corners hold the most-rotated content.
    private const int B = 4;
    private static double Diff(uint[] a, uint[] b)
    {
        double sum = 0; int n = 0;
        for (int by = H / 4; by + B <= 3 * H / 4; by += B)
            for (int bx = W / 4; bx + B <= 3 * W / 4; bx += B)
                for (int s = 0; s < 24; s += 8)
                {
                    double ma = 0, mb = 0;
                    for (int y = by; y < by + B; y++)
                        for (int x = bx; x < bx + B; x++)
                        {
                            ma += (a[y * W + x] >> s) & 0xFF;
                            mb += (b[y * W + x] >> s) & 0xFF;
                        }
                    sum += Math.Abs(ma - mb) / (B * B);
                    n++;
                }
        return sum / (255.0 * n);
    }

    private (double Seam, double Mid, double Wrong) Check(MisiurewiczResult m, double zoom, string name)
    {
        Assert.True(m.Found, name);
        double mag = m.Multiplier.Magnitude, deg = m.Multiplier.Phase * 180 / Math.PI;
        var loop = MisiurewiczLoop.For(mag, deg, m.Period);
        var req = Frame(m.Point, zoom, 3000);
        var f0 = MisiurewiczLoopRenderer.RenderFrame(req, loop, 0, default);
        var f1 = MisiurewiczLoopRenderer.RenderFrame(req, loop, 1, default);
        var fm = MisiurewiczLoopRenderer.RenderFrame(req, loop, 0.5, default);
        // Controls: the same loop turning the wrong way, and without the palette shift.
        var wrong = MisiurewiczLoopRenderer.RenderFrame(req, loop with { AngleDegrees = -loop.AngleDegrees }, 1, default);
        var noPal = MisiurewiczLoopRenderer.RenderFrame(req, loop with { Period = 0 }, 1, default);
        var r = (Diff(f0, f1), Diff(f0, fm), Diff(f0, wrong));
        _out.WriteLine($"{name}: M({m.Preperiod},{m.Period}) |λ| {mag:G5} ∠{deg:F1}° cycles {loop.Cycles}: seam {r.Item1:F4}, mid {r.Item2:F4}, wrong-turn {r.Item3:F4}, no-palette-shift {Diff(f0, noPal):F4}");
        return r;
    }

    [Theory]
    [InlineData("i", 0.001, 0.999, 2e3)]
    [InlineData("seahorse spiral", -0.77568377, 0.13646737, 2e5)]
    [InlineData("seahorse spiral, deep", -0.77568377, 0.13646737, 2e12)]
    public void LoopEnd_ReproducesLoopStart(string name, double x, double y, double zoom)
    {
        var m = MisiurewiczFinder.FindNear(new DeepComplex(x, y), 0.01 * (x == 0.001 ? 1 : 1e-4));
        var (seam, mid, wrong) = Check(m, zoom, name);
        // A slow spiral (|λ| ≈ 1.04, 19 cycles) compounds the small nonlinear
        // drift of the escape count, so the seam is a little hue drift in the
        // core; the geometry still closes (an order of magnitude below the controls).
        Assert.True(seam < 0.05, $"{name}: loop seam {seam}");
        Assert.True(mid > 8 * seam, $"{name}: mid-loop frame {mid} should differ (not a trivial loop)");
        Assert.True(wrong > 8 * seam, $"{name}: a wrong-way turn {wrong} should break the seam");
    }

    [Fact]
    public void Tip_MinusTwo_ZoomOnlyLoop()
    {
        // c = −2: M(2,1), λ = 4, no turn.
        var m = MisiurewiczFinder.FindNear(new DeepComplex(-1.9999, 0.00001), 0.01);
        Assert.Equal(-2, m.Point.Re.X0, 12);
        Assert.Equal(4, m.Multiplier.Magnitude, 9);
        // The tip's surroundings are a near-uniform exterior, so only the seam is checked.
        var (seam, _, _) = Check(m, 50, "-2");
        Assert.True(seam < 0.05, $"seam {seam}");
    }

    [Theory]
    [InlineData(5.657, 1)]
    [InlineData(1.05, 15)]      // slow spiral: cycles until ≥ 2× zoom
    [InlineData(2.0, 1)]
    public void AutoCycles_ZoomAtLeastTwice(double mag, int expected)
    {
        Assert.Equal(expected, MisiurewiczLoop.AutoCycles(mag));
        Assert.True(Math.Pow(mag, expected) >= MisiurewiczLoop.MinLoopZoom - 1e-9);
    }

    [Fact]
    public void Multiplier_ParsesAndFormats()
    {
        Assert.True(MisiurewiczLoop.TryParseMultiplier(MisiurewiczLoop.FormatMultiplier(5.656854249492381, 135), out var m, out var d));
        Assert.Equal(5.656854249492381, m);
        Assert.Equal(135, d);
        Assert.False(MisiurewiczLoop.TryParseMultiplier("0.9,10", out _, out _));   // |λ| ≤ 1 is not a repelling cycle
        Assert.False(MisiurewiczLoop.TryParseMultiplier("4", out _, out _));
    }
}
