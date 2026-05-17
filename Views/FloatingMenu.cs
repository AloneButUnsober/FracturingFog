using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Views;
using static FracturingFog.Views.FormHelpers;

namespace FracturingFog.Views
{
    public sealed class FloatingMenu : Form
    {
        #region UI Components

        // UI: coordinate / region bar
        private readonly Form _parentForm;
        private readonly Panel _coordPanel;
        private readonly Button _resetButton;
        private readonly Button _spanButton;
        private readonly Button _posterButton;
        private readonly Button _screenshotButton;
        private readonly Button _slideshowButton;
        private readonly Button _videoButton;
        private readonly ComboBox _formResolutionCombo;
        private readonly Label _lblCX;
        private readonly TextBox _txCX;
        private readonly Label _lblCY;
        private readonly TextBox _txCY;
        private readonly Label _qualityLabel;
        private readonly ComboBox _qualityCombo;
        private readonly Label _lblZoom;
        private readonly TextBox _txZoom;
        private readonly Label _lblIter;
        private readonly TextBox _txIter;
        private readonly CheckBox _chkLockIter;
        private readonly Button _goButton;
        private readonly Button _flipButton;
        private readonly Button _exportRegionsButton;
        private readonly Button _importRegionsButton;
        private Label? _currentRegionLabel;
        private readonly ComboBox _regionCombo;
        private GroupBox? _themeBox;
        private Label? _currentColorThemeLabel;
        private readonly ComboBox _colorThemeCombo;
        private readonly Button _exportColorThemeButton;
        private readonly Button _importColorThemeButton;
        private readonly Button _deleteColorThemeButton;
        private readonly Button _loadColorThemesButton;
        private readonly CheckBox _chkSlideshowUseExtremeRegions;
        private readonly Button _saveViewButton;
        private readonly Button _delRegionButton;
        private readonly Button _menuButton;
        private readonly CheckBox _checkBoxShowCoordPanel;
        private readonly CheckBox _checkBoxShowFooterPanel;
        private readonly CheckBox _checkBoxShowGrid;
        private readonly ToolTip _toolTip = new();

        // Brightness / Contrast
        private TrackBar? _brightnessSlider;
        private TrackBar? _contrastSlider;
        private Label? _brightnessLabel;
        private Label? _contrastLabel;

        /// <summary>Brightness offset in [-100, 100]; 0 = neutral.</summary>
        private int _brightness = 0;

        /// <summary>Contrast multiplier encoded as integer [-100, 100]; 0 = neutral (1.0×).</summary>
        private int _contrast = 0;

        // Mouse click-n-drag window repositioning
        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 0x2;

        private bool _disposed;

        #endregion UI Components

        #region Events

        public event EventHandler OnResetClick;
        public event EventHandler OnGoClick;
        public event EventHandler OnCloseCoordPanelClick;
        public event EventHandler OnSpanMonitorsClick;
        public event EventHandler OnPosterClick;
        public event EventHandler OnExportColorThemeClick;
        public event EventHandler OnImportColorThemeClick;
        public event EventHandler OnDeleteColorThemeClick;
        public event EventHandler OnLoadColorThemesClick;
        public event EventHandler OnColorThemeChanged;
        public event EventHandler OnSlideshowClick;
        public event EventHandler OnCheckBoxShowGridClick;
        public event EventHandler OnCheckBoxShowFooterClick;
        public event EventHandler OnFlipClick;
        public event EventHandler OnQualityComboChanged;
        public event Action<object?, EventArgs, object?, int> OnIterLockChanged;
        public event EventHandler OnRegionComboChanged;
        public event EventHandler OnSaveViewClick;
        public event EventHandler OnDelRegionClick;
        public event EventHandler OnExportRegionsClick;
        public event EventHandler OnImportRegionsClick;
        public event EventHandler OnChangeIncludeExtremeRegionsChange;
        public event EventHandler OnScreenshotClick;
        public event EventHandler OnVideoClick;
        public event EventHandler OnGridClick;
        public event EventHandler OnStatusClick;
        public event Action<object?, EventArgs, object?> OnBrightnessSlide;
        public event Action<object?, EventArgs, object?> OnContrastSlide;

