// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #947 — batch video for every fractal type: per-family motion resolution
// (VideoMotionPlan), the new CLI grammar, and offline-calculator coverage so no
// type silently falls back to Mandelbrot.

using System;
using System.Linq;

using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Imaging;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class BatchVideoMotionTests
{
    private static BatchOptions Parse(params string[] tail)
    {
        var argv = new[] { "FracturingFog", "--batch" }.Concat(tail).ToArray();
        Assert.True(BatchOptions.TryParse(argv, startIndex: 2, out var opts, out var err), err);
        return opts;
    }

    [Fact]
    public void Auto_picks_the_family_motion_for_every_type()
    {
        foreach (FractalType t in Enum.GetValues<FractalType>())
        {
            var k = VideoMotionPlan.Resolve(t, BatchVideoMotion.Auto, out var note);
            Assert.Null(note);
            var expected = FractalMotionCapabilities.MotionClass(t) switch
            {
                FractalMotionClass.Zoomable2D => VideoMotionKind.Zoom,
                FractalMotionClass.Raymarch3D => VideoMotionKind.Dolly,
                _ => FractalMotionCapabilities.SupportsVideoParamSweep(t) ? VideoMotionKind.Sweep : VideoMotionKind.KenBurns,
            };
            Assert.Equal(expected, k);
        }
    }

    [Fact]
    public void Explicit_requests_adapt_to_the_family_with_a_note()
    {
        Assert.Equal(VideoMotionKind.KenBurns, VideoMotionPlan.Resolve(FractalType.Flame, BatchVideoMotion.Zoom, out var n1));
        Assert.NotNull(n1);
        Assert.Equal(VideoMotionKind.KenBurns, VideoMotionPlan.Resolve(FractalType.Julia, BatchVideoMotion.Sweep, out var n2));
        Assert.NotNull(n2);
        Assert.Equal(VideoMotionKind.Dolly, VideoMotionPlan.Resolve(FractalType.Mandelbulb, BatchVideoMotion.Zoom, out _));
        Assert.Equal(VideoMotionKind.Hold, VideoMotionPlan.Resolve(FractalType.Mandelbrot, BatchVideoMotion.Hold, out _));
        Assert.Equal(VideoMotionKind.Sweep, VideoMotionPlan.Resolve(FractalType.Logistic, BatchVideoMotion.Sweep, out var n3));
        Assert.Null(n3);
    }

    [Fact]
    public void Dolly_starts_six_times_wide_with_a_floor()
    {
        Assert.Equal((1.0 / 6.0, 1.0), VideoMotionPlan.CameraDolly(0.3));
        Assert.Equal((0.5, 3.0), VideoMotionPlan.CameraDolly(3.0));
    }

    [Fact]
    public void Easings_and_sweeps_hit_their_endpoints()
    {
        Assert.Equal(0.0, VideoMotionPlan.SmootherStep(0.0));
        Assert.Equal(1.0, VideoMotionPlan.SmootherStep(1.0));
        Assert.Equal(0.5, VideoMotionPlan.SmoothStep(0.5), 12);
        Assert.Equal(8.0, VideoMotionPlan.LogLerpZoom(2.0, 32.0, 0.5), 9);
        Assert.Equal(3.0 + 0.5 * 3.5 / 2.0, VideoMotionPlan.LogisticSweepX(3.0, 2.0, 1.0), 12);
        Assert.Equal(1.5, VideoMotionPlan.AcidWarpSweepFlow(0.0, 1.0), 12);
        Assert.Equal(Math.PI, VideoMotionPlan.OrbitTheta(0.0, 180.0, 1.0), 12);
    }

    [Fact]
    public void KenBurns_frame0_is_the_full_frame_and_stays_inside()
    {
        var kb = KenBurnsPath.Random(new Random(7));
        Assert.Equal((0.0, 0.0, 200.0, 100.0), kb.Rect(0.0, 200, 100));
        var (x, y, w, h) = kb.Rect(1.0, 200, 100);
        Assert.InRange(kb.ZoomEnd, 1.08, 1.14);
        Assert.True(x >= 0 && y >= 0 && x + w <= 200.0001 && y + h <= 100.0001);
        Assert.True(w < 200 && h < 100);
    }

    [Fact]
    public void Cli_parses_motion_flags()
    {
        var o = Parse("--fractal", "Mandelbulb", "--x", "0", "--y", "0", "--zoom", "1", "--mode", "video", "--video-motion", "kenburns",
                      "--orbit", "90", "--no-drift", "--video-seed", "42", "--start-zoom", "0.2", "--out", "x");
        Assert.Equal(BatchVideoMotion.KenBurns, o.VideoMotion);
        Assert.Equal(90.0, o.VideoOrbitDegrees);
        Assert.True(o.VideoNoDrift);
        Assert.Equal(42, o.VideoSeed);
        Assert.True(o.VideoStartZoomSet);

        var d = Parse("--fractal", "Julia", "--x", "0", "--y", "0", "--zoom", "1", "--mode", "video", "--out", "x");
        Assert.Equal(BatchVideoMotion.Auto, d.VideoMotion);
        Assert.False(d.VideoStartZoomSet);
    }

    [Fact]
    public void Cli_rejects_bad_motion_and_orbit()
    {
        Assert.False(BatchOptions.TryParse(
            new[] { "FracturingFog", "--batch", "--mode", "video", "--video-motion", "spin", "--out", "x" },
            2, out _, out var e1));
        Assert.Contains("--video-motion", e1);
        Assert.False(BatchOptions.TryParse(
            new[] { "FracturingFog", "--batch", "--x", "0", "--y", "0", "--zoom", "1", "--mode", "video", "--orbit", "5000", "--out", "x" },
            2, out _, out var e2));
        Assert.Contains("--orbit", e2);
    }

    [Fact]
    public void Every_type_but_Mandelbrot_has_an_offline_calculator()
    {
        foreach (FractalType t in Enum.GetValues<FractalType>())
        {
            var c = PosterRenderer.BuildCaptureCalculator(new PosterRequest
            {
                FractalType = t, Width = 16, Height = 16, Zoom = 1, MaxIterations = 64,
                Quality = QualityPreset.Standard, ColorMap = new HsvPalette(),
                FractalParameters = new FractalParameters(),
            });
            if (t == FractalType.Mandelbrot) Assert.Null(c);
            else Assert.True(c != null, $"{t} has no offline calculator (batch would render Mandelbrot)");
        }
    }
}
