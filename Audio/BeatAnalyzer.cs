using System;
using System.Threading;
using NAudio.Dsp;
using NAudio.Wave;

namespace FracturingFog.Audio
{
    /// <summary>
    /// Streaming audio analyzer: FFT-based spectral flux onset detection,
    /// adaptive threshold, BPM estimation via onset autocorrelation, and
    /// per-band energy smoothing. Thread-safe input (ProcessSamples / ProcessRawBytes).
    /// </summary>
    public sealed class BeatAnalyzer : IBeatSource
    {
        // FFT config
        private const int FftSizeLog2 = 11;           // 2048-point FFT
        private const int FftSize = 1 << FftSizeLog2; // 2048
        private const int HopSize = FftSize / 2;      // 50% overlap

        // Spectral flux history window (for adaptive threshold). ~3 s at 86 frames/s.
        private const int FluxHistory = 256;

        // Onset history for BPM estimation. ~10 s worth of frames.
        private const int OnsetHistory = 1024;

        private readonly object _stateLock = new();
        private readonly float[] _windowFn = new float[FftSize];
        private readonly float[] _ring = new float[FftSize];
        private int _ringFill;
        private readonly float[] _prevMag = new float[FftSize / 2];
        private readonly float[] _fluxHist = new float[FluxHistory];
        private int _fluxHead;
        private int _fluxCount;
        private readonly float[] _onsetTimes = new float[OnsetHistory]; // seconds since start
        private int _onsetCount;
        private double _sampleClock;     // running sample count
        private int _sampleRate = 44100;
        private int _channels = 2;

        // Band edges in Hz (5 bands).
        private static readonly float[] BandEdges = { 20f, 150f, 400f, 1500f, 4000f, 12000f };
        private readonly float[] _bandEnergyEmaShort = new float[5];
        private readonly float[] _bandEnergyEmaLong = new float[5];
        private float _rmsEma;

        private int _beatIndex;
        private int _downbeatBeatCounter;
        private double _bpm;
        private long _lastBeatSampleClock = long.MinValue;
        private BandEnergy _currentEnergy = BandEnergy.Empty;

        public BeatAnalyzer(int sampleRate, int channels)
        {
            _sampleRate = sampleRate;
            _channels = System.Math.Max(1, channels);
            // Hann window
            for (int i = 0; i < FftSize; i++)
                _windowFn[i] = 0.5f * (1f - (float)System.Math.Cos(2.0 * System.Math.PI * i / (FftSize - 1)));
        }

        public float Sensitivity { get; set; } = 0.5f;

        public bool IsActive { get; private set; } = true;

        public double EstimatedBpm => _bpm;

        public BandEnergy CurrentEnergy => _currentEnergy;

        public event EventHandler<BeatEventArgs>? Beat;
        public event EventHandler<BeatEventArgs>? Downbeat;

        public void EnsureFormat(int sampleRate, int channels)
        {
            if (sampleRate == _sampleRate && channels == _channels) return;
            lock (_stateLock)
            {
                _sampleRate = sampleRate;
                _channels = System.Math.Max(1, channels);
                Array.Clear(_ring, 0, _ring.Length);
                Array.Clear(_prevMag, 0, _prevMag.Length);
                Array.Clear(_fluxHist, 0, _fluxHist.Length);
                _ringFill = 0;
                _fluxHead = 0;
                _fluxCount = 0;
                _onsetCount = 0;
                _sampleClock = 0;
                _beatIndex = 0;
                _bpm = 0;
                _lastBeatSampleClock = long.MinValue;
            }
        }

        /// <summary>Accepts raw bytes in the supplied WaveFormat — converts to mono float[-1,1].</summary>
        public void ProcessRawBytes(ReadOnlySpan<byte> bytes, WaveFormat fmt)
        {
            int frames = bytes.Length / (fmt.Channels * fmt.BitsPerSample / 8);
            if (frames <= 0) return;
            Span<float> mono = frames <= 4096 ? stackalloc float[frames] : new float[frames];

            if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
            {
                var src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes);
                MixDownToMono(src, mono, fmt.Channels);
            }
            else if (fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 16)
            {
                var src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(bytes);
                Span<float> tmp = src.Length <= 8192 ? stackalloc float[src.Length] : new float[src.Length];
                for (int i = 0; i < src.Length; i++) tmp[i] = src[i] / 32768f;
                MixDownToMono(tmp, mono, fmt.Channels);
            }
            else if (fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 32)
            {
                var src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(bytes);
                Span<float> tmp = src.Length <= 8192 ? stackalloc float[src.Length] : new float[src.Length];
                for (int i = 0; i < src.Length; i++) tmp[i] = src[i] / (float)int.MaxValue;
                MixDownToMono(tmp, mono, fmt.Channels);
            }
            else
            {
                // Unsupported format — silently ignore.
                return;
            }

