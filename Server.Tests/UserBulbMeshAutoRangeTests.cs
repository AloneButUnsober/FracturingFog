// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using Xunit;
using FracturingFog.Export;

namespace FracturingFog.Server.Tests;

// #112 follow-up — auto-range probe. ProbeBoundingRange casts rays and returns
// a padded half-extent enclosing the set, so the export cube can be auto-sized
// instead of hand-tuned (too small clips; too large wastes grid / leaves holes).
public class UserBulbMeshAutoRangeTests
{
    [Fact]
    public void Probe_EnclosesSphere_WithMargin()
    {
        // Unit-radius sphere DE (surface at |p| = 1).
        SampleDistance sphere = (x, y, z) => Math.Sqrt(x * x + y * y + z * z) - 1.0;
        double r = UserBulbMeshExporter.ProbeBoundingRange(sphere, 0, 0, 0);

        // Must enclose radius 1 (never clip) but stay reasonably tight (margin,
        // not the 8.0 cap).
        Assert.True(r >= 1.0, $"probe {r:F3} must enclose the unit sphere");
        Assert.True(r < 2.0, $"probe {r:F3} should be a snug margin, not the cap");
    }

    [Fact]
    public void Probe_OffsetSphere_EnclosesFarSide()
    {
        // Sphere radius 1 centred at x=1 → far side reaches x=2 from the origin.
        SampleDistance off = (x, y, z) =>
            Math.Sqrt((x - 1.0) * (x - 1.0) + y * y + z * z) - 1.0;
        double r = UserBulbMeshExporter.ProbeBoundingRange(off, 0, 0, 0);
        Assert.True(r >= 2.0, $"probe {r:F3} must reach the far side at radius 2");
    }

    [Fact]
    public void Probe_EmptyField_ReturnsZero()
    {
        // DE never at/inside the surface anywhere in range → no surface found.
        SampleDistance empty = (_, _, _) => 100.0;
        Assert.Equal(0.0, UserBulbMeshExporter.ProbeBoundingRange(empty, 0, 0, 0));
    }
}
