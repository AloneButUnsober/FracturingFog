using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace FracturingFog.Views
{


    // ─────────────────────────────────────────────────────────────────────────────
    // Minimal text-input dialog (used by Save View)
    // ─────────────────────────────────────────────────────────────────────────────

    public sealed class InputDialog : Form
    {
        public string Input => _tx.Text;
        private readonly TextBox _tx;

        public InputDialog(string title, string prompt)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ClientSize = new Size(360, 100);
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.FromArgb(35, 35, 35);
            TopMost = true;

            Controls.Add(new Label
            {
                Text = prompt,
                Left = 12,
                Top = 14,
                AutoSize = true,
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 9f)
            });

            _tx = new TextBox
            {
                Left = 12,
                Top = 36,
                Width = 336,
                BackColor = Color.FromArgb(50, 50, 50),
                ForeColor = Color.White,
                Font = new Font("Consolas", 10f),
                BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(_tx);

            var ok = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Left = 196,
                Top = 66,
                Width = 72,
                Height = 26,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Left = 276,
                Top = 66,
                Width = 72,
                Height = 26,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(ok);
            Controls.Add(cancel);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Minimal text-input dialog (used by Save View)
    // ─────────────────────────────────────────────────────────────────────────────

    public sealed class PosterDialog : Form
    {
        public string WidthInput => _widthTx.Text;
        public string HeightInput => _heightTx.Text;

        public bool IsPortrait => _portraitCB.Checked;

        public bool RotateImage => _portraitCB.Checked;

        private readonly Label _widthLabel;
        private readonly TextBox _widthTx;

        private readonly Label _heightLabel;
        private readonly TextBox _heightTx;

        private readonly Label _posterWLabel;
        private readonly Label _posterHLabel;
        private readonly TextBox _postWTx;
        private readonly TextBox _postHTx;

        private readonly CheckBox _portraitCB;
        private readonly CheckBox _lowDefCB;
        private readonly CheckBox _medDefCB;
        private readonly CheckBox _highDefCB;

        public PosterDialog()
        {
            Text = "Poster Print";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ClientSize = new Size(340, 180);
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.FromArgb(35, 35, 35);
            TopMost = true;

            _posterHLabel = new Label
            {
                Text = "Poster Height (inches):",
                Left = 4,
                Top = 7,
                Width = 130,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 9f)
            };

            _postHTx = new TextBox
            {
                Left = 136,
                Top = 10,
                Width = 50,
                BackColor = Color.FromArgb(50, 50, 50),
                ForeColor = Color.White,
                Font = new Font("Consolas", 10f),
                BorderStyle = BorderStyle.FixedSingle
            };
            _postHTx.TextChanged += (s, e) => CalculatePixelDimensions();

            Controls.Add(_posterHLabel);
            Controls.Add(_postHTx);

            _postWTx = new TextBox
            {
                Left = 136,
                Top = 36,
                Width = 50,
                BackColor = Color.FromArgb(50, 50, 50),
                ForeColor = Color.White,
                Font = new Font("Consolas", 10f),
                BorderStyle = BorderStyle.FixedSingle
            };
            _postWTx.TextChanged += (s, e) => CalculatePixelDimensions();

            _posterWLabel = new Label
            {
                Text = "Poster Width (inches):",
                Left = 4,
                Top = 38,
                Width = 130,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 9f)
            };

            Controls.Add(_posterWLabel);
            Controls.Add(_postWTx);

            _widthLabel = new Label
            {
                Text = "Pixel Width:",
                Left = 4,
                Top = 70,
                Width = 130,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 9f)
            };
            Controls.Add(_widthLabel);

            _widthTx = new TextBox
            {
                Left = 136,
                Top = 71,
                Width = 50,
                BackColor = Color.FromArgb(50, 50, 50),
                ForeColor = Color.White,
                Font = new Font("Consolas", 10f),
                BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(_widthTx);

            _heightLabel = new Label
            {
                Text = "Pixel Height:",
                Left = 4,
                Top = 98,
                Width = 130,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 9f)
            };
            Controls.Add(_heightLabel);

            _heightTx = new TextBox
            {
                Left = 136,
                Top = 97,
                Width = 50,
                BackColor = Color.FromArgb(50, 50, 50),
                ForeColor = Color.White,
                Font = new Font("Consolas", 10f),
                BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(_heightTx);

            _portraitCB = new CheckBox
            {
                Text = "Portrait Orientation",
                Left = 203,
                Top = 10,
                Width = 200,
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 9f),
                Checked = true
            };
            Controls.Add(_portraitCB);

            _lowDefCB = new CheckBox
            {
                Text = "Low Def (150 DPI)",
                Left = 203,
                Top = 36,
                Width = 120,
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 9f),
                Checked = false
            };
            _lowDefCB.CheckedChanged += (s, e) =>
            {
                CalculatePixelDimensions();
                if (_lowDefCB.Checked)
                {
                    _medDefCB?.Checked = false;
                    _highDefCB?.Checked = false;
                }
            };
            Controls.Add(_lowDefCB);

            _medDefCB = new CheckBox
            {
                Text = "Med Def (300 DPI)",
                Left = 203,
                Top = 60,
                Width = 180,
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 9f),
                Checked = true
            };
            _medDefCB.CheckedChanged += (s, e) =>
            {
                CalculatePixelDimensions();
                if (_medDefCB.Checked)
                {
                    _lowDefCB?.Checked = false;
                    _highDefCB?.Checked = false;
                }
            };
            Controls.Add(_medDefCB);

            _highDefCB = new CheckBox
            {
                Text = "High Def (600 DPI)",
                Left = 203,
                Top = 84,
                Width = 180,
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 9f),
                Checked = false
            };
            _highDefCB.CheckedChanged += (s, e) =>
            {
                CalculatePixelDimensions();
                if (_highDefCB.Checked)
                {
                    _lowDefCB?.Checked = false;
                    _medDefCB?.Checked = false;
                }
            };
            Controls.Add(_highDefCB);

            ToolTip _portraitTip = new ToolTip();
            _portraitTip.SetToolTip(_portraitCB, "If checked, the output image will be formatted for portrait-oriented paper.  If unchecked, the image will be formatted for landscape-oriented paper.  When printing a poster taller than it is wide, select portrait orientation for the best results.");

            var ok = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Left = 82,
                Top = 138,
                Width = 72,
                Height = 26,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Left = 162,
                Top = 138,
                Width = 72,
                Height = 26,
                BackColor = Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(ok);
            Controls.Add(cancel);
        }

        private void CalculatePixelDimensions()
        {
            int posterHeightInches = 0;
            int posterWidthInches = 0;

            int.TryParse(_postHTx.Text, out posterHeightInches);
            int.TryParse(_postWTx.Text, out posterWidthInches);
            if (posterHeightInches < 0 || posterWidthInches < 0)
            {
                MessageBox.Show("Please enter valid integer values for poster width and height in inches.", "Invalid Input", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            int dpi = _lowDefCB.Checked ? 150 : _highDefCB.Checked ? 600 : 300;
            int pixelWidth = posterWidthInches * dpi;
            int pixelHeight = posterHeightInches * dpi;
            _widthTx.Text = pixelWidth.ToString();
            _heightTx.Text = pixelHeight.ToString();
        }
    }
}
