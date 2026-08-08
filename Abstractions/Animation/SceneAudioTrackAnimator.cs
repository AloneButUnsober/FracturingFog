// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Animation/SceneAudioTrackAnimator.cs
//
// Audio-Reactive Expansion — Phase 6 (#265), realtime consumer. Drives a scene's
// SceneAudioTrack set from a live IAudioModulationSource through the animation
// bus, so scene-wide audio-reactive post/look (exposure pumping on the kick,
// bloom swelling with loudness) inherits the bus's render-completion gate — the
// same flicker-free contract as the camera / param / keyframe-global tracks.
//
// Unlike SceneGlobalTrackAnimator this animator has no clock: it samples the
// live source each tick rather than advancing a global scene time (the deterministic
// scene-time sampling — SampleAt — belongs to the offline export path in Phase 7).
// The bus clears its dynamic animators on each shot cut, so the realtime scene
// player re-installs this animator per shot (see AnimationBusHost.LoadSceneShot);
// with no clock there is nothing to seed.

using System;
using System.Collections.Generic;

using FracturingFog.Audio;
using FracturingFog.Models;

namespace FracturingFog.Abstractions.Animation
{
    /// <summary>Samples an <see cref="IAudioModulationSource"/> each
    /// <see cref="Tick"/> and applies every <see cref="SceneAudioTrack"/> onto the
    /// bound params. Cost is <see cref="AnimatableParamCost.Cheap"/> — post-process
    /// scalars, so the animated-param ceiling never sheds them ahead of a raymarch
    /// track. When the source is inactive the tick is a no-op, leaving the base
    /// look untouched (headless / silent-backend contract).</summary>
    public sealed class SceneAudioTrackAnimator : IParameterAnimator
    {
        private readonly IReadOnlyList<SceneAudioTrack> _tracks;
        private readonly FractalParameters _parameters;
        private readonly IAudioModulationSource _source;

        /// <param name="tracks">The scene's audio tracks (shared reference).</param>
        /// <param name="parameters">The live params whose lighting fields are driven.</param>
        /// <param name="source">The live audio modulation source (pull-model).</param>
        public SceneAudioTrackAnimator(IReadOnlyList<SceneAudioTrack> tracks,
                                       FractalParameters parameters,
                                       IAudioModulationSource source)
        {
            _tracks = tracks ?? throw new ArgumentNullException(nameof(tracks));
            _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
            _source = source ?? throw new ArgumentNullException(nameof(source));
        }

        public string Name => "Scene audio tracks";

        public bool IsEnabled { get; set; } = true;

        /// <summary>Post-process scalars — cheap to change, never shed first.</summary>
        public AnimatableParamCost Cost => AnimatableParamCost.Cheap;

        public void Tick(double dt)
        {
            if (!IsEnabled || _tracks.Count == 0) return;
            if (!_source.IsActive) return;
            SceneAudioTracks.Apply(_tracks, _parameters, _source.Sample());
        }

        /// <summary>True when the set has at least one track to apply.</summary>
        public bool HasWork => _tracks.Count > 0;
    }
}
