// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Export/MathPlane3DMeshExporter.cs
//
// #1718 (Math Plane 3D T6): export a scene's surfaces as a mesh through the S9 writers
// (#391): glTF 2.0 (.glb / .gltf, GltfMeshWriter) or binary STL. FF is a mesh exporter,
// not an editor (design plan §3.8).
//
// What is exported: every visible surface (explicit, parametric and implicit), after the
// map at the current t, which is what the view shows. Points, vectors, segments, curves,
// grids and fills are not surfaces and are left out.
//   - Explicit / parametric surfaces: the drawn cells of MathPlane3DCalculator.BuildSurface,
//     two triangles a cell, wound as the render winds them, over shared grid vertices, so a
//     full grid is one connected, consistently oriented sheet.
//   - Implicit surfaces: the marching-cubes mesh of MathPlane3DImplicitMesher (outward).
//   - Fractal surfaces (#1731): MathPlane3DFractalMesher's distance-estimate mesh.
//   - A map with det < 0 mirrors space, which would turn every face inside out; the
//     winding is reversed then, so a closed surface still faces outward.
//   - Normals: area-weighted per-vertex sums of the mapped triangles' normals (glTF).
//   - Colour: each surface's colour on its vertices. When every surface has the same colour
//     it is the glTF material's base colour instead (no COLOR_0). Alpha is dropped: the S9
//     writers export opaque. STL carries no colour.
//   - Axes: STL keeps the scene's z up (slicers expect Z up). glTF is Y up by its spec, so
//     (x, y, z) is written as (x, z, -y).
//
// Formats: Khronos, glTF 2.0 Specification v2.0.1 (2021), "+Y as up"; binary STL as described
// by the Library of Congress (fdd000505), after 3D Systems' StereoLithography Interface
// Specification. See Docs/Resources-Bibliography.md #khronos-gltf-2 and #loc-stl-binary.
//
// The result is checked with MeshValidator (closed, manifold, oriented) and the report is
// returned for the UI / batch to show.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FracturingFog.Models;

namespace FracturingFog.Export;

/// <summary>#1718 — the scene's surfaces as one indexed triangle mesh (scene axes, z up).</summary>
public sealed class MathPlane3DSceneMesh
{
    public List<(double X, double Y, double Z)> Positions { get; } = new();
    public List<(float X, float Y, float Z)> Normals { get; } = new();
    /// <summary>ARGB per vertex: the colour of the surface it belongs to.</summary>
    public List<uint> Colors { get; } = new();
    public List<(int A, int B, int C)> Triangles { get; } = new();
    /// <summary>Surfaces that contributed triangles.</summary>
    public int Surfaces { get; internal set; }
    /// <summary>True when an implicit surface hit the mesher's triangle cap.</summary>
    public bool Truncated { get; internal set; }
}

/// <summary>#1718 — what an export wrote.</summary>
public sealed record MathPlane3DMeshExportResult(string Path, int Surfaces, int Triangles, MeshReport Report, bool Truncated)
{
    /// <summary>One line for the UI / batch.</summary>
    public string Summary
    {
        get
        {
            string shape = Report.IsClosedManifold ? "closed"
                : !Report.IsEdgeManifold ? "not manifold"
                : !Report.IsConsistentlyOriented ? "inconsistently wound"
                : "open (has edges)";
            return $"{Surfaces} surface{(Surfaces == 1 ? "" : "s")}, {Triangles} triangles, {shape}"
                 + (Truncated ? ", an implicit surface hit the triangle cap" : "");
        }
    }
}

public static class MathPlane3DMeshExporter
{
    /// <summary>The file types written, by extension.</summary>
    public static readonly IReadOnlyList<string> Extensions = new[] { ".glb", ".gltf", ".stl" };

    public const string NoSurfacesMessage = "The scene has no visible surfaces to export.";

    /// <summary>True when <paramref name="path"/> ends in a written extension.</summary>
    public static bool IsSupportedPath(string? path)
        => path != null && Extensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

    /// <summary>The mesh of the scene in <paramref name="p"/>, after its map at the current t.
    /// A scene that does not parse yields an empty mesh.</summary>
    public static MathPlane3DSceneMesh Build(FractalParameters p, CancellationToken ct = default)
    {
        var map = MathPlane3DMap.Current(p);
        if (!IsFinite(map)) map = Mat3.Identity;   // as the calculator draws
        MathPlane3DScene.TryParse(p.MathPlane3DScene ?? "", out var scene, out _);
        return Build(scene, map, ct);
    }

