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

using FracturingFog;
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
        private double _currentZoom;

        /// <summary>
        /// Current view zoom. Items whose theme's
        /// <see cref="Models.ColorPalette.GetStaticMaxZoom"/> is below this
        /// value are rendered dimmed with a strikethrough line to indicate
        /// they are not recommended for the current depth. Items remain
        /// selectable — this is purely an advisory visual cue.
        /// </summary>
        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(
            System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public double CurrentZoom
        {
            get => _currentZoom;
            set
            {
                if (_currentZoom == value) return;
                _currentZoom = value;
                Invalidate();
            }
        }

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

            bool selected = (e.State & DrawItemState.Selected) != 0;
            bool incompatible = _currentZoom > 0
                             && _currentZoom > Models.ColorPalette.GetStaticMaxZoom(map);

            Color textColor;
            if (incompatible)
                textColor = selected ? Color.FromArgb(180, 180, 180) : Color.FromArgb(110, 110, 110);
            else
                textColor = selected ? Color.White : Color.LightGray;

            using var textBrush = new SolidBrush(textColor);
            int textX = swatchRect.Right + 4;
            int textY = e.Bounds.Y + 2;
            e.Graphics.DrawString(text, Font, textBrush, textX, textY);

            if (incompatible)
            {
                // Strikethrough line through the text — same colour as the
                // dimmed text so the cue reads as "deprecated for this zoom"
                // without looking like a selection or error indicator.
                SizeF sz = e.Graphics.MeasureString(text, Font);
                int lineY = textY + (int)(sz.Height / 2);
                using var strikePen = new Pen(textColor, 1f);
                e.Graphics.DrawLine(strikePen, textX, lineY, textX + (int)sz.Width, lineY);
            }

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

        public enum ColorComboSortMode
        {
            Default,
            All,
            ByKind,
        }

        /// <summary>
        /// Sort/filter state for a colour theme combo. Stored on
        /// <see cref="ComboBox.Tag"/> so the combo can rebuild itself after the
        /// user picks a different sort mode from the right-click context menu.
        /// </summary>
        public sealed class ColorComboSortState
        {
            public ColorComboSortMode Mode { get; set; } = ColorComboSortMode.Default;
            public ColorPaletteType KindFilter { get; set; } = ColorPaletteType.GradientLinear;
        }

        private static ColorComboSortState GetOrCreateSortState(ComboBox comboBox)
        {
            if (comboBox.Tag is ColorComboSortState s) return s;
            var ns = new ColorComboSortState();
            comboBox.Tag = ns;
            return ns;
        }

        public static void BuildColorCombo(ComboBox comboBox, EventHandler func)
        {
            if (comboBox == null) return;
            var state = GetOrCreateSortState(comboBox);
            comboBox.SelectedIndexChanged -= func;
            comboBox.Items.Clear();
            switch (state.Mode)
            {
                case ColorComboSortMode.Default:
                    foreach (var type in Enum.GetValues<ColorPaletteType>())
                    {
                        var palettes = Models.ColorPalette.GetPalettesByType(type);
                        if (palettes.Count == 0) continue;
                        comboBox.Items.Add($"— {type} —");
                        foreach (var name in palettes.ToImmutableSortedDictionary().Keys)
                            comboBox.Items.Add(name);
                    }
                    break;

                case ColorComboSortMode.All:
                    foreach (var name in CollectAllThemeNames())
                        comboBox.Items.Add(name);
                    break;

                case ColorComboSortMode.ByKind:
                    var byKind = Models.ColorPalette.GetPalettesByType(state.KindFilter);
                    foreach (var name in byKind.ToImmutableSortedDictionary().Keys)
                        comboBox.Items.Add(name);
                    break;
            }
            if (comboBox.Items.Count > 0) comboBox.SelectedIndex = 0;
            comboBox.SelectedIndexChanged += func;
        }

        private static IEnumerable<string> CollectAllThemeNames()
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var type in Enum.GetValues<ColorPaletteType>())
            {
                var palettes = Models.ColorPalette.GetPalettesByType(type);
                foreach (var name in palettes.Keys) names.Add(name);
            }
            return names;
        }

        /// <summary>
        /// Rebuilds the combo with the current sort state and restores
        /// <paramref name="preserveName"/> as the selection when present.
        /// Falls back to index 0 (or no selection) when the name is gone.
        /// </summary>
        public static void RebuildColorCombo(ComboBox comboBox, EventHandler func, string? preserveName)
        {
            BuildColorCombo(comboBox, func);
            if (!string.IsNullOrEmpty(preserveName))
            {
                int idx = comboBox.FindStringExact(preserveName);
                if (idx >= 0)
                {
                    comboBox.SelectedIndexChanged -= func;
                    comboBox.SelectedIndex = idx;
                    comboBox.SelectedIndexChanged += func;
                }
            }
        }

        /// <summary>
        /// Attaches a right-click context menu to a colour-theme combo. The
        /// menu offers Default (grouped with dividers), All (flat
        /// alphabetical), and one entry per <see cref="ColorPaletteType"/>
        /// that filters the list to that kind. The current sort mode is
        /// shown as checked.
        /// </summary>
        public static void AttachColorComboSortMenu(
            ComboBox comboBox,
            EventHandler selectionHandler,
            Action? onAfterRebuild = null)
        {
            if (comboBox == null) return;
            GetOrCreateSortState(comboBox);

            comboBox.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                if (comboBox.DroppedDown) comboBox.DroppedDown = false;
                ShowColorComboSortMenu(comboBox, selectionHandler, onAfterRebuild, e.Location);
            };
        }

        private static void ShowColorComboSortMenu(
            ComboBox comboBox,
            EventHandler selectionHandler,
            Action? onAfterRebuild,
            Point screenLocal)
        {
            var state = GetOrCreateSortState(comboBox);
            var menu = new ContextMenuStrip
            {
                BackColor = Color.FromArgb(45, 45, 45),
                ForeColor = Color.White,
                ShowImageMargin = false,
                Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()),
            };

            void Add(string text, ColorComboSortMode mode, ColorPaletteType? kind, bool isChecked)
            {
                var item = new ToolStripMenuItem(text) { Checked = isChecked };
                item.Click += (s, e) =>
                {
                    string? prev = comboBox.SelectedItem?.ToString();
                    state.Mode = mode;
                    if (kind.HasValue) state.KindFilter = kind.Value;
                    RebuildColorCombo(comboBox, selectionHandler, prev);
                    onAfterRebuild?.Invoke();
                };
                menu.Items.Add(item);
            }

            Add("Default", ColorComboSortMode.Default, null, state.Mode == ColorComboSortMode.Default);
            Add("All (A–Z)", ColorComboSortMode.All, null, state.Mode == ColorComboSortMode.All);
            menu.Items.Add(new ToolStripSeparator());
            foreach (var kind in Enum.GetValues<ColorPaletteType>())
            {
                bool isChecked = state.Mode == ColorComboSortMode.ByKind && state.KindFilter == kind;
                Add(kind.ToString(), ColorComboSortMode.ByKind, kind, isChecked);
            }
            menu.Show(comboBox, screenLocal);
        }

        private sealed class DarkMenuColors : ProfessionalColorTable
        {
            public override Color MenuItemSelected => Color.FromArgb(70, 70, 70);
            public override Color MenuItemSelectedGradientBegin => Color.FromArgb(70, 70, 70);
            public override Color MenuItemSelectedGradientEnd => Color.FromArgb(70, 70, 70);
            public override Color MenuItemBorder => Color.FromArgb(90, 90, 90);
            public override Color ToolStripDropDownBackground => Color.FromArgb(45, 45, 45);
            public override Color ImageMarginGradientBegin => Color.FromArgb(45, 45, 45);
            public override Color ImageMarginGradientMiddle => Color.FromArgb(45, 45, 45);
            public override Color ImageMarginGradientEnd => Color.FromArgb(45, 45, 45);
        }

        public enum RegionComboSortMode
        {
            /// <summary>Built-ins first (alpha), then user regions (alpha). All fractal types.</summary>
            Default,
            /// <summary>Filter to a single FractalType, alphabetical.</summary>
            ByFractalType,
        }

        /// <summary>
        /// Sort/filter state for a region combo. Stored on <see cref="ComboBox.Tag"/> so the combo
        /// can rebuild itself after the user picks a different sort mode from the right-click menu.
        /// </summary>
        public sealed class RegionComboSortState
        {
            public RegionComboSortMode Mode { get; set; } = RegionComboSortMode.Default;
            public FractalType TypeFilter { get; set; } = FractalType.Mandelbrot;
        }

        private static RegionComboSortState GetOrCreateRegionSortState(ComboBox comboBox)
        {
            if (comboBox.Tag is RegionComboSortState s) return s;
            var ns = new RegionComboSortState();
            comboBox.Tag = ns;
            return ns;
        }

        public static void RebuildRegionCombo(ComboBox comboBox, EventHandler func)
            => RebuildRegionCombo(comboBox, func, preserveName: null, excludeExtreme: false);

        public static void RebuildRegionComboNoExtreme(ComboBox comboBox, EventHandler func)
            => RebuildRegionCombo(comboBox, func, preserveName: null, excludeExtreme: true);

        /// <summary>
        /// Rebuilds the region combo per its <see cref="RegionComboSortState"/> on the Tag.
        /// Default mode: built-ins first, then user regions, alphabetical within each group.
        /// ByFractalType mode: only regions matching the state's <see cref="RegionComboSortState.TypeFilter"/>.
        /// </summary>
        public static void RebuildRegionCombo(ComboBox comboBox, EventHandler func, string? preserveName, bool excludeExtreme)
        {
            if (comboBox == null) return;
            var state = GetOrCreateRegionSortState(comboBox);

            comboBox.SelectedIndexChanged -= func;
            comboBox.Items.Clear();
            comboBox.Items.Add("— select region —");

            IEnumerable<FractalRegion> source = FractalRegionLibrary.Instance.All;
            if (excludeExtreme)
                source = source.Where(r => r.QualityPreset != QualityPreset.Extreme);

            IEnumerable<FractalRegion> regions = state.Mode switch
            {
                RegionComboSortMode.ByFractalType =>
                    source.Where(r => r.FractalType == state.TypeFilter)
                          .OrderBy(r => r.IsBuiltIn ? 0 : 1).ThenBy(r => r.Name),
                _ => source.OrderBy(r => r.IsBuiltIn ? 0 : 1).ThenBy(r => r.Name),
            };

            foreach (var r in regions)
                comboBox.Items.Add(r.Name);

            int idx = 0;
            if (!string.IsNullOrEmpty(preserveName))
            {
                int found = comboBox.FindStringExact(preserveName);
                if (found >= 0) idx = found;
            }
            comboBox.SelectedIndex = idx;
            comboBox.SelectedIndexChanged += func;
        }

        /// <summary>
        /// Attaches a right-click context menu to a region combo. Menu offers Default (all types,
        /// built-ins first) and one entry per <see cref="FractalType"/>. Current sort is checked.
        /// </summary>
        public static void AttachRegionComboSortMenu(
            ComboBox comboBox,
            EventHandler selectionHandler,
            bool excludeExtreme = false,
            Action? onAfterRebuild = null)
        {
            if (comboBox == null) return;
            GetOrCreateRegionSortState(comboBox);

            comboBox.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                if (comboBox.DroppedDown) comboBox.DroppedDown = false;
                ShowRegionComboSortMenu(comboBox, selectionHandler, excludeExtreme, onAfterRebuild, e.Location);
            };
        }

        private static void ShowRegionComboSortMenu(
            ComboBox comboBox,
            EventHandler selectionHandler,
            bool excludeExtreme,
            Action? onAfterRebuild,
            Point screenLocal)
        {
            var state = GetOrCreateRegionSortState(comboBox);
            var menu = new ContextMenuStrip
            {
                BackColor = Color.FromArgb(45, 45, 45),
                ForeColor = Color.White,
                ShowImageMargin = false,
                Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()),
            };

            void Add(string text, RegionComboSortMode mode, FractalType? kind, bool isChecked)
            {
                var item = new ToolStripMenuItem(text) { Checked = isChecked };
                item.Click += (s, e) =>
                {
                    string? prev = comboBox.SelectedItem?.ToString();
                    state.Mode = mode;
                    if (kind.HasValue) state.TypeFilter = kind.Value;
                    RebuildRegionCombo(comboBox, selectionHandler, prev, excludeExtreme);
                    onAfterRebuild?.Invoke();
                };
                menu.Items.Add(item);
            }

            Add("Default", RegionComboSortMode.Default, null, state.Mode == RegionComboSortMode.Default);
            menu.Items.Add(new ToolStripSeparator());
            foreach (var kind in Enum.GetValues<FractalType>())
            {
                bool isChecked = state.Mode == RegionComboSortMode.ByFractalType && state.TypeFilter == kind;
                Add(kind.ToString(), RegionComboSortMode.ByFractalType, kind, isChecked);
            }
            menu.Show(comboBox, screenLocal);
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
