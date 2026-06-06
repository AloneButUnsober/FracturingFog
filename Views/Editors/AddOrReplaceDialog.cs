// Views/Editors/AddOrReplaceDialog.cs
//
// Tiny modal: imported color count summary, three buttons —
//   Add      → append colors to end of current stops (position=1)
//   Replace  → drop current stops, rebuild from imported colors
//   Cancel
//
// Result is exposed as a Choice enum on the caller side.

using System.Drawing;
using System.Windows.Forms;

namespace FracturingFog.Views.Editors
{
    public sealed class AddOrReplaceDialog : Form
    {
        public enum Choice { Cancel, Add, Replace }

        public Choice Result { get; private set; } = Choice.Cancel;

        public AddOrReplaceDialog(int importedCount, int currentCount, string fileLabel)
        {
            Text = "Import Palette";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(420, 170);
            BackColor = Color.FromArgb(28, 28, 28);
            ForeColor = Color.FromArgb(220, 220, 220);
            KeyPreview = true;

            var title = new Label
            {
                Text = $"Loaded {importedCount} color" + (importedCount == 1 ? "" : "s")
                    + $" from {fileLabel}",
                Left = 16,
                Top = 14,
                AutoSize = true,
                ForeColor = Color.FromArgb(200, 200, 100),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            };
            Controls.Add(title);

            var hint = new Label
            {
                Text = $"Current stops: {currentCount}.\n\n"
                    + "Add: append imported colors at position 1.0\n"
                    + "        (existing stops unchanged).\n"
                    + "Replace: discard current stops and rebuild from\n"
                    + "        imported colors (positions redistributed 0…1).",
                Left = 16,
                Top = 38,
                Width = ClientSize.Width - 32,
                Height = 80,
                ForeColor = Color.FromArgb(200, 200, 200),
                Font = new Font("Segoe UI", 8.75f),
            };
            Controls.Add(hint);

            var btnReplace = MakeButton("Replace", 0, ClientSize.Height - 40,
                Color.FromArgb(80, 50, 50));
            btnReplace.Click += (s, e) => { Result = Choice.Replace; DialogResult = DialogResult.OK; Close(); };

            var btnAdd = MakeButton("Add", 0, ClientSize.Height - 40,
                Color.FromArgb(40, 80, 40));
            btnAdd.Click += (s, e) => { Result = Choice.Add; DialogResult = DialogResult.OK; Close(); };

            var btnCancel = MakeButton("Cancel", 0, ClientSize.Height - 40,
                Color.FromArgb(60, 60, 60));
            btnCancel.Click += (s, e) => { Result = Choice.Cancel; DialogResult = DialogResult.Cancel; Close(); };

            const int gap = 6;
            int total = btnAdd.Width + btnReplace.Width + btnCancel.Width + gap * 2;
            int x = (ClientSize.Width - total) / 2;
            btnAdd.Left = x; x += btnAdd.Width + gap;
            btnReplace.Left = x; x += btnReplace.Width + gap;
            btnCancel.Left = x;

            Controls.Add(btnAdd);
            Controls.Add(btnReplace);
            Controls.Add(btnCancel);

            AcceptButton = btnAdd;
            CancelButton = btnCancel;

            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { Result = Choice.Cancel; DialogResult = DialogResult.Cancel; Close(); }
            };
        }

        private static Button MakeButton(string text, int left, int top, Color bg)
        {
            var b = new Button
            {
                Text = text,
                Left = left,
                Top = top,
                Width = 110,
                Height = 30,
                FlatStyle = FlatStyle.Flat,
                BackColor = bg,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(
                System.Math.Min(255, bg.R + 50),
                System.Math.Min(255, bg.G + 50),
                System.Math.Min(255, bg.B + 50));
            return b;
        }
    }
}