    /// <summary>The mesh of <paramref name="scene"/>'s visible surfaces mapped by <paramref name="map"/>.</summary>
    public static MathPlane3DSceneMesh Build(MathPlane3DScene scene, Mat3 map, CancellationToken ct = default)
    {
        var mesh = new MathPlane3DSceneMesh();
        bool mirror = map.Det < 0;
        foreach (var o in scene.Objects)
        {
            ct.ThrowIfCancellationRequested();
            if (!o.Visible || o.Kind != MathPlaneObjectKind.Surface) continue;
            int before = mesh.Triangles.Count;
            if (o.CurveKind is MathPlaneCurveKind.Implicit or MathPlaneCurveKind.Fractal)
            {
                var im = o.CurveKind == MathPlaneCurveKind.Fractal
                    ? MathPlane3DFractalMesher.Build(o, out _, ct)   // #1731
                    : MathPlane3DImplicitMesher.Build(o, ct);
                if (im is null) continue;
                int b = mesh.Positions.Count;
                foreach (var v in im.Vertices) { mesh.Positions.Add(map.Apply(v)); mesh.Colors.Add(o.Color); }
                foreach (var (a, c, d) in im.Triangles) Add(mesh, b + a, b + c, b + d, mirror);
                mesh.Truncated |= im.Truncated;
            }
            else
            {
                if (MathPlane3DCalculator.BuildSurface(o) is not { } sm) continue;
                int n = sm.N;
                // Only the vertices of drawn cells, each once, shared by the cells round it.
                var index = new int[(n + 1) * (n + 1)];
                Array.Fill(index, -1);
                int V(int i, int j)
                {
                    int k = j * (n + 1) + i;
                    if (index[k] < 0)
                    {
                        index[k] = mesh.Positions.Count;
                        mesh.Positions.Add(map.Apply(sm.Vertices[k]));
                        mesh.Colors.Add(o.Color);
                    }
                    return index[k];
                }
                for (int j = 0; j < n; j++)
                    for (int i = 0; i < n; i++)
                    {
                        if (!sm.CellOk[j * n + i]) continue;
                        int p00 = V(i, j), p10 = V(i + 1, j), p01 = V(i, j + 1), p11 = V(i + 1, j + 1);
                        // The render's winding: (00, 10, 11) and (00, 11, 01).
                        Add(mesh, p00, p10, p11, mirror);
                        Add(mesh, p00, p11, p01, mirror);
                    }
            }
            if (mesh.Triangles.Count > before) mesh.Surfaces++;
        }
        ComputeNormals(mesh);
        return mesh;
    }

    /// <summary>Build and write the scene's mesh to <paramref name="path"/> (.glb, .gltf or .stl),
    /// then validate it. Throws <see cref="InvalidOperationException"/> when there is nothing to
    /// export (no file is written) and <see cref="ArgumentException"/> for another extension.</summary>
    public static MathPlane3DMeshExportResult Export(string path, FractalParameters p, CancellationToken ct = default)
    {
        if (!IsSupportedPath(path))
            throw new ArgumentException("A Math Plane 3D mesh is written as .glb, .gltf or .stl.", nameof(path));
        var mesh = Build(p, ct);
        if (mesh.Triangles.Count == 0) throw new InvalidOperationException(NoSurfacesMessage);
        ct.ThrowIfCancellationRequested();
        Write(path, mesh);
        var report = MeshValidator.Validate(mesh.Positions, mesh.Triangles);
        return new MathPlane3DMeshExportResult(path, mesh.Surfaces, mesh.Triangles.Count, report, mesh.Truncated);
    }

    /// <summary>Write <paramref name="mesh"/> by the extension of <paramref name="path"/>.</summary>
    public static void Write(string path, MathPlane3DSceneMesh mesh)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".stl")
        {
            UserBulbMeshExporter.WriteStlBinary(path, mesh.Positions, mesh.Triangles, "FracturingFog Math Plane 3D");
            return;
        }
        // glTF is Y up: (x, y, z) -> (x, z, -y), a rotation, so the winding is kept.
        var pos = mesh.Positions.Select(ToYUp).ToList();
        var nrm = mesh.Normals.Select(n => (n.X, n.Z, -n.Y)).ToList();
        bool oneColor = mesh.Colors.Count > 0 && mesh.Colors.All(c => (c & 0xFFFFFF) == (mesh.Colors[0] & 0xFFFFFF));
        var material = oneColor ? Material(mesh.Colors[0]) : GltfMeshWriter.PbrMaterial.MatteWhite;
        GltfMeshWriter.Write(path, pos, nrm, oneColor ? null : mesh.Colors, mesh.Triangles, material);
    }

    /// <summary>A scene point in glTF's Y-up axes.</summary>
    public static (double X, double Y, double Z) ToYUp((double X, double Y, double Z) p) => (p.X, p.Z, -p.Y);

    /// <summary>The matte material whose base colour is <paramref name="argb"/> (alpha dropped).</summary>
    public static GltfMeshWriter.PbrMaterial Material(uint argb)
        => GltfMeshWriter.PbrMaterial.Matte(((argb >> 16) & 0xFF) / 255f, ((argb >> 8) & 0xFF) / 255f, (argb & 0xFF) / 255f);

    private static void Add(MathPlane3DSceneMesh m, int a, int b, int c, bool mirror)
        => m.Triangles.Add(mirror ? (a, c, b) : (a, b, c));

    private static void ComputeNormals(MathPlane3DSceneMesh m)
    {
        var acc = new (double X, double Y, double Z)[m.Positions.Count];
        foreach (var (a, b, c) in m.Triangles)
        {
            var pa = m.Positions[a]; var pb = m.Positions[b]; var pc = m.Positions[c];
            double ux = pb.X - pa.X, uy = pb.Y - pa.Y, uz = pb.Z - pa.Z;
            double vx = pc.X - pa.X, vy = pc.Y - pa.Y, vz = pc.Z - pa.Z;
            var n = (uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx);   // |n| = 2 · area
            foreach (int k in new[] { a, b, c }) acc[k] = (acc[k].X + n.Item1, acc[k].Y + n.Item2, acc[k].Z + n.Item3);
        }
        foreach (var n in acc)
        {
            double len = Math.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);
            m.Normals.Add(len > 1e-300 ? ((float)(n.X / len), (float)(n.Y / len), (float)(n.Z / len)) : (0f, 0f, 1f));
        }
    }

    private static bool IsFinite(Mat3 m) => double.IsFinite(m.M11) && double.IsFinite(m.M12) && double.IsFinite(m.M13)
        && double.IsFinite(m.M21) && double.IsFinite(m.M22) && double.IsFinite(m.M23)
        && double.IsFinite(m.M31) && double.IsFinite(m.M32) && double.IsFinite(m.M33);
}
