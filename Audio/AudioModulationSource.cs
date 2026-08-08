// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;

namespace FracturingFog.Audio
{
    /// <summary>
    /// Live <see cref="IAudioModulationSource"/> over an <see cref="IBeatSource"/>.
    /// Band levels / RMS come straight from <see cref="IBeatSource.CurrentEnergy"/>;
    /// the beat / downbeat envelopes and the tempo-locked phase are computed
    /// <em>analytically</em> from the timestamps stashed on each beat event, so
    /// there is no background ticker and no per-consumer state — every
    /// <see cref="Sample"/> is a pure read of a few volatile fields under a short
    /// lock. Beats are infrequent (a handful per second); sampling is ~20 Hz; the
    /// lock is uncontended in practice.
    /// </summary>
    public sealed class AudioModulationSource : IAudioModulationSource
    {
        private readonly IBeatSource _beats;
        private readonly Func<DateTime> _nowUtc;
        private readonly object _gate = new();

        // Snapshots stashed on each beat / downbeat, read back in Sample().
        private DateTime _lastBeatUtc = DateTime.MinValue;
        private float _lastBeatStrength;
        private DateTime _lastDownbeatUtc = DateTime.MinValue;
        private float _lastDownbeatStrength;
        // Phase is anchored to the most recent downbeat (bar start) so the saw
        // resets on the "1" of each bar; falls back to the first beat seen.
        private DateTime _phaseAnchorUtc = DateTime.MinValue;

        /// <summary>Envelope decay time-constant in seconds — a pulse falls to
        /// 1/e after this long. Default 0.18 s (a musical thump). Floored so it
        /// can never divide-by-zero.</summary>
        public double DecaySeconds
        {
            get => _decaySeconds;
            set => _decaySeconds = Math.Max(1e-3, value);
        }
        private double _decaySeconds = 0.18;

        /// <summary>A beat is reported as a one-shot <see cref="AudioModulationFrame.Transient"/>
        /// for this long after it lands — sized to roughly one sample tick so a
        /// consumer polling at ~20 Hz catches each onset exactly once without any
        /// consume-on-read race. Default 60 ms.</summary>
        public double TransientWindowSeconds
        {
            get => _transientWindow;
            set => _transientWindow = Math.Max(1e-3, value);
        }
        private double _transientWindow = 0.060;

        /// <param name="beats">Underlying analyzer.</param>
        /// <param name="nowUtc">Clock override for tests; defaults to
        /// <see cref="DateTime.UtcNow"/>.</param>
        public AudioModulationSource(IBeatSource beats, Func<DateTime>? nowUtc = null)
        {
            _beats = beats ?? throw new ArgumentNullException(nameof(beats));
            _nowUtc = nowUtc ?? (() => DateTime.UtcNow);
            _beats.Beat += OnBeat;
            _beats.Downbeat += OnDownbeat;
        }

        public bool IsActive => _beats.IsActive;

        private void OnBeat(object? sender, BeatEventArgs e)
        {
            lock (_gate)
            {
                _lastBeatUtc = e.TimestampUtc;
                _lastBeatStrength = Clamp01((float)e.Strength);
                if (_phaseAnchorUtc == DateTime.MinValue) _phaseAnchorUtc = e.TimestampUtc;
            }
        }

        private void OnDownbeat(object? sender, BeatEventArgs e)
        {
            lock (_gate)
            {
                _lastDownbeatUtc = e.TimestampUtc;
                _lastDownbeatStrength = Clamp01((float)e.Strength);
                _phaseAnchorUtc = e.TimestampUtc;   // re-anchor phase to the bar
            }
        }

        public AudioModulationFrame Sample() => SampleInternal(_nowUtc());

        // The live source has no seekable per-time history; the offline seeking
        // source arrives in Phase 7 (#266). Until then, ignore the argument.
        public AudioModulationFrame SampleAt(double seconds) => Sample();

        private AudioModulationFrame SampleInternal(DateTime now)
        {
            if (!_beats.IsActive) return AudioModulationFrame.Inactive;

            var energy = _beats.CurrentEnergy;
            double bpm = _beats.EstimatedBpm;

            DateTime lastBeat, lastDown, anchor;
            float beatStr, downStr;
            lock (_gate)
            {
                lastBeat = _lastBeatUtc; beatStr = _lastBeatStrength;
                lastDown = _lastDownbeatUtc; downStr = _lastDownbeatStrength;
                anchor = _phaseAnchorUtc;
            }

            float beatPulse = Envelope(now, lastBeat, beatStr);
            float downPulse = Envelope(now, lastDown, downStr);

            bool transient = lastBeat != DateTime.MinValue
                && (now - lastBeat).TotalSeconds is var dt
                && dt >= 0 && dt < _transientWindow;

            float saw = 0f, sine = 0f;
            if (bpm > 0 && anchor != DateTime.MinValue)
            {
                double period = 60.0 / bpm;                       // seconds per beat
                double since = (now - anchor).TotalSeconds;
                if (since >= 0)
                {
                    double p = since / period;
                    saw = (float)(p - Math.Floor(p));            // 0..1 sawtooth
                    sine = (float)(0.5 + 0.5 * Math.Sin(2.0 * Math.PI * saw));
                }
            }

            return new AudioModulationFrame(
                Clamp01(energy.Bass), Clamp01(energy.LowMid), Clamp01(energy.Mid),
                Clamp01(energy.HighMid), Clamp01(energy.High), Clamp01(energy.Rms),
                beatPulse, downPulse, saw, sine,
                transient, bpm, true);
        }

        private float Envelope(DateTime now, DateTime last, float strength)
        {
            if (last == DateTime.MinValue) return 0f;
            double dt = (now - last).TotalSeconds;
            if (dt < 0) return 0f;                    // future timestamp — ignore
            return Clamp01((float)(strength * Math.Exp(-dt / _decaySeconds)));
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
