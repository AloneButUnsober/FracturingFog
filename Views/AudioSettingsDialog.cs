// DEPRECATED WinForms file — see CLAUDE.md. Hygiene warnings suppressed.
#pragma warning disable CS0169, CS0414, CS0649, CS8618, CS8602, CS8604, CS8625, CS8600, CS8601, CS0219
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using FracturingFog.Audio;

namespace FracturingFog.Views
{
    /// <summary>Settings window for the audio-reactive slideshow.</summary>
    public sealed class AudioSettingsDialog : Form
    {
        private readonly AudioSettings _settings;
        private readonly IBeatSource? _liveSource;

        private readonly ComboBox _sourceCombo;
        private readonly TextBox _filePathBox;
        private readonly Button _browseButton;
        private readonly TrackBar _sensitivitySlider;
        private readonly Label _sensitivityLabel;
        private readonly NumericUpDown _beatsPerTheme;
        private readonly NumericUpDown _beatsPerRegion;
        private readonly NumericUpDown _synthBpm;
        private readonly CheckBox _routeSynthChk;
        private readonly CheckBox _playSynthChk;
        private readonly Label _bpmLabel;
        private readonly Label _levelLabel;
        private readonly System.Windows.Forms.Timer _meterTimer;
        private readonly Button _okButton;
        private readonly Button _cancelButton;
        private readonly TrackBar[] _eqSliders = new TrackBar[5];
        private readonly Label[] _eqValueLabels = new Label[5];
        private readonly Button _eqResetButton;
        private readonly TrackBar _fadeFracSlider;
        private readonly Label _fadeFracLabel;
        private readonly Button? _slideshowToggleButton;
        private readonly Action? _slideshowToggle;
        private readonly Func<bool>? _slideshowIsRunning;
        private readonly System.Windows.Forms.Timer? _slideshowStateTimer;
        private static readonly string[] EqBandNames =
            { "Bass", "LowMid", "Mid", "HighMid", "High" };

        public AudioSettings Result { get; private set; }

        public AudioSettingsDialog(AudioSettings current, IBeatSource? liveSource)
            : this(current, liveSource, null, null) { }

