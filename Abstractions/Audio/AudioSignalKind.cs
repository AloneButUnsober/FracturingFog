// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

namespace FracturingFog.Audio
{
    /// <summary>
    /// A single derived audio signal a <see cref="AudioModulationBinding"/> can
    /// map onto a target parameter. Every value is normalized 0..1 (bands / RMS
    /// / envelopes / phase carriers) so bindings share one shaping pipeline.
    /// Produced by <see cref="IAudioModulationSource"/>.
    /// </summary>
    public enum AudioSignalKind
    {
        /// <summary>20-150 Hz band level (kick / sub).</summary>
        Bass,
        /// <summary>150-400 Hz band level.</summary>
        LowMid,
        /// <summary>400-1500 Hz band level.</summary>
        Mid,
        /// <summary>1500-4000 Hz band level.</summary>
        HighMid,
        /// <summary>4000-12000 Hz band level (hats / air).</summary>
        High,
        /// <summary>Overall loudness (RMS).</summary>
        Rms,
        /// <summary>Attack-decay envelope that jumps on every detected beat.</summary>
        BeatPulse,
        /// <summary>Attack-decay envelope gated to bar starts (downbeats).</summary>
        DownbeatPulse,
        /// <summary>Tempo-locked sawtooth 0-&gt;1 per beat (free LFO synced to BPM).</summary>
        BpmPhaseSaw,
        /// <summary>Smooth 0..1 carrier: 0.5 + 0.5*sin of the BPM phase.</summary>
        BpmPhaseSine,
    }
}
