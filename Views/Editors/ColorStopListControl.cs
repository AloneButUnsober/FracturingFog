// Views/Editors/ColorStopListControl.cs
//
// Reusable editor control for List<ColorStopData>. Renders each stop as a
// horizontal row: position TextBox, color swatch (click → ColorDialog),
// R/G/B numeric TextBoxes, delete button. "Add stop" button at the bottom.
//
// Sized explicitly (no AutoSize on the UserControl) so the host form can
// guarantee the rows are visible and scrollable.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
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
        private bool _suppressChange;

        public event EventHandler? OnStopsChanged;
        public event EventHandler? OnFromFile;

        public ColorStopListControl()
        {
            BackColor = Color.FromArgb(22, 22, 22);
            Size = new Size(340, 200);

            _scroll = new Panel
            {
                Left = 0,
                Top = ButtonHeight + 4,
                Width = Width,
                Height = Height, // - ButtonHeight - 4,
                BackColor = Color.FromArgb(22, 22, 22),
                AutoScroll = true,
            };
            Controls.Add(_scroll);

            int buttonWidth = _scroll.Width / 2;
            _addButton = new Button
            {
                Text = "+ Add Stop",
                Left = 0,
                Top = 0,
                Width = buttonWidth,
                Height = ButtonHeight,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 60, 40),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            };
            _addButton.FlatAppearance.BorderColor = Color.FromArgb(70, 110, 70);
            _addButton.Click += (s, e) =>
            {
                AddRow(new ColorStopData { Position = 1f, R = 255, G = 255, B = 255 });
                RaiseChanged();
            };
            Controls.Add(_addButton);

            _fromFileButton = new Button
            {
                Text = "From Image...",
                Left = _addButton.Left + _addButton.Width + 2,
                Top = 0,
                Width = buttonWidth,
                Height = ButtonHeight,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(60, 50, 90),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            };
            _fromFileButton.FlatAppearance.BorderColor = Color.FromArgb(70, 110, 70);
            _fromFileButton.Click += (s,e) => OnFromFile(s,e);
            Controls.Add(_fromFileButton);

            Resize += (s, e) =>
            {
                _scroll.Width = Width;
                _scroll.Height = Math.Max(20, Height - ButtonHeight - 4);
                _addButton.Top = 0;
                _fromFileButton.Top = 0;
            };
        }

        public void LoadStops(IEnumerable<ColorStopData>? stops)
        {
            _suppressChange = true;
            _scroll.SuspendLayout();
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
            row.OnDeleteClicked += (s, e) =>
            {
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

        private void RaiseChanged()
        {
            if (_suppressChange) return;
            OnStopsChanged?.Invoke(this, EventArgs.Empty);
        }

        // ── Row ──────────────────────────────────────────────────────────────

        private sealed class StopRow : Panel
        {
            private readonly TextBox _pos;
            private readonly Panel _swatch;
            private readonly NumericUpDown _r, _g, _b;
            private readonly Button _del;

            public event EventHandler? OnRowChanged;
            public event EventHandler? OnDeleteClicked;

            public StopRow(ColorStopData data)
            {
                Height = 26;
                BackColor = Color.FromArgb(28, 28, 28);
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
                    using var dlg = new ColorDialog
                    {
                        FullOpen = true,
                        AnyColor = true,
                        SolidColorOnly = false,
                        Color = _swatch.BackColor,
                    };
                    if (dlg.ShowDialog(FindForm()) == DialogResult.OK)
                    {
                        _swatch.BackColor = dlg.Color;
                        _r.Value = dlg.Color.R;
                        _g.Value = dlg.Color.G;
                        _b.Value = dlg.Color.B;
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

                _del = new Button
                {
                    Text = "X",
                    Left = _b.Right + 4,
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
