// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Render/CameraPathFit.cs
//
// #1056 — turn a camera path drawn with the mouse into shot camera keys.
//
// The user draws on a top-down "plan" of the orbit: the object sits at the
// origin, the distance from it is the camera distance and the angle around it
// is the azimuth (θ). A mouse gives two values, the orbit camera needs three,
// so elevation (φ) comes from the dialog as a start → end ramp along the path.
//
// Steps (all pure, unit-tested):
//   1. drop duplicate / jittery points;
//   2. reject a path that passes inside the object (distance < MinDistance) or
//      is too short to be a path;
//   3. polar-convert, unwrapping θ across ±π so a full orbit keeps turning the
//      same way instead of snapping back (CatmullRom would spin the long way);
//   4. simplify with Ramer–Douglas–Peucker in plan space down to MaxKeys;
//   5. time each kept point by its arc length (constant speed along the drawing)
//      and give it the elevation ramp value at that arc fraction.
//
// Plan coordinates: +X right, +Y up (screen Y is flipped by the canvas), in
// camera-distance units. θ = 0 lies along +X and grows counter-clockwise.

using System;
using System.Collections.Generic;

namespace FracturingFog.Render
{
    /// <summary>Inputs for <see cref="CameraPathFit.Fit"/>.</summary>
    public sealed record CameraPathFitOptions
    {
        /// <summary>Shot time the path takes, seconds (the last key's time).</summary>
        public double DurationSeconds { get; init; } = 5.0;
        /// <summary>Elevation at the start / end of the path, radians.</summary>
        public double StartElevation { get; init; } = 0.3;
        public double EndElevation { get; init; } = 0.3;
        /// <summary>Closest the camera may come to the object (its rough radius).</summary>
        public double MinDistance { get; init; } = 1.2;
        /// <summary>Farthest allowed camera distance.</summary>
        public double MaxDistance { get; init; } = 20.0;
        /// <summary>Upper bound on generated keys (≥ 2).</summary>
        public int MaxKeys { get; init; } = 12;
        /// <summary>RDP tolerance as a fraction of the path's mean distance.</summary>
        public double Tolerance { get; init; } = 0.02;
        /// <summary>Interpolation of the generated track.</summary>
        public CameraInterpolation Interpolation { get; init; } = CameraInterpolation.CatmullRom;
    }

    /// <summary>Result of <see cref="CameraPathFit.Fit"/>: a track, or why not.</summary>
    public sealed record CameraPathFitResult(CameraTrack? Track, string? Error)
    {
        public bool Ok => Track != null;
    }

    public static class CameraPathFit
    {
        /// <summary>Fit drawn plan-view <paramref name="points"/> into a camera track.</summary>
        public static CameraPathFitResult Fit(IReadOnlyList<(double X, double Y)> points, CameraPathFitOptions options)
        {
            ArgumentNullException.ThrowIfNull(points);
            ArgumentNullException.ThrowIfNull(options);
            if (!(options.DurationSeconds > 0))
                return Fail("Duration must be greater than zero.");

            var pts = Dedupe(points, options.MinDistance * 0.01);
            if (pts.Count < 2) return Fail("Draw a path — click and drag around the object.");

            double length = 0;
            for (int i = 1; i < pts.Count; i++) length += Dist(pts[i - 1], pts[i]);
            if (length < options.MinDistance * 0.25)
                return Fail("The path is too short. Drag further around the object.");

            for (int i = 0; i < pts.Count; i++)
            {
                double r = Radius(pts[i]);
                if (r < options.MinDistance)
                    return Fail("The path passes inside the object. Keep the line outside the centre shape.");
                if (r > options.MaxDistance)
                    return Fail($"The path goes beyond the farthest allowed distance ({options.MaxDistance:0.##}).");
            }

            // Arc-length parameter + unwrapped azimuth for every point.
            var cum = new double[pts.Count];
            var theta = new double[pts.Count];
            theta[0] = Math.Atan2(pts[0].Y, pts[0].X);
            for (int i = 1; i < pts.Count; i++)
            {
                cum[i] = cum[i - 1] + Dist(pts[i - 1], pts[i]);
                double raw = Math.Atan2(pts[i].Y, pts[i].X);
                double d = raw - Math.Atan2(pts[i - 1].Y, pts[i - 1].X);
                d -= 2 * Math.PI * Math.Round(d / (2 * Math.PI));   // into (-π, π]
                theta[i] = theta[i - 1] + d;
            }

            var keep = Simplify(pts, options);
            var track = new CameraTrack { Interpolation = options.Interpolation };
            foreach (int i in keep)
            {
                double f = cum[i] / length;
                double phi = options.StartElevation + (options.EndElevation - options.StartElevation) * f;
                track.Add(new CameraKey(options.DurationSeconds * f, new CameraState(Radius(pts[i]), theta[i], phi)));
            }
            return new CameraPathFitResult(track, null);
        }

        // RDP in plan space, tolerance scaled by the path's mean distance; grows the
        // tolerance until the key count fits MaxKeys. Endpoints always kept.
        private static List<int> Simplify(List<(double X, double Y)> pts, CameraPathFitOptions o)
        {
            int maxKeys = Math.Max(2, o.MaxKeys);
            double mean = 0;
            foreach (var p in pts) mean += Radius(p);
            mean /= pts.Count;
            double eps = Math.Max(1e-9, o.Tolerance * mean);

            for (int guard = 0; guard < 64; guard++)
            {
                var keep = new List<int>();
                var flags = new bool[pts.Count];
                flags[0] = flags[^1] = true;
                Rdp(pts, 0, pts.Count - 1, eps, flags);
                for (int i = 0; i < flags.Length; i++) if (flags[i]) keep.Add(i);
                if (keep.Count <= maxKeys) return keep;
                eps *= 1.5;
            }
            return new List<int> { 0, pts.Count - 1 };
        }

        private static void Rdp(List<(double X, double Y)> p, int a, int b, double eps, bool[] keep)
        {
            if (b <= a + 1) return;
            double best = -1; int idx = -1;
            for (int i = a + 1; i < b; i++)
            {
                double d = SegmentDistance(p[i], p[a], p[b]);
                if (d > best) { best = d; idx = i; }
            }
            if (best > eps)
            {
                keep[idx] = true;
                Rdp(p, a, idx, eps, keep);
                Rdp(p, idx, b, eps, keep);
            }
        }

        private static double SegmentDistance((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len2 = dx * dx + dy * dy;
            if (len2 <= 0) return Dist(p, a);
            double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
            return Dist(p, (a.X + t * dx, a.Y + t * dy));
        }

        private static List<(double X, double Y)> Dedupe(IReadOnlyList<(double X, double Y)> points, double minStep)
        {
            var list = new List<(double X, double Y)>(points.Count);
            foreach (var p in points)
            {
                if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) continue;
                if (list.Count == 0 || Dist(list[^1], p) > minStep) list.Add(p);
            }
            return list;
        }

        private static double Radius((double X, double Y) p) => Math.Sqrt(p.X * p.X + p.Y * p.Y);
        private static double Dist((double X, double Y) a, (double X, double Y) b)
            => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        private static CameraPathFitResult Fail(string why) => new(null, why);
    }
}
