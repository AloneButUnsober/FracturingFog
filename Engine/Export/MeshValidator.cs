// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Export/MeshValidator.cs
//
// Roadmap slice S9.1 (3D-Rendering-Roadmap.md §S9, parent #389 / #391) — the
// WATERTIGHT / MANIFOLD CONTRACT: the mesh analog of the CPU↔GPU render parity
// twin. Meshing is deterministic geometry, so the correctness contract is a
// validator that proves the exported solid is a closed, 2-manifold, consistently
// wound surface — asserted in tests (Blender's "3D Print Toolbox" is the model).
//
// The FF exporters (HeightfieldMeshExporter, UserBulbMeshExporter) emit a
// TRIANGLE SOUP: each cell adds its own fresh vertices (top / base / skirt walls
// never share an index), so index-based edge adjacency would report every edge as
// a hole. The validator therefore WELDS by position first — snapping coincident
// vertices onto a shared id — then measures edge incidence on the welded topology:
//
//   • boundary edge     — used by exactly 1 face  → a hole (not watertight)
//   • manifold edge     — used by exactly 2 faces → good
//   • non-manifold edge — used by >2 faces        → bad (three sheets meet)
//
// plus orientation (each interior edge should be traversed once each way, so the
// two faces wind consistently), the bounding box, surface area, and the signed
// volume (Σ v0·(v1×v2)/6 — exact for a closed oriented mesh). Pure, allocation-
// light, format-agnostic (positions + index triples in, report out) so any
// exporter or a re-read STL/OBJ can be checked the same way.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FracturingFog.Export;

