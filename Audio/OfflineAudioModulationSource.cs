// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;

namespace FracturingFog.Audio
{
    /// <summary>
    /// Deterministic, seekable <see cref="IAudioModulationSource"/> for offline
    /// export (Audio-Reactive Phase 7 / #266). Where <see cref="AudioModulationSource"/>
    /// derives its envelopes / phase from the wall clock, this source is baked once
    /// from an analysed file into a fixed timeline — a run of band/RMS snapshots and
    /// the beat / downbeat event times, all in <em>audio seconds since the file
    /// start</em> — and reconstructs every signal at an arbitrary
    /// <see cref="SampleAt"/> time. Same input file → same timeline → same frame at
    /// the same second, so an exported video is reproducible frame-for-frame
    /// regardless of how fast the render ran.
    ///
    /// The reconstruction mirrors <see cref="AudioModulationSource"/> exactly
    /// (exponential beat/downbeat envelopes, tempo-locked saw/sine anchored to the
    /// last downbeat) but reads the audio-time deltas from the baked timeline rather
    /// than <see cref="DateTime.UtcNow"/>.
    /// </summary>
    public sealed class OfflineAudioModulationSource : IAudioModulationSource
    {
        private readonly double[] _times;        // sample times (audio seconds, ascending)
        private readonly BandEnergy[] _bands;    // band/RMS snapshot at each _times[i]
        private readonly double[] _bpms;         // BPM estimate at each _times[i]
        private readonly double[] _beatTimes;    // beat onset times (ascending)
        private readonly float[] _beatStrength;  // matching beat strengths
        private readonly double[] _downTimes;    // downbeat times (ascending)
        private readonly float[] _downStrength;  // matching downbeat strengths

        private double _lastSeconds;

        /// <summary>Envelope decay time-constant in seconds — matches the live
        /// source default so an offline render looks the same as the live view.</summary>
        public double DecaySeconds { get; init; } = 0.18;

        /// <summary>One-shot transient window in seconds — matches the live source.</summary>
        public double TransientWindowSeconds { get; init; } = 0.060;

        /// <param name="times">Sample times (audio seconds), strictly ascending.</param>
        /// <param name="bands">Band/RMS snapshot per sample time.</param>
        /// <param name="bpms">BPM estimate per sample time.</param>
        /// <param name="beatTimes">Beat onset times (audio seconds), ascending.</param>
        /// <param name="beatStrength">Strength (0..1) per beat.</param>
        /// <param name="downTimes">Downbeat times (audio seconds), ascending.</param>
        /// <param name="downStrength">Strength (0..1) per downbeat.</param>
        public OfflineAudioModulationSource(
            double[] times, BandEnergy[] bands, double[] bpms,
            double[] beatTimes, float[] beatStrength,
            double[] downTimes, float[] downStrength)
        {
            _times = times ?? throw new ArgumentNullException(nameof(times));
            _bands = bands ?? throw new ArgumentNullException(nameof(bands));
            _bpms = bpms ?? throw new ArgumentNullException(nameof(bpms));
            _beatTimes = beatTimes ?? Array.Empty<double>();
            _beatStrength = beatStrength ?? Array.Empty<float>();
            _downTimes = downTimes ?? Array.Empty<double>();
            _downStrength = downStrength ?? Array.Empty<float>();
            if (_bands.Length != _times.Length || _bpms.Length != _times.Length)
                throw new ArgumentException("times/bands/bpms length mismatch.");
        }

        /// <summary>True once at least one sample was baked. An empty analysis
        /// (silent / unreadable file) is inactive, so every binding is a no-op and
        /// the base look is preserved — same contract as the live source.</summary>
        public bool IsActive => _times.Length > 0;

        public long BeatCount => IsActive ? LastLE(_beatTimes, _lastSeconds) + 1 : 0;
        public long DownbeatCount => IsActive ? LastLE(_downTimes, _lastSeconds) + 1 : 0;

        /// <summary>Snapshot at the most recently seeked time. Offline has no live
        /// playhead; the renderer drives <see cref="SampleAt"/> directly.</summary>
        public AudioModulationFrame Sample() => SampleAt(_lastSeconds);

        public AudioModulationFrame SampleAt(double seconds)
        {
            if (_times.Length == 0) return AudioModulationFrame.Inactive;
            _lastSeconds = seconds;

            // Band / RMS: linear interpolation between the bracketing samples so a
            // param never steps between analysis hops. BPM steps (a discrete
            // estimate, not a continuous meter).
            BandEnergy band;
            double bpm;
            int i = LastLE(_times, seconds);
            if (i < 0) { band = _bands[0]; bpm = _bpms[0]; }
            else if (i >= _times.Length - 1) { band = _bands[^1]; bpm = _bpms[^1]; }
            else
            {
                double span = _times[i + 1] - _times[i];
                double f = span > 1e-9 ? (seconds - _times[i]) / span : 0.0;
                band = LerpBand(_bands[i], _bands[i + 1], (float)f);
                bpm = _bpms[i];
            }

            // Beat / downbeat envelopes from the most recent event at-or-before now.
            float beatPulse = 0f, downPulse = 0f;
            bool transient = false;
            int b = LastLE(_beatTimes, seconds);
            if (b >= 0)
            {
                double dt = seconds - _beatTimes[b];
                if (dt >= 0)
                {
                    beatPulse = Clamp01((float)(_beatStrength[b] * Math.Exp(-dt / DecaySeconds)));
                    transient = dt < TransientWindowSeconds;
                }
            }
            int d = LastLE(_downTimes, seconds);
            if (d >= 0)
            {
                double dt = seconds - _downTimes[d];
                if (dt >= 0)
                    downPulse = Clamp01((float)(_downStrength[d] * Math.Exp(-dt / DecaySeconds)));
            }

            // Tempo-locked phase anchored to the last downbeat (bar start); falls
            // back to the first beat seen, mirroring the live source.
            float saw = 0f, sine = 0f;
            double anchor = d >= 0 ? _downTimes[d]
                          : (_beatTimes.Length > 0 ? _beatTimes[0] : double.NaN);
            if (bpm > 0 && !double.IsNaN(anchor))
            {
                double since = seconds - anchor;
                if (since >= 0)
                {
                    double period = 60.0 / bpm;
                    double p = since / period;
                    saw = (float)(p - Math.Floor(p));
                    sine = (float)(0.5 + 0.5 * Math.Sin(2.0 * Math.PI * saw));
                }
            }

            return new AudioModulationFrame(
                band.Bass, band.LowMid, band.Mid, band.HighMid, band.High, band.Rms,
                beatPulse, downPulse, saw, sine,
                transient, bpm, true);
        }

        // Index of the last element <= v in an ascending array, or -1 if none.
        private static int LastLE(double[] a, double v)
        {
            int lo = 0, hi = a.Length - 1, res = -1;
            while (lo <= hi)
            {
                int m = (lo + hi) >> 1;
                if (a[m] <= v) { res = m; lo = m + 1; }
                else hi = m - 1;
            }
            return res;
        }

        private static BandEnergy LerpBand(BandEnergy a, BandEnergy b, float t) => new(
            a.Bass + (b.Bass - a.Bass) * t,
            a.LowMid + (b.LowMid - a.LowMid) * t,
            a.Mid + (b.Mid - a.Mid) * t,
            a.HighMid + (b.HighMid - a.HighMid) * t,
            a.High + (b.High - a.High) * t,
            a.Rms + (b.Rms - a.Rms) * t);

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