        #endregion Events

        #region DLL Imports

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);
        [DllImport("User32.dll")]
        private static extern bool ReleaseCapture();
        [DllImport("User32.dll")]
        private static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        #endregion DLL Imports

        #region Public Members

        [DefaultValue("")]
        public string CX => _txCX.Text;

        [DefaultValue("")]
        public string CY
        {
            get { return _txCY.Text; }
            set { _txCY.Text = value; }
        }

        [DefaultValue(0)]
        public double Zoom
        {
            get { return double.Parse(_txZoom.Text); }
            set { _txZoom.Text = value.ToString(); }
        }

        [DefaultValue("")]
        public string ZoomString
        {
            get { return _txZoom.Text; }
            set { _txZoom.Text = value; }
        }

        [DefaultValue("")]
        public string Iter => _txIter.Text;

        [DefaultValue("")]
        public string Quality
        {
            get { return _qualityCombo.Text; }
            set { _qualityCombo.Text = value; }
        }

        [DefaultValue(0)]
        public int RegionIdx
        {
            get { return _regionCombo.SelectedIndex; }
            set
            {
                _regionCombo.SelectedIndexChanged -= OnRegionComboSelectionChanged;
                _regionCombo.SelectedIndex = value;
                _regionCombo.SelectedIndexChanged += OnRegionComboSelectionChanged;
            }
        }

        [DefaultValue("")]
        public string RegionName
        {
            get { return _regionCombo.Text; }
            set { _regionCombo.Text = value; }
        }

        [DefaultValue("")]
        public string ColorTheme
        {
            get { return _colorThemeCombo.Text; }
            set { _colorThemeCombo.Text = value; }
        }

        #endregion Public Members

        #region Constructors

