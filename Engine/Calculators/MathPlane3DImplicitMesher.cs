// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Calculators/MathPlane3DImplicitMesher.cs
//
// #1717 (Math Plane 3D T5, #1556) — implicit surfaces F(x, y, z) = 0. Design:
// Docs/Technical/MathPlane3D-DesignPlan.md §3.5.
//
// Marching cubes (Lorensen & Cline 1987, Docs/Resources-Bibliography.md
// #lorensen-cline-marching-cubes) on the surface's box, mesh N cells a side, with the
// edge / triangle tables of Paul Bourke's write-up (#bourke-polygonise) shared with the
// S9 exporter (UserBulbMeshExporter). A corner is inside where F < 0. Triangles are
// wound so their normals point to F > 0 (outward for a closed surface such as
// x^2 + y^2 + z^2 - 1).
//
// Poles are handled as the 2D sampler does (MathPlaneImplicitSampler): F = 1/x changes
// sign across x = 0 without a root. Each crossing is located by bisection on its edge;
// at a root |F| shrinks with the bracket, at a pole it grows. A crossing whose final
// |F| is not well below the edge's end values is a pole, and every cell that uses that
// edge is dropped, so a pole leaves no false sheet. A cell with a non-finite corner is
// dropped too. Each edge's vertex is made once and shared by the cells round it, so a
// closed surface inside the box meshes watertight.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FracturingFog.Export;
using FracturingFog.Models;

namespace FracturingFog;

/// <summary>#1717 — an implicit surface's mesh: shared vertices and index triangles.</summary>
public sealed class MathPlane3DImplicitMesh
{
    public (double X, double Y, double Z)[] Vertices { get; init; } = Array.Empty<(double, double, double)>();
    public (int A, int B, int C)[] Triangles { get; init; } = Array.Empty<(int, int, int)>();
    /// <summary>Cells a side of the sampling grid.</summary>
    public int N { get; init; }
    /// <summary>True when <see cref="MathPlane3DImplicitMesher.MaxTriangles"/> stopped the mesh early.</summary>
    public bool Truncated { get; init; }
    /// <summary>Largest |coordinate| of the vertices (sizes the grid).</summary>
    public double Extent { get; init; }
}

public static class MathPlane3DImplicitMesher
{
    /// <summary>Halvings per edge crossing (bracket = cell / 2^steps), as in 2D.</summary>
    public const int BisectionSteps = MathPlaneImplicitSampler.BisectionSteps;
    /// <summary>A crossing is a root when its final |F| is below this fraction of the
    /// larger end value; a pole is above 1 (as in 2D).</summary>
    public const double RootRatio = MathPlaneImplicitSampler.RootRatio;
    /// <summary>Triangle cap per surface (bounds pathological expressions).</summary>
    public const int MaxTriangles = 4_000_000;

    /// <summary>The mesh of an implicit surface object; null when it is not one or does not parse.</summary>
    public static MathPlane3DImplicitMesh? Build(MathPlaneObject o, CancellationToken ct = default)
    {
        if (o.Kind != MathPlaneObjectKind.Surface || o.CurveKind != MathPlaneCurveKind.Implicit || o.Expressions.Count != 1)
            return null;
        if (!PlotExpression.TryParse(o.Expressions[0], out var f, out _)) return null;
        var b = MathPlane3DScene.ImplicitBox(o);
        return Build(f!.EvalXYZ, (b.X0, b.Y0, b.Z0), (b.X1, b.Y1, b.Z1), MathPlane3DScene.MeshOf(o), ct);
    }

