// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Slideshow/CoursePlanner.cs
//
// "Travel to location" course planner (#789 slice A). Given a set of saved
// regions + a start + an end, order them into a route so consecutive regions
// are neighbours in coordinate space, ready for an ordered slideshow (slice B)
// or a continuous video travel (slice C, depends on #788).
//
// Pure + headless — operates on lightweight RegionWaypoint records (no
// FractalRegion / Engine / UI dependency), so it lives in Abstractions and is
// unit-testable in isolation. Callers project their region source into
// waypoints and read the ordered names back.
//
// Coordinate space: each region is a point (X, Y, log10(Zoom)). Zoom MUST be
// log-scaled — a library spans many orders of magnitude in depth, and a linear
// zoom axis would let one deep region dominate every distance. Axes are
// min-max normalized across the candidate set so X/Y and depth are comparable,
// then weighted. Ordering is a greedy nearest-neighbour chain from the start
// with the end pinned last, refined by an endpoint-fixed 2-opt pass to undo
// obvious back-tracking.

using System;
using System.Collections.Generic;
using System.Linq;

namespace FracturingFog.Slideshow
{
    /// <summary>One region reduced to what the planner needs: a name, plane
    /// centre, zoom depth and fractal type.</summary>
    public readonly record struct RegionWaypoint(
        string Name, double X, double Y, double Zoom, FractalType Type);

    /// <summary>Knobs for <see cref="CoursePlanner.Plan"/>.</summary>
    public sealed class CoursePlanOptions
    {
        /// <summary>Weight on the plane (X/Y) distance. Default 1.</summary>
        public double WeightXY { get; set; } = 1.0;

        /// <summary>Weight on the depth (log10 zoom) distance. Default 1.</summary>
        public double WeightZoom { get; set; } = 1.0;

        /// <summary>When &gt; 0, drop candidates further than this (normalized)
        /// distance from the straight start→end line, keeping the route in a
        /// corridor between the endpoints. 0 = keep every same-type region.</summary>
        public double CorridorRadius { get; set; }

        /// <summary>Run the endpoint-fixed 2-opt refinement after the greedy
        /// nearest-neighbour chain. Default true.</summary>
        public bool Refine { get; set; } = true;
    }

    /// <summary>What the "Travel to…" dialog collects, flattened for transport
    /// from the host dialog to the shell that runs the plan (#789 slice B).</summary>
    public sealed class TravelPlanRequest
    {
        /// <summary>Region to start the journey from (empty = the current view's
        /// region, resolved by the shell).</summary>
        public string StartRegion { get; set; } = string.Empty;

        /// <summary>Region to end the journey at.</summary>
        public string EndRegion { get; set; } = string.Empty;

        public double WeightXY { get; set; } = 1.0;
        public double WeightZoom { get; set; } = 1.0;

        /// <summary>0 = keep every same-type region; &gt; 0 restricts to a
        /// corridor around the start→end line.</summary>
        public double CorridorRadius { get; set; }
    }

    /// <summary>Ordered route + any advisory note.</summary>
    public sealed class CoursePlanResult
    {
        /// <summary>Regions in travel order, start first and end last. Empty when
        /// the start or end could not be resolved.</summary>
        public IReadOnlyList<RegionWaypoint> Ordered { get; init; } = Array.Empty<RegionWaypoint>();

        /// <summary>Human-readable advisory (type mismatch, missing endpoint,
        /// corridor emptied the middle, …), or null when the plan is clean.</summary>
        public string? Warning { get; init; }
    }

    /// <summary>Orders saved regions into a coordinate-coherent route. See file
    /// header.</summary>
    public static class CoursePlanner
    {
        private const double Tiny = 1e-9;

        /// <summary>Plan a route from <paramref name="startName"/> to
        /// <paramref name="endName"/> through the same-fractal-type regions in
        /// <paramref name="regions"/>.</summary>
        public static CoursePlanResult Plan(
            IEnumerable<RegionWaypoint> regions,
            string startName,
            string endName,
            CoursePlanOptions? options = null)
        {
            options ??= new CoursePlanOptions();
            var all = regions?.ToList() ?? new List<RegionWaypoint>();

            RegionWaypoint? start = FindByName(all, startName);
            RegionWaypoint? end = FindByName(all, endName);
            if (start is null || end is null)
                return new CoursePlanResult { Warning = "Start or end region not found." };

            var s = start.Value;
            var e = end.Value;

            if (string.Equals(s.Name, e.Name, StringComparison.Ordinal))
                return new CoursePlanResult { Ordered = new[] { s } };

            string? warning = null;
            if (s.Type != e.Type)
            {
                // Different fractals — X/Y aren't comparable, so no intermediate
                // course is meaningful; hand back a direct two-stop hop.
                return new CoursePlanResult
                {
                    Ordered = new[] { s, e },
                    Warning = $"Start ({s.Type}) and end ({e.Type}) are different fractal types — no intermediate course; direct hop only.",
                };
            }

            // Candidates: same fractal type as the endpoints.
            var candidates = all.Where(r => r.Type == s.Type).ToList();
            // De-dup by name, keeping the endpoints authoritative.
            candidates = DedupByName(candidates);

            // Normalize (X, Y, log10 Zoom) across the candidate set.
            var norm = BuildNormalizer(candidates);

            // Optional corridor: keep only points near the start→end line.
            if (options.CorridorRadius > 0.0)
            {
                var kept = candidates.Where(r =>
                        SegmentDistance(norm(r), norm(s), norm(e)) <= options.CorridorRadius
                        || SameName(r, s) || SameName(r, e))
                    .ToList();
                if (kept.Count < candidates.Count)
                {
                    candidates = kept;
                    if (candidates.Count == 2)
                        warning = "Corridor radius removed all intermediate regions — direct hop only.";
                }
            }

            double Dist(RegionWaypoint a, RegionWaypoint b)
            {
                var (ax, ay, az) = norm(a);
                var (bx, by, bz) = norm(b);
                double dx = ax - bx, dy = ay - by, dz = az - bz;
                return Math.Sqrt(options.WeightXY * (dx * dx + dy * dy)
                               + options.WeightZoom * (dz * dz));
            }

            // Greedy nearest-neighbour chain from start; end pinned last.
            var route = GreedyChain(s, e, candidates, Dist);
            if (options.Refine && route.Count > 3)
                TwoOptFixedEnds(route, Dist);

            return new CoursePlanResult { Ordered = route, Warning = warning };
        }

