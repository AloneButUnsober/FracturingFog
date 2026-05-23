// Views/Editors/LightSourceControl.cs
//
// Reusable GroupBox-shaped editor for a single LightSourceData (used by
// Phong3D + Pbr3D themes). Renders direction (Lx/Ly/Lz), diffuse/specular
// RGB triplets, and shininess. Fires OnChanged on every edit.

using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

using FracturingFog.Models;

namespace FracturingFog.Views.Editors
{
    public sealed class LightSourceControl : GroupBox
    {
        private readonly NumericUpDown _lx, _ly, _lz;
        private readonly Panel _diffSwatch, _specSwatch;
        private readonly NumericUpDown _dr, _dg, _db;
        private readonly NumericUpDown _sr, _sg, _sb;
        private readonly NumericUpDown _shininess;
        private bool _suppress;

        public event EventHandler? OnChanged;

        public LightSourceControl(string title)
        {
            Text = title;
            ForeColor = Color.FromArgb(155, 155, 155);
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            BackColor = Color.FromArgb(22, 22, 22);
            Width = 360;
            Height = 170;

            // ── Row 1: Direction X Y Z ─────────────────────────────────────
            int top = 22;
            AddLabel("Dir:", 8, top + 3);

            AddLabel("X", 65, top + 3);
            _lx = MakeFloat(85, top);
            AddLabel("Y", _lx.Right + 8, top + 3);
            _ly = MakeFloat(_lx.Right + 24, top);
            AddLabel("Z", _ly.Right + 8, top + 3);
            _lz = MakeFloat(_ly.Right + 24, top);
            Controls.Add(_lx); Controls.Add(_ly); Controls.Add(_lz);

            // ── Row 2: Diffuse swatch + RGB ────────────────────────────────
            top += 30;
            AddLabel("Diffuse:", 8, top + 3);
            _diffSwatch = MakeSwatch(_lx.Left, top);
            _dr = MakeColor(_diffSwatch.Right + 8, top);
            _dg = MakeColor(_dr.Right + 8, top);
            _db = MakeColor(_dg.Right + 8, top);
            Controls.Add(_diffSwatch); Controls.Add(_dr); Controls.Add(_dg); Controls.Add(_db);
            WireSwatch(_diffSwatch, _dr, _dg, _db);

            // ── Row 3: Specular swatch + RGB ───────────────────────────────
            top += 30;
            AddLabel("Specular:", 8, top + 3);
            _specSwatch = MakeSwatch(_lx.Left, top);
            _sr = MakeColor(_specSwatch.Right + 8, top);
            _sg = MakeColor(_sr.Right + 8, top);
            _sb = MakeColor(_sg.Right + 8, top);
            Controls.Add(_specSwatch); Controls.Add(_sr); Controls.Add(_sg); Controls.Add(_sb);
            WireSwatch(_specSwatch, _sr, _sg, _sb);

            // ── Row 4: Shininess ───────────────────────────────────────────
            top += 30;
            AddLabel("Shininess:", 8, top + 3);
            _shininess = new NumericUpDown
            {
                Left = _lx.Left,
                Top = top,
                Width = 80,
                Height = 24,
                Minimum = 1,
                Maximum = 512,
                Value = 32,
                Increment = 1,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.FromArgb(220, 220, 220),
                BorderStyle = BorderStyle.FixedSingle,
                TextAlign = HorizontalAlignment.Right,
            };
            _shininess.ValueChanged += (s, e) => Raise();
            Controls.Add(_shininess);

            Height = top + _shininess.Height + 14;
        }

        public void Load(LightSourceData? data)
        {
            _suppress = true;
            if (data == null) data = new LightSourceData { Lx = 0, Ly = 0, Lz = 1, DiffR = 1, DiffG = 1, DiffB = 1, Shininess = 32f };
            _lx.Value = ClampDir(data.Lx);
            _ly.Value = ClampDir(data.Ly);
            _lz.Value = ClampDir(data.Lz);
            _dr.Value = ClampByte(data.DiffR);
            _dg.Value = ClampByte(data.DiffG);
            _db.Value = ClampByte(data.DiffB);
            _sr.Value = ClampByte(data.SpecR);
            _sg.Value = ClampByte(data.SpecG);
            _sb.Value = ClampByte(data.SpecB);
            _shininess.Value = (decimal)Math.Clamp(data.Shininess, 1f, 512f);
            UpdateSwatches();
            _suppress = false;
        }

        public LightSourceData Save()
        {
            return new LightSourceData
            {
                Lx = (float)_lx.Value,
                Ly = (float)_ly.Value,
                Lz = (float)_lz.Value,
                DiffR = (float)_dr.Value / 255f,
                DiffG = (float)_dg.Value / 255f,
                DiffB = (float)_db.Value / 255f,
                SpecR = (float)_sr.Value / 255f,
                SpecG = (float)_sg.Value / 255f,
                SpecB = (float)_sb.Value / 255f,
                Shininess = (float)_shininess.Value,
            };
        }

        private void Raise()
        {
            if (_suppress) return;
            OnChanged?.Invoke(this, EventArgs.Empty);
        }

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

        private NumericUpDown MakeFloat(int left, int top)
        {
            var n = new NumericUpDown
            {
                Left = left,
                Top = top,
                Width = 65,
                Height = 24,
                Minimum = -10M,
                Maximum = 10M,
                DecimalPlaces = 3,
                Increment = 0.05M,
                Value = 0,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.FromArgb(220, 220, 220),
                BorderStyle = BorderStyle.FixedSingle,
                TextAlign = HorizontalAlignment.Right,
            };
            n.ValueChanged += (s, e) => Raise();
            return n;
        }

        private Panel MakeSwatch(int left, int top)
        {
            return new Panel
            {
                Left = left,
                Top = top,
                Width = 45,
                Height = 24,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.White,
                Cursor = Cursors.Hand,
            };
        }

        private NumericUpDown MakeColor(int left, int top)
        {
            var n = new NumericUpDown
            {
                Left = left,
                Top = top,
                Width = 55,
                Height = 24,
                Minimum = 0,
                Maximum = 255,
                Value = 255,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.FromArgb(220, 220, 220),
                BorderStyle = BorderStyle.FixedSingle,
                TextAlign = HorizontalAlignment.Right,
            };
            n.ValueChanged += (s, e) => { UpdateSwatches(); Raise(); };
            return n;
        }

        private void WireSwatch(Panel sw, NumericUpDown r, NumericUpDown g, NumericUpDown b)
        {
            sw.Click += (s, e) =>
            {
                using var dlg = new ColorDialog { FullOpen = true, AnyColor = true, Color = sw.BackColor };
                if (dlg.ShowDialog(FindForm()) == DialogResult.OK)
                {
                    r.Value = dlg.Color.R; g.Value = dlg.Color.G; b.Value = dlg.Color.B;
                    UpdateSwatches();
                    Raise();
                }
            };
        }

        private void UpdateSwatches()
        {
            _diffSwatch.BackColor = Color.FromArgb((int)_dr.Value, (int)_dg.Value, (int)_db.Value);
            _specSwatch.BackColor = Color.FromArgb((int)_sr.Value, (int)_sg.Value, (int)_sb.Value);
        }

        private static decimal ClampByte(float channel01)
        {
            int v = (int)Math.Round(Math.Clamp(channel01, 0f, 1f) * 255f);
            return v;
        }

        private static decimal ClampDir(float v)
        {
            if (v < -10f) return -10M;
            if (v > 10f) return 10M;
            return (decimal)Math.Round(v, 3);
        }
    }
}
