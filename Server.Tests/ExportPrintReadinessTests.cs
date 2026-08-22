// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S9 export-time "will this print?" check (3D-Rendering-Roadmap §S9,
// #391) — the exporters run MeshValidator on the written solid and hand the caller
// a MeshReport via an onReport callback; the shell shows MeshReport.PrintReadiness()
// as a plain-language verdict after export. These lock: the callback fires with a
// report whose triangle count matches the export, a closed solid reads PRINT-READY,
// and an open (undersized, uncapped) mesh reads NOT print-ready and names the holes.

using System;
using System.IO;
using FracturingFog.Export;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public class ExportPrintReadinessTests
{
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
            albedo[i] = 0xFF808080u;
        }
        return (albedo, height);
    }

    private static FractalParameters Relief() => new()
    {
        Relief2DEnabled = true, Relief2DRaymarch = true,
        Relief2DHeightScale = 1.4, Relief2DMeshHeight = 0.4,
    };

    private static SampleDistance Sphere(double r) => (x, y, z) => Math.Sqrt(x * x + y * y + z * z) - r;

    [Fact]
    public void Relief_Export_Reports_Print_Ready()
    {
        int w = 128, h = 96;
        var (albedo, height) = Bump(w, h);
        string path = Path.Combine(Path.GetTempPath(), $"ff-pr-{Guid.NewGuid():N}.stl");
        try
        {
            MeshReport? rep = null;
            int tris = HeightfieldMeshExporter.Export(albedo, height, w, h, Relief(), path,
                targetGrid: 60, onReport: r => rep = r);
            Assert.True(tris > 0);
            Assert.NotNull(rep);
            Assert.Equal(tris, rep!.Value.TriangleCount);
            Assert.True(rep.Value.IsClosedManifold, rep.Value.Summary());

            string readiness = rep.Value.PrintReadiness();
            Assert.Contains("PRINT-READY", readiness);
            Assert.Contains("Triangles:", readiness);
            Assert.Contains("Volume:", readiness);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Mc_Export_Reports_Print_Ready_When_Closed()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ff-pr-mc-{Guid.NewGuid():N}.stl");
        try
        {
            MeshReport? rep = null;
            int tris = UserBulbMeshExporter.ExportMarchingCubes(
                path, Sphere(1.0), 0, 0, 0, 1.6, 48, onReport: r => rep = r);
            Assert.True(tris > 0);
            Assert.NotNull(rep);
            Assert.Contains("PRINT-READY", rep!.Value.PrintReadiness());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Mc_Open_Mesh_Reports_Not_Print_Ready_And_Names_Holes()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ff-pr-mc-{Guid.NewGuid():N}.stl");
        try
        {
            MeshReport? rep = null;
            // Undersized cube with the boundary cap OFF → the surface exits the box
            // and the mesh has holes.
            UserBulbMeshExporter.ExportMarchingCubes(
                path, Sphere(1.8), 0, 0, 0, 1.6, 48, capBoundary: false, onReport: r => rep = r);
            Assert.NotNull(rep);
            Assert.False(rep!.Value.IsWatertight, rep.Value.Summary());

            string readiness = rep.Value.PrintReadiness();
            Assert.Contains("NOT print-ready", readiness);
            Assert.Contains("holes", readiness);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
