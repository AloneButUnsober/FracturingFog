// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;

namespace FracturingFog.Audio
{
    /// <summary>
    /// Provides beat / band-energy events to consumers (slideshow, video zoom).
    /// Thread-safe: implementations may raise events from a non-UI thread.
    /// </summary>
    public interface IBeatSource
    {
        /// <summary>True if engine is running and producing samples (not muted, source connected).</summary>
        bool IsActive { get; }

        /// <summary>Best-effort beats-per-minute estimate, or 0 if unknown.</summary>
        double EstimatedBpm { get; }

        /// <summary>Latest band-energy snapshot (0..1). Read-only, replaced atomically.</summary>
        BandEnergy CurrentEnergy { get; }

        /// <summary>Fires when a beat onset is detected.</summary>
        event EventHandler<BeatEventArgs>? Beat;

        /// <summary>Fires when a downbeat (start of a bar / phrase boundary) is detected.</summary>
        event EventHandler<BeatEventArgs>? Downbeat;
    }

    public sealed class BeatEventArgs : EventArgs
    {
        public DateTime TimestampUtc { get; init; }
        public double Strength { get; init; }   // 0..1, normalized onset magnitude
        public BandEnergy Energy { get; init; } = BandEnergy.Empty;
        public int BeatIndex { get; init; }     // running count since engine start
        public double BpmEstimate { get; init; } // best-effort BPM at event time
    }

    /// <summary>Per-band normalized energy (0..1, smoothed). Immutable snapshot.</summary>
    public readonly record struct BandEnergy(float Bass, float LowMid, float Mid, float HighMid, float High, float Rms)
    {
        public static BandEnergy Empty => new(0f, 0f, 0f, 0f, 0f, 0f);
    }
}
