// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Linq;
using System.Numerics;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1119 (epic #1114 S5) — co-moving orbit trap. Oracles: a test-side lockstep
// loop with the textbook frame w = (m − f)·conj(f)/|f| [/|f|]; the c = 0
// degeneracy (c-orbit ≡ z-orbit ⇒ w ≡ 0 ⇒ the shape's value at the origin);
// rotation covariance against an independent shape (Cross turned 45° is
// DiagonalCross); |c − z| = |z − c| (unrotated point trap: TrapZ ≡ TrapC); and
// the Fixed frame reproducing S3 (α = 0 byte-identical, α = 360° through the
// lockstep path).
public sealed class DualOrbitCoMovingTrapTests
{
    private const int Px = 3;
    private const double Bail = 128.0;

    private sealed class RampMap : IColorMap
    {
        public ColorPaletteType Type => ColorPaletteType.GradientLinear;
        public int MaxIterations { get; set; }
        public uint InSetColor => 0xFF102030u;
        public int Map(float smooth, float distance, int iterations)
            => unchecked((int)(0xFF000000u | ((uint)(smooth * 997f) & 0x00FFFFFFu)));
    }

    private static FractalParameters P(DualOrbitField f, OrbitTrapShapeDef shape, Complex c,
        Action<FractalParameters>? tweak = null)
    {
        var p = new FractalParameters
        {
            DualOrbitField = f, DualOrbitTrapShape = shape, DualOrbitBailout = Bail,
            DualOrbitCSeedX = c.Real, DualOrbitCSeedY = c.Imaginary,
        };
        tweak?.Invoke(p);
        return p;
    }

    private static DualOrbitEscapeCalculator Render(FractalParameters p, int size, double cx, double cy, double zoom, int maxIter = 300)
    {
        var calc = new DualOrbitEscapeCalculator(size, size)
        {
            CenterX = cx, CenterY = cy, Zoom = zoom, MaxIterations = maxIter,
            FractalParameters = p, ColorMap = new RampMap(),
        };
        calc.Calculate();
        return calc;
    }

    private static float PointTrapBuffer(Complex s, Complex c, DualOrbitField f, OrbitTrapShapeDef shape,
        Action<FractalParameters>? tweak = null)
        => Render(P(f, shape, c, tweak), 2, s.Real, s.Imaginary, 1e9).TrapBuffer[Px];

    // Test-side lockstep: min over k ≥ 2 (both orbits bounded) of sdf(w_k).
    // k = 1 is skipped: c_1 − z_1 = c_0² whatever s is.
    private static double Oracle(Complex s, Complex c0, bool measureC, bool rotate, bool scale, double angleDeg,
        Func<Complex, double> sdf, int maxIter = 300)
    {
        Complex z = 0, c = c0;
        double best = double.MaxValue;
        var turn = Complex.FromPolarCoordinates(1, -angleDeg * Math.PI / 180);
        for (int k = 0; k < maxIter; k++)
        {
            if (z.Magnitude > Bail || c.Magnitude > Bail) break;
            if (k > 1)
            {
                Complex m = measureC ? c : z, fr = measureC ? z : c;
                Complex w = m - fr;
                double r = fr.Magnitude;
                if (r > 0 && rotate) w *= Complex.Conjugate(fr) / r;
                if (r > 0 && scale) w /= r;
                best = Math.Min(best, sdf(w * turn));
            }
            z = z * z + s; c = c * c + s;
        }
        return best;
    }

    private static readonly (Complex s, Complex c)[] Cases =
    {
        (new(-0.12, 0.74), new(0.3, 0.1)),     // s in M, c-orbit escapes
        (new(-0.75, 0.05), new(0.05, -0.02)),  // near the 2-bulb neck
        (new(0.28, 0.01), new(-0.2, 0.3)),     // just outside the cardioid
        (new(-1.3, 0.0), new(0.4, 0.0)),       // real axis, 2-bulb
    };

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CoMovingPointAndCross_MatchTheTestSideLoop(bool rotate, bool scale)
    {
        foreach (var (s, c) in Cases)
        {
            void T(FractalParameters p) { p.DualOrbitTrapFrame = DualOrbitTrapFrame.CoMoving; p.DualOrbitTrapRotate = rotate; p.DualOrbitTrapScaleByOrbit = scale; }
            double pt = Oracle(s, c, true, rotate, scale, 0, w => w.Magnitude);
            Assert.Equal(pt, PointTrapBuffer(s, c, DualOrbitField.TrapC, OrbitTrapShapeDef.Point, T), pt * 1e-5 + 1e-6);
            double cross = Oracle(s, c, true, rotate, scale, 0, w => Math.Min(Math.Abs(w.Real), Math.Abs(w.Imaginary)));
            Assert.Equal(cross, PointTrapBuffer(s, c, DualOrbitField.TrapC, OrbitTrapShapeDef.Cross, T), cross * 1e-5 + 1e-6);
            // TrapZ: the z-orbit measured in the c-orbit's frame.
            double crossZ = Oracle(s, c, false, rotate, scale, 0, w => Math.Min(Math.Abs(w.Real), Math.Abs(w.Imaginary)));
            Assert.Equal(crossZ, PointTrapBuffer(s, c, DualOrbitField.TrapZ, OrbitTrapShapeDef.Cross, T), crossZ * 1e-5 + 1e-6);
        }
    }

    [Fact]
    public void TrapAngle_MatchesTheTestSideLoop_InBothFrames()
    {
        foreach (var (s, c) in Cases)
            foreach (var frame in new[] { DualOrbitTrapFrame.Fixed, DualOrbitTrapFrame.CoMoving })
            {
                bool co = frame == DualOrbitTrapFrame.CoMoving;
                // Fixed frame: f ≡ 0, sampled while the measured orbit is bounded.
                double want = co
                    ? Oracle(s, c, true, true, false, 25, w => Math.Min(Math.Abs(w.Real), Math.Abs(w.Imaginary)))
                    : FixedOracle(s, c, 25);
                float got = PointTrapBuffer(s, c, DualOrbitField.TrapC, OrbitTrapShapeDef.Cross,
                    p => { p.DualOrbitTrapFrame = frame; p.DualOrbitTrapAngle = 25; });
                Assert.Equal(want, got, want * 1e-5 + 1e-6);
            }

        static double FixedOracle(Complex s, Complex u, double angleDeg)
        {
            var turn = Complex.FromPolarCoordinates(1, -angleDeg * Math.PI / 180);
            double best = double.MaxValue;
            for (int k = 0; k < 300; k++)
            {
                if (u.Magnitude > Bail) break;
                if (k > 0) { var w = u * turn; best = Math.Min(best, Math.Min(Math.Abs(w.Real), Math.Abs(w.Imaginary))); }
                u = u * u + s;
            }
            return best;
        }
    }

    // c = 0: the c-orbit IS the z-orbit, so every w_k = 0 and the trap field is
    // the shape's own value at the origin — one constant over the whole image.
    [Theory]
    [InlineData(OrbitTrapShapeDef.Point)]
    [InlineData(OrbitTrapShapeDef.Circle)]
    [InlineData(OrbitTrapShapeDef.Ring)]
    [InlineData(OrbitTrapShapeDef.Square)]
    [InlineData(OrbitTrapShapeDef.Hexagon)]
    [InlineData(OrbitTrapShapeDef.Heart)]
    public void ZeroCSeed_CoMovingTrap_IsTheShapeAtTheOrigin_Everywhere(OrbitTrapShapeDef shape)
    {
        var calc = Render(P(DualOrbitField.TrapC, shape, Complex.Zero, p =>
        {
            p.DualOrbitTrapFrame = DualOrbitTrapFrame.CoMoving; p.DualOrbitTrapScaleByOrbit = true;
            p.DualOrbitBailout = 1e6;   // every pixel samples at least k = 1
        }), 48, -0.5, 0, 1.0);
        IOrbitAwareColorMap sampler = shape switch
        {
            OrbitTrapShapeDef.Point => new OrbitTrapPointMap(), OrbitTrapShapeDef.Circle => new OrbitTrapCircleMap(),
            OrbitTrapShapeDef.Ring => new OrbitTrapRingMap(), OrbitTrapShapeDef.Square => new OrbitTrapSquareMap(),
            OrbitTrapShapeDef.Hexagon => new OrbitTrapHexagonMap(), _ => new OrbitTrapHeartMap(),
        };
        sampler.InitOrbit(out var acc);
        sampler.Sample(ref acc, 0, 0, 0.1, 0.1, 1);
        Assert.All(calc.TrapBuffer, t => Assert.Equal(acc.TrapMin, t));
        Assert.Single(calc.ColorBuffer.Distinct());
    }

    // Rotation covariance: turning the Cross by 45° in the frame gives the
    // DiagonalCross shape (an independent sampler), in either frame.
    [Theory]
    [InlineData(DualOrbitTrapFrame.Fixed, false)]
    [InlineData(DualOrbitTrapFrame.CoMoving, false)]
    [InlineData(DualOrbitTrapFrame.CoMoving, true)]
    public void CrossTurned45_IsDiagonalCross(DualOrbitTrapFrame frame, bool scale)
    {
        void Common(FractalParameters p) { p.DualOrbitTrapFrame = frame; p.DualOrbitTrapScaleByOrbit = scale; }
        var turned = Render(P(DualOrbitField.TrapDelta, OrbitTrapShapeDef.Cross, new(0.3, 0.2), p => { Common(p); p.DualOrbitTrapAngle = 45; }), 40, -0.5, 0, 1.0);
        var diag = Render(P(DualOrbitField.TrapDelta, OrbitTrapShapeDef.DiagonalCross, new(0.3, 0.2), Common), 40, -0.5, 0, 1.0);
        var (a, b) = (turned.TrapBuffer, diag.TrapBuffer);
        for (int i = 0; i < a.Length; i++) Assert.Equal(b[i], a[i], 1e-5 + 1e-5 * b[i]);
        Assert.Contains(a, v => v > 1e-3);
    }

    // Unrotated, unscaled point trap: |c − z| = |z − c|, so TrapZ ≡ TrapC.
    [Fact]
    public void UnrotatedPointTrap_IsSymmetricInTheTwoOrbits()
    {
        void T(FractalParameters p) { p.DualOrbitTrapFrame = DualOrbitTrapFrame.CoMoving; p.DualOrbitTrapRotate = false; }
        var z = Render(P(DualOrbitField.TrapZ, OrbitTrapShapeDef.Point, new(0.4, -0.3), T), 40, -0.5, 0, 1.0);
        var c = Render(P(DualOrbitField.TrapC, OrbitTrapShapeDef.Point, new(0.4, -0.3), T), 40, -0.5, 0, 1.0);
        Assert.Equal(c.TrapBuffer, z.TrapBuffer);
        // …and rotation does not change a point trap's distance.
        var rot = Render(P(DualOrbitField.TrapC, OrbitTrapShapeDef.Point, new(0.4, -0.3), p => p.DualOrbitTrapFrame = DualOrbitTrapFrame.CoMoving), 40, -0.5, 0, 1.0);
        for (int i = 0; i < rot.TrapBuffer.Length; i++) Assert.Equal(c.TrapBuffer[i], rot.TrapBuffer[i], 1e-6 + 1e-6 * c.TrapBuffer[i]);
    }

    // Fixed frame: α = 0 is the S3 path untouched (rotate / scale toggles are
    // co-moving only); α = 360° runs the lockstep path and reproduces it.
    [Theory]
    [InlineData(DualOrbitField.TrapZ)]
    [InlineData(DualOrbitField.TrapC)]
    [InlineData(DualOrbitField.TrapDelta)]
    public void FixedFrame_ReproducesS3(DualOrbitField f)
    {
        var s3 = Render(P(f, OrbitTrapShapeDef.Cross, new(0.3, 0.2)), 40, -0.5, 0, 1.0);
        var toggled = Render(P(f, OrbitTrapShapeDef.Cross, new(0.3, 0.2), p => { p.DualOrbitTrapRotate = false; p.DualOrbitTrapScaleByOrbit = true; }), 40, -0.5, 0, 1.0);
        Assert.Equal(s3.ColorBuffer, toggled.ColorBuffer);
        Assert.Equal(s3.TrapBuffer, toggled.TrapBuffer);
        var full = Render(P(f, OrbitTrapShapeDef.Cross, new(0.3, 0.2), p => p.DualOrbitTrapAngle = 360), 40, -0.5, 0, 1.0);
        for (int i = 0; i < s3.TrapBuffer.Length; i++) Assert.Equal(s3.TrapBuffer[i], full.TrapBuffer[i], 1e-6 + 1e-6 * s3.TrapBuffer[i]);
    }

    [Fact]
    public void CoMovingFrame_ChangesTheImage()
    {
        var s3 = Render(P(DualOrbitField.TrapC, OrbitTrapShapeDef.Cross, new(0.3, 0.2)), 40, -0.5, 0, 1.0);
        var co = Render(P(DualOrbitField.TrapC, OrbitTrapShapeDef.Cross, new(0.3, 0.2), p => p.DualOrbitTrapFrame = DualOrbitTrapFrame.CoMoving), 40, -0.5, 0, 1.0);
        Assert.NotEqual(s3.ColorBuffer, co.ColorBuffer);
    }

    // c_1 − z_1 = c_0² for every s: sampling k = 1 would put the real point c_0²
    // (Cross distance 0) into every pixel's minimum for a real c-seed, a flat
    // image. Co-moving sampling starts at k = 2.
    [Fact]
    public void RealCSeed_UnrotatedCross_IsNotStampedByTheSeedStep()
    {
        var calc = Render(P(DualOrbitField.TrapC, OrbitTrapShapeDef.Cross, new(0.5, 0), p =>
        {
            p.DualOrbitTrapFrame = DualOrbitTrapFrame.CoMoving; p.DualOrbitTrapRotate = false;
        }), 40, -0.5, 0, 1.0);
        Assert.True(calc.TrapBuffer.Count(t => t > 1e-4) > calc.TrapBuffer.Length / 4);
    }

    [Fact]
    public void TrapAngle_IsAnimatable()
    {
        var names = FracturingFog.Abstractions.Animation.FractalAnimatableParamsMap.For(FractalType.JulibrotPair).Select(d => d.ParamName);
        Assert.Contains(nameof(FractalParameters.DualOrbitTrapAngle), names);
    }

    [Fact]
    public void LiveView_RoundTripsThroughTheCommandBuilder()
    {
        var p = new FractalParameters
        {
            DualOrbitField = DualOrbitField.TrapC, DualOrbitTrapFrame = DualOrbitTrapFrame.CoMoving,
            DualOrbitTrapRotate = false, DualOrbitTrapScaleByOrbit = true, DualOrbitTrapAngle = 33.5,
        };
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.JulibrotPair, Parameters = p,
            FamilyParams = RegionFractalParams.Snapshot(FractalType.JulibrotPair, p)!.ToKeyValues(),
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
        var fresh = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.JulibrotPair, out _)!.ApplyTo(fresh);
        Assert.Equal(DualOrbitTrapFrame.CoMoving, fresh.DualOrbitTrapFrame);
        Assert.False(fresh.DualOrbitTrapRotate);
        Assert.True(fresh.DualOrbitTrapScaleByOrbit);
        Assert.Equal(33.5, fresh.DualOrbitTrapAngle);
    }
}
