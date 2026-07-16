// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Views/Editors/ColorStopListControl.cs
//
// Reusable editor control for List<ColorStopData>. Renders each stop as a
// horizontal row: position TextBox, color swatch (click → ColorDialog),
// R/G/B numeric TextBoxes, eyedropper, delete button. Action buttons at
// the top:
//   [+ Add Stop] [From Image…] [Import…] [Export…]
//
// Sized explicitly (no AutoSize on the UserControl) so the host form can
// guarantee the rows are visible and scrollable.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

using FracturingFog.Models;
using System.ComponentModel;

namespace FracturingFog.Views.Editors
{
    public sealed class ColorStopListControl : UserControl
    {
        private const int RowHeight = 28;
        private const int ButtonHeight = 26;

        private readonly Panel _scroll;
        private readonly Button _addButton;
        private readonly Button _fromFileButton;
        private readonly Button _importButton;
        private readonly Button _exportButton;
        private bool _suppressChange;
        private StopRow? _selectedRow;

        public event EventHandler? OnStopsChanged;
        public event EventHandler? OnFromFile;

        /// <summary>
        /// Used by the editor to label the exported file. Caller updates
        /// this whenever the Identity Name changes.
        /// </summary>
        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string PaletteNameHint { get; set; } = "Palette";

        public ColorStopListControl()
        {
            BackColor = Color.FromArgb(22, 22, 22);
            Size = new Size(340, 200);

            _scroll = new Panel
            {
                Left = 0,
                Top = ButtonHeight + 4,
                Width = Width,
                Height = Height,
                BackColor = Color.FromArgb(22, 22, 22),
                AutoScroll = true,
            };
            Controls.Add(_scroll);

            // Four action buttons across the top.
            _addButton = MakeTopButton("+ Add Stop", Color.FromArgb(40, 60, 40));
            _addButton.Click += (s, e) =>
            {
                AddRow(new ColorStopData { Position = 1f, R = 255, G = 255, B = 255 });
                RaiseChanged();
            };
            Controls.Add(_addButton);

            _fromFileButton = MakeTopButton("From Image…", Color.FromArgb(60, 50, 90));
            _fromFileButton.Click += (s, e) => OnFromFile?.Invoke(s, e);
            Controls.Add(_fromFileButton);

            _importButton = MakeTopButton("Import…", Color.FromArgb(50, 70, 100));
            _importButton.Click += (s, e) => ImportFromFile();
            Controls.Add(_importButton);

            _exportButton = MakeTopButton("Export…", Color.FromArgb(70, 60, 100));
            _exportButton.Click += (s, e) => ExportToFile();
            Controls.Add(_exportButton);

            LayoutTopButtons();

            Resize += (s, e) =>
            {
                _scroll.Width = Width;
                _scroll.Height = Math.Max(20, Height - ButtonHeight - 4);
                LayoutTopButtons();
                RelayoutRowsWidth();
            };
        }

