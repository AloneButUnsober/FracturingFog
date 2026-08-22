// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Roadmap slice S9 dual contouring (3D-Rendering-Roadmap §S9, #391) — the
// sharp-feature mesher. Marching Cubes rounds hard creases onto grid edges; dual
// contouring places one QEF-solved vertex per cell so the vertex snaps onto the
// crease. These lock, through the readers + MeshValidator:
//   • a sphere (fully interior) DC's to a closed, 2-manifold, outward solid with
//     the right volume — the topology / winding is sound;
//   • on an L-infinity BOX (sharp edges + corners), DC puts a vertex much closer to
//     the true corner than MC does — the whole point of dual contouring;
//   • the colour + print-readiness plumbing rides the same writers as the MC path.

using System;
using System.IO;
using System.Linq;
using FracturingFog.Export;
using Xunit;

namespace FracturingFog.Server.Tests;

public class DualContouringTests
{
    private static SampleDistance Sphere(double r) => (x, y, z) => Math.Sqrt(x * x + y * y + z * z) - r;
    // L-infinity ball = an axis-aligned cube of half-size a, with sharp edges/corners.
    private static SampleDistance Box(double a) => (x, y, z) => Math.Max(Math.Abs(x), Math.Max(Math.Abs(y), Math.Abs(z))) - a;

    [Fact]
    public void Sphere_Dc_Is_Closed_Manifold_Outward_Solid()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ff-dc-{Guid.NewGuid():N}.stl");
        try
        {
            int tris = UserBulbMeshExporter.ExportDualContouring(path, Sphere(1.0), 0, 0, 0, 1.6, 48);
            Assert.True(tris > 0);
            var (pos, t) = StlMeshReader.ReadBinary(path);
            var r = MeshValidator.Validate(pos, t, weldEpsilon: 1e-5);
            Assert.True(r.IsClosedManifold, r.Summary());
            Assert.True(r.SignedVolume > 0, r.Summary());     // outward
            Assert.InRange(r.Volume, 3.5, 6.0);               // ~ 4/3 pi
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Box_Dc_Puts_A_Vertex_Nearer_The_Sharp_Corner_Than_Mc()
    {
        string dc = Path.Combine(Path.GetTempPath(), $"ff-dc-{Guid.NewGuid():N}.stl");
        string mc = Path.Combine(Path.GetTempPath(), $"ff-mc-{Guid.NewGuid():N}.stl");
        try
        {
            var box = Box(1.0);
            UserBulbMeshExporter.ExportDualContouring(dc, box, 0, 0, 0, 1.6, 48);
            UserBulbMeshExporter.ExportMarchingCubes(mc, box, 0, 0, 0, 1.6, 48);

            var (dcPos, dcT) = StlMeshReader.ReadBinary(dc);
            var (mcPos, _)   = StlMeshReader.ReadBinary(mc);

            // DC must still be a valid closed solid...
            var r = MeshValidator.Validate(dcPos, dcT, weldEpsilon: 1e-5);
            Assert.True(r.IsClosedManifold, r.Summary());
            Assert.True(r.SignedVolume > 0, r.Summary());
            Assert.InRange(r.Volume, 6.5, 9.5);               // ~ (2a)^3 = 8

            // ...and it snaps a vertex onto the true 3D corner. The surface sits at
            // the iso level (default = half a cell out), so the box corner is at
            // (a+iso)^3. DC's QEF places a cell vertex right there; MC only has
            // edge-crossing vertices, so its nearest vertex to the 3D corner is
            // ~half a cell off in two axes.
            double cell = 2.0 * 1.6 / 48;
            double corner = 1.0 + cell * 0.5;   // a + iso
            double DcMin = dcPos.Min(p => Dist(p, corner));
            double McMin = mcPos.Min(p => Dist(p, corner));
            Assert.True(DcMin < McMin, $"DC corner dist {DcMin:0.####} should beat MC {McMin:0.####}");
            Assert.True(DcMin < 0.02, $"DC should sit on the corner (got {DcMin:0.####})");
        }
        finally { if (File.Exists(dc)) File.Delete(dc); if (File.Exists(mc)) File.Delete(mc); }
    }

    [Fact]
    public void Dc_Carries_Color_And_Reports_Print_Ready()
    {
        SampleSurfaceColor gradient = (x, y, z, nx, ny, nz) =>
        {
            byte r = (byte)Math.Clamp((x + 1.0) / 2.0 * 255.0, 0, 255);
            byte g = (byte)Math.Clamp((y + 1.0) / 2.0 * 255.0, 0, 255);
            return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8);
        };
        string path = Path.Combine(Path.GetTempPath(), $"ff-dc-{Guid.NewGuid():N}.ply");
        try
        {
            MeshReport? rep = null;
            int tris = UserBulbMeshExporter.ExportDualContouring(
                path, Sphere(1.0), 0, 0, 0, 1.6, 48, sampleColor: gradient, onReport: r => rep = r);
            Assert.True(tris > 0);
            Assert.NotNull(rep);
            Assert.Contains("PRINT-READY", rep!.Value.PrintReadiness());

            var (_, colors, _) = PlyMeshReader.ReadBinary(path);
            int distinct = colors.Select(c => (c.R << 16) | (c.G << 8) | c.B).Distinct().Count();
            Assert.True(distinct > 8, $"expected a colour gradient, got {distinct}");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static double Dist((double X, double Y, double Z) p, double c)
    {
        double dx = p.X - c, dy = p.Y - c, dz = p.Z - c;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
