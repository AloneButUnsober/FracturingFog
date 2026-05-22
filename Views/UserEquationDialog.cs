// Views/UserEquationDialog.cs
//
// Modeless editor for user-defined fractal equations. Auto-compiles after a
// 500 ms idle debounce so the user sees live results as they type. Compile
// errors render in red below the text box.
//
// Saved equations live in UserEquationStore (JSON in %APPDATA%\FracturingFog).
// Selecting an entry from the combobox loads its source into the editor and
// triggers a compile. Save persists the current source under a user-supplied
// name (replaces on collision).

using System;
using System.Drawing;
using System.Windows.Forms;

using FracturingFog.Models;

namespace FracturingFog.Views
{
    public sealed class UserEquationDialog : Form
    {
        private readonly FractalParameters _params;
        private readonly TextBox _editor;
        private readonly Label _errorLabel;
        private readonly Timer _debounce;
        private readonly Label _hint;
        private readonly ComboBox _savedCombo;
        private readonly Button _saveBtn;
        private readonly Button _deleteBtn;
        private readonly CheckBox _promoteCheck;
        private readonly NumericUpDown _rotationBox;
        private bool _suppressComboEvent;
        private bool _suppressPromoteEvent;
        private bool _suppressRotationEvent;
        private bool _loadingNamedEquation;

        public event Action? CompileRequested;

        /// <summary>
        /// Raised when the user toggles the "Promote to fractal list" checkbox.
        /// MainForm listens to refresh the top fractal-type dropdown.
        /// </summary>
        public event Action? PromotionChanged;

        /// <summary>
        /// Raised when a render-only parameter (rotation) changes — caller
        /// should re-render without recompiling the user source.
        /// </summary>
        public event Action? RenderRequested;

        public UserEquationDialog(FractalParameters parameters)
        {
            _params = parameters;

            Text = "User Equation";
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            ClientSize = new Size(520, 475);
            BackColor = Color.FromArgb(40, 40, 40);
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 9f);

            _hint = new Label
            {
                Text = "Step(Complex z, Complex c, int n) → Complex. Example:  return z*z + c;",
                Left = 10, Top = 10, AutoSize = true,
                ForeColor = Color.FromArgb(180, 180, 180)
            };
            Controls.Add(_hint);

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

            // ── Rotation row ──────────────────────────────────────────────────
            var rotLabel = new Label
            {
                Text = "Rotation°:",
                Left = 10, Top = 88, AutoSize = true,
                ForeColor = Color.White
            };
            Controls.Add(rotLabel);

            _rotationBox = new NumericUpDown
            {
                Left = 75, Top = 85, Width = 70,
                Minimum = -360m, Maximum = 360m, DecimalPlaces = 1, Increment = 1m,
                Value = (decimal)Math.Clamp(parameters.UserEquationRotationDegrees, -360.0, 360.0),
                BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White
            };
            _rotationBox.ValueChanged += OnRotationChanged;
            Controls.Add(_rotationBox);

