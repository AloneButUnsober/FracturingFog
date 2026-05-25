// Export/UserBulbMeshExporter.cs
//
// Mesh export from the User Bulb DE field. Samples the DE on a uniform N³
// grid inside a cube of side 2*range centered on the target. For each grid
// cell where the DE sign flips across the cell (surface present), emits a
// cube of triangles at the cell center. Output: ASCII OBJ.
//
// Quality vs marching cubes: blocky (voxel-style) surface, no normal
// smoothing. Adequate for printable export. Real Marching Cubes with the
// 256-entry triangulation table is a follow-up.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace FracturingFog.Export;

public delegate double SampleDistance(double x, double y, double z);

public static class UserBulbMeshExporter
{
    public static int ExportObjVoxelSurface(
        string filePath, SampleDistance sample,
        double cx, double cy, double cz, double range, int n,
        CancellationToken ct = default)
    {
        if (n < 8) n = 8;
        double step = 2.0 * range / n;
        double[,,] field = new double[n + 1, n + 1, n + 1];
        for (int i = 0; i <= n; i++)
        {
            if (ct.IsCancellationRequested) return 0;
            for (int j = 0; j <= n; j++)
            for (int k = 0; k <= n; k++)
            {
                double x = cx - range + i * step;
                double y = cy - range + j * step;
                double z = cz - range + k * step;
                field[i, j, k] = sample(x, y, z);
            }
        }

        var verts = new List<(double X, double Y, double Z)>();
        var tris = new List<(int A, int B, int C)>();
        double surfaceEps = step * 0.5;

        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
        for (int k = 0; k < n; k++)
        {
            double minD = double.PositiveInfinity, maxD = double.NegativeInfinity;
            for (int di = 0; di <= 1; di++)
            for (int dj = 0; dj <= 1; dj++)
            for (int dk = 0; dk <= 1; dk++)
            {
                double d = field[i + di, j + dj, k + dk];
                if (d < minD) minD = d;
                if (d > maxD) maxD = d;
            }
            // Surface present when min < eps AND max > eps (sign-flip near 0).
            if (minD < surfaceEps && maxD > surfaceEps)
            {
                double x0 = cx - range + i * step;
                double y0 = cy - range + j * step;
                double z0 = cz - range + k * step;
                AddVoxelCube(verts, tris, x0, y0, z0, step);
            }
        }

        WriteObj(filePath, verts, tris);
        return tris.Count;
    }

    private static void AddVoxelCube(
        List<(double, double, double)> verts,
        List<(int, int, int)> tris,
        double x, double y, double z, double s)
    {
        int b = verts.Count + 1; // OBJ is 1-indexed
        verts.Add((x, y, z));
        verts.Add((x + s, y, z));
        verts.Add((x + s, y + s, z));
        verts.Add((x, y + s, z));
        verts.Add((x, y, z + s));
        verts.Add((x + s, y, z + s));
        verts.Add((x + s, y + s, z + s));
        verts.Add((x, y + s, z + s));
        // 6 faces × 2 tris = 12 tris
        int v0 = b, v1 = b+1, v2 = b+2, v3 = b+3, v4 = b+4, v5 = b+5, v6 = b+6, v7 = b+7;
        tris.Add((v0, v2, v1)); tris.Add((v0, v3, v2)); // -Z
        tris.Add((v4, v5, v6)); tris.Add((v4, v6, v7)); // +Z
        tris.Add((v0, v1, v5)); tris.Add((v0, v5, v4)); // -Y
        tris.Add((v2, v3, v7)); tris.Add((v2, v7, v6)); // +Y
        tris.Add((v1, v2, v6)); tris.Add((v1, v6, v5)); // +X
        tris.Add((v0, v4, v7)); tris.Add((v0, v7, v3)); // -X
    }

    private static void WriteObj(
        string filePath,
        List<(double X, double Y, double Z)> verts,
        List<(int A, int B, int C)> tris)
    {
        using var w = new StreamWriter(filePath);
        var inv = CultureInfo.InvariantCulture;
        w.WriteLine("# FracturingFog UserBulb mesh export");
        foreach (var v in verts)
            w.WriteLine($"v {v.X.ToString("G7", inv)} {v.Y.ToString("G7", inv)} {v.Z.ToString("G7", inv)}");
        foreach (var t in tris)
            w.WriteLine($"f {t.A} {t.B} {t.C}");
    }
}
