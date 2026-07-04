using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using FracturingFog;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Models;
using FracturingFog.Render;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// Scene Engine Roadmap Phase S3: the camera track. Covers the spline evaluator
/// (clamp, Linear / Bezier / Catmull-Rom, literal angle interpolation,
/// pass-through-keys), the per-type param binding round-trip (every field a
/// CameraKey claims exists on FractalParameters and is a read/write double, for
/// exactly the 8 raymarch types), and the bus-ready animator (time advance,
/// loop wrap, enable gate).
/// </summary>
public sealed class CameraTrackTests
{
    private static CameraTrack Track(CameraInterpolation interp, params CameraKey[] keys)
    {
        var t = new CameraTrack { Interpolation = interp };
        foreach (var k in keys) t.Add(k);
        return t;
    }

    // ── Evaluate — structure ─────────────────────────────────────────────────

    [Fact]
    public void Evaluate_EmptyTrack_Throws()
    {
        var t = new CameraTrack();
        Assert.Throws<InvalidOperationException>(() => t.Evaluate(0.0));
    }

    [Fact]
    public void Evaluate_SingleKey_IsConstant()
    {
        var t = Track(CameraInterpolation.CatmullRom, new CameraKey(5.0, 3.0, 1.0, 0.5));
        Assert.Equal(new CameraState(3.0, 1.0, 0.5), t.Evaluate(-100));
        Assert.Equal(new CameraState(3.0, 1.0, 0.5), t.Evaluate(999));
    }

    [Fact]
    public void Evaluate_ClampsOutsideKeyRange()
    {
        var t = Track(CameraInterpolation.Linear,
            new CameraKey(0.0, 2.0, 0.0, 0.0),
            new CameraKey(10.0, 4.0, 0.0, 0.0));

        Assert.Equal(2.0, t.Evaluate(-1).Distance, precision: 9); // below first
        Assert.Equal(4.0, t.Evaluate(50).Distance, precision: 9); // above last
    }

    // ── Per-key easing (D.1) ─────────────────────────────────────────────────

    [Theory]
    [InlineData(CameraEase.None)]
    [InlineData(CameraEase.EaseIn)]
    [InlineData(CameraEase.EaseOut)]
    [InlineData(CameraEase.EaseInOut)]
    public void ApplyEase_FixesEndpoints(CameraEase ease)
    {
        Assert.Equal(0.0, CameraKey.ApplyEase(ease, 0.0), precision: 9);
        Assert.Equal(1.0, CameraKey.ApplyEase(ease, 1.0), precision: 9);
        // Out-of-range clamps to the endpoints.
        Assert.Equal(0.0, CameraKey.ApplyEase(ease, -0.5), precision: 9);
        Assert.Equal(1.0, CameraKey.ApplyEase(ease, 1.5), precision: 9);
    }

    [Fact]
    public void ApplyEase_ShapesTheMidpoint()
    {
        // EaseIn (u²) starts slow → below the linear 0.5 at the midpoint;
        // EaseOut is its mirror → above; EaseInOut passes through 0.5.
        Assert.Equal(0.25, CameraKey.ApplyEase(CameraEase.EaseIn, 0.5), precision: 9);
        Assert.Equal(0.75, CameraKey.ApplyEase(CameraEase.EaseOut, 0.5), precision: 9);
        Assert.Equal(0.5,  CameraKey.ApplyEase(CameraEase.EaseInOut, 0.5), precision: 9);
        Assert.Equal(0.5,  CameraKey.ApplyEase(CameraEase.None, 0.5), precision: 9);
    }