    /// <summary>Marching cubes of F = 0 over the box [lo, hi] with <paramref name="n"/> cells a side.</summary>
    public static MathPlane3DImplicitMesh Build(Func<double, double, double, double> f,
                                                (double X, double Y, double Z) lo, (double X, double Y, double Z) hi,
                                                int n, CancellationToken ct = default)
    {
        n = Math.Clamp(n, 1, MathPlane3DScene.MaxMesh);
        int side = n + 1;
        double hx = (hi.X - lo.X) / n, hy = (hi.Y - lo.Y) / n, hz = (hi.Z - lo.Z) / n;
        double PX(int i) => lo.X + hx * i;
        double PY(int j) => lo.Y + hy * j;
        double PZ(int k) => lo.Z + hz * k;

        var field = new double[side * side * side];
        Parallel.For(0, side, new ParallelOptions { CancellationToken = ct }, i =>
        {
            double x = PX(i);
            for (int j = 0; j < side; j++)
            {
                double y = PY(j);
                for (int k = 0; k < side; k++) field[(i * side + j) * side + k] = f(x, y, PZ(k));
            }
        });
        double V(int i, int j, int k) => field[(i * side + j) * side + k];

        var verts = new List<(double X, double Y, double Z)>();
        var tris = new List<(int A, int B, int C)>();
        // Edge key: low corner's node index * 3 + axis. Value: vertex index, or -1 for a pole.
        var edges = new Dictionary<long, int>();
        const int Pole = -1;

        // The edge from node (i, j, k) one step along axis (0 = x, 1 = y, 2 = z).
        int Edge(int i, int j, int k, int axis)
        {
            long key = ((long)(i * side + j) * side + k) * 3 + axis;
            if (edges.TryGetValue(key, out int idx)) return idx;
            int i2 = i + (axis == 0 ? 1 : 0), j2 = j + (axis == 1 ? 1 : 0), k2 = k + (axis == 2 ? 1 : 0);
            var a = (PX(i), PY(j), PZ(k));
            var bb = (PX(i2), PY(j2), PZ(k2));
            idx = Cross(f, a, V(i, j, k), bb, V(i2, j2, k2)) is { } p ? Add(p) : Pole;
            edges[key] = idx;
            return idx;
        }
        int Add((double, double, double) p) { verts.Add(p); return verts.Count - 1; }

        Span<int> e = stackalloc int[12];
        bool truncated = false;
        for (int i = 0; i < n && !truncated; i++)
        {
            ct.ThrowIfCancellationRequested();
            for (int j = 0; j < n && !truncated; j++)
                for (int k = 0; k < n; k++)
                {
                    // Corners in Bourke's order: 0 (0,0,0) 1 (1,0,0) 2 (1,1,0) 3 (0,1,0),
                    // 4..7 the same at z + 1.
                    double v0 = V(i, j, k), v1 = V(i + 1, j, k), v2 = V(i + 1, j + 1, k), v3 = V(i, j + 1, k);
                    double v4 = V(i, j, k + 1), v5 = V(i + 1, j, k + 1), v6 = V(i + 1, j + 1, k + 1), v7 = V(i, j + 1, k + 1);
                    if (!double.IsFinite(v0) || !double.IsFinite(v1) || !double.IsFinite(v2) || !double.IsFinite(v3)
                        || !double.IsFinite(v4) || !double.IsFinite(v5) || !double.IsFinite(v6) || !double.IsFinite(v7)) continue;
                    int ci = (v0 < 0 ? 1 : 0) | (v1 < 0 ? 2 : 0) | (v2 < 0 ? 4 : 0) | (v3 < 0 ? 8 : 0)
                           | (v4 < 0 ? 16 : 0) | (v5 < 0 ? 32 : 0) | (v6 < 0 ? 64 : 0) | (v7 < 0 ? 128 : 0);
                    int em = UserBulbMeshExporter.EdgeTable[ci];
                    if (em == 0) continue;

                    // Bourke's edges: 0..3 round the bottom face, 4..7 the top, 8..11 the uprights.
                    bool pole = false;
                    for (int q = 0; q < 12 && !pole; q++)
                    {
                        if ((em & (1 << q)) == 0) continue;
                        e[q] = q switch
                        {
                            0 => Edge(i, j, k, 0),
                            1 => Edge(i + 1, j, k, 1),
                            2 => Edge(i, j + 1, k, 0),
                            3 => Edge(i, j, k, 1),
                            4 => Edge(i, j, k + 1, 0),
                            5 => Edge(i + 1, j, k + 1, 1),
                            6 => Edge(i, j + 1, k + 1, 0),
                            7 => Edge(i, j, k + 1, 1),
                            8 => Edge(i, j, k, 2),
                            9 => Edge(i + 1, j, k, 2),
                            10 => Edge(i + 1, j + 1, k, 2),
                            _ => Edge(i, j + 1, k, 2),
                        };
                        pole = e[q] == Pole;
                    }
                    if (pole) continue;

                    for (int t = 0; UserBulbMeshExporter.TriTable[ci, t] != -1; t += 3)
                    {
                        int a = e[UserBulbMeshExporter.TriTable[ci, t]];
                        int b = e[UserBulbMeshExporter.TriTable[ci, t + 1]];
                        int c = e[UserBulbMeshExporter.TriTable[ci, t + 2]];
                        if (a == b || b == c || a == c) continue;
                        // Reversed (a, c, b): with inside = F < 0 the table's order faces inward
                        // (the S9 exporter's convention, which flips it the same way).
                        tris.Add((a, c, b));
                    }
                    if (tris.Count >= MaxTriangles) { truncated = true; break; }
                }
        }
        double extent = 0;
        foreach (var (x, y, z) in verts) extent = Math.Max(extent, Math.Max(Math.Abs(x), Math.Max(Math.Abs(y), Math.Abs(z))));
        return new MathPlane3DImplicitMesh { Vertices = verts.ToArray(), Triangles = tris.ToArray(), N = n, Truncated = truncated, Extent = extent };
    }

    // The sign change on an edge, by bisection; null when it is a pole or a jump.
    private static (double X, double Y, double Z)? Cross(Func<double, double, double, double> f,
                                                         (double X, double Y, double Z) a, double va,
                                                         (double X, double Y, double Z) b, double vb)
    {
        (double, double, double) At(double s) => (a.X + s * (b.X - a.X), a.Y + s * (b.Y - a.Y), a.Z + s * (b.Z - a.Z));
        double lo = 0, hi = 1, flo = va, fm = 0;
        for (int k = 0; k < BisectionSteps; k++)
        {
            double m = 0.5 * (lo + hi);
            var (x, y, z) = At(m);
            fm = f(x, y, z);
            if (!double.IsFinite(fm)) return null;
            if ((fm < 0) == (flo < 0)) { lo = m; flo = fm; } else hi = m;
        }
        if (Math.Abs(fm) > RootRatio * Math.Max(Math.Abs(va), Math.Abs(vb))) return null;
        // A final linear step inside the small bracket.
        var pl = At(lo); var ph = At(hi);
        double fl = f(pl.Item1, pl.Item2, pl.Item3), fh = f(ph.Item1, ph.Item2, ph.Item3);
        double s = fl != fh ? lo + (hi - lo) * fl / (fl - fh) : 0.5 * (lo + hi);
        if (!(s >= lo && s <= hi)) s = 0.5 * (lo + hi);
        return At(s);
    }
}
