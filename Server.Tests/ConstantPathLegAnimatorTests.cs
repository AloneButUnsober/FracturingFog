// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Numerics;
using FracturingFog;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #92 / P2 — Julia constant-path animated legs. The default constant drift
// synthesised for un-animated Julia/Phoenix/Glynn video legs.
public sealed class ConstantPathLegAnimatorTests
{
    private const double Eps = 1e-9;

    private static Complex Offset(ConstantPathShape shape, double u, double amp = 0.05,
        double startAngle = 0.3, double lineAngle = 0.7)
    {
        var a = new ConstantPathLegAnimator("JuliaC", Complex.Zero, amp, shape,
            legSeconds: 10.0, setter: _ => { }, startAngle: startAngle, lineAngle: lineAngle);
        return a.Offset(u);
    }

    // Every shape must start at zero offset so the leg's pre-rendered start
    // frame (rendered at the authored constant) matches the first animated frame.
    [Theory]
    [InlineData(ConstantPathShape.Line)]
    [InlineData(ConstantPathShape.Orbit)]
    [InlineData(ConstantPathShape.Arc)]
    public void Offset_IsZero_AtStart(ConstantPathShape shape)
    {
        var o = Offset(shape, 0.0);
        Assert.True(o.Magnitude < Eps, $"{shape} u=0 offset {o} not ~0");
    }

    // Orbit is a full circle: it returns to the authored constant at leg end.
    [Fact]
    public void Orbit_ReturnsToBase_AtEnd()
    {
        var o = Offset(ConstantPathShape.Orbit, 1.0);
        Assert.True(o.Magnitude < Eps, $"orbit u=1 offset {o} not ~0");
    }

    // Line is out-and-back: zero at both ends, peak = amplitude at mid.
    [Fact]
    public void Line_OutAndBack()
    {
        const double amp = 0.05;
        Assert.True(Offset(ConstantPathShape.Line, 1.0, amp).Magnitude < Eps);
        Assert.Equal(amp, Offset(ConstantPathShape.Line, 0.5, amp).Magnitude, 6);
    }

    // Orbit bulges away from base mid-leg (it doesn't sit still).
    [Fact]
    public void Orbit_MovesMidLeg()
        => Assert.True(Offset(ConstantPathShape.Orbit, 0.5).Magnitude > 1e-3);

    // Ticking across the whole leg walks u 0→1: base at the start, moved in the
    // middle, back to base at the end (orbit).
    [Fact]
    public void Tick_DrivesSetter_OverLeg()
    {
        var baseC = new Complex(-0.7, 0.27015);
        Complex current = baseC;
        var a = new ConstantPathLegAnimator("JuliaC", baseC, 0.05,
            ConstantPathShape.Orbit, legSeconds: 1.0, setter: c => current = c,
            startAngle: 0.0);

        a.Tick(0.5);   // u = 0.5 — off base
        Assert.True((current - baseC).Magnitude > 1e-3);

        a.Tick(0.5);   // u = 1.0 — orbit closed, back to base
        Assert.True((current - baseC).Magnitude < Eps);
    }

    [Fact]
    public void Tick_Disabled_DoesNotMutate()
    {
        var baseC = new Complex(0.1, 0.2);
        Complex current = baseC;
        var a = new ConstantPathLegAnimator("JuliaC", baseC, 0.05,
            ConstantPathShape.Line, legSeconds: 1.0, setter: c => current = c)
        { IsEnabled = false };
        a.Tick(0.5);
        Assert.Equal(baseC, current);
    }

    [Fact]
    public void Cost_IsCheap()
        => Assert.Equal(AnimatableParamCost.Cheap,
            new ConstantPathLegAnimator("JuliaC", Complex.Zero, 0.05,
                ConstantPathShape.Orbit, 1.0, _ => { }).Cost);

    // ── Resolver ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(FractalType.Julia, "JuliaC")]
    [InlineData(FractalType.Phoenix, "PhoenixP")]
    [InlineData(FractalType.Glynn, "GlynnC")]
    public void Resolver_MapsConstantParamName(FractalType t, string expected)
        => Assert.Equal(expected, ConstantDriftResolver.ConstantParamName(t));

    [Theory]
    [InlineData(FractalType.Mandelbrot)]
    [InlineData(FractalType.Newton)]
    [InlineData(FractalType.BurningShip)]
    [InlineData(FractalType.Mandelbulb)]
    public void Resolver_NoConstant_ForOtherFamilies(FractalType t)
    {
        Assert.Null(ConstantDriftResolver.ConstantParamName(t));
        Assert.Null(ConstantDriftResolver.TryBuild(t, new FractalParameters(), 8.0));
    }

    [Fact]
    public void Resolver_NullParams_ReturnsNull()
        => Assert.Null(ConstantDriftResolver.TryBuild(FractalType.Julia, null, 8.0));

    // Deterministic build (no RNG) centres on the params' constant, names the
    // right param, and — being an orbit — leaves u=0 on the authored value.
    [Fact]
    public void Resolver_Julia_CentersOnAuthoredConstant()
    {
        var p = new FractalParameters { JuliaC = new Complex(-0.8, 0.156) };
        var a = ConstantDriftResolver.TryBuild(FractalType.Julia, p, 8.0);
        Assert.NotNull(a);
        Assert.Equal("JuliaC", a!.Name);

        // u=0 → the authored constant, unchanged.
        a.Tick(0.0);
        Assert.Equal(new Complex(-0.8, 0.156), p.JuliaC);
    }

