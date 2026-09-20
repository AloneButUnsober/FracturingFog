// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using FracturingFog;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #909 — quaternion Mandelbrot dual-orbit surface colouring (the 3D dual-orbit
// render). The detailed quaternion solid, coloured by a decoupled second orbit.
public sealed class QMandelDualOrbitTests
{
    private static uint[] Render(bool dual, double sx, double sy, double sz)
    {
        var p = new FractalParameters
        {
            QMandelDualOrbitColor = dual,
            QMandelDualSeedX = sx, QMandelDualSeedY = sy, QMandelDualSeedZ = sz,
            QMandelIterations = 14,
        };
        var c = new QuatMandelbrotCalculator(128, 128) { Zoom = 1.0, FractalParameters = p };
        c.Calculate();
        return (uint[])c.ColorBuffer.Clone();
    }

    [Fact]
    public void DualColour_DiffersFromStandard()
        => Assert.NotEqual(Render(false, 0, 0, 0), Render(true, 0.4, 0.3, 0.2));

    [Fact]
    public void DualColour_RespondsToSeed()
        => Assert.NotEqual(Render(true, 0.4, 0.3, 0.2), Render(true, 0.6, 0.0, 0.0));

    [Fact]
    public void DualColour_IsDeterministic()
        => Assert.Equal(Render(true, 0.4, 0.3, 0.2), Render(true, 0.4, 0.3, 0.2));

    [Fact]
    public void SurfaceScalar_EscapingSeedNonZero_BoundedSeedZero()
    {
        // At c = 0 the seed-0 orbit is bounded; a decoupled seed just past the
        // unit disk escapes after a few iterations (non-zero continuous count),
        // while a zero seed stays fixed (0).
        double esc = QuatMandelbrotCalculator.DualOrbitSurfaceScalar(0, 0, 0, 0, 1.2, 0, 0, 14, 16.0);
        double zero = QuatMandelbrotCalculator.DualOrbitSurfaceScalar(0, 0, 0, 0, 0, 0, 0, 14, 16.0);
        Assert.True(esc > 0.0, $"escaping seed scalar={esc}");
        Assert.Equal(0.0, zero);
    }

    [Fact]
    public void ClonesAndPersists()
    {
        var p = new FractalParameters
        {
            QMandelDualOrbitColor = true, QMandelDualSeedX = 0.6, QMandelDualSeedY = -0.2, QMandelDualSeedZ = 0.1,
        };
        var cl = p.Clone();
        Assert.True(cl.QMandelDualOrbitColor);
        Assert.Equal(0.6, cl.QMandelDualSeedX);

        var snap = RegionFractalParams.Snapshot(FractalType.QuaternionMandelbrot, p);
        var restored = new FractalParameters();
        snap!.ApplyTo(restored);
        Assert.True(restored.QMandelDualOrbitColor);
        Assert.Equal(0.6, restored.QMandelDualSeedX);
        Assert.Equal(-0.2, restored.QMandelDualSeedY);
        Assert.Equal(0.1, restored.QMandelDualSeedZ);
    }
}
