// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using FracturingFog;
using FracturingFog.Export;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// Dual-orbit escape-geometry volume — S9 (#972, epic #850). World X = c.x, Z = c.y,
// Y = s.x − centre; s.y fixed. The solid is {c-orbit of u → u² + s bounded}.
// Checks use independent ground truth (brute-force orbit membership, the map's
// evenness, the Mandelbrot line) rather than re-running the DE against itself.
public sealed class DualOrbitVolumeTests
{
    private const double Center = -0.75, SY = 0.15, Bail2 = 64.0 * 64.0, Half = 1.4;
    private const int Iter = 48;

    private static double De(double x, double y, double z)
        => DualOrbitVolumeCalculator.VolumeDE(x, y, z, Center, SY, Bail2, Iter, Half);

    // Independent membership: does the orbit of u0 under u → u² + s stay bounded?
    private static bool Bounded(double ur, double ui, double sr, double si, int n = 2000)
    {
        for (int i = 0; i < n; i++)
        {
            if (ur * ur + ui * ui > 4.0 && i > 0) return false;
            double t = ur * ur - ui * ui + sr; ui = 2 * ur * ui + si; ur = t;
        }
        return true;
    }

    [Fact]
    public void DE_IsConservative_NoSetPointInsideHalfTheEstimate()
    {
        // Sample points off the solid; every point within ½·DE must be outside it
        // (escaping c-orbit or beyond the slab). This is what makes raymarching safe.
        var rng = new Random(972);
        int tested = 0;
        for (int k = 0; k < 400; k++)
        {
            double x = rng.NextDouble() * 4 - 2, y = rng.NextDouble() * 2.6 - 1.3, z = rng.NextDouble() * 4 - 2;
            double d = De(x, y, z);
            if (d <= 1e-4 || d > 0.5) continue;
            for (int j = 0; j < 12; j++)
            {
                double dx = rng.NextDouble() * 2 - 1, dy = rng.NextDouble() * 2 - 1, dz = rng.NextDouble() * 2 - 1;
                double len = Math.Sqrt(dx * dx + dy * dy + dz * dz); if (len < 1e-9) continue;
                double r = 0.5 * d * rng.NextDouble() / len;
                double px = x + dx * r, py = y + dy * r, pz = z + dz * r;
                bool inSlab = Math.Abs(py) <= Half;
                Assert.False(inSlab && Bounded(px, pz, py + Center, SY),
                    $"set point ({px:F4},{py:F4},{pz:F4}) within ½·DE={d:F4} of ({x:F3},{y:F3},{z:F3})");
                tested++;
            }
        }
        Assert.True(tested > 500, $"only {tested} samples");
    }

    [Theory]
    [InlineData(0.0, 0.5, 0.0)]      // s = −0.25+0.15i (cardioid), c = 0
    [InlineData(0.1, 0.65, -0.05)]   // s = −0.10+0.15i, c near 0
    [InlineData(0.0, -0.25, 0.0)]    // s = −1.00+0.15i (period-2 bulb)
    public void Interior_ReadsZero(double x, double y, double z)
    {
        Assert.True(Bounded(x, z, y + Center, SY), "test point must be in the set");
        Assert.Equal(0.0, De(x, y, z));
    }

    [Fact]
    public void Volume_IsPointSymmetric_InTheCPlane()
    {
        // u → u² + s is even in u: the c-orbits of ±c coincide after one step.
        var rng = new Random(7);
        for (int k = 0; k < 300; k++)
        {
            double x = rng.NextDouble() * 4 - 2, y = rng.NextDouble() * 2.6 - 1.3, z = rng.NextDouble() * 4 - 2;
            Assert.Equal(De(x, y, z), De(-x, y, -z), 12);
        }
    }

    [Fact]
    public void ZeroColumn_IsTheMandelbrotLine()
    {
        // At c = 0 the c-orbit IS the critical orbit, so the column is solid
        // exactly where s = s.x + i·s.y lies in the Mandelbrot set.
        int inside = 0, outside = 0;
        for (double y = -1.3; y <= 1.3; y += 0.01)
        {
            bool inM = Bounded(0, 0, y + Center, SY);
            double d = De(0, y, 0);
            // Skip the boundary band where finite iteration counts disagree.
            if (!inM && d < 1e-3) continue;
            if (inM) { Assert.Equal(0.0, d); inside++; }
            else { Assert.True(d > 0, $"s.x={y + Center:F3} outside M but DE={d}"); outside++; }
        }
        Assert.True(inside > 20 && outside > 20, $"inside {inside}, outside {outside}");
    }

