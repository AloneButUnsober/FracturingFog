// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Views/ImagePaletteDialog.cs
//
// Modal dialog: drag-and-drop (or browse-to-pick) an image, choose an
// extraction method and a stack of tuning knobs, and produce a
// List<ColorStopData> to drop into the Color Theme Editor.
//
// All four extractors share the same options object so the user can A/B
// them — the "Compare All" button runs every extractor with the current
// options and renders a 4-row swatch+gradient grid for side-by-side
// inspection. Picking a row routes its stops to the Apply button.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SkiaSharp;

using FracturingFog.Imaging.PaletteExtraction;
using FracturingFog.Models;

namespace FracturingFog.Views
{
    public sealed class ImagePaletteDialog : Form
    {
        [DllImport("User32.dll")] private static extern bool ReleaseCapture();
        [DllImport("User32.dll")] private static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 0x2;

        private static readonly IPaletteExtractor[] Extractors =
        {
            new KMeansExtractor(),
            new MedianCutExtractor(),
            new OctreeExtractor(),
            new HistogramExtractor(),
        };

        // ── Drop / preview ───────────────────────────────────────────────────
        private Bitmap? _sourceImage;
        private string? _sourcePath;
        private readonly Panel _dropZone;
        private readonly PictureBox _preview;
        private readonly Label _dropHint;
        private readonly Label _fileLabel;
        private readonly Button _browseButton;

        // ── Controls ─────────────────────────────────────────────────────────
        private readonly ComboBox _cmbMethod;
        private readonly NumericUpDown _numCount;
        private readonly ComboBox _cmbSpace;
        private readonly NumericUpDown _numDownsample;
        private readonly ComboBox _cmbSort;
        private readonly NumericUpDown _numDedup;
        private readonly CheckBox _chkWeighted;
        private readonly CheckBox _chkExcludeBlack;
        private readonly CheckBox _chkExcludeWhite;
        private readonly Button _btnExtract;
        private readonly Button _btnCompareAll;

        // ── Results ──────────────────────────────────────────────────────────
        private readonly Panel _resultsPanel;
        private readonly Button _btnApply;
        private readonly Button _btnCancel;
        private readonly Label _titleLabel;

        private List<ColorStopData>? _selectedStops;

        /// <summary>Stops the user accepted. Null if cancelled.</summary>
        public List<ColorStopData>? Result => _selectedStops;

        public ImagePaletteDialog(Form? parent)
        {
            Owner = parent;
            Text = "Palette from Image";
            BackColor = Color.FromArgb(22, 22, 22);
            ForeColor = Color.FromArgb(220, 220, 220);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(820, 850);
            KeyPreview = true;
            AllowDrop = true;
            // Float above the (already TopMost) Theme Editor so the user
            // can drag this window around freely without it disappearing
            // behind the editor.
            TopMost = true;
            ShowInTaskbar = false;

            if (parent != null)
            {
                const int gap = 8;
                int desiredX = parent.Left - Width - gap;
                int desiredY = parent.Top;
                var bounds = Screen.GetWorkingArea(parent);
                // If there's no room to the left of the editor, fall back
                // to flush-against-its-left-edge (overlap is fine — the
                // user can drag from there).
                if (desiredX < bounds.Left)
                    desiredX = Math.Max(bounds.Left, parent.Left - Width / 2);
                if (desiredY + Height > bounds.Bottom)
                    desiredY = Math.Max(bounds.Top, bounds.Bottom - Height);
                Location = new Point(desiredX, desiredY);
            }

            // Title
            _titleLabel = new Label
            {
                Text = "Palette from Image",
                Left = 12,
                Top = 8,
                AutoSize = true,
                ForeColor = Color.FromArgb(200, 200, 100),
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                BackColor = Color.Transparent,
            };
            _titleLabel.MouseDown += DragTitle;
            Controls.Add(_titleLabel);

            var closeBtn = new Button
            {
                Text = "X",
                Left = ClientSize.Width - 28,
                Top = 6,
                Width = 22,
                Height = 22,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            };
            closeBtn.FlatAppearance.BorderSize = 0;
            closeBtn.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(closeBtn);

            MouseDown += DragTitle;

            int y = 40;

            // ── Drop zone (left) ─────────────────────────────────────────────
            _dropZone = new Panel
            {
                Left = 12,
                Top = y,
                Width = 280,
                Height = 250,
                BackColor = Color.FromArgb(30, 30, 30),
                BorderStyle = BorderStyle.FixedSingle,
                AllowDrop = true,
            };
            Controls.Add(_dropZone);

            _preview = new PictureBox
            {
                Left = 4,
                Top = 4,
                Width = _dropZone.Width - 10,
                Height = _dropZone.Height - 30,
                BackColor = Color.FromArgb(20, 20, 20),
                SizeMode = PictureBoxSizeMode.Zoom,
            };
            _dropZone.Controls.Add(_preview);

            _dropHint = new Label
            {
                Text = "Drop image here\nor click Browse",
                Left = 0,
                Top = (_preview.Height / 2) - 16,
                Width = _preview.Width,
                AutoSize = false,
                Height = 36,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.FromArgb(140, 140, 140),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Italic),
                BackColor = Color.Transparent,
            };
            _preview.Controls.Add(_dropHint);

