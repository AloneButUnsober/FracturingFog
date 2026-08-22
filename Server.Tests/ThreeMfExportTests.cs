// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S9 3MF export (3D-Rendering-Roadmap.md §S9, #391) — the format
// colour slicers prefer over STL: it carries a real millimetre PRINT UNIT and
// PER-VERTEX COLOUR on a watertight solid. These lock, through ThreeMfMeshReader ->
// MeshValidator:
//   • the .3mf route produces the SAME closed, 2-manifold, outward-wound solid as
//     STL/OBJ/PLY/glTF;
//   • the package is a valid OPC ZIP (the three required parts present) declaring a
//     millimetre unit;
//   • the relief export carries the baked theme (colour group present, colours vary);
//   • the true-3D Marching-Cubes path also carries colour when a source is supplied.

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using FracturingFog.Export;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public class ThreeMfExportTests
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

    private static SampleDistance Sphere(double r) => (x, y, z) => Math.Sqrt(x * x + y * y + z * z) - r;

    [Fact]
    public void Relief_3mf_Is_Valid_Opc_Package_With_Mm_Unit()
    {
        int w = 128, h = 96;
        var (albedo, height) = ColoredBump(w, h);
        string path = Path.Combine(Path.GetTempPath(), $"ff-3mf-{Guid.NewGuid():N}.3mf");
        try
        {
            int tris = HeightfieldMeshExporter.Export(albedo, height, w, h, Relief(), path, targetGrid: 60);
            Assert.True(tris > 0);

            // OPC ZIP with the three required parts.
            using (var zip = ZipFile.OpenRead(path))
            {
                Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
                Assert.NotNull(zip.GetEntry("_rels/.rels"));
                Assert.NotNull(zip.GetEntry("3D/3dmodel.model"));
            }

            var (pos, colors, t, unit) = ThreeMfMeshReader.Read(path);
            Assert.Equal("millimeter", unit);
            Assert.Equal(tris, t.Count);

            var r = MeshValidator.Validate(pos, t, weldEpsilon: 1e-5);
            Assert.True(r.IsClosedManifold, r.Summary());
            Assert.True(r.SignedVolume > 0, r.Summary());   // outward

            int distinct = colors.Select(c => (c.R << 16) | (c.G << 8) | c.B).Distinct().Count();
            Assert.True(distinct > 8, $"expected a colour gradient, got {distinct}");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void MarchingCubes_3mf_Carries_Color_On_A_Closed_Solid()
    {
        SampleSurfaceColor gradient = (x, y, z, nx, ny, nz) =>
        {
            byte r = (byte)Math.Clamp((x + 1.0) / 2.0 * 255.0, 0, 255);
            byte g = (byte)Math.Clamp((y + 1.0) / 2.0 * 255.0, 0, 255);
            return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8);
        };
        string path = Path.Combine(Path.GetTempPath(), $"ff-3mf-mc-{Guid.NewGuid():N}.3mf");
        try
        {
            int tris = UserBulbMeshExporter.ExportMarchingCubes(
                path, Sphere(1.0), 0, 0, 0, 1.6, 48, sampleColor: gradient);
            Assert.True(tris > 0);

            var (pos, colors, t, unit) = ThreeMfMeshReader.Read(path);
            Assert.Equal("millimeter", unit);
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
    public void MarchingCubes_3mf_Without_Color_Is_Still_A_Valid_Solid()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ff-3mf-mc-{Guid.NewGuid():N}.3mf");
        try
        {
            int tris = UserBulbMeshExporter.ExportMarchingCubes(path, Sphere(1.0), 0, 0, 0, 1.6, 48);
            Assert.True(tris > 0);
            var (pos, _, t, unit) = ThreeMfMeshReader.Read(path);
            Assert.Equal("millimeter", unit);
            var r = MeshValidator.Validate(pos, t, weldEpsilon: 1e-5);
            Assert.True(r.IsClosedManifold, r.Summary());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