        private static Button MakeTopButton(string text, Color bg)
        {
            var b = new Button
            {
                Text = text,
                Top = 0,
                Height = ButtonHeight,
                FlatStyle = FlatStyle.Flat,
                BackColor = bg,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(
                Math.Min(255, bg.R + 40), Math.Min(255, bg.G + 40), Math.Min(255, bg.B + 40));
            return b;
        }

        private void LayoutTopButtons()
        {
            const int gap = 2;
            int total = Math.Max(120, Width - 0);
            int w = (total - gap * 3) / 4;
            int x = 0;
            _addButton.Left = x; _addButton.Width = w; x += w + gap;
            _fromFileButton.Left = x; _fromFileButton.Width = w; x += w + gap;
            _importButton.Left = x; _importButton.Width = w; x += w + gap;
            _exportButton.Left = x; _exportButton.Width = w;
        }

        public void LoadStops(IEnumerable<ColorStopData>? stops)
        {
            _suppressChange = true;
            _scroll.SuspendLayout();
            _selectedRow = null;
            foreach (Control c in _scroll.Controls)
                c.Dispose();
            _scroll.Controls.Clear();

            if (stops != null)
            {
                foreach (var s in stops.OrderBy(x => x.Position))
                    AddRow(new ColorStopData { Position = s.Position, R = s.R, G = s.G, B = s.B });
            }

            _scroll.ResumeLayout();
            _suppressChange = false;
        }

        public List<ColorStopData> GetStops()
        {
            var result = new List<ColorStopData>();
            foreach (Control c in _scroll.Controls)
                if (c is StopRow row) result.Add(row.ToData());
            return result.OrderBy(s => s.Position).ToList();
        }

        // ── Import / Export ────────────────────────────────────────────────

        private void ImportFromFile()
        {
            using var ofd = new OpenFileDialog
            {
                Filter = PaletteFileIO.ImportFilter,
                Title = "Import Palette",
                CheckFileExists = true,
            };
            if (ofd.ShowDialog(FindForm()) != DialogResult.OK) return;

            List<PaletteFileIO.Rgb> colors;
            try
            {
                colors = PaletteFileIO.Load(ofd.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(FindForm(), "Failed to read palette file:\n" + ex.Message,
                    "Import Palette", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (colors.Count == 0)
            {
                MessageBox.Show(FindForm(),
                    "No colors found in the file. Verify it is a PaletteBuilder JSON, GIMP .gpl, CSS, or hex list.",
                    "Import Palette", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int current = _scroll.Controls.OfType<StopRow>().Count();
            using var dlg = new AddOrReplaceDialog(colors.Count, current, Path.GetFileName(ofd.FileName));
            dlg.ShowDialog(FindForm());
            if (dlg.Result == AddOrReplaceDialog.Choice.Cancel) return;

            if (dlg.Result == AddOrReplaceDialog.Choice.Add)
            {
                // Append: imported colors all land at position=1.0,
                // existing stops are not touched.
                foreach (var c in colors)
                    AddRow(new ColorStopData { Position = 1f, R = c.R, G = c.G, B = c.B });
            }
            else // Replace
            {
                // Discard current stops; redistribute imported colors across
                // the full 0…1 range.
                LoadStops(BuildEvenlyDistributedStops(colors));
            }
            RaiseChanged();
        }

        private static List<ColorStopData> BuildEvenlyDistributedStops(List<PaletteFileIO.Rgb> colors)
        {
            var list = new List<ColorStopData>(colors.Count);
            if (colors.Count == 1)
            {
                list.Add(new ColorStopData { Position = 0.5f, R = colors[0].R, G = colors[0].G, B = colors[0].B });
                return list;
            }
            for (int i = 0; i < colors.Count; i++)
            {
                float p = (float)i / (colors.Count - 1);
                var c = colors[i];
                list.Add(new ColorStopData { Position = p, R = c.R, G = c.G, B = c.B });
            }
            return list;
        }

        private void ExportToFile()
        {
            var stops = GetStops();
            if (stops.Count == 0)
            {
                MessageBox.Show(FindForm(), "No stops to export.", "Export Palette",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var sfd = new SaveFileDialog
            {
                Filter = PaletteFileIO.ExportFilter,
                Title = "Export Palette",
                FileName = SafeFileName(PaletteNameHint) + ".json",
            };
            if (sfd.ShowDialog(FindForm()) != DialogResult.OK) return;

            try
            {
                PaletteFileIO.Save(sfd.FileName, stops, PaletteNameHint);
            }
            catch (Exception ex)
            {
                MessageBox.Show(FindForm(), "Failed to write palette file:\n" + ex.Message,
                    "Export Palette", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static string SafeFileName(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return string.IsNullOrWhiteSpace(s) ? "palette" : s;
        }

        // ── Inspect / Sample API ───────────────────────────────────────────

        /// <summary>
        /// Replace the color on the currently-selected stop row. Used by the
        /// editor's "Sample" eyedropper after the user picks a screen pixel.
        /// If no row is selected, the closest row by RGB distance is used so
        /// the gesture still has an effect.
        /// </summary>
        public bool ApplyColorToSelectedRow(byte r, byte g, byte b)
        {
            var row = _selectedRow;
            if (row == null || row.IsDisposed)
                row = FindClosestRow(r, g, b);
            if (row == null) return false;

            row.SetColor(r, g, b);
            SelectRow(row);
            RaiseChanged();
            return true;
        }

        /// <summary>
        /// Visually highlight the stop whose RGB is closest to (r,g,b).
        /// Used by the editor's "Inspect" mode when the user clicks the main
        /// rendered image.
        /// </summary>
        public void HighlightClosestStop(byte r, byte g, byte b)
        {
            var row = FindClosestRow(r, g, b);
            if (row == null) return;
            SelectRow(row);
            row.Pulse();
            _scroll.ScrollControlIntoView(row);
        }

        private StopRow? FindClosestRow(byte r, byte g, byte b)
        {
            StopRow? best = null;
            int bestDist = int.MaxValue;
            foreach (Control c in _scroll.Controls)
            {
                if (c is not StopRow row) continue;
                var d = row.ToData();
                int dr = d.R - r, dg = d.G - g, db = d.B - b;
                int dist = dr * dr + dg * dg + db * db;
                if (dist < bestDist) { bestDist = dist; best = row; }
            }
            return best;
        }

        private void SelectRow(StopRow row)
        {
            if (_selectedRow != null && !_selectedRow.IsDisposed && _selectedRow != row)
                _selectedRow.SetSelected(false);
            _selectedRow = row;
            row.SetSelected(true);
        }

        // ── Row plumbing ───────────────────────────────────────────────────

        private void AddRow(ColorStopData data)
        {
            int y = _scroll.Controls.OfType<StopRow>().Count() * RowHeight;
            var row = new StopRow(data)
            {
                Left = 0,
                Top = y,
                Width = _scroll.ClientSize.Width - 20,
            };
            row.OnRowChanged += (s, e) => RaiseChanged();
            row.OnRowClicked += (s, e) =>
            {
                if (s is StopRow r) SelectRow(r);
            };
            row.OnSampleClicked += (s, e) =>
            {
                if (s is not StopRow r) return;
                SelectRow(r);
                if (DesktopEyedropper.IsActive) return;
                try
                {
                    DesktopEyedropper.Begin(
                        c =>
                        {
                            if (r.IsDisposed) return;
                            r.SetColor(c.R, c.G, c.B);
                            RaiseChanged();
                        },
                        null);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(FindForm(),
                        "Failed to start eyedropper:\n" + ex.Message,
                        "Sample", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            row.OnDeleteClicked += (s, e) =>
            {
                if (_selectedRow == row) _selectedRow = null;
                _scroll.Controls.Remove(row);
                row.Dispose();
                RelayoutRows();
                RaiseChanged();
            };
            _scroll.Controls.Add(row);
        }

        private void RelayoutRows()
        {
            int y = 0;
            foreach (Control c in _scroll.Controls)
            {
                if (c is StopRow row)
                {
                    row.Top = y;
                    y += RowHeight;
                }
            }
        }

        private void RelayoutRowsWidth()
        {
            int w = Math.Max(120, _scroll.ClientSize.Width - 20);
            foreach (Control c in _scroll.Controls)
                if (c is StopRow row) row.Width = w;
        }

        private void RaiseChanged()
        {
            if (_suppressChange) return;
            OnStopsChanged?.Invoke(this, EventArgs.Empty);
        }

        // ── Row ────────────────────────────────────────────────────────────

        private sealed class StopRow : Panel
        {
            private static readonly Color NormalBg = Color.FromArgb(28, 28, 28);
            private static readonly Color SelectedBg = Color.FromArgb(45, 60, 80);
            private static readonly Color PulseBg = Color.FromArgb(80, 110, 60);

            private readonly TextBox _pos;
            private readonly Panel _swatch;
            private readonly NumericUpDown _r, _g, _b;
            private readonly Button _sample;
            private readonly Button _del;
            private readonly System.Windows.Forms.Timer _pulseTimer;
            private bool _isSelected;

            public event EventHandler? OnRowChanged;
            public event EventHandler? OnRowClicked;
            public event EventHandler? OnDeleteClicked;
            public event EventHandler? OnSampleClicked;

            public StopRow(ColorStopData data)
            {
                Height = 26;
                BackColor = NormalBg;
                Margin = new Padding(0);

                _pos = new TextBox
                {
                    Left = 4,
                    Top = 2,
                    Width = 50,
                    Height = 22,
                    BackColor = Color.FromArgb(40, 40, 40),
                    ForeColor = Color.FromArgb(220, 220, 220),
                    Font = new Font("Consolas", 9f),
                    BorderStyle = BorderStyle.FixedSingle,
                    TextAlign = HorizontalAlignment.Right,
                    Text = data.Position.ToString("0.###", CultureInfo.InvariantCulture),
                };
                _pos.TextChanged += (s, e) => OnRowChanged?.Invoke(this, EventArgs.Empty);
                _pos.MouseDown += BubbleClick;
                Controls.Add(_pos);

                _swatch = new Panel
                {
                    Left = _pos.Right + 6,
                    Top = 2,
                    Width = 40,
                    Height = 22,
                    BackColor = Color.FromArgb(data.R, data.G, data.B),
                    BorderStyle = BorderStyle.FixedSingle,
                    Cursor = Cursors.Hand,
                };
                _swatch.Click += (s, e) =>
                {
                    OnRowClicked?.Invoke(this, EventArgs.Empty);
                    using var dlg = new ColorDialog
                    {
                        FullOpen = true,
                        AnyColor = true,
                        SolidColorOnly = false,
                        Color = _swatch.BackColor,
                    };
                    if (dlg.ShowDialog(FindForm()) == DialogResult.OK)
                    {
                        SetColor(dlg.Color.R, dlg.Color.G, dlg.Color.B);
                        OnRowChanged?.Invoke(this, EventArgs.Empty);
                    }
                };
                Controls.Add(_swatch);

                _r = MakeByte(data.R, _swatch.Right + 8);
                _g = MakeByte(data.G, _r.Right + 4);
                _b = MakeByte(data.B, _g.Right + 4);
                Controls.Add(_r); Controls.Add(_g); Controls.Add(_b);

                EventHandler sync = (s, e) =>
                {
                    _swatch.BackColor = Color.FromArgb((int)_r.Value, (int)_g.Value, (int)_b.Value);
                    OnRowChanged?.Invoke(this, EventArgs.Empty);
                };
                _r.ValueChanged += sync;
                _g.ValueChanged += sync;
                _b.ValueChanged += sync;

                _sample = new Button
                {
                    Text = "◎",
                    Left = _b.Right + 4,
                    Top = 2,
                    Width = 22,
                    Height = 22,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(50, 75, 90),
                    ForeColor = Color.White,
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                    Cursor = Cursors.Hand,
                };
                _sample.FlatAppearance.BorderColor = Color.FromArgb(90, 130, 160);
                ToolTip tt = new();
                tt.SetToolTip(_sample, "Sample color from screen (eyedropper)");
                _sample.Click += (s, e) => OnSampleClicked?.Invoke(this, EventArgs.Empty);
                Controls.Add(_sample);

                _del = new Button
                {
                    Text = "X",
                    Left = _sample.Right + 2,
                    Top = 2,
                    Width = 22,
                    Height = 22,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(80, 35, 35),
                    ForeColor = Color.White,
                    Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                };
                _del.FlatAppearance.BorderColor = Color.FromArgb(140, 60, 60);
                _del.Click += (s, e) => OnDeleteClicked?.Invoke(this, EventArgs.Empty);
                Controls.Add(_del);

                Width = _del.Right + 4;
                Click += BubbleClick;

                _pulseTimer = new System.Windows.Forms.Timer { Interval = 700 };
                _pulseTimer.Tick += (s, e) =>
                {
                    _pulseTimer.Stop();
                    BackColor = _isSelected ? SelectedBg : NormalBg;
                };
            }

            private void BubbleClick(object? sender, EventArgs e) => OnRowClicked?.Invoke(this, EventArgs.Empty);

            public void SetSelected(bool selected)
            {
                _isSelected = selected;
                if (!_pulseTimer.Enabled)
                    BackColor = selected ? SelectedBg : NormalBg;
            }

            public void Pulse()
            {
                BackColor = PulseBg;
                _pulseTimer.Stop();
                _pulseTimer.Start();
            }

            public void SetColor(byte r, byte g, byte b)
            {
                _r.Value = r;
                _g.Value = g;
                _b.Value = b;
                _swatch.BackColor = Color.FromArgb(r, g, b);
            }

            private NumericUpDown MakeByte(byte value, int left)
            {
                return new NumericUpDown
                {
                    Left = left,
                    Top = 2,
                    Width = 60,
                    Height = 22,
                    Minimum = 0,
                    Maximum = 255,
                    Value = value,
                    BackColor = Color.FromArgb(40, 40, 40),
                    ForeColor = Color.FromArgb(220, 220, 220),
                    BorderStyle = BorderStyle.FixedSingle,
                    TextAlign = HorizontalAlignment.Right,
                };
            }

            public ColorStopData ToData()
            {
                float p;
                if (!float.TryParse(_pos.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out p))
                    p = 0f;
                if (p < 0f) p = 0f; if (p > 1f) p = 1f;
                return new ColorStopData
                {
                    Position = p,
                    R = (byte)_r.Value,
                    G = (byte)_g.Value,
                    B = (byte)_b.Value,
                };
            }
        }
    }
}