            _fileLabel = new Label
            {
                Text = "(no image)",
                Left = 4,
                Top = _preview.Bottom + 4,
                Width = _dropZone.Width - 90,
                Height = 18,
                ForeColor = Color.FromArgb(160, 160, 160),
                Font = new Font("Segoe UI", 8.5f),
                BackColor = Color.Transparent,
                AutoEllipsis = true,
            };
            _dropZone.Controls.Add(_fileLabel);

            _browseButton = MakeButton("Browse…", _dropZone.Width - 84, _preview.Bottom + 2, 78, 22, Color.FromArgb(45, 65, 100));
            _browseButton.Click += (s, e) => BrowseForImage();
            _dropZone.Controls.Add(_browseButton);

            // Drop wiring on both the dialog and the drop panel so the
            // entire form catches a drop, not just the small zone.
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
            _dropZone.DragEnter += OnDragEnter;
            _dropZone.DragDrop += OnDragDrop;
            _preview.DragEnter += OnDragEnter;
            _preview.DragDrop += OnDragDrop;
            _preview.AllowDrop = true;

            // ── Controls (right of drop) ─────────────────────────────────────
            int cx = _dropZone.Right + 16;
            int cy = y;

            int labelLeft = cx;
            int fieldLeft = cx + 130;
            const int rowH = 30;

            AddLabel("Method:", labelLeft, cy + 4);
            _cmbMethod = MakeCombo(fieldLeft, cy, 180);
            foreach (var ex in Extractors) _cmbMethod.Items.Add(ex.Name);
            _cmbMethod.SelectedIndex = 0;
            Controls.Add(_cmbMethod);
            cy += rowH;

            AddLabel("Color count:", labelLeft, cy + 4);
            _numCount = MakeNumeric(fieldLeft, cy, 4, 32, 8, 0);
            Controls.Add(_numCount);
            cy += rowH;

            AddLabel("Color space:", labelLeft, cy + 4);
            _cmbSpace = MakeCombo(fieldLeft, cy, 180);
            _cmbSpace.Items.AddRange(new object[] { "RGB", "Lab (perceptual)", "HSL" });
            _cmbSpace.SelectedIndex = 1;
            Controls.Add(_cmbSpace);
            cy += rowH;

            AddLabel("Downsample max:", labelLeft, cy + 4);
            _numDownsample = MakeNumeric(fieldLeft, cy, 64, 1024, 256, 0);
            _numDownsample.Increment = 64;
            Controls.Add(_numDownsample);
            cy += rowH;

            AddLabel("Sort:", labelLeft, cy + 4);
            _cmbSort = MakeCombo(fieldLeft, cy, 180);
            _cmbSort.Items.AddRange(new object[] { "Nearest-Neighbor Chain", "Hue", "Luminance", "Cluster Size" });
            _cmbSort.SelectedIndex = 0;
            Controls.Add(_cmbSort);
            cy += rowH;

            AddLabel("Dedup ΔE:", labelLeft, cy + 4);
            _numDedup = MakeNumeric(fieldLeft, cy, 0M, 30M, 2M, 1);
            _numDedup.Increment = 0.5M;
            Controls.Add(_numDedup);
            cy += rowH;

            _chkWeighted = MakeCheck("Weight stop positions by cluster size", labelLeft, cy);
            Controls.Add(_chkWeighted);
            cy += 24;
            _chkExcludeBlack = MakeCheck("Exclude near-black pixels", labelLeft, cy);
            _chkExcludeBlack.Checked = false;
            Controls.Add(_chkExcludeBlack);
            cy += 24;
            _chkExcludeWhite = MakeCheck("Exclude near-white pixels", labelLeft, cy);
            Controls.Add(_chkExcludeWhite);
            cy += 32;

