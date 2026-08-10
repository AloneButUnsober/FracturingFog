// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Export/UserBulbMeshExporter.cs
//
// Mesh export from the User Bulb DE field.
//
// Two output paths:
//   • Marching Cubes (default, .obj smooth or .stl binary).
//     Samples DE on a uniform (n+1)³ grid in a cube of side 2·range
//     centered on (cx,cy,cz). Classic Lorensen-Cline MC with the
//     256-entry edge + tri tables (Paul Bourke layout) and per-edge
//     linear-interp vertex placement against an iso-level of
//     step·0.5 (matches the band the raymarcher considers surface).
//     Per-vertex normals are accumulated from incident triangle face
//     normals and normalised; OBJ emits `v` + `vn` + `f a//a` lines,
//     binary STL emits one face normal per tri (STL has no smoothing).
//
//   • Voxel cubes (legacy, .obj only).
//     For cells whose corner DE values straddle the iso-band, emit a
//     full unit cube of 12 tris at the cell center. Blocky but cheap;
//     kept for parity w/ the original 4.12 shipment.
//
// Vertex dedup: each MC edge is owned by the lower-coordinate of its
// two corners + an axis (X/Y/Z). For an n³ grid that's
// (n+1)³·3 candidate edges; we map each to a vertex index once.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace FracturingFog.Export;

public delegate double SampleDistance(double x, double y, double z);

public static class UserBulbMeshExporter
{
    // ── Public API ──────────────────────────────────────────────────────────

    /// <summary>Marching Cubes export from any <see cref="FracturingFog.Rendering.Lighting.IDistanceEstimator"/>
    /// (#101 — the whole raymarcher family, not just the User Bulb). Thin
    /// adapter over the <see cref="SampleDistance"/> overload.</summary>
    public static int ExportMarchingCubes(
        string filePath, FracturingFog.Rendering.Lighting.IDistanceEstimator de,
        double cx, double cy, double cz, double range, int n,
        double isoScale = 0.5, bool isoAbsolute = false, int superSamples = 1,
        CancellationToken ct = default)
        => ExportMarchingCubes(filePath, de.Evaluate, cx, cy, cz, range, n, isoScale, isoAbsolute, superSamples, ct);

    /// <summary>Marching Cubes export. Dispatches on file extension:
    /// `.stl` → binary STL (face normals); anything else → OBJ with
    /// smooth per-vertex normals.
    /// <paramref name="isoScale"/> sets the iso-surface level. When
    /// <paramref name="isoAbsolute"/> is false (default) it is a fraction of the
    /// cell size (iso = step·isoScale), so the surface level tracks the grid; the
    /// historical 0.5 places the surface a half-cell OUTSIDE the true DE≈0 shell,
    /// which inflates thin filaments into fat tubes and fuses gaps into a ball at
    /// coarse grids. When <paramref name="isoAbsolute"/> is true, isoScale is the
    /// iso level directly in object-space distance units — grid-independent, so
    /// changing the grid does not move the surface. Lower (fraction ≈0.1–0.25, or
    /// a small absolute distance) hugs the true surface and keeps filament detail;
    /// raise it to bridge gaps if the mesh comes out shattered.
    /// <paramref name="superSamples"/> box-averages an s×s×s stencil of the DE
    /// per grid corner (1 = single sample, the default). Filaments thinner than a
    /// cell alias into broken tubes/dots when point-sampled; averaging antialiases
    /// them into continuous arms. Cost is ~s³× the DE evaluations, so keep it low
    /// (2–3) on fine grids.</summary>
    public static int ExportMarchingCubes(
        string filePath, SampleDistance sample,
        double cx, double cy, double cz, double range, int n,
        double isoScale = 0.5, bool isoAbsolute = false, int superSamples = 1,
        CancellationToken ct = default)
    {
        var (verts, norms, tris) = BuildMarchingCubes(sample, cx, cy, cz, range, n, isoScale, isoAbsolute, superSamples, ct);
        if (tris.Count == 0) { File.WriteAllText(filePath, "# empty\n"); return 0; }
        if (filePath.EndsWith(".stl", StringComparison.OrdinalIgnoreCase))
            WriteStlBinary(filePath, verts, tris);
        else
            WriteObjSmooth(filePath, verts, norms, tris);
        return tris.Count;
    }

    /// <summary>Probe the object-space half-extent that encloses the set, so the
    /// export cube can be auto-sized instead of hand-tuned (too small clips the
    /// fractal; too large wastes grid resolution and can leave the mesh open where
    /// the surface exits the cube face). Casts <paramref name="dirs"/> rays on a
    /// Fibonacci sphere from the centre and records the farthest radius along each
    /// where the DE is at/inside the surface, then pads by a margin. Returns 0
    /// when no surface is found (empty/degenerate field) — the caller should keep
    /// the current range and warn.</summary>
    public static double ProbeBoundingRange(
        SampleDistance sample, double cx, double cy, double cz,
        double maxRange = 8.0, double threshold = 0.0,
        int dirs = 64, int steps = 256, CancellationToken ct = default)
    {
        if (maxRange <= 0.0) maxRange = 8.0;
        if (dirs < 8) dirs = 8;
        if (steps < 16) steps = 16;
        double dt = maxRange / steps;
        // Surface = DE at/below the iso band; approximate with ~one step so the
        // probe brackets the true surface. The 20% margin below absorbs the slack.
        double thr = threshold > 0.0 ? threshold : dt;
        double extent = 0.0;
        double golden = Math.PI * (3.0 - Math.Sqrt(5.0)); // golden angle
        for (int d = 0; d < dirs; d++)
        {
            if (ct.IsCancellationRequested) break;
            // Fibonacci-sphere direction (near-uniform coverage).
            double zc = 1.0 - 2.0 * (d + 0.5) / dirs;
            double rr = Math.Sqrt(Math.Max(0.0, 1.0 - zc * zc));
            double phi = golden * d;
            double ux = Math.Cos(phi) * rr, uy = Math.Sin(phi) * rr, uz = zc;
            double lastHit = 0.0;
            for (int s = 1; s <= steps; s++)
            {
                double t = s * dt;
                double dval = sample(cx + ux * t, cy + uy * t, cz + uz * t);
                if (dval <= thr) lastHit = t;
            }
            if (lastHit > extent) extent = lastHit;
        }
        if (extent <= 0.0) return 0.0;
        return Math.Clamp(extent * 1.2 + dt, 0.25, maxRange);
    }

