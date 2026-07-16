// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

using FracturingFog.Models;

namespace FracturingFog.Views
{


    // ─────────────────────────────────────────────────────────────────────────────
    // Minimal text-input dialog (used by Save View)
    // ─────────────────────────────────────────────────────────────────────────────

    public sealed class InputDialog : Form
    {
        public string Input => _tx.Text;
        private readonly TextBox _tx;

        public InputDialog(string title, string prompt, string? initialValue = null)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ClientSize = new Size(360, 100);
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.FromArgb(35, 35, 35);
            TopMost = true;

            Controls.Add(new Label
            {
                Text = prompt,
                Left = 12,
                Top = 14,
                AutoSize = true,
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 9f)
            });

            _tx = new TextBox
            {
                Left = 12,
                Top = 36,
                Width = 336,
                BackColor = Color.FromArgb(50, 50, 50),
                ForeColor = Color.White,
                Font = new Font("Consolas", 10f),
                BorderStyle = BorderStyle.FixedSingle
            };
            if (!string.IsNullOrEmpty(initialValue))
            {
                _tx.Text = initialValue;
                Shown += (_, _) =>
                {
                    _tx.Focus();
                    _tx.SelectionStart = _tx.Text.Length;
                    _tx.SelectionLength = 0;
                };
            }
            Controls.Add(_tx);

            var ok = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Left = 196,
                Top = 66,
                Width = 72,
                Height = 26,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Left = 276,
                Top = 66,
                Width = 72,
                Height = 26,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(ok);
            Controls.Add(cancel);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Minimal text-input dialog (used by Save View)
    // ─────────────────────────────────────────────────────────────────────────────

    public sealed class PosterDialog : Form
    {
        public string WidthInput => _widthTx.Text;
        public string HeightInput => _heightTx.Text;

        public bool IsPortrait => _portraitCB.Checked;

        public bool RotateImage => _portraitCB.Checked;

        private readonly Label _widthLabel;
        private readonly TextBox _widthTx;

        private readonly Label _heightLabel;
        private readonly TextBox _heightTx;

        private readonly Label _posterWLabel;
        private readonly Label _posterHLabel;
        private readonly TextBox _postWTx;
        private readonly TextBox _postHTx;

        private readonly CheckBox _portraitCB;
        private readonly CheckBox _lowDefCB;
        private readonly CheckBox _medDefCB;
        private readonly CheckBox _highDefCB;

        public PosterDialog()
        {
            Text = "Poster Print";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ClientSize = new Size(340, 180);
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.FromArgb(35, 35, 35);
            TopMost = true;

            _posterHLabel = new Label
            {
                Text = "Poster Height (inches):",
                Left = 4,
                Top = 7,
                Width = 130,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 9f)
            };

            _postHTx = new TextBox
            {
                Left = 136,
                Top = 10,
                Width = 50,
                BackColor = Color.FromArgb(50, 50, 50),
                ForeColor = Color.White,
                Font = new Font("Consolas", 10f),
                BorderStyle = BorderStyle.FixedSingle
            };
            _postHTx.TextChanged += (s, e) => CalculatePixelDimensions();

            Controls.Add(_posterHLabel);
            Controls.Add(_postHTx);

            _postWTx = new TextBox
            {
                Left = 136,
                Top = 36,
                Width = 50,
                BackColor = Color.FromArgb(50, 50, 50),
                ForeColor = Color.White,
                Font = new Font("Consolas", 10f),
                BorderStyle = BorderStyle.FixedSingle
            };
            _postWTx.TextChanged += (s, e) => CalculatePixelDimensions();

            _posterWLabel = new Label
            {
                Text = "Poster Width (inches):",
                Left = 4,
                Top = 38,
                Width = 130,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 9f)
            };

            Controls.Add(_posterWLabel);
            Controls.Add(_postWTx);

