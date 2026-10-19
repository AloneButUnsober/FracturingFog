// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Imaging/Qr/QrMesh.cs
//
// #1411 (3D-look QR, phase C of #1408) - export the fractal QR as a 3D-printable
// tile: a base plate, raised dark modules, optionally on terrain taken from the
// rendered fractal. Units are millimetres.
//
// Not HeightfieldMeshExporter: its despike / close / blur chain is tuned to turn
// a fractal height field into a clean emboss, and it would round the blocks off.
// A QR wants crisp walls, so the tile is a field of flat-topped COLUMNS, one per
// cell (cellsPerModule^2 cells per module):
//   - top: one quad per cell at its height;
//   - bottom: one quad per cell at 0;
//   - walls: wherever two neighbouring cells (or a cell and the outside, height
//     0) differ, a vertical wall between the two heights. A wall's vertical
//     sides are split at EVERY height present at that grid corner, and the wall
//     is zipper-triangulated between the two sides, so walls meeting at a corner
//     share edges exactly: no T-junctions, watertight and 2-manifold by
//     construction. MeshValidator checks it anyway.
//
// Colours (OBJ / 3MF): the flat composite of the rendered view (Steer / Bevel use
// the unlit Bevel bands, so module tones are clean), sampled per cell. STL is
// geometry only: a two-material print swaps filament at the module height (the
// print recipe is in the Capture Guide).
//
// Frames: OBJ is Y-up (as the other FF mesh exports); STL and 3MF are Z-up, the
// slicer convention, so the tile lies flat on the bed. Both read unmirrored from
// above.
//
// Software scan check: the mesh is rasterised top-down (its top faces) and
// decoded with QrVerifier. With colours it uses the vertex colours; without, the
// height (modules above the threshold read dark), which is what a two-material
// print shows.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

using FracturingFog.Export;

namespace FracturingFog.Imaging.Qr
{
    /// <summary>What an exported QR tile is and whether it passes the checks.</summary>
    public sealed record QrMeshResult(int Triangles, MeshReport Report, QrVerifyResult Scan, QrCode Code,
        double SizeMm, double HeightMm)
    {
        public string Summary => FormattableString.Invariant(
            $"{Triangles} triangles, {SizeMm:0.#} x {SizeMm:0.#} x {HeightMm:0.##} mm, ")
            + (Report.IsClosedManifold ? "watertight" : "NOT watertight")
            + "; top-down scan: " + Scan.Summary;
    }

    public static class QrMesh
    {
        /// <summary>The tile as built: Y-up millimetres, per-vertex colour, CCW-outward
        /// triangles (a soup; coincident vertices are welded by consumers).</summary>
        public sealed class Tile
        {
            public List<(double X, double Y, double Z)> Positions { get; } = new();
            public List<uint> Colors { get; } = new();
            public List<(int A, int B, int C)> Triangles { get; } = new();
            public QrCode Code { get; init; } = null!;
            public string Payload { get; init; } = "";
            public int Cells { get; init; }
            public int CellsPerModule { get; init; }
            public double CellMm { get; init; }
            public double SizeMm { get; init; }
            public double BaseMm { get; init; }
            public double ModuleHeightMm { get; init; }
            public double TerrainMm { get; init; }
            public double MaxHeightMm { get; set; }
            /// <summary>Cells raised to remove diagonal-only contacts (see RemoveSaddles).</summary>
            public int SaddleCellsRaised { get; init; }
        }

        public const int CellsPerModuleDefault = 4;

        /// <summary>Two columns that touch only along a vertical corner line (raised
        /// NW + SE, low NE + SW, or the mirror) make that line an edge of four walls:
        /// not 2-manifold, and slicers may treat the two blocks as one point of
        /// contact. At every such grid corner the taller of the low pair is raised to
        /// the lower of the high pair, which leaves at most one column above any level
        /// around the corner. Heights only rise to values already present, so the
        /// passes converge. For QR blocks this fills one corner cell (1/16 module) of
        /// a light module; with terrain it is a small height nudge. Returns the number
        /// of cells raised.</summary>
        public static int RemoveSaddles(double[] height, int n)
        {
            int raised = 0;
            for (bool changed = true; changed;)
            {
                changed = false;
                for (int j = 1; j < n; j++)
                    for (int i = 1; i < n; i++)
                    {
                        int nw = (j - 1) * n + i - 1, ne = (j - 1) * n + i, sw = j * n + i - 1, se = j * n + i;
                        double a = height[nw], b = height[ne], c = height[se], d = height[sw];
                        if (Math.Min(a, c) > Math.Max(b, d))
                        {
                            int k = b >= d ? ne : sw;
                            height[k] = Math.Min(a, c); raised++; changed = true;
                        }
                        else if (Math.Min(b, d) > Math.Max(a, c))
                        {
                            int k = a >= c ? nw : se;
                            height[k] = Math.Min(b, d); raised++; changed = true;
                        }
                    }
            }
            return raised;
        }

