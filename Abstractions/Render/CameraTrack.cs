// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Render/CameraTrack.cs
//
// Scene Engine Roadmap — Phase S3: the camera track (the new engine surface).
//
// Everything before S3 was plumbing that ships behind current behaviour; this
// is the first phase that adds a genuinely new authored thing — a time-varying
// camera pose. The 8 distance-estimation raymarchers (Mandelbulb, Mandelbox,
// KIFS, QJulia, QMandel, Kleinian, Bicomplex, UserBulb) each already consume an
// orbit camera as three scalars on FractalParameters — <Type>CameraDistance /
// Theta / Phi. A CameraTrack is a keyframed sequence of those three scalars
// plus an interpolation kind; CameraParamBinding pushes an evaluated pose onto
// the right per-type fields, and CameraTrackAnimator drives it through the
// existing animation bus tick (inheriting the bus's render-completion gate for
// free — camera motion gets the same flicker-free handshake the procedural
// tracks already have).
//
// Pure + deterministic + unit-tested; no I/O, no threading. The bus
// registration + editor UI are the consumers (S5).

using System;
using System.Collections.Generic;

namespace FracturingFog.Render
{
    /// <summary>The orbit-camera pose the 3D raymarchers consume: spherical
    /// distance from target plus azimuth (<see cref="Theta"/>, around Y) and
    /// elevation (<see cref="Phi"/>). Mirrors the <c>&lt;Type&gt;CameraDistance
    /// / Theta / Phi</c> triple on <see cref="FracturingFog.Models.FractalParameters"/>.</summary>
    public readonly record struct CameraState(double Distance, double Theta, double Phi)
    {
        /// <summary>Component-wise linear blend. <paramref name="t"/> is not
        /// clamped — callers pass a normalised [0,1] or an eased value.</summary>
        public static CameraState Lerp(in CameraState a, in CameraState b, double t)
            => new(
                a.Distance + (b.Distance - a.Distance) * t,
                a.Theta    + (b.Theta    - a.Theta)    * t,
                a.Phi      + (b.Phi      - a.Phi)      * t);
    }

    /// <summary>How a <see cref="CameraTrack"/> blends between adjacent keys.</summary>
    public enum CameraInterpolation
    {
        /// <summary>Straight component-wise lerp — constant velocity within a
        /// segment, a velocity discontinuity at each key.</summary>
        Linear,

        /// <summary>Uniform Catmull-Rom spline through the keys — C¹ continuous
        /// (no velocity jump at keys), tangents derived from the neighbouring
        /// keys. Overshoots slightly on sharp direction changes. The default —
        /// smooth camera moves that still pass exactly through every pose.</summary>
        CatmullRom,

        /// <summary>Cubic ease-in / ease-out between adjacent keys (Hermite with
        /// zero endpoint tangents → the camera settles to a stop at every key).
        /// Good for shot-to-shot moves that pause on each pose. Per-key handle
        /// authoring is the deferred S8 easing editor.</summary>
        Bezier,
    }

    /// <summary>Per-key time easing (S8 / Animation-roadmap D.1). Reparametrises
    /// the normalised segment parameter of the segment that <em>starts</em> at a
    /// key, so an author can control how the camera accelerates out of / into
    /// each pose — without a full Bezier-handle curve editor. Orthogonal to
    /// <see cref="CameraInterpolation"/> (which picks the spatial path shape):
    /// the ease reparametrises the segment fraction before the interpolation
    /// basis reads it, so the two compose.</summary>
    public enum CameraEase
    {
        /// <summary>No reparam — constant time flow across the segment (default;
        /// bit-identical to pre-D.1 behaviour).</summary>
        None,
        /// <summary>Accelerate out of the key (slow start): <c>u²</c>.</summary>
        EaseIn,
        /// <summary>Decelerate into the next key (fast start): <c>1-(1-u)²</c>.</summary>
        EaseOut,
        /// <summary>Ease both ends (smoothstep): <c>u²(3-2u)</c> — settles to a
        /// stop at both poses.</summary>
        EaseInOut,
    }

    /// <summary>One camera keyframe: a <see cref="CameraState"/> at a point in
    /// time (seconds from the track's start).</summary>
    public sealed class CameraKey
    {
        /// <summary>Seconds from the track start. Keys are evaluated in
        /// ascending time order (see <see cref="CameraTrack"/> remarks).</summary>
        public double Time { get; set; }

        /// <summary>The pose at <see cref="Time"/>.</summary>
        public CameraState State { get; set; }

        /// <summary>Time easing applied across the segment that starts at this
        /// key (D.1). Default <see cref="CameraEase.None"/>.</summary>
        public CameraEase Ease { get; set; } = CameraEase.None;

        public CameraKey() { }

        public CameraKey(double time, CameraState state)
        {
            Time = time;
            State = state;
        }

        public CameraKey(double time, double distance, double theta, double phi)
            : this(time, new CameraState(distance, theta, phi)) { }

