using System;
using System.Linq;
using System.Numerics;
using FracturingFog;
using FracturingFog.Abstractions.Animation;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

/// <summary>
/// Phase 3a deliverable: ProceduralAnimator math at known time points +
/// AnimationDataExtensions.ToAnimators reflection round-trip.
/// </summary>
public sealed class ProceduralAnimatorTests
{
    private static AnimationTrack MakeScalarTrack(
        AnimationMode mode, double min, double max, double hz = 1.0, double phase = 0.0)
        => new AnimationTrack
        {
            ParamName = "Test",
            Mode = mode,
            Min = min,
            Max = max,
            FrequencyHz = hz,
            PhaseOffsetRadians = phase,
            Enabled = true,
        };

    [Fact]
    public void Sine_Mode_StartsAtMidpoint_AndPeaksAtQuarterPeriod()
    {
        double captured = double.NaN;
        var track = MakeScalarTrack(AnimationMode.Sine, 0.0, 10.0, hz: 1.0);
        var anim = new DoubleProceduralAnimator(track, v => captured = v) { IsEnabled = true };

        // FrequencyHz = 1 → period 1 s. At t=0 (no tick yet) Phase=0
        // → sin(0) = 0 → 0.5 + 0.5*0 = 0.5 midpoint = 5.0.
        anim.Tick(0.0);
        Assert.Equal(5.0, captured, 6);

        // Advance to π/2 phase → quarter period → peak at Max.
        anim.Tick(0.25);
        Assert.Equal(10.0, captured, 6);

        // Half period → back to midpoint.
        anim.Tick(0.25);
        Assert.Equal(5.0, captured, 6);

        // Three-quarter → Min.
        anim.Tick(0.25);
        Assert.Equal(0.0, captured, 6);
    }

    [Fact]
    public void Triangle_Mode_LinearRamp_PeakAtHalfPeriod()
    {
        double captured = double.NaN;
        var track = MakeScalarTrack(AnimationMode.Triangle, 2.0, 6.0, hz: 1.0);
        var anim = new DoubleProceduralAnimator(track, v => captured = v) { IsEnabled = true };

        anim.Tick(0.0);   // Phase = 0 → triangle = 0 → Min
        Assert.Equal(2.0, captured, 6);

        anim.Tick(0.25);  // quarter period → triangle = 0.5 → mid
        Assert.Equal(4.0, captured, 6);

        anim.Tick(0.25);  // half period → triangle = 1 → Max
        Assert.Equal(6.0, captured, 6);

        anim.Tick(0.5);   // full period → wraps to Min
        Assert.Equal(2.0, captured, 6);
    }

    [Fact]
    public void Linear_Mode_Sawtooth_WrapsAtPeriod()
    {
        double captured = double.NaN;
        var track = MakeScalarTrack(AnimationMode.Linear, 0.0, 1.0, hz: 1.0);
        var anim = new DoubleProceduralAnimator(track, v => captured = v) { IsEnabled = true };

        anim.Tick(0.0);
        Assert.Equal(0.0, captured, 6);

        anim.Tick(0.5);
        Assert.Equal(0.5, captured, 6);

        anim.Tick(0.499);
        Assert.True(captured > 0.99 && captured < 1.0,
            $"expected close to 1 before wrap, got {captured}");

        // Cross the wrap.
        anim.Tick(0.002);
        Assert.True(captured < 0.01, $"expected wrap near 0, got {captured}");
    }

    [Fact]
    public void Hold_Mode_StaysAtMin()
    {
        double captured = double.NaN;
        var track = MakeScalarTrack(AnimationMode.Hold, 3.0, 8.0, hz: 10.0);
        var anim = new DoubleProceduralAnimator(track, v => captured = v) { IsEnabled = true };

        for (int i = 0; i < 20; i++)
        {
            anim.Tick(0.05);
            Assert.Equal(3.0, captured, 6);
        }
    }

    [Fact]
    public void IntAnimator_RoundsToNearestInteger()
    {
        int captured = -1;
        var track = MakeScalarTrack(AnimationMode.Sine, 2.0, 8.0, hz: 1.0);
        var anim = new IntProceduralAnimator(track, v => captured = v) { IsEnabled = true };

        anim.Tick(0.0);
        Assert.Equal(5, captured); // midpoint of [2,8] = 5

        anim.Tick(0.25);
        Assert.Equal(8, captured); // peak

        anim.Tick(0.25);
        Assert.Equal(5, captured);

        anim.Tick(0.25);
        Assert.Equal(2, captured); // trough
    }

