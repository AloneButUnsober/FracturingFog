// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Export/DualContourMesher.cs
//
// Roadmap slice S9 (3D-Rendering-Roadmap.md §S9, #391) — DUAL CONTOURING of the DE
// field, the sharp-feature alternative to Marching Cubes. MC places surface vertices
// ON the grid edges, so a hard crease (a Mandelbox facet edge, a KIFS corner) gets
// rounded off into a chamfer at grid resolution. Dual contouring instead places ONE
// vertex per cell, positioned by minimising a quadratic error function (QEF) over the
// cell's Hermite data — the edge crossing points plus the DE gradient (surface
// normal) at each — so the vertex snaps to the intersection of the tangent planes:
// exactly onto the crease. Vertices of the four cells around each sign-changing grid
// edge are joined into a quad (two triangles), giving the surface as the dual of the
// grid.
//
// This is the interior mesher: it emits quads only for grid edges shared by four
// cells, so the surface is closed when the shape is fully inside the sample cube
// (the auto-sized ProbeBoundingRange path) and open where it exits the box — the
// same starting point Marching Cubes had before the boundary cap (#422). A DC
// boundary cap is a follow-up.
//
// QEF is solved by the regularised normal equations (ATA + lambda*I) p = ATb +
// lambda*c, biased toward the cell's mass point c and clamped to the cell — robust
// against the rank-deficient planar / edge cases without an SVD dependency.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FracturingFog.Export;

/// <summary>Uniform-grid dual contouring of a <see cref="SampleDistance"/> field —
/// one QEF-placed vertex per cell, so hard creases stay sharp (roadmap S9, #391).</summary>
public static class DualContourMesher
{
    // Global winding sense: with the "inside" test DE &lt; iso, this orientation
    // makes the quads wind OUTWARD (positive signed volume — the S9 mesh contract,
    // MeshValidator.SignedVolume &gt; 0), matching the Marching-Cubes exporter.
    private const bool FlipWinding = false;

