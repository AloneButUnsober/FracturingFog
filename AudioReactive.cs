using System;
using System.Threading;
using System.Windows.Forms;
using FracturingFog.Audio;

namespace FracturingFog
{
    /// <summary>
    /// MainForm partial that owns the AudioEngine and drives slideshow theme/region
    /// transitions on detected beats. UI marshalling: capture-thread events flip
    /// volatile skip flags consumed by the slideshow loop's existing SkippableDelay.
    /// </summary>
    public sealed partial class MainForm
    {
        private AudioEngine? _audioEngine;
        private AudioSettings _audioSettings = AudioSettingsStore.Load();
        private int _audioBeatsSinceTheme;
        private int _audioBeatsSinceRegion;
        private readonly object _audioStateLock = new();
        private FractalSynth? _fractalSynth;
        private System.Windows.Forms.Timer? _synthViewportTimer;
        private Views.AudioSettingsDialog? _audioDialog;

        public bool IsAudioReactiveActive =>
            _audioSettings.Enabled
            && _audioEngine != null
            && _audioEngine.IsRunning
            && _audioEngine.BeatSource.IsActive;

        /// <summary>Returns true if the slideshow should defer to beat events for advancement.</summary>
        private bool ShouldUseBeatDrivenTiming() => IsAudioReactiveActive && _slideshowRunning;

        /// <summary>Returns the analyzer's current BPM estimate, or 0 if unknown / inactive.</summary>
        public double GetReactiveBpm() => _audioEngine?.BeatSource.EstimatedBpm ?? 0;

        /// <summary>Master toggle from UI checkbox. Starts engine if a slideshow is running.</summary>
        public void SetAudioReactiveEnabled(bool enabled)
        {
            _audioSettings.Enabled = enabled;
            if (enabled)
            {
                EnsureAudioEngineStarted();
            }
            else
            {
                StopAudioEngine();
            }
            SetStatus(enabled
                ? $"Audio-reactive slideshow: ON ({_audioSettings.Source})"
                : "Audio-reactive slideshow: OFF");
            PersistAudioSettings();
        }

        /// <summary>Apply a new settings snapshot. Reconfigures engine if running.</summary>
        public void ApplyAudioSettings(AudioSettings updated)
        {
            _audioSettings.Source = updated.Source;
            _audioSettings.FilePath = updated.FilePath;
            _audioSettings.Sensitivity = updated.Sensitivity;
            _audioSettings.BeatsPerTheme = System.Math.Max(1, updated.BeatsPerTheme);
            _audioSettings.BeatsPerRegion = System.Math.Max(_audioSettings.BeatsPerTheme,
                                                            updated.BeatsPerRegion);
            _audioSettings.RouteSynthThroughAnalyzer = updated.RouteSynthThroughAnalyzer;
            _audioSettings.PlaySynthOutput = updated.PlaySynthOutput;
            _audioSettings.SynthBpm = System.Math.Clamp(updated.SynthBpm, 30, 240);
            _audioSettings.FadeBeatFraction = System.Math.Clamp(updated.FadeBeatFraction, 0.1, 2.0);
            if (updated.BandWeights != null && updated.BandWeights.Length >= 5)
            {
                _audioSettings.BandWeights = new[]
                {
                    updated.BandWeights[0], updated.BandWeights[1], updated.BandWeights[2],
                    updated.BandWeights[3], updated.BandWeights[4],
                };
            }
            _fractalSynth?.SetBpm(_audioSettings.SynthBpm);

            if (_audioEngine != null && _audioEngine.IsRunning)
            {
                if (_audioSettings.Source == AudioSourceKind.FractalSynth)
                {
                    EnsureFractalSynthAttached();
                }
                _audioEngine.Reconfigure(_audioSettings);
                if (_audioSettings.Source == AudioSourceKind.FractalSynth && _fractalSynth != null)
                {
                    _audioEngine.AttachSynth(_fractalSynth);
                }
            }
        }