        /// <summary>Build the tile for <paramref name="opt"/> from the rendered view
        /// <paramref name="display"/> (w x h, the final display buffer without the QR).</summary>
        public static Tile Build(uint[] display, int w, int h, QrCodeOptions opt)
        {
            string? err = QrExport.Validate(opt);
            if (err != null) throw new ArgumentException(err, nameof(opt));
            double size = Math.Clamp(opt.MeshSizeMm, QrCodeOptions.MinMeshSizeMm, QrCodeOptions.MaxMeshSizeMm);
            double baseMm = Math.Clamp(opt.MeshBaseMm, QrCodeOptions.MinMeshBaseMm, QrCodeOptions.MaxMeshBaseMm);
            double modH = Math.Clamp(opt.MeshHeightMm, QrCodeOptions.MinMeshHeightMm, QrCodeOptions.MaxMeshHeightMm);
            double terrain = Math.Clamp(opt.MeshTerrainMm, 0, QrCodeOptions.MaxMeshTerrainMm);

            // The code + its flat composite (decode-verified mask choice, as the image).
            var composite = (uint[])display.Clone();
            bool smooth = opt.Style is QrStyle.Steer or QrStyle.Bevel;
            var r = smooth
                ? QrExport.Apply(composite, w, h, opt with { Style = QrStyle.Bevel }, QrBevelLighting.None)
                : QrExport.Apply(composite, w, h, opt);
            var code = r.Code;
            var L = r.Layout;

            int span = code.Size + 2 * QrCode.QuietZone;
            // 4 cells per module: the saddle fix below fills one cell (1/16 module).
            const int cpm = CellsPerModuleDefault;
            int n = span * cpm;
            double cellMm = size / n;
            double cellPx = (double)L.ModulePixels / cpm;
            double x0px = L.OriginX - L.QuietPixels, y0px = L.OriginY - L.QuietPixels;
            bool invert = opt.Invert;

            var height = new double[n * n];
            var color = new uint[n * n];
            for (int cy = 0; cy < n; cy++)
                for (int cx = 0; cx < n; cx++)
                {
                    int px0 = (int)Math.Floor(x0px + cx * cellPx), px1 = Math.Max(px0 + 1, (int)Math.Floor(x0px + (cx + 1) * cellPx));
                    int py0 = (int)Math.Floor(y0px + cy * cellPx), py1 = Math.Max(py0 + 1, (int)Math.Floor(y0px + (cy + 1) * cellPx));
                    long sr = 0, sg = 0, sb = 0, sl = 0; int cnt = 0;
                    for (int y = Math.Max(0, py0); y < Math.Min(h, py1); y++)
                        for (int x = Math.Max(0, px0); x < Math.Min(w, px1); x++)
                        {
                            uint c = composite[y * w + x];
                            sr += (c >> 16) & 0xFF; sg += (c >> 8) & 0xFF; sb += c & 0xFF;
                            sl += QrVerifier.LumaOf(display[y * w + x]);
                            cnt++;
                        }
                    int i = cy * n + cx;
                    color[i] = cnt == 0 ? 0xFFFFFFFFu
                        : 0xFF000000u | (uint)(sr / cnt) << 16 | (uint)(sg / cnt) << 8 | (uint)(sb / cnt);
                    double t = cnt == 0 ? 0 : sl / (255.0 * cnt);
                    int mx = cx / cpm - QrCode.QuietZone, my = cy / cpm - QrCode.QuietZone;
                    bool raised = mx >= 0 && my >= 0 && mx < code.Size && my < code.Size && (code.Dark[my, mx] ^ invert);
                    height[i] = Round(baseMm + terrain * t + (raised ? modH : 0));
                }

            int filled = RemoveSaddles(height, n);

            var tile = new Tile
            {
                Code = code, Payload = opt.Payload, Cells = n, CellsPerModule = cpm, CellMm = cellMm, SizeMm = size,
                BaseMm = baseMm, ModuleHeightMm = modH, TerrainMm = terrain, SaddleCellsRaised = filled,
            };
            Mesh(tile, height, color, n, cellMm);
            tile.MaxHeightMm = height.Max();
            return tile;
        }