    public static (List<(double X, double Y, double Z)> verts,
                   List<(double X, double Y, double Z)> norms,
                   List<(int A, int B, int C)> tris)
        Build(SampleDistance sample, double cx, double cy, double cz, double range, int n,
              double isoScale, bool isoAbsolute, CancellationToken ct)
    {
        if (n < 8) n = 8;
        double step = 2.0 * range / n;
        double iso = isoAbsolute
            ? Math.Clamp(isoScale, 1e-6, range)
            : step * Math.Clamp(isoScale, 0.02, 1.0);

        int side = n + 1;
        var field = new double[side * side * side];
        try
        {
            Parallel.For(0, side, new ParallelOptions { CancellationToken = ct }, i =>
            {
                for (int j = 0; j < side; j++)
                for (int k = 0; k < side; k++)
                {
                    double x = cx - range + i * step;
                    double y = cy - range + j * step;
                    double z = cz - range + k * step;
                    field[(i * side + j) * side + k] = sample(x, y, z);
                }
            });
        }
        catch (OperationCanceledException)
        {
            return (new(), new(), new());
        }

        double F(int i, int j, int k) => field[(i * side + j) * side + k];
        double PX(int i) => cx - range + i * step;
        double PY(int j) => cy - range + j * step;
        double PZ(int k) => cz - range + k * step;

        // Unit DE gradient (surface normal) at a world point, by central differences.
        (double x, double y, double z) Grad(double x, double y, double z)
        {
            double h = step * 0.5;
            double gx = sample(x + h, y, z) - sample(x - h, y, z);
            double gy = sample(x, y + h, z) - sample(x, y - h, z);
            double gz = sample(x, y, z + h) - sample(x, y, z - h);
            double l = Math.Sqrt(gx * gx + gy * gy + gz * gz);
            return l < 1e-20 ? (0.0, 0.0, 1.0) : (gx / l, gy / l, gz / l);
        }

        // Cell corner offsets (Bourke-independent; local 0..7 order below).
        // 12 edges as (cornerA, cornerB) with corner = (dx,dy,dz).
        // Corner index c = dx + 2*dy + 4*dz.
        Span<int> ea = stackalloc int[12] { 0, 2, 4, 6,  0, 1, 4, 5,  0, 1, 2, 3 };
        Span<int> eb = stackalloc int[12] { 1, 3, 5, 7,  2, 3, 6, 7,  4, 5, 6, 7 };

        // One vertex per active cell (a cell with at least one sign-changing edge).
        var cellVert = new int[n * n * n];
        Array.Fill(cellVert, -1);
        var verts = new List<(double, double, double)>();

        int CellIdx(int i, int j, int k) => (i * n + j) * n + k;

        // Per-cell scratch — allocated ONCE (stackalloc inside the n^3 loop would
        // never free and overflow the stack).
        Span<double> cv = stackalloc double[8];
        Span<double> px = stackalloc double[8];
        Span<double> py = stackalloc double[8];
        Span<double> pz = stackalloc double[8];
        for (int i = 0; i < n; i++)
        {
            if (ct.IsCancellationRequested) return (new(), new(), new());
            for (int j = 0; j < n; j++)
            for (int k = 0; k < n; k++)
            {
                // Corner scalars in local 0..7 order.
                for (int c = 0; c < 8; c++)
                {
                    int dx = c & 1, dy = (c >> 1) & 1, dz = (c >> 2) & 1;
                    cv[c] = F(i + dx, j + dy, k + dz);
                    px[c] = PX(i + dx); py[c] = PY(j + dy); pz[c] = PZ(k + dz);
                }

                // QEF accumulation over sign-changing edges.
                double a00 = 0, a01 = 0, a02 = 0, a11 = 0, a12 = 0, a22 = 0;
                double b0 = 0, b1 = 0, b2 = 0;
                double mx = 0, my = 0, mz = 0;
                int cross = 0;
                for (int e = 0; e < 12; e++)
                {
                    int ca = ea[e], cb = eb[e];
                    bool ia = cv[ca] < iso, ib = cv[cb] < iso;
                    if (ia == ib) continue;
                    double denom = cv[cb] - cv[ca];
                    double t = Math.Abs(denom) < 1e-20 ? 0.5 : (iso - cv[ca]) / denom;
                    t = Math.Clamp(t, 0.0, 1.0);
                    double x = px[ca] + t * (px[cb] - px[ca]);
                    double y = py[ca] + t * (py[cb] - py[ca]);
                    double z = pz[ca] + t * (pz[cb] - pz[ca]);
                    var (nx, ny, nz) = Grad(x, y, z);
                    a00 += nx * nx; a01 += nx * ny; a02 += nx * nz;
                    a11 += ny * ny; a12 += ny * nz; a22 += nz * nz;
                    double d = nx * x + ny * y + nz * z;
                    b0 += nx * d; b1 += ny * d; b2 += nz * d;
                    mx += x; my += y; mz += z;
                    cross++;
                }
                if (cross == 0) continue;

                mx /= cross; my /= cross; mz /= cross;   // mass point (fallback / bias)

                // Regularise toward the mass point so rank-deficient (planar / edge)
                // QEFs stay well-posed: (ATA + lambda I) p = ATb + lambda c.
                double lambda = 1e-3 * (a00 + a11 + a22) / 3.0 + 1e-6;
                a00 += lambda; a11 += lambda; a22 += lambda;
                b0 += lambda * mx; b1 += lambda * my; b2 += lambda * mz;

                if (!Solve3(a00, a01, a02, a11, a12, a22, b0, b1, b2,
                            out double vx, out double vy, out double vz))
                { vx = mx; vy = my; vz = mz; }

                // Keep the vertex inside its cell (QEF can shoot far on near-flat data).
                double x0 = PX(i), y0 = PY(j), z0 = PZ(k);
                vx = Math.Clamp(vx, x0, x0 + step);
                vy = Math.Clamp(vy, y0, y0 + step);
                vz = Math.Clamp(vz, z0, z0 + step);

                cellVert[CellIdx(i, j, k)] = verts.Count;
                verts.Add((vx, vy, vz));
            }
        }

        // Quads over interior grid edges (shared by 4 cells). Each family joins the
        // four surrounding cells' vertices; winding follows the sign direction.
        var tris = new List<(int, int, int)>();
        void Quad(int c0, int c1, int c2, int c3, bool inside0)
        {
            // c0..c3 are cell vertex indices in CCW order around the edge (as seen
            // looking down the edge's + axis). inside0 = low-corner is inside.
            bool forward = inside0 ^ FlipWinding;
            if (forward)
            {
                tris.Add((c0, c1, c2));
                tris.Add((c0, c2, c3));
            }
            else
            {
                tris.Add((c0, c2, c1));
                tris.Add((c0, c3, c2));
            }
        }

        // X-edges: (i,j,k)->(i+1,j,k). 4 cells vary in (j-1/j, k-1/k), cell x = i.
        for (int i = 0; i < n; i++)
        for (int j = 1; j < n; j++)
        for (int k = 1; k < n; k++)
        {
            bool ia = F(i, j, k) < iso, ib = F(i + 1, j, k) < iso;
            if (ia == ib) continue;
            int q0 = cellVert[CellIdx(i, j - 1, k - 1)];
            int q1 = cellVert[CellIdx(i, j,     k - 1)];
            int q2 = cellVert[CellIdx(i, j,     k)];
            int q3 = cellVert[CellIdx(i, j - 1, k)];
            if (q0 < 0 || q1 < 0 || q2 < 0 || q3 < 0) continue;
            Quad(q0, q1, q2, q3, ia);
        }
        // Y-edges: (i,j,k)->(i,j+1,k). 4 cells vary in (i-1/i, k-1/k), cell y = j.
        for (int i = 1; i < n; i++)
        for (int j = 0; j < n; j++)
        for (int k = 1; k < n; k++)
        {
            bool ia = F(i, j, k) < iso, ib = F(i, j + 1, k) < iso;
            if (ia == ib) continue;
            int q0 = cellVert[CellIdx(i - 1, j, k - 1)];
            int q1 = cellVert[CellIdx(i,     j, k - 1)];
            int q2 = cellVert[CellIdx(i,     j, k)];
            int q3 = cellVert[CellIdx(i - 1, j, k)];
            if (q0 < 0 || q1 < 0 || q2 < 0 || q3 < 0) continue;
            // Y-edge winding is mirrored relative to X/Z (the CCW loop in XZ runs
            // opposite the surface sense), so invert the inside flag.
            Quad(q0, q1, q2, q3, !ia);
        }
        // Z-edges: (i,j,k)->(i,j,k+1). 4 cells vary in (i-1/i, j-1/j), cell z = k.
        for (int i = 1; i < n; i++)
        for (int j = 1; j < n; j++)
        for (int k = 0; k < n; k++)
        {
            bool ia = F(i, j, k) < iso, ib = F(i, j, k + 1) < iso;
            if (ia == ib) continue;
            int q0 = cellVert[CellIdx(i - 1, j - 1, k)];
            int q1 = cellVert[CellIdx(i,     j - 1, k)];
            int q2 = cellVert[CellIdx(i,     j,     k)];
            int q3 = cellVert[CellIdx(i - 1, j,     k)];
            if (q0 < 0 || q1 < 0 || q2 < 0 || q3 < 0) continue;
            Quad(q0, q1, q2, q3, ia);
        }

        // Smooth per-vertex normals from incident face normals (as the MC path).
        var norms = new List<(double X, double Y, double Z)>(verts.Count);
        for (int v = 0; v < verts.Count; v++) norms.Add((0, 0, 0));
        foreach (var (a, b, c) in tris)
        {
            var (axp, ayp, azp) = verts[a];
            var (bxp, byp, bzp) = verts[b];
            var (cxp, cyp, czp) = verts[c];
            double ex = bxp - axp, ey = byp - ayp, ez = bzp - azp;
            double fx = cxp - axp, fy = cyp - ayp, fz = czp - azp;
            double nx = ey * fz - ez * fy, ny = ez * fx - ex * fz, nz = ex * fy - ey * fx;
            var na = norms[a]; norms[a] = (na.X + nx, na.Y + ny, na.Z + nz);
            var nb = norms[b]; norms[b] = (nb.X + nx, nb.Y + ny, nb.Z + nz);
            var nc = norms[c]; norms[c] = (nc.X + nx, nc.Y + ny, nc.Z + nz);
        }
        for (int v = 0; v < norms.Count; v++)
        {
            var (nx, ny, nz) = norms[v];
            double l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            norms[v] = l > 1e-20 ? (nx / l, ny / l, nz / l) : (0, 0, 1);
        }
        return (verts, norms, tris);
    }

    // Solve a symmetric 3x3 system A p = b (A given by its upper triangle). Returns
    // false when |det| is too small to trust.
    private static bool Solve3(
        double a00, double a01, double a02, double a11, double a12, double a22,
        double b0, double b1, double b2,
        out double x, out double y, out double z)
    {
        double c00 = a11 * a22 - a12 * a12;
        double c01 = a02 * a12 - a01 * a22;
        double c02 = a01 * a12 - a02 * a11;
        double det = a00 * c00 + a01 * c01 + a02 * c02;
        if (Math.Abs(det) < 1e-18) { x = y = z = 0; return false; }
        double inv = 1.0 / det;
        double c11 = a00 * a22 - a02 * a02;
        double c12 = a02 * a01 - a00 * a12;
        double c22 = a00 * a11 - a01 * a01;
        x = (c00 * b0 + c01 * b1 + c02 * b2) * inv;
        y = (c01 * b0 + c11 * b1 + c12 * b2) * inv;
        z = (c02 * b0 + c12 * b1 + c22 * b2) * inv;
        return true;
    }
}
