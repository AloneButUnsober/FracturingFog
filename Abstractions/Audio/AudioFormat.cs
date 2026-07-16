// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

namespace FracturingFog.Audio
{
    /// <summary>
    /// Describes the PCM format a capture backend delivers samples in.
    /// Samples are always interleaved float32 in [-1, 1] on the
    /// <see cref="IAudioCaptureBackend"/> event boundary; <see cref="BitDepth"/>
    /// describes the source-side precision (16 / 24 / 32) for diagnostics only.
    /// </summary>
    public readonly record struct AudioFormat(int SampleRate, int Channels, int BitDepth)
    {
        public static AudioFormat Default => new(44100, 2, 32);

        public bool IsValid => SampleRate > 0 && Channels > 0;
    }
}
