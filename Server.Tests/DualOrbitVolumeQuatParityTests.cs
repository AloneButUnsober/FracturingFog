// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Linq;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Interefaces;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

// #1129 (epic #1114 S15) — volume colour sources + quaternion parity.
// Oracles: slice agreement (the per-point evaluator and the volume's surface
// value equal what the 2D calculator's pixel pass stores for the same (c, s),
// #971 precedent); quaternion ≡ complex when every seed lies in the complex
// subalgebra C_i (s = s_x·i, c = c_x·i ↔ complex s = i·s_x, c = i·c_x), with
// every DualOrbitField classified (agrees / no quaternion value); rotation
// covariance of the Hamilton map (rotating s and c about the i-axis leaves every
// norm-built field unchanged — "radially trivial").
public sealed class DualOrbitVolumeQuatParityTests
{
    private const uint Interior = 0xFF102030u;

    private sealed class RampMap : IColorMap
    {
        public ColorPaletteType Type => ColorPaletteType.GradientLinear;
        public int MaxIterations { get; set; }
        public uint InSetColor => Interior;
        public int Map(float smooth, float distance, int iterations)
            => unchecked((int)(0xFF000000u | ((uint)(smooth * 997f) & 0x00FFFFFFu)));
    }

    private static DualOrbitEscapeCalculator Point(FractalParameters p, double x, double y, int maxIter = 300)
    {
        var c = new DualOrbitEscapeCalculator(2, 2)
        {
            CenterX = x, CenterY = y, Zoom = 1e9, MaxIterations = maxIter,
            FractalParameters = p, ColorMap = new RampMap(),
        };
        c.Calculate();
        return c;
    }

    // ---- slice agreement -------------------------------------------------

    [Theory]
    [InlineData(DualOrbitField.SecantLyapunov)]
    [InlineData(DualOrbitField.PairWinding)]
    [InlineData(DualOrbitField.DivergenceTime)]
    [InlineData(DualOrbitField.ClosestApproach)]
    [InlineData(DualOrbitField.PhaseLag)]
    [InlineData(DualOrbitField.CyclePeriod)]
    public void TryFieldAt_EqualsThe2DPixel_OnAGrid(DualOrbitField f)
    {
        const double sx = -0.122561, sy = 0.744862;   // the rabbit (superattracting 3-cycle): interior fields live too
        var p = new FractalParameters { DualOrbitField = f, DualOrbitSliceAxes = DualOrbitSliceAxes.CxCy, DualOrbitSX = sx, DualOrbitSY = sy };
        var calc = new DualOrbitEscapeCalculator(24, 18) { CenterX = 0, CenterY = 0, Zoom = 1.3, MaxIterations = 300, FractalParameters = p, ColorMap = new RampMap() };
        calc.Calculate();
        double pitch = 4.0 / 24 / 1.3;
        int live = 0;
        for (int y = 0; y < 18; y++)
            for (int x = 0; x < 24; x++)
            {
                double cx = (x - 12) * pitch, cy = (y - 9) * pitch;
                bool ok = DualOrbitEscapeCalculator.TryFieldAt(f, cx, cy, sx, sy, 300, p.DualOrbitBailout,
                    p.DualOrbitLyapunovSpan, p.DualOrbitDivergenceRatio, out double v);
                float stored = calc.ColorScalarBuffer[y * 24 + x];
                Assert.Equal(ok ? (float)v : 0f, stored);
                if (ok) live++;
            }
        Assert.True(live > 20, $"{f}: only {live} live points");
    }

    [Theory]
    [InlineData(DualOrbitVolumeColor.SecantLyapunov, DualOrbitField.SecantLyapunov)]
    [InlineData(DualOrbitVolumeColor.PairWinding, DualOrbitField.PairWinding)]
    public void VolumeSurfaceValue_IsThe2DFieldAtTheSamePoint(DualOrbitVolumeColor src, DualOrbitField f)
    {
        var fp = new FractalParameters();
        var rng = new Random(7);
        for (int k = 0; k < 40; k++)
        {
            double px = rng.NextDouble() * 3 - 1.5, py = rng.NextDouble() * 1.2 - 0.6, pz = rng.NextDouble() * 3 - 1.5;
            double v = DualOrbitVolumeCalculator.SurfaceFieldValue(src, px, py, pz, 0, 1, 0, 1e-3, fp, out _);
            // The 2D calculator, Julia plane at s = (py + sxCenter) + i·sy, pixel c = px + i·pz.
            var q = new FractalParameters { DualOrbitField = f, DualOrbitSliceAxes = DualOrbitSliceAxes.CxCy,
                DualOrbitSX = py + fp.DualOrbitVolumeSXCenter, DualOrbitSY = fp.DualOrbitVolumeSY };
            float s = Point(q, px, pz, DualOrbitVolumeCalculator.SurfaceFieldIterations).ColorScalarBuffer[3];
            int it = DualOrbitVolumeCalculator.SurfaceFieldIterations;
            double want = s == 0 ? double.NaN
                : f == DualOrbitField.SecantLyapunov ? s / it * 255.0
                : Math.Clamp(128.0 + 16.0 * (s - 0.5 * it), 0, 255);
            if (double.IsNaN(want)) Assert.True(double.IsNaN(v));
            else Assert.Equal(want, v, 1e-3);
        }
    }

