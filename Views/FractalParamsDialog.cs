// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Views/FractalParamsDialog.cs
//
// Minimal modal editor for fractal-specific parameters introduced in Phase 1.
// Show only the controls relevant to the active FractalType so the dialog
// stays uncluttered when most fractals have no tunable params.

using System;
using System.Drawing;
using System.Globalization;
using System.Numerics;
using System.Windows.Forms;

using FracturingFog.Models;

namespace FracturingFog.Views
{
    public sealed class FractalParamsDialog : Form
    {
        private readonly FractalType _type;
        private readonly FractalParameters _params;

        /// <summary>
        /// Fired whenever a control value changes so the host can re-render
        /// immediately. _params is mutated in place; the host need only
        /// trigger a calculation refresh.
        /// </summary>
        public event Action? ParamChanged;
        private bool _suppress;

        private NumericUpDown? _juliaR;
        private NumericUpDown? _juliaI;
        private NumericUpDown? _multibrotD;
        private NumericUpDown? _phoenixR;
        private NumericUpDown? _phoenixI;
        private ComboBox? _presetCombo;
        private NumericUpDown? _intParam1;   // generic int param (depth / iterations / exponent)
        private NumericUpDown? _floatA;
        private NumericUpDown? _floatB;
        private NumericUpDown? _floatC;
        private NumericUpDown? _floatD;

        public FractalParamsDialog(FractalType type, FractalParameters parameters)
        {
            _type = type;
            _params = parameters;

            Text = $"{type} Parameters";
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            StartPosition = FormStartPosition.Manual;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            ClientSize = new Size(360, 320);
            BackColor = Color.FromArgb(40, 40, 40);
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 9f);

            BuildControls();
        }

