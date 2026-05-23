// Views/ColorThemeEditor.cs
//
// Floating editor window for ColorThemeData. Lets the user pick a starting
// theme (or build from scratch), edit every parameter exposed by the four
// theme kinds (Gradient / Cycling / Phong3D / Pbr3D), live-preview the result
// into the main render window, save into UserColorThemeLibrary, or export to
// a standalone JSON file.
//
// Two-column layout:
//   Left column  — Target, Identity, Kind, Stops, Cycle, In-Set, Actions
//   Right column — 3D Lighting (Phong/PBR), Phong3D extras, Pbr3D extras
//
// Wiring contract with MainForm:
//   • OnPreviewMapChanged(IColorMap)  — MainForm pipes into ApplyPreviewMap()
//   • OnThemeSavedToLibrary(string)   — MainForm rebuilds combo, selects name
//   • OnRegionSelected(string)        — MainForm calls JumpToRegion(name)
// On close, MainForm calls ClearPreview() to restore committed map.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Views.Editors;
using static FracturingFog.Views.FormHelpers;

namespace FracturingFog.Views
{
    public sealed class ColorThemeEditor : Form
    {
        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 0x2;

        private const int LeftX = 8;
        private const int RightX = 388;
        private const int ColWidth = 370;
        private const int FormWidth = LeftX + ColWidth + 10 + ColWidth + LeftX; // 8+370+10+370+8 = 766

        [DllImport("User32.dll")] private static extern bool ReleaseCapture();
        [DllImport("User32.dll")] private static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        public event Action<ColorThemeData>? OnPreviewThemeChanged;
        public event Action<string>? OnThemeSavedToLibrary;
        public event Action<string>? OnRegionSelected;

        /// <summary>
        /// Fires when the user picks a theme from the editor's own theme
        /// combo. MainForm uses this to mirror the selection into its toolbar
        /// and FloatingMenu combos, making the editor the authoritative
        /// source of theme-name changes while it is open.
        /// </summary>
        public event Action<string>? OnEditorThemeSelected;

        /// <summary>
        /// Fires when the user clicks the "Help" button. MainForm shows the
        /// FloatingHelp window and selects its Color Theme Editor tab.
        /// </summary>
        public event EventHandler? OnHelpClick;

        private readonly Panel _root;
        private readonly Label _titleLabel;
        private readonly Button _closeButton;
        private readonly Button _helpButton;

        // Target
        private readonly ComboBox _regionCombo;
        private readonly ComboBox _themeCombo;

        // Identity
        private readonly TextBox _txName, _txCategory, _txDescription;
        private readonly NumericUpDown _txMaxZoom;
        private readonly CheckBox _chkMaxZoomEnabled;

        // Kind
        private readonly RadioButton _rdGradient, _rdCycling, _rdPhong, _rdPbr;

        // Sections
        private readonly GroupBox _targetBox;
        private readonly GroupBox _identBox;
        private readonly GroupBox _kindBox;
        private readonly GroupBox _stopsBox;
        private readonly GroupBox _cycleBox;
        private readonly GroupBox _threeDBox;
        private readonly GroupBox _phongBox;
        private readonly GroupBox _pbrBox;
        private readonly GroupBox _inSetBox;

        // Stops
        private readonly ColorStopListControl _stopsList;

        // Cycle
        private readonly NumericUpDown _txCycleSpeed;

        // 3D shared
        private readonly NumericUpDown _txSteepness, _txAmbient;
        private readonly LightSourceControl _keyLight, _fillLight;

        // Phong
        private readonly NumericUpDown _txKeySpec, _txFillSpec, _txFillDiff;

        // PBR
        private readonly ComboBox _cmbPbrMode;
        private readonly NumericUpDown _txGlowExp, _txGlowScale;
        private readonly MaterialBandListControl _bandList;

        // In-set
        private readonly CheckBox _chkInSetOverride;
        private readonly NumericUpDown _txInSetR, _txInSetG, _txInSetB;
        private readonly Panel _inSetSwatch;

        // Post-FX (Brightness / Contrast / Adaptive)
        private readonly GroupBox _postFxBox;
        private readonly CheckBox _chkUseBrightness, _chkUseContrast, _chkUseAdaptive;
        private readonly NumericUpDown _txBrightness, _txContrast, _txAdaptive;

        // Bottom actions
        private readonly CheckBox _chkLivePreview;
        private readonly Button _btnApply, _btnSave, _btnExport, _btnRevert, _btnNewBlank;

        private readonly System.Windows.Forms.Timer _debounce;
        private bool _suppressChange;
        private string? _loadedSourceName;

        public ColorThemeEditor(Form parent, string? initialThemeName, string? initialRegionName)
        {
            Owner = parent;
            Text = "Color Theme Editor";
            BackColor = Color.FromArgb(22, 22, 22);
            ForeColor = Color.FromArgb(220, 220, 220);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            KeyPreview = true;

            _root = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.FromArgb(22, 22, 22),
            };
            Controls.Add(_root);

            // ── Title bar ───────────────────────────────────────────────────
            _titleLabel = new Label
            {
                Text = "Color Theme Editor",
                Left = LeftX,
                Top = 6,
                AutoSize = true,
                ForeColor = Color.FromArgb(200, 200, 100),
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                BackColor = Color.Transparent,
            };
            _titleLabel.MouseDown += DragWindow;
            _root.Controls.Add(_titleLabel);

            _closeButton = new Button
            {
                Text = "X",
                Width = 24,
                Height = 24,
                Top = 4,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            };
            _closeButton.FlatAppearance.BorderSize = 0;
            _closeButton.Click += (s, e) => Close();
            _root.Controls.Add(_closeButton);

            _helpButton = new Button
            {
                Text = "?",
                Width = 26,
                Height = 24,
                Top = 4,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 60, 100),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Cursor = Cursors.Hand,
            };
            _helpButton.FlatAppearance.BorderColor = Color.FromArgb(80, 120, 180);
            _helpButton.Click += (s, e) => OnHelpClick?.Invoke(this, EventArgs.Empty);
            _root.Controls.Add(_helpButton);

            int leftY = 34;
            int rightY = 66;

            // ── Actions row (left column) ───────────────────────────────────
            _chkLivePreview = new CheckBox
            {
                Text = "Live preview",
                Left = LeftX,
                Top = leftY + 4,
                AutoSize = true,
                ForeColor = Color.FromArgb(200, 200, 120),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                BackColor = Color.Transparent,
                Checked = true,
            };
            _root.Controls.Add(_chkLivePreview);

