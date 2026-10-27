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
                                     string? customDisks = null)
        {
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