    // PhaseLag lives where both orbits are bounded: just INSIDE the surface. At a
    // dead hit point the volume probes inward along −normal (2ε … 256ε) and uses
    // the first live class — checked against the 2D field at that probe point.
    [Fact]
    public void VolumePhaseLag_ProbesInward_AndUsesTheClassColour()
    {
        var fp = new FractalParameters { DualOrbitVolumeSXCenter = -0.122561, DualOrbitVolumeSY = 0.744862 };   // layer y = 0: rabbit
        const double eps = 1e-3;
        var rng = new Random(3);
        int probed = 0;
        for (int t = 0; t < 400 && probed < 5; t++)
        {
            double ang = rng.NextDouble() * 2 * Math.PI, r = 0.2 + rng.NextDouble() * 1.2;
            double px = r * Math.Cos(ang), pz = r * Math.Sin(ang);
            double nx = Math.Cos(ang), nz = Math.Sin(ang);   // outward-ish normal (radial)
            bool At(double x, double z, out double k) => DualOrbitEscapeCalculator.TryFieldAt(DualOrbitField.PhaseLag, x, z,
                fp.DualOrbitVolumeSXCenter, fp.DualOrbitVolumeSY, DualOrbitVolumeCalculator.SurfaceFieldIterations,
                fp.DualOrbitBailout, fp.DualOrbitLyapunovSpan, fp.DualOrbitDivergenceRatio, out k);
            if (At(px, pz, out _)) continue;                 // want a dead hit point
            double kIn = double.NaN;
            for (double k = 2; k <= 256 && double.IsNaN(kIn); k *= 2)
                if (At(px - nx * eps * k, pz - nz * eps * k, out double kk)) kIn = kk;
            if (double.IsNaN(kIn)) continue;                 // no interior within reach
            double v = DualOrbitVolumeCalculator.SurfaceFieldValue(DualOrbitVolumeColor.PhaseLag, px, 0, pz, nx, 0, nz, eps, fp, out uint? cc);
            Assert.False(double.IsNaN(v));
            Assert.Equal(DualOrbitEscapeCalculator.ClassColor(DualOrbitField.PhaseLag, kIn), cc);
            probed++;
        }
        Assert.True(probed >= 3, $"only {probed} dead hit points with interior in reach");
    }

    [Theory]
    [InlineData(DualOrbitVolumeColor.SecantLyapunov)]
    [InlineData(DualOrbitVolumeColor.PhaseLag)]
    [InlineData(DualOrbitVolumeColor.PairWinding)]
    public void Volume_RendersEachNewSource_Deterministically(DualOrbitVolumeColor src)
    {
        uint[] Render()
        {
            var calc = new DualOrbitVolumeCalculator(64, 48) { FractalParameters = new FractalParameters { DualOrbitVolumeColor = src } };
            calc.Calculate();
            return calc.ColorBuffer;
        }
        var a = Render();
        Assert.Equal(a, Render());
        Assert.True(a.Distinct().Count() > 20);
        var steps = new DualOrbitVolumeCalculator(64, 48) { FractalParameters = new FractalParameters { DualOrbitVolumeColor = DualOrbitVolumeColor.Steps } };
        steps.Calculate();
        Assert.NotEqual(steps.ColorBuffer, a);
    }

    // ---- quaternion parity ----------------------------------------------

    // Fields with no quaternion value (interior colour under the quaternion map):
    // planar angles / orientation (Böttcher angle, winding, itinerary sign),
    // derivative / image-plane fields, per-orbit sampling, ensembles, Jacobian.
    private static readonly HashSet<DualOrbitField> NoQuaternionValue = new()
    {
        DualOrbitField.ExternalAngleDelta, DualOrbitField.PairWinding, DualOrbitField.ItineraryAgreement,
        DualOrbitField.PhaseLag, DualOrbitField.PhaseLagFraction, DualOrbitField.CyclePeriod,
        DualOrbitField.TrapZ, DualOrbitField.TrapC, DualOrbitField.TrapDelta,
        DualOrbitField.StripeZ, DualOrbitField.StripeC, DualOrbitField.StripeInterference,
        DualOrbitField.TiaZ, DualOrbitField.TiaC, DualOrbitField.OrbitThemeZ, DualOrbitField.OrbitThemeC,
        DualOrbitField.DistanceZ, DualOrbitField.DistanceC, DualOrbitField.DualOutline, DualOrbitField.BinaryXor,
        DualOrbitField.FinalAngleDelta, DualOrbitField.PathInterference, DualOrbitField.PathInterferencePhase,
        DualOrbitField.BasinEntropy, DualOrbitField.UncertaintyExponent, DualOrbitField.Ftle, DualOrbitField.JacobianAnisotropy,
    };

