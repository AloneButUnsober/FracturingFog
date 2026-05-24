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
        private readonly CheckBox _routeSynthChk;
        private readonly CheckBox _playSynthChk;
        private readonly Label _bpmLabel;
        private readonly Label _levelLabel;
        private readonly System.Windows.Forms.Timer _meterTimer;
        private readonly Button _okButton;
        private readonly Button _cancelButton;

        public AudioSettings Result { get; private set; }

        public AudioSettingsDialog(AudioSettings current, IBeatSource? liveSource)
        {
            _settings = Clone(current);
            _liveSource = liveSource;
            Result = _settings;

            Text = "Audio-Reactive Slideshow";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(440, 360);
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
            y += 30;

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

            _okButton = new Button
            {
                Text = "OK", Left = ClientSize.Width - 180, Top = ClientSize.Height - 36,
                Width = 80, Height = 26, FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 70, 50), ForeColor = Color.White,
                DialogResult = DialogResult.OK,
            };
            _okButton.Click += (s, e) => Commit();
            Controls.Add(_okButton);
            AcceptButton = _okButton;

            _cancelButton = new Button
            {
                Text = "Cancel", Left = ClientSize.Width - 92, Top = ClientSize.Height - 36,
                Width = 80, Height = 26, FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(70, 40, 40), ForeColor = Color.White,
                DialogResult = DialogResult.Cancel,
            };
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
            Result = _settings;
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
        };
    }
}
