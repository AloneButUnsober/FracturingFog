// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Models/BilliardLayout.cs (#1503, #1504)
//
// UI-free layout helpers for the chaotic billiard, shared by the Engine scene,
// the params panel and the ray-path inset editor (UI.Avalonia does not reference
// the Engine), and the tests.
//
// Ring / ThreeDisk centres sit evenly on a circle of radius sep, so neighbours are
// 2 sep sin(pi / n) apart; once that is no more than 2R the disks touch or overlap
// and the gaps between them, the exits, are closed.
//
// #1538 — the Polygon geometry: n disks on a regular k-gon of corner radius sep. With
// n >= k there is a disk on every corner and the other n - k are shared out over the
// edges as evenly as possible (the first (n - k) mod k edges get one more), evenly
// spaced along each edge. With n < k the polygon cannot be completed: the disks go on
// n corners spread as evenly as possible, and the panel says so.
//
// #1539 — Fill adds the polygon's interior: the sites of a grid at the perimeter spacing d
// (the side over the disks per edge), anchored on corner 0 and lined up with edge 0. The
// grid is square for 4 and 8 sides and triangular otherwise, so for the triangle, square
// and hexagon the perimeter disks are grid sites themselves and the whole layout is one
// lattice patch. A site is kept when it lies at least d/2 inside every edge and at least
// d from every perimeter disk (with a little slack for rounding); on the other polygons
// that leaves a thin gap along the edges rather than crowding them.
//
// #1539 — SquareLattice / HexLattice: the n sites of a square or triangular grid of
// spacing sep nearest the origin (the origin is a site). Order: squared length in grid
// units (an exact integer: i^2 + j^2, or i^2 + ij + j^2 for the triangular grid), then
// angle, so the patch grows shell by shell and is the same on every machine.
//
// #1504 — the Custom geometry's disk list is a string, "x,y,r;x,y,r;...", so it
// rides the region snapshot and `--param BilliardCustomDisks=...` like any other
// per-family value. CanPlace is the editor's rule for a dragged / resized / added
// disk: radius in range, inside MaxReach, and an escape gap to every other disk.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FracturingFog.Models
{
    public static class BilliardLayout
    {
        /// <summary>Most disks a layout holds (the count control's cap). #1539 raised it from 64
        /// for the lattices and the filled polygon; tracing cost grows with the count.</summary>
        public const int MaxDisks = 256;

        /// <summary>#1539 — the lattice geometries.</summary>
        public static bool IsLattice(BilliardGeometry geometry)
            => geometry is BilliardGeometry.SquareLattice or BilliardGeometry.HexLattice;

        /// <summary>#1539 — lattice rotation range, degrees.</summary>
        public const double MaxLatticeRotation = 180.0;

        /// <summary>#1539 — grid coordinates (i, j) of the <paramref name="n"/> sites nearest the origin,
        /// nearest first: basis (1, 0), (0, 1) for the square grid, (1, 0), (1/2, sqrt3/2) for the
        /// triangular one.</summary>
        public static List<(int I, int J)> LatticeSites(bool hex, int n)
        {
            n = Math.Clamp(n, 1, MaxDisks);
            int reach = (int)Math.Ceiling(Math.Sqrt(n)) + 2;   // covers n sites on either grid
            var sites = new List<(int I, int J, long Norm, double Angle)>();
            for (int i = -2 * reach; i <= 2 * reach; i++)
                for (int j = -2 * reach; j <= 2 * reach; j++)
                {
                    long norm = hex ? (long)i * i + (long)i * j + (long)j * j : (long)i * i + (long)j * j;
                    if (norm > (long)reach * reach) continue;
                    var (x, y) = hex ? (i + 0.5 * j, j * (Math.Sqrt(3.0) / 2.0)) : (i, (double)j);
                    double a = Math.Atan2(y, x);
                    sites.Add((i, j, norm, a < 0 ? a + 2.0 * Math.PI : a));
                }
            sites.Sort((p, q) => p.Norm != q.Norm ? p.Norm.CompareTo(q.Norm) : p.Angle.CompareTo(q.Angle));
            var list = new List<(int, int)>(n);
            for (int k = 0; k < n && k < sites.Count; k++) list.Add((sites[k].I, sites[k].J));
            return list;
        }

        /// <summary>#1539 — world centres of a lattice patch: <see cref="LatticeSites"/> scaled by
        /// <paramref name="spacing"/> and turned by <paramref name="rotationDegrees"/>. Disk 0 is the origin.</summary>
        public static List<(double X, double Y)> LatticeCentres(BilliardGeometry geometry, int n, double spacing, double rotationDegrees)
        {
            bool hex = geometry == BilliardGeometry.HexLattice;
            double t = Math.Clamp(rotationDegrees, -MaxLatticeRotation, MaxLatticeRotation) * (Math.PI / 180.0);
            double c = Math.Cos(t), s = Math.Sin(t);
            var list = new List<(double X, double Y)>();
            foreach (var (i, j) in LatticeSites(hex, n))
            {
                double x = hex ? i + 0.5 * j : i, y = hex ? j * (Math.Sqrt(3.0) / 2.0) : j;
                x *= spacing; y *= spacing;
                list.Add((c * x - s * y, s * x + c * y));
            }
            return list;
        }

        /// <summary>#1539 — the interior grid of a filled polygon (see the file header), nearest the
        /// centre first and at most <paramref name="max"/>; <paramref name="wanted"/> is how many sites
        /// the full fill has.</summary>
        public static List<(double X, double Y)> PolygonFillCentres(int n, int sides, double separation, int max, out int wanted)
        {
            n = Math.Clamp(n, 1, MaxDisks);
            int k = Math.Clamp(sides, MinSides, MaxSides);
            var perim = PolygonCentres(n, k, separation);
            var corner = new (double X, double Y)[k];
            for (int j = 0; j < k; j++)
            {
                double a = 0.0 + j * (2.0 * Math.PI / k);
                corner[j] = (separation * Math.Cos(a), separation * Math.Sin(a));
            }
            double ex = corner[1].X - corner[0].X, ey = corner[1].Y - corner[0].Y;
            double side = Math.Sqrt(ex * ex + ey * ey);
            int perEdge = n >= k ? (n - k) / k : 0;
            double d = side / (perEdge + 1);
            double ux = ex / side * d, uy = ey / side * d;
            double turn = k is 4 or 8 ? Math.PI / 2.0 : Math.PI / 3.0;
            double vx = Math.Cos(turn) * ux - Math.Sin(turn) * uy, vy = Math.Sin(turn) * ux + Math.Cos(turn) * uy;
            // Inward distance from edge j: the polygon is convex and centred on the origin, so it is
            // apothem - (p . outward normal j).
            double apothem = separation * Math.Cos(Math.PI / k);
            var normal = new (double X, double Y)[k];
            for (int j = 0; j < k; j++)
            {
                double a = (j + 0.5) * (2.0 * Math.PI / k);
                normal[j] = (Math.Cos(a), Math.Sin(a));
            }
            const double slack = 1e-6;
            int span = (int)Math.Ceiling(4.0 * separation / d) + 2;
            var sites = new List<(double X, double Y, double R2, double A)>();
            for (int i = -span; i <= span; i++)
                for (int j = -span; j <= span; j++)
                {
                    double x = corner[0].X + i * ux + j * vx, y = corner[0].Y + i * uy + j * vy;
                    bool inside = true;
                    for (int e = 0; e < k && inside; e++)
                        inside = apothem - (x * normal[e].X + y * normal[e].Y) >= d * (0.5 - slack);
                    if (!inside) continue;
                    bool clear = true;
                    foreach (var (px, py) in perim)
                        if ((x - px) * (x - px) + (y - py) * (y - py) < d * d * (1.0 - slack)) { clear = false; break; }
                    if (!clear) continue;
                    double a = Math.Atan2(y, x);
                    sites.Add((x, y, x * x + y * y, a < 0 ? a + 2.0 * Math.PI : a));
                }
            // Round the radius so sites on one ring of the grid sort by angle, not by rounding noise.
            sites.Sort((p, q) =>
            {
                int c = Math.Round(p.R2 / (d * d), 6).CompareTo(Math.Round(q.R2 / (d * d), 6));
                return c != 0 ? c : p.A.CompareTo(q.A);
            });
            wanted = sites.Count;
            var list = new List<(double X, double Y)>();
            for (int s = 0; s < sites.Count && s < max; s++) list.Add((sites[s].X, sites[s].Y));
            return list;
        }

        /// <summary>#1538 — Polygon side range.</summary>
        public const int MinSides = 3, MaxSides = 12;

        /// <summary>#1538 — centres of <paramref name="n"/> disks on a regular <paramref name="sides"/>-gon of
        /// corner radius <paramref name="separation"/>, corner 0 on +x, going anticlockwise. Disk 0 is corner 0.
        /// Corner j sits at the ring position of j (the same expression as a ring of <paramref name="sides"/>),
        /// so a polygon with one disk per corner has exactly the ring's centres.</summary>
        public static List<(double X, double Y)> PolygonCentres(int n, int sides, double separation)
        {
            n = Math.Clamp(n, 1, MaxDisks);
            int k = Math.Clamp(sides, MinSides, MaxSides);
            var corner = new (double X, double Y)[k];
            for (int j = 0; j < k; j++)
            {
                double a = 0.0 + j * (2.0 * Math.PI / k);
                corner[j] = (separation * Math.Cos(a), separation * Math.Sin(a));
            }
            var list = new List<(double X, double Y)>(n);
            if (n < k)
            {
                // Fewer disks than corners: n corners spread as evenly as possible.
                for (int i = 0; i < n; i++) list.Add(corner[(int)((long)i * k / n)]);
                return list;
            }
            int extra = n - k;
            for (int j = 0; j < k; j++)
            {
                list.Add(corner[j]);
                int m = extra / k + (j < extra % k ? 1 : 0);
                var a = corner[j]; var b = corner[(j + 1) % k];
                for (int s = 1; s <= m; s++)
                {
                    double t = s / (double)(m + 1);
                    list.Add((a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y)));
                }
            }
            return list;
        }

        /// <summary>Editor radius range (as the params panel's radius control).</summary>
        public const double MinRadius = 0.05, MaxRadius = 1.5;

        /// <summary>Farthest a custom disk edge may sit from the origin.</summary>
        public const double MaxReach = 10.0;

        /// <summary>Minimum centre distance as a multiple of r_i + r_j: keeps an escape gap
        /// (the NDisk placer's factor).</summary>
        public const double MinGapFactor = 1.05;

        /// <summary>Disks on the ring for a geometry (ThreeDisk is always 3; NDisk / Custom are not rings: 0).</summary>
        public static int RingDiskCount(BilliardGeometry geometry, int diskCount) => geometry switch
        {
            BilliardGeometry.ThreeDisk => 3,
            BilliardGeometry.Ring => Math.Clamp(diskCount, 1, MaxDisks),
            _ => 0,
        };

        /// <summary>Edge-to-edge gap between neighbouring ring disks (negative = overlap).
        /// +infinity when the ring has fewer than two disks.</summary>
        public static double RingNeighbourGap(int n, double diskRadius, double separation)
            => n < 2 ? double.PositiveInfinity
                     : 2.0 * separation * Math.Sin(Math.PI / n) - 2.0 * diskRadius;

        /// <summary>Warning text for the params panel, or empty when the layout is open.</summary>
        public static string Warning(BilliardGeometry geometry, int diskCount, double diskRadius, double separation,
                                     string? customDisks = null, int polygonSides = 6, bool polygonFill = false)
        {
            if (IsLattice(geometry))
            {
                // Nearest neighbours on either grid are exactly the spacing apart.
                return Math.Clamp(diskCount, 1, MaxDisks) >= 2 && separation <= 2.0 * diskRadius
                    ? "Neighbouring disks touch or overlap, so the gaps between them are closed. "
                      + "Reduce the radius or increase the separation (the lattice spacing)."
                    : string.Empty;
            }
            if (geometry == BilliardGeometry.Polygon)
            {
                int k = Math.Clamp(polygonSides, MinSides, MaxSides);
                if (Math.Clamp(diskCount, 1, MaxDisks) < k)
                    return $"Fewer disks than corners: {Math.Clamp(diskCount, 1, MaxDisks)} disks on a {k}-sided polygon go on some corners only. Use at least {k} disks to complete it.";
                var c = PolygonCentres(diskCount, k, separation);
                if (polygonFill)
                {
                    int perimeter = c.Count, room = MaxDisks - perimeter;
                    c.AddRange(PolygonFillCentres(diskCount, k, separation, room, out int wanted));
                    if (wanted > room)
                        return $"The fill needs {perimeter + wanted} disks, more than the {MaxDisks} limit, so the {wanted - room} nearest the edges are left out. "
                             + "Use fewer disks per edge.";
                }
                for (int i = 0; i < c.Count; i++)
                    for (int j = i + 1; j < c.Count; j++)
                        if (Math.Sqrt((c[i].X - c[j].X) * (c[i].X - c[j].X) + (c[i].Y - c[j].Y) * (c[i].Y - c[j].Y)) <= 2.0 * diskRadius)
                            return "Neighbouring disks touch or overlap, so the gaps between them are closed. "
                                 + "Reduce the radius, increase the separation or use fewer disks.";
                return string.Empty;
            }
            if (geometry == BilliardGeometry.Custom)
            {
                var disks = ParseCustomDisks(customDisks);
                if (disks.Count == 0)
                    return "No custom layout yet: drag a disk in Ray Paths to start one (shown as ThreeDisk until then).";
                for (int i = 0; i < disks.Count; i++)
                    for (int j = i + 1; j < disks.Count; j++)
                        if (Distance(disks[i], disks[j]) <= disks[i].R + disks[j].R)
                            return "Some disks touch or overlap, so the gaps between them are closed.";
                return string.Empty;
            }
            int n = RingDiskCount(geometry, diskCount);
            if (n < 2 || RingNeighbourGap(n, diskRadius, separation) > 0.0) return string.Empty;
            return "Neighbouring disks touch or overlap, so the gaps between them are closed. "
                 + "Reduce the radius, increase the separation or use fewer disks.";
        }

        /// <summary>Parse a custom disk list. Malformed or non-finite entries and entries
        /// with a non-positive radius are skipped; at most <see cref="MaxDisks"/> are kept.</summary>
        public static List<BilliardDisk> ParseCustomDisks(string? text)
        {
            var list = new List<BilliardDisk>();
            if (string.IsNullOrWhiteSpace(text)) return list;
            foreach (var entry in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var f = entry.Split(',', StringSplitOptions.TrimEntries);
                if (f.Length != 3) continue;
                if (!double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                    || !double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
                    || !double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double r))
                    continue;
                if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(r) || r <= 0.0) continue;
                list.Add(new BilliardDisk(x, y, r));
                if (list.Count == MaxDisks) break;
            }
            return list;
        }

        /// <summary>Format a disk list so <see cref="ParseCustomDisks"/> reads back the same doubles.</summary>
        public static string FormatCustomDisks(IReadOnlyList<BilliardDisk> disks)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < disks.Count; i++)
            {
                if (i > 0) sb.Append(';');
                sb.Append(disks[i].X.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                  .Append(disks[i].Y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                  .Append(disks[i].R.ToString("R", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>Editor rule: may disk <paramref name="index"/> of <paramref name="disks"/>
        /// become <paramref name="candidate"/>? (index = disks.Count for a new disk.)</summary>
        public static bool CanPlace(IReadOnlyList<BilliardDisk> disks, int index, BilliardDisk candidate)
        {
            if (!double.IsFinite(candidate.X) || !double.IsFinite(candidate.Y)) return false;
            if (candidate.R < MinRadius || candidate.R > MaxRadius) return false;
            if (Math.Sqrt(candidate.X * candidate.X + candidate.Y * candidate.Y) + candidate.R > MaxReach) return false;
            for (int j = 0; j < disks.Count; j++)
            {
                if (j == index) continue;
                if (Distance(disks[j], candidate) < (disks[j].R + candidate.R) * MinGapFactor) return false;
            }
            return true;
        }

        private static double Distance(BilliardDisk a, BilliardDisk b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