        private void BuildControls()
        {
            int y = 15;

            switch (_type)
            {
                case FractalType.Julia:
                    AddLabel("Julia c (real):", 15, y);
                    _juliaR = AddNumeric(150, y, (decimal)_params.JuliaC.Real, -2m, 2m, 0.0001m);
                    y += 32;
                    AddLabel("Julia c (imag):", 15, y);
                    _juliaI = AddNumeric(150, y, (decimal)_params.JuliaC.Imaginary, -2m, 2m, 0.0001m);
                    y += 32;
                    break;

                case FractalType.Multibrot:
                    AddLabel("Exponent d:", 15, y);
                    _multibrotD = AddNumeric(150, y, _params.MultibrotExponent, 2, 8, 1);
                    _multibrotD.DecimalPlaces = 0;
                    y += 32;
                    break;

                case FractalType.Phoenix:
                    AddLabel("p (real):", 15, y);
                    _phoenixR = AddNumeric(150, y, (decimal)_params.PhoenixP.Real, -2m, 2m, 0.0001m);
                    y += 32;
                    AddLabel("p (imag):", 15, y);
                    _phoenixI = AddNumeric(150, y, (decimal)_params.PhoenixP.Imaginary, -2m, 2m, 0.0001m);
                    y += 32;
                    break;

                case FractalType.Newton:
                case FractalType.Nova:
                    AddLabel("Exponent d:", 15, y);
                    _intParam1 = AddNumeric(150, y, _params.NewtonExponent, 2, 8, 1);
                    _intParam1.DecimalPlaces = 0;
                    y += 32;
                    AddLabel("Relaxation R:", 15, y);
                    _floatA = AddNumeric(150, y, (decimal)_params.NewtonRelaxation, 0.1m, 2.0m, 0.05m);
                    y += 32;
                    break;

                case FractalType.IFS:
                    AddLabel("Preset:", 15, y);
                    _presetCombo = AddCombo(150, y, IFSPresets.All.Keys, _params.IFSPresetName);
                    y += 32;
                    AddLabel("Iterations:", 15, y);
                    _intParam1 = AddNumeric(150, y, _params.IFSIterations, 100_000, 20_000_000, 100_000);
                    _intParam1.DecimalPlaces = 0;
                    _intParam1.ThousandsSeparator = true;
                    y += 32;
                    break;

                case FractalType.LSystem:
                    AddLabel("Preset:", 15, y);
                    _presetCombo = AddCombo(150, y, LSystemPresets.All.Keys, _params.LSystemPresetName);
                    y += 32;
                    AddLabel("Depth:", 15, y);
                    _intParam1 = AddNumeric(150, y, _params.LSystemDepth, 0, 12, 1);
                    _intParam1.DecimalPlaces = 0;
                    y += 32;
                    break;

                case FractalType.StrangeAttractor:
                    AddLabel("Preset:", 15, y);
                    _presetCombo = AddCombo(150, y, new[] { "Clifford", "De Jong", "Hopalong", "Lorenz" }, _params.AttractorPresetName);
                    y += 32;
                    AddLabel("Iterations:", 15, y);
                    _intParam1 = AddNumeric(150, y, _params.AttractorIterations, 100_000, 20_000_000, 100_000);
                    _intParam1.DecimalPlaces = 0;
                    _intParam1.ThousandsSeparator = true;
                    y += 32;
                    AddLabel("a:", 15, y); _floatA = AddNumeric(150, y, (decimal)_params.AttractorA, -3m, 3m, 0.01m); y += 28;
                    AddLabel("b:", 15, y); _floatB = AddNumeric(150, y, (decimal)_params.AttractorB, -3m, 3m, 0.01m); y += 28;
                    AddLabel("c:", 15, y); _floatC = AddNumeric(150, y, (decimal)_params.AttractorC, -3m, 3m, 0.01m); y += 28;
                    AddLabel("d:", 15, y); _floatD = AddNumeric(150, y, (decimal)_params.AttractorD, -3m, 3m, 0.01m); y += 28;
                    break;

                case FractalType.BuddhaBrot:
                    AddLabel("Samples:", 15, y);
                    _intParam1 = AddNumeric(150, y, _params.BuddhaSamples, 50_000, 50_000_000, 50_000);
                    _intParam1.DecimalPlaces = 0;
                    _intParam1.ThousandsSeparator = true;
                    y += 32;
                    AddLabel("Iter low:", 15, y);  _floatA = AddNumeric(150, y, _params.BuddhaIterLow,  50, 100_000, 50); _floatA.DecimalPlaces = 0; y += 28;
                    AddLabel("Iter mid:", 15, y);  _floatB = AddNumeric(150, y, _params.BuddhaIterMid,  100, 200_000, 100); _floatB.DecimalPlaces = 0; y += 28;
                    AddLabel("Iter high:", 15, y); _floatC = AddNumeric(150, y, _params.BuddhaIterHigh, 500, 500_000, 500); _floatC.DecimalPlaces = 0; y += 28;
                    break;

                case FractalType.Mandelbulb:
                    AddLabel("Power N:", 15, y);
                    _intParam1 = AddNumeric(150, y, (decimal)_params.BulbPower, 2m, 16m, 0.1m);
                    _intParam1.DecimalPlaces = 1;
                    y += 28;
                    AddLabel("DE iter:", 15, y);
                    _floatA = AddNumeric(150, y, _params.BulbIterations, 2, 16, 1); _floatA.DecimalPlaces = 0; y += 28;
                    AddLabel("Cam θ (azim):", 15, y);
                    _floatB = AddNumeric(150, y, (decimal)_params.BulbCameraTheta, -10m, 10m, 0.05m); y += 28;
                    AddLabel("Cam φ (elev):", 15, y);
                    _floatC = AddNumeric(150, y, (decimal)_params.BulbCameraPhi, 0.01m, 3.13m, 0.05m); y += 28;
                    AddLabel("Cam dist:", 15, y);
                    _floatD = AddNumeric(150, y, (decimal)_params.BulbCameraDistance, 1.5m, 10m, 0.1m); y += 28;
                    break;

                default:
                    var info = new Label
                    {
                        Text = $"{_type} has no tunable parameters.",
                        Left = 15,
                        Top = y,
                        AutoSize = true,
                        ForeColor = Color.FromArgb(180, 180, 180)
                    };
                    Controls.Add(info);
                    y += 32;
                    break;
            }

            var closeBtn = new Button
            {
                Text = "Close",
                Left = 270,
                Top = 280,
                Width = 70,
                BackColor = Color.FromArgb(70, 70, 70),
                FlatStyle = FlatStyle.Flat
            };
            closeBtn.Click += (_, _) => Close();
            Controls.Add(closeBtn);

            // Wire all interactive controls to fire ParamChanged live.
            WireLive(_juliaR); WireLive(_juliaI);
            WireLive(_multibrotD);
            WireLive(_phoenixR); WireLive(_phoenixI);
            WireLive(_intParam1);
            WireLive(_floatA); WireLive(_floatB); WireLive(_floatC); WireLive(_floatD);
            if (_presetCombo != null)
            {
                _presetCombo.SelectedIndexChanged += OnPresetChanged;
            }
        }

        private void WireLive(NumericUpDown? n)
        {
            if (n == null) return;
            n.ValueChanged += (_, _) =>
            {
                if (_suppress) return;
                CommitValues();
                ParamChanged?.Invoke();
            };
        }

