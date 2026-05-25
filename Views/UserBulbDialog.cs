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
        private readonly CheckBox _promoteCheck;
        private bool _suppressComboEvent;
        private bool _suppressPromoteEvent;
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
        private readonly NumericUpDown _cullBox;
        private readonly ComboBox _deModeBox;
        private readonly ComboBox _backendBox;
        private readonly ComboBox _axisModeBox;
        private readonly NumericUpDown _quatSliceWBox;
        private readonly Label _hintLabel;
        private readonly Panel _paramsPanel;
        private readonly Button _addParamBtn;
        private readonly Button _animPlayBtn;
        private readonly NumericUpDown _animSpeedBox;
        private readonly NumericUpDown _animTimeBox;
        private readonly Timer _animTimer;
        // Animation gating: timer fires at 30 Hz but raymarch can take seconds.
        // Without this guard the next tick cancels the in-flight calc (via
        // MainForm._calcCts.Cancel inside TriggerCalculation), so no frame ever
        // completes. MainForm calls NotifyRenderDone() when the upload lands,
        // clearing the flag for the next tick.
        private volatile bool _renderInFlight;
        public void NotifyRenderDone() => _renderInFlight = false;
        private readonly CheckBox _juliaModeBox;
        private readonly NumericUpDown _juliaCXBox, _juliaCYBox, _juliaCZBox, _juliaCWBox;
        private readonly ComboBox _colorDriverBox;
        private readonly NumericUpDown _trapXBox, _trapYBox, _trapZBox;
        private readonly ComboBox _iterAxisBox;
        private readonly NumericUpDown _l1iBox, _l2iBox, _l3iBox;
        private readonly NumericUpDown _aoBox, _fogBox;
        private readonly NumericUpDown _fovBox;
        private readonly CheckBox _clipBox;
        private readonly ComboBox _ssBox;
        private readonly Panel _chainPanel;
        private readonly Button _addChainBtn;
        private readonly Button _exportMeshBtn;

        /// <summary>Host invokes mesh export. Wires to UserBulbCalculator.</summary>
        public event Action<int, double, string>? ExportMeshRequested;
        private bool _suppressRender;

        public event Action? CompileRequested;

        /// <summary>Fires when a render-only knob changes (no recompile needed).</summary>
        public event Action? RenderRequested;

        /// <summary>Fires when an entry's Promoted flag is toggled.</summary>
        public event Action? PromotionChanged;

        public UserBulbDialog(FractalParameters parameters)
        {
            _params = parameters;

            Text = "User Bulb (3D)";
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            ClientSize = new Size(1100, 920);
            BackColor = Color.FromArgb(40, 40, 40);
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 9f);

            _hintLabel = new Label
            {
                Text = HintFor(_params.UserBulbAxisMode),
                Left = 10, Top = 10, AutoSize = true,
                ForeColor = Color.FromArgb(180, 180, 180)
            };
            Controls.Add(_hintLabel);

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

            var importBtn = new Button
            {
                Text = "Import…", Left = 330, Top = 60, Width = 80, Height = 22,
                BackColor = Color.FromArgb(70, 70, 70), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            importBtn.Click += OnImportFbulbClick;
            Controls.Add(importBtn);

            var exportBtn = new Button
            {
                Text = "Export…", Left = 420, Top = 60, Width = 80, Height = 22,
                BackColor = Color.FromArgb(70, 70, 70), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            exportBtn.Click += OnExportFbulbClick;
            Controls.Add(exportBtn);

            _promoteCheck = new CheckBox
            {
                Text = "Promote to fractal list",
                Left = 60, Top = 62, AutoSize = true,
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Enabled = false,
            };
            _promoteCheck.CheckedChanged += OnPromoteChanged;
            Controls.Add(_promoteCheck);

            // ── Editor ────────────────────────────────────────────────────────
            _editor = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Left = 10, Top = 90, Width = 520, Height = 200,
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
                Left = 10, Top = 295, Width = 520, Height = 50,
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
            int gy = 350;
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
                Text = "Reset cam", Left = 430, Top = gy - 1, Width = 90, Height = 24,
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
            _bailoutBox = AddLabeledNumeric("Bailout:",  180, gy, 1m, 100m, 0.5m, (decimal)_params.UserBulbBailout, 1);
            _stepsBox = AddLabeledNumeric("Max steps:", 360, gy, 16m, 512m, 8m, _params.UserBulbMaxSteps, 0);
            gy += 30;

            _epsBox = AddLabeledNumeric("Epsilon:", 10, gy, 0.00001m, 0.1m, 0.0005m, (decimal)_params.UserBulbEpsilon, 5);
            _jacHBox = AddLabeledNumeric("Jac h:",  180, gy, 0.0000001m, 0.01m, 0.00005m, (decimal)_params.UserBulbJacobianH, 7);
            _cullBox = AddLabeledNumeric("Cull r:", 360, gy, 0.1m, 50m, 0.25m, (decimal)_params.UserBulbCullRadius, 2);
            gy += 30;

            var deModeLbl = new Label
            {
                Text = "DE mode:", Left = 10, Top = gy + 3, AutoSize = true,
                ForeColor = Color.White
            };
            Controls.Add(deModeLbl);
            _deModeBox = new ComboBox
            {
                Left = 85, Top = gy, Width = 100,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _deModeBox.Items.AddRange(new object[] { "Auto", "Analytic", "Numerical" });
            _deModeBox.SelectedIndex = (int)_params.UserBulbDEMode;
            Controls.Add(_deModeBox);

            var backendLbl = new Label
            {
                Text = "Backend:", Left = 200, Top = gy + 3, AutoSize = true,
                ForeColor = Color.White
            };
            Controls.Add(backendLbl);
            _backendBox = new ComboBox
            {
                Left = 270, Top = gy, Width = 110,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _backendBox.Items.AddRange(new object[] { "CPU", "GPU (experimental)" });
            _backendBox.SelectedIndex = (int)_params.UserBulbBackend;
            Controls.Add(_backendBox);
            gy += 30;

            var axisLbl = new Label
            {
                Text = "Algebra:", Left = 10, Top = gy + 3, AutoSize = true,
                ForeColor = Color.White
            };
            Controls.Add(axisLbl);
            _axisModeBox = new ComboBox
            {
                Left = 85, Top = gy, Width = 100,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _axisModeBox.Items.AddRange(new object[] { "Vec3 (3D)", "Quat (4D)" });
            _axisModeBox.SelectedIndex = (int)_params.UserBulbAxisMode;
            Controls.Add(_axisModeBox);

            var sliceLbl = new Label
            {
                Text = "Slice W:", Left = 200, Top = gy + 3, AutoSize = true,
                ForeColor = Color.White
            };
            Controls.Add(sliceLbl);
            _quatSliceWBox = new NumericUpDown
            {
                Left = 260, Top = gy, Width = 80,
                Minimum = -10m, Maximum = 10m, Increment = 0.05m, DecimalPlaces = 3,
                Value = (decimal)Math.Clamp(_params.UserBulbQuatSliceW, -10.0, 10.0),
                BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White,
                Enabled = _params.UserBulbAxisMode == UserBulbAxisModeKind.Quat
            };
            Controls.Add(_quatSliceWBox);

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
            _cullBox.ValueChanged      += OnRenderChanged;
            _deModeBox.SelectedIndexChanged += OnRenderChanged;
            _backendBox.SelectedIndexChanged += OnRenderChanged;
            _quatSliceWBox.ValueChanged += OnRenderChanged;
            _axisModeBox.SelectedIndexChanged += (_, _) =>
            {
                if (_suppressRender) return;
                _params.UserBulbAxisMode = (UserBulbAxisModeKind)Math.Max(0, _axisModeBox.SelectedIndex);
                _quatSliceWBox.Enabled = _params.UserBulbAxisMode == UserBulbAxisModeKind.Quat;
                if (_juliaCWBox != null)
                    _juliaCWBox.Enabled = _params.UserBulbAxisMode == UserBulbAxisModeKind.Quat;
                _hintLabel.Text = HintFor(_params.UserBulbAxisMode);
                CompileRequested?.Invoke();
            };

            // ── Params group ──────────────────────────────────────────────────
            gy += 40;
            AddGroupHeader("Params", 10, gy);
            _addParamBtn = new Button
            {
                Text = "+ Add", Left = 80, Top = gy - 3, Width = 70, Height = 22,
                BackColor = Color.FromArgb(70, 70, 70), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _addParamBtn.Click += (_, _) =>
            {
                _params.UserBulbParams.Add(new UserBulbParam
                {
                    Name = NextFreeName(),
                    Value = 0, Min = -2, Max = 2,
                });
                RebuildParamsPanel();
                CompileRequested?.Invoke();
            };
            Controls.Add(_addParamBtn);
            gy += 22;

            _paramsPanel = new Panel
            {
                Left = 10, Top = gy, Width = 520, Height = 180,
                BackColor = Color.FromArgb(35, 35, 35),
                AutoScroll = true,
                BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(_paramsPanel);
            RebuildParamsPanel();

            // ── Animation bar ─────────────────────────────────────────────────
            gy += 190;
            AddGroupHeader("Animation (t)", 10, gy);
            gy += 22;
            _animPlayBtn = new Button
            {
                Text = "▶", Left = 10, Top = gy, Width = 36, Height = 24,
                BackColor = Color.FromArgb(70, 70, 70), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            Controls.Add(_animPlayBtn);

            var spdLbl = new Label
            {
                Text = "Speed:", Left = 56, Top = gy + 4, AutoSize = true,
                ForeColor = Color.White
            };
            Controls.Add(spdLbl);
            _animSpeedBox = new NumericUpDown
            {
                Left = 100, Top = gy, Width = 70,
                Minimum = -10m, Maximum = 10m, Increment = 0.1m, DecimalPlaces = 2,
                Value = 1m,
                BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White,
            };
            Controls.Add(_animSpeedBox);

            var tLbl = new Label
            {
                Text = "t:", Left = 180, Top = gy + 4, AutoSize = true,
                ForeColor = Color.White
            };
            Controls.Add(tLbl);
            _animTimeBox = new NumericUpDown
            {
                Left = 200, Top = gy, Width = 100,
                Minimum = -1e6m, Maximum = 1e6m, Increment = 0.1m, DecimalPlaces = 3,
                Value = (decimal)Math.Clamp(_params.UserBulbTime, -1e6, 1e6),
                BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White,
            };
            Controls.Add(_animTimeBox);

            _animTimer = new Timer { Interval = 33 }; // ~30 Hz: t advances every tick, render fires only when prior frame is done.
            _animTimer.Tick += (_, _) =>
            {
                // Advance t every tick regardless of render state so when the
                // next eligible render fires, t has accumulated wall-clock
                // motion proportional to elapsed time (not just 1 step per
                // slow frame, which looks stationary at multi-second frames).
                _params.UserBulbTime += (double)_animSpeedBox.Value * 0.033;
                _suppressRender = true;
                _animTimeBox.Value = (decimal)Math.Clamp(_params.UserBulbTime, -1e6, 1e6);
                _suppressRender = false;
                if (_renderInFlight) return; // prior frame still rendering — don't start a new one (would cancel it)
                _renderInFlight = true;
                RenderRequested?.Invoke();
            };
            _animPlayBtn.Click += (_, _) =>
            {
                if (_animTimer.Enabled)
                {
                    _animTimer.Stop();
                    _animPlayBtn.Text = "▶";
                }
                else
                {
                    _animTimer.Start();
                    _animPlayBtn.Text = "■";
                }
            };
            _animTimeBox.ValueChanged += (_, _) =>
            {
                if (_suppressRender) return;
                _params.UserBulbTime = (double)_animTimeBox.Value;
                RenderRequested?.Invoke();
            };

            // ── Julia group ───────────────────────────────────────────────────
            gy += 35;
            AddGroupHeader("Julia mode", 10, gy);
            gy += 22;
            _juliaModeBox = new CheckBox
            {
                Text = "Enable (fix c)",
                Left = 10, Top = gy + 2, AutoSize = true,
                ForeColor = Color.White, BackColor = Color.Transparent,
                Checked = _params.UserBulbJuliaMode
            };
            Controls.Add(_juliaModeBox);
            _juliaCXBox = AddJuliaNumeric("c.X:", 130, gy, _params.UserBulbJuliaCX);
            _juliaCYBox = AddJuliaNumeric("c.Y:", 230, gy, _params.UserBulbJuliaCY);
            _juliaCZBox = AddJuliaNumeric("c.Z:", 330, gy, _params.UserBulbJuliaCZ);
            _juliaCWBox = AddJuliaNumeric("c.W:", 430, gy, _params.UserBulbJuliaCW);
            _juliaCWBox.Enabled = _params.UserBulbAxisMode == UserBulbAxisModeKind.Quat;

            _juliaModeBox.CheckedChanged += (_, _) =>
            {
                if (_suppressRender) return;
                _params.UserBulbJuliaMode = _juliaModeBox.Checked;
                RenderRequested?.Invoke();
            };
            _juliaCXBox.ValueChanged += (_, _) => { if (!_suppressRender) { _params.UserBulbJuliaCX = (double)_juliaCXBox.Value; RenderRequested?.Invoke(); } };
            _juliaCYBox.ValueChanged += (_, _) => { if (!_suppressRender) { _params.UserBulbJuliaCY = (double)_juliaCYBox.Value; RenderRequested?.Invoke(); } };
            _juliaCZBox.ValueChanged += (_, _) => { if (!_suppressRender) { _params.UserBulbJuliaCZ = (double)_juliaCZBox.Value; RenderRequested?.Invoke(); } };
            _juliaCWBox.ValueChanged += (_, _) => { if (!_suppressRender) { _params.UserBulbJuliaCW = (double)_juliaCWBox.Value; RenderRequested?.Invoke(); } };

            // ── Color driver (right column) ───────────────────────────────────
            const int rx = 560;
            gy = 10;
            AddGroupHeader("Color driver", rx, gy);
            gy += 22;
            _colorDriverBox = new ComboBox
            {
                Left = rx, Top = gy, Width = 140,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _colorDriverBox.Items.AddRange(new object[]
            {
                "StepDepth", "OrbitTrap", "EscapeAngle", "FinalMagnitude", "IterComponent", "Normal"
            });
            _colorDriverBox.SelectedIndex = (int)_params.UserBulbColorDriver;
            Controls.Add(_colorDriverBox);

            _trapXBox = AddJuliaNumeric("tx:", rx + 150, gy, _params.UserBulbOrbitTrapX);
            _trapYBox = AddJuliaNumeric("ty:", rx + 230, gy, _params.UserBulbOrbitTrapY);
            _trapZBox = AddJuliaNumeric("tz:", rx + 310, gy, _params.UserBulbOrbitTrapZ);

            var iterAxisLbl = new Label
            {
                Text = "axis:", Left = rx + 390, Top = gy + 3, AutoSize = true,
                ForeColor = Color.White
            };
            Controls.Add(iterAxisLbl);
            _iterAxisBox = new ComboBox
            {
                Left = rx + 422, Top = gy, Width = 60,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _iterAxisBox.Items.AddRange(new object[] { "X", "Y", "Z" });
            _iterAxisBox.SelectedIndex = Math.Clamp(_params.UserBulbIterComponentAxis, 0, 2);
            Controls.Add(_iterAxisBox);

            _colorDriverBox.SelectedIndexChanged += (_, _) => { if (!_suppressRender) { _params.UserBulbColorDriver = (BulbColorDriver)Math.Max(0, _colorDriverBox.SelectedIndex); RenderRequested?.Invoke(); } };
            _trapXBox.ValueChanged += (_, _) => { if (!_suppressRender) { _params.UserBulbOrbitTrapX = (double)_trapXBox.Value; RenderRequested?.Invoke(); } };
            _trapYBox.ValueChanged += (_, _) => { if (!_suppressRender) { _params.UserBulbOrbitTrapY = (double)_trapYBox.Value; RenderRequested?.Invoke(); } };
            _trapZBox.ValueChanged += (_, _) => { if (!_suppressRender) { _params.UserBulbOrbitTrapZ = (double)_trapZBox.Value; RenderRequested?.Invoke(); } };
            _iterAxisBox.SelectedIndexChanged += (_, _) => { if (!_suppressRender) { _params.UserBulbIterComponentAxis = _iterAxisBox.SelectedIndex; RenderRequested?.Invoke(); } };

            // ── Lighting ──────────────────────────────────────────────────────
            gy += 35;
            AddGroupHeader("Lighting (L1/L2/L3 intensity, AO, fog)", rx, gy);
            gy += 22;
            _l1iBox = AddJuliaNumeric("L1:", rx, gy, _params.UserBulbLight1Intensity);
            _l2iBox = AddJuliaNumeric("L2:", rx + 100, gy, _params.UserBulbLight2Intensity);
            _l3iBox = AddJuliaNumeric("L3:", rx + 200, gy, _params.UserBulbLight3Intensity);
            _aoBox = new NumericUpDown
            {
                Left = rx + 310, Top = gy, Width = 70,
                Minimum = 0, Maximum = 16, Increment = 1, DecimalPlaces = 0,
                Value = _params.UserBulbAOSamples,
                BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White,
            };
            var aoLbl = new Label
            {
                Text = "AO:", Left = rx + 280, Top = gy + 3, AutoSize = true,
                ForeColor = Color.White
            };
            Controls.Add(aoLbl);
            Controls.Add(_aoBox);
            _fogBox = new NumericUpDown
            {
                Left = rx + 420, Top = gy, Width = 80,
                Minimum = 0m, Maximum = 5m, Increment = 0.05m, DecimalPlaces = 3,
                Value = (decimal)Math.Clamp(_params.UserBulbFogDensity, 0, 5),
                BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White,
            };
            var fogLbl = new Label
            {
                Text = "Fog:", Left = rx + 390, Top = gy + 3, AutoSize = true,
                ForeColor = Color.White
            };
            Controls.Add(fogLbl);
            Controls.Add(_fogBox);

            _l1iBox.ValueChanged += (_, _) => { if (!_suppressRender) { _params.UserBulbLight1Intensity = (double)_l1iBox.Value; RenderRequested?.Invoke(); } };
            _l2iBox.ValueChanged += (_, _) => { if (!_suppressRender) { _params.UserBulbLight2Intensity = (double)_l2iBox.Value; RenderRequested?.Invoke(); } };
            _l3iBox.ValueChanged += (_, _) => { if (!_suppressRender) { _params.UserBulbLight3Intensity = (double)_l3iBox.Value; RenderRequested?.Invoke(); } };
            _aoBox.ValueChanged += (_, _) => { if (!_suppressRender) { _params.UserBulbAOSamples = (int)_aoBox.Value; RenderRequested?.Invoke(); } };
            _fogBox.ValueChanged += (_, _) => { if (!_suppressRender) { _params.UserBulbFogDensity = (double)_fogBox.Value; RenderRequested?.Invoke(); } };

            // ── View ──────────────────────────────────────────────────────────
            gy += 35;
            AddGroupHeader("View (FOV / clip / SS)", rx, gy);
            gy += 22;
            var fovLbl = new Label { Text = "FOV°:", Left = rx, Top = gy + 3, AutoSize = true, ForeColor = Color.White };
            Controls.Add(fovLbl);
            _fovBox = new NumericUpDown
            {
                Left = rx + 40, Top = gy, Width = 70,
                Minimum = 5m, Maximum = 170m, Increment = 1m, DecimalPlaces = 1,
                Value = (decimal)Math.Clamp(_params.UserBulbFovDegrees, 5, 170),
                BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White,
            };
            Controls.Add(_fovBox);

            _clipBox = new CheckBox
            {
                Text = "Clip+Y", Left = rx + 130, Top = gy + 2, AutoSize = true,
                ForeColor = Color.White, BackColor = Color.Transparent,
                Checked = _params.UserBulbClipPlaneEnabled
            };
            Controls.Add(_clipBox);

            var ssLbl = new Label { Text = "SS:", Left = rx + 220, Top = gy + 3, AutoSize = true, ForeColor = Color.White };
            Controls.Add(ssLbl);
            _ssBox = new ComboBox
            {
                Left = rx + 250, Top = gy, Width = 60,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _ssBox.Items.AddRange(new object[] { "1x", "2x", "4x" });
            _ssBox.SelectedIndex = _params.UserBulbSuperSample switch { 4 => 2, 2 => 1, _ => 0 };
            Controls.Add(_ssBox);

            _fovBox.ValueChanged += (_, _) => { if (!_suppressRender) { _params.UserBulbFovDegrees = (double)_fovBox.Value; RenderRequested?.Invoke(); } };
            _clipBox.CheckedChanged += (_, _) => { if (!_suppressRender) { _params.UserBulbClipPlaneEnabled = _clipBox.Checked; RenderRequested?.Invoke(); } };
            _ssBox.SelectedIndexChanged += (_, _) =>
            {
                if (_suppressRender) return;
                _params.UserBulbSuperSample = _ssBox.SelectedIndex switch { 2 => 4, 1 => 2, _ => 1 };
                RenderRequested?.Invoke();
            };

            // ── Chain ─────────────────────────────────────────────────────────
            gy += 35;
            AddGroupHeader("Chain (when non-empty, overrides single source)", rx, gy);
            _addChainBtn = new Button
            {
                Text = "+ Step", Left = rx + 420, Top = gy - 3, Width = 70, Height = 22,
                BackColor = Color.FromArgb(70, 70, 70), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _addChainBtn.Click += (_, _) =>
            {
                _params.UserBulbChain.Add(new UserBulbChainStep
                {
                    OutputName = $"s{_params.UserBulbChain.Count}",
                    Source = "return z * z + c;"
                });
                RebuildChainPanel();
                CompileRequested?.Invoke();
            };
            Controls.Add(_addChainBtn);
            gy += 22;
            _chainPanel = new Panel
            {
                Left = rx, Top = gy, Width = 520, Height = 200,
                BackColor = Color.FromArgb(35, 35, 35),
                AutoScroll = true, BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(_chainPanel);
            RebuildChainPanel();

            // ── Export ────────────────────────────────────────────────────────
            gy += 210;
            _exportMeshBtn = new Button
            {
                Text = "Export mesh (OBJ)…", Left = rx, Top = gy, Width = 200, Height = 24,
                BackColor = Color.FromArgb(70, 70, 70), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _exportMeshBtn.Click += (_, _) => ShowExportMeshDialog();
            Controls.Add(_exportMeshBtn);
        }

        private void ShowExportMeshDialog()
        {
            using var sfd = new SaveFileDialog
            {
                Title = "Export bulb mesh",
                Filter = "OBJ mesh|*.obj",
                FileName = "bulb.obj"
            };
            if (sfd.ShowDialog(this) != DialogResult.OK) return;

            int n = 64;
            double range = 2.0;
            using var dlg = new Form
            {
                Text = "Mesh export options",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                ClientSize = new Size(280, 130),
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.White,
                Font = Font
            };
            var nLbl = new Label { Text = "Grid N:", Left = 12, Top = 15, AutoSize = true };
            var nNum = new NumericUpDown { Left = 80, Top = 12, Width = 80, Minimum = 16, Maximum = 256, Value = n,
                BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White };
            var rLbl = new Label { Text = "Range:", Left = 12, Top = 45, AutoSize = true };
            var rNum = new NumericUpDown { Left = 80, Top = 42, Width = 80, Minimum = 0.5m, Maximum = 10m,
                Increment = 0.25m, DecimalPlaces = 2, Value = (decimal)range,
                BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White };
            var ok = new Button { Text = "Export", Left = 100, Top = 90, Width = 80, DialogResult = DialogResult.OK,
                BackColor = Color.FromArgb(70, 70, 70), FlatStyle = FlatStyle.Flat };
            var cancel = new Button { Text = "Cancel", Left = 188, Top = 90, Width = 80, DialogResult = DialogResult.Cancel,
                BackColor = Color.FromArgb(70, 70, 70), FlatStyle = FlatStyle.Flat };
            dlg.Controls.AddRange(new Control[] { nLbl, nNum, rLbl, rNum, ok, cancel });
            dlg.AcceptButton = ok; dlg.CancelButton = cancel;
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            ExportMeshRequested?.Invoke((int)nNum.Value, (double)rNum.Value, sfd.FileName);
        }

        private void RebuildChainPanel()
        {
            _chainPanel.SuspendLayout();
            _chainPanel.Controls.Clear();
            int row = 0;
            foreach (var s in _params.UserBulbChain) AddChainRow(s, row++);
            _chainPanel.ResumeLayout();
        }

        private void AddChainRow(UserBulbChainStep s, int row)
        {
            int y = row * 28 + 4;
            var name = new TextBox
            {
                Left = 4, Top = y, Width = 60, Text = s.OutputName,
                BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            var src = new TextBox
            {
                Left = 68, Top = y, Width = 380, Text = s.Source,
                BackColor = Color.FromArgb(28, 28, 28), ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 9f)
            };
            var del = new Button
            {
                Left = 454, Top = y, Width = 24, Height = 22, Text = "X",
                BackColor = Color.FromArgb(90, 50, 50), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            name.Leave += (_, _) =>
            {
                if (_suppressRender) return;
                s.OutputName = name.Text.Trim();
                CompileRequested?.Invoke();
            };
            src.Leave += (_, _) =>
            {
                if (_suppressRender) return;
                s.Source = src.Text;
                CompileRequested?.Invoke();
            };
            del.Click += (_, _) =>
            {
                _params.UserBulbChain.Remove(s);
                RebuildChainPanel();
                CompileRequested?.Invoke();
            };
            _chainPanel.Controls.Add(name);
            _chainPanel.Controls.Add(src);
            _chainPanel.Controls.Add(del);
        }

        private NumericUpDown AddJuliaNumeric(string label, int left, int top, double value)
        {
            var lbl = new Label
            {
                Text = label, Left = left, Top = top + 3, AutoSize = true,
                ForeColor = Color.White
            };
            Controls.Add(lbl);
            var num = new NumericUpDown
            {
                Left = left + 32, Top = top, Width = 60,
                Minimum = -10m, Maximum = 10m, Increment = 0.01m, DecimalPlaces = 3,
                Value = (decimal)Math.Clamp(value, -10.0, 10.0),
                BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White,
            };
            Controls.Add(num);
            return num;
        }

        private string NextFreeName()
        {
            var used = new System.Collections.Generic.HashSet<string>();
            foreach (var p in _params.UserBulbParams) used.Add(p.Name);
            for (char c = 'a'; c <= 'z'; c++)
                if (!used.Contains(c.ToString())) return c.ToString();
            for (int i = 0; i < 1000; i++)
                if (!used.Contains($"p{i}")) return $"p{i}";
            return "p";
        }

        private void RebuildParamsPanel()
        {
            _paramsPanel.SuspendLayout();
            _paramsPanel.Controls.Clear();
            int row = 0;
            foreach (var p in _params.UserBulbParams)
            {
                AddParamRow(p, row++);
            }
            _paramsPanel.ResumeLayout();
        }

        private void AddParamRow(UserBulbParam p, int row)
        {
            int y = row * 26 + 4;
            var name = new TextBox
            {
                Left = 4, Top = y, Width = 60, Text = p.Name,
                BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            var value = new NumericUpDown
            {
                Left = 68, Top = y, Width = 100,
                Minimum = (decimal)p.Min, Maximum = (decimal)p.Max,
                Increment = (decimal)Math.Max((p.Max - p.Min) / 100.0, 0.001),
                DecimalPlaces = 4,
                Value = (decimal)Math.Clamp(p.Value, p.Min, p.Max),
                BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White,
            };
            var min = new NumericUpDown
            {
                Left = 172, Top = y, Width = 70,
                Minimum = -1e6m, Maximum = 1e6m, DecimalPlaces = 3, Increment = 0.5m,
                Value = (decimal)p.Min,
                BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White,
            };
            var max = new NumericUpDown
            {
                Left = 246, Top = y, Width = 70,
                Minimum = -1e6m, Maximum = 1e6m, DecimalPlaces = 3, Increment = 0.5m,
                Value = (decimal)p.Max,
                BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White,
            };
            var del = new Button
            {
                Left = 322, Top = y, Width = 24, Height = 22, Text = "X",
                BackColor = Color.FromArgb(90, 50, 50), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            value.ValueChanged += (_, _) =>
            {
                if (_suppressRender) return;
                p.Value = (double)value.Value;
                RenderRequested?.Invoke();
            };
            name.Leave += (_, _) =>
            {
                if (_suppressRender) return;
                p.Name = name.Text.Trim();
                CompileRequested?.Invoke();
            };
            min.ValueChanged += (_, _) =>
            {
                if (_suppressRender) return;
                p.Min = (double)min.Value;
                value.Minimum = (decimal)p.Min;
            };
            max.ValueChanged += (_, _) =>
            {
                if (_suppressRender) return;
                p.Max = (double)max.Value;
                value.Maximum = (decimal)p.Max;
            };
            del.Click += (_, _) =>
            {
                _params.UserBulbParams.Remove(p);
                RebuildParamsPanel();
                CompileRequested?.Invoke();
            };
            _paramsPanel.Controls.Add(name);
            _paramsPanel.Controls.Add(value);
            _paramsPanel.Controls.Add(min);
            _paramsPanel.Controls.Add(max);
            _paramsPanel.Controls.Add(del);
        }

        private static string HintFor(UserBulbAxisModeKind mode) => mode switch
        {
            UserBulbAxisModeKind.Quat =>
                "Quat Step(Quat z, Quat c, int n) → Quat.   z.W/.X/.Y/.Z available.  Math.* + Quat.* in scope.",
            _ =>
                "Vec3 Step(Vec3 z, Vec3 c, int n) → Vec3.   z.X/.Y/.Z available.  Math.* + Vec3.* in scope."
        };

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
            _params.UserBulbCullRadius = (double)_cullBox.Value;
            _params.UserBulbDEMode = (UserBulbDEModeKind)Math.Max(0, _deModeBox.SelectedIndex);
            _params.UserBulbBackend = (UserBulbBackendKind)Math.Max(0, _backendBox.SelectedIndex);
            _params.UserBulbQuatSliceW = (double)_quatSliceWBox.Value;
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
            SyncPromoteCheckbox();
        }

        private void SyncPromoteCheckbox()
        {
            _suppressPromoteEvent = true;
            try
            {
                string? name = _savedCombo.SelectedItem as string;
                if (string.IsNullOrEmpty(name))
                {
                    _promoteCheck.Enabled = false;
                    _promoteCheck.Checked = false;
                }
                else
                {
                    var entry = UserBulbStore.Instance.GetByName(name);
                    _promoteCheck.Enabled = entry != null;
                    _promoteCheck.Checked = entry?.Promoted ?? false;
                }
            }
            finally { _suppressPromoteEvent = false; }
        }

        private void OnPromoteChanged(object? sender, EventArgs e)
        {
            if (_suppressPromoteEvent) return;
            if (_savedCombo.SelectedItem is not string name) return;
            if (UserBulbStore.Instance.SetPromoted(name, _promoteCheck.Checked))
                PromotionChanged?.Invoke();
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
            SyncPromoteCheckbox();
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

        private void OnImportFbulbClick(object? sender, EventArgs e)
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Import .fbulb",
                Filter = "FracturingFog bulb|*.fbulb;*.json|All files|*.*",
            };
            if (ofd.ShowDialog(this) != DialogResult.OK) return;
            var entry = UserBulbStore.Instance.ImportEntry(ofd.FileName);
            if (entry == null)
            {
                MessageBox.Show(this, "Import failed (invalid file).", "Import", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            RefreshSavedCombo(selectFirst: false, selectName: entry.Name);
        }

        private void OnExportFbulbClick(object? sender, EventArgs e)
        {
            if (_savedCombo.SelectedItem is not string name)
            {
                MessageBox.Show(this, "Select a saved equation to export.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using var sfd = new SaveFileDialog
            {
                Title = "Export .fbulb",
                Filter = "FracturingFog bulb|*.fbulb",
                FileName = $"{name}.fbulb",
            };
            if (sfd.ShowDialog(this) != DialogResult.OK) return;
            if (!UserBulbStore.Instance.ExportEntry(name, sfd.FileName))
                MessageBox.Show(this, "Export failed.", "Export", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            _animTimer?.Stop();
            _animTimer?.Dispose();
            base.OnFormClosed(e);
        }
    }
}
