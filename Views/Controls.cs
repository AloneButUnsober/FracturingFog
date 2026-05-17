using FracturingFog.Interefaces;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Security.Cryptography;

using FracturingFog.Models;
using System.Linq;
using System.IO;
using Microsoft.VisualBasic;

namespace FracturingFog.Views
{
    /// <summary>
    /// MARGINS struct for DwmExtendFrameIntoClientArea call to enable Aero glass effect on the toolbar.  
    /// All fields set to -1 to extend the glass over the entire toolbar area.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    internal static class ControlExtensions
    {
        public static T Also<T>(this T ctrl, Action<T> action) where T : Control
        { action(ctrl); return ctrl; }

        public static T AlsoAdd<T>(this T ctrl, Panel parent) where T : Control
        { parent.Controls.Add(ctrl); return ctrl; }

        public static T AlsoAdd<T>(this T ctrl, Panel parent, string tooltip) where T : Control
        { new ToolTip().SetToolTip(ctrl, tooltip); parent.Controls.Add(ctrl); return ctrl; }
    }

    public class ColorComboBox : ComboBox
    {
        public ColorComboBox()
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            DropDownStyle = ComboBoxStyle.DropDownList;
            ItemHeight = 20;
        }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            e.DrawBackground();

            string text = Items[e.Index]?.ToString() ?? "";
            IColorMap map = Models.ColorPalette.GetPaletteByName(text);
            map.MaxIterations = 500;
            int argb = map.SwatchSample;
            var swatch = Color.FromArgb((argb >> 16) & 0xFF, (argb >> 8) & 0xFF, argb & 0xFF);

            var swatchRect = new Rectangle(e.Bounds.X + 2, e.Bounds.Y + 3, 18, e.Bounds.Height - 6);
            using var sb = new SolidBrush(swatch);
            e.Graphics.FillRectangle(sb, swatchRect);
            e.Graphics.DrawRectangle(Pens.DimGray, swatchRect);