            FeedMono(mono);
        }

        /// <summary>Accepts interleaved float samples — mixes down to mono and feeds the FFT.</summary>
        public void ProcessSamples(ReadOnlySpan<float> interleaved)
        {
            int channels = _channels;
            int frames = interleaved.Length / channels;
            if (frames <= 0) return;
            Span<float> mono = frames <= 4096 ? stackalloc float[frames] : new float[frames];
            MixDownToMono(interleaved, mono, channels);
            FeedMono(mono);
        }

        private static void MixDownToMono(ReadOnlySpan<float> interleaved, Span<float> mono, int channels)
        {
            if (channels == 1)
            {
                interleaved.Slice(0, mono.Length).CopyTo(mono);
                return;
            }
            float scale = 1f / channels;
            for (int f = 0; f < mono.Length; f++)
            {
                float sum = 0f;
                int baseIdx = f * channels;
                for (int c = 0; c < channels; c++) sum += interleaved[baseIdx + c];
                mono[f] = sum * scale;
            }
        }

        private void FeedMono(ReadOnlySpan<float> mono)
        {
            lock (_stateLock)
            {
                int pos = 0;
                while (pos < mono.Length)
                {
                    int take = System.Math.Min(FftSize - _ringFill, mono.Length - pos);
                    for (int i = 0; i < take; i++) _ring[_ringFill + i] = mono[pos + i];
                    _ringFill += take;
                    pos += take;
                    _sampleClock += take;
                    if (_ringFill >= FftSize)
                    {
                        AnalyzeWindow();
                        // Slide ring by HopSize (keep last FftSize-HopSize samples).
                        int keep = FftSize - HopSize;
                        Array.Copy(_ring, HopSize, _ring, 0, keep);
                        _ringFill = keep;
                    }
                }
            }
        }

