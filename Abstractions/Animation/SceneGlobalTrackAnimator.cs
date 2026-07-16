// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Animation/SceneGlobalTrackAnimator.cs
//
// Scene Engine Roadmap — S8 "global tracks", realtime consumer. Drives a scene's
// SceneGlobalTrack set through the animation bus so scene-wide post/look sweeps
// (exposure ramp, bloom swell, closing vignette) inherit the bus's render-
// completion gate — flicker-free, same as the camera + param tracks.
//
// Unlike CameraTrackAnimator, this animator reads GLOBAL scene time, not a
// shot's local clock: the whole set is authored once against the timeline. The
// bus clears its dynamic animators on each shot cut, so the realtime driver
// re-installs this animator per shot seeded with the current global clock
// (see AnimationBusHost.LoadSceneShot) — the track therefore continues from the
// right global time across a cut instead of restarting.

using System;
using System.Collections.Generic;

using FracturingFog.Models;

namespace FracturingFog.Abstractions.Animation
{
    /// <summary>Advances a global scene clock each <see cref="Tick"/> and applies
    /// every active <see cref="SceneGlobalTrack"/> onto the bound params. Cost is
    /// <see cref="AnimatableParamCost.Cheap"/> — these are post-process scalars, so
    /// the animated-param ceiling never sheds them ahead of a raymarch track.</summary>
    public sealed class SceneGlobalTrackAnimator : IParameterAnimator
    {
        private readonly IReadOnlyList<SceneGlobalTrack> _tracks;
        private readonly FractalParameters _parameters;
        private double _time;

        /// <param name="tracks">The scene's global tracks (shared reference).</param>
        /// <param name="parameters">The live params whose lighting fields are driven.</param>
        /// <param name="startTime">Initial global-clock value (seconds) — the
        /// global time of the shot this animator is (re)installed on, so the sweep
        /// picks up mid-timeline across a shot cut.</param>
        public SceneGlobalTrackAnimator(IReadOnlyList<SceneGlobalTrack> tracks,
                                        FractalParameters parameters, double startTime = 0.0)
        {
            _tracks = tracks ?? throw new ArgumentNullException(nameof(tracks));
            _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
            _time = startTime;
        }

        public string Name => "Scene global tracks";

        public bool IsEnabled { get; set; } = true;

        /// <summary>Post-process scalars — cheap to change, never shed first.</summary>
        public AnimatableParamCost Cost => AnimatableParamCost.Cheap;

        /// <summary>Current global-clock position in seconds.</summary>
        public double Time => _time;

        public void Tick(double dt)
        {
            if (!IsEnabled || _tracks.Count == 0) return;
            _time += dt;
            SceneGlobalTracks.Apply(_tracks, _parameters, _time);
        }

        /// <summary>True when at least one bound track has a key to apply.</summary>
        public bool HasWork
        {
            get
            {
                for (int i = 0; i < _tracks.Count; i++)
                    if (_tracks[i] != null && _tracks[i].IsActive) return true;
                return false;
            }
        }

        public void Reset() => _time = 0.0;
    }
}