            _btnExtract = MakeButton("Extract", labelLeft, cy, 130, 30, Color.FromArgb(40, 80, 40));
            _btnExtract.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            _btnExtract.Click += (s, e) => RunSingle();
            Controls.Add(_btnExtract);

            _btnCompareAll = MakeButton("Compare All", labelLeft + 140, cy, 160, 30, Color.FromArgb(60, 50, 100));
            _btnCompareAll.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            _btnCompareAll.Click += (s, e) => RunCompareAll();
            Controls.Add(_btnCompareAll);

            // ── Results panel ────────────────────────────────────────────────
            int resultsTop = Math.Max(_dropZone.Bottom, cy + 40) + 6;
            _resultsPanel = new Panel
            {
                Left = 12,
                Top = resultsTop,
                Width = ClientSize.Width - 24,
                Height = ClientSize.Height - resultsTop - 50,
                BackColor = Color.FromArgb(28, 28, 28),
                BorderStyle = BorderStyle.FixedSingle,
                AutoScroll = true,
            };
            Controls.Add(_resultsPanel);

            // ── Apply / Cancel ───────────────────────────────────────────────
            _btnApply = MakeButton("Apply", ClientSize.Width - 220, ClientSize.Height - 40, 100, 30, Color.FromArgb(40, 100, 60));
            _btnApply.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            _btnApply.Enabled = false;
            _btnApply.Click += (s, e) =>
            {
                if (_selectedStops == null || _selectedStops.Count < 2) return;
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(_btnApply);

            _btnCancel = MakeButton("Cancel", ClientSize.Width - 110, ClientSize.Height - 40, 90, 30, Color.FromArgb(70, 50, 50));
            _btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(_btnCancel);

            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            };
        }

        // ── Helpers (UI build) ───────────────────────────────────────────────

        private void AddLabel(string text, int left, int top)
        {
            var lbl = new Label
            {
                Text = text,
                Left = left,
                Top = top,
                AutoSize = true,
                ForeColor = Color.FromArgb(180, 180, 180),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                BackColor = Color.Transparent,
            };
            Controls.Add(lbl);
        }

        private static ComboBox MakeCombo(int left, int top, int width)
            => new ComboBox
            {
                Left = left,
                Top = top,
                Width = width,
                Height = 24,
                BackColor = Color.FromArgb(55, 55, 55),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                FlatStyle = FlatStyle.Flat,
                DropDownStyle = ComboBoxStyle.DropDownList,
            };

        private static NumericUpDown MakeNumeric(int left, int top, decimal min, decimal max, decimal value, int decimals)
            => new NumericUpDown
            {
                Left = left,
                Top = top,
                Width = 100,
                Height = 24,
                Minimum = min,
                Maximum = max,
                Value = Math.Clamp(value, min, max),
                DecimalPlaces = decimals,
                Increment = decimals > 0 ? 0.5M : 1M,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.FromArgb(220, 220, 220),
                BorderStyle = BorderStyle.FixedSingle,
                TextAlign = HorizontalAlignment.Right,
            };

        private static CheckBox MakeCheck(string text, int left, int top)
            => new CheckBox
            {
                Text = text,
                Left = left,
                Top = top,
                AutoSize = true,
                ForeColor = Color.FromArgb(200, 200, 200),
                Font = new Font("Segoe UI", 8.5f),
                BackColor = Color.Transparent,
            };

