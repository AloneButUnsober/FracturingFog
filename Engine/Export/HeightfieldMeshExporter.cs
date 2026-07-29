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
// colour from the theme) or binary STL.
//
// Mesh grid matches the raymarch world frame: X in [-aspect/2, +aspect/2],
// Z in [-0.5, +0.5], Y up, height = curve(smooth) normalised x 0.35 x heightScale.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

using FracturingFog.Models;

namespace FracturingFog.Export;

public static class HeightfieldMeshExporter
{
    /// <summary>Export the heightfield object to <paramref name="path"/> (.stl =
    /// binary STL, else OBJ with vertex colour). Returns the triangle count.
    /// <paramref name="targetGrid"/> caps the longer grid axis (downsample) so
    /// the file stays manageable. No-op (returns 0) on an all-flat field.</summary>
    public static int Export(uint[] albedo, float[] height, int w, int h,
                             FractalParameters p, string path, int targetGrid = 512)
    {
        if (w < 2 || h < 2 || height.Length < w * h) return 0;

        // Downsample stride so the longer axis is ~targetGrid cells.
        int stride = Math.Max(1, (int)Math.Ceiling(Math.Max(w, h) / (double)Math.Max(16, targetGrid)));
        int gw = (w + stride - 1) / stride;
        int gh = (h + stride - 1) / stride;
        if (gw < 2 || gh < 2) return 0;

        // Compressed + edge-faded height on the downsampled grid (mirror
        // HeightfieldRaymarch2D). BOX-AVERAGE the raw field over each stride
        // block instead of point-sampling one pixel: the smooth-count field has
        // sub-cell filaments, and picking a single pixel per cell aliases them
        // into isolated radial SPIKES (the "rough hedgehog" mesh). Averaging the
        // block antialiases the height into the coarse grid so filaments read as
        // continuous ridges, matching the full-res bicubic raymarch. Albedo is
        // box-averaged the same way so vertex colour tracks the averaged height.
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
        // the low far-from-set smooth counts into a raised rectangular PLATEAU
        // (a tabletop). The raymarch strips it so only boundary structure rises;
        // the mesh must too, or the exported solid keeps that plateau as its
        // whole rough base. Same 60th-percentile histogram flatten as the render.
        SubtractExteriorBaseline(hh);

        float maxH = 0f;
        for (int i = 0; i < hh.Length; i++) if (hh[i] > maxH) maxH = hh[i];
        if (maxH <= 1e-9f) return 0;

        // Edge cap (#140) — pull tall edge structure down to the base plane
        // without lifting the flat exterior (no rectangular lip). Matches
        // HeightfieldRaymarch2D.
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
        double sy2 = 0.35 * Math.Max(0.0, p.Relief2DHeightScale) / maxH;

        // #135 isolation cull mask on the downsampled grid (detail quantile +
        // colour drop), matching the raymarch. keep[gi] = true → part of object.
        bool[] keep = BuildKeepMask(hh, alb, gw, gh, p);

        // World position of grid vertex (gx, gy) at its height.
        (double x, double y, double z) V(int gx, int gy)
        {
            double u = (gx + 0.5) / gw, v = (gy + 0.5) / gh;
            return ((u - 0.5) * aspect, hh[gy * gw + gx] * sy2, v - 0.5);
        }

        var verts = new List<(double x, double y, double z, uint c)>();
        var tris = new List<(int a, int b, int c)>();
        int AddV(double x, double y, double z, uint c)
        {
            verts.Add((x, y, z, c));
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
            uint c00 = alb[gy * gw + gx], c10 = alb[gy * gw + gx + 1];
            uint c01 = alb[(gy + 1) * gw + gx], c11 = alb[(gy + 1) * gw + gx + 1];

            // Top surface (CCW viewed from +Y).
            int t00 = AddV(p00.x, p00.y, p00.z, c00);
            int t10 = AddV(p10.x, p10.y, p10.z, c10);
            int t01 = AddV(p01.x, p01.y, p01.z, c01);
            int t11 = AddV(p11.x, p11.y, p11.z, c11);
            tris.Add((t00, t11, t10));
            tris.Add((t00, t01, t11));

            // Base at y=0 (reversed winding, faces down).
            int b00 = AddV(p00.x, 0.0, p00.z, c00);
            int b10 = AddV(p10.x, 0.0, p10.z, c10);
            int b01 = AddV(p01.x, 0.0, p01.z, c01);
            int b11 = AddV(p11.x, 0.0, p11.z, c11);
            tris.Add((b00, b10, b11));
            tris.Add((b00, b11, b01));

            // Skirt walls where a neighbour cell is not part of the object (or
            // outside the grid) — closes the solid at the object boundary.
            // -X edge (gx side):
            if (gx == 0 || !CellKept(gx - 1, gy)) AddWall(tris, verts, p00, p01, c00, c01);
            // +X edge:
            if (gx == gw - 2 || !CellKept(gx + 1, gy)) AddWall(tris, verts, p11, p10, c11, c10);
            // -Z edge (gy side):
            if (gy == 0 || !CellKept(gx, gy - 1)) AddWall(tris, verts, p10, p00, c10, c00);
            // +Z edge:
            if (gy == gh - 2 || !CellKept(gx, gy + 1)) AddWall(tris, verts, p01, p11, c01, c11);
        }

        if (tris.Count == 0) return 0;

        if (path.EndsWith(".stl", StringComparison.OrdinalIgnoreCase))
            WriteStl(path, verts, tris);
        else
            WriteObj(path, verts, tris);
        return tris.Count;
    }

