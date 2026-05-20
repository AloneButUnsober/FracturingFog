// Views/UserEquationDialog.cs
//
// Modeless editor for user-defined fractal equations. Auto-compiles after a
// 500 ms idle debounce so the user sees live results as they type. Compile
// errors render in red below the text box.

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

        public event Action? CompileRequested;

        public UserEquationDialog(FractalParameters parameters)
        {
            _params = parameters;

            Text = "User Equation";
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            ClientSize = new Size(520, 360);
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

            _editor = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Left = 10, Top = 32, Width = 500, Height = 240,
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
                Left = 10, Top = 280, Width = 500, Height = 60,
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
                _debounce.Stop();
                _debounce.Start();
            };

            // Trigger initial compile so default equation renders immediately.
            _params.UserEquationSource = _editor.Text;
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