        public FloatingMenu(Form parentForm)
        {
            if (parentForm == null) return;

            _parentForm = parentForm;
            FractalRegionLibrary.Instance.Load();
            ClientSize = new System.Drawing.Size(370, 600);
            BackColor = Color.Black;
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;

            #region Coordinate / Navigate panel

            int buttonLeft = 6;
            int buttonTop = 6;
            int labelTop = 9;
            int txTop = 7;

            _coordPanel = new Panel
            {
                AutoSize = true,
                AutoScroll = false,
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(22, 22, 22)
            };

            _coordPanel.MouseMove += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    // Drag the window when the user clicks and drags the footer panel.
                    ReleaseCapture();
                    SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
                }
            };

            buttonLeft = 6;
            txTop = 35;
            #region Buttons

            _resetButton = MakeBtn("", 30, buttonLeft, buttonTop, "Reset view to default center and zoom");
            _resetButton.Padding = new Padding(0, 0, 1, 1);
            _resetButton.Margin = new Padding(0);
            _resetButton.Click += (s, e) => OnResetButtonClick(s, e);
            try
            {
                Image resetImg = (Image)new Bitmap(Image.FromFile(@"Resources\reset.bmp"))
                    .GetThumbnailImage(24, 20, null, IntPtr.Zero);
                _resetButton.Image = resetImg;
            }
            catch { _resetButton.Text = "R"; }
            _coordPanel.Controls.Add(_resetButton);
            buttonLeft += 33;

            _spanButton = MakeBtn("Span", 55, buttonLeft, buttonTop, "Span across all monitors");
            _spanButton.Click += (s, e) => OnSpanButtonClick(s, e);
            _coordPanel.Controls.Add(_spanButton);
            buttonLeft += 58;

            _screenshotButton = MakeBtn("Image", 55, buttonLeft, buttonTop);
            _screenshotButton.Click += (s, e) => OnScreenshotButtonClick(s, e);
            _coordPanel.Controls.Add(_screenshotButton);
            buttonLeft += 58;

            _posterButton = MakeBtn("Poster", 55, buttonLeft, buttonTop);
            _posterButton.Click += (s, e) => OnPosterButtonClick(s, e);
            _coordPanel.Controls.Add(_posterButton);
            buttonLeft += 58;

            _slideshowButton = MakeBtn("Slideshow", 74, buttonLeft, buttonTop, "Start/stop slideshow — auto-cycles regions every 30 s, themes every 10 s");
            _slideshowButton.BackColor = Color.FromArgb(40, 55, 40);
            _slideshowButton.FlatAppearance.BorderColor = Color.FromArgb(60, 100, 60);
            _slideshowButton.Click += (s, e) => OnSlideshowButtonClick(s, e);
            _coordPanel.Controls.Add(_slideshowButton);
            buttonLeft += 76;

            _videoButton = MakeBtn("Video", 55, buttonLeft, buttonTop, "Smooth animated zoom from current view to a target region/coordinate");
            _videoButton.BackColor = Color.FromArgb(55, 40, 70);
            _videoButton.FlatAppearance.BorderColor = Color.FromArgb(100, 70, 130);
            _videoButton.Click += (s, e) => OnVideoButtonClick(s, e);
            _coordPanel.Controls.Add(_videoButton);

            buttonLeft += 58;
            _menuButton = MakeBtn("X", 20, buttonLeft, buttonTop, "Close floating menu");
            _menuButton.Padding = new Padding(0, 0, 1, 1);
            _menuButton.Margin = new Padding(0);
            _menuButton.Click += (s, e) => OnCoordPanelCBClick(s, e);

            #endregion Buttons

            buttonLeft = 98;
            buttonTop += _resetButton.Height + 6;
            labelTop += _resetButton.Height + 10;
            txTop += _resetButton.Height + 4;

            //buttonLeft += _menuButton.PreferredSize.Width + 6;

            _checkBoxShowFooterPanel = new CheckBox
            {
                Text = "Status",
                Left = buttonLeft,
                Top = buttonTop,
                AutoSize = true,
                AutoCheck = true,
                ForeColor = Color.FromArgb(155, 155, 155),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                BackColor = Color.Transparent,
                Checked = true,
            };
            _checkBoxShowFooterPanel.CheckedChanged += (s, e) => OnStatusCBClick(s, e);
            _coordPanel.Controls.Add(_menuButton);
            _coordPanel.Controls.Add(_checkBoxShowFooterPanel);
            buttonLeft += _checkBoxShowFooterPanel.PreferredSize.Width + 12;

            // Grid overlay toggle.
            _checkBoxShowGrid = new CheckBox
            {
                Text = "Grid",
                Left = buttonLeft,
                Top = buttonTop,
                AutoSize = true,
                AutoCheck = true,
                ForeColor = Color.FromArgb(155, 155, 155),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                BackColor = Color.Transparent,
                Checked = false,
            };
            _coordPanel.Controls.Add(_checkBoxShowGrid);
            _checkBoxShowGrid.CheckedChanged += (s, e) => OnCheckBoxShowGridCBClick(s, e);
            _toolTip.SetToolTip(_checkBoxShowGrid, "Overlay a Cartesian complex-plane grid on the fractal view");

            buttonLeft = 8;
            labelTop += _checkBoxShowGrid.Height + 4;

            //_formResolutionCombo = new ComboBox
            //{
            //    Left = buttonLeft + 88,
            //    Top = 28,
            //    Width = 130,
            //    Height = 26,
            //    BackColor = Color.FromArgb(55, 55, 55),
            //    ForeColor = Color.White,
            //    Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            //    Cursor = Cursors.Hand,
            //    DropDownWidth = Math.Max(180, ResolutionDimensions.GetLongestResolutionName() + 40)   // ensure descriptions fit in the dropdown
            //};
            //_formResolutionCombo.SelectedIndexChanged += (s,e) =>
            //{
            //    Resolution? res = ResolutionDimensions.Resolutions.Where( r => r.Name == _formResolutionCombo.Text ).FirstOrDefault<Resolution>();
            //    if (res == null) return;
            //    Size = new Size(res.Width, res.Height);
            //    CenterToParent();
            //};
            //_coordPanel.Controls.Add(_formResolutionCombo);
            //labelTop += 28;
            //txTop += 28;

            _lblCX = MakeLbl("CX:", buttonLeft, labelTop, _coordPanel, true);
            _lblCX.Height = 12;
            _lblCX.Width = 78;
            _lblCX.Padding = new Padding(0);
            buttonLeft += 88;
            _txCX = MakeTx(buttonLeft, txTop, 182, _coordPanel, "Real part of the view center");
            _txCX.TextAlign = HorizontalAlignment.Right;

            labelTop += 28;
            txTop += 28;
            buttonLeft = 8;
            buttonTop += 28;
            _lblCY = MakeLbl("CY:", buttonLeft, labelTop, _coordPanel, true);
            _lblCY.Height = 12;
            _lblCY.Width = 78;
            _lblCY.Padding = new Padding(0);
            buttonLeft += 88;
            _txCY = MakeTx(buttonLeft, txTop, 182, _coordPanel, "Imaginary part of the view center");
            _txCY.TextAlign = HorizontalAlignment.Right;


            labelTop += 28;
            txTop += 28;
            buttonLeft = 8;
            buttonTop += 28;
            _qualityLabel = MakeLbl("Quality:", buttonLeft, labelTop, _coordPanel, true);
            _qualityLabel.Height = 13;
            _qualityLabel.Width = 78;
            _qualityLabel.Padding = new Padding(0);
            buttonLeft = _qualityLabel.Left + _qualityLabel.Width + 10;

            _qualityCombo = new ComboBox
            {
                Left = buttonLeft,
                Top = txTop,
                Width = 182,
                Height = 22,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(45, 45, 45),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            foreach (var p in QualityPreset.All) _qualityCombo.Items.Add(p.Name);
            _qualityCombo.SelectedIndexChanged += (s, e) => OnQualityComboSelectionChanged(s, e);
            _coordPanel.Controls.Add(_qualityCombo);

            labelTop += 28;
            buttonLeft = 8;
            buttonTop += 28;
            txTop += 28;

            _lblZoom = MakeLbl("Zoom:", buttonLeft, labelTop, _coordPanel, true);
            _lblZoom.Height = 12;
            _lblZoom.Width = 78;
            _lblZoom.Padding = new Padding(0);
            buttonLeft += 88;
            _txZoom = MakeTx(buttonLeft, txTop, 182, _coordPanel, "Zoom factor (1 = full view; larger = zoomed in)");
            _txZoom.TextAlign = HorizontalAlignment.Right;

            labelTop += 28;
            buttonLeft = 8;
            buttonTop += 28;
            txTop += 28;
            _lblIter = MakeLbl("Iterations:", buttonLeft, labelTop, _coordPanel, true);
            _lblIter.Height = 12;
            _lblIter.Width = 78;
            _lblIter.Padding = new Padding(0);

            buttonLeft += 88;
            _txIter = MakeTx(buttonLeft, txTop, 182, _coordPanel, "Maximum iteration count");
            _txIter.TextAlign = HorizontalAlignment.Right;

            buttonTop += 54;
            buttonLeft = 98;
            _chkLockIter = new CheckBox
            {
                Text = "Lock Iterations",
                Left = buttonLeft,
                Top = buttonTop + 2,
                AutoSize = true,
                AutoCheck = true,
                ForeColor = Color.FromArgb(200, 200, 120),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                BackColor = Color.Transparent,
                Checked = false,
            };
            _toolTip.SetToolTip(_chkLockIter, "Lock the iteration count — pan/zoom will not recalculate it");
            _chkLockIter.CheckedChanged += (s, e) => OnIterLockCBChanged(s, e);
            _coordPanel.Controls.Add(_chkLockIter);

            buttonLeft = 98;
            buttonTop += 38;
            labelTop += 28;
            txTop += 28;

            _goButton = MakeBtn("Go", 54, buttonLeft, buttonTop, "Go to the specified coordinates");
            _goButton.BackColor = Color.FromArgb(40, 80, 40);
            _goButton.FlatAppearance.BorderColor = Color.FromArgb(70, 120, 70);
            _goButton.Click += (s, e) => OnGoButtonClick(s, e);
            _coordPanel.Controls.Add(_goButton);

            buttonLeft += 62;
            _flipButton = MakeBtn("Flip Y", 54, buttonLeft, buttonTop, "Flip the view vertically (negate CY)");
            _flipButton.BackColor = Color.FromArgb(40, 80, 40);
            _flipButton.FlatAppearance.BorderColor = Color.FromArgb(70, 120, 70);
            _flipButton.Click += (s, e) => OnFlipButtonClick(s, e);
            _coordPanel.Controls.Add(_flipButton);

            #region Brightness & Contrast sliders 

            int sliderTop = buttonTop + 48;
            int sliderLeft = 8;

            _brightnessLabel = new Label
            {
                Text = "Brightness: 0",
                Left = sliderLeft,
                Top = sliderTop + 3,
                Width = 78,
                Height = 12,
                TextAlign = ContentAlignment.MiddleRight,
                Padding = new Padding(0),
                ForeColor = Color.FromArgb(180, 180, 180),
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            _coordPanel.Controls.Add(_brightnessLabel);
            sliderLeft += 86;

            //sliderTop += 28;
            _brightnessSlider = new TrackBar
            {
                Left = sliderLeft,
                Top = sliderTop,
                Width = 200,
                Height = 22,
                Minimum = -100,
                Maximum = 100,
                Value = 0,
                TickFrequency = 25,
                SmallChange = 1,
                LargeChange = 10,
                BackColor = Color.FromArgb(22, 22, 22),
            };
            _toolTip.SetToolTip(_brightnessSlider,
                "Adjust brightness of the rendered fractal  (−100 to +100, default 0)");
            _brightnessSlider.ValueChanged += (s, e) => OnBrightnessSlider(s, e, _brightnessLabel);

            _coordPanel.Controls.Add(_brightnessSlider);

            sliderLeft = 8;
            sliderTop += 44;
            _contrastLabel = new Label
            {
                Text = "Contrast: 0",
                Left = sliderLeft,
                Top = sliderTop + 3,
                Width = 78,
                Height = 12,
                Padding = new Padding(0),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.FromArgb(180, 180, 180),
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            _coordPanel.Controls.Add(_contrastLabel);
            sliderLeft += 86;

            _contrastSlider = new TrackBar
            {
                Left = sliderLeft,
                Top = sliderTop,
                Width = 200,
                Height = 22,
                Minimum = -100,
                Maximum = 100,
                Value = 0,
                TickFrequency = 25,
                SmallChange = 1,
                LargeChange = 10,
                BackColor = Color.FromArgb(22, 22, 22),
            };
            _toolTip.SetToolTip(_contrastSlider,
                "Adjust contrast of the rendered fractal  (−100 to +100, default 0)");
            _contrastSlider.ValueChanged += (s, e) => OnContrastSlider(s, e, _contrastLabel);
            _coordPanel.Controls.Add(_contrastSlider);
            #endregion Brightness & Contrast sliders 

            _chkSlideshowUseExtremeRegions = new CheckBox
            {
                Text = "Slideshow: Use Extreme Regions",
                Left = 68,
                Top = sliderTop + 48,
                AutoSize = true,
                AutoCheck = true,
                ForeColor = Color.FromArgb(200, 120, 120),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                BackColor = Color.Transparent,
                Checked = false,
            };
            _coordPanel.Controls.Add(_chkSlideshowUseExtremeRegions);
            _chkSlideshowUseExtremeRegions.CheckedChanged += (s, e) =>
            {
                FractalRegionLibrary.Instance.IncludeExtremeInAll = _chkSlideshowUseExtremeRegions.Checked;
            };

            #region Region Import/Export buttons
            GroupBox regionBox = new GroupBox
            {
                Text = "Regions",
                Left = 28,
                Top = sliderTop + 68,
                Width = 260,
                Height = 78,
                ForeColor = Color.FromArgb(155, 155, 155),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                BackColor = Color.FromArgb(22, 22, 22),
            };
            _coordPanel.Controls.Add(regionBox);

            _regionCombo = new ComboBox
            {
                Left = 16,
                Top = 20,
                Width = 230,
                Height = 26,
                BackColor = Color.FromArgb(55, 55, 55),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                DropDownWidth = Math.Max(180, Models.FractalRegionLibrary.Instance.MaxRegionNameLength + 40)   // ensure descriptions fit in the dropdown
            };
            RebuildRegionCombo(_regionCombo, OnRegionComboSelectionChanged);
            regionBox.Controls.Add(_regionCombo);

            _saveViewButton = MakeBtn("Save", 55, 16, 45, "Save the current view as a region");
            _saveViewButton.Click += (s, e) => OnSaveViewButtonClick(s, e);
            regionBox.Controls.Add(_saveViewButton);
            buttonLeft += 58;

            _delRegionButton = MakeBtn("Delete", 55, _saveViewButton.Left + _saveViewButton.Width + 3, 45, "Delete the selected region");
            _delRegionButton.Click += OnDelRegionButtonClick;
            regionBox.Controls.Add(_delRegionButton);
            buttonLeft = 98;

            _exportRegionsButton = MakeBtn("Exp...", 55, _delRegionButton.Left + _delRegionButton.Width + 3, 45, "Export all custom regions to a JSON file");
            _exportRegionsButton.Click += OnExportRegionsButtonClick;
            regionBox.Controls.Add(_exportRegionsButton);
            buttonLeft += 58;

            _importRegionsButton = MakeBtn("Imp...", 55, _exportRegionsButton.Left + _exportRegionsButton.Width + 3, 45, "Import custom regions from a JSON file (duplicates get '-imp' suffix)");
            _importRegionsButton.FlatAppearance.BorderColor = Color.FromArgb(60, 90, 120);
            _importRegionsButton.Click += OnImportRegionsButtonClick;
            regionBox.Controls.Add(_importRegionsButton);
            buttonLeft += 58;
            #endregion Region Import/Export buttons

            #region Color Theme Import/Export buttons
            _themeBox = new GroupBox
            {
                Text = "Color Themes",
                Left = 28,
                Top = regionBox.Top + regionBox.Height + 10,
                Width = 260,
                Height = 81,
                ForeColor = Color.FromArgb(155, 155, 155),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                BackColor = Color.FromArgb(22, 22, 22),
            };
            _coordPanel.Controls.Add(_themeBox);

            _colorThemeCombo = new ColorComboBox
            {
                Left = 16,
                Top = 20,
                Width = 230,
                Height = 26,
                BackColor = Color.FromArgb(55, 55, 55),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                DropDownWidth = Math.Max(300, Models.ColorPalette.GetMaxDescriptionLength() + 40)   // ensure descriptions fit in the dropdown
            };
            BuildColorCombo(_colorThemeCombo, OnColorThemeSelectionClick);
            _themeBox.Controls.Add(_colorThemeCombo);
            _colorThemeCombo.SelectedIndex = 0;

            _exportColorThemeButton = MakeBtn("Exp...", 55, 16, 48, "Export the current color theme to a JSON file");
            _exportColorThemeButton.Click += (s, e) => OnExportColorThemeButtonClick(s, e);
            _themeBox.Controls.Add(_exportColorThemeButton);
            _importColorThemeButton = MakeBtn("Imp...", 55, _exportColorThemeButton.Left + _exportColorThemeButton.Width + 3, 48, "Import color themes from a JSON file");
            _importColorThemeButton.Click += (s, e) => OnImportColorThemeButtonClick(s, e);
            _themeBox.Controls.Add(_importColorThemeButton);

            _deleteColorThemeButton = MakeBtn("Delete", 55, _importColorThemeButton.Left + _importColorThemeButton.Width + 3, 48, "Delete selected user-defined color theme");
            _deleteColorThemeButton.Click += (s, e) => OnDeleteColorThemeButtonClick(s, e);
            _themeBox.Controls.Add(_deleteColorThemeButton);
            _loadColorThemesButton = MakeBtn("Reload", 55, _deleteColorThemeButton.Left + _deleteColorThemeButton.Width + 3, 48, "Reload color themes from disk (useful if you edit the JSON files externally)");
            _loadColorThemesButton.Click += (s, e) => OnLoadColorThemesButtonClick(s, e);
            _themeBox.Controls.Add(_loadColorThemesButton);

            #endregion Color Theme Import/Export buttons
            #endregion Coordinate / Navigate panel

            Controls.Add(_coordPanel);

            // ── Events ───────────────────────────────────────────────────────────
            //Load += OnLoad;
            //KeyDown += OnKeyDown;
            FormClosing += OnFormClosing;
            //Application.Idle += OnApplicationIdle;
        }

        #endregion Constructors

        #region Form Events

        private void OnLoad(object? s, EventArgs e)
        {
           
        }

        private void ThisVisibleChanged(object? s, EventArgs e)
        {
            if (s != null && ((Form)s).Visible)
            {
                _checkBoxShowCoordPanel.CheckedChanged -= OnCoordPanelCBClick;
                _checkBoxShowCoordPanel.Checked = true;
                _checkBoxShowCoordPanel.CheckedChanged += (s, e) => OnCoordPanelCBClick(s, e);
            }
        }

        //private void OnApplicationIdle(object? s, EventArgs e)
        //{
        //    if (!_disposed) TopMost = true;
        //}

        private void OnFormClosing(object? s, FormClosingEventArgs e)
        {
            _disposed = true;
        }

        #endregion Form Events

        #region Private Methods

        #region Buttons

        private void OnGoButtonClick(object? s, EventArgs e) =>
            OnGoClick?.Invoke(s, e);

        private void OnFlipButtonClick(object? s, EventArgs e) =>
            OnFlipClick?.Invoke(s, e);

        private void OnResetButtonClick(object? s, EventArgs e) =>
            OnResetClick?.Invoke(s, e);

        private void OnSpanButtonClick(object? s, EventArgs e) =>
            OnSpanMonitorsClick?.Invoke(s, e);

        private void OnPosterButtonClick(object? s, EventArgs e) =>
            OnPosterClick?.Invoke(s, e);

        private void OnSlideshowButtonClick(object? s, EventArgs e) =>
             OnSlideshowClick?.Invoke(s, e);

        #endregion Buttons

        #region Color Themes

        private void OnExportColorThemeButtonClick(object? s, EventArgs e) =>
           OnExportColorThemeClick?.Invoke(s, e);

        private void OnImportColorThemeButtonClick(object? s, EventArgs e) =>
            OnImportColorThemeClick?.Invoke(s, e);

        private void OnLoadColorThemesButtonClick(object? s, EventArgs e) =>
            OnLoadColorThemesClick?.Invoke(s, e);

        private void OnDeleteColorThemeButtonClick(object? s, EventArgs e) =>
            OnDeleteColorThemeClick?.Invoke(s, e);

        private void OnColorThemeSelectionClick(object? s, EventArgs e)
        {
            OnColorThemeChanged?.Invoke(s, e);
            UpdateDeleteColorThemeButton((ComboBox)s, _deleteColorThemeButton);
        }

        #endregion Color Themes

        #region Checkboxes

        private void OnMenuButtonClick(object? s, EventArgs e) =>
            OnCloseCoordPanelClick?.Invoke(s, e);

        private void OnCoordPanelCBClick(object? s, EventArgs e) =>
            OnCloseCoordPanelClick?.Invoke(s, EventArgs.Empty);

        private void OnGridCBClick(object? s, EventArgs e) =>
            OnGridClick?.Invoke(s, e);

        private void OnStatusCBClick(object? s, EventArgs e) =>
            OnStatusClick?.Invoke(s, e);

        private void OnCheckBoxShowGridCBClick(object? s, EventArgs e) =>
           OnCheckBoxShowGridClick?.Invoke(s, e);

        private void OnCheckBoxShowFooterCBClick(object? s, EventArgs e) =>
            OnCheckBoxShowFooterClick?.Invoke(s, e);

        private void OnIterLockCBChanged(object? s, EventArgs e)
        {
            if (int.TryParse(_txIter.Text, out int i)) OnIterLockChanged?.Invoke(s, e, _txIter, i);
        }

        #endregion Checkboxes

        #region Regions

        private void OnRegionComboSelectionChanged(object? s, EventArgs e) =>
            OnRegionComboChanged?.Invoke(s, e);

        private void OnSaveViewButtonClick(object? s, EventArgs e) =>
            OnSaveViewClick?.Invoke(s, e);

        private void OnDelRegionButtonClick(object? s, EventArgs e) =>
            OnDelRegionClick?.Invoke(s, e);

        private void OnExportRegionsButtonClick(object? s, EventArgs e) =>
            OnExportRegionsClick?.Invoke(s, e);

        private void OnImportRegionsButtonClick(object? sender, EventArgs e) =>
            OnImportRegionsClick?.Invoke(this, e);

        #endregion Regions

        #region Navigation

        #endregion Navigation

        private void OnQualityComboSelectionChanged(object? s, EventArgs e) =>
            OnQualityComboChanged?.Invoke(s, e);

        private void OnChangeIncludeExtremeRegionsCBChange(object? s, EventArgs e) =>
            OnChangeIncludeExtremeRegionsChange?.Invoke(s, e);

        private void OnScreenshotButtonClick(object? s, EventArgs e) =>
            OnScreenshotClick?.Invoke(s, e);

        private void OnVideoButtonClick(object? s, EventArgs e) =>
            OnVideoClick?.Invoke(s, e);

        private void OnBrightnessSlider(object? s, EventArgs e, object? l) =>
            OnBrightnessSlide?.DynamicInvoke(s, e, l);

        private void OnContrastSlider(object? s, EventArgs e, object? l) =>
            OnContrastSlide?.DynamicInvoke(s, e, l);

        #endregion Private Methods

        #region Public Methods

        public void ResetView(double centerX, double centerY, double zoom)
        {
            _txCX.Text = centerX.ToString();
            _txCY.Text = centerY.ToString();
            _txZoom.Text = zoom.ToString();
            _brightnessSlider?.Value = 0;
            _contrastSlider?.Value = 0;
            _brightnessLabel?.Text = "Brightness: 0";
            _contrastLabel?.Text = "Contrast: 0";

            _regionCombo.SelectedIndex = 0;
        }

        public void UpdateCoordBoxes(
            string CX,
            string CY,
            string Zoom,
            string Iter)
        {
            if (ActiveControl != _txCX)
                _txCX.Text = CX;
            if (ActiveControl != _txCY)
                _txCY.Text = CY;
            if (ActiveControl != _txZoom)
                _txZoom.Text = Zoom;
            if (ActiveControl != _txIter)
                _txIter.Text = Iter;
        }

        #endregion Public Methods
    }
}


