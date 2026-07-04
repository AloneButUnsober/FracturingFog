// Abstractions/Animation/SceneGlobalTrack.cs
//
// Scene Engine Roadmap — S8 "global tracks": scene-wide, keyframed post/look
// scalars that ride on top of every shot for the whole scene, driven by GLOBAL
// scene time (not a shot's local clock). Where a SceneShot's CameraTrack and
// param-animation are per-shot, a global track is authored once against the
// whole timeline — an exposure ramp that fades the scene up over its opening
// shots, a bloom swell into a climax, a vignette that closes on the finale.
//
// The roadmap named three global-track targets — exposure, tone-map,
// IBL-sky-rotation. Of those only the *continuous* post scalars ship here:
//
//   * Exposure and the other LightingFxData post knobs (bloom / vignette /
//     chromatic aberration) already exist and read naturally as a scene-wide
//     look — they are the headline this slice delivers.
//   * Tone-map is a discrete operator enum (None / Reinhard / ACES), not a
//     keyframeable scalar, so it stays a per-shot choice, not a track.
//   * IBL-sky-rotation has no field yet — the HDRI sampler reads the surface
//     normal directly with no yaw offset (see ShadingPipeline.SampleEnvAmbientHdri).
//     Adding it means plumbing a rotation through all 8 CPU + 8 GPU raymarchers,
//     which is a Lighting-FX-roadmap phase, not this one. The SceneGlobalTarget
//     enum + the data-driven binding are built so it slots in for free once the
//     field lands.
//
// Reuses the S3/D.1 CameraInterpolation + CameraEase so authors get the same
// spline + per-key easing vocabulary they already know from the camera track —
// no parallel enum. Pure + deterministic + unit-tested; the offline renderer
// (S7) and realtime playback (S6) are the consumers.

using System;
using System.Collections.Generic;

using FracturingFog.Models;
using FracturingFog.Render;

namespace FracturingFog.Abstractions.Animation
{
    /// <summary>Which scene-wide, continuous post/look scalar on
    /// <see cref="FractalParameters.Lighting"/> a <see cref="SceneGlobalTrack"/>
    /// drives. Every entry maps to a public <c>double</c> field via
    /// <see cref="SceneGlobalBinding"/>. Discrete look choices (tone-map operator,
    /// sky mode) are deliberately absent — a keyframe track is only meaningful
    /// over a continuous value.</summary>
    public enum SceneGlobalTarget
    {
        /// <summary>Linear exposure multiplier before tone-map (1 = neutral).</summary>
        Exposure,
        /// <summary>Bloom additive strength [0, 1].</summary>
        BloomStrength,
        /// <summary>HDR luminance above which pixels bloom (lower = more bloom).</summary>
        BloomThreshold,
        /// <summary>Vignette strength [0, 1] (0 = uniform).</summary>
        Vignette,
        /// <summary>Chromatic-aberration radial offset in pixels (0 = off).</summary>
        ChromaticAberration,
    }

    /// <summary>One keyframe of a <see cref="SceneGlobalTrack"/>: a scalar value
    /// at a global scene time. Shares <see cref="CameraEase"/> with the camera
    /// track so acceleration into / out of a pose reads the same way.</summary>
    public sealed class SceneGlobalKey
    {
        /// <summary>Seconds from the scene start (global time). Keys are evaluated
        /// in ascending time order; use <see cref="SceneGlobalTrack.Add"/>.</summary>
        public double Time { get; set; }

        /// <summary>The target scalar's value at <see cref="Time"/>.</summary>
        public double Value { get; set; }

        /// <summary>Time easing across the segment that starts at this key (D.1).
        /// Default <see cref="CameraEase.None"/>.</summary>
        public CameraEase Ease { get; set; } = CameraEase.None;

        public SceneGlobalKey() { }

        public SceneGlobalKey(double time, double value)
        {
            Time = time;
            Value = value;
        }

        public SceneGlobalKey(double time, double value, CameraEase ease)
            : this(time, value)
        {
            Ease = ease;
        }
    }

    /// <summary>A scene-wide keyframed scalar. Sampled at GLOBAL scene time and
    /// applied to <see cref="Target"/> after each shot's own params/animation, so
    /// it overrides per-shot look uniformly across the whole scene.
    /// <para><see cref="Keys"/> must be in ascending <see cref="SceneGlobalKey.Time"/>
    /// order (<see cref="Add"/> inserts sorted). <see cref="Evaluate"/> clamps
    /// outside the key range and blends inside it per <see cref="Interpolation"/>.
    /// Default interpolation is <see cref="CameraInterpolation.Linear"/> — a look
    /// ramp wants a predictable monotonic sweep, not the overshoot a spline can
    /// introduce (which could push exposure below 0).</para></summary>
    public sealed class SceneGlobalTrack
    {
        /// <summary>Which scene-wide scalar this track drives.</summary>
        public SceneGlobalTarget Target { get; set; } = SceneGlobalTarget.Exposure;

        /// <summary>Blend kind between adjacent keys. Default
        /// <see cref="CameraInterpolation.Linear"/>.</summary>
        public CameraInterpolation Interpolation { get; set; } = CameraInterpolation.Linear;

        /// <summary>Keyframes in ascending global-time order.</summary>
        public List<SceneGlobalKey> Keys { get; set; } = new();

        /// <summary>Absolute end time — the last key's time, or 0 when empty.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public double Duration => Keys.Count == 0 ? 0.0 : Keys[Keys.Count - 1].Time;