    [Fact]
    public void Slab_ClipsTheDustBeyondTheSweepWindow()
    {
        // s.x = 1.35 is far right of M: its Julia set is dust, but the slab bounds it.
        Assert.True(De(0.3, Half + 0.3, 0.1) >= 0.3 - 1e-9);
        Assert.True(De(0.3, -(Half + 0.5), 0.1) >= 0.5 - 1e-9);
    }

    [Fact]
    public void CriticalSmooth_BoundedInsideM_EscapingOutside()
    {
        Assert.Equal(-1.0, DualOrbitVolumeCalculator.CriticalSmooth(-0.1, 0.15, 64));
        Assert.True(DualOrbitVolumeCalculator.CriticalSmooth(1.0, 0.15, 64) > 0);
    }

    [Theory]
    [InlineData(DualOrbitVolumeColor.ExternalAngle)]
    [InlineData(DualOrbitVolumeColor.CriticalLayer)]
    [InlineData(DualOrbitVolumeColor.Steps)]
    public void Render_HitsTheSolid_AndIsDeterministic(DualOrbitVolumeColor color)
    {
        DualOrbitVolumeCalculator Make() => new(96, 72) { FractalParameters = new FractalParameters { DualOrbitVolumeColor = color } };
        var a = Make(); var b = Make();
        a.Calculate(); b.Calculate();
        uint bg = a.ColorMap.InSetColor;
        int hits = a.ColorBuffer.Count(c => c != bg);
        Assert.True(hits > 96 * 72 / 10, $"{color}: only {hits} hit pixels");
        Assert.Equal(a.ColorBuffer, b.ColorBuffer);
    }

    [Fact]
    public void ColourSources_Differ()
    {
        uint[] Render(DualOrbitVolumeColor c)
        {
            var calc = new DualOrbitVolumeCalculator(96, 72) { FractalParameters = new FractalParameters { DualOrbitVolumeColor = c } };
            calc.Calculate(); return calc.ColorBuffer;
        }
        var angle = Render(DualOrbitVolumeColor.ExternalAngle);
        Assert.NotEqual(angle, Render(DualOrbitVolumeColor.CriticalLayer));
        Assert.NotEqual(angle, Render(DualOrbitVolumeColor.Steps));
    }

    [Fact]
    public void ParamsCloneAndPersist()
    {
        var p = new FractalParameters
        {
            DualOrbitVolumeSY = -0.2, DualOrbitVolumeSXCenter = -1.1, DualOrbitVolumeHalfHeight = 0.9,
            DualOrbitVolumeColor = DualOrbitVolumeColor.CriticalLayer,
            DualOrbitVolumeCameraDistance = 5.5, DualOrbitVolumeCameraTheta = 1.2, DualOrbitVolumeCameraPhi = 0.8,
        };
        var cl = p.Clone();
        Assert.Equal(-0.2, cl.DualOrbitVolumeSY);
        Assert.Equal(0.9, cl.DualOrbitVolumeHalfHeight);
        Assert.Equal(DualOrbitVolumeColor.CriticalLayer, cl.DualOrbitVolumeColor);

        var restored = new FractalParameters();
        RegionFractalParams.Snapshot(FractalType.DualOrbitVolume, p)!.ApplyTo(restored);
        Assert.Equal(-0.2, restored.DualOrbitVolumeSY);
        Assert.Equal(-1.1, restored.DualOrbitVolumeSXCenter);
        Assert.Equal(0.9, restored.DualOrbitVolumeHalfHeight);
        Assert.Equal(DualOrbitVolumeColor.CriticalLayer, restored.DualOrbitVolumeColor);
        Assert.Equal(5.5, restored.DualOrbitVolumeCameraDistance);
        Assert.Equal(1.2, restored.DualOrbitVolumeCameraTheta);
        Assert.Equal(0.8, restored.DualOrbitVolumeCameraPhi);
    }

    [Fact]
    public void Registration()
    {
        Assert.Equal(FractalMotionClass.Raymarch3D, FractalMotionCapabilities.MotionClass(FractalType.DualOrbitVolume));
        Assert.Equal(FractalCapabilities.SuppliesNormals | FractalCapabilities.SuppliesDE,
                     FractalCapabilityMap.For(FractalType.DualOrbitVolume));
        Assert.Equal("Dual-Orbit Volume", Fractals.FractalNameByNameType[FractalType.DualOrbitVolume]);
        Assert.True(RaymarchMeshSampler.IsMeshExportable(FractalType.DualOrbitVolume));
        var de = RaymarchMeshSampler.For(FractalType.DualOrbitVolume, new FractalParameters());
        Assert.NotNull(de);
        Assert.Equal(De(0.4, 0.2, -0.3), de!.Evaluate(0.4, 0.2, -0.3), 12);   // print = picture
    }
}