            int buttonWidth = (this.Width / 3) + 18;
            _btnNewBlank = MakeAction("New ▾", LeftX + 110, leftY, buttonWidth, Color.FromArgb(55, 55, 75));
            var newMenu = new ContextMenuStrip
            {
                BackColor = Color.FromArgb(45, 45, 55),
                ForeColor = Color.White,
                ShowImageMargin = false,
            };
            var miNew = new ToolStripMenuItem("New");
            miNew.Click += (s, e) => NewBlankTheme();
            newMenu.Items.Add(miNew);
            var miCopy = new ToolStripMenuItem("Copy");
            miCopy.Click += (s, e) => CopyCurrentTheme();
            newMenu.Items.Add(miCopy);
            _btnNewBlank.Click += (s, e) => newMenu.Show(_btnNewBlank, new Point(0, _btnNewBlank.Height));
            _root.Controls.Add(_btnNewBlank);

            int buttonLeft = _btnNewBlank.Left + _btnNewBlank.Width + 2;
            _btnRevert = MakeAction("Revert", buttonLeft, leftY, buttonWidth, Color.FromArgb(80, 60, 30));
            _btnRevert.Click += (s, e) => RevertToSource();
            _root.Controls.Add(_btnRevert);

            buttonLeft = _btnRevert.Left + _btnRevert.Width + 2;
            int actionRow2 = leftY; // + 32;
            _btnApply = MakeAction("Apply", buttonLeft, actionRow2, buttonWidth, Color.FromArgb(40, 80, 40));
            _btnApply.Click += (s, e) => PushPreviewToMain();
            _root.Controls.Add(_btnApply);

            buttonLeft = _btnApply.Left + _btnApply.Width + 2;
            _btnSave = MakeAction("Save", buttonLeft, actionRow2, buttonWidth, Color.FromArgb(40, 60, 100));
            _btnSave.Click += (s, e) => SaveToLibrary();
            _root.Controls.Add(_btnSave);

            buttonLeft = _btnSave.Left + _btnSave.Width + 2;
            _btnExport = MakeAction("Export ▾", buttonLeft, actionRow2, buttonWidth + 30, Color.FromArgb(60, 50, 90));
            var exportMenu = new ContextMenuStrip
            {
                BackColor = Color.FromArgb(45, 45, 55),
                ForeColor = Color.White,
                ShowImageMargin = false,
            };
            var miExportJson = new ToolStripMenuItem("JSON…");
            miExportJson.Click += (s, e) => ExportJson();
            exportMenu.Items.Add(miExportJson);
            var miExportCs = new ToolStripMenuItem("C# Class…");
            miExportCs.Click += (s, e) => ExportCSharp();
            exportMenu.Items.Add(miExportCs);
            _btnExport.Click += (s, e) => exportMenu.Show(_btnExport, new Point(0, _btnExport.Height));
            _root.Controls.Add(_btnExport);

            leftY += 32;
            // ── Target ──────────────────────────────────────────────────────
            _targetBox = MakeGroup("Target", LeftX, leftY, ColWidth, 84);
            _root.Controls.Add(_targetBox);

            AddLabel(_targetBox, "Region:", 8, 22);
            _regionCombo = MakeCombo(82, 20, ColWidth - 96);
            RebuildRegionCombo(_regionCombo, OnRegionComboSelectionChanged);
            if (!string.IsNullOrEmpty(initialRegionName))
            {
                int idx = _regionCombo.FindStringExact(initialRegionName);
                if (idx >= 0) { _suppressChange = true; _regionCombo.SelectedIndex = idx; _suppressChange = false; }
            }
            _targetBox.Controls.Add(_regionCombo);

            AddLabel(_targetBox, "Theme:", 8, 52);
            _themeCombo = MakeCombo(82, 50, ColWidth - 96);
            BuildColorCombo(_themeCombo, OnThemeComboSelectionChanged);
            _targetBox.Controls.Add(_themeCombo);

            leftY = _targetBox.Bottom + 6;

            // ── Identity ────────────────────────────────────────────────────
            _identBox = MakeGroup("Identity", LeftX, leftY, ColWidth, 138);
            _root.Controls.Add(_identBox);

            AddLabel(_identBox, "Name:", 8, 22);
            _txName = MakeText(85, 20, ColWidth - 100);
            _txName.TextChanged += (s, e) => OnFieldChanged();
            _identBox.Controls.Add(_txName);

            AddLabel(_identBox, "Category:", 8, 48);
            _txCategory = MakeText(85, 46, ColWidth - 100);
            _txCategory.Text = "User";
            _txCategory.TextChanged += (s, e) => OnFieldChanged();
            _identBox.Controls.Add(_txCategory);

            AddLabel(_identBox, "Desc:", 8, 74);
            _txDescription = MakeText(85, 72, ColWidth - 100);
            _txDescription.TextChanged += (s, e) => OnFieldChanged();
            _identBox.Controls.Add(_txDescription);

            AddLabel(_identBox, "Max zoom:", 8, 102);
            _txMaxZoom = MakeNumeric(85, 100, 0M, 1_000_000_000M, 0M, 0);
            _txMaxZoom.Increment = 1000;
            _txMaxZoom.ValueChanged += (s, e) => OnFieldChanged();
            _identBox.Controls.Add(_txMaxZoom);

