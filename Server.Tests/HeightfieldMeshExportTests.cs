using System;
using System.IO;
using Xunit;
using FracturingFog.Export;
using FracturingFog.Models;

namespace FracturingFog.Server.Tests;

// #138 — export the Oblique 3D heightfield object as a watertight mesh.
public class HeightfieldMeshExportTests
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
            height[i] = d < r ? (float)(r - d) : 0f;      // cone bump, 0 outside
            albedo[i] = 0xFF3080C0u;
        }
        return (albedo, height);
    }

    [Fact]
    public void Exports_Obj_With_Vertices_And_Faces()
    {
        int w = 128, h = 96;
        var (albedo, height) = Bump(w, h);
        var p = new FractalParameters { Relief2DEnabled = true, Relief2DRaymarch = true, Relief2DHeightScale = 1.4 };
        string path = Path.Combine(Path.GetTempPath(), $"ff-relief-{Guid.NewGuid():N}.obj");
        try
        {
            int tris = HeightfieldMeshExporter.Export(albedo, height, w, h, p, path, targetGrid: 80);
            Assert.True(tris > 0, "no triangles exported");
            Assert.True(File.Exists(path));
            string text = File.ReadAllText(path);
            Assert.Contains("\nv ", "\n" + text);
            Assert.Contains("\nf ", "\n" + text);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Exports_Stl_With_Matching_Triangle_Count()
    {
        int w = 128, h = 96;
        var (albedo, height) = Bump(w, h);
        var p = new FractalParameters { Relief2DEnabled = true, Relief2DRaymarch = true, Relief2DHeightScale = 1.4 };
        string path = Path.Combine(Path.GetTempPath(), $"ff-relief-{Guid.NewGuid():N}.stl");
        try
        {
            int tris = HeightfieldMeshExporter.Export(albedo, height, w, h, p, path, targetGrid: 80);
            Assert.True(tris > 0);
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            br.ReadBytes(80);
            uint count = br.ReadUInt32();
            Assert.Equal((uint)tris, count);
            // 80 header + 4 count + 50 bytes/triangle.
            Assert.Equal(84 + 50L * tris, fs.Length);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Isolation_Reduces_Triangles()
    {
        int w = 128, h = 96;
        var (albedo, height) = Bump(w, h);
        var full = new FractalParameters { Relief2DEnabled = true, Relief2DRaymarch = true };
        var iso = full.Clone();
        iso.Relief2DIsolate = true;
        iso.Relief2DIsolateByDetail = true;
        iso.Relief2DDetailThreshold = 0.6;

        string p1 = Path.Combine(Path.GetTempPath(), $"ff-full-{Guid.NewGuid():N}.stl");
        string p2 = Path.Combine(Path.GetTempPath(), $"ff-iso-{Guid.NewGuid():N}.stl");
        try
        {
            int tFull = HeightfieldMeshExporter.Export(albedo, height, w, h, full, p1, 80);
            int tIso  = HeightfieldMeshExporter.Export(albedo, height, w, h, iso,  p2, 80);
            Assert.True(tFull > 0 && tIso > 0);
            Assert.True(tIso < tFull, $"isolation did not cull mesh: {tIso} vs {tFull}");
        }
        finally { foreach (var f in new[] { p1, p2 }) if (File.Exists(f)) File.Delete(f); }
    }
}
