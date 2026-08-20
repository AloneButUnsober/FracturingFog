// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Export/HeightfieldMeshExporter.cs
//
// #138 — export the Oblique 3D "objects" (the 2D heightfield used by
// HeightfieldRaymarch2D) as a real 3D mesh. Reuses the same height pipeline as
// the raymarch — tone curve, edge fade, world scale, and the #135 isolation
// cull mask — so the exported object matches the on-screen cutout. Tessellates
// a downsampled grid into a WATERTIGHT solid: top surface + flat base at y=0 +
// vertical skirt walls at every kept/dropped boundary. Writes OBJ (per-vertex
// colour + smooth normals) or binary STL.
//
// Mesh grid matches the raymarch world frame: X in [-aspect/2, +aspect/2],
// Z in [-0.5, +0.5], Y up, height = curve(smooth) normalised x 0.35 x heightScale.
//
// LIKENESS (why the mesh used to look spiky/blocky vs the smooth on-screen
// raymarch, and what fixes it here):
//   1. SMOOTH NORMALS. The render shades with ANALYTIC surface normals; a bare
//      grid mesh with no normals is flat-shaded per quad => faceted "8-bit"
//      blocks up close. We compute per-vertex analytic normals from the height
//      field (central differences) and emit them, so Blender smooth-shades the
//      top surface exactly like the raymarch.
//   2. DESPIKE. A lone tall cell near the fractal boundary towers over its
//      neighbours => a radial spike. A 3x3 median pass removes salt-and-pepper
//      spikes while preserving real ridges/edges.
//   3. MASK CLEANUP. The binary isolation mask keeps isolated single cells =>
//      tall pillars with vertical skirt walls ("spikes out the back"). A
//      morphological open (drop lone kept cells) + close (fill lone holes)
//      declutters the silhouette so isolation exports are solid, not hedgehogs.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

using FracturingFog.Models;

namespace FracturingFog.Export;

public static class HeightfieldMeshExporter
{
    private readonly record struct Vert(
        double X, double Y, double Z, uint C, float Nx, float Ny, float Nz);

