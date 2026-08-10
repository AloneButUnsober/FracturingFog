// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Globalization;
using System.IO;
using Xunit;
using FracturingFog.Export;

namespace FracturingFog.Server.Tests;

// #112 follow-up — marching-cubes iso level. The historical iso = step·0.5
// places the surface a half-cell OUTSIDE the true DE≈0 shell, inflating the
// mesh; lowering isoScale must pull the surface back toward the real surface
// (the fix for the "ball with tubes" UserBulb export). Verified on a sphere DE
// whose iso-surface radius is exactly r + iso, so a lower iso ⇒ smaller mesh.
public class UserBulbMeshIsoLevelTests
{
    // Signed distance to the unit sphere: surface (DE = iso) sits at |p| = 1+iso.
    private static readonly SampleDistance Sphere =
        (x, y, z) => Math.Sqrt(x * x + y * y + z * z) - 1.0;

    private static double MaxVertexRadius(string objPath)
    {
        double max = 0.0;
        foreach (string line in File.ReadLines(objPath))
        {
            if (line.Length < 2 || line[0] != 'v' || line[1] != ' ') continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            double x = double.Parse(parts[1], CultureInfo.InvariantCulture);
            double y = double.Parse(parts[2], CultureInfo.InvariantCulture);
            double z = double.Parse(parts[3], CultureInfo.InvariantCulture);
            double r = Math.Sqrt(x * x + y * y + z * z);
            if (r > max) max = r;
        }
        return max;
    }

    [Fact]
    public void LowerIso_HugsSurface_TighterThanDefault()
    {
        const int n = 48;
        const double range = 2.0;
        double step = 2.0 * range / n;

        string lo = Path.Combine(Path.GetTempPath(), $"iso_lo_{Guid.NewGuid():N}.obj");
        string hi = Path.Combine(Path.GetTempPath(), $"iso_hi_{Guid.NewGuid():N}.obj");
        try
        {
            int trisLo = UserBulbMeshExporter.ExportMarchingCubes(lo, Sphere, 0, 0, 0, range, n, 0.1);
            int trisHi = UserBulbMeshExporter.ExportMarchingCubes(hi, Sphere, 0, 0, 0, range, n, 0.9);
            Assert.True(trisLo > 0 && trisHi > 0, "both exports should cross the surface");

            double rLo = MaxVertexRadius(lo);
            double rHi = MaxVertexRadius(hi);

            // Surface radius tracks 1 + iso; the low-iso mesh must be measurably
            // tighter than the high-iso (inflated) one, and near the true r=1.
            Assert.True(rLo < rHi - 0.02,
                $"low iso ({rLo:F4}) should hug tighter than high iso ({rHi:F4})");
            Assert.InRange(rLo, 1.0, 1.0 + 0.1 * step + 1e-6);
        }
        finally
        {
            if (File.Exists(lo)) File.Delete(lo);
            if (File.Exists(hi)) File.Delete(hi);
        }
    }

    [Fact]
    public void AbsoluteIso_IsGridIndependent()
    {
        const double range = 2.0;
        const double isoAbs = 0.2; // surface at |p| = 1 + 0.2 regardless of grid

        string coarse = Path.Combine(Path.GetTempPath(), $"iso_c_{Guid.NewGuid():N}.obj");
        string fine   = Path.Combine(Path.GetTempPath(), $"iso_f_{Guid.NewGuid():N}.obj");
        try
        {
            UserBulbMeshExporter.ExportMarchingCubes(coarse, Sphere, 0, 0, 0, range, 32, isoAbs, isoAbsolute: true);
            UserBulbMeshExporter.ExportMarchingCubes(fine,   Sphere, 0, 0, 0, range, 64, isoAbs, isoAbsolute: true);

            double rC = MaxVertexRadius(coarse);
            double rF = MaxVertexRadius(fine);

            // Both hug 1 + isoAbs, and differ only by grid discretisation — the
            // surface level does NOT move with the grid (unlike fraction mode).
            Assert.InRange(rC, 1.0 + isoAbs - 0.06, 1.0 + isoAbs + 0.06);
            Assert.InRange(rF, 1.0 + isoAbs - 0.06, 1.0 + isoAbs + 0.06);
            Assert.True(Math.Abs(rC - rF) < 0.05,
                $"absolute iso should be grid-independent (coarse {rC:F4} vs fine {rF:F4})");
        }
        finally
        {
            if (File.Exists(coarse)) File.Delete(coarse);
            if (File.Exists(fine)) File.Delete(fine);
        }
    }
}
