// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using FracturingFog;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// Dual-orbit escape-geometry field — S1 (#864, epic #850). See
// Docs/Technical/Theoretical-Fractal-RnD.md §3.6.
public sealed class DualOrbitEscapeTests
{
    private static DualOrbitEscapeCalculator Make(DualOrbitField f, double cx, double cy, bool cEqS)
    {
        var p = new FractalParameters
        {
            DualOrbitField = f, DualOrbitCSeedX = cx, DualOrbitCSeedY = cy, DualOrbitCEqualsS = cEqS,
        };
        return new DualOrbitEscapeCalculator(180, 180) { CenterX = -0.5, CenterY = 0, Zoom = 1.0, FractalParameters = p };
    }

    private static double MeanAbs(float[] b)
    {
        double s = 0; foreach (var v in b) s += Math.Abs(v);
        return s / b.Length;
    }

    // The load-bearing degeneracy (§3.6): with c = s the c-orbit is the z-orbit
    // shifted one step, so E_c = E_z and the separation field is identically 0.
    [Fact]
    public void CEqualsS_CollapsesSeparationToZero()
    {
        var c = Make(DualOrbitField.EscapeSeparation, 0.5, 0.0, cEqS: true);
        c.Calculate();
        Assert.True(MeanAbs(c.SmoothBuffer) < 1e-3, $"c=s should give D≡0 but mean={MeanAbs(c.SmoothBuffer)}");
    }

    // Decoupled c gives a non-trivial escape-separation field.
    [Fact]
    public void DecoupledC_ProducesNonTrivialField()
    {
        var c = Make(DualOrbitField.EscapeSeparation, 0.5, 0.0, cEqS: false);
        c.Calculate();
        Assert.True(MeanAbs(c.SmoothBuffer) > 1.0, $"decoupled field mean={MeanAbs(c.SmoothBuffer)}");
    }

    [Fact]
    public void Fields_DifferFromEachOther()
    {
        var sep = Make(DualOrbitField.EscapeSeparation, 0.5, 0.0, false);
        var mid = Make(DualOrbitField.MidpointResidual, 0.5, 0.0, false);
        var ang = Make(DualOrbitField.DualOrbitAngle, 0.5, 0.0, false);
        var dn = Make(DualOrbitField.DeltaN, 0.5, 0.0, false);
        sep.Calculate(); mid.Calculate(); ang.Calculate(); dn.Calculate();
        Assert.NotEqual(sep.ColorBuffer, mid.ColorBuffer);
        Assert.NotEqual(sep.ColorBuffer, ang.ColorBuffer);
        Assert.NotEqual(mid.ColorBuffer, dn.ColorBuffer);
    }

    [Fact]
    public void RespondsToCSeed()
    {
        var a = Make(DualOrbitField.EscapeSeparation, 0.5, 0.0, false);
        var b = Make(DualOrbitField.EscapeSeparation, -0.3, 0.4, false);
        a.Calculate(); b.Calculate();
        Assert.NotEqual(a.ColorBuffer, b.ColorBuffer);
    }

    [Fact]
    public void IsDeterministic()
    {
        var a = Make(DualOrbitField.DualOrbitAngle, 0.5, 0.0, false);
        var b = Make(DualOrbitField.DualOrbitAngle, 0.5, 0.0, false);
        a.Calculate(); b.Calculate();
        Assert.Equal(a.ColorBuffer, b.ColorBuffer);
    }

    [Fact]
    public void RespondsToPan()
    {
        var a = Make(DualOrbitField.EscapeSeparation, 0.5, 0.0, false);
        var b = Make(DualOrbitField.EscapeSeparation, 0.5, 0.0, false);
        b.CenterX = 0.3;
        a.Calculate(); b.Calculate();
        Assert.NotEqual(a.ColorBuffer, b.ColorBuffer);
    }

    [Fact]
    public void SmoothBuffer_DrivesReliefHeight()
        => Assert.IsAssignableFrom<Interefaces.IHeightFieldSource>(
               new DualOrbitEscapeCalculator(8, 8));

    // ── Registration ─────────────────────────────────────────────────────────

    [Fact]
    public void MotionClass_IsZoomable2D()
        => Assert.Equal(FractalMotionClass.Zoomable2D,
                        FractalMotionCapabilities.MotionClass(FractalType.DualOrbitEscape));

    [Fact]
    public void Capabilities_SuppliesHistogram()
        => Assert.Equal(FractalCapabilities.SuppliesHistogram,
                        FractalCapabilityMap.For(FractalType.DualOrbitEscape));

    [Fact]
    public void HasDisplayName()
        => Assert.Equal("Dual-Orbit Escape", Fractals.FractalNameByNameType[FractalType.DualOrbitEscape]);

    [Fact]
    public void ClonesAndPersists()
    {
        var p = new FractalParameters
        {
            DualOrbitField = DualOrbitField.DualOrbitAngle,
            DualOrbitCSeedX = -0.3, DualOrbitCSeedY = 0.4, DualOrbitCEqualsS = true,
        };
        var cl = p.Clone();
        Assert.Equal(DualOrbitField.DualOrbitAngle, cl.DualOrbitField);
        Assert.Equal(-0.3, cl.DualOrbitCSeedX);
        Assert.True(cl.DualOrbitCEqualsS);

        var pp = new FractalParameters { DualOrbitField = DualOrbitField.DeltaN, DualOrbitCSeedX = 0.9 };
        var snap = RegionFractalParams.Snapshot(FractalType.DualOrbitEscape, pp);
        var restored = new FractalParameters();
        snap!.ApplyTo(restored);
        Assert.Equal(DualOrbitField.DeltaN, restored.DualOrbitField);
        Assert.Equal(0.9, restored.DualOrbitCSeedX);
    }
}