    // A vertical wall quad from the top edge (t0->t1) down to y=0, wound so it
    // faces outward. Two triangles.
    private static void AddWall(
        List<(int, int, int)> tris,
        List<(double x, double y, double z, uint c)> verts,
        (double x, double y, double z) t0, (double x, double y, double z) t1,
        uint c0, uint c1)
    {
        int a = verts.Count; verts.Add((t0.x, t0.y, t0.z, c0));
        int b = verts.Count; verts.Add((t1.x, t1.y, t1.z, c1));
        int c = verts.Count; verts.Add((t1.x, 0.0, t1.z, c1));
        int d = verts.Count; verts.Add((t0.x, 0.0, t0.z, c0));
        tris.Add((a, b, c));
        tris.Add((a, c, d));
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

    private static void WriteObj(string path,
        List<(double x, double y, double z, uint c)> verts, List<(int a, int b, int c)> tris)
    {
        var sb = new StringBuilder(verts.Count * 40 + tris.Count * 20);
        sb.Append("# Fracturing Fog heightfield export (#138)\n");
        var ci = CultureInfo.InvariantCulture;
        foreach (var v in verts)
        {
            double r = ((v.c >> 16) & 0xFF) / 255.0;
            double g = ((v.c >> 8) & 0xFF) / 255.0;
            double b = (v.c & 0xFF) / 255.0;
            sb.Append(string.Create(ci, $"v {v.x:0.#####} {v.y:0.#####} {v.z:0.#####} {r:0.###} {g:0.###} {b:0.###}\n"));
        }
        foreach (var t in tris)
            sb.Append(string.Create(ci, $"f {t.a + 1} {t.b + 1} {t.c + 1}\n"));
        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteStl(string path,
        List<(double x, double y, double z, uint c)> verts, List<(int a, int b, int c)> tris)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);
        bw.Write(new byte[80]);              // header
        bw.Write((uint)tris.Count);
        foreach (var t in tris)
        {
            var va = verts[t.a]; var vb = verts[t.b]; var vc = verts[t.c];
            // Face normal.
            double ux = vb.x - va.x, uy = vb.y - va.y, uz = vb.z - va.z;
            double wx = vc.x - va.x, wy = vc.y - va.y, wz = vc.z - va.z;
            double nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
            double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (nl > 1e-12) { nx /= nl; ny /= nl; nz /= nl; }
            bw.Write((float)nx); bw.Write((float)ny); bw.Write((float)nz);
            bw.Write((float)va.x); bw.Write((float)va.y); bw.Write((float)va.z);
            bw.Write((float)vb.x); bw.Write((float)vb.y); bw.Write((float)vb.z);
            bw.Write((float)vc.x); bw.Write((float)vc.y); bw.Write((float)vc.z);
            bw.Write((ushort)0); // attribute byte count
        }
    }
}
