// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// S3 (#568) — the shared accumulation-motion-blur averager. Locks: a single
// weighted frame resolves to itself (opaque) so a closed shutter is byte-
// identical; equal-weight frames average; weights are honoured; ShutterSamples
// collapses to a single tap when the shutter is closed and spans the window
// otherwise.

using System;
using FracturingFog.Rendering;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MotionBlurAccumulatorTests
{
    private static uint Bgra(int r, int g, int b) =>
        0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;

    [Fact]
    public void SingleFrame_ResolvesToItself_Opaque()
    {
        var acc = new MotionBlurAccumulator(2);
        var frame = new[] { Bgra(10, 20, 30), Bgra(200, 100, 50) };
        acc.Add(frame, 1.0);
        var dst = new uint[2];
        acc.Resolve(dst);
        Assert.Equal(frame, dst);
        Assert.Equal(1, acc.SampleCount);
    }

    [Fact]
    public void EqualWeight_Frames_Average()
    {
        var acc = new MotionBlurAccumulator(1);
        acc.Add(new[] { Bgra(0, 0, 0) }, 1.0);
        acc.Add(new[] { Bgra(255, 255, 255) }, 1.0);
        var dst = new uint[1];
        acc.Resolve(dst);
        // (0 + 255) / 2 = 127.5 → 128 (round half up)
        Assert.Equal(Bgra(128, 128, 128), dst[0]);
        Assert.Equal(2, acc.SampleCount);
    }

    [Fact]
    public void Weights_AreHonoured()
    {
        var acc = new MotionBlurAccumulator(1);
        acc.Add(new[] { Bgra(0, 0, 0) }, 3.0);      // 3× weight to black
        acc.Add(new[] { Bgra(100, 100, 100) }, 1.0);
        var dst = new uint[1];
        acc.Resolve(dst);
        // (0*3 + 100*1) / 4 = 25
        Assert.Equal(Bgra(25, 25, 25), dst[0]);
    }

    [Fact]
    public void ZeroWeight_Ignored()
    {
        var acc = new MotionBlurAccumulator(1);
        acc.Add(new[] { Bgra(40, 50, 60) }, 1.0);
        acc.Add(new[] { Bgra(200, 200, 200) }, 0.0);   // ignored
        var dst = new uint[1];
        acc.Resolve(dst);
        Assert.Equal(Bgra(40, 50, 60), dst[0]);
        Assert.Equal(1, acc.SampleCount);
    }

    [Fact]
    public void Reset_Clears()
    {
        var acc = new MotionBlurAccumulator(1);
        acc.Add(new[] { Bgra(255, 255, 255) }, 1.0);
        acc.Reset();
        acc.Add(new[] { Bgra(10, 10, 10) }, 1.0);
        var dst = new uint[1];
        acc.Resolve(dst);
        Assert.Equal(Bgra(10, 10, 10), dst[0]);
    }

    [Fact]
    public void ShutterSamples_ClosedShutter_IsSingleTap()
    {
        var s = MotionBlurAccumulator.ShutterSamples(0.5, 0.1, 0.0, 8);
        Assert.Single(s);
        Assert.Equal(0.5, s[0].t);
        Assert.Equal(1.0, s[0].weight);

        var one = MotionBlurAccumulator.ShutterSamples(0.5, 0.1, 0.5, 1);
        Assert.Single(one);
    }

    [Fact]
    public void ShutterSamples_OpenShutter_SpansWindow_CentredOnT()
    {
        double t = 0.5, frameStep = 0.1, shutter = 1.0;
        var s = MotionBlurAccumulator.ShutterSamples(t, frameStep, shutter, 5);
        Assert.Equal(5, s.Length);
        double half = 0.5 * shutter * frameStep;   // 0.05
        Assert.Equal(t - half, s[0].t, 12);
        Assert.Equal(t + half, s[^1].t, 12);
        // Equal weights summing to 1.
        double wsum = 0; foreach (var (_, w) in s) wsum += w;
        Assert.Equal(1.0, wsum, 12);
    }

    [Fact]
    public void ShutterSamples_Clamps_To_Unit_Interval()
    {
        // t at the very start with an open shutter must not produce negative times.
        var s = MotionBlurAccumulator.ShutterSamples(0.0, 0.2, 1.0, 4);
        foreach (var (ts, _) in s)
            Assert.InRange(ts, 0.0, 1.0);
    }
}
