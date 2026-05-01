// MainForm.cs  — v5
//
// Changes over v4
//   • FIX: GridOverlayPanel Win32Exception resolved — Visible is set to false
//     in the constructor (no handle yet) and the grid checkbox handler defers
//     showing the panel until after OnLoad completes.
//   • FEATURE: Slideshow button — cycles random built-in regions every 30 s,
//     changes colour theme every 10 s with a 2-second CPU-blended cross-fade
//     between both theme changes and region transitions.
//   • FEATURE: "Lock" checkbox in the Navigate bar — when checked, the current
//     Iter value is kept fixed for all operations (pan, zoom, region change).
//   • FEATURE: Iteration limit raised — values above 65535 are now accepted;
//     validation upper bound removed, only minimum of 64 enforced.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

/// <summary>
/// Fracturing Fog main window and UI logic.  This class is responsible for:
/// </summary>
public sealed class MainForm : Form
{
    #region Private fields

    #region Program

    private readonly string _programVersion = "0.1";
    private readonly string _programName = "Fracturing Fog";
    private bool _disposed;

    #endregion Program

    #region UI

    // UI: top toolbar
    private readonly Panel _toolbar;
    private readonly Button _resetButton;
    private readonly Button _spanButton;
    private readonly Button _posterButton;
    private readonly Button _screenshotButton;
    private readonly Button _slideshowButton;
    private readonly Label _qualityLabel;
    private readonly ComboBox _qualityCombo;
    private readonly Label _colorThemeLabel;
    private readonly ComboBox _colorThemeCombo;
    private readonly Label _regionLabel;
    private readonly ComboBox _regionCombo;
    private readonly Button _saveViewButton;
    private readonly Button _saveViewButton2;
    private readonly Button _delRegionButton;
    private readonly Button _delRegionButton2;
    private readonly CheckBox _checkBoxShowCoordPanel;
    private readonly CheckBox _checkBoxShowCoordPanel2;
    private readonly CheckBox _checkBoxShowFooterPanel;
    private readonly CheckBox _checkBoxShowFooterPanel2;
    private readonly CheckBox _checkBoxShowGrid;
    private readonly CheckBox _checkBoxShowGrid2;
    private readonly ToolTip _toolTip = new();
    private int _toolbarLastWidth;
    private int _toolbarLastHeight;

    // UI: coordinate / region bar
    private readonly Panel _coordPanel;
    private readonly Label _lblCX;
    private readonly TextBox _txCX;
    private readonly Label _lblCY;
    private readonly TextBox _txCY;
    private readonly Label _qualityLabel2;
    private readonly ComboBox _qualityCombo2;
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
    private readonly ComboBox _regionCombo2;
    private Label? _currentColorThemeLabel;
    private readonly ComboBox _colorThemeCombo2;
    private readonly Button _exportColorThemeButton;
    private readonly Button _importColorThemeButton;
    private readonly Button _deleteColorThemeButton;
    private readonly Button _loadColorThemesButton;

    // Render panel
    private readonly RenderPanel _renderPanel;

    // GridOverlayPanel — sibling of _renderPanel; see architecture note below.
    // Visible is always false until the user ticks "Grid" AFTER OnLoad.
    private readonly GridOverlayPanel _gridPanel;
    private bool _gridVisible = false;

    // Force D3D11 mode for testing:  (change the next line, recompile, and run on a D3D12-capable machine)
    private bool _forceD3D11 => true;

    // Mini-map
    private MiniMapPanel? _miniMapPanel;

    // Footer
    private readonly Label _statusLabel;
    private readonly Panel _footerPanel;

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

    #endregion UI

    #region View state

    private const double DefaultCenterX = -0.5;
    private const double DefaultCenterY = 0.0;
    private const double DefaultZoom = 0.3;

    private double _centerX = DefaultCenterX;
    private double _centerXLo = 0.0;
    private double _centerX2 = 0.0;
    private double _centerX3 = 0.0;
    private double _centerY = DefaultCenterY;
    private double _centerYLo = 0.0;
    private double _centerY2 = 0.0;
    private double _centerY3 = 0.0;
    private double _zoom = DefaultZoom;

    // Above this zoom, pan/zoom math promotes to QD (4-double, ~62 digits).
    // Below it, DD (~31 digits) is sufficient.
    private const double QDZoomThreshold = 1e25;


    private IColorMap _defaultColorMap = new FirePalette();

    // Active quality preset — Standard by default.
    private QualityPreset _quality = QualityPreset.Standard;

    // Guard: prevents coord boxes being repopulated while user types.
    private bool _suppressCoordUpdate;

    // MiniMode flag: when true, the form is shrunk to its minimum size and borders removed.
    private bool _miniClick;
    private bool _miniMode = false;
    private Size _miniPreviousSize;
    private FormBorderStyle _miniPreviousBorderStyle;

    // Pan state 
    private bool _panning;
    private Point _panStartScreen;
    private double _panStartCX;
    private double _panStartCY;
    private FracturingFog.FFMath.DD _panStartDDCX;
    private FracturingFog.FFMath.DD _panStartDDCY;
    private FracturingFog.FFMath.QD _panStartQDCX;
    private FracturingFog.FFMath.QD _panStartQDCY;


    // Pan-stop debounce timer — fires full-quality render after drag ends.
    private readonly System.Windows.Forms.Timer _panStopTimer;

    // Double-click pan suppression
    private Point _lastMouseDownPos;

    // Multi-monitor span state
    private bool _spanning;
    private bool _fullScreen;
    private Rectangle _preSpanBounds;
    private FormBorderStyle _preSpanBorderStyle;
    private FormWindowState _preSpanWindowState;
    private bool _preToolBarVisible;
    private bool _preCoordBarVisible;
    private bool _preFooterVisible;

    #endregion View state

    #region Core objects - Async calculation - Buffer management

    // Core Objects
    private IFractalRenderer? _renderer;          // D3D12 or D3D11
    private MandelbrotCalculator? _calculator;
    private CancellationTokenSource? _calcCts;
    private readonly object _calcLock = new();

    // The most recently completed, post-processed colour buffer.
    // Re-uploaded at the start of every new calculation so the previous
    // frame stays visible while the next one is being computed, preventing
    // the black-flash that occurs at High/Ultra quality (DD arithmetic).
    private uint[]? _lastUploadedBuffer;
    private int _lastUploadedWidth;
    private int _lastUploadedHeight;

    private CancellationTokenSource? _wallpaperCts;
    private readonly object _wallpaperLock = new();

    // Iteration lock
    private bool _iterLocked;       // mirrors _chkLockIter.Checked
    private int _lockedIterations; // value held while locked

    #endregion Async calculation - Buffer management

    #region Slideshow state

    private bool _slideshowRunning;
    private CancellationTokenSource? _slideshowCts;
    private CancellationTokenSource? _slideshowRegionSkipCts;   // cancelled to unlock region during slideshow
    private readonly object _slideshowLock = new();
    private readonly Random _slideshowRng = new();
    private bool _showSlideshowWatermark;   // true only while slideshow runs
    private string _slideshowRegionName = "";
    private bool _slideshowSkipRegion;   // set to true to skip the current region and move to the next one immediately
    private bool _slideShowLockRegion;     // When true, the slideshow will not change regions; only themes.  Set by Shift+clicking the Slideshow button.

    #endregion Slideshow state