        public AudioSettingsDialog(AudioSettings current, IBeatSource? liveSource,
                                   Action? slideshowToggle, Func<bool>? slideshowIsRunning)
        {
            _slideshowToggle = slideshowToggle;
            _slideshowIsRunning = slideshowIsRunning;
            _settings = Clone(current);
            _liveSource = liveSource;
            Result = _settings;

            Text = "Audio-Reactive Slideshow";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(440, 640);
            BackColor = Color.FromArgb(28, 28, 28);
            ForeColor = Color.WhiteSmoke;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            KeyPreview = true;

            int y = 12;
            int labelW = 110;
            int controlX = 130;
            int controlW = 280;

            AddLabel("Source:", 12, y);
            _sourceCombo = new ComboBox
            {
                Left = controlX, Top = y - 2, Width = controlW,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(55, 55, 55), ForeColor = Color.White,
            };
            _sourceCombo.Items.AddRange(new object[]
            {
                "System Loopback (what's currently playing)",
                "Audio File (MP3/WAV/FLAC/OGG)",
                "Microphone",
                "Fractal Synth (closed-loop)",
            });
            _sourceCombo.SelectedIndex = (int)_settings.Source;
            _sourceCombo.SelectedIndexChanged += (s, e) => UpdateSourceVisibility();
            Controls.Add(_sourceCombo);
            y += 32;

            AddLabel("File path:", 12, y);
            _filePathBox = new TextBox
            {
                Left = controlX, Top = y - 2, Width = controlW - 70,
                BackColor = Color.FromArgb(55, 55, 55), ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Text = _settings.FilePath ?? "",
            };
            Controls.Add(_filePathBox);
            _browseButton = new Button
            {
                Left = _filePathBox.Right + 4, Top = y - 3, Width = 62, Height = 24,
                Text = "Browse…", FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(50, 50, 50), ForeColor = Color.White,
            };
            _browseButton.Click += (s, e) => BrowseFile();
            Controls.Add(_browseButton);
            y += 32;

            AddLabel("Sensitivity:", 12, y);
            _sensitivitySlider = new TrackBar
            {
                Left = controlX, Top = y - 4, Width = controlW - 60,
                Minimum = 0, Maximum = 100,
                Value = (int)System.Math.Round(System.Math.Clamp(_settings.Sensitivity, 0f, 1f) * 100),
                TickFrequency = 10, TickStyle = TickStyle.None,
                BackColor = Color.FromArgb(28, 28, 28),
            };
            _sensitivitySlider.ValueChanged += (s, e) => _sensitivityLabel.Text = $"{_sensitivitySlider.Value}%";
            Controls.Add(_sensitivitySlider);
            _sensitivityLabel = new Label
            {
                Left = _sensitivitySlider.Right + 4, Top = y + 2, Width = 50, AutoSize = false,
                Text = $"{_sensitivitySlider.Value}%", ForeColor = Color.WhiteSmoke,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            Controls.Add(_sensitivityLabel);
            y += 44;

            AddLabel("Beats per theme:", 12, y);
            _beatsPerTheme = MakeSpin(controlX, y - 2, 1, 128, _settings.BeatsPerTheme);
            Controls.Add(_beatsPerTheme);
            y += 30;

            AddLabel("Beats per region:", 12, y);
            _beatsPerRegion = MakeSpin(controlX, y - 2, 1, 512, _settings.BeatsPerRegion);
            Controls.Add(_beatsPerRegion);
            y += 30;

            AddLabel("Synth BPM:", 12, y);
            _synthBpm = MakeSpin(controlX, y - 2, 30, 240, (int)System.Math.Round(_settings.SynthBpm));
            Controls.Add(_synthBpm);
            y += 36;

            _routeSynthChk = new CheckBox
            {
                Text = "Route fractal synth output through analyzer (closed-loop sync)",
                Left = 12, Top = y, AutoSize = true,
                ForeColor = Color.WhiteSmoke, BackColor = Color.Transparent,
                Checked = _settings.RouteSynthThroughAnalyzer,
            };
            Controls.Add(_routeSynthChk);
            y += 24;

            _playSynthChk = new CheckBox
            {
                Text = "Play fractal synth audio to speakers",
                Left = 12, Top = y, AutoSize = true,
                ForeColor = Color.WhiteSmoke, BackColor = Color.Transparent,
                Checked = _settings.PlaySynthOutput,
            };
            Controls.Add(_playSynthChk);
            y += 32;

            // ── Analysis EQ: per-band weighting for beat-trigger flux ─────────
            var eqHeader = new Label
            {
                Text = "Beat-Detector EQ (per-band flux weight)",
                Left = 12, Top = y, Width = 320, AutoSize = false,
                ForeColor = Color.FromArgb(180, 220, 180), BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            };
            Controls.Add(eqHeader);
            _eqResetButton = new Button
            {
                Text = "Reset", Left = ClientSize.Width - 84, Top = y - 2,
                Width = 70, Height = 22, FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(45, 55, 50), ForeColor = Color.White,
                Font = new Font("Segoe UI", 8f, FontStyle.Regular),
            };
            _eqResetButton.Click += (s, e) =>
            {
                for (int i = 0; i < 5; i++) _eqSliders[i].Value = 100;
            };
            Controls.Add(_eqResetButton);
            y += 24;

            for (int b = 0; b < 5; b++)
            {
                int initial = 100;
                if (_settings.BandWeights != null && b < _settings.BandWeights.Length)
                    initial = System.Math.Clamp(
                        (int)System.Math.Round(_settings.BandWeights[b] * 100f), 0, 200);

                var lbl = new Label
                {
                    Text = EqBandNames[b] + ":", Left = 12, Top = y + 4, Width = 64,
                    AutoSize = false, ForeColor = Color.WhiteSmoke,
                    BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft,
                };
                Controls.Add(lbl);

                var slider = new TrackBar
                {
                    Left = 78, Top = y - 2, Width = 290,
                    Minimum = 0, Maximum = 200, Value = initial,
                    TickFrequency = 50, TickStyle = TickStyle.BottomRight,
                    BackColor = Color.FromArgb(28, 28, 28),
                };
                int captureB = b;
                slider.ValueChanged += (s, e) =>
                    _eqValueLabels[captureB].Text = $"{_eqSliders[captureB].Value}%";
                Controls.Add(slider);
                _eqSliders[b] = slider;

                var valLbl = new Label
                {
                    Left = slider.Right + 2, Top = y + 4, Width = 50, AutoSize = false,
                    Text = $"{initial}%", ForeColor = Color.WhiteSmoke,
                    BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft,
                };
                Controls.Add(valLbl);
                _eqValueLabels[b] = valLbl;

                y += 26;
            }
            y += 6;

            // ── Cross-fade duration (fraction of one detected beat) ───────────
            AddLabel("Fade × beat:", 12, y);
            int fadeInit = System.Math.Clamp(
                (int)System.Math.Round(_settings.FadeBeatFraction * 100.0), 10, 200);
            _fadeFracSlider = new TrackBar
            {
                Left = 130, Top = y - 4, Width = 220,
                Minimum = 10, Maximum = 200, Value = fadeInit,
                TickFrequency = 25, TickStyle = TickStyle.BottomRight,
                BackColor = Color.FromArgb(28, 28, 28),
            };
            _fadeFracSlider.ValueChanged += (s, e) =>
                _fadeFracLabel.Text = $"{_fadeFracSlider.Value / 100.0:F2}× beat";
            Controls.Add(_fadeFracSlider);
            _fadeFracLabel = new Label
            {
                Left = _fadeFracSlider.Right + 2, Top = y + 2, Width = 80, AutoSize = false,
                Text = $"{fadeInit / 100.0:F2}× beat", ForeColor = Color.WhiteSmoke,
                BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft,
            };
            Controls.Add(_fadeFracLabel);
            y += 36;

            _bpmLabel = new Label
            {
                Left = 12, Top = y, Width = 200, AutoSize = false,
                Text = "BPM: —", ForeColor = Color.FromArgb(150, 220, 180),
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            };
            Controls.Add(_bpmLabel);
            _levelLabel = new Label
            {
                Left = 220, Top = y, Width = 200, AutoSize = false,
                Text = "Level: —", ForeColor = Color.FromArgb(220, 200, 150),
                Font = new Font("Segoe UI", 9f, FontStyle.Regular),
            };
            Controls.Add(_levelLabel);

            if (_slideshowToggle != null && _slideshowIsRunning != null)
            {
                _slideshowToggleButton = new Button
                {
                    Left = 12, Top = ClientSize.Height - 36,
                    Width = 130, Height = 26, FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(40, 55, 40), ForeColor = Color.White,
                    Text = _slideshowIsRunning() ? "■ Stop Slideshow" : "▶ Start Slideshow",
                };
                _slideshowToggleButton.FlatAppearance.BorderColor = Color.FromArgb(60, 100, 60);
                _slideshowToggleButton.Click += (s, e) =>
                {
                    try { _slideshowToggle(); } catch { }
                    RefreshSlideshowButton();
                };
                Controls.Add(_slideshowToggleButton);

                // Re-poll a few times to catch async start/stop reaching running state.
                _slideshowStateTimer = new System.Windows.Forms.Timer { Interval = 250 };
                _slideshowStateTimer.Tick += (s, e) => RefreshSlideshowButton();
                _slideshowStateTimer.Start();
                FormClosed += (s, e) => _slideshowStateTimer.Stop();
            }

            _okButton = new Button
            {
                Text = "OK", Left = ClientSize.Width - 180, Top = ClientSize.Height - 36,
                Width = 80, Height = 26, FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 70, 50), ForeColor = Color.White,
                DialogResult = DialogResult.OK,
            };
            _okButton.Click += (s, e) => { Commit(); DialogResult = DialogResult.OK; Close(); };
            Controls.Add(_okButton);
            AcceptButton = _okButton;

            _cancelButton = new Button
            {
                Text = "Cancel", Left = ClientSize.Width - 92, Top = ClientSize.Height - 36,
                Width = 80, Height = 26, FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(70, 40, 40), ForeColor = Color.White,
                DialogResult = DialogResult.Cancel,
            };
            _cancelButton.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(_cancelButton);
            CancelButton = _cancelButton;

            _meterTimer = new System.Windows.Forms.Timer { Interval = 100 };
            _meterTimer.Tick += (s, e) => UpdateMeters();
            _meterTimer.Start();

            FormClosed += (s, e) => _meterTimer.Stop();
            UpdateSourceVisibility();
        }

