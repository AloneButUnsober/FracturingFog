// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Calculators/MathPlane3DFractalMesher.cs
//
// #1731 (Math Plane 3D fractal bridge B1) — a 3D fractal as a Math Plane 3D surface:
//   fractal NAME TYPE [KEY=VALUE ...] [options] [mesh N]
// Design: Docs/Technical/MathPlane3D-FractalBridge-DesignPlan.md §3.1.
//
// The mesh is the S9 mesh export's (#391, #101): the object-space distance estimate of
// RaymarchMeshSampler.For (the render's DE) meshed by UserBulbMeshExporter's marching-cubes
// core on the cube [-r, r]³, r = SuggestedRange, with the export's defaults (iso half a cell,
// faces where the solid leaves the cube capped). Marching cubes: Lorensen & Cline, tables after
// Bourke (Docs/Resources-Bibliography.md #lorensen-cline-marching-cubes, #bourke-polygonise).
// The core's minimum grid is 8 cells a side, so mesh N below 8 meshes at 8.
//
// Settings: the KEY=VALUE pairs are --param keys (RegionFractalParams.FromKeyValues, the same
// parser --param uses) applied to default parameters. As with --param, a key of another family
// is accepted and changes nothing; an unknown key or a bad value is an error.
//
// Axes: the raymarchers' object space is y up; Math Plane 3D is z up. Every vertex goes through
// the rotation (x, y, z) -> (x, -z, y) (det 1, so the winding and the outward faces are kept),
// and the fractal stands as it does in its own view.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FracturingFog.Export;
using FracturingFog.Models;

namespace FracturingFog;

public static class MathPlane3DFractalMesher
{
    /// <summary>The raymarchers' y-up object space in Math Plane 3D's z-up axes.</summary>
    public static (double X, double Y, double Z) ToSceneAxes((double X, double Y, double Z) p) => (p.X, -p.Z, p.Y);

    /// <summary>The fractal's parameters and type, or false with the reason.</summary>
    public static bool TryParameters(MathPlaneObject o, out FractalType type, out FractalParameters p, out string? error)
    {
        type = default; p = new FractalParameters(); error = null;
        if (MathPlane3DScene.FractalTypeOf(o) is not { } t || !MeshExportCapabilities.IsMeshExportable(t))
        { error = $"fractal {o.Name}: not a 3D fractal type"; return false; }
        type = t;
        var keys = MathPlane3DScene.FractalKeys(o);
        if (keys.Count > 0)
        {
            var rp = RegionFractalParams.FromKeyValues(keys, t, out string? kerr);
            if (rp == null) { error = $"fractal {o.Name}: {kerr}"; return false; }
            rp.ApplyTo(p);
        }
        if (RaymarchMeshSampler.For(t, p) == null) { error = $"fractal {o.Name}: {t} has no distance estimate to mesh"; return false; }
        return true;
    }

    /// <summary>Null when <paramref name="o"/> is a valid fractal object, else the reason.</summary>
    public static string? Validate(MathPlaneObject o) => TryParameters(o, out _, out _, out var e) ? null : e;

    /// <summary>The mesh of a fractal object, in Math Plane 3D's axes; null with
    /// <paramref name="error"/> when its settings are not valid. Honours <paramref name="ct"/>.</summary>
    public static MathPlane3DImplicitMesh? Build(MathPlaneObject o, out string? error, CancellationToken ct = default)
    {
        if (!TryParameters(o, out var type, out var p, out error)) return null;
        var de = RaymarchMeshSampler.For(type, p)!;
        double range = RaymarchMeshSampler.SuggestedRange(type, p);
        int n = MathPlane3DScene.MeshOf(o);
        var (verts, _, tris) = UserBulbMeshExporter.BuildMarchingCubes(
            de.Evaluate, 0, 0, 0, range, n, isoScale: 0.5, isoAbsolute: false, superSamples: 1, capBoundary: true, ct);
        ct.ThrowIfCancellationRequested();
        var v = new (double X, double Y, double Z)[verts.Count];
        double extent = 0;
        for (int k = 0; k < v.Length; k++)
        {
            v[k] = ToSceneAxes(verts[k]);
            extent = Math.Max(extent, Math.Max(Math.Abs(v[k].X), Math.Max(Math.Abs(v[k].Y), Math.Abs(v[k].Z))));
        }
        return new MathPlane3DImplicitMesh
        {
            Vertices = v,
            Triangles = tris.ToArray(),
            N = Math.Max(8, n),
            Extent = extent,
        };
    }
}
