using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
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
        private readonly Label _titleLabel;
        private readonly Form _parentForm;
        private readonly Panel _menuPanel;
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
        private readonly Button _copyCoordsButton;
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
        private readonly Button _editColorThemeButton;
        private readonly CheckBox _chkSlideshowUseExtremeRegions;
        private CheckBox? _chkAudioReactive;
        private Button? _audioSettingsButton;
        private readonly Button _saveViewButton;
        private readonly Button _delRegionButton;
        private readonly Button _closeButton;
        private readonly Button _helpButton;
        private readonly Button _closeProgramButton;
        private readonly CheckBox _checkBoxShowCoordPanel;
        private readonly CheckBox _checkBoxShowFooterPanel;
        private readonly CheckBox _checkBoxShowGrid;
        private readonly ToolTip _toolTip = new();

        // Brightness / Contrast / Adaptive contrast (histogram eq)
        private TrackBar? _brightnessSlider;
        private TrackBar? _contrastSlider;
        private TrackBar? _histogramEqSlider;
        private Label? _brightnessLabel;
        private Label? _contrastLabel;
        private Label? _histogramEqLabel;

        // Per-slider "lock" checkboxes — when checked, theme-driven defaults
        // (Brightness/Contrast/Adaptive from ColorThemeData) are ignored on
        // theme switch and the current slider position is preserved.
        private CheckBox? _chkLockBrightness;
        private CheckBox? _chkLockContrast;
        private CheckBox? _chkLockAdaptive;

        // Video TAA test sliders — live tuning of temporal blend strength
        // and the deep-zoom fade thresholds while a video zoom is running.
        private SplitContainer _taaContainer;
        private TrackBar? _taaAlphaSlider;
        private TrackBar? _taaFadeStartSlider;
        private TrackBar? _taaFadeEndSlider;
        private Label? _taaAlphaLabel;
        private Label? _taaFadeStartLabel;
        private Label? _taaFadeEndLabel;

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
        public event EventHandler OnEditColorThemeClick;
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
        public event EventHandler<bool>? OnAudioReactiveToggled;
        public event EventHandler? OnAudioSettingsClick;
        public event EventHandler OnScreenshotClick;
        public event EventHandler OnVideoClick;
        public event EventHandler OnGridClick;
        public event EventHandler OnStatusClick;
        public event EventHandler OnChangeDimensions;
        public event EventHandler? OnHelpClick;
        public event Action<object?, EventArgs, object?> OnBrightnessSlide;
        public event Action<object?, EventArgs, object?> OnContrastSlide;
        public event Action<object?, EventArgs, object?> OnHistogramEqSlide;
        public event Action<object?, EventArgs, object?> OnTaaAlphaSlide;
        public event Action<object?, EventArgs, object?> OnTaaFadeStartSlide;
        public event Action<object?, EventArgs, object?> OnTaaFadeEndSlide;

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
            ClientSize = new System.Drawing.Size(330, 691);
            BackColor = Color.Black;
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;

            #region Coordinate / Navigate panel

            int buttonLeft = 8;
            int buttonTop = 14;
            int buttonWidth = 0;
            int labelTop = 17;
            int txTop = 15;

            // Tooltip settings
            _toolTip.AutoPopDelay = 5000;
            _toolTip.InitialDelay = 1000;
            _toolTip.ReshowDelay = 500;
            _toolTip.ShowAlways = true;

            _menuPanel = new Panel
            {
                //AutoSize = true,
                AutoScroll = false,
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(22, 22, 22)
            };
            _menuPanel.MouseMove += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    // Drag the window when the user clicks and drags the footer panel.
                    ReleaseCapture();
                    SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
                }
            };

            // ── Title bar ───────────────────────────────────────────────────
            _titleLabel = new Label
            {
                Text = "Main Menu",
                Left = buttonLeft,
                Top = 4,
                AutoSize = true,
                ForeColor = Color.FromArgb(200, 200, 100),
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                BackColor = Color.Transparent,
            };
            _titleLabel.MouseDown += DragWindow;
            _menuPanel.Controls.Add(_titleLabel);

            _closeButton = new Button
            {
                Text = "X",
                Width = 24,
                Height = 24,
                Top = 2,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            };
            _closeButton.Left = Width - 27;
            _closeButton.FlatAppearance.BorderSize = 0;
            _closeButton.MouseHover += (s, e) => { _closeButton.ForeColor = Color.YellowGreen; _closeButton.BackColor = Color.Black; };
            _closeButton.MouseLeave += (s, e) => { _closeButton.ForeColor = Color.White; _closeButton.BackColor = Color.Transparent; };
            _closeButton.Padding = new Padding(0, 0, 1, 1);
            _closeButton.Margin = new Padding(0);
            _closeButton.Click += (s, e) => OnCoordPanelCBClick(s, e);
            _toolTip.SetToolTip(_closeButton, "Close Main Menu...");

            _helpButton = new Button
            {
                Text = "?",
                Width = 26,
                Height = 24,
                Top = 2,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 60, 100),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Cursor = Cursors.Hand,
            };
            _helpButton.Left = Width - 56;
            _helpButton.FlatAppearance.BorderColor = Color.FromArgb(80, 120, 180);
            _helpButton.Click += (s, e) => OnHelpClick?.Invoke(this, EventArgs.Empty);
            _menuPanel.Controls.Add(_helpButton);
            _toolTip.SetToolTip(_helpButton, "Open the help menu...");

            buttonLeft = Left + 5; // Width / 4 - 8 + 2;
            buttonTop = _titleLabel.Top + _titleLabel.Height + 6;
            txTop = buttonTop;

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
            _toolTip.SetToolTip(_resetButton, "Reset view to the default for selected fractal type.");
            _menuPanel.Controls.Add(_resetButton);

            buttonLeft = _resetButton.Left + _resetButton.Width + 2;

            buttonWidth = ((Width - (buttonLeft + _resetButton.Width)) / 3 + _resetButton.Width / 3) - 2;
            _spanButton = MakeBtn("Span", buttonWidth, buttonLeft, buttonTop, "Span across all monitors");
            _spanButton.Click += (s, e) => OnSpanButtonClick(s, e);
            _menuPanel.Controls.Add(_spanButton);
            _toolTip.SetToolTip(_spanButton, "Span the view across all monitors.");
            buttonLeft = _spanButton.Left + _spanButton.Width + 2;

            _screenshotButton = MakeBtn("Image", buttonWidth, buttonLeft, buttonTop);
            _screenshotButton.Click += (s, e) => OnScreenshotButtonClick(s, e);
            _menuPanel.Controls.Add(_screenshotButton);
            _toolTip.SetToolTip(_screenshotButton, "Take a high-resolution screenshot of the current view...");
            buttonLeft = _screenshotButton.Left + _screenshotButton.Width + 2;

            _posterButton = MakeBtn("Poster", buttonWidth, buttonLeft, buttonTop);
            _posterButton.Click += (s, e) => OnPosterButtonClick(s, e);
            _menuPanel.Controls.Add(_posterButton);
            _toolTip.SetToolTip(_posterButton, "Save the current view as a print-ready image...");
            buttonLeft = _posterButton.Left + _posterButton.Width + 2;

            buttonLeft = Left + 5;
            buttonTop += _posterButton.Height + 2;
            buttonWidth = (_resetButton.Width + _spanButton.Width + _screenshotButton.Width + _posterButton.Width) / 3;
            _slideshowButton = MakeBtn("Slideshow", buttonWidth, buttonLeft, buttonTop, "Start/stop slideshow");
            _slideshowButton.BackColor = Color.FromArgb(40, 55, 40);
            _slideshowButton.FlatAppearance.BorderColor = Color.FromArgb(60, 100, 60);
            _slideshowButton.Click += (s, e) => OnSlideshowButtonClick(s, e);
            _menuPanel.Controls.Add(_slideshowButton);
            _toolTip.SetToolTip(_slideshowButton, "Start a slideshow...");

            buttonLeft = _slideshowButton.Left + _slideshowButton.Width + 3;
            _videoButton = MakeBtn("Video", buttonWidth, buttonLeft, buttonTop, "Smooth animated zoom from current view to a target region/coordinate");
            _videoButton.BackColor = Color.FromArgb(55, 40, 70);
            _videoButton.FlatAppearance.BorderColor = Color.FromArgb(100, 70, 130);
            _videoButton.Click += (s, e) => OnVideoButtonClick(s, e);
            _menuPanel.Controls.Add(_videoButton);
            _toolTip.SetToolTip(_videoButton, "Video menu...");

            #region Close Program button
            buttonLeft = _videoButton.Left + _videoButton.Width + 3;
            _closeProgramButton = MakeBtn(
                "Close Program",
                buttonWidth,
                buttonLeft,
                buttonTop,
                "Exit the program");
            _closeProgramButton.BackColor = Color.FromArgb(80, 35, 35);
            _closeProgramButton.FlatAppearance.BorderColor = Color.FromArgb(140, 60, 60);
            _closeProgramButton.ForeColor = Color.FromArgb(240, 220, 220);
            _closeProgramButton.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            _closeProgramButton.Click += (s, e) => OnCloseProgramButtonClick(s, e);
            _menuPanel.Controls.Add(_closeProgramButton);
            _toolTip.SetToolTip(_closeProgramButton, "Close Fracturing Fog");
            #endregion Close Program button

            #endregion Buttons

            buttonLeft = 8;
            buttonTop = _slideshowButton.Top + _slideshowButton.Height + 5;
            labelTop = _slideshowButton.Top + _slideshowButton.Height + 10;
            txTop = _slideshowButton.Top + _slideshowButton.Height + 4;

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
            _menuPanel.Controls.Add(_closeButton);
            _menuPanel.Controls.Add(_checkBoxShowFooterPanel);
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
            _menuPanel.Controls.Add(_checkBoxShowGrid);
            _checkBoxShowGrid.CheckedChanged += (s, e) => OnCheckBoxShowGridCBClick(s, e);
            _toolTip.SetToolTip(_checkBoxShowGrid, "Overlay a Cartesian complex-plane grid on the fractal view");

            buttonLeft = _checkBoxShowGrid.Left + _checkBoxShowGrid.Width + 67;
            labelTop = _checkBoxShowGrid.Top + _checkBoxShowGrid.Height + 6;
            txTop = _checkBoxShowGrid.Top + _checkBoxShowGrid.Height + 6;

            _formResolutionCombo = new ComboBox
            {
                Left = buttonLeft,
                Top = buttonTop,
                Width = 130,
                Height = 26,
                BackColor = Color.FromArgb(55, 55, 55),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                DropDownWidth = Math.Max(180, ResolutionDimensions.GetLongestResolutionName() + 40)   // ensure descriptions fit in the dropdown
            };
            BuildResolutionSelection();
            _formResolutionCombo.SelectedIndex = 0;
            _formResolutionCombo.SelectedIndexChanged += (s, e) => OnChangeDimensionsSelection(s,e);
            _menuPanel.Controls.Add(_formResolutionCombo);
            _toolTip.SetToolTip(_formResolutionCombo, "Set window dimensions.");

            buttonLeft = 8;
            buttonTop = _formResolutionCombo.Top + _formResolutionCombo.Height + 2;
            labelTop = _formResolutionCombo.Top + _formResolutionCombo.Height + 6;
            txTop = _formResolutionCombo.Top + _formResolutionCombo.Height + 6;

            #region Navigation Group Box

            GroupBox navigationGrpBox = new GroupBox
            {
                Left = buttonLeft + 5,
                Top = buttonTop,
                Width = 300,
                Height = 282,
                Text = "Region Navigation",
                ForeColor = Color.FromArgb(155, 155, 155),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                BackColor = Color.FromArgb(22, 22, 22),
            };

            _menuPanel.Controls.Add(navigationGrpBox);

            #region Region Import/Export buttons

            buttonTop = 18;
            _regionCombo = new ComboBox
            {
                Left = 51,
                Top = buttonTop,
                Width = 230,
                Height = 26,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(55, 55, 55),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                DropDownWidth = Math.Max(180, Models.FractalRegionLibrary.Instance.MaxRegionNameLength + 40)   // ensure descriptions fit in the dropdown
            };
            RebuildRegionCombo(_regionCombo, OnRegionComboSelectionChanged);
            AttachRegionComboSortMenu(_regionCombo, OnRegionComboSelectionChanged,
                onAfterRebuild: () => UpdateDelRegionButton(_regionCombo, _delRegionButton));
            navigationGrpBox.Controls.Add(_regionCombo);
            buttonLeft = 53;
            buttonTop = 45;

            _saveViewButton = MakeBtn("Save", 55, buttonLeft, buttonTop, "Save the current view as a region");
            _saveViewButton.Click += (s, e) => OnSaveViewButtonClick(s, e);
            navigationGrpBox.Controls.Add(_saveViewButton);
            buttonLeft = _saveViewButton.Left + _saveViewButton.Width + 2;

            _delRegionButton = MakeBtn("Delete", 55, buttonLeft, buttonTop, "Delete the selected region");
            _delRegionButton.Click += OnDelRegionButtonClick;
            navigationGrpBox.Controls.Add(_delRegionButton);
            buttonLeft = _delRegionButton.Left +_delRegionButton.Width + 2;

            _exportRegionsButton = MakeBtn("Exp...", 55, buttonLeft, buttonTop, "Export all custom regions to a JSON file");
            _exportRegionsButton.Click += OnExportRegionsButtonClick;
            navigationGrpBox.Controls.Add(_exportRegionsButton);
            buttonLeft = _exportRegionsButton.Left + _exportRegionsButton.Width + 2;

            _importRegionsButton = MakeBtn("Imp...", 55, buttonLeft, buttonTop, "Import custom regions from a JSON file (duplicates get '-imp' suffix)");
            _importRegionsButton.FlatAppearance.BorderColor = Color.FromArgb(60, 90, 120);
            _importRegionsButton.Click += OnImportRegionsButtonClick;
            navigationGrpBox.Controls.Add(_importRegionsButton);
            #endregion Region Import/Export buttons

            buttonLeft = 8;
            labelTop = _saveViewButton.Top + _saveViewButton.Height + 18;
            _lblCX = new Label
            {
                Text = "CX:",
                Left = buttonLeft,
                Top = labelTop,
                Height = 12,
                Width = 78,
                Padding = new Padding(0),
                AutoSize = false, 
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.FromArgb(155, 155, 155),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            navigationGrpBox.Controls.Add(_lblCX);

            _txCX = new TextBox
            {
                Left = _lblCX.Left + _lblCX.Width + 8,
                Top = labelTop - 3,
                Width = 182,
                Height = 22,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.FromArgb(220, 220, 220),
                Font = new Font("Consolas", 9f),
                BorderStyle = BorderStyle.FixedSingle,
                TextAlign = HorizontalAlignment.Right
            };
            navigationGrpBox.Controls.Add(_txCX);

            labelTop = _lblCX.Top + _lblCX.Height + 16;
            _lblCY = new Label
            {
                Text = "CY:",
                Left = buttonLeft,
                Top = labelTop,
                Height = 12,
                Width = 78,
                Padding = new Padding(0),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.FromArgb(155, 155, 155),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            navigationGrpBox.Controls.Add(_lblCY);

            _txCY = new TextBox
            {
                Left = _lblCY.Left + _lblCY.Width + 8,
                Top = labelTop - 3,
                Width = 182,
                Height = 22,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.FromArgb(220, 220, 220),
                Font = new Font("Consolas", 9f),
                BorderStyle = BorderStyle.FixedSingle,
                TextAlign = HorizontalAlignment.Right
            };
            navigationGrpBox.Controls.Add(_txCY);

            labelTop = _lblCY.Top + _lblCY.Height + 16;
            _qualityLabel = new Label
            {
                Text = "Quality:",
                Left = buttonLeft,
                Top = labelTop,
                Height = 13,
                Width = 78,
                Padding = new Padding(0),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.FromArgb(155, 155, 155),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            navigationGrpBox.Controls.Add(_qualityLabel);

            _qualityCombo = new ComboBox
            {
                Left = _qualityLabel.Left + _qualityLabel.Width + 8,
                Top = labelTop - 3,
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
            navigationGrpBox.Controls.Add(_qualityCombo);


            labelTop = _qualityLabel.Top + _qualityLabel.Height + 16;
            _lblZoom = new Label
            {
                Text = "Zoom:",
                Left = buttonLeft,
                Top = labelTop,
                Height = 12,
                Width = 78,
                Padding = new Padding(0),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.FromArgb(155, 155, 155),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            navigationGrpBox.Controls.Add(_lblZoom);

            //_txZoom = MakeTx(buttonLeft, txTop, 182, _coordPanel, "Zoom factor (1 = full view; larger = zoomed in)");
            _txZoom = new TextBox
            {
                Left = _lblZoom.Left + _lblZoom.Width + 8,
                Top = labelTop - 3,
                Width = 182,
                Height = 22,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.FromArgb(220, 220, 220),
                Font = new Font("Consolas", 9f),
                BorderStyle = BorderStyle.FixedSingle,
                TextAlign = HorizontalAlignment.Right
            };
            navigationGrpBox.Controls.Add(_txZoom);

            labelTop = _lblZoom.Top + _lblZoom.Height + 16;
            _lblIter = new Label
            {
                Text = "Iterations:",
                Left = buttonLeft,
                Top = labelTop,
                Height = 12,
                Width = 78,
                Padding = new Padding(0),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.FromArgb(155, 155, 155),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                BackColor = Color.Transparent
            };
            navigationGrpBox.Controls.Add(_lblIter);

            //_txIter = MakeTx(buttonLeft, txTop, 182, _coordPanel, "Maximum iteration count");
            _txIter = new TextBox
            {
                Left = _lblIter.Left + _lblIter.Width + 8,
                Top = labelTop - 3,
                Width = 182,
                Height = 22,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.FromArgb(220, 220, 220),
                Font = new Font("Consolas", 9f),
                BorderStyle = BorderStyle.FixedSingle,
                TextAlign = HorizontalAlignment.Right
            };
            navigationGrpBox.Controls.Add(_txIter);

            buttonTop = _txIter.Top + _txIter.Height + 4;
            _chkLockIter = new CheckBox
            {
                Text = "Lock Iterations",
                Left = _txIter.Left,
                Top = buttonTop,
                AutoSize = true,
                AutoCheck = true,
                ForeColor = Color.FromArgb(200, 200, 120),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                BackColor = Color.Transparent,
                Checked = false,
            };
            _toolTip.SetToolTip(_chkLockIter, "Lock the iteration count — pan/zoom will not recalculate it");
            _chkLockIter.CheckedChanged += (s, e) => OnIterLockCBChanged(s, e);
            navigationGrpBox.Controls.Add(_chkLockIter);

            buttonTop = _chkLockIter.Top + _chkLockIter.Height + 5;
            //_goButton = MakeBtn("Go", 54, buttonLeft, buttonTop, "Go to the specified coordinates");
            _goButton = new Button
            {
                Text = "Go",
                Width = 54,
                Height = 26,
                Left = _chkLockIter.Left,
                Top = buttonTop,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 80, 40),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Cursor = Cursors.Hand,
            };
            _goButton.FlatAppearance.BorderColor = Color.FromArgb(70, 120, 70);
            _goButton.Click += (s, e) => OnGoButtonClick(s, e);
            navigationGrpBox.Controls.Add(_goButton);

            //_flipButton = MakeBtn("Flip Y", 54, buttonLeft, buttonTop, "Flip the view vertically (negate CY)");
            _flipButton = new Button
            {
                Text = "Flip Y",
                Width = 54,
                Height = 26,
                Left = _goButton.Left + _goButton.Width + 4,
                Top = buttonTop,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 80, 40),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Cursor = Cursors.Hand,
            };
            _flipButton.FlatAppearance.BorderColor = Color.FromArgb(70, 120, 70);
            _flipButton.Click += (s, e) => OnFlipButtonClick(s, e);
            navigationGrpBox.Controls.Add(_flipButton);

            // Copy button — dumps CX/CY/Zoom/Iterations to the clipboard in a
            // human-readable, line-per-field layout for easy sharing/paste-back.
            _copyCoordsButton = new Button
            {
                Text = "Copy",
                Width = 54,
                Height = 26,
                Left = _flipButton.Left + _flipButton.Width + 4,
                Top = buttonTop,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(40, 80, 40),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Cursor = Cursors.Hand,
            };
            _copyCoordsButton.FlatAppearance.BorderColor = Color.FromArgb(70, 120, 70);
            _toolTip.SetToolTip(_copyCoordsButton,
                "Copy CX / CY / Zoom / Iterations to the clipboard");
            _copyCoordsButton.Click += (s, e) => OnCopyCoordsClick();
            navigationGrpBox.Controls.Add(_copyCoordsButton);

            #endregion Navigation Group Box

            #region Color Theme Import/Export buttons
            buttonTop = navigationGrpBox.Top + navigationGrpBox.Height + 2;
            _themeBox = new GroupBox
            {
                Text = "Color Themes",
                Left = 13,
                Top = buttonTop, //regionBox.Top + regionBox.Height + 10,
                Width = 300,
                Height = 111,
                ForeColor = Color.FromArgb(155, 155, 155),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                BackColor = Color.FromArgb(22, 22, 22),
            };
            _menuPanel.Controls.Add(_themeBox);

            _colorThemeCombo = new ColorComboBox
            {
                Left = 51,
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
            AttachColorComboSortMenu(
                _colorThemeCombo,
                OnColorThemeSelectionClick,
                () => UpdateDeleteColorThemeButton(_colorThemeCombo, _deleteColorThemeButton));
            _themeBox.Controls.Add(_colorThemeCombo);
            _colorThemeCombo.SelectedIndex = 0;

            _exportColorThemeButton = MakeBtn("Exp...", 55, 51, 48, "Export the current color theme to a JSON file");
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

            _editColorThemeButton = MakeBtn("Edit Theme…", 232, 51, 78,
                "Open the Color Theme Editor to create or edit a theme. Live-preview updates the main view; Save adds it to your library.");
            _editColorThemeButton.BackColor = Color.FromArgb(40, 60, 100);
            _editColorThemeButton.FlatAppearance.BorderColor = Color.FromArgb(70, 110, 160);
            _editColorThemeButton.ForeColor = Color.White;
            _editColorThemeButton.Click += (s, e) => OnEditColorThemeClick?.Invoke(s, e);
            _themeBox.Controls.Add(_editColorThemeButton);

            #endregion Color Theme Import/Export buttons

            #region Brightness & Contrast sliders 

            buttonTop = _themeBox.Top + _themeBox.Height + 6;
            int sliderTop = buttonTop; // + 48;
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
            _menuPanel.Controls.Add(_brightnessLabel);
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

            _menuPanel.Controls.Add(_brightnessSlider);

            _chkLockBrightness = MakeLockBox(_brightnessSlider.Right + 4, sliderTop + 2,
                "Lock brightness — ignore theme defaults on theme switch");
            _menuPanel.Controls.Add(_chkLockBrightness);

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
            _menuPanel.Controls.Add(_contrastLabel);
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
            _menuPanel.Controls.Add(_contrastSlider);

            _chkLockContrast = MakeLockBox(_contrastSlider.Right + 4, sliderTop + 2,
                "Lock contrast — ignore theme defaults on theme switch");
            _menuPanel.Controls.Add(_chkLockContrast);

            sliderLeft = 8;
            sliderTop += 44;
            _histogramEqLabel = new Label
            {
                Text = "Adaptive: 0",
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
            _menuPanel.Controls.Add(_histogramEqLabel);
            sliderLeft += 86;

            _histogramEqSlider = new TrackBar
            {
                Left = sliderLeft,
                Top = sliderTop,
                Width = 200,
                Height = 22,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                TickFrequency = 25,
                SmallChange = 1,
                LargeChange = 10,
                BackColor = Color.FromArgb(22, 22, 22),
            };
            _toolTip.SetToolTip(_histogramEqSlider,
                "Adaptive contrast (histogram equalization) — redistributes iteration density to "
                + "reveal hidden detail in flat-looking regions  (0 = off, 100 = full)");
            _histogramEqSlider.ValueChanged += (s, e) => OnHistogramEqSlider(s, e, _histogramEqLabel);
            _menuPanel.Controls.Add(_histogramEqSlider);

            _chkLockAdaptive = MakeLockBox(_histogramEqSlider.Right + 4, sliderTop + 2,
                "Lock adaptive contrast — ignore theme defaults on theme switch");
            _menuPanel.Controls.Add(_chkLockAdaptive);

            //sliderLeft = 8;
            //sliderTop += _histogramEqSlider.Height + 2;
            //Button taaContainerButton = new Button
            //{
            //    Text = "TAA",
            //    Left = sliderLeft,
            //    Top = sliderTop,
            //    Width = 30,
            //    Height = 20
            //};


            //_taaContainer = new SplitContainer
            //{
            //    Top = sliderTop,
            //    Left = sliderLeft,
            //    Width = 260,
            //    Panel1MinSize = 0,
            //    Panel2MinSize = 0,
            //    Panel1Collapsed = true,
            //    Panel2Collapsed = true,
            //    BorderStyle = BorderStyle.FixedSingle,
            //    Orientation = Orientation.Horizontal
            //};

            //taaContainerButton.Click += (s,e) =>
            //{
            //    _taaContainer.Panel1Collapsed = !_taaContainer.Panel1Collapsed;
            //};

            //_coordPanel.Controls.Add(taaContainerButton);
            //_coordPanel.Controls.Add(_taaContainer);
            // ── Video TAA test sliders ────────────────────────────────────
            // Live tuning of temporal-blend alpha and the deep-zoom fade
            // window. Defaults match the VideoZoom code defaults (55 % alpha,
            // fade from 1e15 → 1e18). Values are passed straight through to
            // MainForm which forwards them to VideoZoom every change.
            //sliderLeft = 8;
            //sliderTop += 44;
            //_taaAlphaLabel = new Label
            //{
            //    Text = "TAA α: 55",
            //    Left = sliderLeft,
            //    Top = sliderTop + 3,
            //    Width = 78,
            //    Height = 12,
            //    Padding = new Padding(0),
            //    TextAlign = ContentAlignment.MiddleRight,
            //    ForeColor = Color.FromArgb(180, 180, 180),
            //    Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            //    BackColor = Color.Transparent
            //};
            //_coordPanel.Controls.Add(_taaAlphaLabel);
            //sliderLeft += 86;

            //_taaAlphaSlider = new TrackBar
            //{
            //    Left = sliderLeft,
            //    Top = sliderTop,
            //    Width = 200,
            //    Height = 22,
            //    Minimum = 0,
            //    Maximum = 100,
            //    Value = 55,
            //    TickFrequency = 10,
            //    SmallChange = 1,
            //    LargeChange = 10,
            //    BackColor = Color.FromArgb(22, 22, 22),
            //};
            //_toolTip.SetToolTip(_taaAlphaSlider,
            //    "Video TAA temporal blend strength (live test)  "
            //    + "(0 = current frame only, 100 = max prev-frame contribution)");
            //_taaAlphaSlider.ValueChanged += (s, e) => OnTaaAlphaSlider(s, e, _taaAlphaLabel);
            //_coordPanel.Controls.Add(_taaAlphaSlider);

            //sliderLeft = 8;
            //sliderTop += 44;
            //_taaFadeStartLabel = new Label
            //{
            //    Text = "Fade @ 1e15",
            //    Left = sliderLeft,
            //    Top = sliderTop + 3,
            //    Width = 78,
            //    Height = 12,
            //    Padding = new Padding(0),
            //    TextAlign = ContentAlignment.MiddleRight,
            //    ForeColor = Color.FromArgb(180, 180, 180),
            //    Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            //    BackColor = Color.Transparent
            //};
            //_coordPanel.Controls.Add(_taaFadeStartLabel);
            //sliderLeft += 86;

            //_taaFadeStartSlider = new TrackBar
            //{
            //    Left = sliderLeft,
            //    Top = sliderTop,
            //    Width = 200,
            //    Height = 22,
            //    Minimum = 0,
            //    Maximum = 25,
            //    Value = 15,
            //    TickFrequency = 1,
            //    SmallChange = 1,
            //    LargeChange = 2,
            //    BackColor = Color.FromArgb(22, 22, 22),
            //};
            //_toolTip.SetToolTip(_taaFadeStartSlider,
            //    "TAA fade-start zoom (log10) — TAA full-strength below this zoom level");
            //_taaFadeStartSlider.ValueChanged += (s, e) => OnTaaFadeStartSlider(s, e, _taaFadeStartLabel);
            //_coordPanel.Controls.Add(_taaFadeStartSlider);

            //sliderLeft = 8;
            //sliderTop += 44;
            //_taaFadeEndLabel = new Label
            //{
            //    Text = "Off @ 1e18",
            //    Left = sliderLeft,
            //    Top = sliderTop + 3,
            //    Width = 78,
            //    Height = 12,
            //    Padding = new Padding(0),
            //    TextAlign = ContentAlignment.MiddleRight,
            //    ForeColor = Color.FromArgb(180, 180, 180),
            //    Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            //    BackColor = Color.Transparent
            //};
            //_coordPanel.Controls.Add(_taaFadeEndLabel);
            //sliderLeft += 86;

            //_taaFadeEndSlider = new TrackBar
            //{
            //    Left = sliderLeft,
            //    Top = sliderTop,
            //    Width = 200,
            //    Height = 22,
            //    Minimum = 0,
            //    Maximum = 25,
            //    Value = 18,
            //    TickFrequency = 1,
            //    SmallChange = 1,
            //    LargeChange = 2,
            //    BackColor = Color.FromArgb(22, 22, 22),
            //};
            //_toolTip.SetToolTip(_taaFadeEndSlider,
            //    "TAA fade-end zoom (log10) — TAA fully off above this zoom level");
            //_taaFadeEndSlider.ValueChanged += (s, e) => OnTaaFadeEndSlider(s, e, _taaFadeEndLabel);
            //_coordPanel.Controls.Add(_taaFadeEndSlider);
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
            _menuPanel.Controls.Add(_chkSlideshowUseExtremeRegions);
            _chkSlideshowUseExtremeRegions.CheckedChanged += (s, e) =>
            {
                FractalRegionLibrary.Instance.IncludeExtremeInAll = _chkSlideshowUseExtremeRegions.Checked;
            };

            _chkAudioReactive = new CheckBox
            {
                Text = "Audio-React Slideshow",
                Left = 68,
                Top = sliderTop + 72,
                AutoSize = true,
                AutoCheck = true,
                ForeColor = Color.FromArgb(120, 200, 160),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                BackColor = Color.Transparent,
                Checked = false,
            };
            _menuPanel.Controls.Add(_chkAudioReactive);
            _chkAudioReactive.CheckedChanged += (s, e) =>
                OnAudioReactiveToggled?.Invoke(this, _chkAudioReactive.Checked);
            _toolTip.SetToolTip(_chkAudioReactive,
                "Drive theme/region changes from a detected beat in system audio, a file, or fractal-generated audio.");

            _audioSettingsButton = new Button
            {
                Text = "Audio…",
                Left = _chkAudioReactive.Left + _chkAudioReactive.PreferredSize.Width + 8,
                Top = sliderTop + 70,
                Width = 60,
                Height = 22,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(30, 50, 45),
                ForeColor = Color.FromArgb(180, 220, 200),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                Cursor = Cursors.Hand,
            };
            _audioSettingsButton.FlatAppearance.BorderColor = Color.FromArgb(60, 110, 90);
            _audioSettingsButton.Click += (s, e) => OnAudioSettingsClick?.Invoke(this, EventArgs.Empty);
            _menuPanel.Controls.Add(_audioSettingsButton);
            _toolTip.SetToolTip(_audioSettingsButton,
                "Configure audio source, sensitivity, and beats-per-change.");

            #endregion Coordinate / Navigate panel

            Controls.Add(_menuPanel);

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

        /// <summary>
        /// Copies the current CX / CY / Zoom / Iterations textbox values to the
        /// clipboard as four labelled lines. The CX/CY values are emitted in
        /// whatever form the textboxes currently display (single-string by
        /// default; pipe-delimited if the user pasted that form unchanged).
        /// </summary>
        private void OnCopyCoordsClick()
        {
            try
            {
                string text =
                    $"CX: {_txCX.Text}\r\n" +
                    $"CY: {_txCY.Text}\r\n" +
                    $"Zoom: {_txZoom.Text}\r\n" +
                    $"Iterations: {_txIter.Text}\r\n";
                Clipboard.SetText(text);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // Clipboard contention with another process — silently ignore;
                // user can simply click Copy again.
            }
        }

        private void OnResetButtonClick(object? s, EventArgs e) =>
            OnResetClick?.Invoke(s, e);

        private void OnSpanButtonClick(object? s, EventArgs e) =>
            OnSpanMonitorsClick?.Invoke(s, e);

        private void OnPosterButtonClick(object? s, EventArgs e) =>
            OnPosterClick?.Invoke(s, e);

        private void OnSlideshowButtonClick(object? s, EventArgs e) =>
             OnSlideshowClick?.Invoke(s, e);

        private void OnCloseProgramButtonClick(object? s, EventArgs e) =>
            Application.Exit();

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

        private void BuildResolutionSelection()
        {
            //return;
            int totalW = 0;
            int totalH = 0;
            foreach (var s in Screen.AllScreens)
            {
                totalH += s.Bounds.Height;
                totalW += s.Bounds.Width;
            }

            _formResolutionCombo.Items.Clear();
            foreach (var rt in ResolutionDimensions.ResolutionTypeName)
            {
                string restypeName = rt.Value;
                _formResolutionCombo.Items.Add($" -- {rt.Value} -- ");
                foreach (Resolution res in ResolutionDimensions.Resolutions.Where(r => r.ResolutionType == rt.Key))
                {
                    if (res == null) continue;
                    if (res.Width == 0 || res.Width > totalW) return;
                    if (res.Height == 0 || res.Height > totalH) return;
                    _formResolutionCombo.Items.Add(res.Name);
                }
            }
        }

        private void OnChangeDimensionsSelection(object? s, EventArgs e) =>
            OnChangeDimensions?.Invoke(s,e);

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

        private void OnHistogramEqSlider(object? s, EventArgs e, object? l) =>
            OnHistogramEqSlide?.DynamicInvoke(s, e, l);

        private void OnTaaAlphaSlider(object? s, EventArgs e, object? l)
        {
            if (_taaAlphaSlider != null && _taaAlphaLabel != null)
                _taaAlphaLabel.Text = $"TAA α: {_taaAlphaSlider.Value}";
            OnTaaAlphaSlide?.DynamicInvoke(s, e, l);
        }

        private void OnTaaFadeStartSlider(object? s, EventArgs e, object? l)
        {
            if (_taaFadeStartSlider != null && _taaFadeStartLabel != null)
                _taaFadeStartLabel.Text = $"Fade @ 1e{_taaFadeStartSlider.Value}";
            OnTaaFadeStartSlide?.DynamicInvoke(s, e, l);
        }

        private void OnTaaFadeEndSlider(object? s, EventArgs e, object? l)
        {
            if (_taaFadeEndSlider != null && _taaFadeEndLabel != null)
                _taaFadeEndLabel.Text = $"Off @ 1e{_taaFadeEndSlider.Value}";
            OnTaaFadeEndSlide?.DynamicInvoke(s, e, l);
        }

        // ── Drag ────────────────────────────────────────────────────────────
        private void DragWindow(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
        }

        #endregion Private Methods

        #region Public Methods

        /// <summary>
        /// Silently selects the named theme in the floating menu's combo
        /// without firing OnColorThemeChanged. Used by MainForm to mirror
        /// selections made elsewhere (toolbar combo or editor) into the
        /// floating menu without re-entering the change handler.
        /// </summary>
        public void SetThemeSilent(string name)
        {
            if (_colorThemeCombo == null || _colorThemeCombo.IsDisposed) return;
            _colorThemeCombo.SelectedIndexChanged -= OnColorThemeSelectionClick;
            try
            {
                int idx = _colorThemeCombo.FindStringExact(name ?? string.Empty);
                if (idx >= 0 && _colorThemeCombo.SelectedIndex != idx)
                    _colorThemeCombo.SelectedIndex = idx;
            }
            finally
            {
                _colorThemeCombo.SelectedIndexChanged += OnColorThemeSelectionClick;
            }
        }

        /// <summary>
        /// Installs (or clears) an equation profile on the floating menu's
        /// colour-theme combo. When set, a "— Suggested for equation —" section
        /// is prepended to the dropdown with the top-ranked themes for that
        /// equation. Mirrors the toolbar combo so suggestions stay in sync.
        /// </summary>
        public void ApplyEquationProfile(Models.EquationProfile? profile)
        {
            if (_colorThemeCombo == null || _colorThemeCombo.IsDisposed) return;
            FormHelpers.ApplyEquationProfile(_colorThemeCombo, profile, OnColorThemeSelectionClick);
        }

        /// <summary>
        /// Silently selects the named region without firing
        /// OnRegionComboChanged. Mirrors selections from the toolbar combo or
        /// the Color Theme Editor.
        /// </summary>
        public void SetRegionSilent(string name)
        {
            if (_regionCombo == null || _regionCombo.IsDisposed) return;
            _regionCombo.SelectedIndexChanged -= OnRegionComboSelectionChanged;
            try
            {
                int idx = _regionCombo.FindStringExact(name ?? string.Empty);
                if (idx >= 0 && _regionCombo.SelectedIndex != idx)
                    _regionCombo.SelectedIndex = idx;
            }
            finally
            {
                _regionCombo.SelectedIndexChanged += OnRegionComboSelectionChanged;
            }
        }

        public int TaaAlphaValue => _taaAlphaSlider?.Value ?? 55;
        public int TaaFadeStartLog10 => _taaFadeStartSlider?.Value ?? 15;
        public int TaaFadeEndLog10 => _taaFadeEndSlider?.Value ?? 18;

        // ── Post-FX lock state + setters (used by theme-switch snap) ──────
        public bool BrightnessLocked => _chkLockBrightness?.Checked ?? false;
        public bool ContrastLocked => _chkLockContrast?.Checked ?? false;
        public bool AdaptiveLocked => _chkLockAdaptive?.Checked ?? false;

        public int BrightnessValue => _brightnessSlider?.Value ?? 0;
        public int ContrastValue => _contrastSlider?.Value ?? 0;
        public int AdaptiveValue => _histogramEqSlider?.Value ?? 0;

        /// <summary>
        /// Sets the brightness slider position; fires its ValueChanged so the
        /// existing OnAdjustBrightness pipeline runs (label update + renderer).
        /// Clamps to slider range.
        /// </summary>
        public void SetBrightness(int value)
        {
            if (_brightnessSlider == null) return;
            int v = Math.Clamp(value, _brightnessSlider.Minimum, _brightnessSlider.Maximum);
            if (_brightnessSlider.Value != v) _brightnessSlider.Value = v;
        }

        public void SetContrast(int value)
        {
            if (_contrastSlider == null) return;
            int v = Math.Clamp(value, _contrastSlider.Minimum, _contrastSlider.Maximum);
            if (_contrastSlider.Value != v) _contrastSlider.Value = v;
        }

        public void SetAdaptive(int value)
        {
            if (_histogramEqSlider == null) return;
            int v = Math.Clamp(value, _histogramEqSlider.Minimum, _histogramEqSlider.Maximum);
            if (_histogramEqSlider.Value != v) _histogramEqSlider.Value = v;
        }

        /// <summary>
        /// Enables or disables the Adaptive (histogram equalization) slider and
        /// its lock checkbox. Adaptive is only meaningful for the Mandelbrot
        /// engine; on other fractal types the control is greyed out so the UI
        /// honestly reflects that it has no effect.
        /// </summary>
        public void SetAdaptiveEnabled(bool enabled)
        {
            if (_histogramEqSlider != null) _histogramEqSlider.Enabled = enabled;
            if (_histogramEqLabel != null) _histogramEqLabel.Enabled = enabled;
            if (_chkLockAdaptive != null) _chkLockAdaptive.Enabled = enabled;
        }

        private CheckBox MakeLockBox(int left, int top, string tooltip)
        {
            var cb = new CheckBox
            {
                Left = left,
                Top = top,
                Width = 22,
                Height = 18,
                Text = "",
                AutoSize = false,
                ForeColor = Color.FromArgb(200, 200, 120),
                BackColor = Color.Transparent,
                Appearance = Appearance.Normal,
                Checked = false,
            };
            _toolTip.SetToolTip(cb, tooltip);
            return cb;
        }

        public void ResetView(double centerX, double centerY, double zoom)
        {
            _txCX.Text = centerX.ToString();
            _txCY.Text = centerY.ToString();
            _txZoom.Text = zoom.ToString();
            _brightnessSlider?.Value = 0;
            _contrastSlider?.Value = 0;
            _histogramEqSlider?.Value = 0;
            _brightnessLabel?.Text = "Brightness: 0";
            _contrastLabel?.Text = "Contrast: 0";
            _histogramEqLabel?.Text = "Adaptive: 0";

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

        /// <summary>
        /// Pushes the current view zoom into the color theme combo so themes
        /// whose <see cref="Models.ColorPalette.GetStaticMaxZoom"/> is below
        /// it are rendered dimmed + strikethrough. Items remain selectable.
        /// </summary>
        public void SetCurrentZoom(double zoom)
        {
            if (_colorThemeCombo is ColorComboBox ccb)
                ccb.CurrentZoom = zoom;
        }

        #endregion Public Methods
    }
}


