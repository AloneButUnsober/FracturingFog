// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S9.4 (3D-Rendering-Roadmap.md §S9, #391) — carry the MATERIAL.
// glTF 2.0 lands the mesh in Blender / a web viewer already shaded (PBR
// metallic-roughness), and for the relief exporter it also carries the theme as
// per-vertex COLOR_0. These lock, through GltfMeshReader -> MeshValidator:
//   • the .glb / .gltf route produces the SAME closed, 2-manifold, outward-wound
//     solid as STL/OBJ/PLY;
//   • the GLB container is structurally valid (magic, version, JSON declares a
//     pbrMetallicRoughness material and a POSITION accessor with min/max);
//   • relief carries the baked colours (COLOR_0 present and varying);
//   • the true-3D Marching-Cubes path also exports a valid dressed solid (material
//     present, no vertex colour yet).

using System;
using System.IO;
using System.Linq;
using System.Text;
using FracturingFog.Export;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public class GltfPbrExportTests
{
    private static (uint[] albedo, float[] height) ColoredBump(int w, int h)
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
            byte rr = (byte)(255.0 * x / (w - 1));
            byte gg = (byte)(255.0 * y / (h - 1));
            albedo[i] = 0xFF000000u | ((uint)rr << 16) | ((uint)gg << 8) | 0x40u;
        }
        return (albedo, height);
    }

    private static FractalParameters Relief() => new()
    {
        Relief2DEnabled = true, Relief2DRaymarch = true,
        Relief2DHeightScale = 1.4, Relief2DMeshHeight = 0.4,
    };

    // Analytic sphere signed distance for the Marching-Cubes path.
    private static SampleDistance Sphere(double r) => (x, y, z) => Math.Sqrt(x * x + y * y + z * z) - r;

    private static string GlbJson(string path)
    {
        // The GLB JSON chunk is ASCII/UTF-8; pulling the whole file as text exposes
        // it for structural assertions without a full parser.
        return Encoding.UTF8.GetString(File.ReadAllBytes(path));
    }

    [Fact]
    public void Relief_Glb_Is_Valid_Closed_Solid_With_Color()
    {
        int w = 128, h = 96;
        var (albedo, height) = ColoredBump(w, h);
        string path = Path.Combine(Path.GetTempPath(), $"ff-gltf-{Guid.NewGuid():N}.glb");
        try
        {
            int tris = HeightfieldMeshExporter.Export(albedo, height, w, h, Relief(), path, targetGrid: 60);
            Assert.True(tris > 0);

            // GLB container header.
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(0x46546C67u, BitConverter.ToUInt32(bytes, 0));   // "glTF"
            Assert.Equal(2u, BitConverter.ToUInt32(bytes, 4));            // version 2
            Assert.Equal((uint)bytes.Length, BitConverter.ToUInt32(bytes, 8));

            string json = GlbJson(path);
            Assert.Contains("pbrMetallicRoughness", json);
            Assert.Contains("\"min\"", json);         // POSITION bounds (required)
            Assert.Contains("\"max\"", json);
            Assert.Contains("COLOR_0", json);

            var (pos, colors, t) = GltfMeshReader.Read(path);
            Assert.Equal(tris, t.Count);
            var r = MeshValidator.Validate(pos, t, weldEpsilon: 1e-5);
            Assert.True(r.IsClosedManifold, r.Summary());
            Assert.True(r.SignedVolume > 0, r.Summary());     // outward

            int distinct = colors.Select(c => (c.R << 16) | (c.G << 8) | c.B).Distinct().Count();
            Assert.True(distinct > 8, $"expected a colour gradient, got {distinct}");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Relief_Gltf_Embedded_Buffer_Round_Trips()
    {
        int w = 128, h = 96;
        var (albedo, height) = ColoredBump(w, h);
        string path = Path.Combine(Path.GetTempPath(), $"ff-gltf-{Guid.NewGuid():N}.gltf");
        try
        {
            int tris = HeightfieldMeshExporter.Export(albedo, height, w, h, Relief(), path, targetGrid: 60);
            Assert.True(tris > 0);

            string json = File.ReadAllText(path);
            Assert.StartsWith("{", json);
            Assert.Contains("data:application/octet-stream;base64,", json);   // inlined buffer

            var (pos, colors, t) = GltfMeshReader.Read(path);
            Assert.Equal(tris, t.Count);
            var r = MeshValidator.Validate(pos, t, weldEpsilon: 1e-5);
            Assert.True(r.IsClosedManifold, r.Summary());
            Assert.Contains(colors, c => c.R > 32 || c.G > 32);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void MarchingCubes_Glb_Is_Valid_Dressed_Solid()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ff-gltf-mc-{Guid.NewGuid():N}.glb");
        try
        {
            // Sphere radius 1 well inside a half-extent-1.6 cube -> closed.
            int tris = UserBulbMeshExporter.ExportMarchingCubes(path, Sphere(1.0), 0, 0, 0, 1.6, 48);
            Assert.True(tris > 0);

            string json = GlbJson(path);
            Assert.Contains("pbrMetallicRoughness", json);
            Assert.Contains("\"metallicFactor\"", json);
            Assert.DoesNotContain("COLOR_0", json);           // MC has no vertex colour yet

            var (pos, _, t) = GltfMeshReader.Read(path);
            Assert.Equal(tris, t.Count);
            var r = MeshValidator.Validate(pos, t, weldEpsilon: 1e-5);
            Assert.True(r.IsClosedManifold, r.Summary());
            Assert.True(r.SignedVolume > 0, r.Summary());     // outward
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