            var rotPlus90 = new Button
            {
                Text = "+90°", Left = 152, Top = 84, Width = 50, Height = 24,
                BackColor = Color.FromArgb(70, 70, 70), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            rotPlus90.Click += (_, _) => BumpRotation(90.0);
            Controls.Add(rotPlus90);

            var rotMinus90 = new Button
            {
                Text = "-90°", Left = 206, Top = 84, Width = 50, Height = 24,
                BackColor = Color.FromArgb(70, 70, 70), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            rotMinus90.Click += (_, _) => BumpRotation(-90.0);
            Controls.Add(rotMinus90);

            var rotReset = new Button
            {
                Text = "Reset", Left = 260, Top = 84, Width = 60, Height = 24,
                BackColor = Color.FromArgb(70, 70, 70), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            rotReset.Click += (_, _) => SetRotation(0.0);
            Controls.Add(rotReset);

            // ── Editor ────────────────────────────────────────────────────────
            _editor = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Left = 10, Top = 115, Width = 500, Height = 240,
                BackColor = Color.FromArgb(28, 28, 28),
                ForeColor = Color.White,
                Font = new Font("Consolas", 10f),
                AcceptsReturn = true,
                AcceptsTab = true,
                Text = string.IsNullOrWhiteSpace(parameters.UserEquationSource)
                    ? "return z*z + c;"
                    : parameters.UserEquationSource
            };
            Controls.Add(_editor);

            _errorLabel = new Label
            {
                Left = 10, Top = 365, Width = 500, Height = 80,
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
                _params.UserEquationSource = _editor.Text;
                CompileRequested?.Invoke();
            };
            _editor.TextChanged += (_, _) =>
            {
                // Manual edits dissociate the source from any named saved entry;
                // selection-driven loads suppress this via _loadingNamedEquation.
                if (!_loadingNamedEquation) _params.UserEquationName = null;
                _debounce.Stop();
                _debounce.Start();
            };

            _savedCombo.SelectedIndexChanged += OnSavedSelectionChanged;

            // Trigger initial compile so default equation renders immediately.
            _params.UserEquationSource = _editor.Text;

            // Load saved equations from disk and populate combo.
            UserEquationStore.Instance.Load();
            RefreshSavedCombo(selectFirst: false, selectName: _params.UserEquationName);
        }

        private void RefreshSavedCombo(bool selectFirst, string? selectName = null)
        {
            _suppressComboEvent = true;
            try
            {
                _savedCombo.Items.Clear();
                foreach (var e in UserEquationStore.Instance.Equations)
                    _savedCombo.Items.Add(e.Name);

                if (!string.IsNullOrEmpty(selectName) && _savedCombo.Items.Contains(selectName))
                {
                    _savedCombo.SelectedItem = selectName;
                }
                else if (selectFirst && _savedCombo.Items.Count > 0)
                {
                    _savedCombo.SelectedIndex = 0;
                }
                else
                {
                    _savedCombo.SelectedIndex = -1;
                }
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
                    var entry = UserEquationStore.Instance.GetByName(name);
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
            if (UserEquationStore.Instance.SetPromoted(name, _promoteCheck.Checked))
                PromotionChanged?.Invoke();
        }

        private void OnRotationChanged(object? sender, EventArgs e)
        {
            if (_suppressRotationEvent) return;
            _params.UserEquationRotationDegrees = (double)_rotationBox.Value;
            RenderRequested?.Invoke();
        }

        private void SetRotation(double degrees)
        {
            decimal clamped = (decimal)Math.Clamp(degrees, -360.0, 360.0);
            _suppressRotationEvent = true;
            try { _rotationBox.Value = clamped; }
            finally { _suppressRotationEvent = false; }
            _params.UserEquationRotationDegrees = (double)clamped;
            RenderRequested?.Invoke();
        }

        private void BumpRotation(double delta)
        {
            double next = _params.UserEquationRotationDegrees + delta;
            // Wrap into [-360, 360]; preserve sign so the spinner doesn't snap.
            while (next > 360.0) next -= 360.0;
            while (next < -360.0) next += 360.0;
            SetRotation(next);
        }

        private void OnSavedSelectionChanged(object? sender, EventArgs e)
        {
            if (_suppressComboEvent) return;
            if (_savedCombo.SelectedItem is not string name) return;

            var entry = UserEquationStore.Instance.GetByName(name);
            if (entry == null) return;

            _loadingNamedEquation = true;
            try { _editor.Text = entry.Source; }
            finally { _loadingNamedEquation = false; }
            _params.UserEquationSource = entry.Source;
            _params.UserEquationName = entry.Name;
            SyncPromoteCheckbox();
            // Compile immediately rather than waiting for the debounce.
            _debounce.Stop();
            CompileRequested?.Invoke();
        }

        private void OnSaveClick(object? sender, EventArgs e)
        {
            string defaultName = _savedCombo.SelectedItem as string ?? string.Empty;
            string? name = PromptForName("Save equation as:", defaultName);
            if (string.IsNullOrWhiteSpace(name)) return;

            var entry = UserEquationStore.Instance.SaveEquation(name.Trim(), _editor.Text);
            if (entry == null) return;

            _params.UserEquationName = entry.Name;
            RefreshSavedCombo(selectFirst: false, selectName: entry.Name);
        }

        private void OnDeleteClick(object? sender, EventArgs e)
        {
            if (_savedCombo.SelectedItem is not string name) return;

            var confirm = MessageBox.Show(
                this,
                $"Delete saved equation '{name}'?",
                "Confirm delete",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            UserEquationStore.Instance.Remove(name);
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

        /// <summary>Fires CompileRequested so the host can compile the current source.</summary>
        public void TriggerCompile() => CompileRequested?.Invoke();

        /// <summary>
        /// Selects the named saved equation in the combo and loads its source into
        /// the editor. Used by MainForm when recalling a region that references a
        /// saved equation by name. No-op if the name is not in the store.
        /// </summary>
        public void LoadEquationByName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var entry = UserEquationStore.Instance.GetByName(name);
            if (entry == null) return;

            _loadingNamedEquation = true;
            try { _editor.Text = entry.Source; }
            finally { _loadingNamedEquation = false; }
            _params.UserEquationSource = entry.Source;
            _params.UserEquationName = entry.Name;
            RefreshSavedCombo(selectFirst: false, selectName: entry.Name);
            _debounce.Stop();
        }

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
