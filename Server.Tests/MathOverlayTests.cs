// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1554 (Math Plane S7, part 2) — the math overlay on 2D views: geometry against
// the escape-time grid formula, the orbit seed per family, the drawn orbit at the
// independently computed pixels, the --math-overlay flag, the Command builder,
// and the poster path.

using System;
using System.Linq;
using System.Numerics;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Imaging;
using FracturingFog.Rendering;
using FracturingFog.ViewState;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MathOverlayTests
{
    private static readonly Complex Rabbit = new(-0.12256116687665361, 0.74486176661974423);

    // ── Spec ──

    [Theory]
    [InlineData("-0.1226,0.7449", -0.1226, 0.7449, MathOverlay.DefaultSteps)]
    [InlineData(" 0.3 , -0.2 , 80 ", 0.3, -0.2, 80)]
    public void Spec_Parses(string text, double re, double im, int steps)
    {
        Assert.True(MathOverlaySpec.TryParse(text, out var s, out var e), e);
        Assert.Equal(new MathOverlaySpec(re, im, steps), s);
        Assert.True(MathOverlaySpec.TryParse(s.ToFlagValue(), out var back, out _));
        Assert.Equal(s, back);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("a,b")]
    [InlineData("1,2,0")]
    [InlineData("1,2,99999")]
    [InlineData("1,2,3,4")]
    public void Spec_RejectsBadValues(string text) => Assert.False(MathOverlaySpec.TryParse(text, out _, out _));

    // ── Geometry ──

    [Fact]
    public void ClientToPlane_IsTheEscapeTimeSample_AndToPixelItsCentre()
    {
        const int w = 640, h = 360; const double cx = -0.5, cy = 0.1, zoom = 1.7;
        double s = 3.5 / Math.Max(w, h) / zoom;   // EscapeTimeCalculator's pitch
        for (int x = 0; x < w; x += 41)
            for (int y = 0; y < h; y += 29)
            {
                var (re, im) = MathOverlay.ClientToPlane(x + 0.3, y + 0.8, w, h, cx, cy, zoom);
                Assert.Equal(cx + (x - w * 0.5) * s, re, 12);
                Assert.Equal(cy + (y - h * 0.5) * s, im, 12);
                var (px, py) = MathOverlay.ToPixel(re, im, w, h, cx, cy, zoom);
                Assert.Equal(x + 0.5, px, 9);
                Assert.Equal(y + 0.5, py, 9);
            }
    }

    [Fact]
    public void Seed_FollowsTheFamily()
    {
        var p = new Complex(0.3, -0.2); var jc = new Complex(-0.8, 0.156);
        Assert.Equal((Complex.Zero, p), MathOverlay.Seed(FractalType.Mandelbrot, p, jc));
        Assert.Equal((p, jc), MathOverlay.Seed(FractalType.Julia, p, jc));
        Assert.Null(MathOverlay.Seed(FractalType.BurningShip, p, jc));
        Assert.Null(MathOverlay.Orbit(FractalType.Newton, new MathOverlaySpec(0, 0), jc));

        // Julia: the orbit of z0 under z^2 + c, independently.
        var orbit = MathOverlay.Orbit(FractalType.Julia, new MathOverlaySpec(p.Real, p.Imaginary, 20), jc)!;
        var z = p;
        Assert.Equal(z, orbit[0]);
        for (int n = 1; n < orbit.Count; n++) { z = z * z + jc; Assert.Equal(z, orbit[n]); }
    }

    [Fact]
    public void Readout_SaysPeriodOrEscape()
    {
        var spec = new MathOverlaySpec(Rabbit.Real, Rabbit.Imaginary, 30);
        var orbit = MathOverlay.Orbit(FractalType.Mandelbrot, spec, default);
        Assert.Contains("settles to period 3", MathOverlay.Readout(FractalType.Mandelbrot, spec, orbit, true));
        var out1 = new MathOverlaySpec(0.5, 0, 30);
        Assert.Contains("leaves |z| <= 2 at step",
            MathOverlay.Readout(FractalType.Mandelbrot, out1, MathOverlay.Orbit(FractalType.Mandelbrot, out1, default), true));
        Assert.Contains("Mandelbrot and Julia", MathOverlay.Readout(FractalType.BurningShip, out1, null, false));
    }

    // ── Drawing ──

    [Fact]
    public void Orbit_IsDrawnAtTheIndependentlyComputedPixels()
    {
        const int w = 400, h = 300; const double cx = -0.5, cy = 0, zoom = 1;
        const uint bg = 0xFF000000;
        var buf = Enumerable.Repeat(bg, w * h).ToArray();
        MathOverlayRenderer.Draw(buf, w, h, cx, cy, zoom, FractalType.Mandelbrot, default,
                                 new MathOverlaySpec(Rabbit.Real, Rabbit.Imaginary, 3), pinned: true);
        double s = 3.5 / Math.Max(w, h) / zoom;
        // z1 = c (under the marker), z2 = c^2 + c: dots at their pixels.
        var z2 = Rabbit * Rabbit + Rabbit;
        int px = (int)((z2.Real - cx) / s + w * 0.5), py = (int)((z2.Imaginary - cy) / s + h * 0.5);
        Assert.NotEqual(bg, buf[py * w + px]);
        // Far from the orbit, the axes and the read-out: untouched.
        Assert.Equal(bg, buf[10 * w + 10]);
        // The real axis is drawn on the row of im = 0.
        int axisRow = (int)((0 - cy) / s + h * 0.5);
        Assert.NotEqual(bg, buf[axisRow * w + 5]);
    }

    [Fact]
    public void MathPlaneViews_AreLeftAlone()
    {
        var buf = new uint[64 * 48];
        MathOverlayRenderer.Draw(buf, 64, 48, 0, 0, 1, FractalType.MathPlane, default, new MathOverlaySpec(0, 0), true);
        Assert.All(buf, v => Assert.Equal(0u, v));
    }

    [Fact]
    public void Poster_DrawsThePinnedOverlay_AndNothingWithout()
    {
        const int w = 200, h = 150;
        var req = new PosterRequest
        {
            FractalType = FractalType.Mandelbrot, CenterX = -0.5, CenterY = 0, Zoom = 1, Width = w, Height = h,
            MathOverlay = new MathOverlaySpec(Rabbit.Real, Rabbit.Imaginary, 10),
        };
        var a = new uint[w * h];
        PosterRenderer.ApplyMathOverlay(req, a, w, h);
        var b = new uint[w * h];
        MathOverlayRenderer.Draw(b, w, h, -0.5, 0, 1, FractalType.Mandelbrot, Complex.Zero, req.MathOverlay!.Value, pinned: true);
        Assert.Equal(b, a);
        Assert.Contains(a, v => v != 0);

        var none = new uint[w * h];
        PosterRenderer.ApplyMathOverlay(req with { MathOverlay = null }, none, w, h);
        Assert.All(none, v => Assert.Equal(0u, v));
    }

    // ── Batch ──

    private static string[] Args(params string[] extra)
        => new[] { "app.exe", "--batch", "--x", "-0.5", "--y", "0", "--zoom", "1", "--out", "o.png" }.Concat(extra).ToArray();

    [Fact]
    public void Flag_Parses_AndRejectsBadValues()
    {
        Assert.True(BatchOptions.TryParse(Args("--math-overlay", "-0.1226,0.7449,30"), 2, out var o, out var e), e);
        Assert.Equal(new MathOverlaySpec(-0.1226, 0.7449, 30), o.MathOverlay);
        Assert.True(BatchOptions.TryParse(Args(), 2, out var none, out _));
        Assert.Null(none.MathOverlay);
        Assert.False(BatchOptions.TryParse(Args("--math-overlay", "1"), 2, out _, out var bad));
        Assert.Contains("--math-overlay", bad);
        Assert.Contains(BatchFlags.MathOverlay, BatchFlagCatalog.FormatUsage());
    }

    [Fact]
    public void CommandBuilder_EmitsThePin_AndFlagsHoverOnlyAsAGap()
    {
        var spec = new MathOverlaySpec(-0.1226, 0.7449, 70);
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.Mandelbrot, CenterX = -0.5, CenterY = 0, Zoom = 1, MathOverlay = spec,
        });
        int i = report.Args.ToList().IndexOf(BatchFlags.MathOverlay);
        Assert.True(i >= 0, report.Command);
        // Round trip: the emitted value parses back to the same spec.
        Assert.True(MathOverlaySpec.TryParse(report.Args[i + 1], out var back, out _));
        Assert.Equal(spec, back);
        Assert.DoesNotContain(report.Gaps, g => g.Contains("Math overlay"));

        var hover = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.Mandelbrot, CenterX = -0.5, CenterY = 0, Zoom = 1, MathOverlayHoverOnly = true,
        });
        Assert.DoesNotContain(BatchFlags.MathOverlay, hover.Args);
        Assert.Contains(hover.Gaps, g => g.Contains("Math overlay"));
    }
}
