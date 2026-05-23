// Views/UserBulbDialog.cs
//
// Modeless editor for user-defined 3D fractal step functions, paired with
// UserBulbCalculator. Source compiles on a debounce; camera and render knobs
// fire a render-only request (no recompile). Layout mirrors the 2D
// UserEquationDialog but adds the Mandelbulb-style camera + lighting + DE
// controls along the bottom half so the user can tune the view without
// dragging the canvas.

using System;
using System.Drawing;
using System.Windows.Forms;

using FracturingFog.Models;

namespace FracturingFog.Views
{
    public sealed class UserBulbDialog : Form
    {
        private readonly FractalParameters _params;
        private readonly TextBox _editor;
        private readonly Label _errorLabel;
        private readonly Timer _debounce;

        // Saved equations row.
        private readonly ComboBox _savedCombo;
        private readonly Button _saveBtn;
        private readonly Button _deleteBtn;
        private bool _suppressComboEvent;
        private bool _loadingNamedEquation;

        // Camera / render knobs.
        private readonly NumericUpDown _camDistBox;
        private readonly NumericUpDown _camThetaBox;
        private readonly NumericUpDown _camPhiBox;
        private readonly NumericUpDown _lightThetaBox;
        private readonly NumericUpDown _lightPhiBox;
        private readonly NumericUpDown _iterBox;
        private readonly NumericUpDown _stepsBox;
        private readonly NumericUpDown _epsBox;
        private readonly NumericUpDown _bailoutBox;
        private readonly NumericUpDown _jacHBox;
        private bool _suppressRender;

        public event Action? CompileRequested;

        /// <summary>Fires when a render-only knob changes (no recompile needed).</summary>
        public event Action? RenderRequested;

        public UserBulbDialog(FractalParameters parameters)
        {
            _params = parameters;

            Text = "User Bulb (3D)";
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            ClientSize = new Size(540, 670);
            BackColor = Color.FromArgb(40, 40, 40);
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 9f);

            var hint = new Label
            {
                Text = "Vec3 Step(Vec3 z, Vec3 c, int n) → Vec3.   z.X/.Y/.Z available.  Math.* + Vec3.Sin/Cos/Sinh/Cosh in scope.",
                Left = 10, Top = 10, AutoSize = true,
                ForeColor = Color.FromArgb(180, 180, 180)
            };
            Controls.Add(hint);

            // ── Saved equations row ───────────────────────────────────────────
            var savedLabel = new Label
            {
                Text = "Saved:",
                Left = 10, Top = 38, AutoSize = true,
                ForeColor = Color.White
            };
            Controls.Add(savedLabel);