            _widthLabel = new Label
            {
                Text = "Pixel Width:",
                Left = 4,
                Top = 70,
                Width = 130,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 9f)
            };
            Controls.Add(_widthLabel);

            _widthTx = new TextBox
            {
                Left = 136,
                Top = 71,
                Width = 50,
                BackColor = Color.FromArgb(50, 50, 50),
                ForeColor = Color.White,
                Font = new Font("Consolas", 10f),
                BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(_widthTx);

            _heightLabel = new Label
            {
                Text = "Pixel Height:",
                Left = 4,
                Top = 98,
                Width = 130,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 9f)
            };
            Controls.Add(_heightLabel);

            _heightTx = new TextBox
            {
                Left = 136,
                Top = 97,
                Width = 50,
                BackColor = Color.FromArgb(50, 50, 50),
                ForeColor = Color.White,
                Font = new Font("Consolas", 10f),
                BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(_heightTx);

            _portraitCB = new CheckBox
            {
                Text = "Portrait Orientation",
                Left = 203,
                Top = 10,
                Width = 200,
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 9f),
                Checked = true
            };
            Controls.Add(_portraitCB);

            _lowDefCB = new CheckBox
            {
                Text = "Low Def (150 DPI)",
                Left = 203,
                Top = 36,
                Width = 120,
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 9f),
                Checked = false
            };
            _lowDefCB.CheckedChanged += (s, e) =>
            {
                CalculatePixelDimensions();
                if (_lowDefCB.Checked)
                {
                    _medDefCB?.Checked = false;
                    _highDefCB?.Checked = false;
                }
            };
            Controls.Add(_lowDefCB);

            _medDefCB = new CheckBox
            {
                Text = "Med Def (300 DPI)",
                Left = 203,
                Top = 60,
                Width = 180,
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 9f),
                Checked = true
            };
            _medDefCB.CheckedChanged += (s, e) =>
            {
                CalculatePixelDimensions();
                if (_medDefCB.Checked)
                {
                    _lowDefCB?.Checked = false;
                    _highDefCB?.Checked = false;
                }
            };
            Controls.Add(_medDefCB);

            _highDefCB = new CheckBox
            {
                Text = "High Def (600 DPI)",
                Left = 203,
                Top = 84,
                Width = 180,
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 9f),
                Checked = false
            };
            _highDefCB.CheckedChanged += (s, e) =>
            {
                CalculatePixelDimensions();
                if (_highDefCB.Checked)
                {
                    _lowDefCB?.Checked = false;
                    _medDefCB?.Checked = false;
                }
            };
            Controls.Add(_highDefCB);

            ToolTip _portraitTip = new ToolTip();
            _portraitTip.SetToolTip(_portraitCB, "If checked, the output image will be formatted for portrait-oriented paper.  If unchecked, the image will be formatted for landscape-oriented paper.  When printing a poster taller than it is wide, select portrait orientation for the best results.");

            var ok = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Left = 82,
                Top = 138,
                Width = 72,
                Height = 26,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Left = 162,
                Top = 138,
                Width = 72,
                Height = 26,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(ok);
            Controls.Add(cancel);
        }

        private void CalculatePixelDimensions()
        {
            int posterHeightInches = 0;
            int posterWidthInches = 0;

            int.TryParse(_postHTx.Text, out posterHeightInches);
            int.TryParse(_postWTx.Text, out posterWidthInches);
            if (posterHeightInches < 0 || posterWidthInches < 0)
            {
                MessageBox.Show("Please enter valid integer values for poster width and height in inches.", "Invalid Input", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            int dpi = _lowDefCB.Checked ? 150 : _highDefCB.Checked ? 600 : 300;
            int pixelWidth = posterWidthInches * dpi;
            int pixelHeight = posterHeightInches * dpi;
            _widthTx.Text = pixelWidth.ToString();
            _heightTx.Text = pixelHeight.ToString();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Video zoom dialog — select target (region or manual coords + zoom) and speed
    // ─────────────────────────────────────────────────────────────────────────────

    public sealed class VideoDialog : Form
    {
        private readonly ComboBox _regionCombo;
        private readonly TextBox _txCX;
        private readonly TextBox _txCY;
        private readonly TextBox _txZoom;
        private readonly RadioButton _rbSlow;
        private readonly RadioButton _rbMed;
        private readonly RadioButton _rbFast;
        private readonly RadioButton _rbCustom;
        private readonly TextBox _txCustomSecs;
        private readonly Label _capWarn;
        private readonly CheckBox _chkConstantRate;
        private readonly CheckBox _chkReverse;
        private readonly CheckBox _chkSaveVideo;
        private readonly CheckBox _chkSaveLossless;
        private readonly ComboBox _losslessEncodeCombo;
        private readonly TrackBar _tbTaaSmoothing;
        private readonly Label _lblTaaSmoothingValue;
        private readonly CheckBox _chkBandDither;
        private readonly TrackBar _tbBandDitherStrength;
        private readonly Label _lblBandDitherValue;

        // Custom duration bounds (seconds).
        private const double CustomSecsMin = 0.5;
        private const double CustomSecsMax = 300.0;

        /// <summary>
        /// True when user clicked the Slideshow button instead of Start.
        /// Caller should ignore target inputs and launch the video slideshow.
        /// </summary>
        public bool IsSlideshow { get; private set; }

        /// <summary>
        /// Per-leg duration override for the slideshow.  Set only when the user
        /// chose the Custom radio with a valid value at the time the Slideshow
        /// button was clicked; null means "use the slideshow's default duration".
        /// </summary>
        public double? SlideshowSecondsOverride { get; private set; }

        /// <summary>
        /// True when "Constant Rate" was checked at click time.  In this mode
        /// the slideshow holds the log-zoom rate constant across regions and
        /// scales per-leg duration with region depth; the user-supplied time
        /// acts as the *minimum* duration applied to the shallowest region.
        /// </summary>
        public bool IsConstantRate { get; private set; }

        /// <summary>
        /// True when "Reverse zoom" was checked at click time.  In reverse mode
        /// the video starts at the user-supplied target coordinates/zoom and
        /// animates back to the classic view, instead of zooming from classic
        /// into the target.  Applies to both single-shot Start and Slideshow.
        /// </summary>
        public bool IsReverse { get; private set; }

        /// <summary>
        /// True when "Save video" was checked at click time. Ignored when the
        /// user clicks Slideshow (per-leg recording isn't supported).
        /// </summary>
        public bool IsSaveVideo { get; private set; }

        /// <summary>
        /// True when "Save lossless (PNG sequence)" was checked at click time.
        /// MP4 + lossless can both be on simultaneously (parallel capture).
        /// </summary>
        public bool IsSaveLossless { get; private set; }

        /// <summary>
        /// Post-capture encoding choice for the PNG sequence. "None" keeps the
        /// PNG folder as-is; the others invoke a bundled/PATH ffmpeg.
        /// </summary>
        public enum LosslessEncodeChoice { None, LosslessH264Mp4, Ffv1Mkv, HighQualityH264Mp4 }
        public LosslessEncodeChoice LosslessEncode { get; private set; } = LosslessEncodeChoice.None;

        /// <summary>
        /// Per-region iteration count captured when the user picks a region
        /// from the combo. Zero when the dialog is in manual-entry mode (no
        /// region selected) — caller should fall back to the quality preset's
        /// auto-computed iteration count. Deep regions ship with a higher
        /// recommended iter cap than the preset formula would produce, and
        /// honouring this value avoids in-set black at the target frame.
        /// </summary>
        public int TargetIterations { get; private set; }

        /// <summary>
        /// Temporal smoothing strength as a 0..100 percentage. Maps inside the
        /// video renderer to the prev-frame blend weight; 0 disables the blend
        /// entirely. Higher values hide per-pixel crawl in densely banded
        /// regions at the cost of mild motion ghosting.
        /// </summary>
        public int TaaSmoothing { get; private set; } = 55;

        /// <summary>
        /// True when the user enabled band-edge dither for the video. Adds a
        /// stable spatial noise pattern to the smooth-iteration value before
        /// palette lookup so band boundaries blur slightly and the per-frame
        /// shift across them becomes less visible.
        /// </summary>
        public bool BandDither { get; private set; }

        /// <summary>
        /// Band-dither magnitude as a 0..100 percentage. Maps inside the video
        /// renderer to a smooth-iteration jitter of up to ~1 iteration. Ignored
        /// when <see cref="BandDither"/> is false.
        /// </summary>
        public int BandDitherStrength { get; private set; } = 25;

        // Full QD precision limbs for the chosen target. Hi mirrors the textbox
        // contents; Lo/X2/X3 carry the extended-precision tail stored on regions
        // and are needed to land on the correct pixel at deep zoom (~1e15+).
        private double _targetCXLo, _targetCX2, _targetCX3;
        private double _targetCYLo, _targetCY2, _targetCY3;

        public VideoDialog(double currentCX, double currentCY, double currentZoom)
        {
            Text = "Video Zoom";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ClientSize = new Size(420, 562);
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.FromArgb(35, 35, 35);
            TopMost = true;

            Font lblFont = new("Segoe UI", 9f);
            Color lblColor = Color.LightGray;
            Color txBack = Color.FromArgb(50, 50, 50);
            Color txFore = Color.White;
            Font txFont = new("Consolas", 9.5f);

            Label MkLabel(string text, int left, int top, int width = 90) => new()
            {
                Text = text,
                Left = left,
                Top = top,
                Width = width,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = lblColor,
                Font = lblFont,
                BackColor = Color.Transparent
            };
            TextBox MkTx(int left, int top, int width = 290) => new()
            {
                Left = left,
                Top = top,
                Width = width,
                BackColor = txBack,
                ForeColor = txFore,
                Font = txFont,
                BorderStyle = BorderStyle.FixedSingle
            };

            // ── Region selector (pre-fills target boxes) ──────────────────────
            Controls.Add(MkLabel("Region:", 8, 14, 90));

            _regionCombo = new ComboBox
            {
                Left = 104,
                Top = 11,
                Width = 296,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(55, 55, 55),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            FormHelpers.RebuildRegionCombo(_regionCombo, OnRegionPicked);
            Controls.Add(_regionCombo);

            // ── Target coordinates ────────────────────────────────────────────
            // CX/CY are displayed as a single decimal/scientific string that
            // collapses all four QD limbs into one number. Paste accepts either
            // the single-string form or the legacy pipe-delimited "Hi|Lo|X2|X3".
            Controls.Add(MkLabel("Target CX:", 8, 50));
            _txCX = MkTx(104, 47);
            _txCX.Text = FormHelpers.FormatCoordSingle(currentCX, 0, 0, 0);
            Controls.Add(_txCX);

            Controls.Add(MkLabel("Target CY:", 8, 80));
            _txCY = MkTx(104, 77);
            _txCY.Text = FormHelpers.FormatCoordSingle(currentCY, 0, 0, 0);
            Controls.Add(_txCY);

            Controls.Add(MkLabel("Target Zoom:", 8, 110));
            _txZoom = MkTx(104, 107);
            _txZoom.Text = Math.Max(currentZoom * 10.0, 100.0).ToString("G6", CultureInfo.InvariantCulture);
            Controls.Add(_txZoom);

            double ultraMax = QualityPreset.Ultra.ZoomMax;
            _capWarn = new Label
            {
                Text = $"Max target zoom: {ultraMax:G3} (Ultra). Deeper values are clamped.",
                Left = 104,
                Top = 132,
                Width = 296,
                ForeColor = Color.FromArgb(180, 160, 100),
                Font = new Font("Segoe UI", 8f, FontStyle.Italic),
                BackColor = Color.Transparent
            };
            Controls.Add(_capWarn);

            // ── Speed radios ──────────────────────────────────────────────────
            var speedBox = new GroupBox
            {
                Text = "Zoom Speed",
                Left = 8,
                Top = 158,
                Width = 392,
                Height = 96,
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            Controls.Add(speedBox);

            _rbSlow = new RadioButton { Text = "Slow (15 s)", Left = 16, Top = 26, Width = 110, ForeColor = lblColor, Font = lblFont, BackColor = Color.Transparent };
            _rbMed = new RadioButton { Text = "Medium (8 s)", Left = 140, Top = 26, Width = 120, ForeColor = lblColor, Font = lblFont, BackColor = Color.Transparent, Checked = true };
            _rbFast = new RadioButton { Text = "Fast (4 s)", Left = 270, Top = 26, Width = 110, ForeColor = lblColor, Font = lblFont, BackColor = Color.Transparent };
            speedBox.Controls.Add(_rbSlow);
            speedBox.Controls.Add(_rbMed);
            speedBox.Controls.Add(_rbFast);

            _rbCustom = new RadioButton { Text = "Custom:", Left = 16, Top = 56, Width = 78, ForeColor = lblColor, Font = lblFont, BackColor = Color.Transparent };
            _txCustomSecs = new TextBox
            {
                Left = 96,
                Top = 54,
                Width = 70,
                BackColor = txBack,
                ForeColor = txFore,
                Font = txFont,
                BorderStyle = BorderStyle.FixedSingle,
                Text = "30"
            };
            // Auto-select Custom radio when user types in the box.
            _txCustomSecs.Enter += (_, _) => _rbCustom.Checked = true;
            _txCustomSecs.TextChanged += (_, _) => { if (_txCustomSecs.Focused) _rbCustom.Checked = true; };
            var customHint = new Label
            {
                Text = $"seconds (0.5 – {CustomSecsMax:F0})",
                Left = 172,
                Top = 56,
                Width = 210,
                ForeColor = lblColor,
                Font = lblFont,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft
            };
            speedBox.Controls.Add(_rbCustom);
            speedBox.Controls.Add(_txCustomSecs);
            speedBox.Controls.Add(customHint);

            // ── Constant Rate (slideshow-only) ────────────────────────────────
            _chkConstantRate = new CheckBox
            {
                Text = "Constant Rate (slideshow): scale duration by depth",
                Left = 12,
                Top = 260,
                Width = 396,
                ForeColor = lblColor,
                Font = lblFont,
                BackColor = Color.Transparent,
                Checked = false
            };
            ToolTip ttConstRate = new ToolTip();
            ttConstRate.SetToolTip(_chkConstantRate,
                "Slideshow only.\n" +
                "Off: every video uses the same duration; deep zooms appear fast,\n" +
                "shallow zooms appear slow.\n" +
                "On: the log-zoom rate is held constant across regions.  The chosen\n" +
                "duration is the minimum (applied to the shallowest region); deeper\n" +
                "regions take proportionally longer so the visual zoom speed matches.");
            Controls.Add(_chkConstantRate);

            // ── Save Video ────────────────────────────────────────────────────
            _chkSaveVideo = new CheckBox
            {
                Text = "Save video as MP4 (single-shot only — ignored for slideshow)",
                Left = 12,
                Top = 286,
                Width = 396,
                ForeColor = lblColor,
                Font = lblFont,
                BackColor = Color.Transparent,
                Checked = false
            };
            ToolTip ttSaveVideo = new ToolTip();
            ttSaveVideo.SetToolTip(_chkSaveVideo,
                "Records the zoom animation while it plays. When the zoom finishes\n" +
                "you'll be prompted for a destination MP4 file.\n" +
                "Has no effect when launching a slideshow.");
            Controls.Add(_chkSaveVideo);

            // ── Save Lossless (PNG sequence + optional ffmpeg) ─────────────────
            _chkSaveLossless = new CheckBox
            {
                Text = "Save lossless (PNG sequence — single-shot only)",
                Left = 12,
                Top = 312,
                Width = 396,
                ForeColor = lblColor,
                Font = lblFont,
                BackColor = Color.Transparent,
                Checked = false
            };
            ToolTip ttSaveLossless = new ToolTip();
            ttSaveLossless.SetToolTip(_chkSaveLossless,
                "Captures every frame as a numbered PNG into a folder. Truly\n" +
                "lossless, large on disk. After the zoom finishes you'll pick a\n" +
                "destination folder, and (if ffmpeg is available) optionally\n" +
                "post-encode to a lossless video file.\n" +
                "Has no effect when launching a slideshow.");
            Controls.Add(_chkSaveLossless);

            Controls.Add(MkLabel("Post-encode:", 8, 343, 90));
            _losslessEncodeCombo = new ComboBox
            {
                Left = 104,
                Top = 340,
                Width = 296,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(55, 55, 55),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f),
                FlatStyle = FlatStyle.Flat,
                Enabled = false,
            };
            _losslessEncodeCombo.Items.Add("Keep PNG sequence only");
            bool ffmpegHere = FracturingFog.FfmpegEncoder.IsAvailable();
            if (ffmpegHere)
            {
                _losslessEncodeCombo.Items.Add("Lossless H.264 (CRF 0) → .mp4");
                _losslessEncodeCombo.Items.Add("FFV1 → .mkv");
                _losslessEncodeCombo.Items.Add("Visually-lossless H.264 (CRF 18) → .mp4");
            }
            else
            {
                _losslessEncodeCombo.Items.Add("(ffmpeg.exe not found — only PNG output available)");
            }
            _losslessEncodeCombo.SelectedIndex = 0;
            Controls.Add(_losslessEncodeCombo);

            _chkSaveLossless.CheckedChanged += (_, _) =>
            {
                _losslessEncodeCombo.Enabled = _chkSaveLossless.Checked && ffmpegHere;
            };

            ToolTip ttEncode = new ToolTip();
            ttEncode.SetToolTip(_losslessEncodeCombo,
                "After the PNG frames are written, optionally invoke ffmpeg to\n" +
                "produce a video file alongside (or instead of) the PNG folder.\n" +
                "Requires ffmpeg.exe next to the app, in a Tools/ subfolder, or\n" +
                "on PATH.");

            // ── Reverse zoom ──────────────────────────────────────────────────
            _chkReverse = new CheckBox
            {
                Text = "Reverse zoom (start at target, end at classic view)",
                Left = 12,
                Top = 372,
                Width = 396,
                ForeColor = lblColor,
                Font = lblFont,
                BackColor = Color.Transparent,
                Checked = false
            };
            ToolTip ttReverse = new ToolTip();
            ttReverse.SetToolTip(_chkReverse,
                "Off: zoom in from the classic view to the target.\n" +
                "On: begin the video at the target coordinates and zoom, then\n" +
                "animate back out to the classic full-set view by the end of the\n" +
                "video run. Applies to single-shot Start and to Slideshow.");
            Controls.Add(_chkReverse);

            // ── Video Smoothing (TAA blend + band-edge dither) ────────────────
            var smoothBox = new GroupBox
            {
                Text = "Smoothing (Video Only)",
                Left = 8,
                Top = 400,
                Width = 392,
                Height = 120,
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            Controls.Add(smoothBox);

            var lblTaa = new Label
            {
                Text = "Temporal blend:",
                Left = 10,
                Top = 24,
                Width = 100,
                ForeColor = lblColor,
                Font = lblFont,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft
            };
            smoothBox.Controls.Add(lblTaa);

            _tbTaaSmoothing = new TrackBar
            {
                Left = 112,
                Top = 23,
                Width = 232,
                Height = 22,
                Minimum = 0,
                Maximum = 100,
                Value = TaaSmoothing,
                TickFrequency = 10,
                TickStyle = TickStyle.BottomRight,
                BackColor = Color.FromArgb(35, 35, 35)
            };
            smoothBox.Controls.Add(_tbTaaSmoothing);

            _lblTaaSmoothingValue = new Label
            {
                Text = $"{TaaSmoothing}%",
                Left = 348,
                Top = 24,
                Width = 36,
                ForeColor = lblColor,
                Font = lblFont,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft
            };
            smoothBox.Controls.Add(_lblTaaSmoothingValue);
            _tbTaaSmoothing.ValueChanged += (_, _) =>
                _lblTaaSmoothingValue.Text = $"{_tbTaaSmoothing.Value}%";

            ToolTip ttTaa = new ToolTip();
            ttTaa.SetToolTip(_tbTaaSmoothing,
                "Blends each new frame with the previous frame (reprojected to\n" +
                "match the new view). 0% disables the blend. Higher values mask\n" +
                "the per-pixel crawl in densely banded regions; very high values\n" +
                "can produce mild motion ghosting on band edges.");

            _chkBandDither = new CheckBox
            {
                Text = "Band dither:",
                Left = 10,
                Top = 69,
                Width = 100,
                ForeColor = lblColor,
                Font = lblFont,
                BackColor = Color.Transparent,
                Checked = BandDither
            };
            smoothBox.Controls.Add(_chkBandDither);

            _tbBandDitherStrength = new TrackBar
            {
                Left = 112,
                Top = 71,
                Width = 232,
                Height = 22,
                Minimum = 0,
                Maximum = 100,
                Value = BandDitherStrength,
                TickFrequency = 10,
                TickStyle = TickStyle.BottomRight,
                BackColor = Color.FromArgb(35, 35, 35),
                Enabled = false
            };
            smoothBox.Controls.Add(_tbBandDitherStrength);

            _lblBandDitherValue = new Label
            {
                Text = $"{BandDitherStrength}%",
                Left = 348,
                Top = 69,
                Width = 36,
                ForeColor = lblColor,
                Font = lblFont,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft
            };
            smoothBox.Controls.Add(_lblBandDitherValue);

            _chkBandDither.CheckedChanged += (_, _) =>
                _tbBandDitherStrength.Enabled = _chkBandDither.Checked;
            _tbBandDitherStrength.ValueChanged += (_, _) =>
                _lblBandDitherValue.Text = $"{_tbBandDitherStrength.Value}%";

            ToolTip ttDither = new ToolTip();
            ttDither.SetToolTip(_chkBandDither,
                "Adds a fixed spatial noise pattern to the smooth-iteration\n" +
                "value before palette lookup. Blurs band boundaries slightly so\n" +
                "the per-frame shift across them becomes less visible. Pattern\n" +
                "is stable per pixel, so it does not introduce new temporal\n" +
                "noise.");

            // ── Slideshow / OK / Cancel ───────────────────────────────────────
            var slideshow = new Button
            {
                Text = "Slideshow",
                Left = 12,
                Top = 525,
                Width = 96,
                Height = 28,
                BackColor = Color.FromArgb(55, 40, 70),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            slideshow.FlatAppearance.BorderColor = Color.FromArgb(100, 70, 130);
            slideshow.Click += (_, _) =>
            {
                // Ignore target CX/CY/zoom (slideshow picks them randomly).
                // Only validate the Custom duration field if the user chose
                // the Custom radio — Slow/Med/Fast radios mean "use slideshow
                // default duration".
                SlideshowSecondsOverride = null;
                if (_rbCustom.Checked)
                {
                    var ic = CultureInfo.InvariantCulture;
                    if (!double.TryParse(_txCustomSecs.Text.Trim(),
                                         NumberStyles.Float, ic, out double secs)
                        || secs < CustomSecsMin || secs > CustomSecsMax)
                    {
                        MessageBox.Show(
                            $"Custom duration must be between {CustomSecsMin} and {CustomSecsMax:F0} seconds.",
                            "Video Slideshow",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    SlideshowSecondsOverride = secs;
                }
                IsConstantRate = _chkConstantRate.Checked;
                IsReverse = _chkReverse.Checked;
                TaaSmoothing = _tbTaaSmoothing.Value;
                BandDither = _chkBandDither.Checked;
                BandDitherStrength = _tbBandDitherStrength.Value;
                IsSlideshow = true;
                DialogResult = DialogResult.OK;
                Close();
            };
            ToolTip ttSlideshow = new ToolTip();
            ttSlideshow.SetToolTip(slideshow,
                "Auto video slideshow: random non-Extreme region + random theme,\n" +
                "reset to classic between each zoom, 7-second pause between videos.");

            var ok = new Button
            {
                Text = "Start",
                DialogResult = DialogResult.OK,
                Left = 239,
                Top = 525,
                Width = 76,
                Height = 28,
                BackColor = Color.FromArgb(60, 80, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Left = 321,
                Top = 525,
                Width = 76,
                Height = 28,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            ok.Click += (_, _) =>
            {
                IsSaveVideo = _chkSaveVideo.Checked;
                IsSaveLossless = _chkSaveLossless.Checked;
                IsReverse = _chkReverse.Checked;
                TaaSmoothing = _tbTaaSmoothing.Value;
                BandDither = _chkBandDither.Checked;
                BandDitherStrength = _tbBandDitherStrength.Value;
                if (IsSaveLossless && _losslessEncodeCombo.Enabled)
                {
                    LosslessEncode = _losslessEncodeCombo.SelectedIndex switch
                    {
                        1 => LosslessEncodeChoice.LosslessH264Mp4,
                        2 => LosslessEncodeChoice.Ffv1Mkv,
                        3 => LosslessEncodeChoice.HighQualityH264Mp4,
                        _ => LosslessEncodeChoice.None,
                    };
                }
                else
                {
                    LosslessEncode = LosslessEncodeChoice.None;
                }
            };
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(slideshow);
            Controls.Add(ok);
            Controls.Add(cancel);
        }

        private bool TryGetSeconds(out double seconds)
        {
            if (_rbCustom.Checked)
            {
                var ic = CultureInfo.InvariantCulture;
                if (!double.TryParse(_txCustomSecs.Text.Trim(), NumberStyles.Float, ic, out seconds))
                    return false;
                if (seconds < CustomSecsMin || seconds > CustomSecsMax) return false;
                return true;
            }
            seconds = _rbSlow.Checked ? 15.0 : _rbFast.Checked ? 4.0 : 8.0;
            return true;
        }

        public bool TryGetTarget(out double cx, out double cy, out double zoom, out double seconds)
        {
            var ic = CultureInfo.InvariantCulture;
            var ns = NumberStyles.Float;
            bool okCX = FormHelpers.TryParseCoordAny(_txCX.Text, out cx, out _, out _, out _);
            bool okCY = FormHelpers.TryParseCoordAny(_txCY.Text, out cy, out _, out _, out _);
            bool okZ = double.TryParse(_txZoom.Text.Trim(), ns, ic, out zoom);
            bool okS = TryGetSeconds(out seconds);
            return okCX && okCY && okZ && zoom > 0 && okS;
        }

        /// <summary>Returns the full QD-precision target coordinates.</summary>
        public bool TryGetTargetQD(
            out double cxHi, out double cxLo, out double cx2, out double cx3,
            out double cyHi, out double cyLo, out double cy2, out double cy3,
            out double zoom, out double seconds)
        {
            // Reparse the textbox each time so that user-pasted single-string or
            // pipe-delimited QD values are honoured even when no region was
            // picked from the combo. Fall back to the cached region limbs only
            // when the textbox parse yields a single-limb (Hi-only) value.
            bool okCX = FormHelpers.TryParseCoordAny(_txCX.Text,
                out cxHi, out cxLo, out cx2, out cx3);
            bool okCY = FormHelpers.TryParseCoordAny(_txCY.Text,
                out cyHi, out cyLo, out cy2, out cy3);

            // If the textbox is just the Hi limb (typed by hand or from a
            // shallow region), the parser yields zeros for Lo/X2/X3 — fold the
            // cached region limbs back in so deep targets keep their precision.
            if (okCX && cxLo == 0 && cx2 == 0 && cx3 == 0)
            { cxLo = _targetCXLo; cx2 = _targetCX2; cx3 = _targetCX3; }
            if (okCY && cyLo == 0 && cy2 == 0 && cy3 == 0)
            { cyLo = _targetCYLo; cy2 = _targetCY2; cy3 = _targetCY3; }

            var ic = CultureInfo.InvariantCulture;
            var ns = NumberStyles.Float;
            bool okZ = double.TryParse(_txZoom.Text.Trim(), ns, ic, out zoom);
            bool okS = TryGetSeconds(out seconds);
            if (!okCX || !okCY || !okZ || zoom <= 0 || !okS)
            {
                cxHi = cxLo = cx2 = cx3 = cyHi = cyLo = cy2 = cy3 = zoom = seconds = 0;
                return false;
            }
            return true;
        }

        private void OnRegionPicked(object? sender, EventArgs e)
        {
            int idx = _regionCombo.SelectedIndex;
            if (idx <= 0) return;
            string? name = _regionCombo.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(name)) return;
            var region = FractalRegionLibrary.Instance.FindByName(name);
            if (region == null) return;

            // Extreme regions are allowed in Video Zoom only with explicit user
            // confirmation. Video output still caps quality/zoom at Ultra, so
            // warn the user that the target zoom will be clamped.
            if (region.QualityPreset == QualityPreset.Extreme)
            {
                double ultraCap = QualityPreset.Ultra.ZoomMax;
                var result = MessageBox.Show(
                    $"\"{region.Name}\" is an Extreme-quality region.\n\n" +
                    "Video Zoom does not support the Extreme regime — target\n" +
                    $"zoom will be clamped to the Ultra cap ({ultraCap:G3}) and\n" +
                    "the resulting video will not reach this region's full depth.\n\n" +
                    "Continue with this selection?",
                    "Video Zoom — Extreme region",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (result != DialogResult.OK)
                {
                    _regionCombo.SelectedIndexChanged -= OnRegionPicked;
                    _regionCombo.SelectedIndex = 0;
                    _regionCombo.SelectedIndexChanged += OnRegionPicked;
                    return;
                }
            }

            var ic = CultureInfo.InvariantCulture;
            // Cache limbs for the legacy TryGetTargetQD fallback path. The
            // textbox itself carries the full QD value as a single-string
            // digest, so paste-back reparses to the same four-limb tuple.
            _targetCXLo = region.CenterXLo;
            _targetCX2 = region.CenterX2;
            _targetCX3 = region.CenterX3;
            _targetCYLo = region.CenterYLo;
            _targetCY2 = region.CenterY2;
            _targetCY3 = region.CenterY3;

            _txCX.Text = FormHelpers.FormatCoordSingle(
                region.CenterX, region.CenterXLo, region.CenterX2, region.CenterX3);
            _txCY.Text = FormHelpers.FormatCoordSingle(
                region.CenterY, region.CenterYLo, region.CenterY2, region.CenterY3);

            double z = region.Zoom;
            double cap = QualityPreset.Ultra.ZoomMax;
            if (z > cap) z = cap;
            _txZoom.Text = z.ToString("G6", ic);

            TargetIterations = region.Iterations;
        }

    }
}