        private void AddLabel(string text, int x, int y)
        {
            Controls.Add(new Label
            {
                Text = text, Left = x, Top = y + 2, Width = 110, AutoSize = false,
                ForeColor = Color.WhiteSmoke, BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft,
            });
        }

        private static NumericUpDown MakeSpin(int x, int y, int min, int max, int val)
        {
            return new NumericUpDown
            {
                Left = x, Top = y, Width = 80,
                Minimum = min, Maximum = max, Value = System.Math.Clamp(val, min, max),
                BackColor = Color.FromArgb(55, 55, 55), ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
            };
        }

        private void BrowseFile()
        {
            using var dlg = new OpenFileDialog
            {
                Filter = "Audio files|*.mp3;*.wav;*.flac;*.ogg;*.aiff;*.wma|All files|*.*",
                Title = "Select an audio file",
            };
            if (!string.IsNullOrWhiteSpace(_filePathBox.Text) && File.Exists(_filePathBox.Text))
                dlg.FileName = _filePathBox.Text;
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _filePathBox.Text = dlg.FileName;
                _sourceCombo.SelectedIndex = (int)AudioSourceKind.File;
            }
        }

        private void UpdateSourceVisibility()
        {
            var src = (AudioSourceKind)_sourceCombo.SelectedIndex;
            bool fileMode = src == AudioSourceKind.File;
            _filePathBox.Enabled = fileMode;
            _browseButton.Enabled = fileMode;
            bool synthMode = src == AudioSourceKind.FractalSynth;
            _routeSynthChk.Enabled = synthMode;
            _playSynthChk.Enabled = synthMode;
        }

