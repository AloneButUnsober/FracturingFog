// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S9.1 (3D-Rendering-Roadmap.md §S9, #391) — the watertight /
// manifold CONTRACT for exported meshes (the mesh analog of the CPU↔GPU render
// parity twin). These lock the validator against known synthetic meshes — a
// closed cube (the full contract), an open sheet (a hole), a three-sheet edge
// (non-manifold), a one-face-flipped cube (inconsistent winding), a welded
// triangle soup (unshared vertices still measure as closed) — and then run it on
// a REAL relief export to document the shipped mesh's health.

using System;
using System.Collections.Generic;
using System.IO;
using FracturingFog.Export;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public class MeshValidatorTests
{
    // Unit cube [0,1]^3 as 8 shared vertices + 12 outward-wound triangles.
    private static (List<(double, double, double)> pos, List<(int, int, int)> tris) UnitCube()
    {
        var pos = new List<(double, double, double)>
        {
            (0,0,0),(1,0,0),(1,1,0),(0,1,0),   // z=0 face corners 0..3
            (0,0,1),(1,0,1),(1,1,1),(0,1,1),   // z=1 face corners 4..7
        };
        // Each face wound CCW as seen from OUTSIDE (outward normal).
        var tris = new List<(int, int, int)>
        {
            (0,3,2),(0,2,1),   // -Z (bottom), normal -Z
            (4,5,6),(4,6,7),   // +Z (top), normal +Z
            (0,1,5),(0,5,4),   // -Y (front)
            (2,3,7),(2,7,6),   // +Y (back)
            (1,2,6),(1,6,5),   // +X (right)
            (0,4,7),(0,7,3),   // -X (left)
        };
        return (pos, tris);
    }

    [Fact]
    public void Cube_Is_Closed_Manifold_With_Unit_Volume()
    {
        var (pos, tris) = UnitCube();
        var r = MeshValidator.Validate(pos, tris);
        Assert.True(r.IsWatertight, r.Summary());
        Assert.True(r.IsEdgeManifold, r.Summary());
        Assert.True(r.IsConsistentlyOriented, r.Summary());
        Assert.True(r.IsClosedManifold);
        Assert.Equal(0, r.BoundaryEdgeCount);
        Assert.Equal(0, r.NonManifoldEdgeCount);
        Assert.Equal(0, r.FlippedEdgeCount);
        Assert.Equal(1.0, r.Volume, 6);
        Assert.Equal(6.0, r.SurfaceArea, 6);
        Assert.Equal(8, r.WeldedVertexCount);
        Assert.Equal(1.0, r.SizeX, 6);
    }

    [Fact]
    public void Welds_Triangle_Soup_To_Shared_Topology()
    {
        // Same cube but every triangle carries its OWN 3 vertices (36 raw), like
        // the FF exporters emit. Welding must recover the 8-vertex closed solid.
        var (pos, tris) = UnitCube();
        var soupPos = new List<(double, double, double)>();
        var soupTris = new List<(int, int, int)>();
        foreach (var (a, b, c) in tris)
        {
            int i = soupPos.Count;
            soupPos.Add(pos[a]); soupPos.Add(pos[b]); soupPos.Add(pos[c]);
            soupTris.Add((i, i + 1, i + 2));
        }
        var r = MeshValidator.Validate(soupPos, soupTris);
        Assert.Equal(36, r.RawVertexCount);
        Assert.Equal(8, r.WeldedVertexCount);
        Assert.True(r.IsClosedManifold, r.Summary());
        Assert.Equal(1.0, r.Volume, 6);
    }

    [Fact]
    public void Open_Sheet_Has_Boundary_Edges_Not_Watertight()
    {
        var pos = new List<(double, double, double)> { (0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0) };
        var tris = new List<(int, int, int)> { (0, 1, 2), (0, 2, 3) };   // a quad, one side
        var r = MeshValidator.Validate(pos, tris);
        Assert.False(r.IsWatertight);
        Assert.Equal(4, r.BoundaryEdgeCount);       // the 4 outer edges (diagonal is shared)
        Assert.True(r.IsEdgeManifold);              // no edge is over-shared
        Assert.False(r.IsClosedManifold);
    }

    [Fact]
    public void Three_Sheet_Edge_Is_NonManifold()
    {
        // Three triangles all sharing the edge (0,1) — a non-manifold "fin".
        var pos = new List<(double, double, double)>
        {
            (0, 0, 0), (1, 0, 0), (0, 1, 0), (0, 0, 1), (0, -1, 0)
        };
        var tris = new List<(int, int, int)> { (0, 1, 2), (0, 1, 3), (0, 1, 4) };
        var r = MeshValidator.Validate(pos, tris);
        Assert.Equal(1, r.NonManifoldEdgeCount);    // edge 0-1 used by 3 faces
        Assert.False(r.IsEdgeManifold);
        Assert.False(r.IsClosedManifold);
    }

    [Fact]
    public void Flipped_Face_Breaks_Orientation_But_Not_Watertightness()
    {
        var (pos, tris) = UnitCube();
        // Reverse one triangle's winding — the solid is still closed + manifold,
        // but two edges are now traversed the same way (a flipped normal).
        tris[0] = (tris[0].Item1, tris[0].Item3, tris[0].Item2);
        var r = MeshValidator.Validate(pos, tris);
        Assert.True(r.IsWatertight);
        Assert.True(r.IsEdgeManifold);
        Assert.False(r.IsConsistentlyOriented);
        Assert.True(r.FlippedEdgeCount > 0);
        Assert.False(r.IsClosedManifold);
    }

    [Fact]
    public void Degenerate_Triangle_Is_Counted_And_Skipped()
    {
        var (pos, tris) = UnitCube();
        tris.Add((2, 2, 5));    // a degenerate (repeated corner) tri appended
        var r = MeshValidator.Validate(pos, tris);
        Assert.Equal(1, r.DegenerateTriangleCount);
        Assert.True(r.IsClosedManifold, r.Summary());   // degenerate ignored for topology
        Assert.Equal(1.0, r.Volume, 6);
    }

    // ── Real relief export ────────────────────────────────────────────────────
    // The shipped HeightfieldMeshExporter builds top + contoured base + skirt
    // walls; the validator proves it is a closed, 2-manifold solid with real
    // volume. Orientation-consistency is a KNOWN gap (the wall seams wind against
    // the surfaces they meet) tracked as S9.1a (#419) — so this asserts the hard
    // topological contract (watertight + edge-manifold + positive volume) but not
    // full winding consistency, which #419 will tighten to IsClosedManifold.
    private static (uint[] albedo, float[] height) Bump(int w, int h)
    {
        var albedo = new uint[w * h];
        var height = new float[w * h];
        double cx = w / 2.0, cy = h / 2.0, r = Math.Min(w, h) * 0.35;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
            int i = y * w + x;
            height[i] = d < r ? (float)(r - d) : 0f;
            albedo[i] = 0xFF3080C0u;
        }
        return (albedo, height);
    }

    [Fact]
    public void Real_Relief_Export_Is_Watertight_Manifold_Solid()
    {
        int w = 128, h = 96;
        var (albedo, height) = Bump(w, h);
        var p = new FractalParameters
        {
            Relief2DEnabled = true, Relief2DRaymarch = true,
            Relief2DHeightScale = 1.4, Relief2DMeshHeight = 0.4,
        };
        string path = Path.Combine(Path.GetTempPath(), $"ff-mv-{Guid.NewGuid():N}.stl");
        try
        {
            int tris = HeightfieldMeshExporter.Export(albedo, height, w, h, p, path, targetGrid: 60);
            Assert.True(tris > 0);

            var (pos, t) = StlMeshReader.ReadBinary(path);
            Assert.Equal(tris, t.Count);

            var r = MeshValidator.Validate(pos, t);
            // The hard topological contract holds today.
            Assert.True(r.IsWatertight, r.Summary());
            Assert.True(r.IsEdgeManifold, r.Summary());
            Assert.True(r.Volume > 0, r.Summary());
            Assert.True(r.WeldedVertexCount < r.RawVertexCount);   // welding recovered shared topology
            // Bounds track the raymarch world frame (X spans the image aspect, Y is up).
            Assert.True(r.SizeX > r.SizeZ, r.Summary());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