        private void EnsureAudioEngineStarted()
        {
            if (_audioEngine == null)
            {
                _audioEngine = new AudioEngine(_audioSettings);
                _audioEngine.BeatSource.Beat += OnAudioBeat;
                _audioEngine.BeatSource.Downbeat += OnAudioDownbeat;
                _audioEngine.Stopped += OnAudioEngineStopped;
            }
            if (_audioSettings.Source == AudioSourceKind.FractalSynth)
            {
                EnsureFractalSynthAttached();
            }
            try
            {
                if (!_audioEngine.IsRunning) _audioEngine.Start();
                lock (_audioStateLock)
                {
                    _audioBeatsSinceTheme = 0;
                    _audioBeatsSinceRegion = 0;
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Audio start failed: {ex.Message}");
            }
        }

        private void EnsureFractalSynthAttached()
        {
            if (_fractalSynth == null)
            {
                _fractalSynth = new FractalSynth(MandelbrotIterationProbe);
                _fractalSynth.SetBpm(_audioSettings.SynthBpm);
                PushSynthViewport();
                _synthViewportTimer = new System.Windows.Forms.Timer { Interval = 500 };
                _synthViewportTimer.Tick += (s, e) => PushSynthViewport();
                _synthViewportTimer.Start();
            }
            _audioEngine?.AttachSynth(_fractalSynth);
        }

        private void PushSynthViewport()
        {
            if (_fractalSynth == null || _calculator == null) return;
            _fractalSynth.UpdateViewport(_calculator.CenterX, _calculator.CenterY,
                                         _calculator.Zoom, _calculator.MaxIterations);
        }

        /// <summary>Simple cardioid-aware Mandelbrot escape iteration for the synth probe.</summary>
        private static int MandelbrotIterationProbe(double cx, double cy, int maxIter)
        {
            // Cardioid / period-2 bulb skip (cheap).
            double q = (cx - 0.25) * (cx - 0.25) + cy * cy;
            if (q * (q + (cx - 0.25)) < 0.25 * cy * cy) return maxIter;
            if ((cx + 1) * (cx + 1) + cy * cy < 0.0625) return maxIter;

            double x = 0, y = 0;
            int i = 0;
            while (i < maxIter && x * x + y * y < 4.0)
            {
                double xn = x * x - y * y + cx;
                y = 2 * x * y + cy;
                x = xn;
                i++;
            }
            return i;
        }

        private void StopAudioEngine()
        {
            if (_audioEngine != null && _audioEngine.IsRunning)
            {
                try { _audioEngine.Stop(); } catch { }
            }
        }

        private void DisposeAudioEngine()
        {
            if (_synthViewportTimer != null)
            {
                _synthViewportTimer.Stop();
                _synthViewportTimer.Dispose();
                _synthViewportTimer = null;
            }
            _fractalSynth = null;
            if (_audioEngine != null)
            {
                _audioEngine.BeatSource.Beat -= OnAudioBeat;
                _audioEngine.BeatSource.Downbeat -= OnAudioDownbeat;
                _audioEngine.Stopped -= OnAudioEngineStopped;
                try { _audioEngine.Dispose(); } catch { }
                _audioEngine = null;
            }
        }

        private void OnAudioEngineStopped(object? sender, EventArgs e)
        {
            // File playback ended or device disconnected. Surface status, keep
            // checkbox state so user can re-trigger.
            if (IsHandleCreated && !_disposed)
            {
                BeginInvoke(() => SetStatus("Audio source ended."));
            }
        }

        private void OnAudioBeat(object? sender, BeatEventArgs e)
        {
            if (!ShouldUseBeatDrivenTiming()) return;
            int beatsTheme, beatsRegion;
            lock (_audioStateLock)
            {
                _audioBeatsSinceTheme++;
                _audioBeatsSinceRegion++;
                beatsTheme = _audioBeatsSinceTheme;
                beatsRegion = _audioBeatsSinceRegion;
            }
            // Region change wins over theme change.
            if (beatsRegion >= _audioSettings.BeatsPerRegion)
            {
                lock (_audioStateLock)
                {
                    _audioBeatsSinceRegion = 0;
                    _audioBeatsSinceTheme = 0;
                }
                lock (_slideshowLock) _slideshowSkipRegion = true;
                return;
            }
            if (beatsTheme >= _audioSettings.BeatsPerTheme)
            {
                lock (_audioStateLock) _audioBeatsSinceTheme = 0;
                lock (_slideshowLock) _slideshowSkipTheme = true;
            }
        }

        private void OnAudioDownbeat(object? sender, BeatEventArgs e)
        {
            // Reserved for future use (e.g. pulse fades). Beat handler covers the
            // primary advancement logic to keep timing deterministic when the
            // downbeat detector misses a phrase boundary.
        }

        /// <summary>
        /// Called by Slideshow.cs Start/Stop hooks so the engine lifecycle tracks
        /// the slideshow when the user has enabled audio.
        /// </summary>
        private void NotifySlideshowStarted()
        {
            if (_audioSettings.Enabled) EnsureAudioEngineStarted();
        }

        private void NotifySlideshowStopped()
        {
            // Leave engine running if user wants persistent BPM display; otherwise
            // stop to release loopback device. Default policy: stop with slideshow.
            StopAudioEngine();
        }

        private void ShowAudioSettingsDialog()
        {
            if (_audioDialog != null && !_audioDialog.IsDisposed)
            {
                if (_audioDialog.WindowState == FormWindowState.Minimized)
                    _audioDialog.WindowState = FormWindowState.Normal;
                _audioDialog.BringToFront();
                _audioDialog.Activate();
                return;
            }

            var dlg = new Views.AudioSettingsDialog(
                _audioSettings,
                _audioEngine?.BeatSource,
                ToggleSlideshowFromAudioDialog,
                () => _slideshowRunning);
            _audioDialog = dlg;
            dlg.FormClosed += (s, e) =>
            {
                try
                {
                    if (dlg.DialogResult == DialogResult.OK)
                    {
                        ApplyAudioSettings(dlg.Result);
                        AudioSettingsStore.Save(_audioSettings);
                    }
                }
                finally
                {
                    if (ReferenceEquals(_audioDialog, dlg)) _audioDialog = null;
                    dlg.Dispose();
                }
            };
            dlg.Show(this);
        }

        private void ToggleSlideshowFromAudioDialog()
        {
            if (_slideshowRunning) StopSlideshow();
            else StartSlideshow();
        }

        /// <summary>Restore Enabled state + sync UI checkbox after settings load.</summary>
        public void InitializeAudioFromDisk()
        {
            // Reflect loaded Enabled state in the floating menu checkbox without
            // re-firing CheckedChanged in a way that double-starts the engine.
            _floatingMenu.SetAudioReactiveChecked(_audioSettings.Enabled);
            if (_audioSettings.Enabled) EnsureAudioEngineStarted();
        }

        /// <summary>Persist whenever Enabled toggles or settings change.</summary>
        private void PersistAudioSettings() => AudioSettingsStore.Save(_audioSettings);
    }
}
