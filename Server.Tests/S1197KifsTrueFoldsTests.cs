// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// #1197 — the Octahedron / Dodecahedron / MandelboxRot KIFS folds now render the
// shapes their names promise (Knighty's KIFS folds, as in Syntopia's Fragmentarium
// examples; MandelboxRot = canonical Mandelbox + per-iteration rotation). These
// tests check the geometry through symmetry and extent, not through the formula:
//   • the octahedral fold is invariant under a 90° turn about X and fits inside
//     the octahedron |x|+|y|+|z| ≤ 1, with a vertex at (1,0,0);
//   • the dodecahedral fold is invariant under a 72° turn about a face axis
//     (5-fold symmetry, which no cube or tetrahedron has) and fits inside the
//     dodecahedron's circumsphere (radius √3, vertex (1,1,1));
//   • MandelboxRot is a bounded body reaching the Mandelbox's 2·|s|+2 size;
//   • offsets resolve per fold; Menger / Sierpinski are unchanged.

using System;
using FracturingFog;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public sealed class S1197KifsTrueFoldsTests
{
    private const int Iter = 12;

    private static double DE(KifsFoldKind f, double x, double y, double z)
        => KifsCalculator.ProbeDE(f, x, y, z, KifsCalculator.ResolveScale(f, 0), 1, 1, 1, Iter);

    /// <summary>Sphere-trace from radius 8 toward the origin along a direction;
    /// the radius of the first surface hit, or −1 for a miss.</summary>
    private static double SurfaceRadius(KifsFoldKind f, double dx, double dy, double dz)
    {
        double l = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        dx /= l; dy /= l; dz /= l;
        double t = 8.0;
        for (int i = 0; i < 2000; i++)
        {
            double d = DE(f, t * dx, t * dy, t * dz);
            if (d < 1e-5) return t;
            t -= d;
            if (t <= 0) return -1;
        }
        return -1;
    }

    private static (double X, double Y, double Z) Fib(int i, int n)
    {
        double phi = Math.Acos(1.0 - 2.0 * (i + 0.5) / n);
        double th = Math.PI * (1.0 + Math.Sqrt(5.0)) * i;
        return (Math.Sin(phi) * Math.Cos(th), Math.Sin(phi) * Math.Sin(th), Math.Cos(phi));
    }

    [Fact]
    public void Octahedron_Has_Four_Fold_Symmetry_About_X()
    {
        var rng = new Random(5);
        for (int i = 0; i < 200; i++)
        {
            double x = rng.NextDouble() * 2.4 - 1.2, y = rng.NextDouble() * 2.4 - 1.2, z = rng.NextDouble() * 2.4 - 1.2;
            Assert.Equal(DE(KifsFoldKind.Octahedron, x, y, z), DE(KifsFoldKind.Octahedron, x, -z, y), 9);
        }
    }

    [Fact]
    public void Octahedron_Fits_The_Octahedron_With_A_Vertex_At_X1()
    {
        Assert.Equal(1.0, SurfaceRadius(KifsFoldKind.Octahedron, 1, 0, 0), 2);
        int hits = 0;
        for (int i = 0; i < 400; i++)
        {
            var (dx, dy, dz) = Fib(i, 400);
            double r = SurfaceRadius(KifsFoldKind.Octahedron, dx, dy, dz);
            if (r < 0) continue;
            hits++;
            Assert.True(r * (Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz)) <= 1.0 + 1e-2,
                $"hit at r={r:F3} along ({dx:F2},{dy:F2},{dz:F2}) lies outside |x|+|y|+|z| <= 1");
        }
        Assert.True(hits > 40, $"only {hits} hits");
    }

    [Fact]
    public void Dodecahedron_Has_Five_Fold_Symmetry_About_A_Face_Axis()
    {
        // For the dodecahedron with vertices (±1,±1,±1), (0,±1/φ,±φ), (±1/φ,±φ,0),
        // (±φ,0,±1/φ), the face centres lie along (0, ±1, ±φ) and its cyclic
        // permutations. Rotating 72° about one must leave the DE unchanged.
        double phi = KifsCalculator.Phi;
        double ax = 0, ay = 1, az = phi;
        double l = Math.Sqrt(ay * ay + az * az); ay /= l; az /= l;
        double a = 2 * Math.PI / 5, c = Math.Cos(a), s = Math.Sin(a);
        var rng = new Random(9);
        for (int i = 0; i < 300; i++)
        {
            double x = rng.NextDouble() * 3 - 1.5, y = rng.NextDouble() * 3 - 1.5, z = rng.NextDouble() * 3 - 1.5;
            double dot = ax * x + ay * y + az * z;
            double cx = ay * z - az * y, cy = az * x - ax * z, cz = ax * y - ay * x;
            double rx = x * c + cx * s + ax * dot * (1 - c);
            double ry = y * c + cy * s + ay * dot * (1 - c);
            double rz = z * c + cz * s + az * dot * (1 - c);
            Assert.Equal(DE(KifsFoldKind.Dodecahedron, x, y, z), DE(KifsFoldKind.Dodecahedron, rx, ry, rz), 6);
        }
    }

    [Fact]
    public void Dodecahedron_Fits_Its_Circumsphere_And_Reaches_The_Vertex()
    {
        double sqrt3 = Math.Sqrt(3.0);
        Assert.Equal(sqrt3, SurfaceRadius(KifsFoldKind.Dodecahedron, 1, 1, 1), 2);
        int hits = 0;
        for (int i = 0; i < 300; i++)
        {
            var (dx, dy, dz) = Fib(i, 300);
            double r = SurfaceRadius(KifsFoldKind.Dodecahedron, dx, dy, dz);
            if (r < 0) continue;
            hits++;
            Assert.True(r <= sqrt3 + 1e-2, $"hit at r={r:F3} is outside the circumsphere");
        }
        Assert.True(hits > 150, $"only {hits} hits");
    }

    [Fact]
    public void MandelboxRot_Is_A_Bounded_Mandelbox_Sized_Body()
    {
        double scale = KifsCalculator.ResolveScale(KifsFoldKind.MandelboxRot, 0);
        double extent = 2 * Math.Abs(scale) + 2;   // 6 at scale 2
        // A point well outside the body is a positive distance away, but not more
        // than its distance to the origin.
        double d = DE(KifsFoldKind.MandelboxRot, 20, 0, 0);
        Assert.True(d > 0 && d < 20, $"DE at x=20: {d:F3}");
        // The body reaches the Mandelbox's size along an axis.
        double r = SurfaceRadius(KifsFoldKind.MandelboxRot, 1, 0.01, 0.02);
        Assert.InRange(r, extent - 0.5, extent + 0.5);
    }

    [Fact]
    public void Offsets_And_Scales_Resolve_Per_Fold()
    {
        Assert.Equal((1.0, 0.0, 0.0), KifsCalculator.FoldOffset(KifsFoldKind.Octahedron, 1, 1, 1));
        Assert.Equal((1.0, 1.0, 1.0), KifsCalculator.FoldOffset(KifsFoldKind.Dodecahedron, 1, 1, 1));
        Assert.Equal((0.0, 0.0, 0.0), KifsCalculator.FoldOffset(KifsFoldKind.MandelboxRot, 1, 1, 1));
        // Unchanged folds: offsets pass straight through.
        Assert.Equal((0.7, 1.2, 0.9), KifsCalculator.FoldOffset(KifsFoldKind.Menger, 0.7, 1.2, 0.9));
        Assert.Equal((0.7, 1.2, 0.9), KifsCalculator.FoldOffset(KifsFoldKind.Sierpinski, 0.7, 1.2, 0.9));
        // A tweak moves the octahedron's vertex the same way.
        var o = KifsCalculator.FoldOffset(KifsFoldKind.Octahedron, 1.2, 1.1, 0.9);
        Assert.Equal(1.2, o.X, 12); Assert.Equal(0.1, o.Y, 12); Assert.Equal(-0.1, o.Z, 12);

        Assert.Equal(KifsCalculator.Phi * KifsCalculator.Phi, KifsCalculator.ResolveScale(KifsFoldKind.Dodecahedron, 0), 12);
        Assert.Equal(3.0, KifsCalculator.ResolveScale(KifsFoldKind.Menger, 0));
        Assert.Equal(1.7, KifsCalculator.ResolveScale(KifsFoldKind.Dodecahedron, 1.7));
    }
}
