// Views/SandboxDialog.cs
//
// Modeless editor for the Sandbox fractal type. Mirrors UserEquationDialog
// in layout but the underlying compiler is the restricted SandboxExpression
// parser — no Roslyn, no BCL access.

using System;
using System.Drawing;
using System.Windows.Forms;

using FracturingFog.Models;

namespace FracturingFog.Views
{
    public sealed class SandboxDialog : Form
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
        private bool _suppressComboEvent;
        private bool _suppressPromoteEvent;
        private bool _loadingNamedEquation;

        public event Action? CompileRequested;

        /// <summary>
        /// Raised when the user toggles the "Promote to fractal list" checkbox.
        /// MainForm listens to refresh the top fractal-type dropdown.
        /// </summary>
        public event Action? PromotionChanged;

        public SandboxDialog(FractalParameters parameters)
        {
            _params = parameters;

            Text = "Sandbox Equation";
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            ClientSize = new Size(520, 460);
            BackColor = Color.FromArgb(40, 40, 40);
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 9f);

            _hint = new Label
            {
                Text = "Expression returns Complex. Vars: z, c, n. Funcs: sin cos tan exp log sqrt abs conj re im arg pow(a,b)." + Environment.NewLine +
                       "Const: pi, e, i. Ops: + - * / ^ unary-. Ternary: cond ? a : b. Compare: < > <= >= == !=. Logical: && || !. Let: let x = expr in body." + Environment.NewLine +
                       "Example:  let zz = z*z in zz + c",
                Left = 10, Top = 10, AutoSize = true,
                ForeColor = Color.FromArgb(180, 180, 180)
            };
            Controls.Add(_hint);

            var savedLabel = new Label
            {
                Text = "Saved:",
                Left = 10, Top = 60, AutoSize = true,
                ForeColor = Color.White
            };
            Controls.Add(savedLabel);

            _savedCombo = new ComboBox
            {
                Left = 60, Top = 57, Width = 260,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            Controls.Add(_savedCombo);

            _saveBtn = new Button
            {
                Text = "Save…", Left = 330, Top = 56, Width = 80, Height = 24,
                BackColor = Color.FromArgb(70, 70, 70), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _saveBtn.Click += OnSaveClick;
            Controls.Add(_saveBtn);

            _deleteBtn = new Button
            {
                Text = "Delete", Left = 420, Top = 56, Width = 80, Height = 24,
                BackColor = Color.FromArgb(70, 70, 70), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _deleteBtn.Click += OnDeleteClick;
            Controls.Add(_deleteBtn);

            _promoteCheck = new CheckBox
            {
                Text = "Promote to fractal list",
                Left = 60, Top = 82, AutoSize = true,
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Enabled = false,
            };
            _promoteCheck.CheckedChanged += OnPromoteChanged;
            Controls.Add(_promoteCheck);

            _editor = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Left = 10, Top = 110, Width = 500, Height = 240,
                BackColor = Color.FromArgb(28, 28, 28),
                ForeColor = Color.White,
                Font = new Font("Consolas", 10f),
                AcceptsReturn = true,
                AcceptsTab = true,
                Text = string.IsNullOrWhiteSpace(parameters.SandboxSource)
                    ? "z*z + c"
                    : parameters.SandboxSource
            };
            Controls.Add(_editor);

            _errorLabel = new Label
            {
                Left = 10, Top = 360, Width = 500, Height = 80,
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
                _params.SandboxSource = _editor.Text;
                CompileRequested?.Invoke();
            };
            _editor.TextChanged += (_, _) =>
            {
                if (!_loadingNamedEquation) _params.SandboxName = null;
                _debounce.Stop();
                _debounce.Start();
            };

            _savedCombo.SelectedIndexChanged += OnSavedSelectionChanged;

            _params.SandboxSource = _editor.Text;

            SandboxEquationStore.Instance.Load();
            RefreshSavedCombo(selectFirst: false, selectName: _params.SandboxName);
        }

        private void RefreshSavedCombo(bool selectFirst, string? selectName = null)
        {
            _suppressComboEvent = true;
            try
            {
                _savedCombo.Items.Clear();
                foreach (var e in SandboxEquationStore.Instance.Equations)
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
                    var entry = SandboxEquationStore.Instance.GetByName(name);
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
            if (SandboxEquationStore.Instance.SetPromoted(name, _promoteCheck.Checked))
                PromotionChanged?.Invoke();
        }

        private void OnSavedSelectionChanged(object? sender, EventArgs e)
        {
            if (_suppressComboEvent) return;
            if (_savedCombo.SelectedItem is not string name) return;

            var entry = SandboxEquationStore.Instance.GetByName(name);
            if (entry == null) return;

            _loadingNamedEquation = true;
            try { _editor.Text = entry.Source; }
            finally { _loadingNamedEquation = false; }
            _params.SandboxSource = entry.Source;
            _params.SandboxName = entry.Name;
            SyncPromoteCheckbox();
            _debounce.Stop();
            CompileRequested?.Invoke();
        }

        private void OnSaveClick(object? sender, EventArgs e)
        {
            string defaultName = _savedCombo.SelectedItem as string ?? string.Empty;
            string? name = PromptForName("Save sandbox equation as:", defaultName);
            if (string.IsNullOrWhiteSpace(name)) return;

            var entry = SandboxEquationStore.Instance.SaveEquation(name.Trim(), _editor.Text);
            if (entry == null) return;

            _params.SandboxName = entry.Name;
            RefreshSavedCombo(selectFirst: false, selectName: entry.Name);
        }

        private void OnDeleteClick(object? sender, EventArgs e)
        {
            if (_savedCombo.SelectedItem is not string name) return;

            var confirm = MessageBox.Show(
                this,
                $"Delete saved sandbox equation '{name}'?",
                "Confirm delete",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;

            SandboxEquationStore.Instance.Remove(name);
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

        public void TriggerCompile() => CompileRequested?.Invoke();

        public void LoadEquationByName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            var entry = SandboxEquationStore.Instance.GetByName(name);
            if (entry == null) return;

            _loadingNamedEquation = true;
            try { _editor.Text = entry.Source; }
            finally { _loadingNamedEquation = false; }
            _params.SandboxSource = entry.Source;
            _params.SandboxName = entry.Name;
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
