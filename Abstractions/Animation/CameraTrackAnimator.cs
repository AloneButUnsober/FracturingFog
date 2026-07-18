// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Animation/CameraTrackAnimator.cs
//
// Scene Engine Roadmap — Phase S3: drive a CameraTrack through the animation
// bus. An IParameterAnimator (same contract as the procedural animators) that
// advances a scene clock on each Tick(dt), evaluates the track, and applies the
// pose to the bound FractalParameters via CameraParamBinding. Registered on the
// ParameterAnimationBus by the Scene playback layer (S6) — inheriting the bus's
// render-completion gate, so camera motion tracks render cadence and never
// races the renderer.

using System;

using FracturingFog.Models;
using FracturingFog.Render;

namespace FracturingFog.Abstractions.Animation
{
    /// <summary>
    /// Animates the orbit camera of one 3D fractal from a <see cref="CameraTrack"/>.
    /// Time-driven (not phase-driven like the procedural animators): each tick
    /// advances an internal clock and samples the track, so the same track plays
    /// identically regardless of tick rate. Cost is
    /// <see cref="AnimatableParamCost.Moderate"/> — it drives a 3D raymarch, so
    /// the animated-param ceiling treats it as an expensive-frame track.
    /// </summary>
    public sealed class CameraTrackAnimator : IParameterAnimator
    {
        private readonly CameraTrack _track;
        private readonly FractalParameters _parameters;
        private readonly FractalType _type;
        private double _time;

        /// <param name="track">The path to play.</param>
        /// <param name="parameters">The live params whose camera fields are driven.</param>
        /// <param name="type">Which 3D fractal's camera to write — must be a
        /// <see cref="CameraParamBinding.Supports"/> type.</param>
        public CameraTrackAnimator(CameraTrack track, FractalParameters parameters, FractalType type)
        {
            _track = track ?? throw new ArgumentNullException(nameof(track));
            _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
            if (!CameraParamBinding.Supports(type))
                throw new ArgumentOutOfRangeException(nameof(type), type,
                    "FractalType has no orbit camera to animate.");
            _type = type;
        }

        /// <summary>Loop back to the track start after <see cref="CameraTrack.Duration"/>
        /// (default true). When false the clock clamps at the end and holds the
        /// final pose.</summary>
        public bool Loop { get; set; } = true;

        /// <summary>Current scene-clock position in seconds.</summary>
        public double Time => _time;

        public string Name => $"Camera ({_type})";

        public bool IsEnabled { get; set; } = true;

        /// <summary>3D-raymarched — the ceiling drops these first under load.</summary>
        public AnimatableParamCost Cost => AnimatableParamCost.Moderate;

        public void Tick(double dt)
        {
            if (!IsEnabled || _track.Keys.Count == 0) return;

            _time += dt;
            double dur = _track.Duration;
            if (dur > 0)
            {
                if (Loop) _time -= global::System.Math.Floor(_time / dur) * dur; // wrap into [0, dur)
                else if (_time > dur) _time = dur;                // clamp + hold
            }

            CameraParamBinding.Apply(_parameters, _type, _track.Evaluate(_time));
        }

        /// <summary>Rewind the scene clock to 0.</summary>
        public void Reset() => _time = 0.0;
    }
}
