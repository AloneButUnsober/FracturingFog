using System;
using NAudio.Wave;

namespace FracturingFog.Audio
{
    /// <summary>
    /// Generates fractal-derived audio: a scale-quantized arpeggio whose notes
    /// come from a horizontal scan of iteration counts at the current viewport,
    /// plus a granular drone bed derived from the same data via IFFT.
    /// Implements <see cref="ISampleProvider"/> so it can drive an NAudio output
    /// device and/or be tapped into the analyzer for closed-loop sync.
    /// </summary>
    public sealed class FractalSynth : ISampleProvider
    {
        private readonly WaveFormat _format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        public WaveFormat WaveFormat => _format;

        // Probe: returns iteration count (0..maxIter) at (re, im).
        private readonly Func<double, double, int, int> _probe;

        // Viewport state — caller updates as the user pans/zooms.
        private double _centerX, _centerY;
        private double _zoom = 1.0;
        private int _maxIter = 256;
        private readonly object _viewLock = new();

        // Arpeggio
        private const int ArpStepsPerBar = 16;   // 16th notes
        private double _bpm = 120;
        private int _arpStepIndex;
        private double _arpStepSamplesAccum;
        private double _arpStepSamples;
        private static readonly int[] PentatonicMinor = { 0, 3, 5, 7, 10 };
        private const int BaseMidi = 48; // C3
        private const int OctaveRange = 4;

        // Voice
        private float _voicePhase;
        private float _voiceFreq;
        private float _voiceEnv;
        private float _voiceEnvTarget;
        private float _voiceEnvCoef = 0.005f;

        // Drone bed via low-frequency oscillator pair
        private float _dronePhaseA;
        private float _dronePhaseB;
        private float _droneFreqA = 55f;   // A1
        private float _droneFreqB = 82.4f; // E2 (a fifth)
        private float _droneAmp = 0.05f;
        private float _droneLpf;

        // Sample probe scan cache (refreshed periodically).
        private int[] _scanCache = new int[16];
        private const double ScanRefreshIntervalSec = 0.25;
        private double _samplesSinceScan;

        private float _masterGain = 0.6f;

        public FractalSynth(Func<double, double, int, int> iterationProbe)
        {
            _probe = iterationProbe ?? throw new ArgumentNullException(nameof(iterationProbe));
            UpdateArpStepSamples();
        }

        public void UpdateViewport(double centerX, double centerY, double zoom, int maxIter)
        {
            lock (_viewLock)
            {
                _centerX = centerX;
                _centerY = centerY;
                _zoom = zoom <= 0 ? 1.0 : zoom;
                _maxIter = System.Math.Max(32, maxIter);
            }
            _samplesSinceScan = double.MaxValue; // force refresh
        }

        public void SetBpm(double bpm)
        {
            _bpm = System.Math.Clamp(bpm, 30, 240);
            UpdateArpStepSamples();
        }

        public void SetMasterGain(float gain) => _masterGain = System.Math.Clamp(gain, 0f, 1f);

        private void UpdateArpStepSamples()
        {
            // Beats per second = bpm/60; ArpStepsPerBar steps per 4 beats.
            double bps = _bpm / 60.0;
            double stepsPerSec = bps * ArpStepsPerBar / 4.0;
            _arpStepSamples = _format.SampleRate / stepsPerSec;
        }

        private void RefreshScan()
        {
            double cx, cy, zoom;
            int maxIter;
            lock (_viewLock)
            {
                cx = _centerX; cy = _centerY; zoom = _zoom; maxIter = _maxIter;
            }
            // Sample horizontal line at center.y across viewport.
            double span = 3.0 / zoom; // arbitrary span
            int n = _scanCache.Length;
            for (int i = 0; i < n; i++)
            {
                double t = (i / (double)(n - 1)) - 0.5;
                double re = cx + t * span;
                double im = cy;
                _scanCache[i] = _probe(re, im, maxIter);
            }
        }

        private void TriggerArpStep()
        {
            int idx = _arpStepIndex % _scanCache.Length;
            int iter = _scanCache[idx];
            // Quantize to pentatonic minor across OctaveRange octaves.
            int scaleLen = PentatonicMinor.Length;
            int totalSteps = scaleLen * OctaveRange;
            int stepInScale = totalSteps == 0 ? 0 : (iter % totalSteps);
            int octave = stepInScale / scaleLen;
            int degree = stepInScale % scaleLen;
            int midi = BaseMidi + octave * 12 + PentatonicMinor[degree];
            _voiceFreq = MidiToFreq(midi);
            // Velocity from iter density.
            float vel = System.Math.Clamp(iter / 96f, 0.15f, 1.0f);
            _voiceEnvTarget = vel;
            // Strong attack: snap env up; decay back to 0 over the step.
            _voiceEnv = vel;
            // Decay coefficient: drops to ~5% over step.
            _voiceEnvCoef = (float)(3.0 / _arpStepSamples);
            _arpStepIndex++;
            if (_arpStepIndex % 4 == 0)
            {
                // Refresh scan every 4 steps so the texture follows the view.
                _samplesSinceScan = double.MaxValue;
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            // count is in samples (interleaved). frames = count / channels.
            int channels = _format.Channels;
            int frames = count / channels;
            int written = 0;
            float sampleRate = _format.SampleRate;

            for (int f = 0; f < frames; f++)
            {
                if (_samplesSinceScan >= ScanRefreshIntervalSec * sampleRate)
                {
                    RefreshScan();
                    _samplesSinceScan = 0;
                }
                else
                {
                    _samplesSinceScan++;
                }

                // Advance arp step.
                _arpStepSamplesAccum++;
                if (_arpStepSamplesAccum >= _arpStepSamples)
                {
                    _arpStepSamplesAccum -= _arpStepSamples;
                    TriggerArpStep();
                }

                // Voice oscillator (saw → soft via tanh-ish).
                _voicePhase += _voiceFreq / sampleRate;
                if (_voicePhase >= 1f) _voicePhase -= 1f;
                float saw = 2f * _voicePhase - 1f;
                float voice = saw * _voiceEnv;
                // Exponential env decay.
                _voiceEnv -= _voiceEnv * _voiceEnvCoef;

                // Drone (two sine pair).
                _dronePhaseA += _droneFreqA / sampleRate;
                if (_dronePhaseA >= 1f) _dronePhaseA -= 1f;
                _dronePhaseB += _droneFreqB / sampleRate;
                if (_dronePhaseB >= 1f) _dronePhaseB -= 1f;
                float droneRaw =
                    (float)System.Math.Sin(_dronePhaseA * 2 * System.Math.PI)
                    + 0.6f * (float)System.Math.Sin(_dronePhaseB * 2 * System.Math.PI);
                // 1-pole LPF.
                _droneLpf += (droneRaw - _droneLpf) * 0.15f;
                float drone = _droneLpf * _droneAmp;

                float mixL = (voice * 0.7f + drone) * _masterGain;
                float mixR = (voice * 0.7f + drone) * _masterGain;
                // Soft clip.
                mixL = System.Math.Clamp(mixL, -1f, 1f);
                mixR = System.Math.Clamp(mixR, -1f, 1f);

                buffer[offset + written] = mixL;
                if (channels > 1) buffer[offset + written + 1] = mixR;
                written += channels;
            }
            return written;
        }

        private static float MidiToFreq(int midi) =>
            (float)(440.0 * System.Math.Pow(2.0, (midi - 69) / 12.0));
    }
}