/// <summary>The result of a <see cref="MeshValidator"/> pass — the printable
/// health report for an exported mesh (roadmap S9.1, #391).</summary>
public readonly record struct MeshReport(
    int TriangleCount,
    int DegenerateTriangleCount,
    int RawVertexCount,
    int WeldedVertexCount,
    int BoundaryEdgeCount,
    int NonManifoldEdgeCount,
    int FlippedEdgeCount,
    double MinX, double MinY, double MinZ,
    double MaxX, double MaxY, double MaxZ,
    double SurfaceArea,
    double SignedVolume)
{
    /// <summary>No boundary edges — the surface has no holes (a closed solid).</summary>
    public bool IsWatertight => BoundaryEdgeCount == 0;

    /// <summary>Every edge is shared by at most two faces (no three-sheet edges).</summary>
    public bool IsEdgeManifold => NonManifoldEdgeCount == 0;

    /// <summary>Every interior edge is traversed once in each direction, so
    /// adjacent faces wind the same way (no flipped normals).</summary>
    public bool IsConsistentlyOriented => FlippedEdgeCount == 0;

    /// <summary>The full 3D-print contract: closed, 2-manifold, consistently
    /// wound. This is the assertion the mesh analog of the parity twin makes.</summary>
    public bool IsClosedManifold => IsWatertight && IsEdgeManifold && IsConsistentlyOriented;

    public double SizeX => MaxX - MinX;
    public double SizeY => MaxY - MinY;
    public double SizeZ => MaxZ - MinZ;

    /// <summary>Absolute enclosed volume — meaningful only for a closed mesh.</summary>
    public double Volume => Math.Abs(SignedVolume);

    /// <summary>A one-block human-readable report (the "3D Print Toolbox" summary).</summary>
    public string Summary()
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(string.Create(ci, $"Triangles: {TriangleCount}  (degenerate: {DegenerateTriangleCount})\n"));
        sb.Append(string.Create(ci, $"Vertices:  {WeldedVertexCount} welded / {RawVertexCount} raw\n"));
        sb.Append(string.Create(ci, $"Watertight: {(IsWatertight ? "yes" : "NO")}  (boundary edges: {BoundaryEdgeCount})\n"));
        sb.Append(string.Create(ci, $"Manifold:   {(IsEdgeManifold ? "yes" : "NO")}  (non-manifold edges: {NonManifoldEdgeCount})\n"));
        sb.Append(string.Create(ci, $"Oriented:   {(IsConsistentlyOriented ? "yes" : "NO")}  (flipped edges: {FlippedEdgeCount})\n"));
        sb.Append(string.Create(ci, $"Bounds:     [{MinX:0.###}, {MinY:0.###}, {MinZ:0.###}] .. [{MaxX:0.###}, {MaxY:0.###}, {MaxZ:0.###}]\n"));
        sb.Append(string.Create(ci, $"Size:       {SizeX:0.###} x {SizeY:0.###} x {SizeZ:0.###}\n"));
        sb.Append(string.Create(ci, $"Area:       {SurfaceArea:0.####}\n"));
        sb.Append(string.Create(ci, $"Volume:     {Volume:0.######}\n"));
        sb.Append(string.Create(ci, $"Verdict:    {(IsClosedManifold ? "CLOSED 2-MANIFOLD (print-ready)" : "NOT print-ready")}\n"));
        return sb.ToString();
    }

    /// <summary>A short, plain-language "will this print?" verdict for the export
    /// dialog (roadmap S9, #391) — the headline, the specific issues in print terms,
    /// and the size / volume / triangle count. Deliberately text-only (no colour):
    /// the shell renders it as neutral dialog text, and colour-as-signal is avoided
    /// for accessibility.</summary>
    public string PrintReadiness()
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        if (IsClosedManifold)
        {
            sb.Append("Print check: PRINT-READY — closed, watertight, 2-manifold solid.\n");
        }
        else
        {
            sb.Append("Print check: NOT print-ready. Issues:\n");
            if (!IsWatertight)
                sb.Append(string.Create(ci, $"  - Has holes (not watertight): {BoundaryEdgeCount} open edge(s).\n"));
            if (!IsEdgeManifold)
                sb.Append(string.Create(ci, $"  - Non-manifold edges (3+ faces meet): {NonManifoldEdgeCount}.\n"));
            if (!IsConsistentlyOriented)
                sb.Append(string.Create(ci, $"  - Flipped / inconsistent faces: {FlippedEdgeCount} edge(s).\n"));
            sb.Append("  Many slicers auto-repair minor issues; otherwise adjust range / grid / iso and re-export.\n");
        }
        if (DegenerateTriangleCount > 0)
            sb.Append(string.Create(ci, $"  Note: {DegenerateTriangleCount} degenerate (zero-area) triangle(s) skipped.\n"));
        sb.Append(string.Create(ci, $"Size: {SizeX:0.###} x {SizeY:0.###} x {SizeZ:0.###}   "));
        sb.Append(string.Create(ci, $"Volume: {Volume:0.####}   Triangles: {TriangleCount:N0}"));
        return sb.ToString();
    }
}