    #region DLL Imports

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);
    [DllImport("User32.dll")]
    private static extern bool ReleaseCapture();
    [DllImport("User32.dll")]
    private static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

    #endregion DLL Imports

    #endregion Private fields

    #region Public Members

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

    #endregion Public Members

    #region Constructors

    /// <summary>
    /// MainForm constructor: sets up the UI and event handlers.  The actual fractal renderer and 
    /// calculator are not initialised here; that happens in OnLoad to ensure the form is fully created 
    /// before we attempt to create D3D devices or load shaders.
    /// </summary>
    public MainForm()
    {
        Text = $"{_programName} v{_programVersion} - {RendererFactory.ProbeDescription()}";
        ClientSize = new Size(1265, 728);
        MinimumSize = new Size(480, 270);
        BackColor = Color.Black;
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        _miniPreviousBorderStyle = FormBorderStyle;
        _miniPreviousSize = Size;

        #region Pan-stop timer 
        _panStopTimer = new System.Windows.Forms.Timer { Interval = 300 };
        _panStopTimer.Tick += (s, e) =>
        {
            _panStopTimer.Stop();
            TriggerCalculation(progressive: false);   // full quality after drag stops
        };
        #endregion Pan-stop timer

        #region Form Helpers

        Button MakeBtn(
            string text,
            int w = 108,
            int left = 0,
            int top = 6,
            string toolTip = "")
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

            if (!string.IsNullOrEmpty(toolTip))
            {
                _toolTip.SetToolTip(_b, toolTip);
            }

            return _b;
        }

        Label MakeLbl(string text, int left, int top, Panel p, bool rightAlign) => new Label
        {
            Text = text,
            Left = left,
            Top = top,
            AutoSize = rightAlign ? false : true,
            TextAlign = rightAlign ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(155, 155, 155),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            BackColor = Color.Transparent
        }.AlsoAdd(p);

        TextBox MakeTx(int left, int top, int w, Panel p, string tip) => new TextBox
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

        #region Top toolbar 

        _toolbar = new Panel
        {
            Height = 38,
            Dock = DockStyle.Top,
            BackColor = Color.FromArgb(28, 28, 28),
        };
        _toolbar.MouseDown += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                // Drag the window when the user clicks and drags the toolbar.
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
            }
        };

        int buttonLeft = 6;
        int buttonTop = 6;
        int labelTop = 9;
        int txTop = 7;

        _resetButton = MakeBtn("", 30, buttonLeft, buttonTop, "Reset view to default center and zoom");
        _resetButton.Padding = new Padding(0, 0, 1, 1);
        _resetButton.Margin = new Padding(0);
        _resetButton.Click += OnResetClick;
        try
        {
            Image resetImg = (Image)new Bitmap(Image.FromFile(@"Resources\reset.bmp"))
                .GetThumbnailImage(24, 20, null, IntPtr.Zero);
            _resetButton.Image = resetImg;
        }
        catch { _resetButton.Text = "R"; }
        _toolbar.Controls.Add(_resetButton);
        buttonLeft += 33;

        _spanButton = MakeBtn("Span", 55, buttonLeft, buttonTop, "Span across all monitors");
        _spanButton.Click += OnSpanMonitorsClick;
        _toolbar.Controls.Add(_spanButton);
        buttonLeft += 58;

        _screenshotButton = MakeBtn("Image", 55, buttonLeft, buttonTop);
        _screenshotButton.Click += OnScreenshotClick;
        _toolbar.Controls.Add(_screenshotButton);
        buttonLeft += 58;

        _posterButton = MakeBtn("Poster", 55, buttonLeft, buttonTop);
        _posterButton.Click += OnPosterClick;
        _toolbar.Controls.Add(_posterButton);
        buttonLeft += 58;

        _slideshowButton = MakeBtn("Slideshow", 72, buttonLeft, buttonTop, "Start/stop slideshow — auto-cycles regions every 30 s, themes every 10 s");
        _slideshowButton.BackColor = Color.FromArgb(40, 55, 40);
        _slideshowButton.FlatAppearance.BorderColor = Color.FromArgb(60, 100, 60);
        _slideshowButton.Click += OnSlideshowClick;
        _toolbar.Controls.Add(_slideshowButton);
        buttonLeft += 76;
        #endregion Top toolbar

        #region Quality label + combo.
        _toolbar.Controls.Add(new Label { Left = buttonLeft, Top = 4, Width = 1, Height = 30, BackColor = Color.FromArgb(65, 65, 65) });
        buttonLeft += 8;

        _qualityLabel = new Label
        {
            Text = "Quality:",
            Left = buttonLeft,
            Top = 10,
            AutoSize = true,
            ForeColor = Color.FromArgb(155, 155, 155),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            BackColor = Color.Transparent
        };
        _toolbar.Controls.Add(_qualityLabel);
        buttonLeft += _qualityLabel.PreferredWidth + 4;

        _qualityCombo = new ComboBox
        {
            Left = buttonLeft,
            Top = 7,
            Width = 80,
            Height = 26,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(45, 45, 45),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        foreach (var p in QualityPreset.All) _qualityCombo.Items.Add(p.Name);
        _qualityCombo.SelectedIndex = 1;

        var qualityTip = new ToolTip();
        _qualityCombo.SelectedIndexChanged += (s, e) =>
        {
            int i = _qualityCombo.SelectedIndex;
            if (i >= 0 && i < QualityPreset.All.Length)
                qualityTip.SetToolTip(_qualityCombo, QualityPreset.All[i].Description);
            OnQualityComboChanged(s, e);
        };
        qualityTip.SetToolTip(_qualityCombo, QualityPreset.Standard.Description);
        _toolbar.Controls.Add(_qualityCombo);
        buttonLeft += 84;
        #endregion

        #region Theme
        _toolbar.Controls.Add(new Label { Left = buttonLeft, Top = 4, Width = 1, Height = 30, BackColor = Color.FromArgb(65, 65, 65) });
        buttonLeft += 10;

        // Theme label + combo.
        _colorThemeLabel = new Label
        {
            Text = "Theme:",
            Left = buttonLeft,
            Top = 10,
            AutoSize = true,
            ForeColor = Color.FromArgb(155, 155, 155),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            BackColor = Color.Transparent
        };
        _toolbar.Controls.Add(_colorThemeLabel);
        buttonLeft += _colorThemeLabel.PreferredWidth + 4;
        Models.ColorPalette.LoadUserThemes();
        _colorThemeCombo = new ColorComboBox
        {
            Left = buttonLeft,
            Top = 7,
            Width = 162,
            Height = 26,
            BackColor = Color.FromArgb(55, 55, 55),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Cursor = Cursors.Hand,
            DropDownWidth = Math.Max(300, Models.ColorPalette.GetMaxDescriptionLength() + 40)   // ensure descriptions fit in the dropdown
        };

        _colorThemeCombo.SelectedIndexChanged += OnColorThemeChanged;
        _toolbar.Controls.Add(_colorThemeCombo);
        buttonLeft += 170;
        #endregion

        #region Regions
        _toolbar.Controls.Add(new Label { Left = buttonLeft, Top = 2, Width = 1, Height = 30, BackColor = Color.FromArgb(60, 60, 60) });
        buttonLeft += 10;

        _regionLabel = new Label
        {
            Text = "Region:",
            Left = buttonLeft,
            Top = 10,
            AutoSize = true,
            ForeColor = Color.FromArgb(155, 155, 155),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            BackColor = Color.Transparent
        };
        _toolbar.Controls.Add(_regionLabel);
        buttonLeft += _regionLabel.PreferredWidth + 3;

        _regionCombo = new ComboBox
        {
            Left = buttonLeft,
            Top = 7,
            Width = 172,
            Height = 26,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(45, 45, 45),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9f),
            FlatStyle = FlatStyle.Flat
        };
        _toolbar.Controls.Add(_regionCombo);
        buttonLeft += 180;

        _saveViewButton = MakeBtn("Save", 55, buttonLeft, 6, "Save the current view as a region");
        _saveViewButton.Click += OnSaveViewClick;
        _toolbar.Controls.Add(_saveViewButton);
        buttonLeft += 58;

        _delRegionButton = MakeBtn("Delete", 55, buttonLeft, 6, "Delete the selected region");
        _delRegionButton.Click += OnDelRegionClick;
        _toolbar.Controls.Add(_delRegionButton);
        buttonLeft += 58;
        #endregion

        #region Checkboxes
        _toolbar.Controls.Add(new Label { Left = buttonLeft, Top = 2, Width = 1, Height = 30, BackColor = Color.FromArgb(60, 60, 60) });
        buttonLeft += 10;

        _checkBoxShowCoordPanel = new CheckBox
        {
            Text = "Navigate",
            Left = buttonLeft,
            Top = 9,
            AutoSize = true,
            AutoCheck = true,
            ForeColor = Color.FromArgb(155, 155, 155),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            BackColor = Color.Transparent,
            Checked = false,
        };
        buttonLeft += _checkBoxShowCoordPanel.PreferredSize.Width + 6;

        _checkBoxShowFooterPanel = new CheckBox
        {
            Text = "Status",
            Left = buttonLeft,
            Top = 9,
            AutoSize = true,
            AutoCheck = true,
            ForeColor = Color.FromArgb(155, 155, 155),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            BackColor = Color.Transparent,
            Checked = true,
        };
        _toolbar.Controls.Add(_checkBoxShowCoordPanel);
        _toolbar.Controls.Add(_checkBoxShowFooterPanel);
        buttonLeft += _checkBoxShowFooterPanel.PreferredSize.Width + 12;

        // Grid overlay toggle.
        _checkBoxShowGrid = new CheckBox
        {
            Text = "Grid",
            Left = buttonLeft,
            Top = 9,
            AutoSize = true,
            AutoCheck = true,
            ForeColor = Color.FromArgb(155, 155, 155),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            BackColor = Color.Transparent,
            Checked = false,
        };
        _toolTip.SetToolTip(_checkBoxShowGrid, "Overlay a Cartesian complex-plane grid on the fractal view");
        _toolbar.Controls.Add(_checkBoxShowGrid);
        #endregion

        #region Footer panel

        _footerPanel = new Panel
        {
            Height = 22,
            Dock = DockStyle.Bottom,
            BackColor = Color.FromArgb(18, 18, 18),
        };
        _statusLabel = new Label
        {
            Left = 6,
            Top = 6,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(140, 140, 140),
            BackColor = Color.Transparent,
            Font = new Font("Consolas", 8f),
            Text = "Initialising…"
        };
        _statusLabel.MouseMove += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                // Drag the window when the user clicks and drags the status label.
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
            }
        };

        _footerPanel.MouseMove += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                // Drag the window when the user clicks and drags the footer panel.
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
            }
        };

        _footerPanel.Controls.Add(_statusLabel);
        _footerPanel.Visible = true;

        #endregion

        #region Coordinate / Navigate panel

        _coordPanel = new Panel
        {
            Width = 300,
            //Height = 58,
            //AutoSize = true,
            Dock = DockStyle.Left,
            BackColor = Color.FromArgb(22, 22, 22),
            Visible = false,   // hidden until user ticks Navigate
        };

        buttonLeft = 45;
        labelTop = 38;
        txTop = 35;

        _checkBoxShowCoordPanel2 = new CheckBox
        {
            Text = "Navigate",
            Left = buttonLeft,
            Top = 9,
            AutoSize = true,
            AutoCheck = true,
            ForeColor = Color.FromArgb(155, 155, 155),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            BackColor = Color.Transparent,
            Checked = false,
        };
        buttonLeft += _checkBoxShowCoordPanel2.PreferredSize.Width + 6;

        _checkBoxShowFooterPanel2 = new CheckBox
        {
            Text = "Status",
            Left = buttonLeft,
            Top = 9,
            AutoSize = true,
            AutoCheck = true,
            ForeColor = Color.FromArgb(155, 155, 155),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            BackColor = Color.Transparent,
            Checked = true,
        };
        _coordPanel.Controls.Add(_checkBoxShowCoordPanel2);
        _coordPanel.Controls.Add(_checkBoxShowFooterPanel2);
        buttonLeft += _checkBoxShowFooterPanel2.PreferredSize.Width + 12;

        // Grid overlay toggle.
        _checkBoxShowGrid2 = new CheckBox
        {
            Text = "Grid",
            Left = buttonLeft,
            Top = 9,
            AutoSize = true,
            AutoCheck = true,
            ForeColor = Color.FromArgb(155, 155, 155),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            BackColor = Color.Transparent,
            Checked = false,
        };
        _coordPanel.Controls.Add(_checkBoxShowGrid2);
        _toolTip.SetToolTip(_checkBoxShowGrid2, "Overlay a Cartesian complex-plane grid on the fractal view");

        buttonLeft = 8;

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
        _qualityLabel2 = MakeLbl("Quality:", buttonLeft, labelTop, _coordPanel, true);
        _qualityLabel2.Height = 13;
        _qualityLabel2.Width = 78;
        _qualityLabel2.Padding = new Padding(0);
        buttonLeft = _qualityLabel2.Left + _qualityLabel2.Width + 10;

        _qualityCombo2 = new ComboBox
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
        foreach (var p in QualityPreset.All) _qualityCombo2.Items.Add(p.Name);
        _qualityCombo2.SelectedIndexChanged += (s, e) =>
        {
            int i = _qualityCombo2.SelectedIndex;
            if (i >= 0 && i < QualityPreset.All.Length)
                qualityTip.SetToolTip(_qualityCombo2, QualityPreset.All[i].Description);
            OnQualityComboChanged(s, e);
        };
        _coordPanel.Controls.Add(_qualityCombo2);
        _qualityCombo2.Text = _qualityCombo.Text;   // sync with top combo

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
        _chkLockIter.CheckedChanged += OnIterLockChanged;
        _coordPanel.Controls.Add(_chkLockIter);

        buttonLeft = 98;
        buttonTop += 38;
        labelTop += 28;
        txTop += 28;

        _goButton = MakeBtn("Go", 54, buttonLeft, buttonTop, "Go to the specified coordinates");
        _goButton.BackColor = Color.FromArgb(40, 80, 40);
        _goButton.FlatAppearance.BorderColor = Color.FromArgb(70, 120, 70);
        _goButton.Click += OnGoClick;
        _coordPanel.Controls.Add(_goButton);

        buttonLeft += 62;
        _flipButton = MakeBtn("Flip Y", 54, buttonLeft, buttonTop, "Flip the view vertically (negate CY)");
        _flipButton.BackColor = Color.FromArgb(40, 80, 40);
        _flipButton.FlatAppearance.BorderColor = Color.FromArgb(70, 120, 70);
        _flipButton.Click += OnFlipClick;
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
        _brightnessSlider.ValueChanged += (s, e) =>
        {
            _brightness = _brightnessSlider.Value;
            if (_brightnessLabel != null)
                _brightnessLabel.Text = $"Brightness: {_brightness:+0;-0;0}";
            RepaintWithBrightnessContrast();
        };
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
        _contrastSlider.ValueChanged += (s, e) =>
        {
            _contrast = _contrastSlider.Value;
            if (_contrastLabel != null)
                _contrastLabel.Text = $"Contrast: {_contrast:+0;-0;0}";
            RepaintWithBrightnessContrast();
        };
        _coordPanel.Controls.Add(_contrastSlider);
        #endregion Brightness & Contrast sliders 

        #region Region Import/Export buttons
        GroupBox regionBox = new GroupBox
        {
            Text = "Regions",
            Left = 28,
            Top = sliderTop + 58,
            Width = 260,
            Height = 78,
            ForeColor = Color.FromArgb(155, 155, 155),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            BackColor = Color.FromArgb(22, 22, 22),
        };
        _coordPanel.Controls.Add(regionBox);

        _regionCombo2 = new ComboBox
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
        _regionCombo2.SelectedIndexChanged += (s, e) =>
        {
            // Sync the second combo with the main one; it's just there to show the new region when you import it without having to re-select the region in the main combo.
            _regionCombo.SelectedIndex = _regionCombo2.SelectedIndex;
        };
        regionBox.Controls.Add(_regionCombo2);

        _saveViewButton2 = MakeBtn("Save", 55, 16, 45, "Save the current view as a region");
        _saveViewButton2.Click += OnSaveViewClick;
        regionBox.Controls.Add(_saveViewButton2);
        buttonLeft += 58;

        _delRegionButton2 = MakeBtn("Delete", 55, _saveViewButton2.Left + _saveViewButton2.Width + 3, 45, "Delete the selected region");
        _delRegionButton2.Click += OnDelRegionClick;
        regionBox.Controls.Add(_delRegionButton2);
        buttonLeft = 98;

        _exportRegionsButton = MakeBtn("Exp...", 55, _delRegionButton2.Left + _delRegionButton2.Width + 3, 45, "Export all custom regions to a JSON file");
        _exportRegionsButton.Click += OnExportRegionsClick;
        regionBox.Controls.Add(_exportRegionsButton);
        buttonLeft += 58;

        _importRegionsButton = MakeBtn("Imp...", 55, _exportRegionsButton.Left + _exportRegionsButton.Width + 3, 45, "Import custom regions from a JSON file (duplicates get '-imp' suffix)");
        _importRegionsButton.FlatAppearance.BorderColor = Color.FromArgb(60, 90, 120);
        _importRegionsButton.Click += OnImportRegionsClick;
        regionBox.Controls.Add(_importRegionsButton);
        buttonLeft += 58;
        #endregion Region Import/Export buttons

        #region Color Theme Import/Export buttons
        GroupBox themeBox = new GroupBox
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
        _coordPanel.Controls.Add(themeBox);

        _colorThemeCombo2 = new ColorComboBox
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
        _colorThemeCombo2.SelectedIndexChanged += (s, e) =>
        {
            // Sync the second combo with the main one; it's just there to show the new theme when you import it without having to re-select the theme in the main combo.
            _colorThemeCombo?.SelectedIndex = _colorThemeCombo2.SelectedIndex;
        };
        themeBox.Controls.Add(_colorThemeCombo2);

        _exportColorThemeButton = MakeBtn("Exp...", 55, 16, 48, "Export the current color theme to a JSON file");
        _exportColorThemeButton.Click += OnExportColorThemeClick;
        themeBox.Controls.Add(_exportColorThemeButton);
        _importColorThemeButton = MakeBtn("Imp...", 55, _exportColorThemeButton.Left + _exportColorThemeButton.Width + 3, 48, "Import color themes from a JSON file");
        _importColorThemeButton.Click += OnImportColorThemeClick;
        themeBox.Controls.Add(_importColorThemeButton);

        _deleteColorThemeButton = MakeBtn("Delete", 55, _importColorThemeButton.Left + _importColorThemeButton.Width + 3, 48, "Delete selected user-defined color theme");
        _deleteColorThemeButton.Click += OnDeleteColorThemeClick;
        themeBox.Controls.Add(_deleteColorThemeButton);

        _loadColorThemesButton = MakeBtn("Reload", 55, _deleteColorThemeButton.Left + _deleteColorThemeButton.Width + 3, 48, "Reload color themes from disk (useful if you edit the JSON files externally)");
        _loadColorThemesButton.Click += OnLoadColorThemesClick;
        themeBox.Controls.Add(_loadColorThemesButton);

        #endregion Color Theme Import/Export buttons
        #endregion Coordinate / Navigate panel

        #region Render panel

        _renderPanel = new RenderPanel { Dock = DockStyle.Fill, Cursor = Cursors.Cross };
        _renderPanel.MouseWheel += OnMouseWheel;
        _renderPanel.MouseDown += OnMouseDown;
        _renderPanel.MouseMove += OnMouseMove;
        _renderPanel.MouseUp += OnMouseUp;
        _renderPanel.MouseDoubleClick += OnMouseDoubleClick;

        // Grid overlay panel
        // The WS_EX_LAYERED sibling-panel approach was unreliable because the
        // D3D11 FlipDiscard swap chain presents directly to the compositor and
        // GDI layered windows cannot composite over it on modern Windows.
        // Instead the grid is drawn into a transparent GDI+ bitmap and then
        // blended pixel-by-pixel into the fractal ColorBuffer before the
        // texture is uploaded to the GPU.  This is fully reliable and correct.
        _gridPanel = new GridOverlayPanel(
            getCenter: () => (_centerX, _centerY),
            getZoom: () => _zoom,
            getPanelSize: () => _renderPanel.ClientSize,
            getSwatchColor: () => GetSwatchColor())
        {
            Visible = false,   // panel itself is never shown; only used for drawing logic
        };

        #region Context menu for render panel
        var contextMenu = new ContextMenuStrip();
        var toolbarItem = new ToolStripMenuItem("Toolbar", null, (s, e) =>
        {
            _toolbar.Visible = !_toolbar.Visible;
        })
        { Checked = true };
        var navigateItem = new ToolStripMenuItem("Navigate");
        var statusItem = new ToolStripMenuItem("Status", null, (s, e) =>
        {
            _checkBoxShowFooterPanel.Checked = !_checkBoxShowFooterPanel.Checked;
            _footerPanel.Visible = _checkBoxShowFooterPanel.Checked;
        });
        var onTopItem = new ToolStripMenuItem("On Top", null, (s, e) =>
        {
            TopMost = !TopMost;
        });
        var miniModeItem = new ToolStripMenuItem("Mini Mode", null, (s, e) =>
        {
            bool wasMini = _miniMode;
            _miniMode = !_miniMode;

            if (_miniMode)
            {
                _miniPreviousBorderStyle = FormBorderStyle;
                _miniPreviousSize = Size;
                _coordPanel.Visible = false;   // hide coordinate panel in mini mode since it doesn't work well there
                TopMost = true;  // mini mode is meant for keeping the window visible while doing other things, so force it on top
                _toolbar.Visible = false;
            }
            _miniClick = true;
            OnFormResize(s, e);  // adjust size and borders
            if (wasMini && !_miniMode)
                CenterToScreen();  // re-center when exiting mini mode since we likely moved the window around while in mini mode
            _miniClick = false;
        });
        var gridItem = new ToolStripMenuItem("Grid", null, (s, e) =>
        {
            _gridVisible = !_gridVisible;
            _checkBoxShowGrid.Checked = _gridVisible;
            _checkBoxShowGrid2.Checked = _gridVisible;
            RepaintWithBrightnessContrast();
        });

        var watermarkItem = new ToolStripMenuItem("Slideshow: Toggle Watermark", null, (s, e) =>
        _showSlideshowWatermark = !_showSlideshowWatermark)
        { Enabled = false };
        var spanMonitorsItem = new ToolStripMenuItem("Span Monitors", null, (s, e) => OnSpanMonitorsClick(s, e));
        var restoreMonitorsItem = new ToolStripMenuItem("Restore Monitors", null, (s, e) => OnSpanMonitorsClick(s, e))
        { Visible = false };
        var slideshowItem = new ToolStripMenuItem("Start Slideshow", null, (s, e) => OnSlideshowClick(s, e));
        var skipItem = new ToolStripMenuItem("Slideshow: Skip to Next Region", null, (s, e) => SkipSlideshowRegion())
        { Enabled = false };
        var slideshowLockRegionItem = new ToolStripMenuItem("Slideshow: Lock Region", null, (s, e) =>
        {
            ToggleSlideshowRegionLock();
        });
        var miniMapItem = new ToolStripMenuItem("Mini Map", null, (s, e) => ToggleMiniMap());
        var systemInfoItem = new ToolStripMenuItem("System Info…", null, (s, e) => ShowSystemInfoDialog());
        var saveRegionItem = new ToolStripMenuItem("Save Current Region", null, (s, e) => OnSaveViewClick(s, e));
        var resetViewItem = new ToolStripMenuItem("Reset View", null, (s, e) => OnResetClick(s, e));
        var saveImageItem = new ToolStripMenuItem("Save Image…", null, (s, e) => OnScreenshotClick(s, e));

        contextMenu.Opening += (s, e) =>
        {
            statusItem.Checked = _footerPanel.Visible;
            miniModeItem.Enabled = !_spanning;
            spanMonitorsItem.Visible = !_spanning;
            spanMonitorsItem.Enabled = !_miniMode;
            restoreMonitorsItem.Visible = _spanning;
            restoreMonitorsItem.Checked = _spanning;
            gridItem.Checked = _gridVisible;
            skipItem.Enabled = _slideshowRunning;
            miniMapItem.Checked = _miniMapPanel?.Visible ?? false;
            miniMapItem.Enabled = !_miniMode;  // mini map doesn't work well in mini mode since it's already small and has no extra space for the inset
            miniMapItem.Visible = !_miniMode;  // hide mini map option in mini mode since it doesn't work well there
            slideshowItem.Checked = _slideshowRunning;
            slideshowLockRegionItem.Checked = _slideShowLockRegion;
            watermarkItem.Enabled = _slideshowRunning;
            slideshowItem.Text = _slideshowRunning ? "Stop Slideshow" : "Start Slideshow";
            watermarkItem.Checked = _showSlideshowWatermark;
            miniModeItem.Checked = _miniMode;
            onTopItem.Checked = TopMost;
            statusItem.Checked = _footerPanel.Visible;
            navigateItem.Checked = _checkBoxShowCoordPanel.Checked;
            navigateItem.Enabled = !_miniMode;  // navigating in mini mode is awkward and not worth supporting
            navigateItem.Visible = !_miniMode;  // hide navigation option in mini mode since it doesn't work well there
            toolbarItem.Checked = _toolbar.Visible;
        };

        contextMenu.Items.Add(toolbarItem);
        contextMenu.Items.Add(navigateItem);
        contextMenu.Items.Add(statusItem);
        contextMenu.Items.Add(resetViewItem);
        contextMenu.Items.Add(gridItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(onTopItem);
        contextMenu.Items.Add(spanMonitorsItem);
        contextMenu.Items.Add(restoreMonitorsItem);
        contextMenu.Items.Add(miniModeItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(slideshowItem);
        contextMenu.Items.Add(watermarkItem);
        contextMenu.Items.Add(slideshowLockRegionItem);
        contextMenu.Items.Add(skipItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(saveRegionItem);
        contextMenu.Items.Add(saveImageItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(miniMapItem);
        contextMenu.Items.Add(systemInfoItem);
        _renderPanel.ContextMenuStrip = contextMenu;
        #endregion Context menu for render panel

        // Build list sources for combos that need it.
        BuildColorThemesSelection();
        _colorThemeCombo2.SelectedIndex = 0;

        _checkBoxShowCoordPanel.Click += (s, e) =>
        {
            _checkBoxShowCoordPanel2.Checked = _checkBoxShowCoordPanel.Checked;  // sync the coordinate panel checkbox in the coordinate panel with the main one
            OnShowCoordPanelClick();
        };

        _checkBoxShowCoordPanel2.Click += (s, e) =>
        {
            _checkBoxShowCoordPanel.Checked = _checkBoxShowCoordPanel2.Checked;  // sync the coordinate panel checkbox in the main toolbar with the one in the coordinate panel
            OnShowCoordPanelClick();
        };

        navigateItem.Click += (s, e) =>
        {
            _checkBoxShowCoordPanel.Checked = !_checkBoxShowCoordPanel.Checked;
            _checkBoxShowCoordPanel2.Checked = _checkBoxShowCoordPanel.Checked;  // sync the coordinate panel checkbox in the coordinate panel with the main one
            OnShowCoordPanelClick();
        };

        _checkBoxShowFooterPanel.Click += (s, e) =>
        {
            _checkBoxShowFooterPanel2.Checked = _checkBoxShowFooterPanel.Checked;  // sync the coordinate panel checkbox with the main one
            OnCheckBoxShowFooterPanelClicked();
        };

        _checkBoxShowFooterPanel2.Click += (s, e) =>
        {
            _checkBoxShowFooterPanel.Checked = _checkBoxShowFooterPanel2.Checked;  // sync the coordinate panel checkbox with the main one
            OnCheckBoxShowFooterPanelClicked();
        };

        // Grid toggle — re-render with or without the grid overlay.
        _checkBoxShowGrid.Click += (s, e) =>
        {
            _checkBoxShowGrid2.Checked = _checkBoxShowGrid.Checked;  // sync the coordinate panel checkbox with the main one
            OnCheckBoxShowGridClick();
        };

        _checkBoxShowGrid2.Click += (s, e) =>
        {
            _checkBoxShowGrid.Checked = _checkBoxShowGrid2.Checked;  // sync the main toolbar checkbox with the one in the coordinate panel
            OnCheckBoxShowGridClick();
        };

        #endregion Render panel

        // Docking / Z-order: Fill first, then Top-docked in reverse, footer last.
        Controls.Add(_renderPanel);
        Controls.Add(_coordPanel);
        Controls.Add(_toolbar);
        Controls.Add(_footerPanel);

        // ── Events ───────────────────────────────────────────────────────────
        Load += OnLoad;
        Resize += OnFormResize;
        KeyDown += OnKeyDown;
        FormClosing += OnFormClosing;
        Application.Idle += OnApplicationIdle;
    }

    private void OnCheckBoxShowGridClick()
    {
        _gridVisible = _checkBoxShowGrid.Checked;
        RepaintWithBrightnessContrast();
    }

    private void OnCheckBoxShowFooterPanelClicked()
    {
        _footerPanel.Visible = !_footerPanel.Visible;
        _checkBoxShowFooterPanel.Checked = _footerPanel.Visible;
        _checkBoxShowFooterPanel2.Checked = _footerPanel.Visible;  // sync the coordinate panel checkbox with the main one
    }

    private void OnShowCoordPanelClick()
    {
        _coordPanel.Visible = !_coordPanel.Visible;
        _checkBoxShowCoordPanel.Visible = !_coordPanel.Visible;  // only show the main toolbar checkbox when the coordinate panel itself is hidden, to avoid confusion between the two sets of checkboxes
        _checkBoxShowFooterPanel.Visible = !_coordPanel.Visible;  // only show the main toolbar checkbox when the coordinate panel itself is hidden, to avoid confusion between the two sets of checkboxes
        _checkBoxShowGrid.Visible = !_coordPanel.Visible;  // only show the main toolbar checkbox when the coordinate panel itself is hidden, to avoid confusion between the two sets of checkboxes
        _qualityCombo.Visible = !_coordPanel.Visible;  // only show the main toolbar quality combo when the coordinate panel itself is hidden, to avoid confusion between the two sets of controls
        _regionCombo.Visible = !_coordPanel.Visible;  // only show the main toolbar region combo when the coordinate panel itself is hidden, to avoid confusion between the two sets of controls
        _saveViewButton.Visible = !_coordPanel.Visible;  // only show the main toolbar save view button when the coordinate panel itself is hidden, to avoid confusion between the two sets of controls
        _delRegionButton.Visible = !_coordPanel.Visible;
        _qualityLabel.Visible = !_coordPanel.Visible;
        _colorThemeLabel.Visible = !_coordPanel.Visible;
        _colorThemeCombo.Visible = !_coordPanel.Visible;
        _regionLabel.Visible = !_coordPanel.Visible;

        if (_coordPanel.Visible)
        {
            _toolbarLastWidth = _toolbar.Width;
            _toolbarLastHeight = _toolbar.Height;
            _toolbar.Width = _coordPanel.Left + _coordPanel.Width;
        }
        else
        {
            _toolbar.Width = _toolbarLastWidth;
            _toolbar.Height = _toolbarLastHeight;
        }
    }

    #endregion Constructors

    private void OnLoadColorThemesClick(object? sender, EventArgs e)
    {
        string? currentText = _colorThemeCombo.GetItemText(_colorThemeCombo.SelectedItem);
        UserColorThemeLibrary.Instance.Load();
        BuildColorThemesSelection();
        int index = _colorThemeCombo.FindStringExact(currentText ?? string.Empty);
        _colorThemeCombo.SelectedIndex = index;

        var map = Models.ColorPalette.GetPaletteByName(_colorThemeCombo.GetItemText(_colorThemeCombo.SelectedItem));
        if (_calculator != null)
        {
            _calculator.ColorMap = map;
            TriggerCalculation();
        }
        _miniMapPanel?.RequestRedraw();
        SetStatus("Color themes reloaded.");
    }

    private void OnDeleteColorThemeClick(object? sender, EventArgs e)
    {
        string? name = _colorThemeCombo.SelectedItem as string;
        if (string.IsNullOrEmpty(name) || name == "— select theme —") return;

        if (MessageBox.Show($"Delete color theme \"{name}\"?", "Confirm Delete",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        UserColorThemeLibrary.Instance.Remove(name);
        UserColorThemeLibrary.Instance.Load();
        BuildColorThemesSelection();
        _colorThemeCombo.SelectedIndex = 0;
        SetStatus($"Color theme \"{name}\" deleted.");
    }

    private void OnImportColorThemeClick(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Import Color Theme",
            Filter = "JSON File (*.json)|*.json",
            DefaultExt = "json",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            string json = File.ReadAllText(dlg.FileName);
            Debug.WriteLine($"Importing color data: Length: {json?.Length}  ←  {dlg.FileName}");

            var opts = new JsonSerializerOptions();
            opts.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            var data = JsonSerializer.Deserialize<ColorThemeData>(json ?? string.Empty, opts);

            if (data == null
                || string.IsNullOrWhiteSpace(data.Name)
                || data.Stops == null
                || data.Stops.Count < 2)
            {
                MessageBox.Show(
                    "This file does not contain a valid color theme.\n\n" +
                    "Expected a single ColorThemeData object with a Name and at least two Stops.",
                    "Import Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Avoid colliding with built-in or existing user themes.  Built-ins are
            // matched first by GetPaletteByName, so a duplicate name would shadow
            // the import and make it un-selectable; auto-rename with a suffix.
            string originalName = data.Name;
            if (NameExistsInPalettes(data.Name))
            {
                int n = 2;
                while (NameExistsInPalettes($"{originalName} ({n})")) n++;
                data.Name = $"{originalName} ({n})";
            }

            if (!UserColorThemeLibrary.Instance.Add(data))
            {
                MessageBox.Show(
                    $"Failed to add theme '{data.Name}' to the user library.",
                    "Import Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // New theme is now in UserPalettes — rebuild the combo, select it,
            // and re-render with it active.
            BuildColorThemesSelection();
            ApplyColorThemeSilent(data.Name);
            TriggerCalculation();

            string suffixNote = data.Name == originalName
                ? string.Empty
                : $" (renamed from '{originalName}')";
            SetStatus($"Imported color theme '{data.Name}'{suffixNote}  ←  {Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Import failed:\n\n{ex.Message}", "Import Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static bool NameExistsInPalettes(string name)
    {
        foreach (var p in Models.ColorPalette.Palettes)
            if (string.Equals(Models.ColorPalette.GetStaticName(p), name, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private void OnExportColorThemeClick(object? sender, EventArgs e)
    {
        if (_calculator != null && _calculator.ColorMap != null)
        {
            string defaultName = FracturingFog.Models.ColorPalette.GetStaticName(_calculator.ColorMap);
            using var dlg = new SaveFileDialog
            {
                Title = "Export Color Theme",
                Filter = "JSON File (*.json)|*.json",
                DefaultExt = "json",
                FileName = defaultName.Replace(" ", "") + ".json"
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                //var opts = new JsonSerializerOptions { WriteIndented = true };
                string? _s = UserColorThemeLibrary.ExportToJson(_calculator.ColorMap);
                Debug.WriteLine($"Exporting color data: Null: {string.IsNullOrWhiteSpace(_s)} Length: {_s?.Length}");
                File.WriteAllText(dlg.FileName, _s);
                SetStatus($"Exported color theme '{defaultName}'  →  {Path.GetFileName(dlg.FileName)}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed:\n\n{ex.Message}", "Export Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private void OnPosterClick(object? sender, EventArgs e)
    {
        if (_calculator == null)
        {
            MessageBox.Show("No fractal data to save yet.", "Poster",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dlg = new PosterDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        bool isPortrait = dlg.IsPortrait;
        bool rotateImage = dlg.RotateImage;
        int width = int.TryParse(dlg.WidthInput, out var w) ? w : 0;
        int height = int.TryParse(dlg.HeightInput, out var h) ? h : 0;
        if (width <= 0 || height <= 0)
        {
            MessageBox.Show("Width and Height must be positive numbers.", "Save View",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var (maxW, maxH) = ComputeMaxDimensions();
        if (width > maxW || height > maxH)
        {
            long availMB = 0;
            try { availMB = (long)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024) * 0.60); }
            catch { availMB = 512; }
            MessageBox.Show(
                $"Cannot use dimensions {width}×{height}.\n\n" +
                $"Based on available memory (~{availMB} MB usable), the maximum\n" +
                $"safe dimensions on this machine are approximately {maxW}×{maxH}.\n\n" +
                "Try reducing the poster size or closing other applications.",
                "Dimensions Too Large", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string colorName = _calculator.ColorMap?.GetType().GetProperty("Name")?.GetValue(null)?.ToString() ?? "Theme";
        string regionName = "";
        if (!string.IsNullOrEmpty(CurrentRegionName()))
            regionName = CurrentRegionName()?.Replace(" ", "") + "_" ?? "";

        int savedW = isPortrait ? height : width;
        int savedH = isPortrait ? width : height;
        string sizeTag = $"{savedW}x{savedH}_poster";
        string rotatedTag = isPortrait ? "_portrait" : rotateImage ? "_rotated" : "";

        using var saveDlg = new SaveFileDialog
        {
            Title = "Save Poster Image",
            Filter = "PNG Image (*.png)|*.png|TIFF Image (*.tiff;*.tif)|*.tiff;*.tif|BMP Image (*.bmp)|*.bmp",
            FilterIndex = 1,
            DefaultExt = "png",
            FileName = $"{_programName}_{colorName}_{regionName}" +
                         $"x{_txCX.Text.Replace(".", "")}_" +
                         $"y{_txCY.Text.Replace(".", "")}_" +
                         $"z{_txZoom.Text.Replace(".", "")}_" +
                         $"i{_txIter.Text.Replace(".", "")}_" +
                         sizeTag +
                         rotatedTag
        };
        if (saveDlg.ShowDialog(this) != DialogResult.OK) return;

        string path = saveDlg.FileName;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        var format = ext switch { ".bmp" => ImageFormat.Bmp, ".tif" or ".tiff" => ImageFormat.Tiff, _ => ImageFormat.Png };
        string wm = $"{(!string.IsNullOrEmpty(CurrentRegionName()) ? CurrentRegionName() : "Fracturing Fog")}" +
                      $"{(!string.IsNullOrEmpty(CurrentColorMapName()) ? " - " + CurrentColorMapName() : "")}";
        string subText = $"{_programName} v{_programVersion} {DateTime.Now.Year}";

        TakePosterScreenshot(width, height, isPortrait, rotateImage, path, format, wm, subText);

    }

    private void OnFlipClick(object? sender, EventArgs e)
    {
        // Mirror across the real axis on the full DD pair so deep-zoom
        // positions don't drift. The textbox only carries the Hi half via
        // G15, so re-parsing it would drop _centerYLo and lose Hi precision.
        if (_centerY == 0.0 && _centerYLo == 0.0) return;

        _centerY = -_centerY;
        _centerYLo = -_centerYLo;

        var ic = System.Globalization.CultureInfo.InvariantCulture;
        _txCY.Text = _centerY.ToString("G15", ic);

        OnGoClick(sender, e);
    }

    // Compute max safe dimensions from available memory.
    // Each pixel needs 4 bytes (BGRA uint[]), plus the calculator allocates
    // 5 float[] buffers (smooth, distance, nx, ny) and 1 int[] (iterations)
    // = 4 + 5*4 + 4 = 28 bytes per pixel.  We use 60% of available physical
    // memory to leave headroom for the OS and other processes.
    private static (int maxW, int maxH) ComputeMaxDimensions()
    {
        long available;
        try
        {
            var gcInfo = GC.GetGCMemoryInfo();
            available = (long)(gcInfo.TotalAvailableMemoryBytes * 0.60);
        }
        catch
        {
            available = 512L * 1024 * 1024; // 512 MB safe fallback
        }
        const int BytesPerPixel = 28; // see comment above
        long maxPixels = available / BytesPerPixel;
        // Cap each axis: assume square aspect as worst case.
        int maxSide = (int)System.Math.Min(System.Math.Sqrt(maxPixels), 100_000);
        return (maxSide, maxSide);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Initialisation
    // ─────────────────────────────────────────────────────────────────────────

    private void OnLoad(object? sender, EventArgs e)
    {
        FractalRegionLibrary.Instance.Load();
        RebuildRegionCombo();
        int w = _renderPanel.ClientSize.Width;
        int h = _renderPanel.ClientSize.Height;

        try
        {
            //MARGINS margins = new MARGINS { cxLeftWidth = 0, cxRightWidth = 0, cyTopHeight = 30, cyBottomHeight = -1 };
            //_ = DwmExtendFrameIntoClientArea(Handle, ref margins);

            _renderer = RendererFactory.Create(_renderPanel.Handle, w, h, _forceD3D11);
            _calculator = new MandelbrotCalculator(w, h);

            if (_defaultColorMap != null) _calculator.ColorMap = _defaultColorMap;
            _colorThemeCombo.Text = Models.ColorPalette.GetStaticName(_calculator.ColorMap);
            Text = $"{_programName} v{_programVersion}  —  {_renderer.RendererDescription}";
            ApplyViewState();
            TriggerCalculation();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Renderer initialisation failed:\n\n{ex.Message}\n\n" +
                "Ensure your GPU supports Feature Level 10.0+\n" +
                "and Vortice.DirectX 3.8.3 packages are installed.",
                "Initialisation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Application.Exit();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Mini-map
    // ─────────────────────────────────────────────────────────────────────────

    private void ToggleMiniMap()
    {
        if (_miniMapPanel == null)
        {
            _miniMapPanel = new MiniMapPanel();
            _miniMapPanel.Configure(
                getCenter: () => (_centerX, _centerY),
                getZoom: () => _zoom,
                getColorMap: () => _calculator?.ColorMap,
                navigateTo: (cx, cy) =>
                {
                    _centerX = cx; _centerXLo = 0.0; _centerX2 = 0.0; _centerX3 = 0.0;
                    _centerY = cy; _centerYLo = 0.0; _centerY2 = 0.0; _centerY3 = 0.0;
                    ApplyViewState();
                    TriggerCalculation();
                },
                getSwatchColor: GetSwatchColor);

            _miniMapPanel.Left = _renderPanel.ClientSize.Width - _miniMapPanel.Width - 4;
            _miniMapPanel.Top = _renderPanel.ClientSize.Height - _miniMapPanel.Height - 4;
            _miniMapPanel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _renderPanel.Controls.Add(_miniMapPanel);
            _miniMapPanel.BringToFront();
            _miniMapPanel.RequestRedraw();
        }
        else
        {
            _renderPanel.Controls.Remove(_miniMapPanel);
            _miniMapPanel.Dispose();
            _miniMapPanel = null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // System Info dialog
    // ─────────────────────────────────────────────────────────────────────────

    private void ShowSystemInfoDialog()
    {
        var sb = new StringBuilder();

        sb.AppendLine("=== Renderer ===");
        sb.AppendLine($"Active:          {_renderer?.RendererDescription ?? "none"}");
        sb.AppendLine($"D3D12 available: {DirectX12Renderer.IsAvailable()}");
        sb.AppendLine();

        sb.AppendLine("=== GPU Adapters (DXGI) ===");
        try
        {
            using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<Vortice.DXGI.IDXGIFactory1>();
            uint idx = 0;
            while (factory.EnumAdapters1(idx, out var adapter).Success)
            {
                var desc = adapter.Description1;
                sb.AppendLine($"Adapter {idx}: {desc.Description}");
                sb.AppendLine($"  Vendor ID:   0x{desc.VendorId:X4}");
                sb.AppendLine($"  Device ID:   0x{desc.DeviceId:X4}");
                sb.AppendLine($"  Dedicated VRAM: {desc.DedicatedVideoMemory / (1024 * 1024)} MB");
                sb.AppendLine($"  Shared RAM:     {desc.SharedSystemMemory / (1024 * 1024)} MB");
                adapter.Dispose();
                idx++;
            }
        }
        catch (Exception ex) { sb.AppendLine($"  (DXGI enumeration failed: {ex.Message})"); }

        sb.AppendLine();
        sb.AppendLine("=== D3D11 Feature Level ===");
        try
        {
            Vortice.Direct3D11.D3D11.D3D11CreateDevice(
                null,
                Vortice.Direct3D.DriverType.Hardware,
                Vortice.Direct3D11.DeviceCreationFlags.None,
                null,
                out _, out var fl, out _);
            sb.AppendLine($"Max Feature Level: {fl}");
        }
        catch { sb.AppendLine("  (Could not query D3D11 feature level.)"); }

        sb.AppendLine();
        sb.AppendLine("=== CPU / OS ===");
        sb.AppendLine($"Logical CPUs:  {Environment.ProcessorCount}");
        sb.AppendLine($"OS:            {Environment.OSVersion}");
        sb.AppendLine($".NET Runtime:  {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Architecture:  {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");

        sb.AppendLine();
        sb.AppendLine("=== Fractal Calculator ===");
        if (_calculator != null)
        {
            sb.AppendLine($"SIMD vector width (double): {System.Numerics.Vector<double>.Count}");
            sb.AppendLine($"Current size:    {_calculator.Width}×{_calculator.Height}");
            sb.AppendLine($"Max iterations:  {_calculator.MaxIterations}");
            sb.AppendLine($"Precision:       {((_calculator.IsHighPrecisionActive) ? "Double-Double (DD)" : "Double (SP)")}");
        }

        using var dlg = new Form
        {
            Text = "System / Hardware Information",
            ClientSize = new Size(560, 500),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent,
            BackColor = Color.FromArgb(28, 28, 28),
        };
        var txt = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Text = sb.ToString(),
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(18, 18, 18),
            ForeColor = Color.FromArgb(200, 200, 200),
            Font = new Font("Consolas", 9f),
            BorderStyle = BorderStyle.None,
        };
        dlg.Controls.Add(txt);
        dlg.ShowDialog(this);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Quality
    // ─────────────────────────────────────────────────────────────────────────

    private void OnQualityComboChanged(object? sender, EventArgs e)
    {
        if (sender == null) return;

        ComboBox combo = (ComboBox)sender;
        int idx = combo.SelectedIndex;
        if (idx < 0 || idx >= QualityPreset.All.Length) return;

        QualityPreset newQuality = QualityPreset.All[idx];
        _quality = newQuality;

        double prevZoom = _zoom;
        _zoom = System.Math.Clamp(_zoom, _quality.ZoomMin, _quality.ZoomMax);

        if (_calculator != null)
            _calculator.Quality = _quality;

        ApplyViewState();
        TriggerCalculation();

        if (prevZoom > _quality.ZoomMax)
            SetStatus($"Quality → {_quality.Name}.  Zoom clamped to {_quality.ZoomMax:G3}.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Colour theme
    // ─────────────────────────────────────────────────────────────────────────

    private void BuildColorThemesSelection()
    {
        _colorThemeCombo.Items.Clear();
        foreach (var type in Enum.GetValues<ColorPaletteType>())
        {
            var palettes = Models.ColorPalette.GetPalettesByType(type);
            if (palettes.Count == 0) continue;
            _colorThemeCombo.Items.Add($"— {type} —");
            _colorThemeCombo2?.Items.Add($"— {type} —");
            foreach (var name in palettes.ToImmutableSortedDictionary().Keys)
            {
                _colorThemeCombo.Items.Add(name);
                _colorThemeCombo2?.Items.Add(name);
            }
        }
    }

    private void OnColorThemeChanged(object? sender, EventArgs e)
    {
        string name = _colorThemeCombo?.SelectedItem?.ToString() ?? "";
        _currentColorThemeLabel?.Text = name;
        var map = Models.ColorPalette.GetPaletteByName(name);
        if (_calculator != null)
        {
            _calculator.ColorMap = map;
            TriggerCalculation();
        }
        _miniMapPanel?.RequestRedraw();
        UpdateDeleteColorThemeButton();
    }

    private Color GetSwatchColor()
    {
        if (_calculator?.ColorMap == null) return Color.White;
        _calculator.ColorMap.MaxIterations = 500;
        int argb = _calculator.ColorMap.SwatchSample;
        return Color.FromArgb((argb >> 16) & 0xFF, (argb >> 8) & 0xFF, argb & 0xFF);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ComputeContrastColor
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a colour that contrasts well against <paramref name="swatch"/>.
    /// When <paramref name="watermark"/> is true and a pixel buffer is supplied,
    /// the method samples the lower-right region of the image (where the
    /// watermark will be placed) instead of using the swatch, yielding a colour
    /// that is always readable against the actual rendered content.
    /// </summary>
    private static Color ComputeContrastColor(
        Color swatch,
        bool watermark = false,
        uint[]? pixels = null,
        int imgW = 0,
        int imgH = 0)
    {
        Color baseColor = swatch;

        // When in watermark mode and we have pixel data, sample the region
        // where the watermark text will land (lower-right corner).
        if (watermark && pixels != null && imgW > 0 && imgH > 0)
        {
            // The watermark main line uses 16px bold; estimate ~300×22 px.
            // The sub-line uses 8px bold; estimate ~300×12 px.
            // Together the bounding box is roughly 320×42 px ending at
            // (imgW-2, imgH-2) (from AddWaterMark positioning).
            const int regionW = 320;
            const int regionH = 46;
            int x0 = Math.Max(0, imgW - regionW - 20);
            int y0 = Math.Max(0, imgH - regionH - 2);
            int x1 = Math.Min(imgW, imgW);
            int y1 = Math.Min(imgH, imgH);

            long sumR = 0, sumG = 0, sumB = 0, count = 0;
            for (int row = y0; row < y1; row++)
            {
                int rb = row * imgW;
                for (int col = x0; col < x1; col++)
                {
                    uint p = pixels[rb + col];
                    sumR += (p >> 16) & 0xFF;
                    sumG += (p >> 8) & 0xFF;
                    sumB += p & 0xFF;
                    count++;
                }
            }

            if (count > 0)
                baseColor = Color.FromArgb(
                    (int)(sumR / count),
                    (int)(sumG / count),
                    (int)(sumB / count));
        }

        // Compute complementary + luminance-adjusted colour.
        float r = baseColor.R / 255f, g = baseColor.G / 255f, b = baseColor.B / 255f;
        float cmax = System.Math.Max(r, System.Math.Max(g, b));
        float cmin = System.Math.Min(r, System.Math.Min(g, b));
        float delta = cmax - cmin;
        float l = (cmax + cmin) * 0.5f;
        float h2 = 0f;
        if (delta > 0.001f)
        {
            if (cmax == r) h2 = ((g - b) / delta) % 6f;
            else if (cmax == g) h2 = (b - r) / delta + 2f;
            else h2 = (r - g) / delta + 4f;
            h2 = (h2 / 6f + 1f) % 1f;
        }
        float s2 = delta < 0.001f ? 0f : delta / (1f - System.Math.Abs(2f * l - 1f));
        float hc = (h2 + 0.5f) % 1f;
        float lc = l < 0.5f
            ? System.Math.Clamp(1f - l * 0.6f, 0.65f, 1.0f)
            : System.Math.Clamp(1f - l * 1.4f, 0.0f, 0.35f);
        float sc = System.Math.Clamp(s2 * 0.5f + 0.5f, 0.5f, 1.0f);
        float cv = (1f - System.Math.Abs(2f * lc - 1f)) * sc;
        float xv = cv * (1f - System.Math.Abs((hc * 6f) % 2f - 1f));
        float m = lc - cv * 0.5f;
        float rr, gg, bb;
        switch ((int)(hc * 6f))
        {
            case 0: rr = cv; gg = xv; bb = 0; break;
            case 1: rr = xv; gg = cv; bb = 0; break;
            case 2: rr = 0; gg = cv; bb = xv; break;
            case 3: rr = 0; gg = xv; bb = cv; break;
            case 4: rr = xv; gg = 0; bb = cv; break;
            default: rr = cv; gg = 0; bb = xv; break;
        }
        // Watermark mode is always fully opaque; fade flag kept for non-watermark uses.
        int alpha = watermark ? 205 : 255;
        return Color.FromArgb(
            alpha,
            (int)System.Math.Clamp((rr + m) * 255f, 0, 255),
            (int)System.Math.Clamp((gg + m) * 255f, 0, 255),
            (int)System.Math.Clamp((bb + m) * 255f, 0, 255));
    }

    // Backward-compatible overload used by GridOverlayPanel (no pixel sampling).
    private static Color ComputeContrastColorSimple(Color swatch, bool fade = false)
    {
        var c = ComputeContrastColor(swatch);
        return fade ? Color.FromArgb(75, c.R, c.G, c.B) : c;
    }
    // ─────────────────────────────────────────────────────────────────────────
    // Reset
    // ─────────────────────────────────────────────────────────────────────────

    private void OnResetClick(object? sender, EventArgs e)
    {
        StopSlideshow();
        _centerX = DefaultCenterX; _centerXLo = 0.0; _centerX2 = 0.0; _centerX3 = 0.0;
        _centerY = DefaultCenterY; _centerYLo = 0.0; _centerY2 = 0.0; _centerY3 = 0.0;
        _zoom = DefaultZoom;
        _regionCombo.SelectedIndex = 0;

        // Reset brightness and contrast to defaults.
        _brightness = 0;
        _contrast = 0;
        if (_brightnessSlider != null) _brightnessSlider.Value = 0;
        if (_contrastSlider != null) _contrastSlider.Value = 0;
        if (_brightnessLabel != null) _brightnessLabel.Text = "Brightness: 0";
        if (_contrastLabel != null) _contrastLabel.Text = "Contrast: 0";

        ApplyViewState();
        TriggerCalculation();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Iteration lock
    // ─────────────────────────────────────────────────────────────────────────

    private void OnIterLockChanged(object? sender, EventArgs e)
    {
        _iterLocked = _chkLockIter.Checked;
        if (_iterLocked && _calculator != null)
        {
            // Capture current iteration value when lock is engaged.
            if (int.TryParse(_txIter.Text.Trim(), out int parsed) && parsed >= 64)
                _lockedIterations = parsed;
            else
                _lockedIterations = _calculator.MaxIterations;
            _txIter.BackColor = Color.FromArgb(55, 50, 30);   // tinted to show locked state
        }
        else
        {
            _txIter.BackColor = Color.FromArgb(40, 40, 40);    // restore normal colour
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Coordinate entry — "Go"
    // ─────────────────────────────────────────────────────────────────────────

    private void OnGoClick(object? sender, EventArgs e)
    {
        if (!TryParseCoords(out double cx, out double cy, out double zoom, out int iter))
        {
            MessageBox.Show(
                "One or more values are invalid.\n\n" +
                "CX / CY: decimal numbers  (e.g. -0.7435669)\n" +
                "Zoom: positive number  (e.g. 400)\n" +
                "Iterations: integer ≥ 64",
                "Invalid Coordinates",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // The CX/CY textboxes display G15 of the Hi half of a DD pair and
        // can't represent _centerXLo / _centerYLo. Only stomp the DD pair
        // when the user actually edited the displayed text — otherwise
        // pressing Go after panning at deep zoom would shift the location.
        var ic = System.Globalization.CultureInfo.InvariantCulture;
        if (_txCX.Text.Trim() != _centerX.ToString("G15", ic))
        {
            _centerX = cx; _centerXLo = 0.0; _centerX2 = 0.0; _centerX3 = 0.0;
        }
        if (_txCY.Text.Trim() != _centerY.ToString("G15", ic))
        {
            _centerY = cy; _centerYLo = 0.0; _centerY2 = 0.0; _centerY3 = 0.0;
        }
        _zoom = System.Math.Clamp(zoom, _quality.ZoomMin, _quality.ZoomMax);

        if (_calculator != null && iter > 0)
            _calculator.MaxIterations = iter;

        // When "Go" is pressed while locked, update the locked value too.
        if (_iterLocked)
            _lockedIterations = iter;

        ApplyViewState(iter);
        TriggerCalculation();
    }

    private bool TryParseCoords(out double cx, out double cy,
                                 out double zoom, out int iter)
    {
        cx = _centerX;
        cy = _centerY;
        zoom = _zoom;
        iter = _calculator?.MaxIterations ?? 512;

        var ic = System.Globalization.CultureInfo.InvariantCulture;
        var ns = System.Globalization.NumberStyles.Float;

        // Upper limit removed — only minimum of 64 enforced.
        return double.TryParse(_txCX.Text.Trim(), ns, ic, out cx)
            && double.TryParse(_txCY.Text.Trim(), ns, ic, out cy)
            && double.TryParse(_txZoom.Text.Trim(), ns, ic, out zoom) && zoom > 0
            && int.TryParse(_txIter.Text.Trim(), out iter) && iter >= 64;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // REGION MANAGEMENT
    // ─────────────────────────────────────────────────────────────────────────

    private void RebuildRegionCombo()
    {
        _regionCombo.SelectedIndexChanged -= OnRegionComboChanged;
        _regionCombo.Items.Clear();
        _regionCombo2?.Items.Clear();
        _regionCombo.Items.Add("— select region —");
        _regionCombo2?.Items.Add("— select region —");
        var regions = FractalRegionLibrary.Instance.All.OrderBy(r => r.IsBuiltIn).ThenBy(r => r.Name);
        foreach (var r in regions)
        {
            _regionCombo.Items.Add(r.Name);
            _regionCombo2?.Items.Add(r.Name);
        }

        _regionCombo.SelectedIndex = 0;
        _regionCombo2?.SelectedIndex = 0;
        _regionCombo.SelectedIndexChanged += OnRegionComboChanged;
        UpdateDelRegionButton();
    }

    private void OnRegionComboChanged(object? sender, EventArgs e)
    {
        UpdateDelRegionButton();

        string? name = _regionCombo.SelectedItem?.ToString();
        _currentRegionLabel?.Text = name;
        if (string.IsNullOrEmpty(name) || name == "— select region —") return;

        var region = FractalRegionLibrary.Instance.FindByName(name);
        if (region == null) return;

        ApplyRegion(region);
        TriggerCalculation();

        _toolTip.SetToolTip(_regionCombo, region.Description);
    }

    /// <summary>Applies a FractalRegion to the view state, respecting the iteration lock.</summary>
    private void ApplyRegion(FractalRegion region)
    {
        // Round-trip all four QD limbs. Legacy regions (DD or shallower) default
        // X2/X3 to 0, matching prior behaviour.
        _centerX = region.CenterX; _centerXLo = region.CenterXLo;
        _centerX2 = region.CenterX2; _centerX3 = region.CenterX3;
        _centerY = region.CenterY; _centerYLo = region.CenterYLo;
        _centerY2 = region.CenterY2; _centerY3 = region.CenterY3;
        _quality = region.QualityPreset;

        _qualityCombo.SelectedIndexChanged -= OnQualityComboChanged;
        _qualityCombo.Text = region.QualityPresetName;
        _qualityCombo.SelectedIndexChanged += OnQualityComboChanged;

        _zoom = System.Math.Clamp(region.Zoom, _quality.ZoomMin, _quality.ZoomMax);

        if (_calculator != null)
        {
            _calculator.Quality = region.QualityPreset;
            if (!_iterLocked && region.Iterations > 0)
                _calculator.MaxIterations = region.Iterations;
            else if (_iterLocked)
                _calculator.MaxIterations = _lockedIterations;
        }

        ApplyViewState(_iterLocked ? _lockedIterations : (region.Iterations > 0 ? region.Iterations : 0));
    }

    private void OnSaveViewClick(object? sender, EventArgs e)
    {
        using var dlg = new InputDialog("Save Current View", "Region name:");
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        string name = dlg.Input.Trim();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show("Name cannot be empty.", "Save View",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var region = new FractalRegion
        {
            Name = name,
            CenterX = _centerX,
            CenterXLo = _centerXLo,
            CenterX2 = _centerX2,
            CenterX3 = _centerX3,
            CenterY = _centerY,
            CenterYLo = _centerYLo,
            CenterY2 = _centerY2,
            CenterY3 = _centerY3,
            Zoom = _zoom,
            Iterations = _calculator?.MaxIterations ?? 512,
            QualityPresetName = _quality.Name,
            Description = $"Saved {DateTime.Now:yyyy-MM-dd HH:mm}"
        };

        if (!FractalRegionLibrary.Instance.AddUserRegion(region))
        {
            MessageBox.Show($"\"{name}\" already exists.", "Duplicate Name",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        RebuildRegionCombo();
        for (int i = 0; i < _regionCombo.Items.Count; i++)
            if (_regionCombo.Items[i]?.ToString() == name) { _regionCombo.SelectedIndex = i; break; }

        SetStatus($"Region \"{name}\" saved.");
    }

    private void OnDelRegionClick(object? sender, EventArgs e)
    {
        string? name = _regionCombo.SelectedItem?.ToString();
        if (string.IsNullOrEmpty(name) || name == "— select region —") return;

        var region = FractalRegionLibrary.Instance.FindByName(name);
        if (region == null || region.IsBuiltIn) return;

        if (MessageBox.Show($"Delete region \"{name}\"?", "Confirm Delete",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        FractalRegionLibrary.Instance.RemoveUserRegion(name);
        RebuildRegionCombo();
        SetStatus($"Region \"{name}\" deleted.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // REGION EXPORT
    // ─────────────────────────────────────────────────────────────────────────

    private void OnExportRegionsClick(object? sender, EventArgs e)
    {
        var userRegions = FractalRegionLibrary.Instance.UserRegions;
        if (userRegions.Count == 0)
        {
            MessageBox.Show("There are no custom regions to export.\n\nUse \"Save View\" to create one first.",
                "Export Regions", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string defaultName = "regions";
        string? selected = _regionCombo.SelectedItem?.ToString();
        if (!string.IsNullOrEmpty(selected) && selected != "— select region —")
        {
            var sel = FractalRegionLibrary.Instance.FindByName(selected);
            if (sel != null && !sel.IsBuiltIn) defaultName = selected;
        }

        using var dlg = new SaveFileDialog
        {
            Title = "Export Custom Regions",
            Filter = "JSON File (*.json)|*.json",
            DefaultExt = "json",
            FileName = defaultName + ".json"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(userRegions, opts));
            SetStatus($"Exported {userRegions.Count} region(s)  →  {Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Export failed:\n\n{ex.Message}", "Export Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // REGION IMPORT
    // ─────────────────────────────────────────────────────────────────────────

    private void OnImportRegionsClick(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Import Custom Regions",
            Filter = "JSON File (*.json)|*.json|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        List<FractalRegion>? imported;
        try
        {
            imported = JsonSerializer.Deserialize<List<FractalRegion>>(File.ReadAllText(dlg.FileName));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not read or parse the file:\n\n{ex.Message}",
                "Import Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (imported == null || imported.Count == 0)
        {
            MessageBox.Show("The file contains no region entries.",
                "Import Regions", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        int added = 0, renamed = 0;
        foreach (var region in imported)
        {
            if (string.IsNullOrWhiteSpace(region.Name)) continue;
            region.RegionType = RegionType.UserDefined;

            string candidate = region.Name;
            if (FractalRegionLibrary.Instance.FindByName(candidate) != null) continue;
            // Skip duplicates to avoid overwriting existing regions.
            // Alternative would be to auto-rename with a suffix, but that could lead to many confusingly similar entries if the same file is imported multiple times.
            //{
            //candidate = region.Name + "-imp";
            //int suffix = 2;
            //while (FractalRegionLibrary.Instance.FindByName(candidate) != null)
            //    candidate = region.Name + "-imp-" + suffix++;
            //region.Name = candidate;
            //renamed++;
            //}

            FractalRegionLibrary.Instance.UserRegions.Add(region);
            added++;
        }

        if (added == 0)
        {
            MessageBox.Show("No valid regions found.", "Import Regions",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        FractalRegionLibrary.Instance.Save();
        RebuildRegionCombo();

        string summary = added == 1 ? "1 region imported" : $"{added} regions imported";
        if (renamed > 0) summary += $" ({renamed} renamed with '-imp')";
        SetStatus(summary + $"  ←  {Path.GetFileName(dlg.FileName)}");
    }

    private void UpdateDelRegionButton()
    {
        string? name = _regionCombo.SelectedItem?.ToString();
        if (string.IsNullOrEmpty(name) || name == "— select region —")
        { _delRegionButton.Enabled = false; return; }
        var region = FractalRegionLibrary.Instance.FindByName(name);
        _delRegionButton.Enabled = region != null && !region.IsBuiltIn;
    }

    private void UpdateDeleteColorThemeButton()
    {
        string? name = _colorThemeCombo.SelectedItem?.ToString();
        if (string.IsNullOrEmpty(name) || name.StartsWith("—"))
        { _deleteColorThemeButton.Enabled = false; return; }

        _deleteColorThemeButton.Enabled = UserColorThemeLibrary.Instance.Themes
            .Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SLIDESHOW (NEW)
    // ─────────────────────────────────────────────────────────────────────────
    //
    // Timing
    //   • Each region is shown for 30 s total.
    //   • Within each region the colour theme changes every 10 s.
    //   • Theme and region transitions use a 2-second CPU cross-fade:
    //     both the outgoing and incoming colour buffers are rendered and
    //     blended pixel-by-pixel over 20 frames at ~100 ms each.
    //
    // Cross-fade implementation
    //   Since the frame is ultimately a uint[] BGRA buffer delivered to
    //   DirectXRenderer.UpdateTexture, a per-pixel lerp between old and new
    //   buffers is trivially achievable on the CPU without any GPU blend state
    //   changes.  The calculator always runs on a background thread; the fade
    //   itself is done on the background thread too and the blended frames are
    //   posted to the UI via Invoke.

    private void OnSlideshowClick(object? sender, EventArgs e)
    {
        if (_slideshowRunning)
            StopSlideshow();
        else
            StartSlideshow();
    }

    private void StartSlideshow()
    {
        if (_slideshowRunning) return;
        _slideshowRunning = true;
        _showSlideshowWatermark = false;
        RepaintWithBrightnessContrast();
        _slideshowButton.Text = "■ Stop";
        _slideshowButton.BackColor = Color.FromArgb(70, 30, 30);
        _slideshowButton.FlatAppearance.BorderColor = Color.FromArgb(120, 50, 50);
        SetStatus("Slideshow running…");

        CancellationTokenSource cts;
        lock (_slideshowLock)
        {
            _slideshowCts?.Cancel();
            _slideshowCts = new CancellationTokenSource();
            cts = _slideshowCts;
        }

        Task.Run(() => SlideshowLoop(cts.Token, () => IsSlideshowRegionLocked(), () => IsSkipSlideshowRegion()), cts.Token)
            .ContinueWith(t =>
            {
                if (!IsHandleCreated || _disposed) return;
                Invoke(() =>
                {
                    _slideshowRunning = false;
                    _showSlideshowWatermark = false;
                    RepaintWithBrightnessContrast();
                    _slideshowButton.Text = "Slideshow";
                    _slideshowButton.BackColor = Color.FromArgb(40, 55, 40);
                    _slideshowButton.FlatAppearance.BorderColor = Color.FromArgb(60, 100, 60);
                    if (!t.IsCanceled && t.IsFaulted)
                        SetStatus($"Slideshow error: {t.Exception?.InnerException?.Message}");
                    else
                        SetStatus("Slideshow stopped.");
                });
            }, TaskScheduler.Default);
    }

    private void StopSlideshow()
    {
        lock (_slideshowLock) _slideshowCts?.Cancel();
    }

    private void ToggleSlideshowRegionLock()
    {
        _slideShowLockRegion = !_slideShowLockRegion;
    }

    public bool IsSlideshowRegionLocked()
    {
        lock (_slideshowLock) return _slideShowLockRegion;
    }

    private void SkipSlideshowRegion()
    {
        _slideshowSkipRegion = true;
        lock (_slideshowLock)
            SetStatus("Slideshow: skipping to next region…");
    }

    public bool IsSkipSlideshowRegion()
    {
        lock (_slideshowLock)
        {
            if (_slideshowSkipRegion)
            {
                _slideshowSkipRegion = false;
                return true;
            }
            return false;
        }
    }

    // Returns all palettes whose Name does not start with "— " (i.e. header items excluded).
    private List<string> GetAllPaletteNames()
    {
        var names = new List<string>();
        foreach (var item in _colorThemeCombo.Items)
        {
            string s = item?.ToString() ?? "";
            if (!s.StartsWith("—")) names.Add(s);
        }
        return names;
    }

    private async Task SlideshowLoop(CancellationToken ct, Func<bool> regionLockFunc, Func<bool> skipRegionFunc)
    {
        var builtIns = new List<FractalRegion>(FractalRegionLibrary.Instance.AllSlideshowRegions);
        var paletteNames = GetAllPaletteNames();
        if (builtIns.Count == 0 || paletteNames.Count == 0) return;

        // Timing design:
        //   Each region shows exactly 3 colour themes.
        //   Each theme is visible for themeDurationMs, then a fadeDurationMs cross-fade
        //   transitions to the next theme (or the next region after the 3rd theme).
        //   The fade is counted as part of the *outgoing* theme's slot, so the
        //   incoming theme gets its full themeDurationMs of uninterrupted display.
        const int themesPerRegion = 3;
        const int themeDurationMs = 12_000;   // 12 s fully visible per theme
        const int fadeDurationMs = 2_000;   // 2 s cross-fade (overlaps end of theme slot)
        const int fadeSteps = 22;
        const int fadeStepMs = fadeDurationMs / fadeSteps;
        int lastRegionIdx = -1;
        int lastThemeIdx = -1;
        int renderCounter = 0;
        int regionIdx = -1;
        bool[] regionsUsed = new bool[builtIns.Count];
        FractalRegion? lockedRegion = null;

        while (!ct.IsCancellationRequested)
        {
            FractalRegion region;
            if (regionLockFunc() && lockedRegion != null)
            {
                region = lockedRegion;
            }
            else
            {
                // ── Pick a new region different from the last ─────────────────────
                do { regionIdx = _slideshowRng.Next(builtIns.Count); }
                while (builtIns.Count > 1 && regionIdx == lastRegionIdx);
                lastRegionIdx = regionIdx;
                if (regionsUsed[regionIdx]) continue;
                region = builtIns[regionIdx];
                renderCounter = 0;   // reset theme counter when moving to a new region
            }

            lockedRegion = region;

            string lockStatus = regionLockFunc() ? "(L)" : "";
            // Mark the just-used region to avoid immediate repeats until all have been shown.
            if (!regionLockFunc())
            {
                regionsUsed[regionIdx] = true;
                if (regionsUsed.All(u => u)) Array.Clear(regionsUsed, 0, regionsUsed.Length);
            }

            // ── Pick an initial theme ─────────────────────────────────────────
            int themeIdx;
            do { themeIdx = _slideshowRng.Next(paletteNames.Count); }
            while (paletteNames.Count > 1 && themeIdx == lastThemeIdx);
            lastThemeIdx = themeIdx;
            string themeName = paletteNames[themeIdx];

            // ── Render the new region with the initial theme ───────────────────
            uint[]? previousBuffer = null;
            if (_calculator != null && _renderer != null &&
                (!regionLockFunc() || renderCounter < 1))
            {
                if (!regionLockFunc()) renderCounter = 0;   // reset counter when moving to a new region, if not locking
                // ── FIX: snapshot the current on-screen buffer NOW, before any
                //    region/theme state is changed.  _lastUploadedBuffer always
                //    holds the most-recently-uploaded post-processed frame and is
                //    updated on the UI thread, so reading it here (still on the
                //    background slideshow task, but before any Invoke) is safe
                //    because we copy it under no concurrent mutation.
                uint[] oldBuf;
                lock (_calcLock)   // brief lock to avoid racing with TriggerCalculation
                {
                    if (_lastUploadedBuffer != null
                        && _calculator != null
                        && _lastUploadedWidth == _calculator.Width
                        && _lastUploadedHeight == _calculator.Height)
                    {
                        Debug.WriteLine($"SldShwLp: Capturing old buffer for cross-fade. " +
                        $"Last uploaded buffer: Length: {_lastUploadedBuffer.Length} pixels, size {_lastUploadedWidth}×{_lastUploadedHeight}");
                        oldBuf = new uint[_lastUploadedBuffer.Length];
                        _lastUploadedBuffer.CopyTo(oldBuf, 0);
                    }
                    else if (_calculator != null)
                    {
                        Debug.WriteLine($"SldShwLp: Falling back to direct ColorBuffer copy of {_calculator.ColorBuffer.Length} pixels");
                        oldBuf = new uint[_calculator.ColorBuffer.Length];
                        _calculator.ColorBuffer.CopyTo(oldBuf, 0);
                    }
                    else
                    {
                        oldBuf = Array.Empty<uint>();
                    }
                }

                // Apply region & theme on UI thread WITHOUT triggering a
                // normal TriggerCalculation — we manage rendering ourselves.
                if (ct.IsCancellationRequested) return;
                await InvokeAsync(() =>
                {
                    if (_disposed) return;
                    _slideshowRegionName = region.Name;
                    ApplyRegionSilent(region);
                    //var map = Models.ColorPalette.GetPaletteByName(themeName);
                    //if (_calculator != null) _calculator.ColorMap = map;
                    SuppressedSetRegionCombo(region.Name);
                    ApplyColorThemeSilent(themeName);
                    SetStatus($"Slideshow: {region.Name} {lockStatus}  •  {themeName}");
                });

                // Calculate on background thread.
                if (ct.IsCancellationRequested) return;
                uint[] newBuf = await Task.Run(() =>
                {
                    if (_calculator == null) return Array.Empty<uint>();
                    Debug.WriteLine($"SldShwLp: Starting calculation for new region/theme. " +
                        $"Calculator state: {_calculator.Width}×{_calculator.Height}, MaxIterations: {_calculator.MaxIterations}, " +
                        $"Precision: {(_calculator.IsHighPrecisionActive ? "DD" : "SP")}");
                    _calculator.Calculate(ct);
                    var copy = new uint[_calculator.ColorBuffer.Length];
                    _calculator.ColorBuffer.CopyTo(copy, 0);
                    return copy;
                }, ct);

                if (ct.IsCancellationRequested) return;

                // Cross-fade between the captured on-screen frame and the new render.
                if (oldBuf.Length == newBuf.Length && oldBuf.Length > 0)
                {
                    await CrossFade(oldBuf, newBuf, fadeSteps, fadeStepMs, ct);
                }
                else
                {
                    await InvokeAsync(() =>
                    {
                        if (!_disposed && _renderer != null && _calculator != null)
                            _renderer.UpdateTexture(newBuf, _calculator.Width, _calculator.Height);
                    });
                }

                previousBuffer = newBuf;
                renderCounter += regionLockFunc() ? 1 : 0;
            }

            // ── Run exactly (themesPerRegion - 1) additional theme changes ────
            // The first theme was shown above; now show 2 more for a total of 3.
            int themesCount = regionLockFunc() ? paletteNames.Count : themesPerRegion;
            for (int themeNum = 1; themeNum < themesCount && !ct.IsCancellationRequested; themeNum++)
            {
                Debug.WriteLine($"SldShwLp: Theme {themeNum + 1} of {themesPerRegion} for region \"{region.Name}\" starting in {themeDurationMs} ms");
                // Wait for the full theme display duration before starting the next fade.
                await DelayWithCancel(themeDurationMs, ct);
                if (ct.IsCancellationRequested) return;
                lockStatus = regionLockFunc() ? "(L)" : "";
                // Pick next theme.

                int newThemeIdx;
                do { newThemeIdx = _slideshowRng.Next(paletteNames.Count); }
                while (paletteNames.Count > 1 && newThemeIdx == lastThemeIdx);
                lastThemeIdx = newThemeIdx;
                string newThemeName = paletteNames[newThemeIdx];

                if (_calculator == null || _renderer == null) break;

                uint[] oldThemeBuf = previousBuffer ?? Array.Empty<uint>();

                // Apply new theme silently — no TriggerCalculation.
                Debug.WriteLine($"Pre await invoke: Applying new theme \"{newThemeName}\"");
                await InvokeAsync(() =>
                {
                    if (_disposed) return;
                    ApplyColorThemeSilent(newThemeName);
                    SetStatus($"Slideshow: {region.Name}{lockStatus}  •  {newThemeName}");
                });

                if (ct.IsCancellationRequested) return;

                Debug.WriteLine($"Post await invoke: Starting calculation for new theme \"{newThemeName}\"");
                uint[] newThemeBuf = await Task.Run(() =>
                {
                    if (_calculator == null) return Array.Empty<uint>();
                    Debug.WriteLine($"SldShwLp: Calculating new theme \"{newThemeName}\" for region \"{region.Name}\"");
                    _calculator.Calculate(ct);
                    var copy = new uint[_calculator.ColorBuffer.Length];
                    _calculator.ColorBuffer.CopyTo(copy, 0);
                    return copy;
                }, ct);

                if (ct.IsCancellationRequested) return;
                if (skipRegionFunc()) break;  // move to next region if skip requested

                if (oldThemeBuf.Length == newThemeBuf.Length && oldThemeBuf.Length > 0)
                {
                    Debug.WriteLine($"SldShwLp: Starting theme cross-fade between buffers of {oldThemeBuf.Length} pixels");
                    await CrossFade(oldThemeBuf, newThemeBuf, fadeSteps, fadeStepMs, ct);
                }
                else
                {
                    Debug.WriteLine($"SldShwLp: Theme buffer size mismatch or empty (old: {oldThemeBuf.Length}, new: {newThemeBuf.Length}), skipping cross-fade");
                    await InvokeAsync(() =>
                    {
                        if (!_disposed && _renderer != null && _calculator != null)
                            _renderer.UpdateTexture(newThemeBuf, _calculator.Width, _calculator.Height);
                    });
                }

                previousBuffer = newThemeBuf;
                Debug.WriteLine($"Region lock: {regionLockFunc()}, theme {themeNum + 1} of {themesCount} for region \"{region.Name}\" displayed");
                if (!regionLockFunc() && themeNum >= themesPerRegion) break;  // move to next region if not locking; otherwise show all themes for this region before moving on
                else if (regionLockFunc() && themeNum >= themesPerRegion) themesCount = paletteNames.Count - themeNum;  // if locking and we've shown the preset number of themes, switch to showing all themes for the rest of the slideshow loop
            }

            // Wait for the final theme to display its full duration before
            // transitioning to the next region.
            Debug.WriteLine($"SldShwLp: Final theme for region \"{region.Name}\" displayed, waiting {themeDurationMs} ms before next region");
            await DelayWithCancel(themeDurationMs, ct);
            Debug.WriteLine($"SldShwLp: Theme duration complete for region \"{region.Name}\"");
            _lastUploadedBuffer = previousBuffer;
            Debug.WriteLine($"Region lock: {regionLockFunc()}, completed region \"{region.Name}\" with final theme displayed for full duration");
            lastRegionIdx = regionLockFunc() ? -1 : regionIdx;
        }
    }

    /// <summary>
    /// Applies a region to the calculator state without triggering a render
    /// (used by the slideshow, which manages rendering explicitly).
    /// </summary>
    private void ApplyRegionSilent(FractalRegion region)
    {
        _centerX = region.CenterX; _centerXLo = region.CenterXLo;
        _centerX2 = region.CenterX2; _centerX3 = region.CenterX3;
        _centerY = region.CenterY; _centerYLo = region.CenterYLo;
        _centerY2 = region.CenterY2; _centerY3 = region.CenterY3;
        _quality = region.QualityPreset;
        _qualityCombo.SelectedIndexChanged -= OnQualityComboChanged;
        //_qualityCombo.Text = region.QualityPresetName;
        _zoom = System.Math.Clamp(region.Zoom, _quality.ZoomMin, _quality.ZoomMax);

        if (_calculator != null)
        {
            _calculator.CenterX = _centerX;
            _calculator.CenterXLo = _centerXLo;
            _calculator.CenterX2 = _centerX2;
            _calculator.CenterX3 = _centerX3;
            _calculator.CenterY = _centerY;
            _calculator.CenterYLo = _centerYLo;
            _calculator.CenterY2 = _centerY2;
            _calculator.CenterY3 = _centerY3;
            _calculator.Zoom = _zoom;
            _calculator.Quality = region.QualityPreset;
            if (!_iterLocked && region.Iterations > 0)
                _calculator.MaxIterations = region.Iterations;
            else if (_iterLocked)
                _calculator.MaxIterations = _lockedIterations;
        }
        UpdateCoordBoxes();
        _qualityCombo.SelectedIndexChanged += OnQualityComboChanged;
    }

    private void ApplyColorThemeSilent(string themeName)
    {
        _colorThemeCombo.SelectedIndexChanged -= OnColorThemeChanged;
        try
        {
            var map = Models.ColorPalette.GetPaletteByName(themeName);
            _calculator?.ColorMap = map;
            _colorThemeCombo.Text = themeName;
        }
        finally
        {
            _colorThemeCombo.SelectedIndexChanged += OnColorThemeChanged;
        }
    }

    /// <summary>
    /// Sets the region combo to the named entry without firing
    /// <see cref="OnRegionComboChanged"/> (which would call TriggerCalculation).
    /// </summary>
    private void SuppressedSetRegionCombo(string name)
    {
        _regionCombo.SelectedIndexChanged -= OnRegionComboChanged;
        try
        {
            for (int i = 0; i < _regionCombo.Items.Count; i++)
                if (_regionCombo.Items[i]?.ToString() == name)
                { _regionCombo.SelectedIndex = i; break; }
        }
        finally
        {
            UpdateDelRegionButton();
            _regionCombo.SelectedIndexChanged += OnRegionComboChanged;
        }
    }

    /// <summary>
    /// Cross-fades two BGRA uint[] buffers by posting <paramref name="steps"/>
    /// blended frames to the renderer.  Each frame alpha-blends the buffers by
    /// an incrementing weight and posts the result to the UI thread.
    /// </summary>
    private async Task CrossFade(uint[] from, uint[] to, int steps, int stepMs, CancellationToken ct)
    {
        int len = System.Math.Min(from.Length, to.Length);
        var blended = new uint[len];
        int w = _calculator?.Width ?? 0;
        int h = _calculator?.Height ?? 0;
        if (w == 0 || h == 0 || w * h != len) return;

        for (int step = 1; step <= steps; step++)
        {
            if (ct.IsCancellationRequested) return;
            float alpha = step / (float)steps;

            // CPU pixel-blend — runs on the calling background thread.
            BlendBuffers(from, to, blended, len, alpha);
            if (_showSlideshowWatermark) BlendWatermarkOverlay(blended, w, h);

            //// Re-apply watermark on every fade frame so it never disappears
            //// during transitions (both region and theme cross-fades).
            //if (_showSlideshowWatermark)
            //        BlendWatermarkOverlay(blended, w, h);

            // Take a snapshot for the upload so we're not mutating blended
            // on the background thread while the UI thread may be reading it.
            var frame = new uint[len];
            Array.Copy(blended, frame, len);
            await InvokeAsync(() =>
            {
                if (!_disposed && _renderer != null)
                    _renderer.UpdateTexture(frame, w, h);
            });

            await DelayWithCancel(stepMs, ct);
        }
    }

    /// <summary>Per-pixel linear blend between two BGRA uint[] buffers.</summary>
    private static void BlendBuffers(uint[] from, uint[] to, uint[] result, int len, float alpha)
    {
        float beta = 1f - alpha;
        for (int i = 0; i < len; i++)
        {
            uint pF = from[i], pT = to[i];
            byte bF = (byte)(pF & 0xFF);
            byte gF = (byte)(pF >> 8 & 0xFF);
            byte rF = (byte)(pF >> 16 & 0xFF);
            byte bT = (byte)(pT & 0xFF);
            byte gT = (byte)(pT >> 8 & 0xFF);
            byte rT = (byte)(pT >> 16 & 0xFF);

            byte bR = (byte)(bF * beta + bT * alpha);
            byte gR = (byte)(gF * beta + gT * alpha);
            byte rR = (byte)(rF * beta + rT * alpha);
            result[i] = 0xFF000000u | ((uint)rR << 16) | ((uint)gR << 8) | bR;
        }
    }

    /// <summary>Awaitable Task.Delay that tolerates cancellation silently.</summary>
    private static async Task DelayWithCancel(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct); }
        catch (OperationCanceledException) { /* expected */ }
    }

    /// <summary>Awaitable Control.Invoke wrapper for use inside async methods.</summary>
    private Task InvokeAsync(Action action)
    {
        if (!IsHandleCreated || _disposed) return Task.CompletedTask;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            BeginInvoke(() =>
            {
                try { action(); tcs.TrySetResult(true); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
        }
        catch (Exception ex) { tcs.TrySetException(ex); }
        return tcs.Task;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MULTI-MONITOR SPAN
    // ─────────────────────────────────────────────────────────────────────────

    private void OnSpanMonitorsClick(object? sender, EventArgs e)
    {
        if (_spanning)
        {
            ExitSpanMode();
        }
        else
        {
            EnterSpanMode();
        }
    }

    private void EnterSpanMode()
    {
        if (_spanning) return;
        _preSpanWindowState = WindowState;
        _preSpanBorderStyle = FormBorderStyle;
        if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
        _preSpanBounds = Bounds;
        _spanning = true;
        _spanButton.Text = "Back";
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Normal;
        Bounds = SystemInformation.VirtualScreen;
        TopMost = true;
        Activate();
    }

    private void ExitSpanMode()
    {
        if (!_spanning) return;
        _spanning = false;
        TopMost = false;
        _spanButton.Text = "Span";
        FormBorderStyle = _preSpanBorderStyle;
        WindowState = FormWindowState.Normal;
        Bounds = _preSpanBounds;
        if (_preSpanWindowState == FormWindowState.Maximized)
            WindowState = FormWindowState.Maximized;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && _spanning)
        { ExitSpanMode(); e.Handled = true; return; }
        if (e.KeyCode == Keys.Escape && _slideshowRunning)
        { StopSlideshow(); e.Handled = true; return; }
        if (e.KeyCode == Keys.Return &&
            (ActiveControl == _txCX || ActiveControl == _txCY ||
             ActiveControl == _txZoom || ActiveControl == _txIter))
        { OnGoClick(null, EventArgs.Empty); e.Handled = true; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SCREENSHOT
    // ─────────────────────────────────────────────────────────────────────────

    private void OnScreenshotClick(object? sender, EventArgs e)
    {
        if (_calculator == null)
        {
            MessageBox.Show("No fractal data to save yet.", "Screenshot",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string colorName = _calculator.ColorMap?.GetType().GetProperty("Name")?.GetValue(null)?.ToString() ?? "Theme";
        string regionName = "";
        if (!string.IsNullOrEmpty(CurrentRegionName()))
            regionName = CurrentRegionName()?.Replace(" ", "") + "_" ?? "";

        Rectangle vs = SystemInformation.VirtualScreen;
        string sizeTag = _spanning
            ? $"{vs.Width}x{vs.Height}_wallpaper"
            : $"{_calculator.Width}x{_calculator.Height}";

        using var dlg = new SaveFileDialog
        {
            Title = _spanning ? "Save Wallpaper Screenshot" : "Save Fractal Screenshot",
            Filter = "PNG Image (*.png)|*.png|TIFF Image (*.tiff;*.tif)|*.tiff;*.tif|BMP Image (*.bmp)|*.bmp",
            FilterIndex = 1,
            DefaultExt = "png",
            FileName = $"{_programName}_{colorName}_{regionName}" +
                         $"x{_txCX.Text.Replace(".", "")}_" +
                         $"y{_txCY.Text.Replace(".", "")}_" +
                         $"z{_txZoom.Text.Replace(".", "")}_" +
                         $"i{_txIter.Text.Replace(".", "")}_" +
                         sizeTag
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        string path = dlg.FileName;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        var format = ext switch { ".bmp" => ImageFormat.Bmp, ".tif" or ".tiff" => ImageFormat.Tiff, _ => ImageFormat.Png };
        string wm = $"{(!string.IsNullOrEmpty(CurrentRegionName()) ? CurrentRegionName() : "Fracturing Fog")}" +
                      $"{(!string.IsNullOrEmpty(CurrentColorMapName()) ? " - " + CurrentColorMapName() : "")}";
        string subText = $"{_programName} v{_programVersion} {DateTime.Now.Year}";

        if (_spanning) TakeWallpaperScreenshot(path, format, wm, subText);
        else TakeNormalScreenshot(path, format, wm, subText);
    }

    private void TakeNormalScreenshot(string path, ImageFormat format, string waterMark, string subText)
    {
        int w = _calculator!.Width;
        int h = _calculator!.Height;
        // Apply the same brightness/contrast post-processing as the live view.
        uint[] pixels = BuildProcessedBuffer(_calculator);
        try
        {
            // Pixel-sampled contrast colour for the watermark.
            var fontColor = ComputeContrastColor(GetSwatchColor(),
                watermark: true, pixels: pixels, imgW: w, imgH: h);
            SavePixelsToFile(pixels, w, h, path, format, waterMark, fontColor, subText);
            SetStatus($"Saved  {Path.GetFileName(path)}  ({w}×{h},  {new FileInfo(path).Length / 1024:N0} KB)");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Save failed:\n{ex.Message}", "Screenshot Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Returns a BGRA buffer with brightness/contrast applied (and grid overlay
    /// if visible).  Returns the original ColorBuffer reference if no adjustments
    /// are active (avoids unnecessary allocation).
    /// </summary>
    private uint[] BuildProcessedBuffer(MandelbrotCalculator calc)
    {
        bool needsProcess = _brightness != 0 || _contrast != 0 || _gridVisible;
        if (!needsProcess) return calc.ColorBuffer;

        int n = calc.Width * calc.Height;
        var dst = new uint[n];
        float cf = 1.0f + _contrast / 100.0f;
        float bo = _brightness / 100.0f;
        uint[] src = calc.ColorBuffer;

        if (_brightness != 0 || _contrast != 0)
        {
            for (int i = 0; i < n; i++)
            {
                uint p = src[i];
                float r = ((p >> 16) & 0xFF) / 255f;
                float g = ((p >> 8) & 0xFF) / 255f;
                float b = (p & 0xFF) / 255f;
                r = (r - 0.5f) * cf + 0.5f + bo;
                g = (g - 0.5f) * cf + 0.5f + bo;
                b = (b - 0.5f) * cf + 0.5f + bo;
                byte R = (byte)(System.Math.Clamp(r, 0f, 1f) * 255f);
                byte G = (byte)(System.Math.Clamp(g, 0f, 1f) * 255f);
                byte B = (byte)(System.Math.Clamp(b, 0f, 1f) * 255f);
                dst[i] = 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
            }
        }
        else
        {
            Array.Copy(src, dst, n);
        }
        if (_gridVisible) BlendGridOverlay(dst, calc.Width, calc.Height);
        return dst;
    }

    private void TakeWallpaperScreenshot(string path, ImageFormat format, string waterMark, string subText)
    {
        Rectangle vs = SystemInformation.VirtualScreen;
        int fullW = vs.Width;
        int fullH = vs.Height;

        int toolbarH = 0;
        foreach (Control c in Controls)
            if (c.Dock == DockStyle.Top) toolbarH += c.Height;

        double cx = _calculator!.CenterX, cxLo = _calculator!.CenterXLo;
        double cx2 = _calculator!.CenterX2, cx3 = _calculator!.CenterX3;
        double cy = _calculator!.CenterY, cyLo = _calculator!.CenterYLo;
        double cy2 = _calculator!.CenterY2, cy3 = _calculator!.CenterY3;
        double zoom = _calculator!.Zoom;
        int maxIter = _calculator!.MaxIterations;
        IColorMap map = _calculator!.ColorMap;
        QualityPreset q = _quality;

        long mpix = (long)fullW * fullH / 1_000_000;
        _screenshotButton.Enabled = false;
        _screenshotButton.Text = "Rendering…";
        SetStatus($"Rendering wallpaper  {fullW}×{fullH}  ({mpix} MP, +{toolbarH} px over render panel)  …");

        CancellationToken token;
        lock (_wallpaperLock)
        {
            _wallpaperCts?.Cancel();
            _wallpaperCts = new CancellationTokenSource();
            token = _wallpaperCts.Token;
        }

        var sw = Stopwatch.StartNew();

        Task.Run(() =>
        {
            var tempCalc = new MandelbrotCalculator(fullW, fullH)
            {
                CenterX = cx,
                CenterXLo = cxLo,
                CenterX2 = cx2,
                CenterX3 = cx3,
                CenterY = cy,
                CenterYLo = cyLo,
                CenterY2 = cy2,
                CenterY3 = cy3,
                Zoom = zoom,
                MaxIterations = maxIter,
                ColorMap = map,
                Quality = q
            };
            tempCalc.Calculate(token);
            token.ThrowIfCancellationRequested();
            return tempCalc;
        }, token)
        .ContinueWith(t =>
        {
            if (!IsHandleCreated || _disposed) return;
            Invoke(() =>
            {
                _screenshotButton.Enabled = true;
                _screenshotButton.Text = "Image";

                if (t.IsCanceled) { SetStatus("Wallpaper render cancelled."); return; }
                if (t.IsFaulted)
                {
                    MessageBox.Show($"Wallpaper render failed:\n\n{t.Exception?.InnerException?.Message}",
                        "Screenshot Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                sw.Stop();
                MandelbrotCalculator result = t.Result;
                try
                {
                    var fontColor = ComputeContrastColor(GetSwatchColor(),
                        watermark: true, pixels: result.ColorBuffer,
                        imgW: result.Width, imgH: result.Height);
                    SavePixelsToFile(result.ColorBuffer, result.Width, result.Height, path, format, waterMark, fontColor, subText);
                    SetStatus($"Wallpaper saved  →  {Path.GetFileName(path)}  ({result.Width}×{result.Height} px,  {new FileInfo(path).Length / 1024:N0} KB)  [{sw.ElapsedMilliseconds} ms]");
                }
                catch (Exception ex) { MessageBox.Show($"Failed to save wallpaper:\n\n{ex.Message}", "Screenshot Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            });
        }, TaskScheduler.Default);
    }

    private void TakePosterScreenshot(int width, int height, bool isPortrait, bool rotateImage, string path, ImageFormat format, string waterMark, string subText)
    {
        int fullW = width;
        int fullH = height;

        int toolbarH = 0;
        foreach (Control c in Controls)
            if (c.Dock == DockStyle.Top) toolbarH += c.Height;

        double cx = _calculator!.CenterX, cxLo = _calculator!.CenterXLo;
        double cx2 = _calculator!.CenterX2, cx3 = _calculator!.CenterX3;
        double cy = _calculator!.CenterY, cyLo = _calculator!.CenterYLo;
        double cy2 = _calculator!.CenterY2, cy3 = _calculator!.CenterY3;
        double zoom = _calculator!.Zoom;
        int maxIter = _calculator!.MaxIterations;
        IColorMap map = _calculator!.ColorMap;
        QualityPreset q = _quality;

        long mpix = (long)fullW * fullH / 1_000_000;
        _screenshotButton.Enabled = false;
        _posterButton.Enabled = false;
        _posterButton.Text = "Rendering…";
        SetStatus($"Rendering poster  {fullW}×{fullH}  ({mpix} MP, +{toolbarH} px over render panel)  …");

        CancellationToken token;
        lock (_wallpaperLock)
        {
            _wallpaperCts?.Cancel();
            _wallpaperCts = new CancellationTokenSource();
            token = _wallpaperCts.Token;
        }

        var sw = Stopwatch.StartNew();

        Task.Run(() =>
        {
            var tempCalc = new MandelbrotCalculator(fullW, fullH)
            {
                CenterX = cx,
                CenterXLo = cxLo,
                CenterX2 = cx2,
                CenterX3 = cx3,
                CenterY = cy,
                CenterYLo = cyLo,
                CenterY2 = cy2,
                CenterY3 = cy3,
                Zoom = zoom,
                MaxIterations = maxIter,
                ColorMap = map,
                Quality = q
            };
            tempCalc.Calculate(token);
            token.ThrowIfCancellationRequested();
            return tempCalc;
        }, token)
        .ContinueWith(t =>
        {
            if (!IsHandleCreated || _disposed) return;
            Invoke(() =>
            {
                _screenshotButton.Enabled = true;
                _screenshotButton.Text = "Image";
                _posterButton.Enabled = true;
                _posterButton.Text = "Poster";

                if (t.IsCanceled) { SetStatus("Poster render cancelled."); return; }
                if (t.IsFaulted)
                {
                    MessageBox.Show($"Poster render failed:\n\n{t.Exception?.InnerException?.Message}",
                        "Screenshot Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                sw.Stop();
                MandelbrotCalculator result = t.Result;
                try
                {
                    // Rotate 90° clockwise when portrait or rotateImage is requested.
                    // The landscape render (width × height) becomes portrait (height × width).
                    if (isPortrait || rotateImage)
                    {
                        var rotated = new uint[result.ColorBuffer.Length];
                        for (int y = 0; y < result.Height; y++)
                            for (int x = 0; x < result.Width; x++)
                                rotated[x * result.Height + (result.Height - 1 - y)] = result.ColorBuffer[y * result.Width + x];
                        // After 90° CW rotation the saved dimensions are result.Height × result.Width.
                        var fontColor = ComputeContrastColor(GetSwatchColor(),
                            watermark: true, pixels: rotated, imgW: result.Height, imgH: result.Width);
                        SavePixelsToFile(
                            rotated,
                            result.Height,
                            result.Width,
                            path,
                            format,
                            waterMark,
                            fontColor,
                            subText,
                            true);
                        SetStatus($"Poster saved  →  {Path.GetFileName(path)}  ({result.Height}×{result.Width} px,  {new FileInfo(path).Length / 1024:N0} KB)  [{sw.ElapsedMilliseconds} ms]");
                    }
                    else
                    {
                        var fontColor = ComputeContrastColor(GetSwatchColor(),
                            watermark: true, pixels: result.ColorBuffer,
                            imgW: result.Width, imgH: result.Height);
                        SavePixelsToFile(
                            result.ColorBuffer,
                            result.Width,
                            result.Height,
                            path,
                            format,
                            waterMark,
                            fontColor,
                            subText,
                            true);
                        SetStatus($"Poster saved  →  {Path.GetFileName(path)}  ({result.Width}×{result.Height} px,  {new FileInfo(path).Length / 1024:N0} KB)  [{sw.ElapsedMilliseconds} ms]");
                    }
                }
                catch (Exception ex) { MessageBox.Show($"Failed to save poster:\n\n{ex.Message}", "Screenshot Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            });
        }, TaskScheduler.Default);
    }

    private static unsafe void SavePixelsToFile(
        uint[] pixels, int w, int h, string path, ImageFormat format,
        string watermarkText, Color fontColor, string subText = "", bool poster = false)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var bmpData = bmp.LockBits(new Rectangle(0, 0, w, h),
                                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            fixed (uint* src = pixels)
            {
                if (bmpData.Stride == w * 4)
                    Buffer.MemoryCopy(src, (void*)bmpData.Scan0, (long)w * h * 4, (long)w * h * 4);
                else
                {
                    byte* dst = (byte*)bmpData.Scan0;
                    for (int row = 0; row < h; row++)
                        Buffer.MemoryCopy((byte*)src + (long)row * w * 4,
                                          dst + (long)row * bmpData.Stride,
                                          (long)w * 4, (long)w * 4);
                }
            }
        }
        finally { bmp.UnlockBits(bmpData); }

        if (format == ImageFormat.Tiff)
        {
            ImageCodecInfo? codec = null;
            foreach (var c in ImageCodecInfo.GetImageEncoders())
                if (c.MimeType == "image/tiff") { codec = c; break; }
            if (codec != null)
            {
                using var ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Compression, (long)EncoderValue.CompressionLZW);
                bmp.Save(path, codec, ep);
            }
            else bmp.Save(path, format);
        }
        else bmp.Save(path, format);

        Debug.WriteLine($"Watermark text: '{watermarkText}'");
        if (!string.IsNullOrEmpty(watermarkText))
        {
            using var g = Graphics.FromImage(bmp);
            AddWaterMark(g, watermarkText, w, h, fontColor, subText, poster);
            bmp.Save(path, format);
        }
    }

    private static void AddWaterMark(
        Graphics g,
        string text,
        int width,
        int height,
        Color fontColor,
        string subText = "",
        bool poster = false)
    {
        int fontSize = poster ? System.Math.Max(width, height) / 140 : 16;
        Debug.WriteLine($"Watermark font size: {fontSize}px");

        using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        var sz = g.MeasureString(text, font);
        int yOffset = poster ? System.Math.Min(width, height) / 150 : 12;
        Debug.WriteLine($"Watermark position offset: {yOffset}px from bottom-right corner");
        var pos = new PointF(width - sz.Width - 20, height - sz.Height - yOffset);
        using var brush = new SolidBrush(fontColor);
        g.DrawString(text, font, brush, pos);

        if (!string.IsNullOrEmpty(subText))
        {
            using var fontSmall = new Font("Segoe UI", fontSize / 2, FontStyle.Bold, GraphicsUnit.Pixel);
            var sz2 = g.MeasureString(subText, fontSmall);
            int subTextOffset = poster ? 0 : 2;
            Debug.WriteLine($"Subtext font size: {fontSize / 2}px, offset: {subTextOffset}px");
            g.DrawString($"{subText}", fontSmall, brush,
                new PointF(width - sz2.Width - 55, height - sz2.Height - subTextOffset));
        }

        g.Save();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Mouse: wheel zoom  (with progressive preview)
    // ─────────────────────────────────────────────────────────────────────────

    private void OnMouseWheel(object? sender, MouseEventArgs e)
    {
        if (_calculator == null || _slideshowRunning) return;

        double wf = _quality.WheelZoomFactor;
        double factor = e.Delta > 0 ? wf : 1.0 / wf;
        double scale = CurrentScale();
        double ox = e.X - _renderPanel.ClientSize.Width * 0.5;
        double oy = e.Y - _renderPanel.ClientSize.Height * 0.5;
        //double compX = _centerX + ox * scale;
        //double compY = _centerY + oy * scale;

        //_zoom = System.Math.Clamp(_zoom * factor, _quality.ZoomMin, _quality.ZoomMax);

        //double ns = CurrentScale();
        //_centerX = compX - ox * ns;
        //_centerY = compY - oy * ns;

        // Anchor preservation: choose precision based on current zoom.
        //   • Zoom > QDZoomThreshold (1e25) → QD math (~62 digits, supports 5e58).
        //   • Above HP threshold (1e12)     → DD math (~31 digits, supports 5e27).
        //   • Else                          → plain double.
        if (_zoom > QDZoomThreshold)
        {
            var qdCX = new FracturingFog.FFMath.QD(_centerX, _centerXLo, _centerX2, _centerX3);
            var qdCY = new FracturingFog.FFMath.QD(_centerY, _centerYLo, _centerY2, _centerY3);
            var anchorX = qdCX + ox * scale;
            var anchorY = qdCY + oy * scale;
            _zoom = System.Math.Clamp(_zoom * factor, _quality.ZoomMin, _quality.ZoomMax);
            double ns = CurrentScale();
            var newCX = anchorX + (-ox * ns);
            var newCY = anchorY + (-oy * ns);
            _centerX = newCX.X0; _centerXLo = newCX.X1; _centerX2 = newCX.X2; _centerX3 = newCX.X3;
            _centerY = newCY.X0; _centerYLo = newCY.X1; _centerY2 = newCY.X2; _centerY3 = newCY.X3;
        }
        else if (_quality.NeedsHighPrecision(_zoom))
        {
            var ddCX = new FracturingFog.FFMath.DD(_centerX, _centerXLo);
            var ddCY = new FracturingFog.FFMath.DD(_centerY, _centerYLo);
            var anchorX = ddCX + ox * scale;
            var anchorY = ddCY + oy * scale;
            _zoom = System.Math.Clamp(_zoom * factor, _quality.ZoomMin, _quality.ZoomMax);
            double ns = CurrentScale();
            var newCX = anchorX - ox * ns;
            var newCY = anchorY - oy * ns;
            _centerX = newCX.Hi; _centerXLo = newCX.Lo; _centerX2 = 0; _centerX3 = 0;
            _centerY = newCY.Hi; _centerYLo = newCY.Lo; _centerY2 = 0; _centerY3 = 0;
        }
        else
        {
            double compX = _centerX + ox * scale;
            double compY = _centerY + oy * scale;
            _zoom = System.Math.Clamp(_zoom * factor, _quality.ZoomMin, _quality.ZoomMax);
            double ns = CurrentScale();
            _centerX = compX - ox * ns; _centerXLo = 0.0; _centerX2 = 0; _centerX3 = 0;
            _centerY = compY - oy * ns; _centerYLo = 0.0; _centerY2 = 0; _centerY3 = 0;
        }

        ApplyViewState();
        TriggerCalculation(progressive: false);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Mouse: pan  (throttled with debounce timer)
    // ─────────────────────────────────────────────────────────────────────────

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _slideshowRunning) return;
        _lastMouseDownPos = e.Location;
        _panning = true;
        _panStartScreen = e.Location;
        _panStartCX = _centerX;
        _panStartCY = _centerY;
        _panStartDDCX = new FracturingFog.FFMath.DD(_centerX, _centerXLo);
        _panStartDDCY = new FracturingFog.FFMath.DD(_centerY, _centerYLo);
        _panStartQDCX = new FracturingFog.FFMath.QD(_centerX, _centerXLo, _centerX2, _centerX3);
        _panStartQDCY = new FracturingFog.FFMath.QD(_centerY, _centerYLo, _centerY2, _centerY3);
        _renderPanel.Cursor = Cursors.SizeAll;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Mouse: double-click to center
    // ─────────────────────────────────────────────────────────────────────────

    private void OnMouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _calculator == null || _slideshowRunning) return;

        // Cancel any pan that started on the first click of the double-click.
        _panning = false;
        _renderPanel.Cursor = Cursors.Cross;

        // Convert the clicked screen pixel to complex-plane coordinates.
        double scale = CurrentScale();
        double ox = e.X - _renderPanel.ClientSize.Width * 0.5;
        double oy = e.Y - _renderPanel.ClientSize.Height * 0.5;
        //_centerX = _centerX + ox * scale;
        //_centerY = _centerY + oy * scale;
        if (_zoom > QDZoomThreshold)
        {
            var qdCX = new FracturingFog.FFMath.QD(_centerX, _centerXLo, _centerX2, _centerX3) + ox * scale;
            var qdCY = new FracturingFog.FFMath.QD(_centerY, _centerYLo, _centerY2, _centerY3) + oy * scale;
            _centerX = qdCX.X0; _centerXLo = qdCX.X1; _centerX2 = qdCX.X2; _centerX3 = qdCX.X3;
            _centerY = qdCY.X0; _centerYLo = qdCY.X1; _centerY2 = qdCY.X2; _centerY3 = qdCY.X3;
        }
        else if (_quality.NeedsHighPrecision(_zoom))
        {
            var newCX = new FracturingFog.FFMath.DD(_centerX, _centerXLo) + ox * scale;
            var newCY = new FracturingFog.FFMath.DD(_centerY, _centerYLo) + oy * scale;
            _centerX = newCX.Hi; _centerXLo = newCX.Lo; _centerX2 = 0; _centerX3 = 0;
            _centerY = newCY.Hi; _centerYLo = newCY.Lo; _centerY2 = 0; _centerY3 = 0;
        }
        else
        {
            _centerX = _centerX + ox * scale; _centerXLo = 0; _centerX2 = 0; _centerX3 = 0;
            _centerY = _centerY + oy * scale; _centerYLo = 0; _centerY2 = 0; _centerY3 = 0;
        }

        ApplyViewState();
        TriggerCalculation();
        SetStatus($"Centerd on  cx={_centerX:G12}  cy={_centerY:G12}");
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_panning || _calculator == null) return;
        double scale = CurrentScale();
        //_centerX = _panStartCX - (e.X - _panStartScreen.X) * scale;
        //_centerY = _panStartCY - (e.Y - _panStartScreen.Y) * scale;

        if (_zoom > QDZoomThreshold)
        {
            double dx = -(e.X - _panStartScreen.X) * scale;
            double dy = -(e.Y - _panStartScreen.Y) * scale;
            var newCX = _panStartQDCX + dx;
            var newCY = _panStartQDCY + dy;
            _centerX = newCX.X0; _centerXLo = newCX.X1; _centerX2 = newCX.X2; _centerX3 = newCX.X3;
            _centerY = newCY.X0; _centerYLo = newCY.X1; _centerY2 = newCY.X2; _centerY3 = newCY.X3;
        }
        else if (_quality.NeedsHighPrecision(_zoom))
        {
            double dx = -(e.X - _panStartScreen.X) * scale;
            double dy = -(e.Y - _panStartScreen.Y) * scale;
            var newCX = _panStartDDCX + dx;
            var newCY = _panStartDDCY + dy;
            _centerX = newCX.Hi; _centerXLo = newCX.Lo; _centerX2 = 0; _centerX3 = 0;
            _centerY = newCY.Hi; _centerYLo = newCY.Lo; _centerY2 = 0; _centerY3 = 0;
        }
        else
        {
            _centerX = _panStartCX - (e.X - _panStartScreen.X) * scale; _centerXLo = 0; _centerX2 = 0; _centerX3 = 0;
            _centerY = _panStartCY - (e.Y - _panStartScreen.Y) * scale; _centerYLo = 0; _centerY2 = 0; _centerY3 = 0;
        }

        ApplyViewState();
        // Throttled pan: fire a fast capped-iteration render immediately,
        // schedule a full-quality render for 300 ms after movement stops.
        _panStopTimer.Stop();
        _panStopTimer.Start();
        TriggerCalculationFast();
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _panning = false; _renderPanel.Cursor = Cursors.Cross;
        // If the timer is still running let it fire the full render naturally.
    }

    /// <summary>
    /// Fires a calculation with iterations capped for interactive responsiveness.
    /// Full-quality render is triggered by _panStopTimer after dragging stops.
    /// </summary>
    private void TriggerCalculationFast()
    {
        if (_calculator == null) return;
        int saved = _calculator.MaxIterations;
        _calculator.MaxIterations = System.Math.Min(128, saved);
        TriggerCalculation(progressive: false);
        _calculator.MaxIterations = saved;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Resize
    // ─────────────────────────────────────────────────────────────────────────

    private void OnFormResize(object? sender, EventArgs e)
    {
        if (_renderer == null || _calculator == null) return;
        if (WindowState == FormWindowState.Minimized) return;
        Debug.WriteLine($"MiniMode: {_miniMode}  Size: {Size}  RenderPanel: {_renderPanel.ClientSize} Previous Size: {_miniPreviousSize}");

        if (!_spanning && _miniClick)
        {
            FormBorderStyle = _miniMode ? FormBorderStyle.None : _miniPreviousBorderStyle;
            Size = new Size(
                _miniMode ? MinimumSize.Width : _miniPreviousSize.Width,
                _miniMode ? MinimumSize.Height : _miniPreviousSize.Height);
        }


        int w = _renderPanel.ClientSize.Width;
        int h = _renderPanel.ClientSize.Height;
        if (w < 1 || h < 1) return;

        // Discard the cached buffer — its dimensions no longer match.
        _lastUploadedBuffer = null;

        _renderer.Resize(w, h);
        _calculator.Resize(w, h);
        ApplyViewState();
        TriggerCalculation();
        PositionGridPanel();
    }

    private void PositionGridPanel()
    {
        // The grid is blended into the ColorBuffer directly; no window to reposition.
        // Trigger a repaint so the grid is redrawn at the new panel size.
        if (_gridVisible) RepaintWithBrightnessContrast();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Idle render loop
    // ─────────────────────────────────────────────────────────────────────────

    private void OnApplicationIdle(object? sender, EventArgs e)
    {
        if (!_disposed && _renderer != null) _renderer.Render();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // View state helpers
    // ─────────────────────────────────────────────────────────────────────────

    private double CurrentScale()
    {
        if (_calculator == null) return 3.5;
        return 3.5 / (System.Math.Max(_calculator.Width, _calculator.Height) * _zoom);
    }

    /// <summary>
    /// Pushes the current view state into the calculator.
    /// When <paramref name="maxIters"/> is &gt; 0 it is used directly;
    /// otherwise the quality preset auto-computes it — unless the lock is active,
    /// in which case the locked value is always used.
    /// </summary>
    private void ApplyViewState(int maxIters = 0)
    {
        if (_calculator == null) return;
        _calculator.CenterX = _centerX;
        _calculator.CenterXLo = _centerXLo;
        _calculator.CenterX2 = _centerX2;
        _calculator.CenterX3 = _centerX3;
        _calculator.CenterY = _centerY;
        _calculator.CenterYLo = _centerYLo;
        _calculator.CenterY2 = _centerY2;
        _calculator.CenterY3 = _centerY3;
        _calculator.Zoom = _zoom;
        _calculator.Quality = _quality;

        if (_iterLocked)
            _calculator.MaxIterations = _lockedIterations;
        else if (maxIters > 0)
            _calculator.MaxIterations = maxIters;
        else
            _calculator.MaxIterations = _quality.ComputeIterations(_zoom);

        UpdateCoordBoxes();
    }

    private void UpdateCoordBoxes()
    {
        if (_suppressCoordUpdate) return;
        _suppressCoordUpdate = true;
        try
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            if (ActiveControl != _txCX) _txCX.Text = _centerX.ToString("G15", ic);
            if (ActiveControl != _txCY) _txCY.Text = _centerY.ToString("G15", ic);
            if (ActiveControl != _txZoom) _txZoom.Text = _zoom.ToString("G8", ic);
            if (ActiveControl != _txIter && _calculator != null)
                _txIter.Text = _calculator.MaxIterations.ToString();
        }
        finally { _suppressCoordUpdate = false; }
    }

    private string CurrentRegionName()
    {
        string? selected = _regionCombo.SelectedItem?.ToString();
        selected = !string.IsNullOrEmpty(_slideshowRegionName) ? _slideshowRegionName : selected;
        if (string.IsNullOrEmpty(selected) || selected == "— select region —") return "";
        return selected;
    }

    private string CurrentColorMapName()
    {
        if (_calculator?.ColorMap == null) return "";
        var prop = _calculator.ColorMap.GetType()
            .GetProperty("Name", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        return prop?.GetValue(null)?.ToString() ?? "Theme";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Async calculation  (with optional progressive preview)
    // ─────────────────────────────────────────────────────────────────────────

    private void TriggerCalculation(bool progressive = false)
    {
        var callingMethod = new StackTrace().GetFrame(1)?.GetMethod();
        Debug.WriteLine($"TriggerCalculation called from {callingMethod?.DeclaringType?.Name}.{callingMethod?.Name}. Progressive: {progressive}");
        if (_calculator == null) return;

        CancellationTokenSource cts;
        lock (_calcLock)
        {
            _calcCts?.Cancel();
            _calcCts = new CancellationTokenSource();
            cts = _calcCts;
        }

        // ── Keep the previous frame visible while the new one computes ────────
        // Re-upload the last completed buffer immediately so the screen shows a
        // stale (but correct) image rather than going black during a long
        // High/Ultra-quality recalculation.  Skip if the dimensions changed
        // (resize) — a size-mismatch upload would corrupt the texture.
        if (_lastUploadedBuffer != null
            && _renderer != null
            && _lastUploadedWidth == _calculator.Width
            && _lastUploadedHeight == _calculator.Height)
        {
            _renderer.UpdateTexture(_lastUploadedBuffer, _lastUploadedWidth, _lastUploadedHeight);
        }

        var token = cts.Token;
        var calc = _calculator;
        var renderer = _renderer;

        SetStatus("Calculating…");
        var sw = Stopwatch.StartNew();

        // ── Full-resolution render ────────────────────────────────────────────
        Task.Run(() => { calc.Calculate(token); return sw.ElapsedMilliseconds; }, token)
        .ContinueWith(t =>
        {
            if (t.IsCanceled || token.IsCancellationRequested) return;
            if (renderer == null) return;

            long ms = t.IsCompletedSuccessfully ? t.Result : -1;

            if (IsHandleCreated && !_disposed)
            {
                Invoke(() =>
                {
                    if (_disposed) return;
                    // Apply brightness/contrast and grid overlay, then upload to GPU.
                    UploadProcessedBuffer(calc, renderer);
                    _miniMapPanel?.RefreshIndicator();
                    string precTag = calc.IsHighPrecisionActive ? "[DD]" : "[SP]";
                    SetStatus(
                        $"cx={calc.CenterX:G12}  cy={calc.CenterY:G12}  " +
                        $"zoom={calc.Zoom:G6}  iter={calc.MaxIterations}  " +
                        $"{precTag}  [{ms} ms  {calc.Width}×{calc.Height}]" +
                        (_iterLocked ? "  [ITER LOCKED]" : ""));
                });
            }
        }, TaskScheduler.Default);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Post-processing: brightness / contrast / grid / watermark
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Called when brightness/contrast sliders change or the grid is toggled.
    /// Re-applies post-processing to the existing ColorBuffer and re-uploads
    /// to the GPU without re-running the fractal calculation.
    /// </summary>
    private void RepaintWithBrightnessContrast()
    {
        if (_calculator == null || _renderer == null || _disposed) return;
        UploadProcessedBuffer(_calculator, _renderer);
    }

    /// <summary>
    /// Applies brightness/contrast adjustment and optional grid overlay to
    /// <paramref name="calc"/>.ColorBuffer, then uploads the result to the GPU.
    /// The original ColorBuffer is never modified — a temporary buffer is used.
    /// </summary>
    private void UploadProcessedBuffer(MandelbrotCalculator calc, IFractalRenderer renderer)
    {
        int w = calc.Width;
        int h = calc.Height;
        uint[] src = calc.ColorBuffer;
        int n = w * h;

        bool needsProcess = _brightness != 0 || _contrast != 0
                         || _gridVisible || _showSlideshowWatermark;

        // Always allocate a destination buffer so that _lastUploadedBuffer always
        // holds the post-processed result.  When no adjustments are active this is
        // just a fast Array.Copy, but it ensures that the stale-frame re-upload in
        // TriggerCalculation never flashes unprocessed pixels during zoom/pan.


        // Build a processed copy.
        var dst = new uint[n];

        // Pre-compute contrast factor.
        // _contrast in [-100, 100] maps to a multiplier:
        //   0  → 1.0×   (neutral)
        //  +100 → 2.0×   (doubled contrast)
        //  -100 → 0.0×   (flat grey)
        float contrastFactor = 1.0f + _contrast / 100.0f;

        float brightnessOffset = _brightness / 100.0f;  // [-1, 1]

        if (_brightness != 0 || _contrast != 0)
        {
            for (int i = 0; i < n; i++)
            {
                uint p = src[i];
                float r = ((p >> 16) & 0xFF) / 255f;
                float g = ((p >> 8) & 0xFF) / 255f;
                float b = (p & 0xFF) / 255f;

                // Contrast: scale around 0.5 midpoint.
                r = (r - 0.5f) * contrastFactor + 0.5f;
                g = (g - 0.5f) * contrastFactor + 0.5f;
                b = (b - 0.5f) * contrastFactor + 0.5f;

                // Brightness: linear offset.
                r += brightnessOffset;
                g += brightnessOffset;
                b += brightnessOffset;

                byte R = (byte)(System.Math.Clamp(r, 0f, 1f) * 255f);
                byte G = (byte)(System.Math.Clamp(g, 0f, 1f) * 255f);
                byte B = (byte)(System.Math.Clamp(b, 0f, 1f) * 255f);
                dst[i] = 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
            }
        }
        else
        {
            Array.Copy(src, dst, n);
        }

        // Grid overlay — blend GDI+ grid lines into the buffer.
        if (_gridVisible)
            BlendGridOverlay(dst, w, h);
        if (_showSlideshowWatermark) BlendWatermarkOverlay(dst, w, h);

        renderer.UpdateTexture(dst, w, h);
        // Cache the fully processed buffer so stale-frame re-uploads during
        // zoom/pan always show the correct brightness/contrast/grid state.
        _lastUploadedBuffer = dst;
        _lastUploadedWidth = w;
        _lastUploadedHeight = h;
    }

    /// <summary>
    /// Blends the standard watermark text into a BGRA uint[] buffer using GDI+.
    /// The contrast colour is sampled from the lower-right region of dst itself.
    /// </summary>
    private unsafe void BlendWatermarkOverlay(uint[] dst, int w, int h)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            // Sample the destination buffer for a contrast colour.
            Color fontColor = ComputeContrastColor(
                GetSwatchColor(), watermark: true, pixels: dst, imgW: w, imgH: h);

            string wm = $"{(!string.IsNullOrEmpty(CurrentRegionName()) ? CurrentRegionName() : "")}" + // ? " - " + 
                        $"{(!string.IsNullOrEmpty(CurrentColorMapName()) ? " - " + CurrentColorMapName() : "")}";
            string subText = $"{_programName} v{_programVersion} {DateTime.Now.Year}";
            AddWaterMark(g, wm, w, h, fontColor, subText);
        }

        var data = bmp.LockBits(new Rectangle(0, 0, w, h),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte* srcPtr = (byte*)data.Scan0;
            int stride = data.Stride;
            for (int row = 0; row < h; row++)
            {
                byte* rowPtr = srcPtr + (long)row * stride;
                for (int col = 0; col < w; col++)
                {
                    byte gA = rowPtr[col * 4 + 3];
                    if (gA == 0) continue;
                    byte gB = rowPtr[col * 4 + 0];
                    byte gG = rowPtr[col * 4 + 1];
                    byte gR = rowPtr[col * 4 + 2];
                    int idx = row * w + col;
                    uint p = dst[idx];
                    byte dR = (byte)((p >> 16) & 0xFF);
                    byte dG = (byte)((p >> 8) & 0xFF);
                    byte dB = (byte)(p & 0xFF);
                    float a = gA / 255f, ia = 1f - a;
                    dst[idx] = 0xFF000000u
                        | ((uint)(byte)(gR * a + dR * ia) << 16)
                        | ((uint)(byte)(gG * a + dG * ia) << 8)
                        | (uint)(byte)(gB * a + dB * ia);
                }
            }
        }
        finally { bmp.UnlockBits(data); }
    }
    private unsafe void BlendGridOverlay(uint[] dst, int w, int h)
    {
        using var bmp = new System.Drawing.Bitmap(w, h,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint =
                System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            _gridPanel.DrawGrid(g, w, h);
        }

        var data = bmp.LockBits(
            new System.Drawing.Rectangle(0, 0, w, h),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            byte* src = (byte*)data.Scan0;
            int stride = data.Stride;
            for (int row = 0; row < h; row++)
            {
                byte* rowPtr = src + (long)row * stride;
                for (int col = 0; col < w; col++)
                {
                    byte gB = rowPtr[col * 4 + 0];
                    byte gG = rowPtr[col * 4 + 1];
                    byte gR = rowPtr[col * 4 + 2];
                    byte gA = rowPtr[col * 4 + 3];
                    if (gA == 0) continue;   // fully transparent — skip

                    int idx = row * w + col;
                    uint p = dst[idx];
                    byte dR = (byte)((p >> 16) & 0xFF);
                    byte dG = (byte)((p >> 8) & 0xFF);
                    byte dB = (byte)(p & 0xFF);

                    float a = gA / 255f;
                    float ia = 1f - a;
                    byte oR = (byte)(gR * a + dR * ia);
                    byte oG = (byte)(gG * a + dG * ia);
                    byte oB = (byte)(gB * a + dB * ia);
                    dst[idx] = 0xFF000000u | ((uint)oR << 16) | ((uint)oG << 8) | oB;
                }
            }
        }
        finally { bmp.UnlockBits(data); }
    }

    private void SetStatus(string text)
    {
        if (InvokeRequired) Invoke(() => _statusLabel.Text = text);
        else _statusLabel.Text = text;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Disposal
    // ─────────────────────────────────────────────────────────────────────────

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _disposed = true;
        Application.Idle -= OnApplicationIdle;

        _panStopTimer.Stop(); _panStopTimer.Dispose();
        StopSlideshow();
        lock (_calcLock) _calcCts?.Cancel();
        lock (_wallpaperLock) _wallpaperCts?.Cancel();

        _renderer?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _renderer?.Dispose();
        base.Dispose(disposing);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Extension helpers for fluent control construction
// ─────────────────────────────────────────────────────────────────────────────

internal static class ControlExtensions
{
    public static T Also<T>(this T ctrl, Action<T> action) where T : Control
    { action(ctrl); return ctrl; }

    public static T AlsoAdd<T>(this T ctrl, Panel parent) where T : Control
    { parent.Controls.Add(ctrl); return ctrl; }

    public static T AlsoAdd<T>(this T ctrl, Panel parent, string tooltip) where T : Control
    { new ToolTip().SetToolTip(ctrl, tooltip); parent.Controls.Add(ctrl); return ctrl; }
}

// ─────────────────────────────────────────────────────────────────────────────
// DirectX render panel
// ─────────────────────────────────────────────────────────────────────────────

internal sealed class RenderPanel : Panel
{
    public RenderPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque | ControlStyles.UserPaint, true);
        MouseEnter += (_, _) => Focus();
    }
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x00200000; return cp; }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Colour theme combo with swatch preview
// ─────────────────────────────────────────────────────────────────────────────

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

// ─────────────────────────────────────────────────────────────────────────────
// Cartesian grid helper
// ─────────────────────────────────────────────────────────────────────────────
// The previous WS_EX_LAYERED sibling-window approach could not composite over
// the D3D11 FlipDiscard swap chain on modern Windows.  The grid is now blended
// directly into the fractal ColorBuffer by MainForm.BlendGridOverlay() before
// the texture is uploaded to the GPU — see UploadProcessedBuffer().
//
// This class is purely a drawing helper: it holds the view-state accessors and
// exposes DrawGrid(Graphics,w,h) which renders the Cartesian grid into any
// Graphics context (typically a 32bpp ARGB Bitmap with a transparent background).

internal sealed class GridOverlayPanel : System.Windows.Forms.Control
{
    private readonly Func<(double cx, double cy)> _getCenter;
    private readonly Func<double> _getZoom;
    private readonly Func<Size> _getPanelSize;
    private readonly Func<Color> _getSwatchColor;

    public GridOverlayPanel(
        Func<(double, double)> getCenter,
        Func<double> getZoom,
        Func<Size> getPanelSize,
        Func<Color> getSwatchColor)
    {
        _getCenter = getCenter;
        _getZoom = getZoom;
        _getPanelSize = getPanelSize;
        _getSwatchColor = getSwatchColor;
    }

    /// <summary>
    /// Renders the Cartesian grid into <paramref name="g"/> at the given pixel
    /// dimensions.  The caller is responsible for clearing the bitmap to
    /// Transparent before calling this method.
    /// </summary>
    public void DrawGrid(Graphics g, int w, int h)
        => DrawCartesianGrid(g, w, h);

    // ── Drawing implementation ────────────────────────────────────────────────

    private void DrawCartesianGrid(Graphics g, int w, int h)
    {
        var (cx, cy) = _getCenter();
        double zoom = _getZoom();
        double scale = 3.5 / (System.Math.Max(w, h) * zoom);

        double xMin = cx - w * scale * 0.5, xMax = cx + w * scale * 0.5;
        double yMin = cy - h * scale * 0.5, yMax = cy + h * scale * 0.5;

        Color gridColor = ComputeContrastColor(_getSwatchColor());
        using var gridPen = new Pen(Color.FromArgb(160, gridColor), 1.0f);
        using var axisPen = new Pen(Color.FromArgb(210, gridColor), 1.8f);
        using var labelBrush = new SolidBrush(Color.FromArgb(200, gridColor));
        using var shadowBrush = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
        using var labelFont = new Font("Consolas", 7.5f, FontStyle.Regular, GraphicsUnit.Point);
        using var zeroFont = new Font("Consolas", 8.5f, FontStyle.Bold, GraphicsUnit.Point);

        double gridStep = NiceStep((xMax - xMin) / 7.0);

        // Vertical lines.
        for (double wx = System.Math.Ceiling(xMin / gridStep) * gridStep;
             wx <= xMax + gridStep * 0.01; wx += gridStep)
        {
            float px = W2SX(wx, cx, scale, w);
            if (px < 0 || px > w) continue;
            bool isAxis = System.Math.Abs(wx) < gridStep * 0.01;
            g.DrawLine(isAxis ? axisPen : gridPen, px, 0, px, h);
            string lbl = FormatCoord(wx);
            var sz = g.MeasureString(lbl, labelFont);
            float lx = px - sz.Width * 0.5f, ly = h - sz.Height - 2;
            if (ly < 0) ly = 2;
            g.DrawString(lbl, labelFont, shadowBrush, lx + 1, ly + 1);
            g.DrawString(lbl, labelFont, labelBrush, lx, ly);
        }

        // Horizontal lines.
        for (double wy = System.Math.Ceiling(yMin / gridStep) * gridStep;
             wy <= yMax + gridStep * 0.01; wy += gridStep)
        {
            float py = W2SY(wy, cy, scale, h);
            if (py < 0 || py > h) continue;
            bool isAxis = System.Math.Abs(wy) < gridStep * 0.01;
            g.DrawLine(isAxis ? axisPen : gridPen, 0, py, w, py);
            if (isAxis) continue;
            string lbl = FormatCoord(wy) + "i";
            var sz = g.MeasureString(lbl, labelFont);
            g.DrawString(lbl, labelFont, shadowBrush, 4, py - sz.Height * 0.5f + 1);
            g.DrawString(lbl, labelFont, labelBrush, 3, py - sz.Height * 0.5f);
        }

        // Origin label.
        float ox = W2SX(0, cx, scale, w);
        float oy = W2SY(0, cy, scale, h);
        if (ox >= 0 && ox <= w && oy >= 0 && oy <= h)
        {
            g.DrawString("0", zeroFont, shadowBrush, ox + 3, oy + 3);
            g.DrawString("0", zeroFont, labelBrush, ox + 2, oy + 2);
        }
    }

    private static float W2SX(double wx, double cx, double scale, int w)
        => (float)((wx - cx) / scale + w * 0.5);

    private static float W2SY(double wy, double cy, double scale, int h)
        => (float)(-(wy - cy) / scale + h * 0.5);

    private static double NiceStep(double raw)
    {
        if (raw <= 0) return 1.0;
        double mag = System.Math.Pow(10, System.Math.Floor(System.Math.Log10(raw)));
        double norm = raw / mag;
        double nice = norm <= 1.0 ? 1.0 : norm <= 2.0 ? 2.0 : norm <= 5.0 ? 5.0 : 10.0;
        return nice * mag;
    }

    private static string FormatCoord(double v)
    {
        if (v == 0.0) return "0";
        double abs = System.Math.Abs(v);
        // Always render 7 significant digits so that deep-zoom grid lines
        // show distinct labels even when graduations differ only in the 6th–7th
        // decimal place (e.g. -1.744453 vs -1.744452).
        // "mag" is the order of magnitude of the integer part:
        //   abs = 1.744  → mag = 0  → decimals = 6
        //   abs = 0.022  → mag = -2 → decimals = 8  (clamped to 15)
        int mag = (int)System.Math.Floor(System.Math.Log10(abs));
        int decimals = System.Math.Clamp(6 - mag, 0, 15);
        return v.ToString("F" + decimals, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Color ComputeContrastColor(Color swatch)
    {
        float r = swatch.R / 255f, g = swatch.G / 255f, b = swatch.B / 255f;
        float cmax = System.Math.Max(r, System.Math.Max(g, b));
        float cmin = System.Math.Min(r, System.Math.Min(g, b));
        float delta = cmax - cmin;
        float l = (cmax + cmin) * 0.5f;
        float h2 = 0f;
        if (delta > 0.001f)
        {
            if (cmax == r) h2 = ((g - b) / delta) % 6f;
            else if (cmax == g) h2 = (b - r) / delta + 2f;
            else h2 = (r - g) / delta + 4f;
            h2 = (h2 / 6f + 1f) % 1f;
        }
        float s2 = delta < 0.001f ? 0f : delta / (1f - System.Math.Abs(2f * l - 1f));
        float hc = (h2 + 0.5f) % 1f;
        float lc = l < 0.5f
            ? System.Math.Clamp(1f - l * 0.6f, 0.65f, 1.0f)
            : System.Math.Clamp(1f - l * 1.4f, 0.0f, 0.35f);
        float sc = System.Math.Clamp(s2 * 0.5f + 0.5f, 0.5f, 1.0f);
        float cv = (1f - System.Math.Abs(2f * lc - 1f)) * sc;
        float xv = cv * (1f - System.Math.Abs((hc * 6f) % 2f - 1f));
        float m = lc - cv * 0.5f;
        float rr, gg, bb;
        switch ((int)(hc * 6f))
        {
            case 0: rr = cv; gg = xv; bb = 0; break;
            case 1: rr = xv; gg = cv; bb = 0; break;
            case 2: rr = 0; gg = cv; bb = xv; break;
            case 3: rr = 0; gg = xv; bb = cv; break;
            case 4: rr = xv; gg = 0; bb = cv; break;
            default: rr = cv; gg = 0; bb = xv; break;
        }
        return Color.FromArgb(
            (int)System.Math.Clamp((rr + m) * 255f, 0, 255),
            (int)System.Math.Clamp((gg + m) * 255f, 0, 255),
            (int)System.Math.Clamp((bb + m) * 255f, 0, 255));
    }
}