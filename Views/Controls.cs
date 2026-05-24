using FracturingFog.Interefaces;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Drawing;
using System.Numerics;
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

            /// <summary>
            /// When set, BuildColorCombo prepends a "— Suggested —" section with
            /// the top theme picks ranked against this equation's structural
            /// profile (see <see cref="EquationAnalyzer"/> and
            /// <see cref="ThemeRecommender"/>). Cleared when the active fractal
            /// type does not consume a user-supplied equation.
            /// </summary>
            public EquationProfile? SuggestedFor { get; set; }

            /// <summary>Max suggestions to surface when <see cref="SuggestedFor"/> is set.</summary>
            public int SuggestionCount { get; set; } = 8;
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

            // Suggestion section comes first, regardless of sort mode, so the
            // top picks are always one click away when an equation is active.
            if (state.SuggestedFor != null)
            {
                var picks = Models.ThemeRecommender.RecommendNames(
                    state.SuggestedFor, Models.ColorPalette.Palettes, state.SuggestionCount);
                if (picks.Count > 0)
                {
                    comboBox.Items.Add("— Suggested for equation —");
                    foreach (var name in picks) comboBox.Items.Add(name);
                }
            }

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
        /// Applies (or clears) an equation profile on the combo's sort state
        /// and rebuilds. Pass null to remove the suggested section. The current
        /// selection is preserved when possible.
        /// </summary>
        public static void ApplyEquationProfile(
            ComboBox comboBox, EquationProfile? profile, EventHandler func)
        {
            if (comboBox == null) return;
            var state = GetOrCreateSortState(comboBox);
            if (ReferenceEquals(state.SuggestedFor, profile)) return;
            state.SuggestedFor = profile;
            string? prev = comboBox.SelectedItem?.ToString();
            RebuildColorCombo(comboBox, func, prev);
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

        // ── Single-string QD coordinate codec ────────────────────────────────────
        //
        // FormatCoordSingle: collapses a QD value (Hi+Lo+X2+X3) into one decimal
        // string with up to ~80 significant digits — enough to round-trip QD
        // precision (~62 digits) plus headroom for greedy double extraction.
        //
        // TryParseCoordSingle: inverse — parses a single decimal (or scientific)
        // string and greedily extracts up to four IEEE doubles whose unevaluated
        // sum reproduces the input to full precision.
        //
        // TryParseCoordAny: accepts either format — pipe-delimited "Hi|Lo|X2|X3"
        // (native FormatCoord output) or single-string form. Used by widgets that
        // must accept both legacy paste and the new single-string display.

        /// <summary>
        /// Decomposes an IEEE 754 double into exact integer mantissa × 2^exp.
        /// Handles ±0, denormals, and signed values. Returns (0,0) for ±0.
        /// </summary>
        private static (BigInteger m, int e) DecomposeDouble(double d)
        {
            if (d == 0.0) return (BigInteger.Zero, 0);
            long bits = BitConverter.DoubleToInt64Bits(d);
            int sign = (int)((bits >> 63) & 1);
            int rawExp = (int)((bits >> 52) & 0x7FF);
            long rawMant = bits & 0xFFFFFFFFFFFFFL;
            int exp;
            long mant;
            if (rawExp == 0)
            {
                // Denormal: value = rawMant × 2^(-1074)
                mant = rawMant;
                exp = -1074;
            }
            else
            {
                mant = rawMant | (1L << 52);
                exp = rawExp - 1023 - 52;
            }
            BigInteger m = mant;
            if (sign == 1) m = -m;
            return (m, exp);
        }

        /// <summary>
        /// Exact sum of doubles as rational num × 2^e2. Drops any zero limbs.
        /// </summary>
        private static (BigInteger num, int e2) ExactSum(params double[] limbs)
        {
            int eMin = int.MaxValue;
            var parts = new (BigInteger m, int e)[limbs.Length];
            for (int i = 0; i < limbs.Length; i++)
            {
                if (limbs[i] == 0.0) continue;
                parts[i] = DecomposeDouble(limbs[i]);
                if (parts[i].e < eMin) eMin = parts[i].e;
            }
            if (eMin == int.MaxValue) return (BigInteger.Zero, 0);
            BigInteger sum = BigInteger.Zero;
            for (int i = 0; i < limbs.Length; i++)
            {
                if (limbs[i] == 0.0) continue;
                int shift = parts[i].e - eMin;
                sum += parts[i].m << shift;
            }
            return (sum, eMin);
        }

        /// <summary>
        /// Formats a QD coordinate as a single decimal string of up to ~80
        /// significant digits. Round-trips through <see cref="TryParseCoordSingle"/>
        /// back to the same four-limb representation.
        /// Examples:
        ///   "-0.5"
        ///   "-0.748392837462382912345678901234..."
        /// </summary>
        public static string FormatCoordSingle(double hi, double lo, double x2, double x3)
        {
            var (num, e2) = ExactSum(hi, lo, x2, x3);
            if (num.IsZero) return "0";

            bool neg = num.Sign < 0;
            if (neg) num = -num;

            // value = num × 2^e2 → express as integer numerator over power-of-2 denom
            BigInteger numerator, denominator;
            if (e2 >= 0)
            {
                numerator = num << e2;
                denominator = BigInteger.One;
            }
            else
            {
                numerator = num;
                denominator = BigInteger.One << (-e2);
            }

            BigInteger intPart = BigInteger.DivRem(numerator, denominator, out BigInteger frac);

            var sb = new StringBuilder();
            if (neg) sb.Append('-');
            sb.Append(intPart.ToString(System.Globalization.CultureInfo.InvariantCulture));

            if (!frac.IsZero)
            {
                sb.Append('.');
                // Emit at most ~80 fractional digits (QD = ~62 sig digits + headroom).
                const int MaxFracDigits = 80;
                int produced = 0;
                while (!frac.IsZero && produced < MaxFracDigits)
                {
                    frac *= 10;
                    BigInteger d = BigInteger.DivRem(frac, denominator, out frac);
                    sb.Append((char)('0' + (int)d));
                    produced++;
                }
                // Trim trailing zeros / dangling decimal point.
                while (sb.Length > 0 && sb[sb.Length - 1] == '0') sb.Length--;
                if (sb.Length > 0 && sb[sb.Length - 1] == '.') sb.Length--;
            }

            return sb.ToString();
        }

        /// <summary>
        /// Best-effort conversion of a non-negative rational num/den to the nearest
        /// IEEE double (round-to-nearest, ties-to-even via shift+divide). Caller
        /// supplies positive operands; sign handled separately.
        /// </summary>
        private static double RationalToDouble(BigInteger num, BigInteger den)
        {
            if (num.IsZero) return 0.0;
            // Shift numerator so that quotient has ~64 bits; then cast to double.
            int nb = (int)num.GetBitLength();
            int db = (int)den.GetBitLength();
            int shift = 64 + db - nb;
            BigInteger shifted = shift >= 0 ? num << shift : num >> -shift;
            BigInteger q = BigInteger.DivRem(shifted, den, out _);
            double dq = (double)q;
            // q ≈ (num/den) × 2^shift → divide back out via ScaleB for exact power-of-2 scale.
            return System.Math.ScaleB(dq, -shift);
        }

        /// <summary>
        /// Parses a single decimal string (optionally scientific) and decomposes
        /// it greedily into up to four IEEE doubles whose unevaluated sum equals
        /// the input value to full precision. Inverse of <see cref="FormatCoordSingle"/>.
        /// </summary>
        public static bool TryParseCoordSingle(string text,
            out double hi, out double lo, out double x2, out double x3)
        {
            hi = lo = x2 = x3 = 0.0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.Trim();

            // Strip leading sign.
            bool neg = false;
            int idx = 0;
            if (s[idx] == '+') { idx++; }
            else if (s[idx] == '-') { neg = true; idx++; }
            if (idx >= s.Length) return false;

            // Split off exponent (e/E).
            int eIdx = s.IndexOfAny(new[] { 'e', 'E' }, idx);
            string mantStr = eIdx < 0 ? s.Substring(idx) : s.Substring(idx, eIdx - idx);
            int exp10 = 0;
            if (eIdx >= 0)
            {
                if (!int.TryParse(s.AsSpan(eIdx + 1),
                                  System.Globalization.NumberStyles.Integer,
                                  System.Globalization.CultureInfo.InvariantCulture,
                                  out exp10))
                    return false;
            }

            // Strip decimal point.
            int dot = mantStr.IndexOf('.');
            string digits;
            int fracLen;
            if (dot < 0) { digits = mantStr; fracLen = 0; }
            else
            {
                digits = mantStr.Substring(0, dot) + mantStr.Substring(dot + 1);
                fracLen = mantStr.Length - dot - 1;
            }
            if (digits.Length == 0) return false;

            if (!BigInteger.TryParse(digits,
                                     System.Globalization.NumberStyles.Integer,
                                     System.Globalization.CultureInfo.InvariantCulture,
                                     out BigInteger mant))
                return false;

            // value = mant × 10^(exp10 - fracLen)
            int netE10 = exp10 - fracLen;
            BigInteger numerator = mant;
            BigInteger denominator = BigInteger.One;
            if (netE10 >= 0) numerator *= BigInteger.Pow(10, netE10);
            else denominator = BigInteger.Pow(10, -netE10);

            if (neg) numerator = -numerator;

            // Greedy extraction: pull off the nearest double, subtract its exact
            // rational, repeat up to 4 times. Stops early when residual hits zero.
            double[] limbs = new double[4];
            for (int i = 0; i < 4; i++)
            {
                if (numerator.IsZero) break;
                bool nNeg = numerator.Sign < 0;
                BigInteger absN = nNeg ? -numerator : numerator;
                double d = RationalToDouble(absN, denominator);
                if (nNeg) d = -d;
                if (d == 0.0 || double.IsInfinity(d) || double.IsNaN(d)) break;
                limbs[i] = d;

                // Subtract d × denominator from numerator (keep denominator constant
                // by representing d exactly as integer / 2^k).
                var (dm, de) = DecomposeDouble(d);
                BigInteger dNum, dDen;
                if (de >= 0) { dNum = dm << de; dDen = BigInteger.One; }
                else { dNum = dm; dDen = BigInteger.One << (-de); }

                // numerator/denominator -= dNum/dDen
                // → numerator = numerator*dDen - dNum*denominator
                // → denominator = denominator*dDen
                numerator = numerator * dDen - dNum * denominator;
                denominator = denominator * dDen;
            }

            hi = limbs[0]; lo = limbs[1]; x2 = limbs[2]; x3 = limbs[3];
            return true;
        }

        /// <summary>
        /// Parses either pipe-delimited "Hi|Lo|X2|X3" (native FormatCoord output)
        /// or a single decimal string (FormatCoordSingle output). Used by widgets
        /// that accept user paste in either form.
        /// </summary>
        public static bool TryParseCoordAny(string text,
            out double hi, out double lo, out double x2, out double x3)
        {
            hi = lo = x2 = x3 = 0.0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.Trim();
            if (s.IndexOf('|') >= 0)
            {
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                var ns = System.Globalization.NumberStyles.Float;
                var parts = s.Split('|');
                if (!double.TryParse(parts[0].Trim(), ns, ic, out hi)) return false;
                if (parts.Length > 1 && !double.TryParse(parts[1].Trim(), ns, ic, out lo)) return false;
                if (parts.Length > 2 && !double.TryParse(parts[2].Trim(), ns, ic, out x2)) return false;
                if (parts.Length > 3 && !double.TryParse(parts[3].Trim(), ns, ic, out x3)) return false;
                return true;
            }
            return TryParseCoordSingle(s, out hi, out lo, out x2, out x3);
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