    /// <summary>Legacy voxel-cube OBJ export. Kept for parity with
    /// pre-4.12 shipment + as a fallback when MC topology gets pinched
    /// at very low N.</summary>
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
            if (minD < surfaceEps && maxD > surfaceEps)
            {
                double x0 = cx - range + i * step;
                double y0 = cy - range + j * step;
                double z0 = cz - range + k * step;
                AddVoxelCube(verts, tris, x0, y0, z0, step);
            }
        }

        WriteObjPlain(filePath, verts, tris);
        return tris.Count;
    }

    // ── Marching Cubes core ─────────────────────────────────────────────────

    private static (List<(double X, double Y, double Z)> verts,
                    List<(double X, double Y, double Z)> norms,
                    List<(int A, int B, int C)> tris)
        BuildMarchingCubes(SampleDistance sample,
                           double cx, double cy, double cz, double range, int n,
                           double isoScale, bool isoAbsolute, int superSamples,
                           CancellationToken ct)
    {
        if (n < 8) n = 8;
        double step = 2.0 * range / n;

        // Corner sampler: single point (ss<=1) or a box-averaged s×s×s stencil
        // spanning the corner's cell (±half a step), to antialias sub-cell
        // filaments into continuous surface instead of broken tubes.
        int ss = Math.Clamp(superSamples, 1, 4);
        double SampleCorner(double x, double y, double z)
        {
            if (ss <= 1) return sample(x, y, z);
            double h = 0.5 * step, span = 2.0 * h / ss;
            double acc = 0.0;
            for (int ax = 0; ax < ss; ax++)
            for (int ay = 0; ay < ss; ay++)
            for (int az = 0; az < ss; az++)
                acc += sample(x - h + (ax + 0.5) * span,
                              y - h + (ay + 0.5) * span,
                              z - h + (az + 0.5) * span);
            return acc / (ss * ss * ss);
        }

        // Iso level: absolute object-space distance (grid-independent) or a
        // fraction of the cell size (tracks the grid). Both clamped positive and
        // below the sampled half-extent so the surface stays inside the cube.
        double iso = isoAbsolute
            ? Math.Clamp(isoScale, 1e-6, range)
            : step * Math.Clamp(isoScale, 0.02, 1.0);
        int side = n + 1;
        var field = new double[side * side * side];
        for (int i = 0; i < side; i++)
        {
            if (ct.IsCancellationRequested) goto done_sample;
            for (int j = 0; j < side; j++)
            for (int k = 0; k < side; k++)
            {
                double x = cx - range + i * step;
                double y = cy - range + j * step;
                double z = cz - range + k * step;
                field[(i * side + j) * side + k] = SampleCorner(x, y, z);
            }
        }
        done_sample:;

        var verts = new List<(double, double, double)>();
        var tris = new List<(int, int, int)>();

        // Edge ownership: each grid corner (i,j,k) owns up to 3 edges,
        // along +X / +Y / +Z. Pack (i,j,k,axis) → linear index into
        // a single -1-seeded array. Size: side³·3.
        var edgeVert = new int[side * side * side * 3];
        Array.Fill(edgeVert, -1);

        // Per-cell corner offsets matching Bourke's vertex order:
        //   0:(0,0,0) 1:(1,0,0) 2:(1,1,0) 3:(0,1,0)
        //   4:(0,0,1) 5:(1,0,1) 6:(1,1,1) 7:(0,1,1)
        // Edges (axis, low-corner offset):
        //   0:(X,0) 1:(Y,1) 2:(X,3) 3:(Y,0)
        //   4:(X,4) 5:(Y,5) 6:(X,7) 7:(Y,4)
        //   8:(Z,0) 9:(Z,1)10:(Z,2)11:(Z,3)
        // where "axis" picks which of +X/+Y/+Z the edge runs along, and
        // "low-corner offset" selects from the 8-vertex list above.

        Span<int> edgeIdx = stackalloc int[12];
        for (int i = 0; i < n; i++)
        {
            if (ct.IsCancellationRequested) goto done_cells;
            for (int j = 0; j < n; j++)
            for (int k = 0; k < n; k++)
            {
                double v0 = field[((i + 0) * side + (j + 0)) * side + (k + 0)];
                double v1 = field[((i + 1) * side + (j + 0)) * side + (k + 0)];
                double v2 = field[((i + 1) * side + (j + 1)) * side + (k + 0)];
                double v3 = field[((i + 0) * side + (j + 1)) * side + (k + 0)];
                double v4 = field[((i + 0) * side + (j + 0)) * side + (k + 1)];
                double v5 = field[((i + 1) * side + (j + 0)) * side + (k + 1)];
                double v6 = field[((i + 1) * side + (j + 1)) * side + (k + 1)];
                double v7 = field[((i + 0) * side + (j + 1)) * side + (k + 1)];

                int ci = 0;
                if (v0 < iso) ci |= 1;
                if (v1 < iso) ci |= 2;
                if (v2 < iso) ci |= 4;
                if (v3 < iso) ci |= 8;
                if (v4 < iso) ci |= 16;
                if (v5 < iso) ci |= 32;
                if (v6 < iso) ci |= 64;
                if (v7 < iso) ci |= 128;

                int em = EdgeTable[ci];
                if (em == 0) continue;

                double x0 = cx - range + i * step;
                double y0 = cy - range + j * step;
                double z0 = cz - range + k * step;

                // 0: X-edge from (0,0,0) → (1,0,0).
                if ((em & 1)    != 0) edgeIdx[0]  = GetOrCreateEdgeVert(edgeVert, verts, side, i,   j,   k,   0, x0,        y0,        z0,        x0 + step, y0,        z0,        v0, v1, iso);
                if ((em & 2)    != 0) edgeIdx[1]  = GetOrCreateEdgeVert(edgeVert, verts, side, i+1, j,   k,   1, x0 + step, y0,        z0,        x0 + step, y0 + step, z0,        v1, v2, iso);
                if ((em & 4)    != 0) edgeIdx[2]  = GetOrCreateEdgeVert(edgeVert, verts, side, i,   j+1, k,   0, x0,        y0 + step, z0,        x0 + step, y0 + step, z0,        v3, v2, iso);
                if ((em & 8)    != 0) edgeIdx[3]  = GetOrCreateEdgeVert(edgeVert, verts, side, i,   j,   k,   1, x0,        y0,        z0,        x0,        y0 + step, z0,        v0, v3, iso);
                if ((em & 16)   != 0) edgeIdx[4]  = GetOrCreateEdgeVert(edgeVert, verts, side, i,   j,   k+1, 0, x0,        y0,        z0 + step, x0 + step, y0,        z0 + step, v4, v5, iso);
                if ((em & 32)   != 0) edgeIdx[5]  = GetOrCreateEdgeVert(edgeVert, verts, side, i+1, j,   k+1, 1, x0 + step, y0,        z0 + step, x0 + step, y0 + step, z0 + step, v5, v6, iso);
                if ((em & 64)   != 0) edgeIdx[6]  = GetOrCreateEdgeVert(edgeVert, verts, side, i,   j+1, k+1, 0, x0,        y0 + step, z0 + step, x0 + step, y0 + step, z0 + step, v7, v6, iso);
                if ((em & 128)  != 0) edgeIdx[7]  = GetOrCreateEdgeVert(edgeVert, verts, side, i,   j,   k+1, 1, x0,        y0,        z0 + step, x0,        y0 + step, z0 + step, v4, v7, iso);
                if ((em & 256)  != 0) edgeIdx[8]  = GetOrCreateEdgeVert(edgeVert, verts, side, i,   j,   k,   2, x0,        y0,        z0,        x0,        y0,        z0 + step, v0, v4, iso);
                if ((em & 512)  != 0) edgeIdx[9]  = GetOrCreateEdgeVert(edgeVert, verts, side, i+1, j,   k,   2, x0 + step, y0,        z0,        x0 + step, y0,        z0 + step, v1, v5, iso);
                if ((em & 1024) != 0) edgeIdx[10] = GetOrCreateEdgeVert(edgeVert, verts, side, i+1, j+1, k,   2, x0 + step, y0 + step, z0,        x0 + step, y0 + step, z0 + step, v2, v6, iso);
                if ((em & 2048) != 0) edgeIdx[11] = GetOrCreateEdgeVert(edgeVert, verts, side, i,   j+1, k,   2, x0,        y0 + step, z0,        x0,        y0 + step, z0 + step, v3, v7, iso);

                for (int t = 0; TriTable[ci, t] != -1; t += 3)
                {
                    int a = edgeIdx[TriTable[ci, t + 0]];
                    int b = edgeIdx[TriTable[ci, t + 1]];
                    int c = edgeIdx[TriTable[ci, t + 2]];
                    if (a == b || b == c || a == c) continue;
                    tris.Add((a, b, c));
                }
            }
        }
        done_cells:;

        // Smooth normals: accumulate face normal at each incident vertex.
        var norms = new List<(double X, double Y, double Z)>(verts.Count);
        for (int v = 0; v < verts.Count; v++) norms.Add((0, 0, 0));
        for (int t = 0; t < tris.Count; t++)
        {
            var (a, b, c) = tris[t];
            var (ax, ay, az) = verts[a];
            var (bx, by, bz) = verts[b];
            var (cxv, cyv, czv) = verts[c];
            double ex = bx - ax, ey = by - ay, ez = bz - az;
            double fx = cxv - ax, fy = cyv - ay, fz = czv - az;
            // n = e × f, magnitude proportional to 2·area → natural
            // area weighting for accumulation.
            double nx = ey * fz - ez * fy;
            double ny = ez * fx - ex * fz;
            double nz = ex * fy - ey * fx;
            var na = norms[a]; norms[a] = (na.X + nx, na.Y + ny, na.Z + nz);
            var nb = norms[b]; norms[b] = (nb.X + nx, nb.Y + ny, nb.Z + nz);
            var nc = norms[c]; norms[c] = (nc.X + nx, nc.Y + ny, nc.Z + nz);
        }
        for (int v = 0; v < norms.Count; v++)
        {
            var (nx, ny, nz) = norms[v];
            double L = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (L > 1e-20) norms[v] = (nx / L, ny / L, nz / L);
            else           norms[v] = (0, 0, 1);
        }
        return (verts, norms, tris);
    }

    private static int GetOrCreateEdgeVert(
        int[] edgeVert, List<(double, double, double)> verts, int side,
        int i, int j, int k, int axis,
        double ax, double ay, double az, double bx, double by, double bz,
        double va, double vb, double iso)
    {
        int key = ((i * side + j) * side + k) * 3 + axis;
        int existing = edgeVert[key];
        if (existing >= 0) return existing;
        // Linear interp toward iso. Guard div-zero when corners exactly
        // tie; clamp t to [0,1] so degenerate cases don't shoot a vertex
        // out of the cell.
        double t;
        double denom = vb - va;
        if (Math.Abs(denom) < 1e-20) t = 0.5;
        else { t = (iso - va) / denom; if (t < 0) t = 0; else if (t > 1) t = 1; }
        double x = ax + t * (bx - ax);
        double y = ay + t * (by - ay);
        double z = az + t * (bz - az);
        int idx = verts.Count;
        verts.Add((x, y, z));
        edgeVert[key] = idx;
        return idx;
    }

    // ── Writers ─────────────────────────────────────────────────────────────

    private static void WriteObjSmooth(
        string filePath,
        List<(double X, double Y, double Z)> verts,
        List<(double X, double Y, double Z)> norms,
        List<(int A, int B, int C)> tris)
    {
        using var w = new StreamWriter(filePath);
        var inv = CultureInfo.InvariantCulture;
        w.WriteLine("# FracturingFog UserBulb mesh export (Marching Cubes, smooth normals)");
        foreach (var v in verts)
            w.WriteLine($"v {v.X.ToString("G7", inv)} {v.Y.ToString("G7", inv)} {v.Z.ToString("G7", inv)}");
        foreach (var n in norms)
            w.WriteLine($"vn {n.X.ToString("G7", inv)} {n.Y.ToString("G7", inv)} {n.Z.ToString("G7", inv)}");
        foreach (var t in tris)
        {
            int a = t.A + 1, b = t.B + 1, c = t.C + 1; // OBJ is 1-indexed
            w.WriteLine($"f {a}//{a} {b}//{b} {c}//{c}");
        }
    }

    private static void WriteObjPlain(
        string filePath,
        List<(double X, double Y, double Z)> verts,
        List<(int A, int B, int C)> tris)
    {
        using var w = new StreamWriter(filePath);
        var inv = CultureInfo.InvariantCulture;
        w.WriteLine("# FracturingFog UserBulb mesh export (voxel)");
        foreach (var v in verts)
            w.WriteLine($"v {v.X.ToString("G7", inv)} {v.Y.ToString("G7", inv)} {v.Z.ToString("G7", inv)}");
        foreach (var t in tris)
            w.WriteLine($"f {t.A} {t.B} {t.C}");
    }

    private static void WriteStlBinary(
        string filePath,
        List<(double X, double Y, double Z)> verts,
        List<(int A, int B, int C)> tris)
    {
        using var fs = File.Create(filePath);
        using var bw = new BinaryWriter(fs);
        // 80-byte header.
        var header = new byte[80];
        var tag = System.Text.Encoding.ASCII.GetBytes("FracturingFog UserBulb MC");
        Buffer.BlockCopy(tag, 0, header, 0, Math.Min(tag.Length, 80));
        bw.Write(header);
        bw.Write((uint)tris.Count);
        foreach (var t in tris)
        {
            var (ax, ay, az) = verts[t.A];
            var (bx, by, bz) = verts[t.B];
            var (cxv, cyv, czv) = verts[t.C];
            double ex = bx - ax, ey = by - ay, ez = bz - az;
            double fx = cxv - ax, fy = cyv - ay, fz = czv - az;
            double nx = ey * fz - ez * fy;
            double ny = ez * fx - ex * fz;
            double nz = ex * fy - ey * fx;
            double L = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (L > 1e-20) { nx /= L; ny /= L; nz /= L; } else { nx = 0; ny = 0; nz = 1; }
            bw.Write((float)nx); bw.Write((float)ny); bw.Write((float)nz);
            bw.Write((float)ax); bw.Write((float)ay); bw.Write((float)az);
            bw.Write((float)bx); bw.Write((float)by); bw.Write((float)bz);
            bw.Write((float)cxv); bw.Write((float)cyv); bw.Write((float)czv);
            bw.Write((ushort)0); // attribute byte count
        }
    }

    // ── Voxel-cube helper (legacy path) ─────────────────────────────────────

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
        int v0 = b, v1 = b+1, v2 = b+2, v3 = b+3, v4 = b+4, v5 = b+5, v6 = b+6, v7 = b+7;
        tris.Add((v0, v2, v1)); tris.Add((v0, v3, v2)); // -Z
        tris.Add((v4, v5, v6)); tris.Add((v4, v6, v7)); // +Z
        tris.Add((v0, v1, v5)); tris.Add((v0, v5, v4)); // -Y
        tris.Add((v2, v3, v7)); tris.Add((v2, v7, v6)); // +Y
        tris.Add((v1, v2, v6)); tris.Add((v1, v6, v5)); // +X
        tris.Add((v0, v4, v7)); tris.Add((v0, v7, v3)); // -X
    }

    // ── Lorensen-Cline tables (Paul Bourke layout) ──────────────────────────

    private static readonly int[] EdgeTable = new int[256]
    {
        0x0  , 0x109, 0x203, 0x30a, 0x406, 0x50f, 0x605, 0x70c,
        0x80c, 0x905, 0xa0f, 0xb06, 0xc0a, 0xd03, 0xe09, 0xf00,
        0x190, 0x99 , 0x393, 0x29a, 0x596, 0x49f, 0x795, 0x69c,
        0x99c, 0x895, 0xb9f, 0xa96, 0xd9a, 0xc93, 0xf99, 0xe90,
        0x230, 0x339, 0x33 , 0x13a, 0x636, 0x73f, 0x435, 0x53c,
        0xa3c, 0xb35, 0x83f, 0x936, 0xe3a, 0xf33, 0xc39, 0xd30,
        0x3a0, 0x2a9, 0x1a3, 0xaa , 0x7a6, 0x6af, 0x5a5, 0x4ac,
        0xbac, 0xaa5, 0x9af, 0x8a6, 0xfaa, 0xea3, 0xda9, 0xca0,
        0x460, 0x569, 0x663, 0x76a, 0x66 , 0x16f, 0x265, 0x36c,
        0xc6c, 0xd65, 0xe6f, 0xf66, 0x86a, 0x963, 0xa69, 0xb60,
        0x5f0, 0x4f9, 0x7f3, 0x6fa, 0x1f6, 0xff , 0x3f5, 0x2fc,
        0xdfc, 0xcf5, 0xfff, 0xef6, 0x9fa, 0x8f3, 0xbf9, 0xaf0,
        0x650, 0x759, 0x453, 0x55a, 0x256, 0x35f, 0x55 , 0x15c,
        0xe5c, 0xf55, 0xc5f, 0xd56, 0xa5a, 0xb53, 0x859, 0x950,
        0x7c0, 0x6c9, 0x5c3, 0x4ca, 0x3c6, 0x2cf, 0x1c5, 0xcc ,
        0xfcc, 0xec5, 0xdcf, 0xcc6, 0xbca, 0xac3, 0x9c9, 0x8c0,
        0x8c0, 0x9c9, 0xac3, 0xbca, 0xcc6, 0xdcf, 0xec5, 0xfcc,
        0xcc , 0x1c5, 0x2cf, 0x3c6, 0x4ca, 0x5c3, 0x6c9, 0x7c0,
        0x950, 0x859, 0xb53, 0xa5a, 0xd56, 0xc5f, 0xf55, 0xe5c,
        0x15c, 0x55 , 0x35f, 0x256, 0x55a, 0x453, 0x759, 0x650,
        0xaf0, 0xbf9, 0x8f3, 0x9fa, 0xef6, 0xfff, 0xcf5, 0xdfc,
        0x2fc, 0x3f5, 0xff , 0x1f6, 0x6fa, 0x7f3, 0x4f9, 0x5f0,
        0xb60, 0xa69, 0x963, 0x86a, 0xf66, 0xe6f, 0xd65, 0xc6c,
        0x36c, 0x265, 0x16f, 0x66 , 0x76a, 0x663, 0x569, 0x460,
        0xca0, 0xda9, 0xea3, 0xfaa, 0x8a6, 0x9af, 0xaa5, 0xbac,
        0x4ac, 0x5a5, 0x6af, 0x7a6, 0xaa , 0x1a3, 0x2a9, 0x3a0,
        0xd30, 0xc39, 0xf33, 0xe3a, 0x936, 0x83f, 0xb35, 0xa3c,
        0x53c, 0x435, 0x73f, 0x636, 0x13a, 0x33 , 0x339, 0x230,
        0xe90, 0xf99, 0xc93, 0xd9a, 0xa96, 0xb9f, 0x895, 0x99c,
        0x69c, 0x795, 0x49f, 0x596, 0x29a, 0x393, 0x99 , 0x190,
        0xf00, 0xe09, 0xd03, 0xc0a, 0xb06, 0xa0f, 0x905, 0x80c,
        0x70c, 0x605, 0x50f, 0x406, 0x30a, 0x203, 0x109, 0x0
    };

    private static readonly int[,] TriTable = new int[256, 16]
    {
        {-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {0,8,3,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {0,1,9,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {1,8,3,9,8,1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {1,2,10,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {0,8,3,1,2,10,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {9,2,10,0,2,9,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {2,8,3,2,10,8,10,9,8,-1,-1,-1,-1,-1,-1,-1},
        {3,11,2,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {0,11,2,8,11,0,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {1,9,0,2,3,11,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {1,11,2,1,9,11,9,8,11,-1,-1,-1,-1,-1,-1,-1},
        {3,10,1,11,10,3,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {0,10,1,0,8,10,8,11,10,-1,-1,-1,-1,-1,-1,-1},
        {3,9,0,3,11,9,11,10,9,-1,-1,-1,-1,-1,-1,-1},
        {9,8,10,10,8,11,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {4,7,8,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {4,3,0,7,3,4,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {0,1,9,8,4,7,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {4,1,9,4,7,1,7,3,1,-1,-1,-1,-1,-1,-1,-1},
        {1,2,10,8,4,7,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {3,4,7,3,0,4,1,2,10,-1,-1,-1,-1,-1,-1,-1},
        {9,2,10,9,0,2,8,4,7,-1,-1,-1,-1,-1,-1,-1},
        {2,10,9,2,9,7,2,7,3,7,9,4,-1,-1,-1,-1},
        {8,4,7,3,11,2,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {11,4,7,11,2,4,2,0,4,-1,-1,-1,-1,-1,-1,-1},
        {9,0,1,8,4,7,2,3,11,-1,-1,-1,-1,-1,-1,-1},
        {4,7,11,9,4,11,9,11,2,9,2,1,-1,-1,-1,-1},
        {3,10,1,3,11,10,7,8,4,-1,-1,-1,-1,-1,-1,-1},
        {1,11,10,1,4,11,1,0,4,7,11,4,-1,-1,-1,-1},
        {4,7,8,9,0,11,9,11,10,11,0,3,-1,-1,-1,-1},
        {4,7,11,4,11,9,9,11,10,-1,-1,-1,-1,-1,-1,-1},
        {9,5,4,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {9,5,4,0,8,3,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {0,5,4,1,5,0,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {8,5,4,8,3,5,3,1,5,-1,-1,-1,-1,-1,-1,-1},
        {1,2,10,9,5,4,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {3,0,8,1,2,10,4,9,5,-1,-1,-1,-1,-1,-1,-1},
        {5,2,10,5,4,2,4,0,2,-1,-1,-1,-1,-1,-1,-1},
        {2,10,5,3,2,5,3,5,4,3,4,8,-1,-1,-1,-1},
        {9,5,4,2,3,11,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {0,11,2,0,8,11,4,9,5,-1,-1,-1,-1,-1,-1,-1},
        {0,5,4,0,1,5,2,3,11,-1,-1,-1,-1,-1,-1,-1},
        {2,1,5,2,5,8,2,8,11,4,8,5,-1,-1,-1,-1},
        {10,3,11,10,1,3,9,5,4,-1,-1,-1,-1,-1,-1,-1},
        {4,9,5,0,8,1,8,10,1,8,11,10,-1,-1,-1,-1},
        {5,4,0,5,0,11,5,11,10,11,0,3,-1,-1,-1,-1},
        {5,4,8,5,8,10,10,8,11,-1,-1,-1,-1,-1,-1,-1},
        {9,7,8,5,7,9,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {9,3,0,9,5,3,5,7,3,-1,-1,-1,-1,-1,-1,-1},
        {0,7,8,0,1,7,1,5,7,-1,-1,-1,-1,-1,-1,-1},
        {1,5,3,3,5,7,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {9,7,8,9,5,7,10,1,2,-1,-1,-1,-1,-1,-1,-1},
        {10,1,2,9,5,0,5,3,0,5,7,3,-1,-1,-1,-1},
        {8,0,2,8,2,5,8,5,7,10,5,2,-1,-1,-1,-1},
        {2,10,5,2,5,3,3,5,7,-1,-1,-1,-1,-1,-1,-1},
        {7,9,5,7,8,9,3,11,2,-1,-1,-1,-1,-1,-1,-1},
        {9,5,7,9,7,2,9,2,0,2,7,11,-1,-1,-1,-1},
        {2,3,11,0,1,8,1,7,8,1,5,7,-1,-1,-1,-1},
        {11,2,1,11,1,7,7,1,5,-1,-1,-1,-1,-1,-1,-1},
        {9,5,8,8,5,7,10,1,3,10,3,11,-1,-1,-1,-1},
        {5,7,0,5,0,9,7,11,0,1,0,10,11,10,0,-1},
        {11,10,0,11,0,3,10,5,0,8,0,7,5,7,0,-1},
        {11,10,5,7,11,5,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {10,6,5,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {0,8,3,5,10,6,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {9,0,1,5,10,6,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {1,8,3,1,9,8,5,10,6,-1,-1,-1,-1,-1,-1,-1},
        {1,6,5,2,6,1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {1,6,5,1,2,6,3,0,8,-1,-1,-1,-1,-1,-1,-1},
        {9,6,5,9,0,6,0,2,6,-1,-1,-1,-1,-1,-1,-1},
        {5,9,8,5,8,2,5,2,6,3,2,8,-1,-1,-1,-1},
        {2,3,11,10,6,5,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {11,0,8,11,2,0,10,6,5,-1,-1,-1,-1,-1,-1,-1},
        {0,1,9,2,3,11,5,10,6,-1,-1,-1,-1,-1,-1,-1},
        {5,10,6,1,9,2,9,11,2,9,8,11,-1,-1,-1,-1},
        {6,3,11,6,5,3,5,1,3,-1,-1,-1,-1,-1,-1,-1},
        {0,8,11,0,11,5,0,5,1,5,11,6,-1,-1,-1,-1},
        {3,11,6,0,3,6,0,6,5,0,5,9,-1,-1,-1,-1},
        {6,5,9,6,9,11,11,9,8,-1,-1,-1,-1,-1,-1,-1},
        {5,10,6,4,7,8,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {4,3,0,4,7,3,6,5,10,-1,-1,-1,-1,-1,-1,-1},
        {1,9,0,5,10,6,8,4,7,-1,-1,-1,-1,-1,-1,-1},
        {10,6,5,1,9,7,1,7,3,7,9,4,-1,-1,-1,-1},
        {6,1,2,6,5,1,4,7,8,-1,-1,-1,-1,-1,-1,-1},
        {1,2,5,5,2,6,3,0,4,3,4,7,-1,-1,-1,-1},
        {8,4,7,9,0,5,0,6,5,0,2,6,-1,-1,-1,-1},
        {7,3,9,7,9,4,3,2,9,5,9,6,2,6,9,-1},
        {3,11,2,7,8,4,10,6,5,-1,-1,-1,-1,-1,-1,-1},
        {5,10,6,4,7,2,4,2,0,2,7,11,-1,-1,-1,-1},
        {0,1,9,4,7,8,2,3,11,5,10,6,-1,-1,-1,-1},
        {9,2,1,9,11,2,9,4,11,7,11,4,5,10,6,-1},
        {8,4,7,3,11,5,3,5,1,5,11,6,-1,-1,-1,-1},
        {5,1,11,5,11,6,1,0,11,7,11,4,0,4,11,-1},
        {0,5,9,0,6,5,0,3,6,11,6,3,8,4,7,-1},
        {6,5,9,6,9,11,4,7,9,7,11,9,-1,-1,-1,-1},
        {10,4,9,6,4,10,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {4,10,6,4,9,10,0,8,3,-1,-1,-1,-1,-1,-1,-1},
        {10,0,1,10,6,0,6,4,0,-1,-1,-1,-1,-1,-1,-1},
        {8,3,1,8,1,6,8,6,4,6,1,10,-1,-1,-1,-1},
        {1,4,9,1,2,4,2,6,4,-1,-1,-1,-1,-1,-1,-1},
        {3,0,8,1,2,9,2,4,9,2,6,4,-1,-1,-1,-1},
        {0,2,4,4,2,6,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {8,3,2,8,2,4,4,2,6,-1,-1,-1,-1,-1,-1,-1},
        {10,4,9,10,6,4,11,2,3,-1,-1,-1,-1,-1,-1,-1},
        {0,8,2,2,8,11,4,9,10,4,10,6,-1,-1,-1,-1},
        {3,11,2,0,1,6,0,6,4,6,1,10,-1,-1,-1,-1},
        {6,4,1,6,1,10,4,8,1,2,1,11,8,11,1,-1},
        {9,6,4,9,3,6,9,1,3,11,6,3,-1,-1,-1,-1},
        {8,11,1,8,1,0,11,6,1,9,1,4,6,4,1,-1},
        {3,11,6,3,6,0,0,6,4,-1,-1,-1,-1,-1,-1,-1},
        {6,4,8,11,6,8,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {7,10,6,7,8,10,8,9,10,-1,-1,-1,-1,-1,-1,-1},
        {0,7,3,0,10,7,0,9,10,6,7,10,-1,-1,-1,-1},
        {10,6,7,1,10,7,1,7,8,1,8,0,-1,-1,-1,-1},
        {10,6,7,10,7,1,1,7,3,-1,-1,-1,-1,-1,-1,-1},
        {1,2,6,1,6,8,1,8,9,8,6,7,-1,-1,-1,-1},
        {2,6,9,2,9,1,6,7,9,0,9,3,7,3,9,-1},
        {7,8,0,7,0,6,6,0,2,-1,-1,-1,-1,-1,-1,-1},
        {7,3,2,6,7,2,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {2,3,11,10,6,8,10,8,9,8,6,7,-1,-1,-1,-1},
        {2,0,7,2,7,11,0,9,7,6,7,10,9,10,7,-1},
        {1,8,0,1,7,8,1,10,7,6,7,10,2,3,11,-1},
        {11,2,1,11,1,7,10,6,1,6,7,1,-1,-1,-1,-1},
        {8,9,6,8,6,7,9,1,6,11,6,3,1,3,6,-1},
        {0,9,1,11,6,7,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {7,8,0,7,0,6,3,11,0,11,6,0,-1,-1,-1,-1},
        {7,11,6,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {7,6,11,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {3,0,8,11,7,6,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {0,1,9,11,7,6,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {8,1,9,8,3,1,11,7,6,-1,-1,-1,-1,-1,-1,-1},
        {10,1,2,6,11,7,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {1,2,10,3,0,8,6,11,7,-1,-1,-1,-1,-1,-1,-1},
        {2,9,0,2,10,9,6,11,7,-1,-1,-1,-1,-1,-1,-1},
        {6,11,7,2,10,3,10,8,3,10,9,8,-1,-1,-1,-1},
        {7,2,3,6,2,7,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {7,0,8,7,6,0,6,2,0,-1,-1,-1,-1,-1,-1,-1},
        {2,7,6,2,3,7,0,1,9,-1,-1,-1,-1,-1,-1,-1},
        {1,6,2,1,8,6,1,9,8,8,7,6,-1,-1,-1,-1},
        {10,7,6,10,1,7,1,3,7,-1,-1,-1,-1,-1,-1,-1},
        {10,7,6,1,7,10,1,8,7,1,0,8,-1,-1,-1,-1},
        {0,3,7,0,7,10,0,10,9,6,10,7,-1,-1,-1,-1},
        {7,6,10,7,10,8,8,10,9,-1,-1,-1,-1,-1,-1,-1},
        {6,8,4,11,8,6,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {3,6,11,3,0,6,0,4,6,-1,-1,-1,-1,-1,-1,-1},
        {8,6,11,8,4,6,9,0,1,-1,-1,-1,-1,-1,-1,-1},
        {9,4,6,9,6,3,9,3,1,11,3,6,-1,-1,-1,-1},
        {6,8,4,6,11,8,2,10,1,-1,-1,-1,-1,-1,-1,-1},
        {1,2,10,3,0,11,0,6,11,0,4,6,-1,-1,-1,-1},
        {4,11,8,4,6,11,0,2,9,2,10,9,-1,-1,-1,-1},
        {10,9,3,10,3,2,9,4,3,11,3,6,4,6,3,-1},
        {8,2,3,8,4,2,4,6,2,-1,-1,-1,-1,-1,-1,-1},
        {0,4,2,4,6,2,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {1,9,0,2,3,4,2,4,6,4,3,8,-1,-1,-1,-1},
        {1,9,4,1,4,2,2,4,6,-1,-1,-1,-1,-1,-1,-1},
        {8,1,3,8,6,1,8,4,6,6,10,1,-1,-1,-1,-1},
        {10,1,0,10,0,6,6,0,4,-1,-1,-1,-1,-1,-1,-1},
        {4,6,3,4,3,8,6,10,3,0,3,9,10,9,3,-1},
        {10,9,4,6,10,4,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {4,9,5,7,6,11,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {0,8,3,4,9,5,11,7,6,-1,-1,-1,-1,-1,-1,-1},
        {5,0,1,5,4,0,7,6,11,-1,-1,-1,-1,-1,-1,-1},
        {11,7,6,8,3,4,3,5,4,3,1,5,-1,-1,-1,-1},
        {9,5,4,10,1,2,7,6,11,-1,-1,-1,-1,-1,-1,-1},
        {6,11,7,1,2,10,0,8,3,4,9,5,-1,-1,-1,-1},
        {7,6,11,5,4,10,4,2,10,4,0,2,-1,-1,-1,-1},
        {3,4,8,3,5,4,3,2,5,10,5,2,11,7,6,-1},
        {7,2,3,7,6,2,5,4,9,-1,-1,-1,-1,-1,-1,-1},
        {9,5,4,0,8,6,0,6,2,6,8,7,-1,-1,-1,-1},
        {3,6,2,3,7,6,1,5,0,5,4,0,-1,-1,-1,-1},
        {6,2,8,6,8,7,2,1,8,4,8,5,1,5,8,-1},
        {9,5,4,10,1,6,1,7,6,1,3,7,-1,-1,-1,-1},
        {1,6,10,1,7,6,1,0,7,8,7,0,9,5,4,-1},
        {4,0,10,4,10,5,0,3,10,6,10,7,3,7,10,-1},
        {7,6,10,7,10,8,5,4,10,4,8,10,-1,-1,-1,-1},
        {6,9,5,6,11,9,11,8,9,-1,-1,-1,-1,-1,-1,-1},
        {3,6,11,0,6,3,0,5,6,0,9,5,-1,-1,-1,-1},
        {0,11,8,0,5,11,0,1,5,5,6,11,-1,-1,-1,-1},
        {6,11,3,6,3,5,5,3,1,-1,-1,-1,-1,-1,-1,-1},
        {1,2,10,9,5,11,9,11,8,11,5,6,-1,-1,-1,-1},
        {0,11,3,0,6,11,0,9,6,5,6,9,1,2,10,-1},
        {11,8,5,11,5,6,8,0,5,10,5,2,0,2,5,-1},
        {6,11,3,6,3,5,2,10,3,10,5,3,-1,-1,-1,-1},
        {5,8,9,5,2,8,5,6,2,3,8,2,-1,-1,-1,-1},
        {9,5,6,9,6,0,0,6,2,-1,-1,-1,-1,-1,-1,-1},
        {1,5,8,1,8,0,5,6,8,3,8,2,6,2,8,-1},
        {1,5,6,2,1,6,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {1,3,6,1,6,10,3,8,6,5,6,9,8,9,6,-1},
        {10,1,0,10,0,6,9,5,0,5,6,0,-1,-1,-1,-1},
        {0,3,8,5,6,10,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {10,5,6,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {11,5,10,7,5,11,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {11,5,10,11,7,5,8,3,0,-1,-1,-1,-1,-1,-1,-1},
        {5,11,7,5,10,11,1,9,0,-1,-1,-1,-1,-1,-1,-1},
        {10,7,5,10,11,7,9,8,1,8,3,1,-1,-1,-1,-1},
        {11,1,2,11,7,1,7,5,1,-1,-1,-1,-1,-1,-1,-1},
        {0,8,3,1,2,7,1,7,5,7,2,11,-1,-1,-1,-1},
        {9,7,5,9,2,7,9,0,2,2,11,7,-1,-1,-1,-1},
        {7,5,2,7,2,11,5,9,2,3,2,8,9,8,2,-1},
        {2,5,10,2,3,5,3,7,5,-1,-1,-1,-1,-1,-1,-1},
        {8,2,0,8,5,2,8,7,5,10,2,5,-1,-1,-1,-1},
        {9,0,1,5,10,3,5,3,7,3,10,2,-1,-1,-1,-1},
        {9,8,2,9,2,1,8,7,2,10,2,5,7,5,2,-1},
        {1,3,5,3,7,5,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {0,8,7,0,7,1,1,7,5,-1,-1,-1,-1,-1,-1,-1},
        {9,0,3,9,3,5,5,3,7,-1,-1,-1,-1,-1,-1,-1},
        {9,8,7,5,9,7,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {5,8,4,5,10,8,10,11,8,-1,-1,-1,-1,-1,-1,-1},
        {5,0,4,5,11,0,5,10,11,11,3,0,-1,-1,-1,-1},
        {0,1,9,8,4,10,8,10,11,10,4,5,-1,-1,-1,-1},
        {10,11,4,10,4,5,11,3,4,9,4,1,3,1,4,-1},
        {2,5,1,2,8,5,2,11,8,4,5,8,-1,-1,-1,-1},
        {0,4,11,0,11,3,4,5,11,2,11,1,5,1,11,-1},
        {0,2,5,0,5,9,2,11,5,4,5,8,11,8,5,-1},
        {9,4,5,2,11,3,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {2,5,10,3,5,2,3,4,5,3,8,4,-1,-1,-1,-1},
        {5,10,2,5,2,4,4,2,0,-1,-1,-1,-1,-1,-1,-1},
        {3,10,2,3,5,10,3,8,5,4,5,8,0,1,9,-1},
        {5,10,2,5,2,4,1,9,2,9,4,2,-1,-1,-1,-1},
        {8,4,5,8,5,3,3,5,1,-1,-1,-1,-1,-1,-1,-1},
        {0,4,5,1,0,5,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {8,4,5,8,5,3,9,0,5,0,3,5,-1,-1,-1,-1},
        {9,4,5,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {4,11,7,4,9,11,9,10,11,-1,-1,-1,-1,-1,-1,-1},
        {0,8,3,4,9,7,9,11,7,9,10,11,-1,-1,-1,-1},
        {1,10,11,1,11,4,1,4,0,7,4,11,-1,-1,-1,-1},
        {3,1,4,3,4,8,1,10,4,7,4,11,10,11,4,-1},
        {4,11,7,9,11,4,9,2,11,9,1,2,-1,-1,-1,-1},
        {9,7,4,9,11,7,9,1,11,2,11,1,0,8,3,-1},
        {11,7,4,11,4,2,2,4,0,-1,-1,-1,-1,-1,-1,-1},
        {11,7,4,11,4,2,8,3,4,3,2,4,-1,-1,-1,-1},
        {2,9,10,2,7,9,2,3,7,7,4,9,-1,-1,-1,-1},
        {9,10,7,9,7,4,10,2,7,8,7,0,2,0,7,-1},
        {3,7,10,3,10,2,7,4,10,1,10,0,4,0,10,-1},
        {1,10,2,8,7,4,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {4,9,1,4,1,7,7,1,3,-1,-1,-1,-1,-1,-1,-1},
        {4,9,1,4,1,7,0,8,1,8,7,1,-1,-1,-1,-1},
        {4,0,3,7,4,3,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {4,8,7,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {9,10,8,10,11,8,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {3,0,9,3,9,11,11,9,10,-1,-1,-1,-1,-1,-1,-1},
        {0,1,10,0,10,8,8,10,11,-1,-1,-1,-1,-1,-1,-1},
        {3,1,10,11,3,10,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {1,2,11,1,11,9,9,11,8,-1,-1,-1,-1,-1,-1,-1},
        {3,0,9,3,9,11,1,2,9,2,11,9,-1,-1,-1,-1},
        {0,2,11,8,0,11,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {3,2,11,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {2,3,8,2,8,10,10,8,9,-1,-1,-1,-1,-1,-1,-1},
        {9,10,2,0,9,2,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {2,3,8,2,8,10,0,1,8,1,10,8,-1,-1,-1,-1},
        {1,10,2,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {1,3,8,9,1,8,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {0,9,1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {0,3,8,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1},
        {-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1}
    };
}