/// <summary>Validates an exported triangle mesh against the 3D-print contract:
/// watertight (no holes), 2-manifold (no three-sheet edges), consistently wound
/// (no flipped faces). Welds coincident vertices by position so a triangle-soup
/// export (unshared vertices) is measured on its true topology. Pure — no I/O,
/// no state (roadmap S9.1, #391).</summary>
public static class MeshValidator
{
    /// <summary>Validate a triangle mesh given raw vertex positions and index
    /// triples into them. <paramref name="weldEpsilon"/> is the grid onto which
    /// coincident vertices are snapped before edge adjacency is computed; it
    /// should be well below the smallest real feature and above float noise
    /// (default 1e-6 world units). Degenerate triangles (a repeated welded corner)
    /// are counted and skipped for topology so they cannot masquerade as holes.</summary>
    public static MeshReport Validate(
        IReadOnlyList<(double X, double Y, double Z)> positions,
        IReadOnlyList<(int A, int B, int C)> triangles,
        double weldEpsilon = 1e-6)
    {
        if (positions == null) throw new ArgumentNullException(nameof(positions));
        if (triangles == null) throw new ArgumentNullException(nameof(triangles));
        double inv = weldEpsilon > 0 ? 1.0 / weldEpsilon : 1e6;

        // Weld: snap each position to the epsilon grid, map to a canonical id.
        var weld = new Dictionary<(long, long, long), int>(positions.Count);
        var canonical = new int[positions.Count];
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;
        for (int i = 0; i < positions.Count; i++)
        {
            var (x, y, z) = positions[i];
            if (x < minX) minX = x; if (y < minY) minY = y; if (z < minZ) minZ = z;
            if (x > maxX) maxX = x; if (y > maxY) maxY = y; if (z > maxZ) maxZ = z;
            var key = ((long)Math.Round(x * inv), (long)Math.Round(y * inv), (long)Math.Round(z * inv));
            if (!weld.TryGetValue(key, out int id)) { id = weld.Count; weld[key] = id; }
            canonical[i] = id;
        }
        if (positions.Count == 0) { minX = minY = minZ = maxX = maxY = maxZ = 0; }

        // Directed half-edge counts on the welded topology. Undirected incidence
        // = fwd+bwd; a consistently wound interior edge has fwd==bwd==1. `undirected`
        // holds each edge once as (lo,hi) so a boundary edge that exists only in the
        // hi→lo direction is still classified.
        var dir = new Dictionary<(int, int), int>(triangles.Count * 3);
        var undirected = new HashSet<(int, int)>();
        int degenerate = 0;
        double area = 0.0, vol6 = 0.0;

        foreach (var (a, b, c) in triangles)
        {
            if ((uint)a >= (uint)positions.Count || (uint)b >= (uint)positions.Count || (uint)c >= (uint)positions.Count)
                throw new ArgumentException("MeshValidator: triangle index out of range.");

            int ca = canonical[a], cb = canonical[b], cc = canonical[c];
            if (ca == cb || cb == cc || cc == ca) { degenerate++; continue; }

            // Geometry (raw positions — welding is topology-only).
            var (ax, ay, az) = positions[a];
            var (bx, by, bz) = positions[b];
            var (cx, cy, cz) = positions[c];
            double ux = bx - ax, uy = by - ay, uz = bz - az;
            double wx = cx - ax, wy = cy - ay, wz = cz - az;
            double crx = uy * wz - uz * wy, cry = uz * wx - ux * wz, crz = ux * wy - uy * wx;
            area += 0.5 * Math.Sqrt(crx * crx + cry * cry + crz * crz);
            vol6 += ax * (by * cz - bz * cy) - ay * (bx * cz - bz * cx) + az * (bx * cy - by * cx);

            AddDir(dir, undirected, ca, cb);
            AddDir(dir, undirected, cb, cc);
            AddDir(dir, undirected, cc, ca);
        }

        // Classify each undirected edge once. incidence = fwd + bwd across the two
        // directions; a consistently wound interior edge is fwd==bwd==1.
        int boundary = 0, nonManifold = 0, flipped = 0;
        foreach (var (lo, hi) in undirected)
        {
            dir.TryGetValue((lo, hi), out int fwd);
            dir.TryGetValue((hi, lo), out int bwd);
            int incidence = fwd + bwd;

            if (incidence == 1) boundary++;
            else if (incidence > 2) nonManifold++;
            else if (fwd != 1 || bwd != 1) flipped++;  // incidence 2 but same winding
        }

        return new MeshReport(
            TriangleCount: triangles.Count,
            DegenerateTriangleCount: degenerate,
            RawVertexCount: positions.Count,
            WeldedVertexCount: weld.Count,
            BoundaryEdgeCount: boundary,
            NonManifoldEdgeCount: nonManifold,
            FlippedEdgeCount: flipped,
            MinX: minX, MinY: minY, MinZ: minZ,
            MaxX: maxX, MaxY: maxY, MaxZ: maxZ,
            SurfaceArea: area,
            SignedVolume: vol6 / 6.0);
    }

    private static void AddDir(Dictionary<(int, int), int> dir, HashSet<(int, int)> undirected, int u, int v)
    {
        var k = (u, v);
        dir.TryGetValue(k, out int n);
        dir[k] = n + 1;
        undirected.Add(u < v ? (u, v) : (v, u));
    }
}
