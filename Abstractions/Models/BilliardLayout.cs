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
        /// <summary>Most disks a layout holds (as the NDisk / Ring count cap).</summary>
        public const int MaxDisks = 64;

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
                                     string? customDisks = null, int polygonSides = 6)
        {
            if (geometry == BilliardGeometry.Polygon)
            {
                int k = Math.Clamp(polygonSides, MinSides, MaxSides);
                if (Math.Clamp(diskCount, 1, MaxDisks) < k)
                    return $"Fewer disks than corners: {Math.Clamp(diskCount, 1, MaxDisks)} disks on a {k}-sided polygon go on some corners only. Use at least {k} disks to complete it.";
                var c = PolygonCentres(diskCount, k, separation);
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