    [Fact]
    public void Resolver_Phoenix_DrivesPhoenixP()
    {
        var p = new FractalParameters { PhoenixP = new Complex(0.56667, 0.0) };
        var a = ConstantDriftResolver.TryBuild(FractalType.Phoenix, p, 4.0);
        Assert.NotNull(a);
        a!.Tick(2.0); // mid-leg
        Assert.NotEqual(new Complex(0.56667, 0.0), p.PhoenixP);
    }

    // ── #801 — per-leg start-position + speed variance ─────────────────────

    // Default: no start offset, leg begins on the authored constant.
    [Fact]
    public void StartValue_Default_IsBase()
    {
        var baseC = new Complex(-0.7, 0.27015);
        var a = new ConstantPathLegAnimator("JuliaC", baseC, 0.05,
            ConstantPathShape.Orbit, 1.0, _ => { });
        Assert.False(a.HasStartOffset);
        Assert.Equal(baseC, a.StartValue);
    }

    // A start offset shifts StartValue but the path offset is still zero at u=0
    // (the offset is added on top, not folded into Offset()).
    [Fact]
    public void StartOffset_ShiftsStartValue_ButOffsetStillZeroAtStart()
    {
        var baseC = new Complex(0.1, 0.2);
        var off = new Complex(0.03, -0.01);
        var a = new ConstantPathLegAnimator("JuliaC", baseC, 0.05,
            ConstantPathShape.Orbit, 1.0, _ => { }, startOffset: off);
        Assert.True(a.HasStartOffset);
        Assert.Equal(baseC + off, a.StartValue);
        Assert.True(a.Offset(0.0).Magnitude < Eps);
    }

    // ApplyStartValue writes StartValue into the bound param (used to sync the
    // leg pre-render before the first Tick).
    [Fact]
    public void ApplyStartValue_WritesStartValue()
    {
        var baseC = new Complex(-0.8, 0.156);
        var off = new Complex(0.02, 0.02);
        Complex current = Complex.Zero;
        var a = new ConstantPathLegAnimator("JuliaC", baseC, 0.05,
            ConstantPathShape.Line, 1.0, c => current = c, startOffset: off);
        a.ApplyStartValue();
        Assert.Equal(baseC + off, current);
    }

    // First tick begins at (near) StartValue when there's a start offset.
    [Fact]
    public void Tick_BeginsNearStartValue_WithOffset()
    {
        var baseC = new Complex(0.0, 0.0);
        var off = new Complex(0.04, 0.0);
        Complex current = Complex.Zero;
        var a = new ConstantPathLegAnimator("JuliaC", baseC, 0.05,
            ConstantPathShape.Orbit, legSeconds: 100.0, setter: c => current = c,
            startOffset: off);
        a.Tick(0.001); // u ≈ 0
        Assert.True((current - a.StartValue).Magnitude < 1e-3);
    }

    // Integer speed still closes an orbit (returns to base); a fractional speed
    // ends off-base.
    [Fact]
    public void Speed_IntegerOrbit_ClosesToBase_FractionalDoesNot()
    {
        var closed = new ConstantPathLegAnimator("JuliaC", Complex.Zero, 0.05,
            ConstantPathShape.Orbit, 1.0, _ => { }, speed: 2.0);
        Assert.True(closed.Offset(1.0).Magnitude < Eps);

        var open = new ConstantPathLegAnimator("JuliaC", Complex.Zero, 0.05,
            ConstantPathShape.Orbit, 1.0, _ => { }, speed: 1.5);
        Assert.True(open.Offset(1.0).Magnitude > 1e-3);
    }

    // Higher speed travels farther by mid-leg (more of the orbit covered).
    [Fact]
    public void Speed_MovesFartherByMidLeg()
    {
        var slow = new ConstantPathLegAnimator("JuliaC", Complex.Zero, 0.05,
            ConstantPathShape.Line, 1.0, _ => { }, speed: 0.5);
        var fast = new ConstantPathLegAnimator("JuliaC", Complex.Zero, 0.05,
            ConstantPathShape.Line, 1.0, _ => { }, speed: 1.0);
        // At u=0.5: slow = sin(π·0.5·0.5)=sin(π/4); fast = sin(π·0.5)=1 → farther.
        Assert.True(fast.Offset(0.5).Magnitude > slow.Offset(0.5).Magnitude);
    }

    // Resolver: varyStart produces a bounded start offset (≤ amplitude); default
    // leaves the leg on the authored constant.
    [Fact]
    public void Resolver_VaryStart_BoundedOffset()
    {
        var p = new FractalParameters { JuliaC = new Complex(-0.7, 0.27015) };
        var rng = new Random(1234);
        var a = ConstantDriftResolver.TryBuild(FractalType.Julia, p, 8.0, rng, varyStart: true);
        Assert.NotNull(a);
        Assert.True(a!.HasStartOffset);
        double amp = System.Math.Clamp(0.045 * (0.5 + p.JuliaC.Magnitude), 0.02, 0.12);
        double offMag = (a.StartValue - p.JuliaC).Magnitude;
        Assert.True(offMag > 0.0 && offMag <= amp + Eps, $"offset {offMag} out of (0, {amp}]");
    }

    [Fact]
    public void Resolver_NoVaryStart_StartsOnConstant()
    {
        var p = new FractalParameters { JuliaC = new Complex(-0.7, 0.27015) };
        var a = ConstantDriftResolver.TryBuild(FractalType.Julia, p, 8.0, new Random(1), varyStart: false);
        Assert.NotNull(a);
        Assert.False(a!.HasStartOffset);
        Assert.Equal(p.JuliaC, a.StartValue);
    }
}