            _chkMaxZoomEnabled = new CheckBox
            {
                Text = "Limited",
                Left = 200,
                Top = 102,
                AutoSize = true,
                ForeColor = Color.FromArgb(180, 180, 180),
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 8.5f),
            };
            _chkMaxZoomEnabled.CheckedChanged += (s, e) =>
            {
                _txMaxZoom.Enabled = _chkMaxZoomEnabled.Checked;
                OnFieldChanged();
            };
            _identBox.Controls.Add(_chkMaxZoomEnabled);
            _txMaxZoom.Enabled = false;

            leftY = _identBox.Bottom + 6;

            // ── Kind radio ──────────────────────────────────────────────────
            _kindBox = MakeGroup("Kind", LeftX, leftY, ColWidth, 55);
            _root.Controls.Add(_kindBox);

            _rdGradient = MakeRadio("Gradient", 12, 22);
            _rdCycling = MakeRadio("Cycling", 100, 22);
            _rdPhong = MakeRadio("Phong3D", 184, 22);
            _rdPbr = MakeRadio("Pbr3D", 270, 22);
            _kindBox.Controls.Add(_rdGradient);
            _kindBox.Controls.Add(_rdCycling);
            _kindBox.Controls.Add(_rdPhong);
            _kindBox.Controls.Add(_rdPbr);
            _rdGradient.CheckedChanged += (s, e) => { if (_rdGradient.Checked) { UpdateVisibleKindSections(); OnFieldChanged(); } };
            _rdCycling.CheckedChanged += (s, e) => { if (_rdCycling.Checked) { UpdateVisibleKindSections(); OnFieldChanged(); } };
            _rdPhong.CheckedChanged += (s, e) => { if (_rdPhong.Checked) { UpdateVisibleKindSections(); OnFieldChanged(); } };
            _rdPbr.CheckedChanged += (s, e) => { if (_rdPbr.Checked) { UpdateVisibleKindSections(); OnFieldChanged(); } };

            leftY = _kindBox.Bottom + 6;

            // ── Stops ───────────────────────────────────────────────────────
            _stopsBox = MakeGroup("Color Stops", LeftX, leftY, ColWidth, 267);
            _root.Controls.Add(_stopsBox);

            buttonWidth = (_stopsBox.Width - 16) / 2;
            //var btnFromImage = MakeAction("From Image…", buttonWidth + 2, 12, buttonWidth, Color.FromArgb(60, 50, 90));
            //btnFromImage.Click += (s, e) => OpenImagePaletteDialog();
            //_stopsBox.Controls.Add(btnFromImage);

            _stopsList = new ColorStopListControl
            {
                Left = 6,
                Top = 24,
                Width = ColWidth - 14,
                Height = _stopsBox.Height - 30,
            };
            _stopsList.OnStopsChanged += (s, e) => OnFieldChanged();
            _stopsList.OnFromFile += (s,e) => OpenImagePaletteDialog();
            _stopsBox.Controls.Add(_stopsList);

            leftY = _stopsBox.Bottom + 6;

            // ── Cycle ───────────────────────────────────────────────────────
            _cycleBox = MakeGroup("Cycle", LeftX, leftY, ColWidth, 52);
            _root.Controls.Add(_cycleBox);

            AddLabel(_cycleBox, "Speed:", 8, 22);
            _txCycleSpeed = MakeNumeric(85, 20, 0.0001M, 10M, 0.02M, 4);
            _txCycleSpeed.ValueChanged += (s, e) => OnFieldChanged();
            _cycleBox.Controls.Add(_txCycleSpeed);

            leftY = _cycleBox.Bottom + 6;

            // ── In-set ──────────────────────────────────────────────────────
            _inSetBox = MakeGroup("In-Set (Interior)", LeftX, leftY, ColWidth, 62);
            _root.Controls.Add(_inSetBox);

            _chkInSetOverride = new CheckBox
            {
                Text = "Override",
                Left = 8,
                Top = 26,
                AutoSize = true,
                ForeColor = Color.FromArgb(180, 180, 180),
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            };
            _chkInSetOverride.CheckedChanged += (s, e) =>
            {
                bool on = _chkInSetOverride.Checked;
                _txInSetR.Enabled = _txInSetG.Enabled = _txInSetB.Enabled = on;
                _inSetSwatch.BackColor = on
                    ? Color.FromArgb((int)_txInSetR.Value, (int)_txInSetG.Value, (int)_txInSetB.Value)
                    : Color.Black;
                OnFieldChanged();
            };
            _inSetBox.Controls.Add(_chkInSetOverride);

            _inSetSwatch = new Panel
            {
                Left = 85,
                Top = 24,
                Width = 45,
                Height = 24,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.Black,
                Cursor = Cursors.Hand,
            };
            _inSetSwatch.Click += (s, e) =>
            {
                if (!_chkInSetOverride.Checked) return;
                using var dlg = new ColorDialog { Color = _inSetSwatch.BackColor, FullOpen = true, AnyColor = true };
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _txInSetR.Value = dlg.Color.R;
                    _txInSetG.Value = dlg.Color.G;
                    _txInSetB.Value = dlg.Color.B;
                    _inSetSwatch.BackColor = dlg.Color;
                    OnFieldChanged();
                }
            };
            _inSetBox.Controls.Add(_inSetSwatch);

            _txInSetR = MakeByte(_inSetSwatch.Right + 6, 24);
            _txInSetG = MakeByte(_txInSetR.Right + 4, 24);
            _txInSetB = MakeByte(_txInSetG.Right + 4, 24);
            EventHandler swatchSync = (s, e) =>
            {
                _inSetSwatch.BackColor = Color.FromArgb((int)_txInSetR.Value, (int)_txInSetG.Value, (int)_txInSetB.Value);
                OnFieldChanged();
            };
            _txInSetR.ValueChanged += swatchSync;
            _txInSetG.ValueChanged += swatchSync;
            _txInSetB.ValueChanged += swatchSync;
            _txInSetR.Enabled = _txInSetG.Enabled = _txInSetB.Enabled = false;
            _inSetBox.Controls.Add(_txInSetR);
            _inSetBox.Controls.Add(_txInSetG);
            _inSetBox.Controls.Add(_txInSetB);

            leftY = _inSetBox.Bottom + 6;

            // ── Post-FX defaults ────────────────────────────────────────────
            // Theme can record default Brightness/Contrast/Adaptive values.
            // The "Set by theme" checkbox toggles whether the corresponding
            // field is persisted; when unchecked, the field is null and the
            // host slider stays at whatever the user left it on theme switch.
            _postFxBox = MakeGroup("Post-FX Defaults", LeftX, leftY, ColWidth, 112);
            _root.Controls.Add(_postFxBox);

            const int postFxNumericLeft = 140;

            _chkUseBrightness = MakePostFxCheck("Brightness", 22);
            _postFxBox.Controls.Add(_chkUseBrightness);
            _txBrightness = MakeNumeric(postFxNumericLeft, 20, -100M, 100M, 0M, 0);
            _txBrightness.Increment = 1;
            _txBrightness.ValueChanged += (s, e) => OnFieldChanged();
            _postFxBox.Controls.Add(_txBrightness);

            _chkUseContrast = MakePostFxCheck("Contrast", 50);
            _postFxBox.Controls.Add(_chkUseContrast);
            _txContrast = MakeNumeric(postFxNumericLeft, 48, -100M, 100M, 0M, 0);
            _txContrast.Increment = 1;
            _txContrast.ValueChanged += (s, e) => OnFieldChanged();
            _postFxBox.Controls.Add(_txContrast);

            _chkUseAdaptive = MakePostFxCheck("Adaptive", 78);
            _postFxBox.Controls.Add(_chkUseAdaptive);
            _txAdaptive = MakeNumeric(postFxNumericLeft, 76, 0M, 100M, 0M, 0);
            _txAdaptive.Increment = 1;
            _txAdaptive.ValueChanged += (s, e) => OnFieldChanged();
            _postFxBox.Controls.Add(_txAdaptive);

            _chkUseBrightness.CheckedChanged += (s, e) => { _txBrightness.Enabled = _chkUseBrightness.Checked; OnFieldChanged(); };
            _chkUseContrast.CheckedChanged += (s, e) => { _txContrast.Enabled = _chkUseContrast.Checked; OnFieldChanged(); };
            _chkUseAdaptive.CheckedChanged += (s, e) => { _txAdaptive.Enabled = _chkUseAdaptive.Checked; OnFieldChanged(); };
            _txBrightness.Enabled = _txContrast.Enabled = _txAdaptive.Enabled = false;

            leftY = _postFxBox.Bottom + 8;

            

            int leftEnd = actionRow2 + 32;

            // ── 3D shared (right column) ────────────────────────────────────
            _threeDBox = MakeGroup("3D Lighting (Phong/PBR)", RightX, rightY, ColWidth, 60);
            _root.Controls.Add(_threeDBox);

            AddLabel(_threeDBox, "Steepness:", 8, 22);
            _txSteepness = MakeNumeric(90, 20, 0.1M, 10M, 1.6M, 2);
            _txSteepness.Width = 80;
            _txSteepness.ValueChanged += (s, e) => OnFieldChanged();
            _threeDBox.Controls.Add(_txSteepness);

            AddLabel(_threeDBox, "Ambient:", 200, 22);
            _txAmbient = MakeNumeric(270, 20, 0M, 1M, 0.12M, 3);
            _txAmbient.Width = 80;
            _txAmbient.ValueChanged += (s, e) => OnFieldChanged();
            _threeDBox.Controls.Add(_txAmbient);

            _keyLight = new LightSourceControl("Key Light")
            {
                Left = 6,
                Top = 50,
                Width = ColWidth - 14,
            };
            _keyLight.OnChanged += (s, e) => OnFieldChanged();
            _threeDBox.Controls.Add(_keyLight);

            _fillLight = new LightSourceControl("Fill Light")
            {
                Left = 6,
                Top = _keyLight.Bottom + 6,
                Width = ColWidth - 14,
            };
            _fillLight.OnChanged += (s, e) => OnFieldChanged();
            _threeDBox.Controls.Add(_fillLight);

            _threeDBox.Height = _fillLight.Bottom + 12;

            rightY = _threeDBox.Bottom + 6;

            // ── Phong extras ────────────────────────────────────────────────
            _phongBox = MakeGroup("Phong3D Extras", RightX, rightY, ColWidth, 110);
            _root.Controls.Add(_phongBox);

            AddLabel(_phongBox, "Key spec:", 8, 24);
            _txKeySpec = MakeNumeric(90, 22, 0M, 10M, 0.85M, 3);
            _txKeySpec.ValueChanged += (s, e) => OnFieldChanged();
            _phongBox.Controls.Add(_txKeySpec);

            AddLabel(_phongBox, "Fill spec:", 8, 52);
            _txFillSpec = MakeNumeric(90, 50, 0M, 10M, 0.25M, 3);
            _txFillSpec.ValueChanged += (s, e) => OnFieldChanged();
            _phongBox.Controls.Add(_txFillSpec);

            AddLabel(_phongBox, "Fill diff:", 8, 80);
            _txFillDiff = MakeNumeric(90, 78, 0M, 10M, 0.35M, 3);
            _txFillDiff.ValueChanged += (s, e) => OnFieldChanged();
            _phongBox.Controls.Add(_txFillDiff);

            rightY = _phongBox.Bottom + 6;

            // ── PBR extras ──────────────────────────────────────────────────
            _pbrBox = MakeGroup("Pbr3D Extras", RightX, rightY, ColWidth, 320);
            _root.Controls.Add(_pbrBox);

            AddLabel(_pbrBox, "Lighting:", 8, 24);
            _cmbPbrMode = MakeCombo(90, 22, 240);
            foreach (var v in Enum.GetValues<PbrLightingMode>())
                _cmbPbrMode.Items.Add(v.ToString());
            _cmbPbrMode.SelectedIndex = 0;
            _cmbPbrMode.SelectedIndexChanged += (s, e) => OnFieldChanged();
            _pbrBox.Controls.Add(_cmbPbrMode);

            AddLabel(_pbrBox, "Glow exp:", 8, 54);
            _txGlowExp = MakeNumeric(90, 52, 0M, 50M, 8M, 2);
            _txGlowExp.ValueChanged += (s, e) => OnFieldChanged();
            _pbrBox.Controls.Add(_txGlowExp);

            AddLabel(_pbrBox, "Glow scl:", 8, 82);
            _txGlowScale = MakeNumeric(90, 80, 0M, 10M, 0M, 3);
            _txGlowScale.ValueChanged += (s, e) => OnFieldChanged();
            _pbrBox.Controls.Add(_txGlowScale);

            AddLabel(_pbrBox, "Material bands:", 8, 110);
            _bandList = new MaterialBandListControl
            {
                Left = 6,
                Top = 128,
                Width = ColWidth - 14,
                Height = _pbrBox.Height - 138,
            };
            _bandList.OnChanged += (s, e) => OnFieldChanged();
            _pbrBox.Controls.Add(_bandList);

            rightY = _pbrBox.Bottom + 6;

            // ── Form size ───────────────────────────────────────────────────
            int finalHeight = Math.Max(leftEnd, rightY) + 12;
            ClientSize = new Size(FormWidth, finalHeight);
            _closeButton.Left = ClientSize.Width - _closeButton.Width - 4;
            _helpButton.Left = _closeButton.Left - _helpButton.Width - 4;

            if (parent != null)
            {
                int x = Math.Max(0, parent.Location.X + parent.Width + 8);
                int y = Math.Max(0, parent.Location.Y + 40);
                // Keep on the primary screen if the natural placement falls off.
                var bounds = Screen.GetWorkingArea(parent);
                if (x + ClientSize.Width > bounds.Right) x = Math.Max(bounds.Left, bounds.Right - ClientSize.Width - 8);
                if (y + ClientSize.Height > bounds.Bottom) y = Math.Max(bounds.Top, bounds.Bottom - ClientSize.Height - 8);
                Location = new Point(x, y);
            }

            // Drag from title and from empty background.
            MouseDown += DragWindow;
            _root.MouseDown += DragWindow;

            _debounce = new System.Windows.Forms.Timer { Interval = 150 };
            _debounce.Tick += (s, e) =>
            {
                _debounce.Stop();
                if (_chkLivePreview.Checked) PushPreviewToMain();
            };

            // Initial load from the supplied theme.
            if (!string.IsNullOrEmpty(initialThemeName))
            {
                int idx = _themeCombo.FindStringExact(initialThemeName);
                if (idx >= 0)
                {
                    _suppressChange = true;
                    _themeCombo.SelectedIndex = idx;
                    _suppressChange = false;
                    LoadFromTheme(initialThemeName);
                }
            }
            UpdateVisibleKindSections();
        }

        // ── Drag ────────────────────────────────────────────────────────────

        private void DragWindow(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private static GroupBox MakeGroup(string title, int left, int top, int width, int height)
        {
            return new GroupBox
            {
                Text = title,
                Left = left,
                Top = top,
                Width = width,
                Height = height,
                ForeColor = Color.FromArgb(155, 155, 155),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                BackColor = Color.FromArgb(22, 22, 22),
            };
        }

        private static Label AddLabel(Control parent, string text, int left, int top)
        {
            var lbl = new Label
            {
                Text = text,
                Left = left,
                Top = top,
                AutoSize = true,
                ForeColor = Color.FromArgb(180, 180, 180),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                BackColor = Color.Transparent,
            };
            parent.Controls.Add(lbl);
            return lbl;
        }

        private static TextBox MakeText(int left, int top, int width)
        {
            return new TextBox
            {
                Left = left,
                Top = top,
                Width = width,
                Height = 24,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.FromArgb(220, 220, 220),
                Font = new Font("Consolas", 9f),
                BorderStyle = BorderStyle.FixedSingle,
            };
        }

        private static ComboBox MakeCombo(int left, int top, int width)
        {
            return new ComboBox
            {
                Left = left,
                Top = top,
                Width = width,
                Height = 24,
                BackColor = Color.FromArgb(55, 55, 55),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                FlatStyle = FlatStyle.Flat,
            };
        }

        private static NumericUpDown MakeNumeric(int left, int top, decimal min, decimal max, decimal value, int decimals)
        {
            return new NumericUpDown
            {
                Left = left,
                Top = top,
                Width = 95,
                Height = 24,
                Minimum = min,
                Maximum = max,
                Value = Math.Clamp(value, min, max),
                DecimalPlaces = decimals,
                Increment = decimals > 0 ? (decimal)Math.Pow(10, -Math.Max(1, decimals - 1)) : 1M,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.FromArgb(220, 220, 220),
                BorderStyle = BorderStyle.FixedSingle,
                TextAlign = HorizontalAlignment.Right,
            };
        }

        private static NumericUpDown MakeByte(int left, int top)
        {
            return new NumericUpDown
            {
                Left = left,
                Top = top,
                Width = 60,
                Height = 24,
                Minimum = 0,
                Maximum = 255,
                Value = 0,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.FromArgb(220, 220, 220),
                BorderStyle = BorderStyle.FixedSingle,
                TextAlign = HorizontalAlignment.Right,
            };
        }

        private static CheckBox MakePostFxCheck(string text, int top)
        {
            return new CheckBox
            {
                Text = text, // + " — set by theme",
                Left = 8,
                Top = top + 2,
                AutoSize = true,
                ForeColor = Color.FromArgb(180, 180, 180),
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 8.5f),
            };
        }

        private static RadioButton MakeRadio(string text, int left, int top)
        {
            return new RadioButton
            {
                Text = text,
                Left = left,
                Top = top,
                AutoSize = true,
                ForeColor = Color.FromArgb(220, 220, 220),
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            };
        }

        private static Button MakeAction(string text, int left, int top, int width, Color bg)
        {
            var b = new Button
            {
                Text = text,
                Left = left,
                Top = top,
                Width = width,
                Height = 28,
                FlatStyle = FlatStyle.Flat,
                BackColor = bg,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(
                Math.Min(255, bg.R + 40), Math.Min(255, bg.G + 40), Math.Min(255, bg.B + 40));
            return b;
        }

        // ── Combo callbacks ─────────────────────────────────────────────────

        private void OnRegionComboSelectionChanged(object? sender, EventArgs e)
        {
            if (_suppressChange || sender is not ComboBox cb) return;
            string? name = cb.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(name) || name == "— select region —") return;
            OnRegionSelected?.Invoke(name);
        }

        private void OnThemeComboSelectionChanged(object? sender, EventArgs e)
        {
            if (_suppressChange || sender is not ComboBox cb) return;
            string? name = cb.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(name) || name.StartsWith("—")) return;
            LoadFromTheme(name);
            // Selecting a theme is an explicit user action — push the
            // corresponding map even if the debounce timer hasn't fired yet
            // and regardless of the live-preview checkbox state, so that the
            // user sees the chosen theme without having to hit Apply.
            PushPreviewToMain();
            // Tell the host so MainForm + FloatingMenu combos mirror this
            // pick. Editor combos are *not* updated from the other direction
            // — once the editor is open it owns the selection until close.
            OnEditorThemeSelected?.Invoke(name);
        }

        // ── Load / Save ─────────────────────────────────────────────────────

        private void LoadFromTheme(string themeName)
        {
            _loadedSourceName = themeName;
            var map = ColorPalette.GetPaletteByName(themeName);
            var data = DataDrivenColorThemes.Export(map);
            if (data == null)
            {
                _titleLabel.Text = $"\"{themeName}\" — no editable params. Click \"New Blank\".";
                _btnApply.Enabled = false;
                _btnSave.Enabled = false;
                _btnExport.Enabled = false;
                return;
            }
            _btnApply.Enabled = true;
            _btnSave.Enabled = true;
            _btnExport.Enabled = true;
            _titleLabel.Text = "Color Theme Editor";
            LoadData(data);
        }

        private void OpenImagePaletteDialog()
        {
            using var dlg = new ImagePaletteDialog(this);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            var stops = dlg.Result;
            if (stops == null || stops.Count < 2) return;

            _suppressChange = true;
            try { _stopsList.LoadStops(stops); }
            finally { _suppressChange = false; }

            // Heuristic: image-derived palettes work best as a Gradient.
            // Leave the user's existing Kind alone if it's already set to
            // something compatible; otherwise nudge to Gradient.
            if (!_rdGradient.Checked && !_rdCycling.Checked && !_rdPhong.Checked && !_rdPbr.Checked)
                _rdGradient.Checked = true;

            OnFieldChanged();
            PushPreviewToMain();
        }

        private void NewBlankTheme()
        {
            _loadedSourceName = null;
            var data = new ColorThemeData
            {
                Name = "My Theme",
                Category = "User",
                Description = "",
                Kind = ColorThemeKind.Gradient,
                Stops = new List<ColorStopData>
                {
                    new ColorStopData { Position = 0f, R = 0, G = 0, B = 0 },
                    new ColorStopData { Position = 1f, R = 255, G = 255, B = 255 },
                },
            };
            _btnApply.Enabled = true;
            _btnSave.Enabled = true;
            _btnExport.Enabled = true;
            _titleLabel.Text = "Color Theme Editor — new theme";
            LoadData(data);
            PushPreviewToMain();
        }

        private void CopyCurrentTheme()
        {
            var data = BuildData();
            string baseName = string.IsNullOrWhiteSpace(data.Name) ? "Theme" : data.Name;
            data.Name = "Copy of " + baseName;
            _loadedSourceName = null;
            _btnApply.Enabled = true;
            _btnSave.Enabled = true;
            _btnExport.Enabled = true;
            _titleLabel.Text = "Color Theme Editor — new theme (copy)";
            LoadData(data);
            PushPreviewToMain();
        }

        private void RevertToSource()
        {
            if (string.IsNullOrEmpty(_loadedSourceName)) return;
            LoadFromTheme(_loadedSourceName);
            PushPreviewToMain();
        }

        private void LoadData(ColorThemeData data)
        {
            _suppressChange = true;
            try
            {
                _txName.Text = data.Name ?? "";
                _txCategory.Text = string.IsNullOrEmpty(data.Category) ? "User" : data.Category;
                _txDescription.Text = data.Description ?? "";

                if (data.MaxRecommendedZoom.HasValue && !double.IsPositiveInfinity(data.MaxRecommendedZoom.Value))
                {
                    _chkMaxZoomEnabled.Checked = true;
                    _txMaxZoom.Value = (decimal)Math.Clamp(data.MaxRecommendedZoom.Value, 0d, 1_000_000_000d);
                    _txMaxZoom.Enabled = true;
                }
                else
                {
                    _chkMaxZoomEnabled.Checked = false;
                    _txMaxZoom.Value = 0;
                    _txMaxZoom.Enabled = false;
                }

                switch (data.Kind)
                {
                    case ColorThemeKind.Gradient: _rdGradient.Checked = true; break;
                    case ColorThemeKind.Cycling: _rdCycling.Checked = true; break;
                    case ColorThemeKind.Phong3D: _rdPhong.Checked = true; break;
                    case ColorThemeKind.Pbr3D: _rdPbr.Checked = true; break;
                }

                _stopsList.LoadStops(data.Stops);
                _txCycleSpeed.Value = ClampDec((decimal)data.CycleSpeed, _txCycleSpeed.Minimum, _txCycleSpeed.Maximum);

                _txSteepness.Value = ClampDec((decimal)data.Steepness, _txSteepness.Minimum, _txSteepness.Maximum);
                _txAmbient.Value = ClampDec((decimal)data.Ambient, _txAmbient.Minimum, _txAmbient.Maximum);
                _keyLight.Load(data.KeyLight);
                _fillLight.Load(data.FillLight);

                _txKeySpec.Value = ClampDec((decimal)data.KeySpecScale, _txKeySpec.Minimum, _txKeySpec.Maximum);
                _txFillSpec.Value = ClampDec((decimal)data.FillSpecScale, _txFillSpec.Minimum, _txFillSpec.Maximum);
                _txFillDiff.Value = ClampDec((decimal)data.FillDiffScale, _txFillDiff.Minimum, _txFillDiff.Maximum);

                int pbrIdx = _cmbPbrMode.FindStringExact(data.PbrLightingMode.ToString());
                _cmbPbrMode.SelectedIndex = pbrIdx >= 0 ? pbrIdx : 0;
                _txGlowExp.Value = ClampDec((decimal)data.GlowBoostExponent, _txGlowExp.Minimum, _txGlowExp.Maximum);
                _txGlowScale.Value = ClampDec((decimal)data.GlowBoostScale, _txGlowScale.Minimum, _txGlowScale.Maximum);
                _bandList.LoadBands(data.MaterialBands);

                if (data.InSetColor != null)
                {
                    _chkInSetOverride.Checked = true;
                    _txInSetR.Value = data.InSetColor.R;
                    _txInSetG.Value = data.InSetColor.G;
                    _txInSetB.Value = data.InSetColor.B;
                    _txInSetR.Enabled = _txInSetG.Enabled = _txInSetB.Enabled = true;
                    _inSetSwatch.BackColor = Color.FromArgb(data.InSetColor.R, data.InSetColor.G, data.InSetColor.B);
                }
                else
                {
                    _chkInSetOverride.Checked = false;
                    _txInSetR.Value = _txInSetG.Value = _txInSetB.Value = 0;
                    _txInSetR.Enabled = _txInSetG.Enabled = _txInSetB.Enabled = false;
                    _inSetSwatch.BackColor = Color.Black;
                }

                _chkUseBrightness.Checked = data.Brightness.HasValue;
                _txBrightness.Enabled = _chkUseBrightness.Checked;
                _txBrightness.Value = ClampDec(data.Brightness ?? 0, _txBrightness.Minimum, _txBrightness.Maximum);

                _chkUseContrast.Checked = data.Contrast.HasValue;
                _txContrast.Enabled = _chkUseContrast.Checked;
                _txContrast.Value = ClampDec(data.Contrast ?? 0, _txContrast.Minimum, _txContrast.Maximum);

                _chkUseAdaptive.Checked = data.Adaptive.HasValue;
                _txAdaptive.Enabled = _chkUseAdaptive.Checked;
                _txAdaptive.Value = ClampDec(data.Adaptive ?? 0, _txAdaptive.Minimum, _txAdaptive.Maximum);

                UpdateVisibleKindSections();
            }
            finally
            {
                _suppressChange = false;
            }
        }

        private ColorThemeData BuildData()
        {
            var data = new ColorThemeData
            {
                Name = string.IsNullOrWhiteSpace(_txName.Text) ? "Unnamed Theme" : _txName.Text.Trim(),
                Category = string.IsNullOrWhiteSpace(_txCategory.Text) ? "User" : _txCategory.Text.Trim(),
                Description = _txDescription.Text ?? "",
                MaxRecommendedZoom = _chkMaxZoomEnabled.Checked ? (double?)(double)_txMaxZoom.Value : null,
                Kind = SelectedKind(),
                Stops = _stopsList.GetStops(),
                CycleSpeed = (float)_txCycleSpeed.Value,
                Steepness = (float)_txSteepness.Value,
                Ambient = (float)_txAmbient.Value,
                KeyLight = _keyLight.Save(),
                FillLight = _fillLight.Save(),
                KeySpecScale = (float)_txKeySpec.Value,
                FillSpecScale = (float)_txFillSpec.Value,
                FillDiffScale = (float)_txFillDiff.Value,
                PbrLightingMode = Enum.TryParse<PbrLightingMode>((string?)_cmbPbrMode.SelectedItem ?? "PBRRealistic", out var m) ? m : PbrLightingMode.PBRRealistic,
                GlowBoostExponent = (float)_txGlowExp.Value,
                GlowBoostScale = (float)_txGlowScale.Value,
                MaterialBands = _bandList.GetBands(),
                InSetColor = _chkInSetOverride.Checked
                    ? new InSetColorData((byte)_txInSetR.Value, (byte)_txInSetG.Value, (byte)_txInSetB.Value)
                    : null,
                Brightness = _chkUseBrightness.Checked ? (int?)(int)_txBrightness.Value : null,
                Contrast = _chkUseContrast.Checked ? (int?)(int)_txContrast.Value : null,
                Adaptive = _chkUseAdaptive.Checked ? (int?)(int)_txAdaptive.Value : null,
            };
            return data;
        }

        private ColorThemeKind SelectedKind()
        {
            if (_rdCycling.Checked) return ColorThemeKind.Cycling;
            if (_rdPhong.Checked) return ColorThemeKind.Phong3D;
            if (_rdPbr.Checked) return ColorThemeKind.Pbr3D;
            return ColorThemeKind.Gradient;
        }

        private void UpdateVisibleKindSections()
        {
            var kind = SelectedKind();
            _cycleBox.Visible = kind != ColorThemeKind.Gradient;
            _threeDBox.Visible = kind == ColorThemeKind.Phong3D || kind == ColorThemeKind.Pbr3D;
            _phongBox.Visible = kind == ColorThemeKind.Phong3D;
            _pbrBox.Visible = kind == ColorThemeKind.Pbr3D;
        }

        // ── Field-changed pipeline ──────────────────────────────────────────

        private void OnFieldChanged()
        {
            if (_suppressChange) return;
            if (!_chkLivePreview.Checked) return;
            _debounce.Stop();
            _debounce.Start();
        }

        private void PushPreviewToMain()
        {
            var data = BuildData();
            if (data.Stops == null || data.Stops.Count < 2) return;
            // Validate the map can be built before notifying the host so we
            // don't push a half-formed theme.
            var map = DataDrivenColorThemes.Create(data);
            if (map == null) return;
            OnPreviewThemeChanged?.Invoke(data);
        }

        private void SaveToLibrary()
        {
            var data = BuildData();
            if (string.IsNullOrWhiteSpace(data.Name))
            {
                MessageBox.Show(this, "Name cannot be empty.", "Save Theme",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (data.Stops == null || data.Stops.Count < 2)
            {
                MessageBox.Show(this, "Need at least 2 color stops.", "Save Theme",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            bool exists = UserColorThemeLibrary.Instance.Themes.Any(t => t.Name.Equals(data.Name, StringComparison.OrdinalIgnoreCase));
            if (exists)
            {
                var r = MessageBox.Show(this,
                    $"A user theme named \"{data.Name}\" already exists.\n\nReplace it?",
                    "Replace Theme",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return;
            }

            UserColorThemeLibrary.Instance.ReplaceOrAdd(data);
            OnThemeSavedToLibrary?.Invoke(data.Name);

            _suppressChange = true;
            BuildColorCombo(_themeCombo, OnThemeComboSelectionChanged);
            int idx = _themeCombo.FindStringExact(data.Name);
            if (idx >= 0) _themeCombo.SelectedIndex = idx;
            _suppressChange = false;
            _loadedSourceName = data.Name;

            MessageBox.Show(this, $"\"{data.Name}\" saved.", "Save Theme",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ExportJson()
        {
            var data = BuildData();
            using var sfd = new SaveFileDialog
            {
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                FileName = SanitizeFileName(data.Name) + ".json",
                Title = "Export Color Theme",
            };
            if (sfd.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                var opts = UserColorThemeLibrary.BuildJsonOptions();
                string json = JsonSerializer.Serialize(new[] { data }, opts);
                File.WriteAllText(sfd.FileName, json);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Export failed:\n" + ex.Message, "Export",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static string SanitizeFileName(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return string.IsNullOrWhiteSpace(s) ? "theme" : s;
        }

        // ── C# concrete-class export ────────────────────────────────────────

        private void ExportCSharp()
        {
            var data = BuildData();
            if (data.Stops == null || data.Stops.Count < 2)
            {
                MessageBox.Show(this, "Need at least 2 color stops.", "Export C#",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string className = MakeClassName(data.Name);

            using var sfd = new SaveFileDialog
            {
                Filter = "C# files (*.cs)|*.cs|All files (*.*)|*.*",
                FileName = className + ".cs",
                Title = "Export Color Theme as C# Class",
            };
            if (sfd.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                string code = BuildCSharpSource(data, className);
                File.WriteAllText(sfd.FileName, code);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Export failed:\n" + ex.Message, "Export C#",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static string MakeClassName(string themeName)
        {
            var sb = new StringBuilder();
            bool upper = true;
            foreach (char c in themeName ?? "")
            {
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(upper ? char.ToUpperInvariant(c) : c);
                    upper = false;
                }
                else
                {
                    upper = true;
                }
            }
            if (sb.Length == 0) sb.Append("MyTheme");
            if (char.IsDigit(sb[0])) sb.Insert(0, '_');
            sb.Append("Theme");
            return sb.ToString();
        }

        private static string BuildCSharpSource(ColorThemeData data, string className)
        {
            bool is3D = data.Kind == ColorThemeKind.Phong3D || data.Kind == ColorThemeKind.Pbr3D;
            bool isPbr = data.Kind == ColorThemeKind.Pbr3D;
            bool isPhong = data.Kind == ColorThemeKind.Phong3D;
            bool isCycling = data.Kind == ColorThemeKind.Cycling;
            bool hasPostFx = data.Brightness.HasValue || data.Contrast.HasValue || data.Adaptive.HasValue;
            bool hasInSet = data.InSetColor != null;
            bool emitBands = isPbr && data.MaterialBands != null && data.MaterialBands.Count > 0;
            bool emitGlow = isPbr && data.GlowBoostScale != 0f;

            string baseClass = data.Kind switch
            {
                ColorThemeKind.Gradient => "GradientColorMap",
                ColorThemeKind.Cycling => "CyclingGradientColorMap",
                ColorThemeKind.Phong3D => "GradientPhong3DBase",
                ColorThemeKind.Pbr3D => "PbrGradient3DBase",
                _ => "GradientColorMap",
            };

            var bases = new List<string> { baseClass };
            if (hasPostFx) bases.Add("IThemePostFx");

            var sb = new StringBuilder();
            sb.AppendLine("// Auto-generated by Color Theme Editor.");
            sb.AppendLine("// Drop into Models/ColorSchemes (or any sub-folder of FracturingFog.Models).");
            sb.AppendLine("// Register by adding `new " + className + "()` to ColorPalette.BuiltIns.");
            sb.AppendLine();
            if (isPbr) sb.AppendLine("using System;");
            sb.AppendLine("using System.Drawing;");
            if (hasPostFx || hasInSet) sb.AppendLine("using FracturingFog.Interefaces;");
            sb.AppendLine();
            sb.AppendLine("namespace FracturingFog.Models");
            sb.AppendLine("{");
            sb.AppendLine("    public sealed class " + className + " : " + string.Join(", ", bases));
            sb.AppendLine("    {");

            sb.AppendLine("        public static string Name => " + Quote(data.Name) + ";");
            sb.AppendLine("        public static string Category => " + Quote(data.Category ?? "User") + ";");
            sb.AppendLine("        public static string Description => " + Quote(data.Description ?? "") + ";");
            if (data.MaxRecommendedZoom.HasValue && !double.IsPositiveInfinity(data.MaxRecommendedZoom.Value))
                sb.AppendLine("        public static double MaxRecommendedZoom => " + Dbl(data.MaxRecommendedZoom.Value) + ";");
            sb.AppendLine();

            if (isCycling || is3D)
                sb.AppendLine("        protected override float CycleSpeed => " + Flt(data.CycleSpeed) + ";");
            if (is3D)
            {
                sb.AppendLine("        protected override float Steepness => " + Flt(data.Steepness) + ";");
                sb.AppendLine("        protected override float Ambient => " + Flt(data.Ambient) + ";");
            }
            if (isPhong)
            {
                sb.AppendLine("        protected override float KeySpecScale => " + Flt(data.KeySpecScale) + ";");
                sb.AppendLine("        protected override float FillSpecScale => " + Flt(data.FillSpecScale) + ";");
                sb.AppendLine("        protected override float FillDiffScale => " + Flt(data.FillDiffScale) + ";");
            }
            if (isPbr)
            {
                sb.AppendLine("        protected override PbrLightingMode LightingMode => PbrLightingMode." + data.PbrLightingMode + ";");
            }
            if (isCycling || is3D || isPbr || isPhong) sb.AppendLine();

            if (emitBands)
            {
                sb.AppendLine("        private static readonly (float UpperT, float Metal, float Roughness)[] Bands = new[]");
                sb.AppendLine("        {");
                foreach (var b in data.MaterialBands!)
                {
                    sb.AppendLine("            (" + Flt(b.UpperT) + ", " + Flt(b.Metal) + ", " + Flt(b.Roughness) + "),");
                }
                sb.AppendLine("        };");
                sb.AppendLine();
            }

            sb.AppendLine("        public " + className + "()");
            sb.AppendLine("        {");
            if (data.Stops != null)
            {
                foreach (var s in data.Stops)
                {
                    sb.AppendLine("            Stops.Add(new ColorStop(" + Flt(s.Position)
                        + ", Color.FromArgb(" + s.R + ", " + s.G + ", " + s.B + ")));");
                }
            }
            if (is3D)
            {
                if (data.KeyLight != null) AppendLight(sb, "KeyLight", data.KeyLight);
                if (data.FillLight != null) AppendLight(sb, "FillLight", data.FillLight);
            }
            sb.AppendLine("        }");

            if (emitBands)
            {
                sb.AppendLine();
                sb.AppendLine("        protected override PbrMaterial BuildMaterial(float t, float r, float g, float b)");
                sb.AppendLine("        {");
                sb.AppendLine("            for (int i = 0; i < Bands.Length - 1; i++)");
                sb.AppendLine("            {");
                sb.AppendLine("                if (t < Bands[i].UpperT)");
                sb.AppendLine("                    return new PbrMaterial(r, g, b, Bands[i].Metal, Bands[i].Roughness);");
                sb.AppendLine("            }");
                sb.AppendLine("            var last = Bands[Bands.Length - 1];");
                sb.AppendLine("            return new PbrMaterial(r, g, b, last.Metal, last.Roughness);");
                sb.AppendLine("        }");
            }

            if (emitGlow)
            {
                sb.AppendLine();
                sb.AppendLine("        protected override float GlowBoost(float t)");
                sb.AppendLine("            => " + Flt(data.GlowBoostScale) + " * MathF.Pow(t, " + Flt(data.GlowBoostExponent) + ");");
            }

            if (hasInSet)
            {
                uint argb = data.InSetColor!.ToPackedArgb();
                sb.AppendLine();
                sb.AppendLine("        uint IColorMap.InSetColor => 0x" + argb.ToString("X8", CultureInfo.InvariantCulture) + "u;");
            }

            if (hasPostFx)
            {
                sb.AppendLine();
                sb.AppendLine("        int? IThemePostFx.ThemeBrightness => " + NullableInt(data.Brightness) + ";");
                sb.AppendLine("        int? IThemePostFx.ThemeContrast => " + NullableInt(data.Contrast) + ";");
                sb.AppendLine("        int? IThemePostFx.ThemeAdaptive => " + NullableInt(data.Adaptive) + ";");
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static void AppendLight(StringBuilder sb, string fieldName, LightSourceData l)
        {
            sb.AppendLine("            " + fieldName + " = new LightSource(");
            sb.AppendLine("                lx: " + Flt(l.Lx) + ", ly: " + Flt(l.Ly) + ", lz: " + Flt(l.Lz) + ",");
            sb.AppendLine("                diffR: " + Flt(l.DiffR) + ", diffG: " + Flt(l.DiffG) + ", diffB: " + Flt(l.DiffB) + ",");
            sb.AppendLine("                specR: " + Flt(l.SpecR) + ", specG: " + Flt(l.SpecG) + ", specB: " + Flt(l.SpecB) + ",");
            sb.AppendLine("                shininess: " + Flt(l.Shininess) + ");");
        }

        private static string NullableInt(int? v)
            => v.HasValue ? v.Value.ToString(CultureInfo.InvariantCulture) : "null";

        private static string Quote(string? s)
        {
            s ??= "";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\"': sb.Append("\\\""); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append('\"');
            return sb.ToString();
        }

        private static string Flt(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return "0F";
            return v.ToString("R", CultureInfo.InvariantCulture) + "F";
        }

        private static string Dbl(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "0D";
            return v.ToString("R", CultureInfo.InvariantCulture) + "D";
        }

        private static decimal ClampDec(decimal v, decimal min, decimal max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }
    }
}