    [Fact]
    public void Complex_Lissajous_FixedRadius_TracesCircle()
    {
        Complex captured = Complex.Zero;
        var track = MakeScalarTrack(AnimationMode.Lissajous, 0.5, 0.5, hz: 1.0);
        var anim = new ComplexProceduralAnimator(track, c => captured = c) { IsEnabled = true };

        anim.Tick(0.0);
        Assert.Equal(0.5, captured.Real, 6);
        Assert.Equal(0.0, captured.Imaginary, 6);

        anim.Tick(0.25); // quarter period → 90°
        Assert.Equal(0.0, captured.Real, 6);
        Assert.Equal(0.5, captured.Imaginary, 6);

        anim.Tick(0.25); // 180°
        Assert.Equal(-0.5, captured.Real, 6);
        Assert.Equal(0.0, captured.Imaginary, 6);

        anim.Tick(0.25); // 270°
        Assert.Equal(0.0, captured.Real, 6);
        Assert.Equal(-0.5, captured.Imaginary, 6);
    }

    [Fact]
    public void Disabled_Animator_DoesNotAdvance()
    {
        double captured = -42.0;
        var track = MakeScalarTrack(AnimationMode.Sine, 0.0, 10.0, hz: 1.0);
        var anim = new DoubleProceduralAnimator(track, v => captured = v) { IsEnabled = false };

        for (int i = 0; i < 10; i++)
            anim.Tick(0.1);

        Assert.Equal(-42.0, captured); // setter never called
    }

    [Fact]
    public void PhaseOffset_ShiftsStartPosition()
    {
        double captured = double.NaN;
        // Sine at offset = π/2 → at t=0 the wave is already at peak.
        var track = MakeScalarTrack(AnimationMode.Sine, 0.0, 10.0, hz: 1.0, phase: Math.PI / 2.0);
        var anim = new DoubleProceduralAnimator(track, v => captured = v) { IsEnabled = true };

        anim.Tick(0.0);
        Assert.Equal(10.0, captured, 6);
    }

    [Fact]
    public void ToAnimators_ResolvesValidTracks_AndSkipsUnknown()
    {
        var fp = new FractalParameters();

        var data = new AnimationData
        {
            Name = "mixed",
            TargetFractalTypes = new System.Collections.Generic.List<FractalType>(),
            Tracks = new System.Collections.Generic.List<AnimationTrack>
            {
                new AnimationTrack { ParamName = "JuliaC", Mode = AnimationMode.Lissajous, Min = 0.3, Max = 0.3, FrequencyHz = 1.0 },
                new AnimationTrack { ParamName = "MultibrotExponent", Mode = AnimationMode.Sine, Min = 2, Max = 8, FrequencyHz = 1.0 },
                new AnimationTrack { ParamName = "BulbPower", Mode = AnimationMode.Sine, Min = 2.0, Max = 8.0, FrequencyHz = 1.0 },
                new AnimationTrack { ParamName = "NoSuchProperty", Mode = AnimationMode.Sine, Min = 0, Max = 1, FrequencyHz = 1.0 },
            },
        };

        var animators = data.ToAnimators(fp).ToList();

        Assert.Equal(3, animators.Count); // unknown track silently dropped
        Assert.IsType<ComplexProceduralAnimator>(animators[0]);
        Assert.IsType<IntProceduralAnimator>(animators[1]);
        Assert.IsType<DoubleProceduralAnimator>(animators[2]);
    }

    [Fact]
    public void ToAnimators_WritesIntoTargetFractalParameters()
    {
        var fp = new FractalParameters();
        var data = new AnimationData
        {
            Name = "writes",
            Tracks = new System.Collections.Generic.List<AnimationTrack>
            {
                new AnimationTrack { ParamName = "BulbPower", Mode = AnimationMode.Hold, Min = 5.5, Max = 5.5, FrequencyHz = 1.0 },
            },
        };

        foreach (var a in data.ToAnimators(fp))
            a.Tick(0.0);

        Assert.Equal(5.5, fp.BulbPower, 6);
    }
}