        /// <summary>Reparametrise <paramref name="u"/> ∈ [0,1] per <paramref name="ease"/>.
        /// Endpoints are fixed (0→0, 1→1) so keys are always passed through
        /// exactly; only the traversal speed between them changes.</summary>
        public static double ApplyEase(CameraEase ease, double u)
        {
            if (u <= 0) return 0;
            if (u >= 1) return 1;
            return ease switch
            {
                CameraEase.EaseIn    => u * u,
                CameraEase.EaseOut   => 1.0 - (1.0 - u) * (1.0 - u),
                CameraEase.EaseInOut => u * u * (3.0 - 2.0 * u),
                _                    => u,
            };
        }
    }

    /// <summary>
    /// A keyframed camera path. <see cref="Keys"/> must be in ascending
    /// <see cref="CameraKey.Time"/> order (use <see cref="Add"/>, which inserts
    /// sorted). <see cref="Evaluate"/> clamps outside the key range and blends
    /// inside it per <see cref="Interpolation"/>.
    ///
    /// <para>Angles interpolate literally, not shortest-path: a track from
    /// θ = 0 to θ = 4π orbits twice on purpose. Authors get exactly the path
    /// their keys describe.</para>
    /// </summary>
    public sealed class CameraTrack
    {
        /// <summary>Keyframes in ascending time order.</summary>
        public List<CameraKey> Keys { get; set; } = new();

        /// <summary>Blend kind between adjacent keys. Default
        /// <see cref="CameraInterpolation.CatmullRom"/>.</summary>
        public CameraInterpolation Interpolation { get; set; } = CameraInterpolation.CatmullRom;

        /// <summary>Absolute end time — the last key's <see cref="CameraKey.Time"/>,
        /// or 0 when empty. The animator loops / clamps against this. Tracks
        /// normally start at time 0 for a clean loop.</summary>
        public double Duration => Keys.Count == 0 ? 0.0 : Keys[Keys.Count - 1].Time;

        /// <summary>Insert a key in ascending-time order.</summary>
        public void Add(CameraKey key)
        {
            ArgumentNullException.ThrowIfNull(key);
            int i = Keys.Count;
            while (i > 0 && Keys[i - 1].Time > key.Time) i--;
            Keys.Insert(i, key);
        }

        /// <summary>Evaluate the pose at <paramref name="time"/> (seconds).
        /// Below the first key returns the first pose; above the last returns
        /// the last; between keys blends per <see cref="Interpolation"/>.</summary>
        /// <exception cref="InvalidOperationException">The track has no keys —
        /// evaluating an empty path would silently produce a degenerate
        /// zero-distance camera, so it is an explicit error.</exception>
        public CameraState Evaluate(double time)
        {
            int n = Keys.Count;
            if (n == 0)
                throw new InvalidOperationException("CameraTrack has no keys to evaluate.");
            if (n == 1 || time <= Keys[0].Time) return Keys[0].State;
            if (time >= Keys[n - 1].Time) return Keys[n - 1].State;

            // Find segment [i, i+1] with Keys[i].Time <= time < Keys[i+1].Time.
            int i = 0;
            while (i < n - 1 && Keys[i + 1].Time <= time) i++;

            double t0 = Keys[i].Time;
            double t1 = Keys[i + 1].Time;
            double span = t1 - t0;
            double u = span > 0 ? (time - t0) / span : 0.0; // coincident keys → step

            // Per-key time easing (D.1): reparametrise the segment fraction using
            // the starting key's ease before the spatial basis reads it.
            u = CameraKey.ApplyEase(Keys[i].Ease, u);

            CameraState p1 = Keys[i].State;
            CameraState p2 = Keys[i + 1].State;

            switch (Interpolation)
            {
                case CameraInterpolation.Linear:
                    return CameraState.Lerp(p1, p2, u);

                case CameraInterpolation.Bezier:
                    // Smoothstep = cubic Hermite with zero endpoint tangents.
                    return CameraState.Lerp(p1, p2, u * u * (3.0 - 2.0 * u));

                case CameraInterpolation.CatmullRom:
                default:
                    // Neighbour keys for the tangents; clamp at the ends so the
                    // first / last segment uses a one-sided tangent.
                    CameraState p0 = Keys[i - 1 >= 0 ? i - 1 : i].State;
                    CameraState p3 = Keys[i + 2 < n ? i + 2 : i + 1].State;
                    return new CameraState(
                        CatmullRom(p0.Distance, p1.Distance, p2.Distance, p3.Distance, u),
                        CatmullRom(p0.Theta,    p1.Theta,    p2.Theta,    p3.Theta,    u),
                        CatmullRom(p0.Phi,      p1.Phi,      p2.Phi,      p3.Phi,      u));
            }
        }

        /// <summary>Uniform Catmull-Rom basis for one component at parameter
        /// <paramref name="u"/> in [0,1] across the segment p1→p2.</summary>
        private static double CatmullRom(double p0, double p1, double p2, double p3, double u)
        {
            double u2 = u * u;
            double u3 = u2 * u;
            return 0.5 * (
                (2.0 * p1)
              + (-p0 + p2) * u
              + (2.0 * p0 - 5.0 * p1 + 4.0 * p2 - p3) * u2
              + (-p0 + 3.0 * p1 - 3.0 * p2 + p3) * u3);
        }
    }
}