        private void UpdateMeters()
        {
            if (_liveSource == null || !_liveSource.IsActive)
            {
                _bpmLabel.Text = "BPM: —";
                _levelLabel.Text = "Level: —";
                return;
            }
            double bpm = _liveSource.EstimatedBpm;
            _bpmLabel.Text = bpm > 0 ? $"BPM: {bpm:F1}" : "BPM: (detecting…)";
            var e = _liveSource.CurrentEnergy;
            _levelLabel.Text = $"Bass {Bar(e.Bass)} Mid {Bar(e.Mid)} High {Bar(e.High)}";
        }

        private static string Bar(float v)
        {
            int n = System.Math.Clamp((int)System.Math.Round(v * 8), 0, 8);
            return new string('█', n) + new string('░', 8 - n);
        }

        private void Commit()
        {
            _settings.Source = (AudioSourceKind)_sourceCombo.SelectedIndex;
            _settings.FilePath = string.IsNullOrWhiteSpace(_filePathBox.Text) ? null : _filePathBox.Text;
            _settings.Sensitivity = _sensitivitySlider.Value / 100f;
            _settings.BeatsPerTheme = (int)_beatsPerTheme.Value;
            _settings.BeatsPerRegion = (int)_beatsPerRegion.Value;
            _settings.RouteSynthThroughAnalyzer = _routeSynthChk.Checked;
            _settings.PlaySynthOutput = _playSynthChk.Checked;
            _settings.SynthBpm = (double)_synthBpm.Value;
            var weights = new float[5];
            for (int i = 0; i < 5; i++) weights[i] = _eqSliders[i].Value / 100f;
            _settings.BandWeights = weights;
            _settings.FadeBeatFraction = System.Math.Clamp(_fadeFracSlider.Value / 100.0, 0.1, 2.0);
            Result = _settings;
        }

        private void RefreshSlideshowButton()
        {
            if (_slideshowToggleButton == null || _slideshowIsRunning == null) return;
            bool running = _slideshowIsRunning();
            string desired = running ? "■ Stop Slideshow" : "▶ Start Slideshow";
            if (_slideshowToggleButton.Text != desired)
                _slideshowToggleButton.Text = desired;
            var bg = running ? Color.FromArgb(70, 30, 30) : Color.FromArgb(40, 55, 40);
            var border = running ? Color.FromArgb(120, 50, 50) : Color.FromArgb(60, 100, 60);
            if (_slideshowToggleButton.BackColor != bg)
                _slideshowToggleButton.BackColor = bg;
            _slideshowToggleButton.FlatAppearance.BorderColor = border;
        }

        private static AudioSettings Clone(AudioSettings s) => new()
        {
            Enabled = s.Enabled,
            Source = s.Source,
            FilePath = s.FilePath,
            Sensitivity = s.Sensitivity,
            BeatsPerTheme = s.BeatsPerTheme,
            BeatsPerRegion = s.BeatsPerRegion,
            RouteSynthThroughAnalyzer = s.RouteSynthThroughAnalyzer,
            PlaySynthOutput = s.PlaySynthOutput,
            SynthBpm = s.SynthBpm,
            BandWeights = s.BandWeights != null
                ? (float[])s.BandWeights.Clone()
                : new[] { 1f, 1f, 1f, 1f, 1f },
            FadeBeatFraction = s.FadeBeatFraction,
        };
    }
}
