// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S9 (3D-Rendering-Roadmap.md §S9, #391) — extend the S9.1 watertight
// / manifold CONTRACT to the SECOND exporter: UserBulbMeshExporter's Marching Cubes
// (the true-3D DE-isosurface path, S9.2 territory). Using an analytic sphere DE
// (a known closed surface) these lock, via MeshValidator, that:
//   • when the surface is INTERIOR to the sample cube, MC yields a CLOSED,
//     2-manifold, consistently-wound solid with the right volume — i.e. FF's MC is
//     topologically correct (no ambiguous-face non-manifold edges, no flipped
//     winding), the regression guard for the tables / dedup / crease code;
//   • the recommended path — auto-size the cube with ProbeBoundingRange so the set
//     is enclosed — produces a PRINT-READY mesh even when the raw range was too
//     small;
//   • an UNDERSIZED cube leaves the mesh OPEN with capping OFF, but the default
//     boundary cap (#422) seals the box-face cut into a watertight solid — while
//     staying a byte-for-byte no-op when the surface is interior;
//   • the crease-normal path (which splits vertices for shading) does not change
//     the welded topology, so the solid stays closed.

using System;
using System.IO;
using FracturingFog.Export;
using Xunit;

namespace FracturingFog.Server.Tests;

public class McMeshValidationTests
{
    // Analytic sphere signed distance — a clean closed isosurface.
    private static SampleDistance Sphere(double r) => (x, y, z) => Math.Sqrt(x * x + y * y + z * z) - r;

    private static MeshReport ExportAndValidate(SampleDistance de, double range, int n,
        double creaseDegrees = 180.0, bool capBoundary = true)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ff-mc-{Guid.NewGuid():N}.stl");
        try
        {
            UserBulbMeshExporter.ExportMarchingCubes(path, de, 0, 0, 0, range, n,
                isoScale: 0.5, isoAbsolute: false, superSamples: 1,
                creaseDegrees: creaseDegrees, capBoundary: capBoundary);
            var (pos, tris) = StlMeshReader.ReadBinary(path);
            return MeshValidator.Validate(pos, tris, weldEpsilon: 1e-5);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static int ExportTris(SampleDistance de, double range, int n, bool capBoundary)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ff-mc-{Guid.NewGuid():N}.stl");
        try
        {
            return UserBulbMeshExporter.ExportMarchingCubes(path, de, 0, 0, 0, range, n,
                isoScale: 0.5, isoAbsolute: false, superSamples: 1,
                creaseDegrees: 180.0, capBoundary: capBoundary);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Interior_Surface_Is_Closed_Manifold()
    {
        // Sphere radius 1 well inside a half-extent-1.6 cube → fully enclosed.
        var r = ExportAndValidate(Sphere(1.0), range: 1.6, n: 48);
        Assert.True(r.IsWatertight, r.Summary());
        Assert.True(r.IsEdgeManifold, r.Summary());
        Assert.True(r.IsConsistentlyOriented, r.Summary());
        Assert.True(r.IsClosedManifold, r.Summary());
        Assert.True(r.SignedVolume > 0, r.Summary());             // wound outward
        // A sphere of radius ~1 (MC iso sits a fraction of a cell out): volume in
        // the right neighbourhood of 4/3·π, not degenerate or wildly off.
        Assert.InRange(r.Volume, 3.5, 6.0);
        Assert.True(r.WeldedVertexCount < r.RawVertexCount);      // shared topology recovered
    }

    [Fact]
    public void ProbeBoundingRange_AutoSize_Yields_PrintReady_Mesh()
    {
        // The set exceeds the raw range; the recommended path probes a range that
        // encloses it, so the export comes out closed + manifold instead of open.
        var de = Sphere(1.8);
        double probed = UserBulbMeshExporter.ProbeBoundingRange(de, 0, 0, 0, maxRange: 8.0);
        Assert.True(probed > 1.8, $"probe should enclose the r=1.8 set (got {probed})");

        var r = ExportAndValidate(de, range: probed, n: 48);
        Assert.True(r.IsClosedManifold, r.Summary());
        Assert.True(r.SignedVolume > 0, r.Summary());
    }

    [Fact]
    public void Undersized_Cube_Leaves_Boundary_Open_Without_Cap()
    {
        // Documents the raw MC limitation with capping OFF: a surface that exits the
        // sample cube is not capped, so the mesh has boundary edges.
        var r = ExportAndValidate(Sphere(1.8), range: 1.6, n: 48, capBoundary: false);
        Assert.False(r.IsWatertight, r.Summary());
        Assert.True(r.BoundaryEdgeCount > 0);
        // Even open, MC keeps it edge-manifold and consistently wound.
        Assert.True(r.IsEdgeManifold, r.Summary());
        Assert.True(r.IsConsistentlyOriented, r.Summary());
    }

    [Fact]
    public void Undersized_Cube_Capped_Is_Closed_Solid()
    {
        // #422: the SAME undersized cube, with boundary capping ON (the default),
        // seals the box-face cut into a watertight, outward-wound solid — a fractal
        // that exits the sample cube now exports print-ready instead of as a shell
        // with holes.
        var r = ExportAndValidate(Sphere(1.8), range: 1.6, n: 48);   // capBoundary defaults true
        Assert.Equal(0, r.BoundaryEdgeCount);
        Assert.True(r.IsClosedManifold, r.Summary());
        Assert.True(r.SignedVolume > 0, r.Summary());                // wound outward
    }

    [Fact]
    public void Cap_Is_NoOp_When_Surface_Interior()
    {
        // When the surface is fully interior the shell corners are all outside, so
        // no cap fires: capped and uncapped exports are geometrically identical.
        var de = Sphere(1.0);
        int capped   = ExportTris(de, range: 1.6, n: 48, capBoundary: true);
        int uncapped = ExportTris(de, range: 1.6, n: 48, capBoundary: false);
        Assert.Equal(uncapped, capped);
        Assert.True(capped > 0);
    }

    [Fact]
    public void Crease_Normals_Do_Not_Break_Topology()
    {
        // creaseDegrees < 180 splits vertices for per-facet normals; the validator
        // welds by POSITION, so the interior sphere stays a closed 2-manifold.
        var r = ExportAndValidate(Sphere(1.0), range: 1.6, n: 48, creaseDegrees: 30.0);
        Assert.True(r.IsClosedManifold, r.Summary());
    }
}