    /// <summary>Export the heightfield object to <paramref name="path"/> (.stl =
    /// binary STL, else OBJ with vertex colour + smooth normals). Returns the
    /// triangle count. <paramref name="targetGrid"/> caps the longer grid axis
    /// (downsample); pass -1 to take it from <see cref="FractalParameters.Relief2DMeshGrid"/>.
    /// No-op (returns 0) on an all-flat field.</summary>
    public static int Export(uint[] albedo, float[] height, int w, int h,
                             FractalParameters p, string path, int targetGrid = -1)
    {
        if (w < 2 || h < 2 || height.Length < w * h) return 0;

        // Detail = grid resolution (from the knob unless the caller overrides).
        if (targetGrid <= 0) targetGrid = p.Relief2DMeshGrid > 0 ? p.Relief2DMeshGrid : 512;
        targetGrid = Math.Clamp(targetGrid, 16, 4096);

        // File-size budget: clamp the grid down so the estimated output stays
        // under the requested megabytes (rough: ~0.55 KB per grid cell for the
        // top+base+wall tessellation with per-vertex colour+normal).
        if (p.Relief2DMeshMaxMB > 0.0)
        {
            double aspectR = (double)w / h;
            double budgetCells = p.Relief2DMeshMaxMB * 1024.0 * 1024.0 / 560.0;
            if (budgetCells > 16)
            {
                int budgetGrid = (int)Math.Sqrt(budgetCells * aspectR);
                if (budgetGrid < targetGrid) targetGrid = Math.Max(16, budgetGrid);
            }
        }

        // Downsample stride so the longer axis is ~targetGrid cells.
        int stride = Math.Max(1, (int)Math.Ceiling(Math.Max(w, h) / (double)Math.Max(16, targetGrid)));
        int gw = (w + stride - 1) / stride;
        int gh = (h + stride - 1) / stride;
        if (gw < 2 || gh < 2) return 0;

        // Compressed height on the downsampled grid (mirror HeightfieldRaymarch2D).
        // BOX-AVERAGE the raw field over each stride block instead of point-
        // sampling one pixel: the smooth-count field has sub-cell filaments, and
        // picking a single pixel per cell aliases them into isolated radial
        // SPIKES. Averaging antialiases the height into the coarse grid so
        // filaments read as continuous ridges, matching the full-res raymarch.
        // Albedo is box-averaged the same way so vertex colour tracks the height.
        var curve = p.Relief2DHeightCurve;
        double edgeFade = Math.Clamp(p.Relief2DEdgeFade, 0.0, 0.5);
        double mxF = Math.Max(1.0, edgeFade * gw), myF = Math.Max(1.0, edgeFade * gh);
        float[] hh = new float[gw * gh];
        uint[] alb = new uint[gw * gh];
        for (int gy = 0; gy < gh; gy++)
        for (int gx = 0; gx < gw; gx++)
        {
            int x0 = gx * stride, y0 = gy * stride;
            int x1 = Math.Min(w, x0 + stride), y1 = Math.Min(h, y0 + stride);
            double sumRaw = 0.0, aA = 0.0, aR = 0.0, aG = 0.0, aB = 0.0;
            long cnt = 0;
            for (int sy = y0; sy < y1; sy++)
            for (int sx = x0; sx < x1; sx++)
            {
                int si = sy * w + sx;
                float rv = height[si];
                sumRaw += rv > 0f ? rv : 0f;
                uint c = (uint)si < (uint)albedo.Length ? albedo[si] : 0xFF808080u;
                aA += (c >> 24) & 0xFF; aR += (c >> 16) & 0xFF; aG += (c >> 8) & 0xFF; aB += c & 0xFF;
                cnt++;
            }
            double raw = cnt > 0 ? sumRaw / cnt : 0.0;
            float hv = raw <= 0.0 ? 0f : curve switch
            {
                HeightCurve2D.Linear => (float)raw,
                HeightCurve2D.Sqrt   => (float)Math.Sqrt(raw),
                _                    => (float)Math.Log(1.0 + raw),
            };
            int gi = gy * gw + gx;
            hh[gi] = hv;
            alb[gi] = cnt > 0
                ? ((uint)Math.Round(aA / cnt) << 24) | ((uint)Math.Round(aR / cnt) << 16)
                  | ((uint)Math.Round(aG / cnt) << 8) | (uint)Math.Round(aB / cnt)
                : 0xFF808080u;
        }

        // #141 exterior baseline subtraction — the tone curve (esp. Log) lifts
        // the low far-from-set smooth counts into a raised rectangular PLATEAU.
        // The raymarch strips it so only boundary structure rises; the mesh must
        // too, or the solid keeps that plateau as its whole rough base.
        SubtractExteriorBaseline(hh);

        // SMOOTHING knob [0,1] drives the despike/merge strength. 0 = raw (max
        // detail, spiky); 0.5 (default) = the tuned clean look (close 6 / blur 2);
        // 1 = heavy.
        double smooth = Math.Clamp(p.Relief2DMeshSmoothing, 0.0, 1.0);
        int closeIters = (int)Math.Round(smooth * 12.0);
        int blurPasses = (int)Math.Round(smooth * 4.0);

        // DESPIKE — 3x3 median. A lone cell near the boundary can be far taller
        // than its neighbours (the smooth count is chaotic there); tessellated
        // that becomes a radial spike. Median removes the lone outliers while
        // keeping real ridges. Skipped at smoothing 0 (raw = max detail).
        if (smooth > 0.0) MedianFilter3x3(hh, gw, gh);

        // FILL THIN VALLEYS — grayscale morphological close (dilate then erode).
        // The fractal boundary is a comb of tall dendrites separated by thin
        // deep valleys; tessellated, each dendrite is an isolated tooth => the
        // "forest of spikes". Closing raises the thin valleys to the surrounding
        // height so the dendrites MERGE into one solid ridge (what the sphere-
        // tracing DE does implicitly by skipping sub-cone gaps).
        for (int i = 0; i < closeIters; i++) GrayDilate3x3(hh, gw, gh);
        for (int i = 0; i < closeIters; i++) GrayErode3x3(hh, gw, gh);

        // LOW-PASS — the raymarch's DE skips features thinner than its cone, so
        // the on-screen relief reads as a smooth ridge. Box passes spread each
        // remaining cliff into a slope so the mesh top surface is continuous.
        for (int i = 0; i < blurPasses; i++) BoxBlur3x3(hh, gw, gh);

        float maxH = 0f;
        for (int i = 0; i < hh.Length; i++) if (hh[i] > maxH) maxH = hh[i];
        if (maxH <= 1e-9f) return 0;

        // DEADZONE — flatten the near-zero plain to exactly 0. The far exterior
        // and set interior carry residual smooth-count noise after the baseline
        // subtract; left in, it tessellates into a salt-and-pepper bumpy plain
        // (and z-fights the flat base). Snapping small heights to 0 makes the
        // plain a single clean sheet.
        float dead = 0.03f * maxH;
        for (int i = 0; i < hh.Length; i++) if (hh[i] < dead) hh[i] = 0f;

        // Edge cap (#140) — pull tall edge structure down to the base plane
        // without lifting the flat exterior (no rectangular lip).
        if (edgeFade > 0.0)
        {
            for (int gy = 0; gy < gh; gy++)
            for (int gx = 0; gx < gw; gx++)
            {
                double dxE = Math.Min(gx, gw - 1 - gx), dyE = Math.Min(gy, gh - 1 - gy);
                double wx = dxE >= mxF ? 1.0 : Smoothstep(dxE / mxF);
                double wy = dyE >= myF ? 1.0 : Smoothstep(dyE / myF);
                double f = wx * wy;
                if (f < 1.0)
                {
                    float cap = (float)(f * maxH);
                    int gi = gy * gw + gx;
                    if (hh[gi] > cap) hh[gi] = cap;
                }
            }
        }

        double aspect = (double)w / h;
        // Mesh relief height (world units) from the HEIGHT knob. Deliberately
        // gentler than the raymarch's 0.35: a faithful mesh of the full-height
        // relief is a forest of tall thin dendrites (spikes) when seen from the
        // side in Blender — the render only hides that with top-down framing +
        // shadow. A low-relief embossing reads as a clean Mandelbrot from every
        // angle. Independent of the on-screen Relief2DHeightScale.
        double meshHeight = Math.Max(0.0, p.Relief2DMeshHeight);
        double sy2 = meshHeight / maxH;
        // Solid slab: the base sits a slab-thickness below the flat plain so the
        // top surface never coincides with the base (no z-fighting on the flat
        // regions). baseThick is that minimum thickness under the plain.
        double reliefTop = sy2 * maxH;
        double baseThick = 0.30 * reliefTop;
        // Contoured underside: mirror a fraction of the (already smoothed) top
        // relief down onto the base, so the back carries the fractal contour too
        // instead of being dead flat. Because it reuses the SAME smoothed height
        // field, the underside is smooth — no spikes reintroduced. 0 = flat back
        // (old behaviour), 1 = the back bulges as deep as the top rises.
        double underScale = Math.Clamp(p.Relief2DMeshUnderside, 0.0, 1.0);

        // Base Y under grid vertex (gx,gy): always >= baseThick below the top, so
        // the wall is never degenerate (thickness = baseThick + (1+underScale)*topY).
        double BaseYAt(int gx, int gy) => -baseThick - underScale * hh[gy * gw + gx] * sy2;

        // #135 isolation cull mask on the downsampled grid, then morphological
        // cleanup so isolated kept cells (spike pillars) and lone holes vanish.
        bool[] keep = BuildKeepMask(hh, alb, gw, gh, p);
        if (p.Relief2DIsolate) CleanMask(keep, gw, gh);

        // World-space top-surface slope at (gx,gy) via central differences —
        // the basis for both the top and the (mirrored) base analytic normals.
        double dxw = aspect / gw, dzw = 1.0 / gh;
        (double sx, double sz) Slope(int gx, int gy)
        {
            int xl = Math.Max(0, gx - 1), xr = Math.Min(gw - 1, gx + 1);
            int yu = Math.Max(0, gy - 1), yd = Math.Min(gh - 1, gy + 1);
            double dYdX = (hh[gy * gw + xr] - hh[gy * gw + xl]) * sy2 / ((xr - xl) * dxw);
            double dYdZ = (hh[yd * gw + gx] - hh[yu * gw + gx]) * sy2 / ((yd - yu) * dzw);
            return (dYdX, dYdZ);
        }
        // Top-surface outward (up) normal — smooth shading, faceting gone.
        (float nx, float ny, float nz) N(int gx, int gy)
        {
            var (sx, sz) = Slope(gx, gy);
            double nx = -sx, ny = 1.0, nz = -sz;
            double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (nl < 1e-12) return (0f, 1f, 0f);
            return ((float)(nx / nl), (float)(ny / nl), (float)(nz / nl));
        }
        // Base-surface outward (down) normal. baseY = -baseThick - underScale*topY,
        // so its slope is -underScale * topSlope; the outward (downward) normal is
        // (-underScale*sx, -1, -underScale*sz). At underScale 0 this is (0,-1,0),
        // i.e. the old flat back.
        (float nx, float ny, float nz) NB(int gx, int gy)
        {
            var (sx, sz) = Slope(gx, gy);
            double nx = -underScale * sx, ny = -1.0, nz = -underScale * sz;
            double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (nl < 1e-12) return (0f, -1f, 0f);
            return ((float)(nx / nl), (float)(ny / nl), (float)(nz / nl));
        }

        // World position of grid vertex (gx, gy) at its height.
        (double x, double y, double z) V(int gx, int gy)
        {
            double u = (gx + 0.5) / gw, v = (gy + 0.5) / gh;
            return ((u - 0.5) * aspect, hh[gy * gw + gx] * sy2, v - 0.5);
        }

        var verts = new List<Vert>();
        var tris = new List<(int a, int b, int c)>();
        int AddV(double x, double y, double z, uint c, float nx, float ny, float nz)
        {
            verts.Add(new Vert(x, y, z, c, nx, ny, nz));
            return verts.Count - 1;
        }

        // A cell (gx,gy)->(gx+1,gy+1) is part of the object when all four corner
        // vertices are kept.
        bool CellKept(int gx, int gy)
            => keep[gy * gw + gx] && keep[gy * gw + gx + 1]
            && keep[(gy + 1) * gw + gx] && keep[(gy + 1) * gw + gx + 1];

        for (int gy = 0; gy < gh - 1; gy++)
        for (int gx = 0; gx < gw - 1; gx++)
        {
            if (!CellKept(gx, gy)) continue;

            var p00 = V(gx, gy);     var p10 = V(gx + 1, gy);
            var p01 = V(gx, gy + 1); var p11 = V(gx + 1, gy + 1);
            var n00 = N(gx, gy);     var n10 = N(gx + 1, gy);
            var n01 = N(gx, gy + 1); var n11 = N(gx + 1, gy + 1);
            uint c00 = alb[gy * gw + gx], c10 = alb[gy * gw + gx + 1];
            uint c01 = alb[(gy + 1) * gw + gx], c11 = alb[(gy + 1) * gw + gx + 1];

            // Top surface (CCW viewed from +Y) — smooth analytic normals.
            int t00 = AddV(p00.x, p00.y, p00.z, c00, n00.nx, n00.ny, n00.nz);
            int t10 = AddV(p10.x, p10.y, p10.z, c10, n10.nx, n10.ny, n10.nz);
            int t01 = AddV(p01.x, p01.y, p01.z, c01, n01.nx, n01.ny, n01.nz);
            int t11 = AddV(p11.x, p11.y, p11.z, c11, n11.nx, n11.ny, n11.nz);
            tris.Add((t00, t11, t10));
            tris.Add((t00, t01, t11));

            // Base — contoured underside (mirrors the smoothed relief). Reversed
            // winding, smooth down-facing normals.
            double y00 = BaseYAt(gx, gy),     y10 = BaseYAt(gx + 1, gy);
            double y01 = BaseYAt(gx, gy + 1), y11 = BaseYAt(gx + 1, gy + 1);
            var m00 = NB(gx, gy);     var m10 = NB(gx + 1, gy);
            var m01 = NB(gx, gy + 1); var m11 = NB(gx + 1, gy + 1);
            int b00 = AddV(p00.x, y00, p00.z, c00, m00.nx, m00.ny, m00.nz);
            int b10 = AddV(p10.x, y10, p10.z, c10, m10.nx, m10.ny, m10.nz);
            int b01 = AddV(p01.x, y01, p01.z, c01, m01.nx, m01.ny, m01.nz);
            int b11 = AddV(p11.x, y11, p11.z, c11, m11.nx, m11.ny, m11.nz);
            tris.Add((b00, b10, b11));
            tris.Add((b00, b11, b01));

            // Skirt walls where a neighbour cell is not part of the object (or
            // outside the grid) — close the solid from top edge down to the
            // (contoured) base at each end.
            if (gx == 0 || !CellKept(gx - 1, gy)) AddWall(tris, verts, p00, p01, c00, c01, y00, y01);
            if (gx == gw - 2 || !CellKept(gx + 1, gy)) AddWall(tris, verts, p11, p10, c11, c10, y11, y10);
            if (gy == 0 || !CellKept(gx, gy - 1)) AddWall(tris, verts, p10, p00, c10, c00, y10, y00);
            if (gy == gh - 2 || !CellKept(gx, gy + 1)) AddWall(tris, verts, p01, p11, c01, c11, y01, y11);
        }

        if (tris.Count == 0) return 0;

        if (path.EndsWith(".stl", StringComparison.OrdinalIgnoreCase))
            WriteStl(path, verts, tris);
        else if (path.EndsWith(".ply", StringComparison.OrdinalIgnoreCase))
            WritePly(path, verts, tris);
        else if (path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)
              || path.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase))
            WriteGltf(path, verts, tris);
        else
            WriteObj(path, verts, tris);
        return tris.Count;
    }

    // A vertical wall quad from the top edge (t0->t1) down to the base at each
    // end (baseY0 under t0, baseY1 under t1), closing the solid. The callers pass
    // the seam edge as the TOP SURFACE traverses it (t0->t1); the wall is wound so
    // its own top edge runs the OTHER way (t1->t0), so the shared edge is crossed
    // once in each direction — a manifold-consistent seam. (S9.1a #419: the old
    // winding matched the surface's, flipping ~every wall seam — watertight and
    // 2-manifold but not consistently oriented, caught by MeshValidator.) The flat
    // wall normal is taken from the actual winding so the STL per-face normal and
    // the OBJ vn agree and point outward.
    private static void AddWall(
        List<(int, int, int)> tris, List<Vert> verts,
        (double x, double y, double z) t0, (double x, double y, double z) t1,
        uint c0, uint c1, double baseY0, double baseY1)
    {
        // Quad corners: a = top@t0, b = top@t1, c = base@t1, d = base@t0.
        // Wound (b,a,d)+(b,d,c): top edge b->a = t1->t0 (opposes the surface).
        double ax = t0.x, ay = t0.y, az = t0.z;
        double bx = t1.x, by = t1.y, bz = t1.z;
        double cx = t1.x, cy = baseY1, cz = t1.z;
        double dx = t0.x, dy = baseY0, dz = t0.z;

        // Face normal of the wound triangle (b,a,d): cross(a-b, d-b).
        double ux = ax - bx, uy = ay - by, uz = az - bz;
        double vx = dx - bx, vy = dy - by, vz = dz - bz;
        double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
        double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        float wnx = nl > 1e-12 ? (float)(nx / nl) : 0f;
        float wny = nl > 1e-12 ? (float)(ny / nl) : 0f;
        float wnz = nl > 1e-12 ? (float)(nz / nl) : 0f;

        int a = verts.Count; verts.Add(new Vert(ax, ay, az, c0, wnx, wny, wnz));
        int b = verts.Count; verts.Add(new Vert(bx, by, bz, c1, wnx, wny, wnz));
        int c = verts.Count; verts.Add(new Vert(cx, cy, cz, c1, wnx, wny, wnz));
        int d = verts.Count; verts.Add(new Vert(dx, dy, dz, c0, wnx, wny, wnz));
        tris.Add((b, a, d));
        tris.Add((b, d, c));
    }

    // 3x3 median filter (despike). Writes into a scratch copy then back.
    private static void MedianFilter3x3(float[] hh, int w, int h)
    {
        if (w < 3 || h < 3) return;
        float[] src = (float[])hh.Clone();
        Span<float> win = stackalloc float[9];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int n = 0;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int xx = x + dx, yy = y + dy;
                if (xx < 0 || xx >= w || yy < 0 || yy >= h) continue;
                win[n++] = src[yy * w + xx];
            }
            // Insertion sort of the small window, take the middle element.
            for (int i = 1; i < n; i++)
            {
                float v = win[i]; int j = i - 1;
                while (j >= 0 && win[j] > v) { win[j + 1] = win[j]; j--; }
                win[j + 1] = v;
            }
            hh[y * w + x] = win[n / 2];
        }
    }

    // Grayscale 3x3 dilate (local max) — one step of morphological growth.
    private static void GrayDilate3x3(float[] hh, int w, int h)
    {
        if (w < 3 || h < 3) return;
        float[] src = (float[])hh.Clone();
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            float mx = src[y * w + x];
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int xx = x + dx, yy = y + dy;
                if (xx < 0 || xx >= w || yy < 0 || yy >= h) continue;
                float v = src[yy * w + xx]; if (v > mx) mx = v;
            }
            hh[y * w + x] = mx;
        }
    }

    // Grayscale 3x3 erode (local min) — one step of morphological shrink.
    private static void GrayErode3x3(float[] hh, int w, int h)
    {
        if (w < 3 || h < 3) return;
        float[] src = (float[])hh.Clone();
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            float mn = src[y * w + x];
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int xx = x + dx, yy = y + dy;
                if (xx < 0 || xx >= w || yy < 0 || yy >= h) continue;
                float v = src[yy * w + xx]; if (v < mn) mn = v;
            }
            hh[y * w + x] = mn;
        }
    }

    // 3x3 box blur (one pass ~ a light gaussian). Clamped at borders. Used to
    // low-pass the height so tall boundary cliffs become slopes, not fingers.
    private static void BoxBlur3x3(float[] hh, int w, int h)
    {
        if (w < 3 || h < 3) return;
        float[] src = (float[])hh.Clone();
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            float sum = 0f; int n = 0;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int xx = x + dx, yy = y + dy;
                if (xx < 0 || xx >= w || yy < 0 || yy >= h) continue;
                sum += src[yy * w + xx]; n++;
            }
            hh[y * w + x] = sum / n;
        }
    }

    // Morphological open (erode->dilate: drop isolated kept cells) then close
    // (dilate->erode: fill isolated holes) on the keep mask, so isolation
    // exports don't sprout lone pillars or riddle the surface with holes.
    private static void CleanMask(bool[] keep, int w, int h)
    {
        Erode(keep, w, h); Dilate(keep, w, h);   // open
        Dilate(keep, w, h); Erode(keep, w, h);   // close
    }

    private static void Erode(bool[] m, int w, int h)
    {
        bool[] src = (bool[])m.Clone();
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            if (!src[y * w + x]) { m[y * w + x] = false; continue; }
            // Kept only if all 4-neighbours (clamped) are kept.
            bool all = src[Math.Max(0, y - 1) * w + x] && src[Math.Min(h - 1, y + 1) * w + x]
                    && src[y * w + Math.Max(0, x - 1)] && src[y * w + Math.Min(w - 1, x + 1)];
            m[y * w + x] = all;
        }
    }

    private static void Dilate(bool[] m, int w, int h)
    {
        bool[] src = (bool[])m.Clone();
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            if (src[y * w + x]) { m[y * w + x] = true; continue; }
            bool any = src[Math.Max(0, y - 1) * w + x] || src[Math.Min(h - 1, y + 1) * w + x]
                    || src[y * w + Math.Max(0, x - 1)] || src[y * w + Math.Min(w - 1, x + 1)];
            m[y * w + x] = any;
        }
    }

    // #135 cull mask on the export grid — same detail-quantile + colour-drop
    // logic as HeightfieldRaymarch2D. All-true when isolation is off.
    private static bool[] BuildKeepMask(float[] hbuf, uint[] albedo, int w, int h, FractalParameters p)
    {
        int n = w * h;
        var keep = new bool[n];
        for (int i = 0; i < n; i++) keep[i] = true;
        if (!p.Relief2DIsolate) return keep;

        bool byDetail = p.Relief2DIsolateByDetail;
        uint[] drops = Rendering.Lighting.HeightfieldRaymarch2D.ParseDropColors(p.Relief2DDropColorsCsv);
        bool byColor = p.Relief2DIsolateByColor && drops.Length > 0;
        if (!byDetail && !byColor) return keep;

        double thr = Math.Clamp(p.Relief2DDetailThreshold, 0.0, 1.0);
        double tol = Math.Clamp(p.Relief2DColorTolerance, 0.0, 1.0) * 441.6729;

        double keepDetail = 0.0;
        if (byDetail)
        {
            const int BINS = 512;
            int[] hist = new int[BINS];
            double maxDetail = 1e-9;
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float c = hbuf[y * w + x];
                double dx = hbuf[y * w + Math.Min(x + 1, w - 1)] - c;
                double dz = hbuf[Math.Min(y + 1, h - 1) * w + x] - c;
                double d = Math.Sqrt(dx * dx + dz * dz);
                if (d > maxDetail) maxDetail = d;
            }
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float c = hbuf[y * w + x];
                double dx = hbuf[y * w + Math.Min(x + 1, w - 1)] - c;
                double dz = hbuf[Math.Min(y + 1, h - 1) * w + x] - c;
                double d = Math.Sqrt(dx * dx + dz * dz);
                int b = (int)(d / maxDetail * (BINS - 1));
                hist[Math.Clamp(b, 0, BINS - 1)]++;
            }
            int target = (int)(thr * n), cum = 0, tb = 0;
            for (int b = 0; b < BINS; b++) { cum += hist[b]; if (cum >= target) { tb = b; break; } }
            keepDetail = (tb + 1) / (double)BINS * maxDetail;
        }

        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = y * w + x;
            bool drop = false;
            if (byDetail)
            {
                float c = hbuf[i];
                double dx = hbuf[y * w + Math.Min(x + 1, w - 1)] - c;
                double dz = hbuf[Math.Min(y + 1, h - 1) * w + x] - c;
                if (Math.Sqrt(dx * dx + dz * dz) < keepDetail) drop = true;
            }
            if (!drop && byColor)
            {
                uint a = albedo[i];
                double ar = (a >> 16) & 0xFF, ag = (a >> 8) & 0xFF, ab = a & 0xFF;
                foreach (uint dc in drops)
                {
                    double dr = ar - ((dc >> 16) & 0xFF);
                    double dg = ag - ((dc >> 8) & 0xFF);
                    double db = ab - (dc & 0xFF);
                    if (Math.Sqrt(dr * dr + dg * dg + db * db) <= tol) { drop = true; break; }
                }
            }
            keep[i] = !drop;
        }
        return keep;
    }

    // #141 exterior baseline subtraction, mirroring HeightfieldRaymarch2D.
    // Subtract a low percentile (60th) of the nonzero heights so the flat
    // far-from-set plateau drops back to the base plane; only structure rises.
    private static void SubtractExteriorBaseline(float[] hh)
    {
        float hmax = 0f;
        for (int i = 0; i < hh.Length; i++) { float hv = hh[i]; if (hv > hmax) hmax = hv; }
        if (hmax <= 1e-9f) return;

        const int B = 512;
        int[] hist = new int[B];
        int nz = 0;
        for (int i = 0; i < hh.Length; i++)
        {
            float hv = hh[i];
            if (hv > 0f) { hist[Math.Clamp((int)(hv / hmax * (B - 1)), 0, B - 1)]++; nz++; }
        }
        if (nz == 0) return;

        int target = (int)(0.60 * nz), cum = 0;
        float baseline = 0f;
        for (int b = 0; b < B; b++) { cum += hist[b]; if (cum >= target) { baseline = (b + 0.5f) / B * hmax; break; } }
        if (baseline <= 0f) return;
        for (int i = 0; i < hh.Length; i++)
            hh[i] = hh[i] > baseline ? hh[i] - baseline : 0f;
    }

    private static double Smoothstep(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    private static void WriteObj(string path, List<Vert> verts, List<(int a, int b, int c)> tris)
    {
        var sb = new StringBuilder(verts.Count * 60 + tris.Count * 24);
        sb.Append("# Fracturing Fog heightfield export (#138) — smooth normals\n");
        var ci = CultureInfo.InvariantCulture;
        foreach (var v in verts)
        {
            double r = ((v.C >> 16) & 0xFF) / 255.0;
            double g = ((v.C >> 8) & 0xFF) / 255.0;
            double b = (v.C & 0xFF) / 255.0;
            sb.Append(string.Create(ci, $"v {v.X:0.#####} {v.Y:0.#####} {v.Z:0.#####} {r:0.###} {g:0.###} {b:0.###}\n"));
        }
        foreach (var v in verts)
            sb.Append(string.Create(ci, $"vn {v.Nx:0.####} {v.Ny:0.####} {v.Nz:0.####}\n"));
        // Vertex and normal indices are 1-based and share the same numbering.
        foreach (var t in tris)
            sb.Append(string.Create(ci,
                $"f {t.a + 1}//{t.a + 1} {t.b + 1}//{t.b + 1} {t.c + 1}//{t.c + 1}\n"));
        File.WriteAllText(path, sb.ToString());
    }

    // Binary little-endian PLY with per-vertex COLOUR (roadmap S9.3, #391) — the
    // palette idiom crossing into mesh: the exported solid carries the fractal's
    // theme as vertex colours, so a colour print (3MF/PLY slicers) or a web/Blender
    // drop-in lands dressed instead of grey clay. STL (WriteStl) cannot hold colour
    // and OBJ vertex colour is a non-standard extension many tools ignore; PLY is
    // the widely-read format built for it (MeshLab, Blender, Meshmixer, colour
    // slicers). Positions + smooth normals + RGB per vertex; the same watertight,
    // outward-wound topology WriteStl/WriteObj emit.
    private static void WritePly(string path, List<Vert> verts, List<(int a, int b, int c)> tris)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        // Header is ASCII; the body is binary little-endian. Write the header bytes
        // directly so no encoder inserts a BOM or CRLF that would corrupt the offset.
        var header =
            "ply\n" +
            "format binary_little_endian 1.0\n" +
            "comment Fracturing Fog relief mesh export (S9.3 vertex colour)\n" +
            $"element vertex {verts.Count}\n" +
            "property float x\nproperty float y\nproperty float z\n" +
            "property float nx\nproperty float ny\nproperty float nz\n" +
            "property uchar red\nproperty uchar green\nproperty uchar blue\n" +
            $"element face {tris.Count}\n" +
            "property list uchar int vertex_indices\n" +
            "end_header\n";
        var headerBytes = System.Text.Encoding.ASCII.GetBytes(header);
        fs.Write(headerBytes, 0, headerBytes.Length);

        using var bw = new BinaryWriter(fs);
        foreach (var v in verts)
        {
            bw.Write((float)v.X); bw.Write((float)v.Y); bw.Write((float)v.Z);
            bw.Write(v.Nx); bw.Write(v.Ny); bw.Write(v.Nz);
            bw.Write((byte)((v.C >> 16) & 0xFF));   // red
            bw.Write((byte)((v.C >> 8) & 0xFF));    // green
            bw.Write((byte)(v.C & 0xFF));           // blue
        }
        foreach (var t in tris)
        {
            bw.Write((byte)3);
            bw.Write(t.a); bw.Write(t.b); bw.Write(t.c);
        }
    }

    // glTF 2.0 (.glb / .gltf) with a PBR material + per-vertex COLOR_0 (roadmap
    // S9.4, #391) — carries the theme AND a shaded material, so the relief solid
    // opens in Blender / a web viewer dressed, not grey clay. Same watertight,
    // outward-wound topology the other writers emit. Base colour left white so the
    // vertex colours drive the albedo; matte (metallic 0, roughness 0.8) reads
    // print-like.
    private static void WriteGltf(string path, List<Vert> verts, List<(int a, int b, int c)> tris)
    {
        var pos = new List<(double, double, double)>(verts.Count);
        var nrm = new List<(float, float, float)>(verts.Count);
        var col = new List<uint>(verts.Count);
        foreach (var v in verts) { pos.Add((v.X, v.Y, v.Z)); nrm.Add((v.Nx, v.Ny, v.Nz)); col.Add(v.C); }
        GltfMeshWriter.Write(path, pos, nrm, col, tris, GltfMeshWriter.PbrMaterial.MatteWhite);
    }

    private static void WriteStl(string path, List<Vert> verts, List<(int a, int b, int c)> tris)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);
        bw.Write(new byte[80]);              // header
        bw.Write((uint)tris.Count);
        foreach (var t in tris)
        {
            var va = verts[t.a]; var vb = verts[t.b]; var vc = verts[t.c];
            // Face normal (STL is faceted — per-face only).
            double ux = vb.X - va.X, uy = vb.Y - va.Y, uz = vb.Z - va.Z;
            double wx = vc.X - va.X, wy = vc.Y - va.Y, wz = vc.Z - va.Z;
            double nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
            double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (nl > 1e-12) { nx /= nl; ny /= nl; nz /= nl; }
            bw.Write((float)nx); bw.Write((float)ny); bw.Write((float)nz);
            bw.Write((float)va.X); bw.Write((float)va.Y); bw.Write((float)va.Z);
            bw.Write((float)vb.X); bw.Write((float)vb.Y); bw.Write((float)vb.Z);
            bw.Write((float)vc.X); bw.Write((float)vc.Y); bw.Write((float)vc.Z);
            bw.Write((ushort)0); // attribute byte count
        }
    }
}
