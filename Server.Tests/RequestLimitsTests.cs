// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using FracturingFog.Server.Guard;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class RequestLimitsTests
{
    [Fact]
    public void Default_MaxPixels_Matches64MP()
    {
        // Documented in Guard/RequestLimits.cs — 64 MiB pixel ceiling.
        Assert.Equal(64L * 1024L * 1024L, RequestLimits.Default.MaxPixels);
    }

    [Theory]
    [InlineData(1920, 1080, true)]    // FHD
    [InlineData(3840, 2160, true)]    // 4K
    [InlineData(8192, 8192, true)]    // 64 MP exact ceiling
    [InlineData(8193, 8192, false)]   // 1 over
    [InlineData(16384, 16384, false)] // 256 MP — way over
    public void MaxPixels_GuardsAtDocumentedCeiling(int w, int h, bool shouldFit)
    {
        var lim = RequestLimits.Default;
        long pixels = (long)w * h;
        Assert.Equal(shouldFit, pixels <= lim.MaxPixels);
    }

    [Theory]
    [InlineData(15, false)]      // below MinWidth=16
    [InlineData(16, true)]
    [InlineData(32768, true)]    // MaxWidth=32768
    [InlineData(32769, false)]
    public void Width_RespectsConfiguredBounds(int w, bool inRange)
    {
        var lim = RequestLimits.Default;
        Assert.Equal(inRange, w >= lim.MinWidth && w <= lim.MaxWidth);
    }

    [Theory]
    [InlineData(0.4, false)]
    [InlineData(0.5, true)]      // MinVideoSeconds
    [InlineData(600.0, true)]    // MaxVideoSeconds
    [InlineData(600.1, false)]
    public void VideoSeconds_RespectsBounds(double s, bool inRange)
    {
        var lim = RequestLimits.Default;
        Assert.Equal(inRange, s >= lim.MinVideoSeconds && s <= lim.MaxVideoSeconds);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]        // MinVideoFps
    [InlineData(240, true)]      // MaxVideoFps
    [InlineData(241, false)]
    public void VideoFps_RespectsBounds(int fps, bool inRange)
    {
        var lim = RequestLimits.Default;
        Assert.Equal(inRange, fps >= lim.MinVideoFps && fps <= lim.MaxVideoFps);
    }

    [Fact]
    public void Default_MaxIterations_IsOneMillion()
    {
        // Documented in Guard/RequestLimits.cs — caps inner calculator
        // loop budget per pixel so a remote client can't request 10^9
        // iterations and burn the worker.
        Assert.Equal(1_000_000, RequestLimits.Default.MaxIterations);
    }

    [Theory]
    [InlineData(1_000, true)]
    [InlineData(1_000_000, true)]      // exact ceiling
    [InlineData(1_000_001, false)]     // one over
    [InlineData(1_000_000_000, false)] // pathological
    public void MaxIterations_GuardsAtDocumentedCeiling(int iter, bool shouldFit)
    {
        Assert.Equal(shouldFit, iter <= RequestLimits.Default.MaxIterations);
    }

    [Fact]
    public void Default_MaxVideoFramePixels_Is16Billion()
    {
        // Aggregate budget w*h*seconds*fps; documented in RequestLimits.cs.
        Assert.Equal(16_000_000_000L, RequestLimits.Default.MaxVideoFramePixels);
    }

    [Theory]
    // w * h * seconds * fps
    [InlineData(1920, 1080, 60.0, 60, true)]     // FHD/60 — easy
    [InlineData(3840, 2160, 60.0, 60, false)]    // 4K/60 — over budget (~30 B px)
    [InlineData(8192, 8192, 1.0, 1, true)]       // single deep frame inside MaxPixels budget
    [InlineData(8192, 8192, 600.0, 240, false)]  // pathological — would be ~9.7 T pixels
    public void MaxVideoFramePixels_GuardsAggregate(int w, int h, double seconds, int fps, bool shouldFit)
    {
        long framePixels = (long)w * h * (long)System.Math.Ceiling(seconds * fps);
        Assert.Equal(shouldFit, framePixels <= RequestLimits.Default.MaxVideoFramePixels);
    }
}
