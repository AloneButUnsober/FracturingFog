// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Regression for the relief height-field prepass concurrency race: the #155
// pre-pass used to cache its filtered field + maxima in mutable process-global
// statics (s_compressed / s_prepassMaxH / despike + low-pass scratch), so two
// relief renders running CONCURRENTLY (UI vs batch, or parallel test classes)
// could read prepass state another render wrote — producing systematically wrong
// shading. The fix makes the prepass state per-render (an immutable cache swapped
// under a lock). This test runs two DIFFERENT relief renders on parallel tasks
// many times and asserts each still matches its own serial baseline.

using System;
using System.Threading.Tasks;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.Rendering.Lighting;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class ReliefPrepassConcurrencyTests
{
    private static (uint[] albedo, float[] height) Scene(double cx, double cy, double zoom)
    {
        int w = 128, h = 96;
        var calc = new MandelbrotCalculator(w, h)
        {
            CenterX = cx, CenterY = cy, Zoom = zoom, MaxIterations = 400,
            ColorMap = new MonoBandMap(),
        };
        calc.Calculate(default);
        return ((uint[])calc.ColorBuffer.Clone(), (float[])calc.SmoothBuffer.Clone());
    }

    private static FractalParameters Relief() => new()
    {
        Relief2DEnabled = true,
        Relief2DRaymarch = true,
        Relief2DHeightScale = 1.4,
        Relief2DCameraAzimuthDeg = 25,
        Relief2DCameraElevationDeg = 45,
        Relief2DCameraFovDeg = 55,
        Relief2DGroundPlane = false,
        Relief2DSupersample = 2,
    };

    private static uint[] Render(uint[] albedo, float[] height, int w, int h)
    {
        var dst = new uint[w * h];
        HeightfieldRaymarch2D.Render(albedo, height, w, h, Relief(), dst, out _);
        return dst;
    }

    [Fact]
    public void Concurrent_Different_Renders_Match_Their_Serial_Baselines()
    {
        int w = 128, h = 96;
        // Two DISTINCT fields → distinct prepass keys → the cache slot is contested
        // if the two renders overlap.
        var a = Scene(-0.75, 0.0, 1.0);
        var b = Scene(-0.5, 0.6, 2.0);

        // Serial golden for each — the ground truth an interleaved render must equal.
        var goldenA = Render(a.albedo, a.height, w, h);
        var goldenB = Render(b.albedo, b.height, w, h);
        // Sanity: the two scenes actually differ (otherwise the test is vacuous).
        Assert.False(System.Linq.Enumerable.SequenceEqual(goldenA, goldenB));

        // Hammer: many rounds of A and B rendered on parallel tasks. Under the old
        // shared-static prepass one of them would intermittently pick up the other's
        // filtered field / max-height and diverge from its baseline.
        for (int round = 0; round < 40; round++)
        {
            var ta = Task.Run(() => Render(a.albedo, a.height, w, h));
            var tb = Task.Run(() => Render(b.albedo, b.height, w, h));
            Task.WaitAll(ta, tb);
            Assert.True(System.Linq.Enumerable.SequenceEqual(goldenA, ta.Result),
                $"render A diverged from its serial baseline on round {round} (prepass race)");
            Assert.True(System.Linq.Enumerable.SequenceEqual(goldenB, tb.Result),
                $"render B diverged from its serial baseline on round {round} (prepass race)");
        }
    }
}