        private void OnPresetChanged(object? sender, EventArgs e)
        {
            if (_suppress) return;

            // For Strange Attractor: load known-good default a/b/c/d for the
            // newly-selected preset so the user sees a meaningful render
            // instead of a single fixed point.
            if (_type == FractalType.StrangeAttractor
                && _presetCombo?.SelectedItem is string atName
                && _floatA != null && _floatB != null && _floatC != null && _floatD != null)
            {
                var (da, db, dc, dd) = AttractorCalculator.DefaultParams(atName);
                _suppress = true;
                try
                {
                    _floatA.Value = ClampDecimal((decimal)da, _floatA);
                    _floatB.Value = ClampDecimal((decimal)db, _floatB);
                    _floatC.Value = ClampDecimal((decimal)dc, _floatC);
                    _floatD.Value = ClampDecimal((decimal)dd, _floatD);
                }
                finally { _suppress = false; }
            }

            CommitValues();
            ParamChanged?.Invoke();
        }

        private static decimal ClampDecimal(decimal v, NumericUpDown n)
            => v < n.Minimum ? n.Minimum : (v > n.Maximum ? n.Maximum : v);

        private void CommitValues()
        {
            switch (_type)
            {
                case FractalType.Julia:
                    _params.JuliaC = new Complex((double)(_juliaR?.Value ?? 0), (double)(_juliaI?.Value ?? 0));
                    break;
                case FractalType.Multibrot:
                    _params.MultibrotExponent = (int)(_multibrotD?.Value ?? 3);
                    break;
                case FractalType.Phoenix:
                    _params.PhoenixP = new Complex((double)(_phoenixR?.Value ?? 0), (double)(_phoenixI?.Value ?? 0));
                    break;
                case FractalType.Newton:
                case FractalType.Nova:
                    if (_intParam1 != null) _params.NewtonExponent = (int)_intParam1.Value;
                    if (_floatA != null) _params.NewtonRelaxation = (double)_floatA.Value;
                    break;
                case FractalType.IFS:
                    if (_presetCombo?.SelectedItem is string ifsName) _params.IFSPresetName = ifsName;
                    if (_intParam1 != null) _params.IFSIterations = (int)_intParam1.Value;
                    _params.IFSMaps = null; // reset override so preset name takes effect
                    break;
                case FractalType.LSystem:
                    if (_presetCombo?.SelectedItem is string lsName) _params.LSystemPresetName = lsName;
                    if (_intParam1 != null) _params.LSystemDepth = (int)_intParam1.Value;
                    break;
                case FractalType.StrangeAttractor:
                    if (_presetCombo?.SelectedItem is string atName) _params.AttractorPresetName = atName;
                    if (_intParam1 != null) _params.AttractorIterations = (int)_intParam1.Value;
                    if (_floatA != null) _params.AttractorA = (double)_floatA.Value;
                    if (_floatB != null) _params.AttractorB = (double)_floatB.Value;
                    if (_floatC != null) _params.AttractorC = (double)_floatC.Value;
                    if (_floatD != null) _params.AttractorD = (double)_floatD.Value;
                    break;
                case FractalType.BuddhaBrot:
                    if (_intParam1 != null) _params.BuddhaSamples = (int)_intParam1.Value;
                    if (_floatA != null) _params.BuddhaIterLow = (int)_floatA.Value;
                    if (_floatB != null) _params.BuddhaIterMid = (int)_floatB.Value;
                    if (_floatC != null) _params.BuddhaIterHigh = (int)_floatC.Value;
                    break;
                case FractalType.Mandelbulb:
                    if (_intParam1 != null) _params.BulbPower = (double)_intParam1.Value;
                    if (_floatA != null) _params.BulbIterations = (int)_floatA.Value;
                    if (_floatB != null) _params.BulbCameraTheta = (double)_floatB.Value;
                    if (_floatC != null) _params.BulbCameraPhi = (double)_floatC.Value;
                    if (_floatD != null) _params.BulbCameraDistance = (double)_floatD.Value;
                    break;
            }
        }

        private ComboBox AddCombo(int x, int y, System.Collections.Generic.IEnumerable<string> items, string? selected)
        {
            var cb = new ComboBox
            {
                Left = x,
                Top = y,
                Width = 180,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            foreach (var s in items) cb.Items.Add(s);
            if (!string.IsNullOrEmpty(selected) && cb.Items.Contains(selected)) cb.SelectedItem = selected;
            else if (cb.Items.Count > 0) cb.SelectedIndex = 0;
            Controls.Add(cb);
            return cb;
        }

        private Label AddLabel(string text, int x, int y)
        {
            var l = new Label
            {
                Text = text,
                Left = x,
                Top = y + 3,
                AutoSize = true,
                ForeColor = Color.White
            };
            Controls.Add(l);
            return l;
        }

        private NumericUpDown AddNumeric(int x, int y, decimal value, decimal min, decimal max, decimal step)
        {
            var n = new NumericUpDown
            {
                Left = x,
                Top = y,
                Width = 150,
                Minimum = min,
                Maximum = max,
                DecimalPlaces = 5,
                Increment = step,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            try { n.Value = value; } catch { n.Value = min; }
            Controls.Add(n);
            return n;
        }
    }
}
