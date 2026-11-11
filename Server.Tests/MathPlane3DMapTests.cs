// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1715 (Math Plane 3D T3, #1556) — the 3×3 map: Higham's polar factors (orthonormal R,
// symmetric positive definite S, R·S = M), quaternion slerp (unit length, endpoints,
// angle linear in t), the polar frame turning at a constant rate, det as the
// parallelepiped's triple product, eigenpairs satisfying M v = λ v, the renderer moving
// objects through the map, and the params riding the region snapshot / Command builder.

using System;
using System.Linq;
using System.Threading;
using FracturingFog;
using FracturingFog.Batch;
using FracturingFog.Cli;
using FracturingFog.Models;
using FracturingFog.Render;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class MathPlane3DMapTests
{
    private const int W = 320, H = 200;

    private static Mat3 Random3(Random rng, double spread = 2)
        => new(rng.NextDouble() * 2 * spread - spread, rng.NextDouble() * 2 * spread - spread, rng.NextDouble() * 2 * spread - spread,
               rng.NextDouble() * 2 * spread - spread, rng.NextDouble() * 2 * spread - spread, rng.NextDouble() * 2 * spread - spread,
               rng.NextDouble() * 2 * spread - spread, rng.NextDouble() * 2 * spread - spread, rng.NextDouble() * 2 * spread - spread);

    // Entry (r, c) written out, independently of Mat3's own operators.
    private static double E(Mat3 m, int r, int c) => (r, c) switch
    {
        (0, 0) => m.M11, (0, 1) => m.M12, (0, 2) => m.M13,
        (1, 0) => m.M21, (1, 1) => m.M22, (1, 2) => m.M23,
        (2, 0) => m.M31, (2, 1) => m.M32, _ => m.M33,
    };

    private static double[,] Mul(Mat3 a, Mat3 b)
    {
        var p = new double[3, 3];
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                for (int k = 0; k < 3; k++) p[r, c] += E(a, r, k) * E(b, k, c);
        return p;
    }

    // Rotation by angle a about a tilted axis: Q · Rz(a) · Qᵀ, with Q a fixed rotation (a
    // product of turns about x and y) carrying z to the axis. Built from plane rotations only.
    private static Mat3 Rz(double a) => new(Math.Cos(a), -Math.Sin(a), 0, Math.Sin(a), Math.Cos(a), 0, 0, 0, 1);
    private static Mat3 Rx(double a) => new(1, 0, 0, 0, Math.Cos(a), -Math.Sin(a), 0, Math.Sin(a), Math.Cos(a));
    private static Mat3 Ry(double a) => new(Math.Cos(a), 0, Math.Sin(a), 0, 1, 0, -Math.Sin(a), 0, Math.Cos(a));
    private static Mat3 AxisAngle(double tiltX, double tiltY, double a)
    {
        var q = Ry(tiltY) * Rx(tiltX);
        return q * Rz(a) * q.Transposed();
    }

    // ── Polar decomposition ──────────────────────────────────────────────

    [Fact]
    public void PolarFactors_AreARotation_AndASymmetricPositiveDefiniteMatrix_ThatRebuildM()
    {
        var rng = new Random(11);
        int done = 0;
        while (done < 200)
        {
            var m = Random3(rng);
            if (!(m.Det > 0.05)) continue;
            done++;
            var (r, s) = MathPlane3DMap.PolarDecompose(m)!.Value;
            var rtr = Mul(r.Transposed(), r);
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    Assert.Equal(i == j ? 1.0 : 0.0, rtr[i, j], 12);
                    Assert.Equal(E(s, i, j), E(s, j, i), 12);
                }
            Assert.Equal(1.0, r.Det, 12);
            // S positive definite, by the definition: xᵀ S x > 0 for every x ≠ 0 (sampled).
            for (int k = 0; k < 50; k++)
            {
                double x = rng.NextDouble() - 0.5, y = rng.NextDouble() - 0.5, z = rng.NextDouble() - 0.5;
                var sx = s.Apply(x, y, z);
                Assert.True(x * sx.X + y * sx.Y + z * sx.Z > 0);
            }
            Assert.True(s.Det > 0);
            var rs = Mul(r, s);
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++) Assert.Equal(E(m, i, j), rs[i, j], 11);
        }
    }

    [Fact]
    public void APureRotation_HasSEqualToTheIdentity()
    {
        var rot = AxisAngle(0.7, -0.4, 2.1);
        var (r, s) = MathPlane3DMap.PolarDecompose(rot)!.Value;
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                Assert.Equal(i == j ? 1.0 : 0.0, E(s, i, j), 12);
                Assert.Equal(E(rot, i, j), E(r, i, j), 12);
            }
    }

    // ── Slerp ────────────────────────────────────────────────────────────

    [Fact]
    public void Slerp_KeepsUnitLength_HitsBothEnds_AndItsAngleIsLinearInT()
    {
        var rng = new Random(5);
        for (int k = 0; k < 50; k++)
        {
            var a = new MathPlaneQuat(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5).Normalized();
            var b = new MathPlaneQuat(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5).Normalized();
            if (MathPlaneQuat.Dot(a, b) < 0) b = new(-b.W, -b.X, -b.Y, -b.Z);   // the shorter arc
            double total = Math.Acos(Math.Min(1, MathPlaneQuat.Dot(a, b)));
            AssertQuat(a, MathPlaneQuat.Slerp(a, b, 0));
            AssertQuat(b, MathPlaneQuat.Slerp(a, b, 1));
            foreach (double t in new[] { 0.1, 0.25, 0.5, 0.8 })
            {
                var q = MathPlaneQuat.Slerp(a, b, t);
                Assert.Equal(1.0, q.Length, 12);
                Assert.Equal(t * total, Math.Acos(Math.Min(1, MathPlaneQuat.Dot(a, q))), 9);
                Assert.Equal((1 - t) * total, Math.Acos(Math.Min(1, MathPlaneQuat.Dot(q, b))), 9);
            }
        }
        static void AssertQuat(MathPlaneQuat e, MathPlaneQuat g)
        {
            Assert.Equal(e.W, g.W, 12); Assert.Equal(e.X, g.X, 12); Assert.Equal(e.Y, g.Y, 12); Assert.Equal(e.Z, g.Z, 12);
        }
    }

    [Fact]
    public void Slerp_TakesTheShorterArc_SoQAndMinusQGiveTheSameRotations()
    {
        // q and −q are the same rotation; slerping towards either must give the same frames.
        var rng = new Random(13);
        for (int k = 0; k < 30; k++)
        {
            var a = new MathPlaneQuat(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5).Normalized();
            var b = new MathPlaneQuat(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5, rng.NextDouble() - 0.5).Normalized();
            var nb = new MathPlaneQuat(-b.W, -b.X, -b.Y, -b.Z);
            foreach (double t in new[] { 0.3, 0.7 })
            {
                var m1 = MathPlaneQuat.Slerp(a, b, t).ToMatrix();
                var m2 = MathPlaneQuat.Slerp(a, nb, t).ToMatrix();
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++) Assert.Equal(E(m1, i, j), E(m2, i, j), 10);
            }
        }
    }

    [Fact]
    public void Mat3_PrintsWithoutRecursing()
    {
        // A record prints its properties; a Mat3-valued property would print itself forever.
        string s = new Mat3(1, 2, 3, 4, 5, 6, 7, 8, 10).ToString();
        Assert.Contains("M33 = 10", s);
        Assert.True(s.Length < 1000);
    }

    [Fact]
    public void QuaternionMatrix_RoundTrips_IncludingNearAHalfTurn()
    {
        foreach (double angle in new[] { 0.0, 0.4, 1.9, 3.0, Math.PI - 1e-7 })
        {
            var r = AxisAngle(0.3, -1.1, angle);
            var back = MathPlaneQuat.FromMatrix(r).ToMatrix();
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++) Assert.Equal(E(r, i, j), E(back, i, j), 10);
        }
    }

    [Fact]
    public void ThePolarFrame_TurnsAPureRotationAtAConstantRate_WithoutShrinking()
    {
        // 150° about a tilted axis: polar keeps det 1 and turns t·150°; linear would shrink.
        var rot = AxisAngle(0.5, 0.9, 150 * Math.PI / 180);
        foreach (double t in new[] { 0.2, 0.5, 0.9 })
        {
            var f = MathPlane3DMap.At(rot, t, MathPlaneInterpolation.Polar);
            Assert.Equal(1.0, f.Det, 10);
            var expect = AxisAngle(0.5, 0.9, t * 150 * Math.PI / 180);
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++) Assert.Equal(E(expect, i, j), E(f, i, j), 10);
        }
        Assert.True(MathPlane3DMap.At(rot, 0.5, MathPlaneInterpolation.Linear).Det < 0.6, "linear passes close to collapse");
    }

    [Fact]
    public void TheFrame_IsTheIdentityAtZero_AndMAtOne_AndFallsBackWhenDetIsNotPositive()
    {
        var m = new Mat3(1, 0.5, 0, 0, 2, 0, 0.3, 0, 1.2);
        foreach (var mode in new[] { MathPlaneInterpolation.Linear, MathPlaneInterpolation.Polar })
        {
            Assert.Equal(Mat3.Identity, MathPlane3DMap.At(m, 0, mode));
            var one = MathPlane3DMap.At(m, 1, mode);
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) Assert.Equal(E(m, i, j), E(one, i, j), 11);
        }
        var flip = new Mat3(-1, 0, 0, 0, 1, 0, 0, 0, 1);
        Assert.Equal(MathPlane3DMap.At(flip, 0.3, MathPlaneInterpolation.Linear), MathPlane3DMap.At(flip, 0.3, MathPlaneInterpolation.Polar));
        var p = new FractalParameters { MathPlane3DMapOn = true, MathPlane3DM11 = -1, MathPlane3DM12 = 0, MathPlane3DInterpolation = MathPlaneInterpolation.Polar };
        Assert.True(MathPlane3DMap.PolarFallsBack(p));
        p.MathPlane3DM11 = 1;
        Assert.False(MathPlane3DMap.PolarFallsBack(p));
    }

    // ── Read-outs ────────────────────────────────────────────────────────

    [Fact]
    public void TheParallelepipedsVolume_IsTheCofactorDeterminant()
    {
        var rng = new Random(2);
        for (int k = 0; k < 100; k++)
        {
            var m = Random3(rng);
            var v = MathPlane3DMap.Parallelepiped(m);
            var o = v[0];
            (double X, double Y, double Z) Edge(int i) => (v[i].X - o.X, v[i].Y - o.Y, v[i].Z - o.Z);
            var (a, b, c) = (Edge(1), Edge(2), Edge(4));
            double triple = a.X * (b.Y * c.Z - b.Z * c.Y) - a.Y * (b.X * c.Z - b.Z * c.X) + a.Z * (b.X * c.Y - b.Y * c.X);
            // Cofactor expansion down the first column, written out here.
            double cof = m.M11 * (m.M22 * m.M33 - m.M32 * m.M23) - m.M21 * (m.M12 * m.M33 - m.M32 * m.M13) + m.M31 * (m.M12 * m.M23 - m.M22 * m.M13);
            Assert.Equal(cof, triple, 10);
            Assert.Equal(cof, m.Det, 10);
            // The far corner is the sum of the three columns.
            Assert.Equal(a.X + b.X + c.X, v[7].X, 12);
        }
    }

    [Fact]
    public void EveryReportedEigenpair_SatisfiesMvEqualsLambdaV()
    {
        var rng = new Random(9);
        int withPairs = 0;
        for (int k = 0; k < 300; k++)
        {
            var m = Random3(rng);
            var eig = MathPlane3DMap.Eigen(m, out bool all);
            Assert.False(all);
            Assert.InRange(eig.Count, 1, 3);   // a real 3×3 always has a real eigenvalue
            withPairs++;
            foreach (var e in eig) AssertPair(m, e);
        }
        Assert.Equal(300, withPairs);
    }

    [Fact]
    public void Eigen_SpecialCases()
    {
        var diag = MathPlane3DMap.Eigen(new Mat3(2, 0, 0, 0, 3, 0, 0, 0, 4), out _);
        Assert.Equal(new[] { 2.0, 3.0, 4.0 }, diag.Select(e => Math.Round(e.Lambda, 9)).OrderBy(x => x));
        // A rotation about z: only the axis, with λ = 1.
        var rot = MathPlane3DMap.Eigen(AxisAngle(0, 0, 1.0), out _);
        var only = Assert.Single(rot);
        Assert.Equal(1.0, only.Lambda, 9);
        Assert.Equal(1.0, Math.Abs(only.Z), 9);
        // The default shear: λ = 1 (triple) with the x-z plane as its eigenspace.
        var shear = new Mat3(1, 1, 0, 0, 1, 0, 0, 0, 1);
        var sh = MathPlane3DMap.Eigen(shear, out _);
        Assert.Equal(2, sh.Count);
        foreach (var e in sh) { AssertPair(shear, e); Assert.Equal(0.0, e.Y, 9); }
        // A double eigenvalue with a plane of eigenvectors, plus a single one.
        var dbl = new Mat3(2, 0, 0, 0, 2, 0, 0, 0, 5);
        var d = MathPlane3DMap.Eigen(dbl, out _);
        Assert.Equal(3, d.Count);
        foreach (var e in d) AssertPair(dbl, e);
        // A multiple of the identity.
        MathPlane3DMap.Eigen(new Mat3(1.5, 0, 0, 0, 1.5, 0, 0, 0, 1.5), out bool all);
        Assert.True(all);
    }

    private static void AssertPair(Mat3 m, MathPlane3DEigen e)
    {
        Assert.Equal(1.0, Math.Sqrt(e.X * e.X + e.Y * e.Y + e.Z * e.Z), 9);
        var mv = (m.M11 * e.X + m.M12 * e.Y + m.M13 * e.Z, m.M21 * e.X + m.M22 * e.Y + m.M23 * e.Z, m.M31 * e.X + m.M32 * e.Y + m.M33 * e.Z);
        double tol = 1e-7 * Math.Max(1, m.Norm);
        Assert.InRange(mv.Item1 - e.Lambda * e.X, -tol, tol);
        Assert.InRange(mv.Item2 - e.Lambda * e.Y, -tol, tol);
        Assert.InRange(mv.Item3 - e.Lambda * e.Z, -tol, tol);
    }

    // ── Renderer ─────────────────────────────────────────────────────────

    private static FractalParameters Params(string scene) => new()
    {
        MathPlane3DScene = scene,
        MathPlane3DCameraDistance = 7, MathPlane3DCameraTheta = 0.7, MathPlane3DCameraPhi = 1.0,
    };

    private static MathPlane3DCalculator Render(FractalParameters p, double zoom = 1)
    {
        var calc = new MathPlane3DCalculator(W, H) { Zoom = zoom, FractalParameters = p };
        calc.Calculate(CancellationToken.None);
        Assert.Null(calc.LastSceneError);
        return calc;
    }

    [Fact]
    public void AMappedPoint_IsDrawnWhereTheMapSendsIt()
    {
        // A point at (1, 0, 0) under the map is drawn at M (1, 0, 0); unmapped, at (1, 0, 0).
        const string scene = "v1; axes off; grid off; point P 1 0 0 #F0E442 nolabel";
        var p = Params(scene);
        p.MathPlane3DMapOn = true;
        (p.MathPlane3DM11, p.MathPlane3DM21, p.MathPlane3DM31) = (0.5, 1.5, 1.0);
        var c = Render(p);
        var cam = MathPlane3DCalculator.CameraFor(p, 1, W, H);
        uint At(double x, double y, double z) { var q = cam.Project(x, y, z)!.Value; return c.ColorBuffer[(int)q.Y * W + (int)q.X]; }
        Assert.Equal(0xFFF0E442u, At(0.5, 1.5, 1.0));
        Assert.NotEqual(0xFFF0E442u, At(1, 0, 0));
        p.MathPlane3DMapOn = false;
        Assert.Equal(Mat3.Identity, Render(p).LastMap);
    }

    [Fact]
    public void TheMappedFrame_IsTheParamsFrame()
    {
        var p = Params("v1");
        (p.MathPlane3DMapOn, p.MathPlane3DT, p.MathPlane3DInterpolation) = (true, 0.4, MathPlaneInterpolation.Polar);
        (p.MathPlane3DM11, p.MathPlane3DM12, p.MathPlane3DM21, p.MathPlane3DM22, p.MathPlane3DM33) = (0, -1, 1, 0, 2);
        Assert.Equal(MathPlane3DMap.At(MathPlane3DMap.Matrix(p), 0.4, MathPlaneInterpolation.Polar), Render(p).LastMap);
    }

    [Fact]
    public void TheReadOuts_Parse_RoundTrip_AndDraw()
    {
        Assert.True(MathPlane3DScene.TryParse("v1; det on; eigen on; lengths on; vector v 1 2 0", out var s, out var err), err?.ToString());
        Assert.True(s.ShowDet && s.ShowEigen && s.ShowLengths);
        Assert.True(MathPlane3DScene.TryParse(s.Serialize(), out var again, out _));
        Assert.Equal(s, again);
        Assert.False(MathPlane3DScene.TryParse("det maybe", out _, out var bad));
        Assert.Contains("on or off", bad!.Message);

        var p = Params("v1; axes off; grid off; vector v 1 2 0 nolabel");
        p.MathPlane3DMapOn = true;
        var plain = Render(p).ColorBuffer;
        p.MathPlane3DScene = "v1; axes off; grid off; det on; eigen on; vector v 1 2 0 nolabel";
        var withReadOuts = Render(p).ColorBuffer;
        Assert.True(plain.Zip(withReadOuts).Count(x => x.First != x.Second) > 500);
    }

    // ── Batch ────────────────────────────────────────────────────────────

    [Fact]
    public void TheMapParams_RideTheSnapshot_AndTheCommandBuilder()
    {
        var p = Params("v1; det on; vector v 1 1 1");
        (p.MathPlane3DMapOn, p.MathPlane3DT, p.MathPlane3DInterpolation) = (true, 0.6, MathPlaneInterpolation.Polar);
        (p.MathPlane3DM11, p.MathPlane3DM12, p.MathPlane3DM13) = (0.8, -0.6, 0.1);
        (p.MathPlane3DM21, p.MathPlane3DM22, p.MathPlane3DM23) = (0.6, 0.8, 0.2);
        (p.MathPlane3DM31, p.MathPlane3DM32, p.MathPlane3DM33) = (0, 0.3, 1.4);
        var snap = RegionFractalParams.Snapshot(FractalType.MathPlane3D, p)!;
        var report = BatchCommandBuilder.BuildWithReport(new BatchCommandSnapshot
        {
            Fractal = FractalType.MathPlane3D, CenterX = 0, CenterY = 0, Zoom = 1, Parameters = p, FamilyParams = snap.ToKeyValues(),
        });
        Assert.True(BatchOptions.TryParse(report.Args.ToArray(), 0, out var o, out var perr), perr);
        var fresh = new FractalParameters();
        RegionFractalParams.FromKeyValues(o.Params, FractalType.MathPlane3D, out var ferr)!.ApplyTo(fresh);
        Assert.Null(ferr);
        Assert.Equal(MathPlane3DMap.Matrix(p), MathPlane3DMap.Matrix(fresh));
        Assert.Equal((true, 0.6, MathPlaneInterpolation.Polar), (fresh.MathPlane3DMapOn, fresh.MathPlane3DT, fresh.MathPlane3DInterpolation));
        Assert.Equal(Render(p).ColorBuffer, Render(fresh).ColorBuffer);
        var clone = p.Clone();
        Assert.Equal(MathPlane3DMap.Matrix(p), MathPlane3DMap.Matrix(clone));
        Assert.Equal(p.MathPlane3DT, clone.MathPlane3DT);
    }

    [Fact]
    public void TAndTheMatrix_AreAnimatable()
    {
        var names = FracturingFog.Abstractions.Animation.FractalAnimatableParamsMap.For(FractalType.MathPlane3D).Select(d => d.ParamName).ToHashSet();
        Assert.Contains("MathPlane3DT", names);
        foreach (int r in new[] { 1, 2, 3 })
            foreach (int c in new[] { 1, 2, 3 }) Assert.Contains($"MathPlane3DM{r}{c}", names);
    }
}