            var textBrush = (e.State & DrawItemState.Selected) != 0 ? Brushes.White : Brushes.LightGray;
            e.Graphics.DrawString(text, Font, textBrush, swatchRect.Right + 4, e.Bounds.Y + 2);
            e.DrawFocusRectangle();
        }
        protected override void OnPaintBackground(PaintEventArgs pevent)
        {
            base.OnPaintBackground(pevent);
            using var b = new SolidBrush(BackColor);
            pevent.Graphics.FillRectangle(b, ClientRectangle);
            pevent.Graphics.DrawRectangle(Pens.DarkGray, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }
    }

    public static class FormHelpers
    {
        #region Form Helpers

        public static Button MakeBtn(
            string text,
            int w = 108,
            int left = 0,
            int top = 6,
            string toolTip = "",
            ToolTip? ttComp = null)
        {
            Button _b = new Button
            {
                Text = text,
                Width = w,
                Height = 26,
                Left = left,
                Top = top,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(55, 55, 55),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Cursor = Cursors.Hand
            }.Also(b => b.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90));

            if (ttComp != null && !string.IsNullOrEmpty(toolTip))
            {
                ttComp.SetToolTip(_b, toolTip);
            }

            return _b;
        }

        public static Label MakeLbl(string text, int left, int top, Panel p, bool rightAlign) => new Label
        {
            Text = text,
            Left = left,
            Top = top,
            AutoSize = !rightAlign,
            TextAlign = rightAlign ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(155, 155, 155),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            BackColor = Color.Transparent
        }.AlsoAdd(p);

        public static TextBox MakeTx(int left, int top, int w, Panel p, string tip) => new TextBox
        {
            Left = left,
            Top = top,
            Width = w,
            Height = 22,
            BackColor = Color.FromArgb(40, 40, 40),
            ForeColor = Color.FromArgb(220, 220, 220),
            Font = new Font("Consolas", 9f),
            BorderStyle = BorderStyle.FixedSingle
        }.AlsoAdd(p, tip);
        #endregion Form Helpers

        public static void BuildColorCombo(ComboBox comboBox, EventHandler func)
        {
            if (comboBox != null)
            {
                comboBox.SelectedIndexChanged -= func;
                comboBox.Items.Clear();
                foreach (var type in Enum.GetValues<ColorPaletteType>())
                {
                    var palettes = Models.ColorPalette.GetPalettesByType(type);
                    if (palettes.Count == 0) continue;
                    comboBox.Items.Add($"— {type} —");
                    foreach (var name in palettes.ToImmutableSortedDictionary().Keys)
                    {
                        comboBox.Items.Add(name);
                    }
                }
                comboBox.SelectedIndex = 0;
                comboBox.SelectedIndexChanged += func;
            }
        }

        public static void RebuildRegionCombo(ComboBox comboBox, EventHandler func)
        {
            if (comboBox != null)
            {
                comboBox.SelectedIndexChanged -= func;
                comboBox.Items.Clear();
                comboBox.Items.Add("— select region —");
                var regions = FractalRegionLibrary.Instance.All.OrderBy(r => r.IsBuiltIn).ThenBy(r => r.Name);
                foreach (var r in regions)
                {
                    comboBox.Items.Add(r.Name);
                }

                comboBox.SelectedIndex = 0;
                comboBox.SelectedIndexChanged += func;
            }
        }

        public static void RebuildRegionComboNoExtreme(ComboBox comboBox, EventHandler func)
        {
            if (comboBox != null)
            {
                comboBox.SelectedIndexChanged -= func;
                comboBox.Items.Clear();
                comboBox.Items.Add("— select region —");
                var regions = FractalRegionLibrary.Instance.All
                    .Where(r => r.QualityPreset != QualityPreset.Extreme)
                    .OrderBy(r => r.IsBuiltIn).ThenBy(r => r.Name);
                foreach (var r in regions)
                {
                    comboBox.Items.Add(r.Name);
                }

                comboBox.SelectedIndex = 0;
                comboBox.SelectedIndexChanged += func;
            }
        }

        public static void UpdateDeleteColorThemeButton(ComboBox comboBox, Button delButton)
        {
            if (comboBox != null &&
                delButton != null)
            {
                string? name = comboBox.SelectedItem?.ToString();
                if (string.IsNullOrEmpty(name) || name.StartsWith("—"))
                { delButton.Enabled = false; return; }

                delButton.Enabled = UserColorThemeLibrary.Instance.Themes
                    .Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            }
        }

        public static void UpdateDelRegionButton(ComboBox comboBox, Button delButton)
        {
            if (comboBox != null &&
                delButton != null)
            {
                string? name = comboBox.SelectedItem?.ToString();
                if (string.IsNullOrEmpty(name) || name == "— select region —")
                { delButton.Enabled = false; return; }
                var region = FractalRegionLibrary.Instance.FindByName(name);
                delButton.Enabled = region != null && !region.IsBuiltIn;
            }
        }

        // ── Coordinate formatting / parsing helpers ───────────────────────────────

        // Formats a QD coordinate as a pipe-separated string of non-zero limbs.
        // Uses "R" (round-trip) format so paste-back restores the exact bit pattern.
        // Single-limb example:  "-0.5"
        // DD example:           "-0.748392837462382|-1.23456789012345e-16"
        // QD example:           "-0.748392837...|...|...|..."
        public static string FormatCoord(double hi, double lo, double x2, double x3)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            string s = hi.ToString("R", ic);
            if (lo != 0.0) s += "|" + lo.ToString("R", ic);
            if (x2 != 0.0) s += "|" + x2.ToString("R", ic);
            if (x3 != 0.0) s += "|" + x3.ToString("R", ic);
            return s;
        }

        public static string GetFileHash(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) ||
                !File.Exists(filePath)) return string.Empty;
            
            using StreamReader sr = new StreamReader(filePath);
            string fHash = sr.ReadToEnd();
            byte[] bHash = Encoding.UTF8.GetBytes(fHash);
            bHash = SHA256.HashData(bHash);
            return Convert.ToBase64String(bHash);
        }
    }
}
