// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S9.4 MC vertex colour (3D-Rendering-Roadmap.md §S9, #391) — the
// Marching-Cubes isosurface (true-3D DE fractals) now bakes a per-vertex albedo,
// supplied as a view-independent SampleSurfaceColor delegate, so the exported solid
// carries the theme in the colour-capable formats (PLY, glTF COLOR_0) instead of a
// flat grey. These lock: a colour delegate lands varying colours in the PLY and the
// GLB (COLOR_0) while the geometry stays a closed, outward-wound solid; without a
// delegate the glTF is material-only (no COLOR_0).

using System;
using System.IO;
using System.Linq;
using System.Text;
using FracturingFog.Export;
using Xunit;

namespace FracturingFog.Server.Tests;

public class McVertexColorTests
{
    private static SampleDistance Sphere(double r) => (x, y, z) => Math.Sqrt(x * x + y * y + z * z) - r;

    // A LEFT->RIGHT / BOTTOM->TOP gradient so the baked colours vary across the
    // surface (a flat colour would pass "present" but not "theme carried").
    private static readonly SampleSurfaceColor Gradient = (x, y, z, nx, ny, nz) =>
    {
        byte r = (byte)Math.Clamp((x + 1.0) / 2.0 * 255.0, 0, 255);
        byte g = (byte)Math.Clamp((y + 1.0) / 2.0 * 255.0, 0, 255);
        return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | 0x20u;
    };

    [Fact]
    public void Ply_Carries_MC_Vertex_Color_On_A_Closed_Solid()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ff-mcply-{Guid.NewGuid():N}.ply");
        try
        {
            int tris = UserBulbMeshExporter.ExportMarchingCubes(
                path, Sphere(1.0), 0, 0, 0, 1.6, 48, sampleColor: Gradient);
            Assert.True(tris > 0);

            var (pos, colors, t) = PlyMeshReader.ReadBinary(path);
            Assert.Equal(tris, t.Count);

            var r = MeshValidator.Validate(pos, t, weldEpsilon: 1e-5);
            Assert.True(r.IsClosedManifold, r.Summary());
            Assert.True(r.SignedVolume > 0, r.Summary());

            int distinct = colors.Select(c => (c.R << 16) | (c.G << 8) | c.B).Distinct().Count();
            Assert.True(distinct > 8, $"expected a colour gradient, got {distinct}");
            Assert.Contains(colors, c => c.R > 32 || c.G > 32);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Glb_Carries_MC_Vertex_Color_As_Color0()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ff-mcglb-{Guid.NewGuid():N}.glb");
        try
        {
            int tris = UserBulbMeshExporter.ExportMarchingCubes(
                path, Sphere(1.0), 0, 0, 0, 1.6, 48, sampleColor: Gradient);
            Assert.True(tris > 0);

            string json = Encoding.UTF8.GetString(File.ReadAllBytes(path));
            Assert.Contains("COLOR_0", json);                 // vertex colour now present

            var (pos, colors, t) = GltfMeshReader.Read(path);
            Assert.Equal(tris, t.Count);
            var r = MeshValidator.Validate(pos, t, weldEpsilon: 1e-5);
            Assert.True(r.IsClosedManifold, r.Summary());
            Assert.True(r.SignedVolume > 0, r.Summary());

            int distinct = colors.Select(c => (c.R << 16) | (c.G << 8) | c.B).Distinct().Count();
            Assert.True(distinct > 8, $"expected a colour gradient, got {distinct}");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void No_Delegate_Leaves_Gltf_Material_Only()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ff-mcglb-{Guid.NewGuid():N}.glb");
        try
        {
            UserBulbMeshExporter.ExportMarchingCubes(path, Sphere(1.0), 0, 0, 0, 1.6, 48);
            string json = Encoding.UTF8.GetString(File.ReadAllBytes(path));
            Assert.DoesNotContain("COLOR_0", json);
            Assert.Contains("pbrMetallicRoughness", json);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
