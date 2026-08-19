// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S9.3 (3D-Rendering-Roadmap.md §S9, #391) — vertex-COLOUR export:
// the palette idiom crossing into mesh. The relief exporter already computes a
// per-vertex colour from the theme; PLY is the widely-read format that carries it
// (STL cannot, OBJ vertex colour is non-standard). These lock: the .ply route
// produces the SAME closed, 2-manifold, outward-wound solid as STL/OBJ (validated
// through PlyMeshReader → MeshValidator), the header advertises the colour
// properties, and the baked colours actually vary across the mesh (the theme is
// carried, not flat grey clay).

using System;
using System.IO;
using System.Linq;
using FracturingFog.Export;
using FracturingFog.Models;
using Xunit;

namespace FracturingFog.Server.Tests;

public class PlyVertexColorExportTests
{
    // A cone bump with a LEFT→RIGHT colour gradient, so the baked vertex colours
    // vary across the mesh (a flat colour would pass a "colours present" check but
    // not prove the theme is carried).
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

    [Fact]
    public void Ply_Header_Advertises_Color_Properties()
    {
        int w = 128, h = 96;
        var (albedo, height) = ColoredBump(w, h);
        string path = Path.Combine(Path.GetTempPath(), $"ff-ply-{Guid.NewGuid():N}.ply");
        try
        {
            int tris = HeightfieldMeshExporter.Export(albedo, height, w, h, Relief(), path, targetGrid: 60);
            Assert.True(tris > 0);
            // Read the ASCII header prefix.
            var head = new byte[512];
            using (var fs = File.OpenRead(path)) fs.ReadExactly(head, 0, Math.Min(512, (int)new FileInfo(path).Length));
            string header = System.Text.Encoding.ASCII.GetString(head);
            Assert.StartsWith("ply", header);
            Assert.Contains("format binary_little_endian", header);
            Assert.Contains("property uchar red", header);
            Assert.Contains("property uchar green", header);
            Assert.Contains("property uchar blue", header);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Ply_Export_Is_Closed_Manifold_Solid()
    {
        int w = 128, h = 96;
        var (albedo, height) = ColoredBump(w, h);
        string path = Path.Combine(Path.GetTempPath(), $"ff-ply-{Guid.NewGuid():N}.ply");
        try
        {
            int tris = HeightfieldMeshExporter.Export(albedo, height, w, h, Relief(), path, targetGrid: 60);
            var (pos, _, t) = PlyMeshReader.ReadBinary(path);
            Assert.Equal(tris, t.Count);

            var r = MeshValidator.Validate(pos, t);
            Assert.True(r.IsClosedManifold, r.Summary());       // same contract as STL/OBJ
            Assert.True(r.SignedVolume > 0, r.Summary());        // outward
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Ply_Carries_The_Baked_Theme_Colors()
    {
        int w = 128, h = 96;
        var (albedo, height) = ColoredBump(w, h);
        string path = Path.Combine(Path.GetTempPath(), $"ff-ply-{Guid.NewGuid():N}.ply");
        try
        {
            HeightfieldMeshExporter.Export(albedo, height, w, h, Relief(), path, targetGrid: 60);
            var (_, colors, _) = PlyMeshReader.ReadBinary(path);
            Assert.NotEmpty(colors);

            // Colours must VARY (theme carried), not be a single flat value.
            int distinct = colors.Select(c => (c.R << 16) | (c.G << 8) | c.B).Distinct().Count();
            Assert.True(distinct > 8, $"expected a colour gradient, got {distinct} distinct colours");
            // And they must not all be black/grey (real albedo baked).
            Assert.Contains(colors, c => c.R > 32 || c.G > 32);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