        private static double Round(double v) => Math.Round(v, 4);

        private static void Mesh(Tile t, double[] height, uint[] color, int n, double cell)
        {
            double H(int cx, int cy) => cx < 0 || cy < 0 || cx >= n || cy >= n ? 0.0 : height[cy * n + cx];
            uint C(int cx, int cy) => cx < 0 || cy < 0 || cx >= n || cy >= n ? 0xFFFFFFFFu : color[cy * n + cx];

            // Distinct heights at each grid corner (the four cells around it + 0).
            var cornerHeights = new double[(n + 1) * (n + 1)][];
            for (int j = 0; j <= n; j++)
                for (int i = 0; i <= n; i++)
                {
                    var s = new SortedSet<double> { 0.0, H(i - 1, j - 1), H(i, j - 1), H(i - 1, j), H(i, j) };
                    cornerHeights[j * (n + 1) + i] = s.ToArray();
                }

            int V(double x, double y, double z, uint c)
            {
                t.Positions.Add((x, y, z));
                t.Colors.Add(c);
                return t.Positions.Count - 1;
            }

            for (int cy = 0; cy < n; cy++)
                for (int cx = 0; cx < n; cx++)
                {
                    double hv = H(cx, cy);
                    uint c = C(cx, cy);
                    double x0 = cx * cell, x1 = (cx + 1) * cell, z0 = cy * cell, z1 = (cy + 1) * cell;
                    // Top (+Y): (p00, p01, p11), (p00, p11, p10).
                    int a = V(x0, hv, z0, c), b = V(x0, hv, z1, c), d = V(x1, hv, z1, c), e = V(x1, hv, z0, c);
                    t.Triangles.Add((a, b, d)); t.Triangles.Add((a, d, e));
                    // Bottom (-Y): reversed.
                    int a2 = V(x0, 0, z0, c), b2 = V(x0, 0, z1, c), d2 = V(x1, 0, z1, c), e2 = V(x1, 0, z0, c);
                    t.Triangles.Add((a2, d2, b2)); t.Triangles.Add((a2, e2, d2));
                }

            // Walls on x = const lines (between cells (i-1, j) and (i, j)).
            for (int j = 0; j < n; j++)
                for (int i = 0; i <= n; i++)
                {
                    double hl = H(i - 1, j), hr = H(i, j);
                    if (hl == hr) continue;
                    // Outward = from the taller cell toward the lower one.
                    double dirX = hl > hr ? 1 : -1;
                    uint c = hl > hr ? C(i - 1, j) : C(i, j);
                    Wall(t, (i * cell, j * cell), (i * cell, (j + 1) * cell),
                        cornerHeights[j * (n + 1) + i], cornerHeights[(j + 1) * (n + 1) + i],
                        Math.Min(hl, hr), Math.Max(hl, hr), (dirX, 0), c, V);
                }
            // Walls on z = const lines (between cells (i, j-1) and (i, j)).
            for (int j = 0; j <= n; j++)
                for (int i = 0; i < n; i++)
                {
                    double hu = H(i, j - 1), hd = H(i, j);
                    if (hu == hd) continue;
                    double dirZ = hu > hd ? 1 : -1;
                    uint c = hu > hd ? C(i, j - 1) : C(i, j);
                    Wall(t, (i * cell, j * cell), ((i + 1) * cell, j * cell),
                        cornerHeights[j * (n + 1) + i], cornerHeights[j * (n + 1) + i + 1],
                        Math.Min(hu, hd), Math.Max(hu, hd), (0, dirZ), c, V);
                }
        }

