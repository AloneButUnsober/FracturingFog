// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using FracturingFog;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// Dual-orbit slice-axis selector — S8 (#971, epic #850). Every view is a 2D slice
// of one field F(c0, s) with z0 = 0. The invariants below come from the maths of
// that field, not from re-running the same code path:
//   • two different slices through the same (c0, s) point give the same value;
//   • u → u² + s is even in u, so F(−c0, s) = F(c0, s) (c-plane point symmetry);
//   • at c0 = 0 the c-orbit IS the critical orbit (volume's c = 0 column = the
//     Mandelbrot line);
//   • in SxSy the fixed s params are inert (default view unchanged).
public sealed class DualOrbitSliceTests
{
    private static DualOrbitEscapeCalculator Make(DualOrbitSliceAxes axes, DualOrbitField field,
        int w = 120, int h = 120, double cx = 0.0, double cy = 0.0, double zoom = 1.0,
        double seedX = 0.3, double seedY = 0.2, double sX = -0.78, double sY = 0.15)
    {
        var p = new FractalParameters
        {
            DualOrbitSliceAxes = axes, DualOrbitField = field,
            DualOrbitCSeedX = seedX, DualOrbitCSeedY = seedY, DualOrbitSX = sX, DualOrbitSY = sY,
        };
        return new DualOrbitEscapeCalculator(w, h) { CenterX = cx, CenterY = cy, Zoom = zoom, MaxIterations = 300, FractalParameters = p };
    }

    // Single pixel pinned at the image point (cx, cy): tiny pitch, 1×1 buffer.
    private static float Pin(DualOrbitSliceAxes axes, DualOrbitField field, double imgX, double imgY,
        double seedX, double seedY, double sX, double sY)
    {
        var c = Make(axes, field, 1, 1, imgX, imgY, 1e12, seedX, seedY, sX, sY);
        c.Calculate();
        return c.SmoothBuffer[0];
    }

    [Theory]
    [InlineData(DualOrbitField.GreenRatio)]
    [InlineData(DualOrbitField.EscapeSeparation)]
    [InlineData(DualOrbitField.EscapeTimeC)]
    [InlineData(DualOrbitField.ExternalAngleDelta)]
    public void Slices_Agree_AtTheSamePointOfTheField(DualOrbitField field)
    {
        // One (c0, s) point, reached through four different slices.
        const double c0x = 0.4, c0y = -0.25, s0x = 0.36, s0y = 0.42;
        float viaS  = Pin(DualOrbitSliceAxes.SxSy, field, s0x, s0y, c0x, c0y, 9, 9);
        float viaC  = Pin(DualOrbitSliceAxes.CxCy, field, c0x, c0y, 9, 9, s0x, s0y);
        float viaCS = Pin(DualOrbitSliceAxes.CxSx, field, c0x, s0x, 9, c0y, 9, s0y);
        float viaCY = Pin(DualOrbitSliceAxes.CySy, field, c0y, s0y, c0x, 9, s0x, 9);
        Assert.NotEqual(0f, viaS);
        Assert.Equal(viaS, viaC, 3);
        Assert.Equal(viaS, viaCS, 3);
        Assert.Equal(viaS, viaCY, 3);
    }

    [Fact]
    public void CPlane_IsPointSymmetric_BecauseTheMapIsEven()
    {
        // F(−c0, s) = F(c0, s): the c-orbits of ±c0 coincide after one step.
        var calc = Make(DualOrbitSliceAxes.CxCy, DualOrbitField.EscapeTimeC, 120, 120, 0, 0, 1.4);
        calc.Calculate();
        var b = calc.SmoothBuffer; int w = 120, h = 120, checkedPx = 0;
        for (int y = 1; y < h; y++)
            for (int x = 1; x < w; x++)
            {
                Assert.Equal(b[y * w + x], b[(h - y) * w + (w - x)], 3);
                checkedPx++;
            }
        Assert.True(b.Count(v => v > 0) > 1000, "c-plane should have escaping pixels");
        Assert.True(checkedPx > 10000);
    }

    [Fact]
    public void VolumeCrossSection_ZeroColumn_IsTheMandelbrotLine()
    {
        // CxSx with fixed c.y = 0: the c.x = 0 column has c0 = 0 — the critical
        // orbit itself — so EscapeTimeC there equals EscapeTimeZ, row by row.
        var c = Make(DualOrbitSliceAxes.CxSx, DualOrbitField.EscapeTimeC, 120, 120, 0, -0.75, 1.6, seedY: 0.0);
        var z = Make(DualOrbitSliceAxes.CxSx, DualOrbitField.EscapeTimeZ, 120, 120, 0, -0.75, 1.6, seedY: 0.0);
        c.Calculate(); z.Calculate();
        int col = 60, nonZero = 0, interior = 0;   // imgX = 0 at x = w/2
        for (int y = 0; y < 120; y++)
        {
            float vc = c.SmoothBuffer[y * 120 + col], vz = z.SmoothBuffer[y * 120 + col];
            Assert.Equal(vz, vc, 4);
            if (vz > 0) nonZero++; else interior++;
        }
        // The column crosses the Mandelbrot set along Im s = 0.15: both regimes present.
        Assert.True(nonZero > 5 && interior > 5, $"escaping {nonZero}, bounded {interior}");
    }

    [Fact]
    public void EscapeTimeC_StaysLive_WhereTheCriticalOrbitIsBounded()
    {
        // s = −0.1+0.1i is inside the main cardioid: the z-orbit never escapes, so
        // every two-orbit field is blank on the c-plane — EscapeTimeC is not.
        var green = Make(DualOrbitSliceAxes.CxCy, DualOrbitField.GreenRatio, sX: -0.1, sY: 0.1);
        var escC = Make(DualOrbitSliceAxes.CxCy, DualOrbitField.EscapeTimeC, sX: -0.1, sY: 0.1);
        green.Calculate(); escC.Calculate();
        Assert.All(green.SmoothBuffer, v => Assert.Equal(0f, v));
        Assert.True(escC.SmoothBuffer.Count(v => v > 0) > 1000);
    }

    [Fact]
    public void SxSy_IgnoresFixedS_DefaultViewUnchanged()
    {
        var a = Make(DualOrbitSliceAxes.SxSy, DualOrbitField.EscapeSeparation, cx: -0.5, sX: -0.78, sY: 0.15);
        var b = Make(DualOrbitSliceAxes.SxSy, DualOrbitField.EscapeSeparation, cx: -0.5, sX: 0.9, sY: -1.2);
        a.Calculate(); b.Calculate();
        Assert.Equal(a.SmoothBuffer, b.SmoothBuffer);
    }

    [Fact]
    public void SliceParams_CloneAndPersist()
    {
        var p = new FractalParameters { DualOrbitSliceAxes = DualOrbitSliceAxes.CxSx, DualOrbitSX = -1.3, DualOrbitSY = 0.02 };
        var cl = p.Clone();
        Assert.Equal(DualOrbitSliceAxes.CxSx, cl.DualOrbitSliceAxes);
        Assert.Equal(-1.3, cl.DualOrbitSX);
        Assert.Equal(0.02, cl.DualOrbitSY);

        var restored = new FractalParameters();
        RegionFractalParams.Snapshot(FractalType.DualOrbitEscape, p)!.ApplyTo(restored);
        Assert.Equal(DualOrbitSliceAxes.CxSx, restored.DualOrbitSliceAxes);
        Assert.Equal(-1.3, restored.DualOrbitSX);
        Assert.Equal(0.02, restored.DualOrbitSY);
    }
}