    [Fact]
    public void Evaluate_HonoursTheStartingKeysEase()
    {
        // Linear spatial path 2→4 over [0,10]; the start key eases-in, so the
        // pose at the time-midpoint lags the un-eased linear value (3.0).
        var eased = Track(CameraInterpolation.Linear,
            new CameraKey(0.0, 2.0, 0.0, 0.0) { Ease = CameraEase.EaseIn },
            new CameraKey(10.0, 4.0, 0.0, 0.0));
        // u=0.5 → eased 0.25 → distance = 2 + 0.25*(4-2) = 2.5.
        Assert.Equal(2.5, eased.Evaluate(5.0).Distance, precision: 9);

        // The default (None) is the plain linear midpoint 3.0 — unchanged from
        // pre-D.1 behaviour.
        var plain = Track(CameraInterpolation.Linear,
            new CameraKey(0.0, 2.0, 0.0, 0.0),
            new CameraKey(10.0, 4.0, 0.0, 0.0));
        Assert.Equal(3.0, plain.Evaluate(5.0).Distance, precision: 9);

        // Keys are always passed through exactly regardless of ease.
        Assert.Equal(2.0, eased.Evaluate(0.0).Distance, precision: 9);
        Assert.Equal(4.0, eased.Evaluate(10.0).Distance, precision: 9);
    }

    [Fact]
    public void Duration_IsLastKeyTime()
    {
        var t = Track(CameraInterpolation.Linear,
            new CameraKey(0.0, 2.0, 0.0, 0.0),
            new CameraKey(7.5, 4.0, 0.0, 0.0));
        Assert.Equal(7.5, t.Duration, precision: 9);
    }

    [Fact]
    public void Add_KeepsKeysAscending_RegardlessOfInsertOrder()
    {
        var t = Track(CameraInterpolation.Linear,
            new CameraKey(10.0, 4.0, 0.0, 0.0),
            new CameraKey(0.0, 2.0, 0.0, 0.0),
            new CameraKey(5.0, 3.0, 0.0, 0.0));

        Assert.Equal(new[] { 0.0, 5.0, 10.0 }, t.Keys.Select(k => k.Time));
    }

    // ── Evaluate — interpolation ─────────────────────────────────────────────

    [Fact]
    public void Linear_MidpointIsExactAverage()
    {
        var t = Track(CameraInterpolation.Linear,
            new CameraKey(0.0, 2.0, 0.0, 0.0),
            new CameraKey(10.0, 4.0, 0.0, 0.0));
        Assert.Equal(3.0, t.Evaluate(5.0).Distance, precision: 9);
    }

    [Fact]
    public void Angles_InterpolateLiterally_NotShortestPath()
    {
        // theta 0 -> 4π must pass through 2π at the midpoint (two full orbits),
        // not collapse to the shortest 0-distance path.
        var t = Track(CameraInterpolation.Linear,
            new CameraKey(0.0, new CameraState(3.0, 0.0, 0.0)),
            new CameraKey(10.0, new CameraState(3.0, 4.0 * Math.PI, 0.0)));
        Assert.Equal(2.0 * Math.PI, t.Evaluate(5.0).Theta, precision: 9);
    }

    [Fact]
    public void Bezier_EasesInFromKeys_BelowLinearOnRisingQuarter()
    {
        var lin = Track(CameraInterpolation.Linear,
            new CameraKey(0.0, 0.0, 0.0, 0.0),
            new CameraKey(4.0, 4.0, 0.0, 0.0));
        var bez = Track(CameraInterpolation.Bezier,
            new CameraKey(0.0, 0.0, 0.0, 0.0),
            new CameraKey(4.0, 4.0, 0.0, 0.0));

        // Quarter-way: smoothstep(0.25) = 0.15625 -> 0.625, vs linear 1.0.
        Assert.Equal(1.0, lin.Evaluate(1.0).Distance, precision: 9);
        Assert.Equal(0.625, bez.Evaluate(1.0).Distance, precision: 9);
        Assert.True(bez.Evaluate(1.0).Distance < lin.Evaluate(1.0).Distance);
    }

    [Fact]
    public void AllInterpolations_PassExactlyThroughKeys()
    {
        foreach (var interp in new[] { CameraInterpolation.Linear, CameraInterpolation.Bezier, CameraInterpolation.CatmullRom })
        {
            var t = Track(interp,
                new CameraKey(0.0, 2.0, 0.1, 0.0),
                new CameraKey(5.0, 9.0, 0.7, 0.3),   // interior key
                new CameraKey(10.0, 4.0, 0.2, 0.9));

            Assert.Equal(2.0, t.Evaluate(0.0).Distance, precision: 9);
            Assert.Equal(9.0, t.Evaluate(5.0).Distance, precision: 9); // through interior key
            Assert.Equal(0.7, t.Evaluate(5.0).Theta, precision: 9);
            Assert.Equal(4.0, t.Evaluate(10.0).Distance, precision: 9);
        }
    }