        /// <summary>A vertical wall from height lo to hi along P -> Q, its sides split
        /// at every corner height in [lo, hi], zipper-triangulated and wound so its
        /// normal points along <paramref name="outward"/> (XZ).</summary>
        private static void Wall(Tile t, (double X, double Z) p, (double X, double Z) q,
            double[] hp, double[] hq, double lo, double hi, (double X, double Z) outward, uint c,
            Func<double, double, double, uint, int> V)
        {
            var left = hp.Where(v => v >= lo && v <= hi).Select(v => V(p.X, v, p.Z, c)).ToList();
            var right = hq.Where(v => v >= lo && v <= hi).Select(v => V(q.X, v, q.Z, c)).ToList();
            int i = 0, j = 0;
            while (i < left.Count - 1 || j < right.Count - 1)
            {
                bool advanceLeft = j == right.Count - 1
                    || (i < left.Count - 1 && t.Positions[left[i + 1]].Y <= t.Positions[right[j + 1]].Y);
                if (advanceLeft) { Tri(t, left[i], right[j], left[i + 1], outward); i++; }
                else { Tri(t, left[i], right[j], right[j + 1], outward); j++; }
            }
        }

        private static void Tri(Tile t, int a, int b, int c, (double X, double Z) outward)
        {
            var pa = t.Positions[a]; var pb = t.Positions[b]; var pc = t.Positions[c];
            double ux = pb.X - pa.X, uy = pb.Y - pa.Y, uz = pb.Z - pa.Z;
            double vx = pc.X - pa.X, vy = pc.Y - pa.Y, vz = pc.Z - pa.Z;
            double nx = uy * vz - uz * vy, nz = ux * vy - uy * vx;
            if (nx * outward.X + nz * outward.Z < 0) t.Triangles.Add((a, c, b));
            else t.Triangles.Add((a, b, c));
        }

        // ── export ─────────────────────────────────────────────────────────

        /// <summary>Build, write (.stl binary, .3mf, else OBJ with vertex colour), validate
        /// and scan-check the tile.</summary>
        public static QrMeshResult Export(uint[] display, int w, int h, QrCodeOptions opt, string path)
        {
            var tile = Build(display, w, h, opt);
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".stl") WriteStl(path, tile);
            else if (ext == ".3mf") ThreeMfMeshWriter.Write(path, ZUp(tile), tile.Colors, tile.Triangles);
            else WriteObj(path, tile);

            var report = MeshValidator.Validate(tile.Positions, tile.Triangles);
            // STL carries no colour: check what a two-material print shows (height);
            // OBJ / 3MF: the colours.
            var scan = ScanTopDown(tile, useColor: ext != ".stl");
            return new QrMeshResult(tile.Triangles.Count, report, scan, tile.Code, tile.SizeMm, tile.MaxHeightMm);
        }

        /// <summary>Y-up tile → Z-up print frame (a proper rotation: winding kept),
        /// shifted so all coordinates are positive.</summary>
        public static List<(double X, double Y, double Z)> ZUp(Tile t)
            => t.Positions.Select(p => (p.X, t.SizeMm - p.Z, p.Y)).ToList();