        /// <summary>True when the track has at least one key to apply. An empty
        /// track is inert (applying it would clobber the look with a degenerate
        /// value).</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsActive => Keys.Count > 0;

        /// <summary>Insert a key in ascending-time order.</summary>
        public void Add(SceneGlobalKey key)
        {
            ArgumentNullException.ThrowIfNull(key);
            int i = Keys.Count;
            while (i > 0 && Keys[i - 1].Time > key.Time) i--;
            Keys.Insert(i, key);
        }

        /// <summary>Evaluate the scalar at global <paramref name="time"/> (seconds).
        /// Below the first key returns the first value; above the last returns the
        /// last; between keys blends per <see cref="Interpolation"/>, with the
        /// starting key's <see cref="SceneGlobalKey.Ease"/> reparametrising the
        /// segment fraction first (so keys are always hit exactly).</summary>
        /// <exception cref="InvalidOperationException">The track has no keys.</exception>
        public double Evaluate(double time)
        {
            int n = Keys.Count;
            if (n == 0)
                throw new InvalidOperationException("SceneGlobalTrack has no keys to evaluate.");
            if (n == 1 || time <= Keys[0].Time) return Keys[0].Value;
            if (time >= Keys[n - 1].Time) return Keys[n - 1].Value;

            int i = 0;
            while (i < n - 1 && Keys[i + 1].Time <= time) i++;

            double t0 = Keys[i].Time;
            double t1 = Keys[i + 1].Time;
            double span = t1 - t0;
            double u = span > 0 ? (time - t0) / span : 0.0; // coincident keys → step
            u = CameraKey.ApplyEase(Keys[i].Ease, u);

            double p1 = Keys[i].Value;
            double p2 = Keys[i + 1].Value;

            switch (Interpolation)
            {
                case CameraInterpolation.Linear:
                    return p1 + (p2 - p1) * u;

                case CameraInterpolation.Bezier:
                    return p1 + (p2 - p1) * (u * u * (3.0 - 2.0 * u)); // smoothstep

                case CameraInterpolation.CatmullRom:
                default:
                    double p0 = Keys[i - 1 >= 0 ? i - 1 : i].Value;
                    double p3 = Keys[i + 2 < n ? i + 2 : i + 1].Value;
                    return CatmullRom(p0, p1, p2, p3, u);
            }
        }

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

    /// <summary>The seam from a <see cref="SceneGlobalTarget"/> onto the concrete
    /// <see cref="FractalParameters.Lighting"/> field. Kept data-driven (one
    /// switch) so a new global-track target is a two-line change here plus an enum
    /// entry — the same pattern <c>CameraParamBinding</c> uses for the camera
    /// fields.</summary>
    public static class SceneGlobalBinding
    {
        /// <summary>Write <paramref name="value"/> to the scalar that
        /// <paramref name="target"/> names. <see cref="FractalParameters.Lighting"/>
        /// is a struct, so read-modify-write the whole value.</summary>
        public static void Apply(FractalParameters p, SceneGlobalTarget target, double value)
        {
            ArgumentNullException.ThrowIfNull(p);
            var fx = p.Lighting;
            switch (target)
            {
                case SceneGlobalTarget.Exposure:            fx.Exposure = value; break;
                case SceneGlobalTarget.BloomStrength:       fx.BloomStrength = value; break;
                case SceneGlobalTarget.BloomThreshold:      fx.BloomThreshold = value; break;
                case SceneGlobalTarget.Vignette:            fx.Vignette = value; break;
                case SceneGlobalTarget.ChromaticAberration: fx.ChromaticAberration = value; break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(target), target,
                        "No LightingFxData binding for this global-track target.");
            }
            p.Lighting = fx;
        }

        /// <summary>Read the current value of the scalar <paramref name="target"/>
        /// names (round-trip / test helper).</summary>
        public static double Read(FractalParameters p, SceneGlobalTarget target)
        {
            ArgumentNullException.ThrowIfNull(p);
            var fx = p.Lighting;
            return target switch
            {
                SceneGlobalTarget.Exposure            => fx.Exposure,
                SceneGlobalTarget.BloomStrength       => fx.BloomStrength,
                SceneGlobalTarget.BloomThreshold      => fx.BloomThreshold,
                SceneGlobalTarget.Vignette            => fx.Vignette,
                SceneGlobalTarget.ChromaticAberration => fx.ChromaticAberration,
                _ => throw new ArgumentOutOfRangeException(nameof(target), target,
                        "No LightingFxData binding for this global-track target."),
            };
        }
    }

    /// <summary>Applies a whole set of scene global tracks at one global time.
    /// Later tracks win if two target the same scalar (mirrors the "later track
    /// overrides earlier" rule on <c>AnimationData.Tracks</c>).</summary>
    public static class SceneGlobalTracks
    {
        /// <summary>Evaluate every active track in <paramref name="tracks"/> at
        /// <paramref name="globalTime"/> and write the result onto
        /// <paramref name="p"/>. A null / empty list is a no-op, so a scene with
        /// no global tracks renders bit-identically to before this feature.</summary>
        public static void Apply(IReadOnlyList<SceneGlobalTrack>? tracks,
                                 FractalParameters p, double globalTime)
        {
            if (tracks == null || tracks.Count == 0) return;
            ArgumentNullException.ThrowIfNull(p);
            for (int i = 0; i < tracks.Count; i++)
            {
                var t = tracks[i];
                if (t != null && t.IsActive)
                    SceneGlobalBinding.Apply(p, t.Target, t.Evaluate(globalTime));
            }
        }
    }
}
