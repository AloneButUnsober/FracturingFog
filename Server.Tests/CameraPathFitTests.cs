// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1056 — drawn camera path → shot camera keys (pure fitting core).

using System;
using System.Collections.Generic;
using System.Linq;

using FracturingFog.Render;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class CameraPathFitTests
{
    private static List<(double X, double Y)> Arc(double r, double fromDeg, double toDeg, int n = 200)
        => Enumerable.Range(0, n + 1)
            .Select(i => fromDeg + (toDeg - fromDeg) * i / n)
            .Select(d => (r * Math.Cos(d * Math.PI / 180), r * Math.Sin(d * Math.PI / 180)))
            .ToList();

    private static readonly CameraPathFitOptions Opts = new()
    {
        DurationSeconds = 8, StartElevation = 0.2, EndElevation = 0.6, MinDistance = 1.2, MaxDistance = 10, MaxKeys = 12,
    };

    [Fact]
    public void FullCircle_GivesAFullTurn_AtTheDrawnDistance()
    {
        var res = CameraPathFit.Fit(Arc(3, 0, 360), Opts);

        Assert.True(res.Ok, res.Error);
        var keys = res.Track!.Keys;
        Assert.InRange(keys.Count, 3, Opts.MaxKeys);
        Assert.Equal(0.0, keys[0].Time);
        Assert.Equal(8.0, keys[^1].Time, 9);
        Assert.Equal(2 * Math.PI, keys[^1].State.Theta - keys[0].State.Theta, 3);   // unwrapped, not 0
        Assert.All(keys, k => Assert.Equal(3.0, k.State.Distance, 6));
        for (int i = 1; i < keys.Count; i++)
        {
            Assert.True(keys[i].Time > keys[i - 1].Time);
            Assert.True(keys[i].State.Theta > keys[i - 1].State.Theta);           // never spins back
        }
    }

    [Fact]
    public void FittedTrack_StaysOnTheDrawnCircle_BetweenKeys()
    {
        // Independent check on the *evaluated* path, not the keys: the camera
        // distance along the whole interpolated track stays near the drawn radius.
        var track = CameraPathFit.Fit(Arc(3, 0, 360), Opts).Track!;
        for (double t = 0; t <= 8; t += 0.05)
            Assert.InRange(track.Evaluate(t).Distance, 2.9, 3.1);
    }

    [Fact]
    public void Clockwise_DecreasesAzimuth_AndCrossingPlusMinusPiStaysContinuous()
    {
        var res = CameraPathFit.Fit(Arc(2.5, 170, -190), Opts); // 170° → -190°: clockwise through ±180
        Assert.True(res.Ok, res.Error);
        var k = res.Track!.Keys;
        for (int i = 1; i < k.Count; i++) Assert.True(k[i].State.Theta < k[i - 1].State.Theta);
        Assert.Equal(-(360.0) * Math.PI / 180, k[^1].State.Theta - k[0].State.Theta, 2);
    }

    [Fact]
    public void ElevationRamps_ByArcFraction()
    {
        var k = CameraPathFit.Fit(Arc(3, 0, 180), Opts).Track!.Keys;
        Assert.Equal(0.2, k[0].State.Phi, 9);
        Assert.Equal(0.6, k[^1].State.Phi, 9);
        foreach (var key in k)
            Assert.Equal(0.2 + 0.4 * key.Time / 8.0, key.State.Phi, 9);
    }

    [Fact]
    public void RadialPushIn_KeepsAzimuth_AndShrinksDistance()
    {
        var line = Enumerable.Range(0, 50).Select(i => (6.0 - i * 0.08, 0.0)).ToList();
        var k = CameraPathFit.Fit(line, Opts).Track!.Keys;
        Assert.Equal(2, k.Count);                                 // a straight line needs only its ends
        Assert.All(k, key => Assert.Equal(0.0, key.State.Theta, 9));
        Assert.True(k[^1].State.Distance < k[0].State.Distance);
    }

    [Fact]
    public void NoisyPath_IsCappedAtMaxKeys()
    {
        var rng = new Random(7);
        var noisy = Arc(4, 0, 720, 800).Select(p => (p.Item1 + rng.NextDouble() * 0.3, p.Item2 + rng.NextDouble() * 0.3)).ToList();
        var res = CameraPathFit.Fit(noisy, Opts with { MaxKeys = 6 });
        Assert.True(res.Ok, res.Error);
        Assert.InRange(res.Track!.Keys.Count, 2, 6);
    }

    [Theory]
    [InlineData("inside")]
    [InlineData("short")]
    [InlineData("far")]
    [InlineData("empty")]
    public void InvalidPaths_AreRejectedWithAReason(string kind)
    {
        List<(double, double)> pts = kind switch
        {
            "inside" => Enumerable.Range(0, 40).Select(i => (-3.0 + i * 0.15, 0.0)).ToList(), // through the centre
            "short" => new() { (3.0, 0.0), (3.05, 0.0) },
            "far" => Arc(15, 0, 90),
            _ => new() { (3.0, 0.0) },
        };
        var res = CameraPathFit.Fit(pts, Opts);
        Assert.False(res.Ok);
        Assert.False(string.IsNullOrWhiteSpace(res.Error));
    }

    [Fact]
    public void DuplicateAndNonFinitePoints_AreIgnored()
    {
        var pts = Arc(3, 0, 90, 20);
        pts.Insert(5, pts[5]);
        pts.Insert(9, (double.NaN, 1));
        Assert.True(CameraPathFit.Fit(pts, Opts).Ok);
    }
}