        private static void WriteStl(string path, Tile t)
        {
            var pos = ZUp(t);
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs);
            var header = new byte[80];
            Encoding.ASCII.GetBytes("Fracturing Fog QR tile (#1411), mm, Z up").CopyTo(header, 0);
            bw.Write(header);
            bw.Write((uint)t.Triangles.Count);
            foreach (var (a, b, c) in t.Triangles)
            {
                var pa = pos[a]; var pb = pos[b]; var pc = pos[c];
                double ux = pb.X - pa.X, uy = pb.Y - pa.Y, uz = pb.Z - pa.Z;
                double vx = pc.X - pa.X, vy = pc.Y - pa.Y, vz = pc.Z - pa.Z;
                double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                double l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (l > 0) { nx /= l; ny /= l; nz /= l; }
                bw.Write((float)nx); bw.Write((float)ny); bw.Write((float)nz);
                foreach (var p in new[] { pa, pb, pc }) { bw.Write((float)p.X); bw.Write((float)p.Y); bw.Write((float)p.Z); }
                bw.Write((ushort)0);
            }
        }

        private static void WriteObj(string path, Tile t)
        {
            var inv = CultureInfo.InvariantCulture;
            using var sw = new StreamWriter(path, false, new UTF8Encoding(false));
            sw.Write("# Fracturing Fog QR tile (#1411), millimetres, Y up, vertex colour\n");
            for (int i = 0; i < t.Positions.Count; i++)
            {
                var p = t.Positions[i]; uint c = t.Colors[i];
                sw.Write(string.Format(inv, "v {0:0.####} {1:0.####} {2:0.####} {3:0.###} {4:0.###} {5:0.###}\n",
                    p.X, p.Y, p.Z, ((c >> 16) & 0xFF) / 255.0, ((c >> 8) & 0xFF) / 255.0, (c & 0xFF) / 255.0));
            }
            foreach (var (a, b, c) in t.Triangles)
                sw.Write(string.Format(inv, "f {0} {1} {2}\n", a + 1, b + 1, c + 1));
        }

        // ── software scan check ────────────────────────────────────────────

        /// <summary>Rasterise the tile's top faces looking straight down (Y-up frame,
        /// x right, z down the image) and decode. <paramref name="useColor"/>: vertex
        /// colours; otherwise height (above base + terrain + half a module = dark).</summary>
        public static QrVerifyResult ScanTopDown(Tile t, bool useColor, int pixelsPerModule = 8)
        {
            var img = RenderTopDown(t.Positions, useColor ? t.Colors : null, t.Triangles, t.SizeMm,
                t.Cells / t.CellsPerModule * pixelsPerModule,
                t.BaseMm + t.TerrainMm + 0.5 * t.ModuleHeightMm, out int side);
            return QrVerifier.Verify(img, side, side, t.Payload, pixelsPerModule);
        }

        /// <summary>Top-down orthographic raster of a Y-up mesh over [0, size] in x and
        /// z: per pixel the highest upward face wins; its vertex colour, or (no colours)
        /// black above <paramref name="darkAboveMm"/>, white below.</summary>
        public static uint[] RenderTopDown(IReadOnlyList<(double X, double Y, double Z)> pos, IReadOnlyList<uint>? colors,
            IReadOnlyList<(int A, int B, int C)> tris, double sizeMm, int side, double darkAboveMm, out int outSide)
        {
            outSide = side;
            var img = Enumerable.Repeat(0xFFFFFFFFu, side * side).ToArray();
            var depth = Enumerable.Repeat(double.NegativeInfinity, side * side).ToArray();
            double s = side / sizeMm;
            foreach (var (a, b, c) in tris)
            {
                var pa = pos[a]; var pb = pos[b]; var pc = pos[c];
                // Upward faces only (normal Y > 0).
                double ny = (pb.Z - pa.Z) * (pc.X - pa.X) - (pb.X - pa.X) * (pc.Z - pa.Z);
                if (ny <= 1e-12) continue;
                double minX = Math.Min(pa.X, Math.Min(pb.X, pc.X)) * s, maxX = Math.Max(pa.X, Math.Max(pb.X, pc.X)) * s;
                double minZ = Math.Min(pa.Z, Math.Min(pb.Z, pc.Z)) * s, maxZ = Math.Max(pa.Z, Math.Max(pb.Z, pc.Z)) * s;
                double y = (pa.Y + pb.Y + pc.Y) / 3;
                uint col = colors != null ? colors[a] : (y > darkAboveMm ? 0xFF000000u : 0xFFFFFFFFu);
                for (int py = Math.Max(0, (int)Math.Floor(minZ)); py < Math.Min(side, (int)Math.Ceiling(maxZ)); py++)
                    for (int px = Math.Max(0, (int)Math.Floor(minX)); px < Math.Min(side, (int)Math.Ceiling(maxX)); px++)
                    {
                        double x = (px + 0.5) / s, z = (py + 0.5) / s;
                        if (!Inside(x, z, pa, pb, pc)) continue;
                        int i = py * side + px;
                        if (y > depth[i]) { depth[i] = y; img[i] = col; }
                    }
            }
            return img;
        }

        private static bool Inside(double x, double z, (double X, double Y, double Z) a, (double X, double Y, double Z) b, (double X, double Y, double Z) c)
        {
            double d1 = (x - b.X) * (a.Z - b.Z) - (a.X - b.X) * (z - b.Z);
            double d2 = (x - c.X) * (b.Z - c.Z) - (b.X - c.X) * (z - c.Z);
            double d3 = (x - a.X) * (c.Z - a.Z) - (c.X - a.X) * (z - a.Z);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }
    }
}
