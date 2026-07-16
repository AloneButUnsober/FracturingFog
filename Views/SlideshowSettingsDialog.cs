// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Drawing;
using System.Windows.Forms;
using FracturingFog.Models;

namespace FracturingFog.Views
{
    /// <summary>Settings window for the slideshow (timing, extreme regions, audio toggle).</summary>
    public sealed class SlideshowSettingsDialog : Form
    {
        private readonly SlideshowSettings _settings;
        private readonly Action? _showAudioDialog;

        private readonly CheckBox _chkAudioReactive;
        private readonly CheckBox _chkUseExtremeRegions;
        private readonly Button _audioButton;
        private readonly NumericUpDown _totalDisplaySec;
        private readonly NumericUpDown _themeFadeMs;
        private readonly NumericUpDown _regionFadeMs;
        private readonly NumericUpDown _fadeSteps;
        private readonly Label _timingGroupNote;
        private readonly Button _okButton;
        private readonly Button _cancelButton;

        public SlideshowSettings Result { get; private set; }
        public bool AudioReactiveResult { get; private set; }

        public SlideshowSettingsDialog(SlideshowSettings current, bool audioReactive, Action? showAudioDialog)
        {
            _settings = Clone(current);
            _showAudioDialog = showAudioDialog;
            Result = _settings;
            AudioReactiveResult = audioReactive;

            Text = "Slideshow Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(420, 360);
            BackColor = Color.FromArgb(28, 28, 28);
            ForeColor = Color.WhiteSmoke;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            KeyPreview = true;

            int y = 14;
            int labelX = 12;
            int controlX = 200;

            // ── Audio-Reactive switch + Audio… button ─────────────────────────
            _chkAudioReactive = new CheckBox
            {
                Text = "Audio-Reactive Slideshow",
                Left = labelX, Top = y, AutoSize = true,
                Appearance = Appearance.Button,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 55, 50),
                ForeColor = Color.FromArgb(200, 230, 210),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Checked = audioReactive,
                MinimumSize = new Size(180, 28),
                TextAlign = ContentAlignment.MiddleCenter,
                Padding = new Padding(8, 0, 8, 0),
            };
            _chkAudioReactive.FlatAppearance.BorderColor = Color.FromArgb(60, 110, 90);
            _chkAudioReactive.CheckedChanged += (s, e) => UpdateTimingEnable();
            Controls.Add(_chkAudioReactive);

            _audioButton = new Button
            {
                Text = "Audio…",
                Left = _chkAudioReactive.Left + _chkAudioReactive.PreferredSize.Width + 12,
                Top = y, Width = 80, Height = 28,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(30, 50, 45),
                ForeColor = Color.FromArgb(180, 220, 200),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Cursor = Cursors.Hand,
            };
            _audioButton.FlatAppearance.BorderColor = Color.FromArgb(60, 110, 90);
            _audioButton.Click += (s, e) => _showAudioDialog?.Invoke();
            Controls.Add(_audioButton);
            y += 40;

            // ── Use Extreme Regions ───────────────────────────────────────────
            _chkUseExtremeRegions = new CheckBox
            {
                Text = "Use Extreme Regions",
                Left = labelX, Top = y, AutoSize = true,
                ForeColor = Color.FromArgb(220, 160, 160),
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Checked = _settings.UseExtremeRegions,
            };
            Controls.Add(_chkUseExtremeRegions);
            y += 32;

            // ── Timing group header ───────────────────────────────────────────
            var header = new Label
            {
                Text = "Timing (used when Audio-Reactive is OFF)",
                Left = labelX, Top = y, Width = 360, AutoSize = false,
                ForeColor = Color.FromArgb(180, 220, 180),
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            };
            Controls.Add(header);
            y += 22;

            AddLabel("Total display time per region (s):", labelX, y);
            _totalDisplaySec = MakeSpin(controlX, y - 2, 3, 600,
                System.Math.Clamp(_settings.TotalDisplayMsPerRegion / 1000, 3, 600));
            Controls.Add(_totalDisplaySec);
            y += 30;

            AddLabel("Color theme fade (ms):", labelX, y);
            _themeFadeMs = MakeSpin(controlX, y - 2, 100, 20_000, _settings.ColorThemeFadeMs);
            Controls.Add(_themeFadeMs);
            y += 30;

            AddLabel("Region fade (ms):", labelX, y);
            _regionFadeMs = MakeSpin(controlX, y - 2, 100, 20_000, _settings.RegionFadeMs);
            Controls.Add(_regionFadeMs);
            y += 30;

            AddLabel("Fade steps:", labelX, y);
            _fadeSteps = MakeSpin(controlX, y - 2, 2, 200, _settings.FadeSteps);
            Controls.Add(_fadeSteps);
            y += 36;

            _timingGroupNote = new Label
            {
                Text = "(Disabled while Audio-Reactive is on — beats drive timing.)",
                Left = labelX, Top = y, Width = 380, AutoSize = false,
                ForeColor = Color.FromArgb(150, 150, 150),
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            };
            Controls.Add(_timingGroupNote);

            // ── OK / Cancel ───────────────────────────────────────────────────
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

            UpdateTimingEnable();
        }

        private void UpdateTimingEnable()
        {
            bool audio = _chkAudioReactive.Checked;
            _totalDisplaySec.Enabled = !audio;
            _themeFadeMs.Enabled = !audio;
            _regionFadeMs.Enabled = !audio;
            _fadeSteps.Enabled = !audio;
            _timingGroupNote.Visible = audio;
            _chkAudioReactive.BackColor = audio
                ? Color.FromArgb(60, 100, 80)
                : Color.FromArgb(40, 55, 50);
        }

        private void AddLabel(string text, int x, int y)
        {
            Controls.Add(new Label
            {
                Text = text, Left = x, Top = y + 2, Width = 180, AutoSize = false,
                ForeColor = Color.WhiteSmoke, BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft,
            });
        }

        private static NumericUpDown MakeSpin(int x, int y, int min, int max, int val)
        {
            return new NumericUpDown
            {
                Left = x, Top = y, Width = 100,
                Minimum = min, Maximum = max,
                Value = System.Math.Clamp(val, min, max),
                BackColor = Color.FromArgb(55, 55, 55), ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Increment = 1,
            };
        }

        private void Commit()
        {
            AudioReactiveResult = _chkAudioReactive.Checked;
            _settings.UseExtremeRegions = _chkUseExtremeRegions.Checked;
            _settings.TotalDisplayMsPerRegion = (int)_totalDisplaySec.Value * 1000;
            _settings.ColorThemeFadeMs = (int)_themeFadeMs.Value;
            _settings.RegionFadeMs = (int)_regionFadeMs.Value;
            _settings.FadeSteps = (int)_fadeSteps.Value;
            Result = _settings;
        }

        private static SlideshowSettings Clone(SlideshowSettings s) => new()
        {
            UseExtremeRegions = s.UseExtremeRegions,
            TotalDisplayMsPerRegion = s.TotalDisplayMsPerRegion,
            ColorThemeFadeMs = s.ColorThemeFadeMs,
            RegionFadeMs = s.RegionFadeMs,
            FadeSteps = s.FadeSteps,
        };
    }
}