    // ── Per-type binding round-trip ──────────────────────────────────────────

    private static readonly FractalType[] ExpectedCameraTypes =
    {
        FractalType.Mandelbulb, FractalType.Mandelbox, FractalType.Kifs,
        FractalType.QuaternionJulia, FractalType.QuaternionMandelbrot,
        FractalType.Kleinian, FractalType.BicomplexMandelbrot, FractalType.UserBulb,
    };

    [Fact]
    public void SupportedTypes_AreExactlyTheEightRaymarchTypes()
    {
        Assert.Equal(
            ExpectedCameraTypes.OrderBy(x => x).ToArray(),
            CameraParamBinding.SupportedTypes.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void EveryClaimedField_ExistsOnFractalParameters_AsReadWriteDouble()
    {
        var pt = typeof(FractalParameters);
        foreach (var type in CameraParamBinding.SupportedTypes)
        {
            var (dist, theta, phi) = CameraParamBinding.ParamNames(type);
            foreach (var name in new[] { dist, theta, phi })
            {
                var prop = pt.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                Assert.NotNull(prop);
                Assert.Equal(typeof(double), prop!.PropertyType);
                Assert.True(prop.CanRead && prop.CanWrite, $"{name} must be read/write");
            }
        }
    }

    [Fact]
    public void ApplyThenRead_RoundTrips_ForEverySupportedType()
    {
        foreach (var type in CameraParamBinding.SupportedTypes)
        {
            var p = new FractalParameters();
            var state = new CameraState(7.25, 1.23, -0.45);
            CameraParamBinding.Apply(p, type, state);
            Assert.Equal(state, CameraParamBinding.Read(p, type));
        }
    }

    [Fact]
    public void UnsupportedType_IsRejected()
    {
        Assert.False(CameraParamBinding.Supports(FractalType.Mandelbrot));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CameraParamBinding.Apply(new FractalParameters(), FractalType.Mandelbrot, default));
    }

    // ── Bus-ready animator ───────────────────────────────────────────────────

    [Fact]
    public void Animator_Tick_AdvancesClock_AndWritesCameraParams()
    {
        var p = new FractalParameters();
        var track = Track(CameraInterpolation.Linear,
            new CameraKey(0.0, 2.0, 0.0, 0.0),
            new CameraKey(10.0, 4.0, 0.0, 0.0));
        var a = new CameraTrackAnimator(track, p, FractalType.Mandelbulb) { Loop = false };

        a.Tick(5.0);

        Assert.Equal(5.0, a.Time, precision: 9);
        Assert.Equal(3.0, p.BulbCameraDistance, precision: 9); // linear midpoint
    }

    [Fact]
    public void Animator_Loops_WrappingTimeIntoDuration()
    {
        var p = new FractalParameters();
        var track = Track(CameraInterpolation.Linear,
            new CameraKey(0.0, 2.0, 0.0, 0.0),
            new CameraKey(10.0, 4.0, 0.0, 0.0));
        var a = new CameraTrackAnimator(track, p, FractalType.Mandelbulb) { Loop = true };

        a.Tick(12.0); // past the 10 s duration
        Assert.Equal(2.0, a.Time, precision: 9); // wrapped to 2 s
    }

    [Fact]
    public void Animator_Disabled_DoesNotWrite()
    {
        var p = new FractalParameters();
        double before = p.BulbCameraDistance;
        var track = Track(CameraInterpolation.Linear,
            new CameraKey(0.0, 99.0, 0.0, 0.0),
            new CameraKey(10.0, 42.0, 0.0, 0.0));
        var a = new CameraTrackAnimator(track, p, FractalType.Mandelbulb) { IsEnabled = false };

        a.Tick(5.0);

        Assert.Equal(before, p.BulbCameraDistance, precision: 9);
        Assert.Equal(0.0, a.Time, precision: 9);
    }

    [Fact]
    public void Animator_RejectsNonCameraType()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CameraTrackAnimator(new CameraTrack(), new FractalParameters(), FractalType.Mandelbrot));
    }
}