    private static (float v, uint col) Quat(DualOrbitField f, double s1, double s2, double s3, double c1, double c2, double c3)
    {
        var p = new FractalParameters { DualOrbitField = f, DualOrbitMap = DualOrbitMap.Quaternion,
            DualOrbitSZ = s3, DualOrbitCSeedX = c1, DualOrbitCSeedY = c2, DualOrbitCSeedZ = c3 };
        var c = Point(p, s1, s2);
        return (c.ColorScalarBuffer[3], c.ColorBuffer[3]);
    }

    private static readonly (double s, double c)[] SubalgebraPoints =
    {
        (0.4, 0.3), (0.7, -0.5), (-0.3, 0.2), (1.1, 0.6), (0.62, 0.1), (-0.5, -0.45),
    };

    [Fact]
    public void EveryField_IsClassified_ForTheQuaternionMap()
    {
        foreach (DualOrbitField f in Enum.GetValues(typeof(DualOrbitField)))
            foreach (var (s, c) in SubalgebraPoints)
            {
                var q = Quat(f, s, 0, 0, c, 0, 0);
                var z = Point(new FractalParameters { DualOrbitField = f, DualOrbitCSeedX = 0, DualOrbitCSeedY = c }, 0, s);
                if (NoQuaternionValue.Contains(f))
                {
                    Assert.Equal(Interior, q.col);
                    continue;
                }
                // In C_i the quaternion map IS the complex map: same value, same liveness.
                Assert.Equal(z.ColorBuffer[3] == Interior, q.col == Interior);
                float zv = z.ColorScalarBuffer[3];
                Assert.True(Math.Abs(q.v - zv) <= 1e-3 * (1 + Math.Abs(zv)), $"{f} at s = {s}i, c = {c}i: quaternion {q.v} vs complex {zv}");
            }
    }

    // The explicit-D secant sum: for a bounded pair in C_i both orbits share the
    // attracting fixed point, and λ = ln|μ| (μ = 2α) — not the round-off floor
    // the differenced c_N − z_N used to give.
    [Fact]
    public void QuaternionSecantLyapunov_ReachesLnMultiplier_NotTheRoundOffFloor()
    {
        const double s = -0.3, c = 0.2;   // s = −0.3i: main cardioid
        var p = new FractalParameters { DualOrbitField = DualOrbitField.SecantLyapunov, DualOrbitMap = DualOrbitMap.Quaternion,
            DualOrbitSZ = 0, DualOrbitCSeedX = c, DualOrbitCSeedY = 0, DualOrbitCSeedZ = 0, DualOrbitLyapunovSpan = 5 };
        var calc = Point(p, s, 0, 2000);
        var pl = calc.PairPlanes!;
        double lambda = pl.SecantLogSum[3] / pl.PairSteps[3];
        var sc = new System.Numerics.Complex(0, s);
        double mu = (1 - System.Numerics.Complex.Sqrt(1 - 4 * sc)).Magnitude;   // |2α|
        Assert.Equal(Math.Log(mu), lambda, 3e-3);   // O(1/N) residue
    }

    // Rotation covariance: conjugating s and c by a rotation about the i-axis
    // rotates every orbit, so every norm-built field is unchanged.
    [Theory]
    [InlineData(DualOrbitField.SecantLyapunov)]
    [InlineData(DualOrbitField.GreenRatio)]
    [InlineData(DualOrbitField.EscapeSeparation)]
    [InlineData(DualOrbitField.ClosestApproach)]
    [InlineData(DualOrbitField.MidpointPerturbation)]
    public void QuaternionFields_AreRotationCovariant(DualOrbitField f)
    {
        foreach (var phi in new[] { 0.7, 2.1 })
        {
            double cs = Math.Cos(phi), sn = Math.Sin(phi);
            var a = Quat(f, 0.3, 0.5, 0.0, 0.2, -0.4, 0.0);
            var b = Quat(f, 0.3, 0.5 * cs, 0.5 * sn, 0.2, -0.4 * cs, -0.4 * sn);
            Assert.True(Math.Abs(a.v - b.v) <= 1e-3 * (1 + Math.Abs(a.v)), $"{f}, φ = {phi}: {a.v} vs {b.v}");
        }
    }

    [Fact]
    public void VolumeColour_RoundTripsThroughTheCommandBuilder()
    {
        foreach (var src in new[] { DualOrbitVolumeColor.SecantLyapunov, DualOrbitVolumeColor.PhaseLag, DualOrbitVolumeColor.PairWinding })
        {
            var p = new FractalParameters { DualOrbitVolumeColor = src };
            var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
            {
                Fractal = FractalType.DualOrbitVolume, Parameters = p,
                FamilyParams = RegionFractalParams.Snapshot(FractalType.DualOrbitVolume, p)!.ToKeyValues(),
            });
            Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var err), err);
            var fresh = new FractalParameters();
            RegionFractalParams.FromKeyValues(o.Params, FractalType.DualOrbitVolume, out _)!.ApplyTo(fresh);
            Assert.Equal(src, fresh.DualOrbitVolumeColor);
        }
    }
}
