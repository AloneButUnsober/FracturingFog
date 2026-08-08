// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

namespace FracturingFog.Audio
{
    /// <summary>
    /// Immutable snapshot of every derived audio signal at one instant, produced
    /// by <see cref="IAudioModulationSource.Sample"/>. All band / RMS / envelope /
    /// phase fields are normalized 0..1. Consumers pull one frame per tick and
    /// feed it to their <see cref="AudioModulationBinding"/>s.
    /// </summary>
    public readonly record struct AudioModulationFrame(
        float Bass,
        float LowMid,
        float Mid,
        float HighMid,
        float High,
        float Rms,
        float BeatPulse,
        float DownbeatPulse,
        float BpmPhaseSaw,
        float BpmPhaseSine,
        bool Transient,
        double Bpm,
        bool IsActive)
    {
        /// <summary>All-zero, inactive frame. Bindings must treat this as
        /// "leave the target untouched" (see <see cref="IsActive"/>).</summary>
        public static AudioModulationFrame Inactive =>
            new(0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, false, 0.0, false);

        /// <summary>Read one normalized 0..1 signal by kind.</summary>
        public float Signal(AudioSignalKind kind) => kind switch
        {
            AudioSignalKind.Bass => Bass,
            AudioSignalKind.LowMid => LowMid,
            AudioSignalKind.Mid => Mid,
            AudioSignalKind.HighMid => HighMid,
            AudioSignalKind.High => High,
            AudioSignalKind.Rms => Rms,
            AudioSignalKind.BeatPulse => BeatPulse,
            AudioSignalKind.DownbeatPulse => DownbeatPulse,
            AudioSignalKind.BpmPhaseSaw => BpmPhaseSaw,
            AudioSignalKind.BpmPhaseSine => BpmPhaseSine,
            _ => 0f,
        };
    }
}
