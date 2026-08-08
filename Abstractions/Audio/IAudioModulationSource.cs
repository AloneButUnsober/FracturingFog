// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

namespace FracturingFog.Audio
{
    /// <summary>
    /// Derives ready-to-use modulation signals (band levels, RMS, beat / downbeat
    /// envelopes, tempo-locked phase) from an <see cref="IBeatSource"/> and hands
    /// them out as immutable <see cref="AudioModulationFrame"/>s on a pull model.
    /// Pull (not event) fits the render-gated animation bus: a consumer samples
    /// the latest state when it ticks, never mid-render, and multiple consumers
    /// can sample the same instant without coordination.
    /// </summary>
    public interface IAudioModulationSource
    {
        /// <summary>True when the underlying analyzer is running and producing
        /// samples. When false, callers must leave their base parameters
        /// untouched (see <see cref="AudioModulationFrame.Inactive"/>).</summary>
        bool IsActive { get; }

        /// <summary>Snapshot every signal at the current wall-clock instant.
        /// Cheap, allocation-free, thread-safe.</summary>
        AudioModulationFrame Sample();

        /// <summary>
        /// Snapshot every signal at <paramref name="seconds"/> since the source's
        /// timeline start — for deterministic offline export (Phase 7 / #266),
        /// where frames must be reproducible from a file's audio timeline rather
        /// than the wall clock. A live source that has no seekable history may
        /// ignore the argument and return <see cref="Sample"/>; a dedicated
        /// offline source seeds real per-time state.
        /// </summary>
        AudioModulationFrame SampleAt(double seconds);
    }
}