        private static Button MakeButton(string text, int left, int top, int width, int height, Color bg)
        {
            var b = new Button
            {
                Text = text,
                Left = left,
                Top = top,
                Width = width,
                Height = height,
                FlatStyle = FlatStyle.Flat,
                BackColor = bg,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(
                Math.Min(255, bg.R + 40), Math.Min(255, bg.G + 40), Math.Min(255, bg.B + 40));
            return b;
        }

        // ── Drag ─────────────────────────────────────────────────────────────

        private void DragTitle(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
        }

        // ── Image acquisition ────────────────────────────────────────────────

        private void OnDragEnter(object? sender, DragEventArgs e)
        {
            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effect = DragDropEffects.Copy;
        }

        private void OnDragDrop(object? sender, DragEventArgs e)
        {
            if (e.Data == null) return;
            string[]? files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;
            LoadImage(files[0]);
        }

        private void BrowseForImage()
        {
            using var ofd = new OpenFileDialog
            {
                Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files (*.*)|*.*",
                Title = "Open Image",
            };
            if (ofd.ShowDialog(this) != DialogResult.OK) return;
            LoadImage(ofd.FileName);
        }

        private void LoadImage(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".webp")
            {
                MessageBox.Show(this,
                    "WebP files are not supported by GDI+ on this machine.\n\n" +
                    "Re-save the image as PNG or JPG and try again, or install\n" +
                    "Microsoft's \"WebP Image Extensions\" from the Microsoft Store\n" +
                    "to add system-wide WebP decoding.",
                    "Unsupported Format",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                using var raw = Image.FromFile(path);
                var copy = new Bitmap(raw);
                _sourceImage?.Dispose();
                _sourceImage = copy;
                _sourcePath = path;
                _preview.Image?.Dispose();
                _preview.Image = new Bitmap(copy);
                _dropHint.Visible = false;
                _fileLabel.Text = Path.GetFileName(path);
                InvalidatePixelCache();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to load image:\n" + ex.Message, "Open Image",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ── Extraction ───────────────────────────────────────────────────────

        private PaletteExtractionOptions BuildOptions()
        {
            return new PaletteExtractionOptions
            {
                ColorCount = (int)_numCount.Value,
                Space = _cmbSpace.SelectedIndex switch
                {
                    0 => PaletteColorSpace.Rgb,
                    2 => PaletteColorSpace.Hsl,
                    _ => PaletteColorSpace.Lab,
                },
                DownsampleMaxDim = (int)_numDownsample.Value,
                ExcludeNearBlack = _chkExcludeBlack.Checked,
                ExcludeNearWhite = _chkExcludeWhite.Checked,
            };
        }

        private PaletteStopBuilder BuildStopBuilder()
        {
            return new PaletteStopBuilder
            {
                Sort = _cmbSort.SelectedIndex switch
                {
                    1 => StopSortMode.Hue,
                    2 => StopSortMode.Luminance,
                    3 => StopSortMode.ClusterSize,
                    _ => StopSortMode.NearestNeighborChain,
                },
                DedupDeltaE = (float)_numDedup.Value,
                WeightedPositions = _chkWeighted.Checked,
            };
        }

        /// <summary>
        /// Cache the downsampled-and-filtered pixel buffer so repeated
        /// extractions for the same image / option combo don't redo the work.
        /// </summary>
        private byte[]? _cachedPixels;
        private int _cachedCount;
        private string? _cacheKey;

        private (byte[] pixels, int count) GetPixels(PaletteExtractionOptions opts)
        {
            string key = $"{_sourcePath}|{opts.DownsampleMaxDim}|{opts.ExcludeNearBlack}|{opts.ExcludeNearWhite}";
            if (_cachedPixels != null && _cacheKey == key)
                return (_cachedPixels, _cachedCount);

            // WinForms dialog is on the deprecation tail (CLAUDE.md): keep it
            // buildable but minimise churn. _sourceImage stays a GDI Bitmap
            // (for the PictureBox preview); convert to SKBitmap at the
            // sampler boundary so the new SkiaSharp BitmapSampler API stays
            // the only path for pixel extraction.
            using var skia = GdiToSkia(_sourceImage!);
            using var down = BitmapSampler.Downsample(skia, opts.DownsampleMaxDim);
            _cachedPixels = BitmapSampler.ExtractPixels(down,
                opts.ExcludeNearBlack, opts.ExcludeNearWhite,
                out _cachedCount);
            _cacheKey = key;
            return (_cachedPixels, _cachedCount);
        }

        private void InvalidatePixelCache()
        {
            _cachedPixels = null;
            _cachedCount = 0;
            _cacheKey = null;
        }

        private void RunSingle()
        {
            if (_sourceImage == null)
            {
                MessageBox.Show(this, "Drop or browse to an image first.", "Extract",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var opts = BuildOptions();
            var (pixels, count) = GetPixels(opts);
            if (count == 0)
            {
                ShowEmptyResults("No pixels left after filters.");
                return;
            }

            int methodIdx = Math.Max(0, _cmbMethod.SelectedIndex);
            var extractor = Extractors[methodIdx];
            var palette = extractor.Extract(pixels, count, opts);

            var stops = BuildStopBuilder().Build(palette);

            _resultsPanel.SuspendLayout();
            ClearResultsPanel();
            BuildResultRow(extractor.Name, palette, stops, top: 8, isSelected: true, exclusiveSelect: false);
            _resultsPanel.ResumeLayout();

            _selectedStops = stops;
            _btnApply.Enabled = stops.Count >= 2;
        }

        private void RunCompareAll()
        {
            if (_sourceImage == null)
            {
                MessageBox.Show(this, "Drop or browse to an image first.", "Compare",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var opts = BuildOptions();
            var (pixels, count) = GetPixels(opts);
            if (count == 0)
            {
                ShowEmptyResults("No pixels left after filters.");
                return;
            }

            var builder = BuildStopBuilder();

            _resultsPanel.SuspendLayout();
            ClearResultsPanel();

            int y = 8;
            foreach (var ex in Extractors)
            {
                var palette = ex.Extract(pixels, count, opts);
                var stops = builder.Build(palette);
                BuildResultRow(ex.Name, palette, stops, y, isSelected: false, exclusiveSelect: true);
                y += 110;
            }

            _resultsPanel.ResumeLayout();

            _selectedStops = null;
            _btnApply.Enabled = false;
        }

        private void ClearResultsPanel()
        {
            foreach (Control c in _resultsPanel.Controls.OfType<Control>().ToList())
                c.Dispose();
            _resultsPanel.Controls.Clear();
        }

        private void ShowEmptyResults(string message)
        {
            ClearResultsPanel();
            var lbl = new Label
            {
                Text = message,
                Left = 12,
                Top = 12,
                AutoSize = true,
                ForeColor = Color.FromArgb(200, 100, 100),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                BackColor = Color.Transparent,
            };
            _resultsPanel.Controls.Add(lbl);
            _selectedStops = null;
            _btnApply.Enabled = false;
        }

        private void BuildResultRow(string name,
                                    IReadOnlyList<ExtractedColor> palette,
                                    List<ColorStopData> stops,
                                    int top,
                                    bool isSelected,
                                    bool exclusiveSelect)
        {
            int rowWidth = _resultsPanel.ClientSize.Width - 20;

            var rowBg = new Panel
            {
                Left = 8,
                Top = top,
                Width = rowWidth,
                Height = 100,
                BackColor = isSelected ? Color.FromArgb(40, 60, 40) : Color.FromArgb(35, 35, 35),
                BorderStyle = BorderStyle.FixedSingle,
                Tag = stops,
            };
            _resultsPanel.Controls.Add(rowBg);

            var radio = new RadioButton
            {
                Text = name,
                Left = 6,
                Top = 4,
                AutoSize = true,
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Checked = isSelected,
                Visible = exclusiveSelect,
            };
            radio.CheckedChanged += (s, e) =>
            {
                if (!radio.Checked) return;
                foreach (Control c in _resultsPanel.Controls)
                {
                    if (c is Panel p && p != rowBg)
                    {
                        p.BackColor = Color.FromArgb(35, 35, 35);
                        foreach (Control cc in p.Controls)
                            if (cc is RadioButton rb && rb != radio) rb.Checked = false;
                    }
                }
                rowBg.BackColor = Color.FromArgb(40, 60, 40);
                _selectedStops = (List<ColorStopData>)rowBg.Tag!;
                _btnApply.Enabled = _selectedStops.Count >= 2;
            };
            rowBg.Controls.Add(radio);

            if (!exclusiveSelect)
            {
                var titleLbl = new Label
                {
                    Text = name + $"   —   {palette.Count} swatches",
                    Left = 6,
                    Top = 4,
                    AutoSize = true,
                    ForeColor = Color.FromArgb(220, 220, 160),
                    Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                    BackColor = Color.Transparent,
                };
                rowBg.Controls.Add(titleLbl);
            }

            // Swatch row
            int swatchY = 26;
            int swatchH = 26;
            int swatchSpacing = 2;
            int availW = rowWidth - 20;
            int swatchW = palette.Count > 0
                ? Math.Max(8, (availW - swatchSpacing * (palette.Count - 1)) / palette.Count)
                : availW;
            int sx = 10;
            foreach (var c in palette)
            {
                var sp = new Panel
                {
                    Left = sx,
                    Top = swatchY,
                    Width = swatchW,
                    Height = swatchH,
                    BackColor = Color.FromArgb(c.R, c.G, c.B),
                    BorderStyle = BorderStyle.None,
                };
                rowBg.Controls.Add(sp);
                sx += swatchW + swatchSpacing;
            }

            // Gradient strip
            var grad = new Panel
            {
                Left = 10,
                Top = swatchY + swatchH + 6,
                Width = rowWidth - 20,
                Height = 30,
                BorderStyle = BorderStyle.FixedSingle,
            };
            grad.Paint += (s, e) => PaintGradient(e.Graphics, grad.ClientSize, stops);
            rowBg.Controls.Add(grad);

            if (!exclusiveSelect)
            {
                // Single-extract mode: treat the row as auto-selected.
                rowBg.Tag = stops;
            }

            // Make the entire row click-selectable in compare mode.
            if (exclusiveSelect)
            {
                rowBg.Click += (s, e) => radio.Checked = true;
                foreach (Control c in rowBg.Controls) c.Click += (s, e) => radio.Checked = true;
            }
        }

        private static void PaintGradient(Graphics g, Size size, List<ColorStopData> stops)
        {
            g.Clear(Color.FromArgb(20, 20, 20));
            if (stops == null || stops.Count == 0) return;
            int w = size.Width, h = size.Height;
            if (w <= 0 || h <= 0) return;

            var ordered = stops.OrderBy(s => s.Position).ToList();
            if (ordered.Count == 1)
            {
                using var sb = new SolidBrush(Color.FromArgb(ordered[0].R, ordered[0].G, ordered[0].B));
                g.FillRectangle(sb, 0, 0, w, h);
                return;
            }

            for (int x = 0; x < w; x++)
            {
                float t = (float)x / Math.Max(1, w - 1);
                Color c = SampleGradient(ordered, t);
                using var pen = new Pen(c);
                g.DrawLine(pen, x, 0, x, h);
            }
        }

        private static Color SampleGradient(List<ColorStopData> stops, float t)
        {
            if (t <= stops[0].Position) return Color.FromArgb(stops[0].R, stops[0].G, stops[0].B);
            var last = stops[^1];
            if (t >= last.Position) return Color.FromArgb(last.R, last.G, last.B);

            for (int i = 0; i < stops.Count - 1; i++)
            {
                var a = stops[i]; var b = stops[i + 1];
                if (t >= a.Position && t <= b.Position)
                {
                    float span = b.Position - a.Position;
                    float u = span > 1e-6f ? (t - a.Position) / span : 0f;
                    return Color.FromArgb(
                        (int)(a.R + (b.R - a.R) * u),
                        (int)(a.G + (b.G - a.G) * u),
                        (int)(a.B + (b.B - a.B) * u));
                }
            }
            return Color.FromArgb(last.R, last.G, last.B);
        }

        // ── GDI → Skia bridge (deprecation-tail only) ────────────────────────
        //
        // ImagePaletteDialog keeps a GDI Bitmap for the WinForms PictureBox
        // preview; the SkiaSharp BitmapSampler is the canonical pixel path.
        // Convert at the sampler boundary so the WinForms shell stays
        // buildable per CLAUDE.md without polluting the lib with a GDI
        // overload.
        private static unsafe SKBitmap GdiToSkia(Bitmap src)
        {
            var info = new SKImageInfo(src.Width, src.Height,
                                       SKColorType.Bgra8888, SKAlphaType.Premul);
            var dst = new SKBitmap(info);
            var rect = new Rectangle(0, 0, src.Width, src.Height);
            var data = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int rowBytes = src.Width * 4;
                byte* sp = (byte*)data.Scan0.ToPointer();
                byte* dp = (byte*)dst.GetPixels().ToPointer();
                int dstStride = dst.RowBytes;
                for (int y = 0; y < src.Height; y++)
                    Buffer.MemoryCopy(sp + y * data.Stride, dp + y * dstStride, rowBytes, rowBytes);
            }
            finally { src.UnlockBits(data); }
            return dst;
        }

        // ── Cleanup ──────────────────────────────────────────────────────────

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _sourceImage?.Dispose();
            _preview.Image?.Dispose();
            base.OnFormClosed(e);
        }

        // ── React to filter-change so pixel cache is dropped ─────────────────

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _chkExcludeBlack.CheckedChanged += (s, e2) => InvalidatePixelCache();
            _chkExcludeWhite.CheckedChanged += (s, e2) => InvalidatePixelCache();
            _numDownsample.ValueChanged += (s, e2) => InvalidatePixelCache();
        }
    }
}