        // ── Ordering ─────────────────────────────────────────────────────────

        private static List<RegionWaypoint> GreedyChain(
            RegionWaypoint start, RegionWaypoint end,
            List<RegionWaypoint> candidates, Func<RegionWaypoint, RegionWaypoint, double> dist)
        {
            var remaining = candidates
                .Where(r => !SameName(r, start) && !SameName(r, end))
                .ToList();

            var route = new List<RegionWaypoint> { start };
            var current = start;
            while (remaining.Count > 0)
            {
                int best = 0;
                double bestD = double.MaxValue;
                for (int i = 0; i < remaining.Count; i++)
                {
                    double d = dist(current, remaining[i]);
                    // Stable tie-break by name so the plan is deterministic.
                    if (d < bestD
                        || (d == bestD && string.CompareOrdinal(remaining[i].Name, remaining[best].Name) < 0))
                    {
                        bestD = d;
                        best = i;
                    }
                }
                current = remaining[best];
                route.Add(current);
                remaining.RemoveAt(best);
            }
            route.Add(end);
            return route;
        }

        // Endpoint-fixed 2-opt: reverse interior segments while the total route
        // length improves. Keeps route[0] (start) and route[^1] (end) in place.
        private static void TwoOptFixedEnds(
            List<RegionWaypoint> route, Func<RegionWaypoint, RegionWaypoint, double> dist)
        {
            int n = route.Count;
            bool improved = true;
            int guard = 0;
            while (improved && guard++ < 64)
            {
                improved = false;
                for (int i = 1; i < n - 2; i++)
                {
                    for (int k = i + 1; k < n - 1; k++)
                    {
                        double before = dist(route[i - 1], route[i]) + dist(route[k], route[k + 1]);
                        double after = dist(route[i - 1], route[k]) + dist(route[i], route[k + 1]);
                        if (after + 1e-12 < before)
                        {
                            route.Reverse(i, k - i + 1);
                            improved = true;
                        }
                    }
                }
            }
        }

        // ── Normalization ────────────────────────────────────────────────────

        // Returns a projector region → (nx, ny, nz) in [0,1]³ over the candidate
        // set. A zero-range axis maps to 0 (contributes nothing to distance).
        private static Func<RegionWaypoint, (double x, double y, double z)> BuildNormalizer(
            List<RegionWaypoint> candidates)
        {
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            double minZ = double.MaxValue, maxZ = double.MinValue;
            foreach (var r in candidates)
            {
                double lz = Math.Log10(Math.Max(r.Zoom, Tiny));
                if (r.X < minX) minX = r.X; if (r.X > maxX) maxX = r.X;
                if (r.Y < minY) minY = r.Y; if (r.Y > maxY) maxY = r.Y;
                if (lz < minZ) minZ = lz; if (lz > maxZ) maxZ = lz;
            }
            double rx = maxX - minX, ry = maxY - minY, rz = maxZ - minZ;
            return r =>
            {
                double lz = Math.Log10(Math.Max(r.Zoom, Tiny));
                double nx = rx > Tiny ? (r.X - minX) / rx : 0.0;
                double ny = ry > Tiny ? (r.Y - minY) / ry : 0.0;
                double nz = rz > Tiny ? (lz - minZ) / rz : 0.0;
                return (nx, ny, nz);
            };
        }

        // Distance from point p to segment a→b (all in normalized 3-space).
        private static double SegmentDistance(
            (double x, double y, double z) p,
            (double x, double y, double z) a,
            (double x, double y, double z) b)
        {
            double abx = b.x - a.x, aby = b.y - a.y, abz = b.z - a.z;
            double apx = p.x - a.x, apy = p.y - a.y, apz = p.z - a.z;
            double denom = abx * abx + aby * aby + abz * abz;
            double t = denom > Tiny ? (apx * abx + apy * aby + apz * abz) / denom : 0.0;
            t = Math.Clamp(t, 0.0, 1.0);
            double cx = a.x + t * abx, cy = a.y + t * aby, cz = a.z + t * abz;
            double dx = p.x - cx, dy = p.y - cy, dz = p.z - cz;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static RegionWaypoint? FindByName(List<RegionWaypoint> all, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var r in all)
                if (string.Equals(r.Name, name, StringComparison.Ordinal)) return r;
            return null;
        }

        private static bool SameName(RegionWaypoint a, RegionWaypoint b) =>
            string.Equals(a.Name, b.Name, StringComparison.Ordinal);

        private static List<RegionWaypoint> DedupByName(List<RegionWaypoint> src)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var outp = new List<RegionWaypoint>(src.Count);
            foreach (var r in src)
                if (seen.Add(r.Name)) outp.Add(r);
            return outp;
        }
    }
}
