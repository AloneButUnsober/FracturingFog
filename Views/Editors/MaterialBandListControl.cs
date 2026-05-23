// Views/Editors/MaterialBandListControl.cs
//
// Editor for List<PbrMaterialBandData> — the piecewise metal/roughness function
// used by PBR3D themes. Each band: UpperT, Metal, Roughness. Bands are
// rendered in list order; renderer evaluates first band whose UpperT exceeds
// the t value. UI does not auto-sort; user controls order.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

using FracturingFog.Models;

namespace FracturingFog.Views.Editors
{
    public sealed class MaterialBandListControl : UserControl
    {
        private const int RowHeight = 26;
        private const int ButtonHeight = 26;
        private const int HeaderHeight = 18;

        private readonly Label _header;
        private readonly Panel _scroll;
        private readonly Button _addButton;
        private bool _suppress;

        public event EventHandler? OnChanged;

        public MaterialBandListControl()
        {
            BackColor = Color.FromArgb(22, 22, 22);
            Size = new Size(340, 180);

            _header = new Label
            {
                Text = "  UpperT   Metal   Rough",
                ForeColor = Color.FromArgb(155, 155, 155),
                Font = new Font("Consolas", 8.5f, FontStyle.Bold),
                Left = 0,
                Top = 0,
                Width = Width,
                Height = HeaderHeight,
                BackColor = Color.Transparent,
            };
            Controls.Add(_header);

            _scroll = new Panel
            {
                Left = 0,
                Top = HeaderHeight,
                Width = Width,
                Height = Height - HeaderHeight - ButtonHeight - 4,
                BackColor = Color.FromArgb(22, 22, 22),
                AutoScroll = true,
            };
            Controls.Add(_scroll);

            _addButton = new Button
            {
                Text = "+ Add Band",
                Left = 0,
                Top = _scroll.Bottom + 2,
                Width = 110,
                Height = ButtonHeight,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 60, 40),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            };
            _addButton.FlatAppearance.BorderColor = Color.FromArgb(70, 110, 70);
            _addButton.Click += (s, e) =>
            {
                AddRow(new PbrMaterialBandData { UpperT = 1.0f, Metal = 0f, Roughness = 0.7f });
                Raise();
            };
            Controls.Add(_addButton);

            Resize += (s, e) =>
            {
                _header.Width = Width;
                _scroll.Width = Width;
                _scroll.Height = Math.Max(20, Height - HeaderHeight - ButtonHeight - 4);
                _addButton.Top = _scroll.Bottom + 2;
            };
        }

        public void LoadBands(IEnumerable<PbrMaterialBandData>? bands)
        {
            _suppress = true;
            _scroll.SuspendLayout();
            foreach (Control c in _scroll.Controls) c.Dispose();
            _scroll.Controls.Clear();
            if (bands != null)
            {
                foreach (var b in bands)
                    AddRow(new PbrMaterialBandData { UpperT = b.UpperT, Metal = b.Metal, Roughness = b.Roughness });
            }
            _scroll.ResumeLayout();
            _suppress = false;
        }

        public List<PbrMaterialBandData> GetBands()
        {
            var result = new List<PbrMaterialBandData>();
            foreach (Control c in _scroll.Controls)
                if (c is BandRow row) result.Add(row.ToData());
            return result;
        }

        private void AddRow(PbrMaterialBandData data)
        {
            int y = _scroll.Controls.OfType<BandRow>().Count() * RowHeight;
            var row = new BandRow(data)
            {
                Left = 0,
                Top = y,
                Width = _scroll.ClientSize.Width - 20,
            };
            row.OnRowChanged += (s, e) => Raise();
            row.OnDeleteClicked += (s, e) =>
            {
                _scroll.Controls.Remove(row);
                row.Dispose();
                Relayout();
                Raise();
            };
            _scroll.Controls.Add(row);
        }

        private void Relayout()
        {
            int y = 0;
            foreach (Control c in _scroll.Controls)
            {
                if (c is BandRow row) { row.Top = y; y += RowHeight; }
            }
        }

        private void Raise()
        {
            if (_suppress) return;
            OnChanged?.Invoke(this, EventArgs.Empty);
        }

        private sealed class BandRow : Panel
        {
            private readonly TextBox _t, _metal, _rough;
            private readonly Button _del;

            public event EventHandler? OnRowChanged;
            public event EventHandler? OnDeleteClicked;

            public BandRow(PbrMaterialBandData data)
            {
                Height = 24;
                BackColor = Color.FromArgb(28, 28, 28);
                Margin = new Padding(0);

                _t = MakeFloat(data.UpperT, 4);
                _metal = MakeFloat(data.Metal, _t.Right + 4);
                _rough = MakeFloat(data.Roughness, _metal.Right + 4);
                Controls.Add(_t); Controls.Add(_metal); Controls.Add(_rough);

                _del = new Button
                {
                    Text = "X",
                    Left = _rough.Right + 6,
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

            private TextBox MakeFloat(float value, int left)
            {
                var tx = new TextBox
                {
                    Left = left,
                    Top = 2,
                    Width = 60,
                    Height = 22,
                    BackColor = Color.FromArgb(40, 40, 40),
                    ForeColor = Color.FromArgb(220, 220, 220),
                    Font = new Font("Consolas", 9f),
                    BorderStyle = BorderStyle.FixedSingle,
                    TextAlign = HorizontalAlignment.Right,
                    Text = value.ToString("0.###", CultureInfo.InvariantCulture),
                };
                tx.TextChanged += (s, e) => OnRowChanged?.Invoke(this, EventArgs.Empty);
                return tx;
            }

            public PbrMaterialBandData ToData()
            {
                return new PbrMaterialBandData
                {
                    UpperT = ParseFloat(_t.Text, 1f),
                    Metal = ParseFloat(_metal.Text, 0f),
                    Roughness = ParseFloat(_rough.Text, 0.7f),
                };
            }

            private static float ParseFloat(string s, float fallback)
            {
                if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) return f;
                return fallback;
            }
        }
    }
}
