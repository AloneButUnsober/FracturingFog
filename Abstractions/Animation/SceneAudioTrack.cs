// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Animation/SceneAudioTrack.cs
//
// Audio-Reactive Expansion — Phase 6 (#265): audio as a Scene Engine track
// *source*. Where a SceneGlobalTrack drives a scene-wide post/look scalar from
// keyframes over global scene time, a SceneAudioTrack drives the SAME scalar
// (the SceneGlobalTarget set: exposure / bloom / vignette / chromatic
// aberration) from a live audio signal instead of a curve. One target, two
// possible sources — keyframes or the music.
//
// The seam onto FractalParameters.Lighting is the identical SceneGlobalBinding
// used by keyframe tracks, so no new target plumbing: an audio track lands its
// value exactly where the exposure ramp does. The audio-shaping half is the
// tested AudioModulationBinding (signal -> curve -> gain/bias -> clamp -> range),
// the same row every other audio consumer uses.
//
// Pure data + a pure Apply, so it is deterministic given a frame and unit-
// testable without real audio (feed a fabricated AudioModulationFrame). The
// realtime consumer is SceneAudioTrackAnimator; the offline deterministic-export
// path (Phase 7 / #266) feeds SampleAt(sceneTime) frames into the same Apply.

using System;
using System.Collections.Generic;

using FracturingFog.Audio;
using FracturingFog.Models;

namespace FracturingFog.Abstractions.Animation
{
    /// <summary>A scene-wide, audio-driven post/look scalar. Reuses
    /// <see cref="SceneGlobalTarget"/> (the continuous exposure / bloom / vignette
    /// / chromatic-aberration knobs on <see cref="FractalParameters.Lighting"/>)
    /// so it lands its value through the same <see cref="SceneGlobalBinding"/> a
    /// keyframe <see cref="SceneGlobalTrack"/> uses — the only difference is the
    /// source: an <see cref="AudioModulationBinding"/> reading the music instead
    /// of keys over time. Applied on top of every shot, after the keyframe global
    /// tracks, so the audio layer is the live modulation riding a static scene
    /// look.</summary>
    public sealed class SceneAudioTrack
    {
        /// <summary>Which scene-wide continuous scalar this track drives.</summary>
        public SceneGlobalTarget Target { get; set; } = SceneGlobalTarget.Exposure;

        /// <summary>Audio signal + shaping + output range for this track. The
        /// binding's <see cref="AudioModulationBinding.OutMin"/> /
        /// <see cref="AudioModulationBinding.OutMax"/> are the target scalar's
        /// range (e.g. exposure 0.8..1.6), so <see cref="AudioModulationBinding.Evaluate"/>
        /// yields the value written straight onto the parameter.</summary>
        public AudioModulationBinding Binding { get; set; } = new();

        /// <summary>Evaluate this track's audio binding against
        /// <paramref name="frame"/> and write the result onto
        /// <paramref name="p"/>. The caller gates on
        /// <see cref="AudioModulationFrame.IsActive"/> (an inactive analyzer must
        /// leave the base look untouched).</summary>
        public void Apply(FractalParameters p, in AudioModulationFrame frame)
        {
            ArgumentNullException.ThrowIfNull(p);
            SceneGlobalBinding.Apply(p, Target, Binding.Evaluate(frame));
        }
    }

    /// <summary>Applies a whole set of scene audio tracks at one audio frame.
    /// Later tracks win if two target the same scalar (mirrors the "later track
    /// overrides earlier" rule on <see cref="SceneGlobalTracks"/>).</summary>
    public static class SceneAudioTracks
    {
        /// <summary>Evaluate every track in <paramref name="tracks"/> against
        /// <paramref name="frame"/> and write the results onto
        /// <paramref name="p"/>. A null / empty list — or an inactive frame — is a
        /// no-op, so a scene with no audio tracks (or a headless / silent backend)
        /// renders exactly as its shots + keyframe global tracks dictate.</summary>
        public static void Apply(IReadOnlyList<SceneAudioTrack>? tracks,
                                 FractalParameters p, in AudioModulationFrame frame)
        {
            if (tracks == null || tracks.Count == 0) return;
            if (!frame.IsActive) return; // inactive analyzer leaves the base look untouched
            ArgumentNullException.ThrowIfNull(p);
            for (int i = 0; i < tracks.Count; i++)
                tracks[i]?.Apply(p, frame);
        }
    }
}