            _savedCombo = new ComboBox
            {
                Left = 60, Top = 35, Width = 260,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            Controls.Add(_savedCombo);

            _saveBtn = new Button
            {
                Text = "Save…", Left = 330, Top = 34, Width = 80, Height = 24,
                BackColor = Color.FromArgb(70, 70, 70), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _saveBtn.Click += OnSaveClick;
            Controls.Add(_saveBtn);

            _deleteBtn = new Button
            {
                Text = "Delete", Left = 420, Top = 34, Width = 80, Height = 24,
                BackColor = Color.FromArgb(70, 70, 70), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _deleteBtn.Click += OnDeleteClick;
            Controls.Add(_deleteBtn);

            // ── Editor ────────────────────────────────────────────────────────
            _editor = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Left = 10, Top = 65, Width = 520, Height = 200,
                BackColor = Color.FromArgb(28, 28, 28),
                ForeColor = Color.White,
                Font = new Font("Consolas", 10f),
                AcceptsReturn = true,
                AcceptsTab = true,
                Text = string.IsNullOrWhiteSpace(parameters.UserBulbSource)
                    ? DefaultSource
                    : parameters.UserBulbSource
            };
            Controls.Add(_editor);

            _errorLabel = new Label
            {
                Left = 10, Top = 270, Width = 520, Height = 50,
                ForeColor = Color.FromArgb(255, 100, 100),
                BackColor = Color.Transparent,
                Font = new Font("Consolas", 8f),
                TextAlign = ContentAlignment.TopLeft,
                AutoEllipsis = true
            };
            Controls.Add(_errorLabel);

            _debounce = new Timer { Interval = 500 };
            _debounce.Tick += (_, _) =>
            {
                _debounce.Stop();
                _params.UserBulbSource = _editor.Text;
                CompileRequested?.Invoke();
            };
            _editor.TextChanged += (_, _) =>
            {
                // Manual edits dissociate the source from any named saved entry;
                // selection-driven loads suppress this via _loadingNamedEquation.
                if (!_loadingNamedEquation) _params.UserBulbName = null;
                _debounce.Stop();
                _debounce.Start();
            };

            _savedCombo.SelectedIndexChanged += OnSavedSelectionChanged;

            _params.UserBulbSource = _editor.Text;

            // Load saved bulbs from disk and populate combo.
            UserBulbStore.Instance.Load();
            RefreshSavedCombo(selectFirst: false, selectName: _params.UserBulbName);

            // ── Camera group ──────────────────────────────────────────────────
            int gy = 325;
            AddGroupHeader("Camera", 10, gy);
            gy += 22;

            _camDistBox = AddLabeledNumeric("Distance:", 10, gy, 0.1m, 50m, 0.1m, (decimal)_params.UserBulbCameraDistance, 2);
            _camThetaBox = AddLabeledNumeric("Theta°:",  180, gy, -360m, 360m, 5m, RadToDeg(_params.UserBulbCameraTheta), 1);
            _camPhiBox = AddLabeledNumeric("Phi°:",      360, gy, 1m, 179m, 5m, RadToDeg(_params.UserBulbCameraPhi), 1);
            gy += 30;

            _lightThetaBox = AddLabeledNumeric("Light θ°:", 10, gy, -360m, 360m, 5m, RadToDeg(_params.UserBulbLightTheta), 1);
            _lightPhiBox = AddLabeledNumeric("Light φ°:",  180, gy, 1m, 179m, 5m, RadToDeg(_params.UserBulbLightPhi), 1);

            var resetCam = new Button
            {
                Text = "Reset cam", Left = 360, Top = gy - 3, Width = 90, Height = 24,
                BackColor = Color.FromArgb(70, 70, 70), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            resetCam.Click += (_, _) => ResetCamera();
            Controls.Add(resetCam);
            gy += 35;

            // ── Render group ──────────────────────────────────────────────────
            AddGroupHeader("Render", 10, gy);
            gy += 22;

            _iterBox  = AddLabeledNumeric("Iterations:", 10, gy, 2m, 64m, 1m, _params.UserBulbIterations, 0);
            _stepsBox = AddLabeledNumeric("Max steps:",  180, gy, 16m, 512m, 8m, _params.UserBulbMaxSteps, 0);
            _bailoutBox = AddLabeledNumeric("Bailout:",  360, gy, 1m, 100m, 0.5m, (decimal)_params.UserBulbBailout, 1);
            gy += 30;

            _epsBox = AddLabeledNumeric("Epsilon:", 10, gy, 0.00001m, 0.1m, 0.0005m, (decimal)_params.UserBulbEpsilon, 5);
            _jacHBox = AddLabeledNumeric("Jac h:",  180, gy, 0.0000001m, 0.01m, 0.00005m, (decimal)_params.UserBulbJacobianH, 7);

            // Wire all knobs → RenderRequested.
            _camDistBox.ValueChanged   += OnCameraChanged;
            _camThetaBox.ValueChanged  += OnCameraChanged;
            _camPhiBox.ValueChanged    += OnCameraChanged;
            _lightThetaBox.ValueChanged += OnCameraChanged;
            _lightPhiBox.ValueChanged  += OnCameraChanged;
            _iterBox.ValueChanged      += OnRenderChanged;
            _stepsBox.ValueChanged     += OnRenderChanged;
            _epsBox.ValueChanged       += OnRenderChanged;
            _bailoutBox.ValueChanged   += OnRenderChanged;
            _jacHBox.ValueChanged      += OnRenderChanged;
        }

        private const string DefaultSource =
            "// Square-triplex Mandelbulb-lite: a 3D Mandelbrot analogue using\n" +
            "// per-component products. Replace freely.\n" +
            "return new Vec3(\n" +
            "    z.X*z.X - z.Y*z.Y - z.Z*z.Z,\n" +
            "    2*z.X*z.Y,\n" +
            "    2*z.X*z.Z) + c;";

        private static decimal RadToDeg(double r) => (decimal)(r * 180.0 / Math.PI);
        private static double DegToRad(decimal d) => (double)d * Math.PI / 180.0;

        private void AddGroupHeader(string text, int left, int top)
        {
            var lbl = new Label
            {
                Text = text,
                Left = left, Top = top, AutoSize = true,
                ForeColor = Color.FromArgb(200, 200, 255),
                Font = new Font(Font, FontStyle.Bold)
            };
            Controls.Add(lbl);
        }

        private NumericUpDown AddLabeledNumeric(
            string labelText, int left, int top,
            decimal min, decimal max, decimal step,
            decimal value, int decimals)
        {
            var lbl = new Label
            {
                Text = labelText,
                Left = left, Top = top + 3, AutoSize = true,
                ForeColor = Color.White
            };
            Controls.Add(lbl);

            var num = new NumericUpDown
            {
                Left = left + 75, Top = top, Width = 80,
                Minimum = min, Maximum = max, Increment = step,
                DecimalPlaces = decimals,
                Value = Math.Clamp(value, min, max),
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White
            };
            Controls.Add(num);
            return num;
        }

        private void OnCameraChanged(object? sender, EventArgs e)
        {
            if (_suppressRender) return;
            _params.UserBulbCameraDistance = (double)_camDistBox.Value;
            _params.UserBulbCameraTheta = DegToRad(_camThetaBox.Value);
            _params.UserBulbCameraPhi = DegToRad(_camPhiBox.Value);
            _params.UserBulbLightTheta = DegToRad(_lightThetaBox.Value);
            _params.UserBulbLightPhi = DegToRad(_lightPhiBox.Value);
            RenderRequested?.Invoke();
        }

        private void OnRenderChanged(object? sender, EventArgs e)
        {
            if (_suppressRender) return;
            _params.UserBulbIterations = (int)_iterBox.Value;
            _params.UserBulbMaxSteps = (int)_stepsBox.Value;
            _params.UserBulbEpsilon = (double)_epsBox.Value;
            _params.UserBulbBailout = (double)_bailoutBox.Value;
            _params.UserBulbJacobianH = (double)_jacHBox.Value;
            RenderRequested?.Invoke();
        }

        private void ResetCamera()
        {
            _suppressRender = true;
            try
            {
                _camDistBox.Value = 3.0m;
                _camThetaBox.Value = RadToDeg(Math.PI * 0.25);
                _camPhiBox.Value = RadToDeg(Math.PI * 0.35);
                _lightThetaBox.Value = RadToDeg(Math.PI * 0.25);
                _lightPhiBox.Value = RadToDeg(Math.PI * 0.45);
            }
            finally { _suppressRender = false; }
            OnCameraChanged(null, EventArgs.Empty);
        }

        private void RefreshSavedCombo(bool selectFirst, string? selectName = null)
        {
            _suppressComboEvent = true;
            try
            {
                _savedCombo.Items.Clear();
                foreach (var e in UserBulbStore.Instance.Equations)
                    _savedCombo.Items.Add(e.Name);

                if (!string.IsNullOrEmpty(selectName) && _savedCombo.Items.Contains(selectName))
                    _savedCombo.SelectedItem = selectName;
                else if (selectFirst && _savedCombo.Items.Count > 0)
                    _savedCombo.SelectedIndex = 0;
                else
                    _savedCombo.SelectedIndex = -1;
            }
            finally { _suppressComboEvent = false; }
        }

        private void OnSavedSelectionChanged(object? sender, EventArgs e)
        {
            if (_suppressComboEvent) return;
            if (_savedCombo.SelectedItem is not string name) return;

            var entry = UserBulbStore.Instance.GetByName(name);
            if (entry == null) return;

            _loadingNamedEquation = true;
            try { _editor.Text = entry.Source; }
            finally { _loadingNamedEquation = false; }
            _params.UserBulbSource = entry.Source;
            _params.UserBulbName = entry.Name;
            _debounce.Stop();
            CompileRequested?.Invoke();
        }

        private void OnSaveClick(object? sender, EventArgs e)
        {
            string defaultName = _savedCombo.SelectedItem as string ?? string.Empty;
            string? name = PromptForName("Save bulb equation as:", defaultName);
            if (string.IsNullOrWhiteSpace(name)) return;

            var entry = UserBulbStore.Instance.SaveEquation(name.Trim(), _editor.Text);
            if (entry == null) return;

            _params.UserBulbName = entry.Name;
            RefreshSavedCombo(selectFirst: false, selectName: entry.Name);
        }

        private void OnDeleteClick(object? sender, EventArgs e)
        {
            if (_savedCombo.SelectedItem is not string name) return;

            var confirm = MessageBox.Show(
                this,
                $"Delete saved bulb equation '{name}'?",
                "Confirm delete",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            UserBulbStore.Instance.Remove(name);
            RefreshSavedCombo(selectFirst: false);
        }

        private string? PromptForName(string caption, string defaultValue)
        {
            using var dlg = new Form
            {
                Text = caption,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(320, 110),
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = false,
                TopMost = true,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.White,
                Font = Font
            };

            var tb = new TextBox
            {
                Left = 12, Top = 15, Width = 296,
                Text = defaultValue,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            var ok = new Button
            {
                Text = "OK", Left = 142, Top = 60, Width = 80,
                DialogResult = DialogResult.OK,
                BackColor = Color.FromArgb(70, 70, 70), FlatStyle = FlatStyle.Flat
            };
            var cancel = new Button
            {
                Text = "Cancel", Left = 228, Top = 60, Width = 80,
                DialogResult = DialogResult.Cancel,
                BackColor = Color.FromArgb(70, 70, 70), FlatStyle = FlatStyle.Flat
            };

            dlg.Controls.Add(tb);
            dlg.Controls.Add(ok);
            dlg.Controls.Add(cancel);
            dlg.AcceptButton = ok;
            dlg.CancelButton = cancel;

            return dlg.ShowDialog(this) == DialogResult.OK ? tb.Text : null;
        }

        /// <summary>
        /// Selects the named saved equation in the combo and loads its source into
        /// the editor.  Used by MainForm when recalling a region that references a
        /// saved bulb equation by name.  No-op if the name is not in the store.
        /// </summary>
        public void LoadEquationByName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var entry = UserBulbStore.Instance.GetByName(name);
            if (entry == null) return;

            _loadingNamedEquation = true;
            try { _editor.Text = entry.Source; }
            finally { _loadingNamedEquation = false; }
            _params.UserBulbSource = entry.Source;
            _params.UserBulbName = entry.Name;
            RefreshSavedCombo(selectFirst: false, selectName: entry.Name);
            _debounce.Stop();
        }

        /// <summary>Fires CompileRequested so the host can compile the current source.</summary>
        public void TriggerCompile() => CompileRequested?.Invoke();

        public void ShowError(string error)
        {
            _errorLabel.Text = string.IsNullOrEmpty(error) ? "✓ Compiled" : error;
            _errorLabel.ForeColor = string.IsNullOrEmpty(error)
                ? Color.FromArgb(100, 255, 100)
                : Color.FromArgb(255, 100, 100);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _debounce.Stop();
            _debounce.Dispose();
            base.OnFormClosed(e);
        }
    }
}