        private void AnalyzeWindow()
        {
            // Apply window + pack into NAudio Complex[].
            var fft = new Complex[FftSize];
            float rmsSum = 0f;
            for (int i = 0; i < FftSize; i++)
            {
                float s = _ring[i] * _windowFn[i];
                fft[i].X = s;
                fft[i].Y = 0f;
                rmsSum += s * s;
            }
            float rms = (float)System.Math.Sqrt(rmsSum / FftSize);
            _rmsEma = Lerp(_rmsEma, rms, 0.15f);

            FastFourierTransform.FFT(true, FftSizeLog2, fft);

            // Magnitude spectrum.
            int half = FftSize / 2;
            Span<float> mag = stackalloc float[half];
            for (int i = 0; i < half; i++)
            {
                float re = fft[i].X, im = fft[i].Y;
                mag[i] = (float)System.Math.Sqrt(re * re + im * im);
            }

            // Spectral flux (positive differences only).
            float flux = 0f;
            for (int i = 0; i < half; i++)
            {
                float d = mag[i] - _prevMag[i];
                if (d > 0f) flux += d;
                _prevMag[i] = mag[i];
            }

            // Per-band energies.
            float binHz = _sampleRate / (float)FftSize;
            Span<float> bands = stackalloc float[5];
            for (int b = 0; b < 5; b++)
            {
                int lo = (int)System.Math.Floor(BandEdges[b] / binHz);
                int hi = System.Math.Min(half - 1, (int)System.Math.Ceiling(BandEdges[b + 1] / binHz));
                float sum = 0f;
                int n = System.Math.Max(1, hi - lo + 1);
                for (int i = lo; i <= hi; i++) sum += mag[i];
                bands[b] = sum / n;
            }
            // Normalize via dual-EMA (fast/slow). Long EMA = noise floor.
            for (int b = 0; b < 5; b++)
            {
                _bandEnergyEmaShort[b] = Lerp(_bandEnergyEmaShort[b], bands[b], 0.30f);
                _bandEnergyEmaLong[b] = Lerp(_bandEnergyEmaLong[b], bands[b], 0.02f);
            }
            float n0 = Norm(_bandEnergyEmaShort[0], _bandEnergyEmaLong[0]);
            float n1 = Norm(_bandEnergyEmaShort[1], _bandEnergyEmaLong[1]);
            float n2 = Norm(_bandEnergyEmaShort[2], _bandEnergyEmaLong[2]);
            float n3 = Norm(_bandEnergyEmaShort[3], _bandEnergyEmaLong[3]);
            float n4 = Norm(_bandEnergyEmaShort[4], _bandEnergyEmaLong[4]);
            var energy = new BandEnergy(n0, n1, n2, n3, n4, System.Math.Min(1f, _rmsEma * 3f));
            _currentEnergy = energy;

            // Push flux into history.
            _fluxHist[_fluxHead] = flux;
            _fluxHead = (_fluxHead + 1) % FluxHistory;
            if (_fluxCount < FluxHistory) _fluxCount++;

            // Adaptive threshold: mean + k * std over recent flux window.
            float mean = 0f;
            for (int i = 0; i < _fluxCount; i++) mean += _fluxHist[i];
            mean /= _fluxCount;
            float varSum = 0f;
            for (int i = 0; i < _fluxCount; i++)
            {
                float d = _fluxHist[i] - mean;
                varSum += d * d;
            }
            float std = (float)System.Math.Sqrt(varSum / System.Math.Max(1, _fluxCount));

            // Map Sensitivity (0..1) -> multiplier (2.5 .. 1.0). Higher sensitivity = lower threshold.
            float k = 2.5f - 1.5f * System.Math.Clamp(Sensitivity, 0f, 1f);
            float thresh = mean + k * std;

            // Refractory period: min 100ms between beats (avoid double-trigger).
            long sampleClockNow = (long)_sampleClock;
            long minGap = _sampleRate / 10;
            bool refractoryOk = sampleClockNow - _lastBeatSampleClock > minGap;

            if (refractoryOk && flux > thresh && _fluxCount > 16)
            {
                _lastBeatSampleClock = sampleClockNow;
                float t = (float)(sampleClockNow / (double)_sampleRate);
                _onsetTimes[_onsetCount % OnsetHistory] = t;
                _onsetCount++;

                EstimateBpm();
                _beatIndex++;
                _downbeatBeatCounter++;

                float strength = std > 1e-6f ? System.Math.Min(1f, (flux - mean) / (std * 4f)) : 0.5f;
                var ev = new BeatEventArgs
                {
                    TimestampUtc = DateTime.UtcNow,
                    Strength = strength,
                    Energy = energy,
                    BeatIndex = _beatIndex,
                    BpmEstimate = _bpm,
                };
                Beat?.Invoke(this, ev);
                // Downbeat every 4 beats (assume 4/4 — robust enough; refined when BPM known).
                if (_downbeatBeatCounter >= 4)
                {
                    _downbeatBeatCounter = 0;
                    Downbeat?.Invoke(this, ev);
                }
            }
        }

        private void EstimateBpm()
        {
            int n = System.Math.Min(_onsetCount, OnsetHistory);
            if (n < 8) return;
            // Use inter-onset intervals → median → BPM.
            int start = _onsetCount - n;
            Span<float> intervals = stackalloc float[n - 1];
            for (int i = 1; i < n; i++)
            {
                float a = _onsetTimes[(start + i - 1) % OnsetHistory];
                float b = _onsetTimes[(start + i) % OnsetHistory];
                intervals[i - 1] = b - a;
            }
            // In-place quickselect-ish: copy + sort (small n, cheap).
            var arr = intervals.ToArray();
            Array.Sort(arr);
            float median = arr[arr.Length / 2];
            if (median > 0.05f && median < 2.0f)
            {
                double bpm = 60.0 / median;
                // Fold extreme values into musical range 60..200.
                while (bpm < 60) bpm *= 2;
                while (bpm > 200) bpm /= 2;
                _bpm = 0.7 * _bpm + 0.3 * bpm; // smooth
            }
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;
        private static float Norm(float shortEma, float longEma)
        {
            if (longEma < 1e-6f) return 0f;
            return System.Math.Clamp(shortEma / (longEma * 4f), 0f, 1f);
        }
    }
}
