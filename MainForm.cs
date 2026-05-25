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
using System.Web;
using System.Windows.Forms;

using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Views;
using static FracturingFog.Views.FormHelpers;

namespace FracturingFog;

/// <summary>
/// Fracturing Fog main window and UI logic.  This class is responsible for:
/// </summary>
public sealed partial class MainForm : Form
{
    #region Private fields

    #region Program

    private readonly string _programVersion = "0.6.1";
    private readonly string _programName = "Fracturing Fog";
    private bool _disposed;

    #endregion Program

    #region UI

    // Floating Menu form
    FloatingMenu _floatingMenu;

    // Floating Help form (lazy)
    FloatingHelp? _floatingHelp;

    // Color Theme Editor form (lazy, singleton). Created on first click of
    // FloatingMenu's "Edit Theme…" button; closed automatically restores the
    // committed color map via ClearPreview().
    ColorThemeEditor? _colorThemeEditor;

    // UI: top toolbar
    private readonly Panel _toolbar;
    private readonly Button _resetButton;
    private readonly Button _spanButton;
    private readonly Button _posterButton;
    private readonly Button _helpButton;
    private readonly Button _screenshotButton;
    private readonly Button _slideshowButton;
    private readonly Button _videoButton;
    private readonly Label _qualityLabel;
    private readonly ComboBox _qualityCombo;
    private readonly Label _colorThemeLabel;
    private readonly ComboBox _colorThemeCombo;
    private Label? _fractalTypeLabel;
    private ComboBox? _fractalTypeCombo;
    private readonly Label _regionLabel;
    private readonly ComboBox _regionCombo;
    private readonly Button _editThemeButton;
    private readonly Button _saveViewButton;
    private readonly Button _delRegionButton;
    private readonly Button _menuButton;
    private readonly ToolTip _toolTip = new();
    private int _toolbarLastWidth;
    private int _toolbarLastHeight;

    // Current values
    private string _currentRegionName;
    private int _currentRegionSelection;
    private string _currentColorThemeName;
    private int _currentColorThemeSelection;

    // Color Theme Editor preview state. When true, _calculator.ColorMap is a
    // transient runtime map built from in-editor parameters; the theme combo
    // selection is not authoritative and must not be re-applied until preview
    // ends (ClearPreview restores the committed _currentColorThemeName map).
    private bool _previewingTransientMap;
    private string _currentQualityName;
    private int _currentQualitySelection;

    //// UI: coordinate / region bar
    private Label? _currentColorThemeLabel;

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

    // Mini-depth indicator
    private MiniDepthPanel? _miniDepthPanel;

    // Footer
    private readonly Label _statusLabel;
    private readonly Panel _footerPanel;

    /// <summary>Brightness offset in [-100, 100]; 0 = neutral.</summary>
    private int _brightness = 0;

    /// <summary>Contrast multiplier encoded as integer [-100, 100]; 0 = neutral (1.0×).</summary>
    private int _contrast = 0;

    /// <summary>Histogram-equalization strength as integer [0, 100]; 0 = disabled, 100 = full eq.</summary>
    private int _histogramEq = 0;

    // Mouse click-n-drag window repositioning
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION = 0x2;

    // Floating menu flag
    private bool _showFloatingMenu = false;

    #endregion UI

    #region View state

    private const double DefaultCenterX = -0.5;
    private const double DefaultCenterY = 0.0;
    private const double DefaultZoom = 0.13;

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

    // Right-click camera-drag state for 3D fractal modes.
    private bool _rightDragging;
    private Point _rightDragStart;
    private double _rightDragStartTheta;
    private double _rightDragStartPhi;
    // Timestamp of last right-mouse-down (any fractal mode). Used to suppress
    // ContextMenuStrip in 3D when click was long-press or moved (drag intent).
    private DateTime _rightDownTimeUtc;
    private const int RightHoldSuppressMs = 1000;
    private const int RightMoveSuppressPx = 4;

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
    private IFractalCalculator _fractalCalculator;
    private MandelbrotCalculator? _calculator;
    private EscapeTimeCalculator? _escapeCalculator;  // Julia / BurningShip / Tricorn / Multibrot / Phoenix
    private IFSCalculator? _ifsCalculator;
    private LSystemCalculator? _lsystemCalculator;
    private AttractorCalculator? _attractorCalculator;
    private BuddhabrotCalculator? _buddhabrotCalculator;
    private NewtonCalculator? _newtonCalculator;
    private UserEquationCalculator? _userEquationCalculator;
    private MandelbulbCalculator? _mandelbulbCalculator;
    private SandboxCalculator? _sandboxCalculator;
    private UserBulbCalculator? _userBulbCalculator;
    private TearDropCalculator? _tearDropCalculator;
    private Views.UserEquationDialog? _userEqDialog;
    private Views.SandboxDialog? _sandboxDialog;
    private Views.UserBulbDialog? _userBulbDialog;
    private FractalType _currentFractalType = FractalType.Mandelbrot;
    private FractalParameters _fractalParams = new();
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
    private bool _showSlideshowWatermark;
    private string _slideshowRegionName = "";
    private bool _slideshowSkipRegion;   // set to true to skip the current region and move to the next one immediately
    private bool _slideshowSkipTheme;    // set to true to skip the current color theme and move to the next one immediately
    private bool _slideshowPaused;       // set to true to hold the slideshow at its current state (no theme/region advance)
    private bool _slideShowLockRegion;     // When true, the slideshow will not change regions; only themes.  Set by Shift+clicking the Slideshow button.
    private bool _slideshowFocusRegion = true;   // When true, the slideshow will focus on the current region.  Set by clicking the Slideshow Focus button.
    private Views.SlideshowVcrPanel? _vcrPanel;  // Floating VCR controls visible during Slideshow / Video Slideshow.

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

    //#region Public Members

    ///// <summary>
    ///// MARGINS struct for DwmExtendFrameIntoClientArea call to enable Aero glass effect on the toolbar.  
    ///// All fields set to -1 to extend the glass over the entire toolbar area.
    ///// </summary>
    //[StructLayout(LayoutKind.Sequential)]
    //public struct MARGINS
    //{
    //    public int cxLeftWidth;
    //    public int cxRightWidth;
    //    public int cyTopHeight;
    //    public int cyBottomHeight;
    //}

    //#endregion Public Members

    #region Constructors

    /// <summary>
    /// MainForm constructor: sets up the UI and event handlers.  The actual fractal renderer and 
    /// calculator are not initialised here; that happens in OnLoad to ensure the form is fully created 
    /// before we attempt to create D3D devices or load shaders.
    /// </summary>
    public MainForm()
    {
        Icon = new Icon(@".\Resources\FracturingFog.ico");
        Text = $"{_programName} v{_programVersion} - {RendererFactory.ProbeDescription()}";
        ClientSize = new Size(1169, 728);
        MinimumSize = new Size(480, 270);
        BackColor = Color.Black;
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        _miniPreviousBorderStyle = FormBorderStyle;
        _miniPreviousSize = Size;

        _floatingMenu = new FloatingMenu(this);

        #region Pan-stop timer 
        _panStopTimer = new System.Windows.Forms.Timer { Interval = 300 };
        _panStopTimer.Tick += (s, e) =>
        {
            _panStopTimer.Stop();
            TriggerCalculation(progressive: false);   // full quality after drag stops
        };
        #endregion Pan-stop timer

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
        //int labelTop = 9;
        //int txTop = 7;

        _helpButton = new Button
        {
            Text = "?",
            Width = 26,
            Height = 24,
            Top = buttonTop,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(40, 60, 100),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Cursor = Cursors.Hand,
        };
        _helpButton.Left = Width - 46;
        _helpButton.FlatAppearance.BorderColor = Color.FromArgb(80, 120, 180);
        _helpButton.Click += (s, e) =>
        {
            OnShowHelpClick();
            _floatingHelp?.ShowEditorTab();
            _floatingHelp?.BringToFront();
        };
        _toolbar.Controls.Add(_helpButton);


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
        buttonLeft += _resetButton.PreferredSize.Width - 1;

        _spanButton = MakeBtn("Span", 55, buttonLeft, buttonTop, "Span across all monitors");
        _spanButton.Click += OnSpanMonitorsClick;
        _toolbar.Controls.Add(_spanButton);
        buttonLeft += _spanButton.PreferredSize.Width + 2;

        _screenshotButton = MakeBtn("Image ▾", 65, buttonLeft, buttonTop, "Capture Image, Poster, or Video");
        _posterButton = MakeBtn("Poster", 55, 0, 0);
        _posterButton.Click += OnPosterClick;
        _videoButton = MakeBtn("Video", 55, 0, 0, "Smooth animated zoom from current view to a target region/coordinate");
        _videoButton.Click += OnVideoClick;

        var imageMenu = new ContextMenuStrip();
        imageMenu.Items.Add(new ToolStripMenuItem("Image", null, (s, e) => OnScreenshotClick(s, e)));
        imageMenu.Items.Add(new ToolStripMenuItem("Poster", null, (s, e) => OnPosterClick(s, e)));
        imageMenu.Items.Add(new ToolStripMenuItem("Video", null, (s, e) => OnVideoClick(s, e)));
        _screenshotButton.Click += (s, e) => imageMenu.Show(_screenshotButton, new Point(0, _screenshotButton.Height));
        _toolbar.Controls.Add(_screenshotButton);
        buttonLeft += _screenshotButton.PreferredSize.Width - 1;

        _slideshowButton = MakeBtn("Slideshow", 74, buttonLeft, buttonTop, "Start/stop slideshow — auto-cycles regions every 30 s, themes every 10 s");
        _slideshowButton.BackColor = Color.FromArgb(40, 55, 40);
        _slideshowButton.FlatAppearance.BorderColor = Color.FromArgb(60, 100, 60);
        _slideshowButton.Click += OnSlideshowClick;
        _toolbar.Controls.Add(_slideshowButton);
        buttonLeft += _slideshowButton.PreferredSize.Width + 1;

        _menuButton = MakeBtn("Menu", 55, buttonLeft, buttonTop, "Diplay floating menu...");
        _menuButton.Click += (s, e) => OnShowCoordPanelClick();
        _toolbar.Controls.Add(_menuButton);
        buttonLeft += _menuButton.PreferredSize.Width + 4;
        #endregion Top toolbar

        #region Quality label + combo.
        _toolbar.Controls.Add(new Label { Left = buttonLeft, Top = 4, Width = 1, Height = 30, BackColor = Color.FromArgb(65, 65, 65) });
        buttonLeft += 5;

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
        //_toolbar.Controls.Add(_qualityLabel);
        //buttonLeft += _qualityLabel.PreferredWidth + 3;

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
        buttonLeft += _qualityCombo.PreferredSize.Width + 2;
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
        //_toolbar.Controls.Add(_colorThemeLabel);
        //buttonLeft += _colorThemeLabel.PreferredWidth + 3;
        Models.ColorPalette.LoadUserThemes();
        // Load saved equations so promoted entries appear in the fractal-type
        // combo from launch, not only after a dialog opens them.
        SandboxEquationStore.Instance.Load();
        UserEquationStore.Instance.Load();
        UserBulbStore.Instance.Load();
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
        AttachColorComboSortMenu(_colorThemeCombo, OnColorThemeChanged);
        _toolbar.Controls.Add(_colorThemeCombo);
        buttonLeft += _colorThemeCombo.PreferredSize.Width + 2;

        _editThemeButton = MakeBtn("Edit", 45, buttonLeft, 6, "Edit current colour theme");
        _editThemeButton.Click += OnEditColorThemeClick;
        _toolbar.Controls.Add(_editThemeButton);
        buttonLeft += _editThemeButton.PreferredSize.Width + 2;
        #endregion

        #region Fractal type
        _toolbar.Controls.Add(new Label { Left = buttonLeft, Top = 2, Width = 1, Height = 30, BackColor = Color.FromArgb(60, 60, 60) });
        buttonLeft += 10;

        _fractalTypeLabel = new Label
        {
            Text = "Fractal:",
            Left = buttonLeft,
            Top = 10,
            AutoSize = true,
            ForeColor = Color.FromArgb(155, 155, 155),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            BackColor = Color.Transparent
        };
        //_toolbar.Controls.Add(_fractalTypeLabel);
        //buttonLeft += _fractalTypeLabel.PreferredWidth + 3;

        _fractalTypeCombo = new ComboBox
        {
            Left = buttonLeft,
            Top = 7,
            Width = 130,
            Height = 26,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(45, 45, 45),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9f),
            FlatStyle = FlatStyle.Flat
        };
        PopulateFractalTypeCombo();
        _fractalTypeCombo.SelectedIndex = 0;
        _fractalTypeCombo.SelectedIndexChanged += OnFractalTypeChanged;
        _toolbar.Controls.Add(_fractalTypeCombo);
        buttonLeft += _fractalTypeCombo.PreferredSize.Width + 3;

        var fractalParamsBtn = MakeBtn("Params", 55, buttonLeft, 6, "Edit fractal-specific parameters");
        fractalParamsBtn.Click += OnFractalParamsClick;
        _toolbar.Controls.Add(fractalParamsBtn);
        buttonLeft += fractalParamsBtn.PreferredSize.Width + 2;
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
        //_toolbar.Controls.Add(_regionLabel);
        //buttonLeft += _regionLabel.PreferredWidth + 3;

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
        AttachRegionComboSortMenu(_regionCombo, OnRegionComboChanged,
            onAfterRebuild: () => UpdateDelRegionButton(_regionCombo, _delRegionButton));
        _toolbar.Controls.Add(_regionCombo);
        buttonLeft += _regionCombo.PreferredSize.Width + 3;

        _saveViewButton = MakeBtn("Save", 55, buttonLeft, 6, "Save the current view as a region");
        _saveViewButton.Click += OnSaveViewClick;
        _toolbar.Controls.Add(_saveViewButton);
        buttonLeft += _saveViewButton.PreferredSize.Width + 2;

        _delRegionButton = MakeBtn("Delete", 55, buttonLeft, 6, "Delete the selected region");
        _delRegionButton.Click += OnDelRegionClick;
        //_toolbar.Controls.Add(_delRegionButton);
        buttonLeft += _delRegionButton.PreferredSize.Width + 2;
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
        var navigateItem = new ToolStripMenuItem("Menu");
        var statusItem = new ToolStripMenuItem("Status", null, (s, e) => _footerPanel.Visible = !_footerPanel.Visible);
        var onTopItem = new ToolStripMenuItem("On Top", null, (s, e) => TopMost = !TopMost);
        var miniModeItem = new ToolStripMenuItem("Mini Mode", null, (s, e) =>
        {
            bool wasMini = _miniMode;
            _miniMode = !_miniMode;

            if (_miniMode)
            {
                _miniPreviousBorderStyle = FormBorderStyle;
                _miniPreviousSize = Size;
                TopMost = true;  // mini mode is meant for keeping the window visible while doing other things, so force it on top
                _toolbar.Visible = false;
            }
            _miniClick = true;
            OnFormResize(s, e);  // adjust size and borders
            if (wasMini && !_miniMode)
                CenterToScreen();  // re-center when exiting mini mode since we likely moved the window around while in mini mode
            _miniClick = false;
        });
        var gridItem = new ToolStripMenuItem("Grid", null, (s, e) => OnCheckBoxShowGridClick(s, e));

        var watermarkItem = new ToolStripMenuItem("Slideshow: Toggle Watermark", null, (s, e) =>
        _showSlideshowWatermark = !_showSlideshowWatermark)
        { Enabled = false };
        var spanMonitorsItem = new ToolStripMenuItem("Span Monitors", null, (s, e) => OnSpanMonitorsClick(s, e));
        var restoreMonitorsItem = new ToolStripMenuItem("Restore Monitors", null, (s, e) => OnSpanMonitorsClick(s, e))
        { Visible = false };
        var slideshowItem = new ToolStripMenuItem("Start Slideshow", null, (s, e) => OnSlideshowClick(s, e));
        var videoActivateItem = new ToolStripMenuItem("Video", null, (s, e) => OnVideoClick(s, e));
        var videoSlideshowItem = new ToolStripMenuItem("Start Video Slideshow", null, (s, e) =>
        {
            if (_videoSlideshowRunning) StopVideoSlideshow();
            else StartVideoSlideshow();
        });
        var slideshowExtremeRegionsItem = new ToolStripMenuItem("Slideshow: Use Extreme Regions", null, (s, e) =>
        FractalRegionLibrary.Instance.IncludeExtremeInAll = !FractalRegionLibrary.Instance.IncludeExtremeInAll);
        var slideshowFocusItem = new ToolStripMenuItem("Slideshow: More Colors", null, (s, e) =>
        {
            if (_slideshowRunning)
            {
                _slideshowFocusRegion = !_slideshowFocusRegion;
            }
        })
        { Enabled = false };
        var skipItem = new ToolStripMenuItem("Slideshow: Skip to Next Region", null, (s, e) => SkipSlideshowRegion())
        { Enabled = false };
        var skipThemeItem = new ToolStripMenuItem("Slideshow: Skip to Next Color Theme", null, (s, e) => SkipSlideshowTheme())
        { Enabled = false };
        var pauseItem = new ToolStripMenuItem("Slideshow: Pause", null, (s, e) => ToggleSlideshowPause())
        { Enabled = false };
        var slideshowLockRegionItem = new ToolStripMenuItem("Slideshow: Lock Region", null, (s, e) =>
        {
            ToggleSlideshowRegionLock();
        });
        var miniMapItem = new ToolStripMenuItem("Mini Map", null, (s, e) => ToggleMiniMap());
        var miniDepthItem = new ToolStripMenuItem("Mini Depth", null, (s, e) => ToggleMiniDepth());
        var systemInfoItem = new ToolStripMenuItem("System Info…", null, (s, e) => ShowSystemInfoDialog());
        var helpItem = new ToolStripMenuItem("Help…", null, (s, e) => OnShowHelpClick());
        var saveRegionItem = new ToolStripMenuItem("Save Current Region", null, (s, e) => OnSaveViewClick(s, e));
        var resetViewItem = new ToolStripMenuItem("Reset View", null, (s, e) => OnResetClick(s, e));
        var saveImageItem = new ToolStripMenuItem("Save Image…", null, (s, e) => OnScreenshotClick(s, e));

        contextMenu.Opening += (s, e) =>
        {
            // In 3D modes, right-click is overloaded for camera rotate.
            // Suppress menu if the user held >1s or moved beyond a small
            // dead-zone — both indicate a drag, not a click.
            if (Is3DFractalType(_currentFractalType))
            {
                var heldMs = (DateTime.UtcNow - _rightDownTimeUtc).TotalMilliseconds;
                var cur = _renderPanel.PointToClient(Cursor.Position);
                int dx = cur.X - _rightDragStart.X;
                int dy = cur.Y - _rightDragStart.Y;
                bool moved = (dx * dx + dy * dy) > (RightMoveSuppressPx * RightMoveSuppressPx);
                if (heldMs > RightHoldSuppressMs || moved)
                {
                    e.Cancel = true;
                    return;
                }
            }

            statusItem.Checked = _footerPanel.Visible;
            statusItem.Checked = _footerPanel.Visible;
            toolbarItem.Checked = _toolbar.Visible;

            spanMonitorsItem.Visible = !_spanning;
            spanMonitorsItem.Enabled = !_miniMode;
            restoreMonitorsItem.Visible = _spanning;
            restoreMonitorsItem.Checked = _spanning;

            onTopItem.Checked = TopMost;
            gridItem.Checked = _gridVisible;
            miniModeItem.Enabled = !_spanning;
            miniModeItem.Checked = _miniMode;
            miniMapItem.Checked = _miniMapPanel?.Visible ?? false;
            miniMapItem.Enabled = !_miniMode;  // mini map doesn't work well in mini mode since it's already small and has no extra space for the inset
            miniMapItem.Visible = !_miniMode;  // hide mini map option in mini mode since it doesn't work well there
            miniDepthItem.Checked = _miniDepthPanel?.Visible ?? false;
            miniDepthItem.Enabled = !_miniMode;
            miniDepthItem.Visible = !_miniMode;

            skipItem.Enabled = _slideshowRunning;
            skipThemeItem.Enabled = _slideshowRunning;
            pauseItem.Enabled = _slideshowRunning;
            pauseItem.Checked = _slideshowRunning && IsSlideshowPaused();
            pauseItem.Text = (_slideshowRunning && IsSlideshowPaused()) ? "Slideshow: Resume" : "Slideshow: Pause";
            slideshowItem.Enabled = !_videoRunning && !_videoSlideshowRunning;
            slideshowItem.Checked = _slideshowRunning;
            slideshowLockRegionItem.Enabled = _slideshowRunning && !_videoRunning && !_videoSlideshowRunning;
            slideshowLockRegionItem.Checked = _slideShowLockRegion;
            watermarkItem.Enabled = _slideshowRunning;
            watermarkItem.Checked = _slideshowRunning && _showSlideshowWatermark;
            slideshowItem.Text = _slideshowRunning ? "Stop Slideshow" : "Start Slideshow";
            slideshowFocusItem.Enabled = _slideshowRunning && !_videoRunning && !_videoSlideshowRunning;
            slideshowFocusItem.Text = _slideshowFocusRegion ? "Slideshow: More Colors" : "Slideshow: More Regions";

            videoActivateItem.Enabled = !_videoSlideshowRunning && !_slideshowRunning && !_videoRunning;
            videoSlideshowItem.Text = _videoSlideshowRunning ? "Stop Video Slideshow" : "Start Video Slideshow";
            videoSlideshowItem.Visible = true;
            videoSlideshowItem.Checked = _videoSlideshowRunning;
            videoSlideshowItem.Enabled = !_slideshowRunning;
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
        contextMenu.Items.Add(slideshowFocusItem);
        contextMenu.Items.Add(watermarkItem);
        contextMenu.Items.Add(slideshowLockRegionItem);
        contextMenu.Items.Add(skipItem);
        contextMenu.Items.Add(skipThemeItem);
        contextMenu.Items.Add(pauseItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(videoActivateItem);
        contextMenu.Items.Add(videoSlideshowItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(saveRegionItem);
        contextMenu.Items.Add(saveImageItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(miniMapItem);
        contextMenu.Items.Add(miniDepthItem);
        contextMenu.Items.Add(systemInfoItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(helpItem);
        _renderPanel.ContextMenuStrip = contextMenu;
        #endregion Context menu for render panel

        // Build list sources for combos that need it.
        BuildResolutionSelection();
        BuildColorThemesSelection();
        navigateItem.Click += (s, e) => OnShowCoordPanelClick();

        #endregion Render panel

        // Docking / Z-order: Fill first, then Top-docked in reverse, footer last.
        Controls.Add(_renderPanel);
        Controls.Add(_toolbar);
        Controls.Add(_footerPanel);

        // ── Events ───────────────────────────────────────────────────────────
        Load += OnLoad;
        Resize += OnFormResize;
        KeyDown += OnKeyDown;
        FormClosing += OnFormClosing;
        Application.Idle += OnApplicationIdle;
    }

    #endregion Constructors

    #region Events

    private void OnLoad(object? sender, EventArgs e)
    {
        FractalRegionLibrary.Instance.Load();
        UserEquationStore.Instance.Load();
        SandboxEquationStore.Instance.Load();
        UserBulbStore.Instance.Load();
        RebuildRegionCombo();
        UserColorThemeLibrary.Instance.UpdateCheck();
        int w = _renderPanel.ClientSize.Width;
        int h = _renderPanel.ClientSize.Height;

        try
        {
            //MARGINS margins = new MARGINS { cxLeftWidth = 0, cxRightWidth = 0, cyTopHeight = 30, cyBottomHeight = -1 };
            //_ = DwmExtendFrameIntoClientArea(Handle, ref margins);

            _renderer = RendererFactory.Create(_renderPanel.Handle, w, h, _forceD3D11);
            _calculator = new MandelbrotCalculator(w, h);
            _escapeCalculator = new EscapeTimeCalculator(w, h);
            _ifsCalculator = new IFSCalculator(w, h);
            _lsystemCalculator = new LSystemCalculator(w, h);
            _attractorCalculator = new AttractorCalculator(w, h);
            _buddhabrotCalculator = new BuddhabrotCalculator(w, h);
            _newtonCalculator = new NewtonCalculator(w, h);
            _userEquationCalculator = new UserEquationCalculator(w, h);
            _mandelbulbCalculator = new MandelbulbCalculator(w, h);
            _sandboxCalculator = new SandboxCalculator(w, h);
            _userBulbCalculator = new UserBulbCalculator(w, h);
            _tearDropCalculator = new TearDropCalculator(w, h);

            if (_defaultColorMap != null)
            {
                _calculator.ColorMap = _defaultColorMap;
                _escapeCalculator.ColorMap = _defaultColorMap;
                _ifsCalculator.ColorMap = _defaultColorMap;
                _lsystemCalculator.ColorMap = _defaultColorMap;
                _attractorCalculator.ColorMap = _defaultColorMap;
                _buddhabrotCalculator.ColorMap = _defaultColorMap;
                _newtonCalculator.ColorMap = _defaultColorMap;
                _userEquationCalculator.ColorMap = _defaultColorMap;
                _mandelbulbCalculator.ColorMap = _defaultColorMap;
                _sandboxCalculator.ColorMap = _defaultColorMap;
                _userBulbCalculator.ColorMap = _defaultColorMap;
                _tearDropCalculator.ColorMap = _defaultColorMap;
            }
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
        _escapeCalculator?.Resize(w, h);
        _ifsCalculator?.Resize(w, h);
        _lsystemCalculator?.Resize(w, h);
        _attractorCalculator?.Resize(w, h);
        _buddhabrotCalculator?.Resize(w, h);
        _newtonCalculator?.Resize(w, h);
        _userEquationCalculator?.Resize(w, h);
        _mandelbulbCalculator?.Resize(w, h);
        _sandboxCalculator?.Resize(w, h);
        _userBulbCalculator?.Resize(w, h);
        _tearDropCalculator?.Resize(w, h);
        ApplyViewState();
        TriggerCalculation();
        PositionGridPanel();
    }

    private void OnApplicationIdle(object? sender, EventArgs e)
    {
        if (!_disposed && _renderer != null) _renderer.Render();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _disposed = true;
        Application.Idle -= OnApplicationIdle;

        _floatingMenu?.Close();
        _floatingHelp?.Close();
        _panStopTimer.Stop();
        _panStopTimer.Dispose();
        StopSlideshow();
        lock (_calcLock) _calcCts?.Cancel();
        lock (_wallpaperLock) _wallpaperCts?.Cancel();

        _renderer?.Dispose();
    }

    private void OnAdjustBrightness(object? s, EventArgs e, object? l)
    {
        if (s != null)
        {
            _brightness = ((TrackBar)s).Value;
            if (l != null) ((Label)l).Text = $"Brightness: {_brightness:+0;-0;0}";
            RepaintWithBrightnessContrast();
        }
    }

    private void OnAdjustContrast(object? s, EventArgs e, object? l)
    {
        if (s != null)
        {
            _contrast = ((TrackBar)s).Value;
            if (l != null) ((Label)l).Text = $"Contrast: {_contrast:+0;-0;0}";
            RepaintWithBrightnessContrast();
        }
    }

    private void OnAdjustHistogramEq(object? s, EventArgs e, object? l)
    {
        if (s == null) return;
        _histogramEq = ((TrackBar)s).Value;
        if (l != null) ((Label)l).Text = $"Adaptive: {_histogramEq}";
        if (_calculator == null || _renderer == null || _disposed) return;
        // Adaptive (histogram equalization) is only implemented for the
        // Mandelbrot engine — other calculators have no equivalent aux buffers
        // to redistribute. The slider is disabled outside Mandelbrot, but if
        // we still receive an event (programmatic theme snap, etc.) just
        // re-upload the active buffer so brightness/contrast/grid remain in sync.
        IFractalCalculator? alt = SelectAltCalculator(_currentFractalType);
        if (alt == null)
        {
            if (_histogramEq > 0)
                _calculator.ApplyHistogramEqualization(_histogramEq / 100.0);
            else
                _calculator.ApplyHistogramEqualization(0.0); // restores identity coloring
            UploadProcessedBuffer(_calculator, _renderer);
        }
        else
        {
            UploadProcessedBuffer(alt.ColorBuffer, alt.Width, alt.Height, _renderer);
        }
    }

    private void OnCheckBoxShowGridClick(object? s, EventArgs e)
    {
        if (s != null)
        {
            _gridVisible = !_gridVisible;
            RepaintWithBrightnessContrast();
        }
    }

    private void OnCheckBoxShowFooterPanelClicked(object? s, EventArgs e)
    {
        if (s != null)
        {
            _footerPanel.Visible = !_footerPanel.Visible;
            ((CheckBox)s).Checked = _footerPanel.Visible;
        }
    }

    private void OnShowCoordPanelClick()
    {
        if (_floatingMenu.IsDisposed)
        {
            SetStatus("Floating menu unavailable.");
            return;
        }

        _showFloatingMenu = true;
        _toolbar.Visible = !_showFloatingMenu;

        _floatingMenu.OnCloseCoordPanelClick += (s, e) => OnCloseCoordPanelClick(s, e);
        _floatingMenu.OnExportColorThemeClick += (s, e) => OnExportColorThemeClick(s, e);
        _floatingMenu.OnFlipClick += (s, e) => OnFlipClick(s, e);
        _floatingMenu.OnGoClick += (s, e) => OnGoClick(s, e);
        _floatingMenu.OnResetClick += (s, e) => OnResetClick(s, e);
        _floatingMenu.OnSpanMonitorsClick += (s, e) => OnSpanMonitorsClick(s, e);
        _floatingMenu.OnPosterClick += (s, e) => OnPosterClick(s, e);
        _floatingMenu.OnSlideshowClick += (s, e) => OnSlideshowClick(s, e);
        _floatingMenu.OnImportColorThemeClick += (s, e) => OnImportColorThemeClick(s, e);
        _floatingMenu.OnDeleteColorThemeClick += (s, e) => OnDeleteColorThemeClick(s, e);
        _floatingMenu.OnLoadColorThemesClick += (s, e) => OnLoadColorThemesClick(s, e);
        _floatingMenu.OnEditColorThemeClick += (s, e) => OnEditColorThemeClick(s, e);
        _floatingMenu.OnColorThemeChanged += (s, e) => OnColorThemeChanged(s, e);
        _floatingMenu.OnCheckBoxShowGridClick += (s, e) => OnCheckBoxShowGridClick(s, e);
        _floatingMenu.OnCheckBoxShowFooterClick += (s, e) => OnCheckBoxShowFooterPanelClicked(s, e);
        _floatingMenu.OnFlipClick += (s, e) => OnFlipClick(s, e);
        _floatingMenu.OnQualityComboChanged += (s, e) => OnQualityComboChanged(s, e);
        _floatingMenu.OnIterLockChanged += (s, e, t, i) => OnIterLockChanged(s, e, t, i);
        _floatingMenu.OnRegionComboChanged += (s, e) => OnRegionComboChanged(s, e);
        _floatingMenu.OnSaveViewClick += (s, e) => OnSaveViewClick(s, e);
        _floatingMenu.OnDelRegionClick += (s, e) => OnDelRegionClick(s, e);
        _floatingMenu.OnExportRegionsClick += (s, e) => OnExportRegionsClick(s, e);
        _floatingMenu.OnImportRegionsClick += (s, e) => OnImportRegionsClick(s, e);
        _floatingMenu.OnSlideshowSettingsClick += (s, e) => ShowSlideshowSettingsDialog();
        _floatingMenu.OnScreenshotClick += (s, e) => OnScreenshotClick(s, e);
        _floatingMenu.OnVideoClick += (s, e) => OnVideoClick(s, e);
        _floatingMenu.OnGridClick += (s, e) => OnCheckBoxShowGridClick(s, e);
        _floatingMenu.OnStatusClick += (s, e) => { _footerPanel.Visible = !_footerPanel.Visible; };
        _floatingMenu.OnBrightnessSlide += (s, e, l) => OnAdjustBrightness(s, e, l);
        _floatingMenu.OnContrastSlide += (s, e, l) => OnAdjustContrast(s, e, l);
        _floatingMenu.OnHistogramEqSlide += (s, e, l) => OnAdjustHistogramEq(s, e, l);
        _floatingMenu.OnTaaAlphaSlide += (s, e, l) => SetVideoTaaAlphaPercent(_floatingMenu.TaaAlphaValue);
        _floatingMenu.OnTaaFadeStartSlide += (s, e, l) => SetVideoTaaFadeStartLog10(_floatingMenu.TaaFadeStartLog10);
        _floatingMenu.OnTaaFadeEndSlide += (s, e, l) => SetVideoTaaFadeEndLog10(_floatingMenu.TaaFadeEndLog10);
        _floatingMenu.OnChangeDimensions += (s, e) => OnChangeDimensions(s, e);
        _floatingMenu.OnHelpClick += (s, e) =>
            {
                OnShowHelpClick();
                _floatingHelp?.ShowEditorTab();
                _floatingHelp?.BringToFront();
            };


        UpdateCoordBoxes();
        if (!_regionCombo.IsDisposed) _floatingMenu.RegionName = _currentRegionName;
        if (!_colorThemeCombo.IsDisposed) _floatingMenu.ColorTheme = _currentColorThemeName;
        if (!_qualityCombo.IsDisposed) _floatingMenu.Quality = _currentQualityName;
        UpdateAdaptiveAvailability();
        _floatingMenu.Show();
        InitializeSlideshowFromDisk();
        InitializeAudioFromDisk();
    }

    /// <summary>
    /// Enables the Adaptive (histogram-eq) slider only for Mandelbrot — other
    /// engines do not implement histogram equalization, so the control is
    /// disabled to make its inapplicability obvious.
    /// </summary>
    private void UpdateAdaptiveAvailability()
    {
        if (_floatingMenu == null || _floatingMenu.IsDisposed) return;
        _floatingMenu.SetAdaptiveEnabled(_currentFractalType == FractalType.Mandelbrot);
    }

    public void OnCloseCoordPanelClick(object? s, EventArgs e)
    {
        if (s != null)
        {
            _floatingMenu.Hide();
            _showFloatingMenu = false;
            _toolbar.Visible = !_showFloatingMenu;
        }
    }

    private void OnShowHelpClick()
    {
        if (_floatingHelp == null || _floatingHelp.IsDisposed)
        {
            _floatingHelp = new FloatingHelp(
                this,
                _programName,
                _programVersion,
                rendererDescriptionProvider: () => _renderer?.RendererDescription ?? "none",
                calculatorInfoProvider: () => _calculator == null
                    ? null
                    : (_calculator.Width, _calculator.Height,
                       _calculator.MaxIterations, _calculator.IsHighPrecisionActive));
            _floatingHelp.OnCloseHelpClick += (s, e) => _floatingHelp?.Hide();
        }
        _floatingHelp.Show();
        _floatingHelp.BringToFront();
    }

    private void OnEditColorThemeClick(object? sender, EventArgs e)
    {
        if (_colorThemeEditor == null || _colorThemeEditor.IsDisposed)
        {
            _colorThemeEditor = new ColorThemeEditor(
                this,
                initialThemeName: _currentColorThemeName,
                initialRegionName: _currentRegionName);

            _colorThemeEditor.OnPreviewThemeChanged += data => ApplyPreviewTheme(data);
            _colorThemeEditor.OnRegionSelected += name =>
            {
                JumpToRegion(name);
                _currentRegionName = name;
                SyncRegionCombos(name);
            };
            _colorThemeEditor.OnEditorThemeSelected += name =>
            {
                // Editor is authoritative while open — update committed name
                // and mirror into both toolbar + floating menu combos. The
                // calculator's color map is driven by the editor's preview
                // pipeline (OnPreviewThemeChanged), not by combo selection.
                _currentColorThemeName = name;
                SyncThemeCombos(name);
            };
            _colorThemeEditor.OnHelpClick += (s, e) =>
            {
                OnShowHelpClick();
                _floatingHelp?.ShowEditorTab();
                _floatingHelp?.BringToFront();
            };
            _colorThemeEditor.OnThemeSavedToLibrary += name =>
            {
                // Rebuild combo (Reload path) and select the newly-saved theme,
                // which commits it as the active map and clears the preview flag.
                BuildColorThemesSelection();
                int idx = _colorThemeCombo.FindStringExact(name);
                if (idx >= 0)
                {
                    _colorThemeCombo.SelectedIndex = idx;
                }
                _previewingTransientMap = false;
                _currentColorThemeName = name;
            };
            _colorThemeEditor.FormClosed += (s, ev) =>
            {
                ClearPreview();
                _colorThemeEditor = null;
            };
        }
        _colorThemeEditor.Show();
        _colorThemeEditor.BringToFront();
    }

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
        _miniDepthPanel?.RequestRedraw();
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

        var altForName = SelectAltCalculator(_currentFractalType);
        double fnCx = altForName?.CenterX ?? _centerX;
        double fnCy = altForName?.CenterY ?? _centerY;
        double fnZoom = altForName?.Zoom ?? _zoom;
        int fnIter = altForName?.MaxIterations ?? (_calculator?.MaxIterations ?? 0);

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
            FileName = $"{_programName.Replace(" ", "")}" +
                        $"_{CurrentFractalTypeName().Replace("/", "").Replace("\\", "")}" +
                        $"_{colorName.Replace(" ", "").Replace("/", "").Replace("\\", "")}" +
                        $"_{regionName.Replace(" ", "").Replace("/", "").Replace("\\", "")}" +
                        $"x{fnCx.ToString("R", System.Globalization.CultureInfo.InvariantCulture).Replace(".", "")}_" +
                        $"y{fnCy.ToString("R", System.Globalization.CultureInfo.InvariantCulture).Replace(".", "")}_" +
                        $"z{fnZoom.ToString("R", System.Globalization.CultureInfo.InvariantCulture).Replace(".", "")}_" +
                        $"i{fnIter.ToString(System.Globalization.CultureInfo.InvariantCulture)}_" +
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
        _centerY2 = -_centerY2;
        _centerY3 = -_centerY3;

        if (_floatingMenu != null &&
            !_floatingMenu.IsDisposed)
        {
            _floatingMenu.CY = FormatCoordSingle(_centerY, _centerYLo, _centerY2, _centerY3);
        }

        OnGoClick(sender, e);
    }

    private void OnQualityComboChanged(object? sender, EventArgs e)
    {
        if (sender == null) return;

        ComboBox combo = (ComboBox)sender;
        _currentQualitySelection = combo.SelectedIndex;
        if (_currentQualitySelection < 0 ||
            _currentQualitySelection >= QualityPreset.All.Length) return;

        QualityPreset newQuality = QualityPreset.All[_currentQualitySelection];
        _quality = newQuality;
        _currentQualityName = _quality.Name;

        double prevZoom = _zoom;
        _zoom = System.Math.Clamp(_zoom, _quality.ZoomMin, _quality.ZoomMax);

        if (_calculator != null)
            _calculator.Quality = _quality;

        ApplyViewState();
        TriggerCalculation();

        if (prevZoom > _quality.ZoomMax)
            SetStatus($"Quality → {_quality.Name}.  Zoom clamped to {_quality.ZoomMax:G3}.");
    }

    /// <summary>
    /// Number of hard-coded FractalType entries in the dropdown (indices 0..N-1).
    /// Indices beyond this point are registered user equations from
    /// <see cref="RegisteredFractalCatalog"/>, separated by a non-selectable
    /// divider header.
    /// </summary>
    private const int BuiltInFractalCount = 16;

    /// <summary>
    /// Repopulates the fractal-type combo with built-in entries followed by a
    /// "— Registered —" divider and all promoted user equations. Suppresses
    /// the SelectedIndexChanged event during rebuild so the active fractal
    /// type is preserved when possible.
    /// </summary>
    private void PopulateFractalTypeCombo()
    {
        if (_fractalTypeCombo == null) return;

        _fractalTypeCombo.SelectedIndexChanged -= OnFractalTypeChanged;
        try
        {
            string? prevName = _fractalTypeCombo.SelectedItem?.ToString();
            _fractalTypeCombo.Items.Clear();
            _fractalTypeCombo.Items.AddRange(new object[]
            {
                "Mandelbrot",
                "Julia",
                "Burning Ship",
                "Tricorn",
                "Multibrot",
                "Phoenix",
                "Newton",
                "Buddhabrot",
                "IFS",
                "L-System",
                "Strange Attractor",
                "User Equation",
                "Mandelbulb (3D)",
                "Sandbox",
                "User Bulb (3D)",
                "Tear Drop",
            });

            var registered = RegisteredFractalCatalog.Snapshot();
            if (registered.Count > 0)
            {
                _fractalTypeCombo.Items.Add("— Registered —");
                foreach (var r in registered) _fractalTypeCombo.Items.Add(r.Name);
            }

            if (!string.IsNullOrEmpty(prevName))
            {
                int idx = _fractalTypeCombo.FindStringExact(prevName);
                if (idx >= 0) _fractalTypeCombo.SelectedIndex = idx;
            }
        }
        finally { _fractalTypeCombo.SelectedIndexChanged += OnFractalTypeChanged; }
    }

    private void OnFractalTypeChanged(object? sender, EventArgs e)
    {
        if (_fractalTypeCombo == null) return;

        int idx = _fractalTypeCombo.SelectedIndex;
        if (idx < 0) return;

        // Index BuiltInFractalCount is the "— Registered —" divider header.
        // Selecting it is a no-op; bounce back to the prior selection.
        if (idx == BuiltInFractalCount)
        {
            _fractalTypeCombo.SelectedIndexChanged -= OnFractalTypeChanged;
            try { _fractalTypeCombo.SelectedIndex = ComboIndexForFractalType(_currentFractalType); }
            finally { _fractalTypeCombo.SelectedIndexChanged += OnFractalTypeChanged; }
            return;
        }

        FractalType sel;
        RegisteredFractal? promoted = null;

        if (idx > BuiltInFractalCount)
        {
            // Registered entry — resolve by display name, load its source
            // into the appropriate FractalParameters slot, then dispatch.
            string name = _fractalTypeCombo.Items[idx]?.ToString() ?? string.Empty;
            promoted = RegisteredFractalCatalog.GetByName(name);
            if (promoted == null) return;
            sel = promoted.Type;
        }
        else
        {
            sel = idx switch
            {
                0 => FractalType.Mandelbrot,
                1 => FractalType.Julia,
                2 => FractalType.BurningShip,
                3 => FractalType.Tricorn,
                4 => FractalType.Multibrot,
                5 => FractalType.Phoenix,
                6 => FractalType.Newton,
                7 => FractalType.BuddhaBrot,
                8 => FractalType.IFS,
                9 => FractalType.LSystem,
                10 => FractalType.StrangeAttractor,
                11 => FractalType.UserEquation,
                12 => FractalType.Mandelbulb,
                13 => FractalType.Sandbox,
                14 => FractalType.UserBulb,
                15 => FractalType.TearDrop,
                _ => FractalType.Mandelbrot
            };
        }

        // Apply the promoted entry's source/name and force a recompile.
        // Calculators do not poll FractalParameters once they have a compiled
        // delegate, so the source change must be pushed explicitly.
        if (promoted != null)
        {
            if (promoted.Engine == EquationEngine.Sandbox)
            {
                _fractalParams.SandboxSource = promoted.Source;
                _fractalParams.SandboxName = promoted.Name;
                _sandboxCalculator?.Compile(promoted.Source);
            }
            else if (promoted.Engine == EquationEngine.UserBulb)
            {
                _fractalParams.UserBulbSource = promoted.Source;
                _fractalParams.UserBulbName = promoted.Name;
                _userBulbCalculator?.Compile(promoted.Source);
            }
            else
            {
                _fractalParams.UserEquationSource = promoted.Source;
                _fractalParams.UserEquationName = promoted.Name;
                _userEquationCalculator?.Compile(promoted.Source);
            }
        }

        if (sel == _currentFractalType && promoted == null) return;
        _currentFractalType = sel;
        UpdateAdaptiveAvailability();

        // Reset view to a fractal-appropriate default so the new render
        // shows something meaningful rather than the inherited Mandelbrot view.
        (_centerX, _centerY, _zoom) = sel switch
        {
            FractalType.Mandelbrot => (-0.5, 0.0, 1.0),
            FractalType.Julia => (0.0, 0.0, 1.0),
            FractalType.BurningShip => (-0.5, -0.5, 1.0),
            FractalType.Tricorn => (0.0, 0.0, 1.0),
            FractalType.Multibrot => (0.0, 0.0, 1.0),
            FractalType.Phoenix => (0.0, 0.0, 1.5),
            FractalType.Newton => (0.0, 0.0, 1.0),
            FractalType.BuddhaBrot => (-0.5, 0.0, 1.0),
            FractalType.IFS => (0.0, 0.0, 1.0),
            FractalType.LSystem => (0.0, 0.0, 1.0),
            FractalType.StrangeAttractor => (0.0, 0.0, 1.0),
            FractalType.UserEquation => (0.0, 0.0, 1.0),
            FractalType.Mandelbulb => (0.0, 0.0, 1.0),
            FractalType.Sandbox => (0.0, 0.0, 1.0),
            FractalType.UserBulb => (0.0, 0.0, 1.0),
            FractalType.TearDrop => (0.0, 0.0, 0.16),
            _ => (-0.5, 0.0, 1.0)
        };
        _centerXLo = _centerX2 = _centerX3 = 0.0;
        _centerYLo = _centerY2 = _centerY3 = 0.0;
        _lastUploadedBuffer = null;

        // Refresh the suggested-themes section for the new fractal type.
        if (sel == FractalType.Sandbox) ApplySandboxEquationSuggestions();
        else if (sel == FractalType.UserEquation) ApplyUserEquationSuggestions();
        else ApplyEquationProfileToCombos(null);

        ApplyViewState();
        TriggerCalculation();
        _miniMapPanel?.RequestRedraw();
    }

    /// <summary>
    /// Reverse map of the FractalType → combo-index switch in <see cref="OnFractalTypeChanged"/>.
    /// Nova has no dedicated combo entry; it falls back to Newton (same calculator path).
    /// </summary>
    private static int ComboIndexForFractalType(FractalType t) => t switch
    {
        FractalType.Mandelbrot => 0,
        FractalType.Julia => 1,
        FractalType.BurningShip => 2,
        FractalType.Tricorn => 3,
        FractalType.Multibrot => 4,
        FractalType.Phoenix => 5,
        FractalType.Newton => 6,
        FractalType.Nova => 6,
        FractalType.BuddhaBrot => 7,
        FractalType.IFS => 8,
        FractalType.LSystem => 9,
        FractalType.StrangeAttractor => 10,
        FractalType.UserEquation => 11,
        FractalType.Mandelbulb => 12,
        FractalType.Sandbox => 13,
        FractalType.UserBulb => 14,
        FractalType.TearDrop => 15,
        _ => 0
    };

    /// <summary>
    /// Programmatic fractal-type switch used when applying a region. Updates the toolbar combo
    /// without firing <see cref="OnFractalTypeChanged"/> (which would clobber the region's coords
    /// with the fractal-default view) and invalidates the cached upload buffer.
    /// </summary>
    private void SwitchFractalTypeForRegion(FractalType t)
    {
        _currentFractalType = t;
        UpdateAdaptiveAvailability();
        if (_fractalTypeCombo != null && !_fractalTypeCombo.IsDisposed)
        {
            int idx = ComboIndexForFractalType(t);
            _fractalTypeCombo.SelectedIndexChanged -= OnFractalTypeChanged;
            try { if (idx >= 0 && idx < _fractalTypeCombo.Items.Count) _fractalTypeCombo.SelectedIndex = idx; }
            finally { _fractalTypeCombo.SelectedIndexChanged += OnFractalTypeChanged; }
        }
        _lastUploadedBuffer = null;
    }

    private Views.FractalParamsDialog? _paramsDialog;

    private void OnFractalParamsClick(object? sender, EventArgs e)
    {
        // UserEquation has its own editor with a multiline source textbox.
        if (_currentFractalType == FractalType.UserEquation)
        {
            ShowUserEquationDialog();
            return;
        }

        if (_currentFractalType == FractalType.Sandbox)
        {
            ShowSandboxDialog();
            return;
        }

        if (_currentFractalType == FractalType.UserBulb)
        {
            ShowUserBulbDialog();
            return;
        }

        // Modeless dialog with live updates — re-render fires on every
        // control change so the user sees parameter sweeps in real time.
        if (_paramsDialog != null && !_paramsDialog.IsDisposed)
        {
            // Close + reopen for the new type if the user switched fractals.
            if (_paramsDialog.Tag is FractalType t && t == _currentFractalType)
            {
                _paramsDialog.BringToFront();
                _paramsDialog.Activate();
                return;
            }
            _paramsDialog.Close();
            _paramsDialog = null;
        }

        var dlg = new Views.FractalParamsDialog(_currentFractalType, _fractalParams) { Tag = _currentFractalType };
        // Position next to the toolbar.
        var loc = PointToScreen(new Point(_toolbar.Right - dlg.Width - 10, _toolbar.Bottom + 10));
        dlg.Location = loc;
        dlg.ParamChanged += () =>
        {
            if (_currentFractalType != FractalType.Mandelbrot)
            {
                _lastUploadedBuffer = null;
                TriggerCalculation();
            }
            _miniMapPanel?.RequestRedraw();
        };
        dlg.FormClosed += (_, _) => { _paramsDialog = null; };
        _paramsDialog = dlg;
        dlg.Show(this);
    }

    private void ShowUserEquationDialog()
    {
        if (_userEqDialog != null && !_userEqDialog.IsDisposed)
        {
            _userEqDialog.BringToFront();
            _userEqDialog.Activate();
            return;
        }

        var dlg = new Views.UserEquationDialog(_fractalParams);
        var loc = PointToScreen(new Point(_toolbar.Right - dlg.Width - 10, _toolbar.Bottom + 10));
        dlg.Location = loc;
        dlg.CompileRequested += () =>
        {
            if (_userEquationCalculator == null) return;
            _userEquationCalculator.Compile(_fractalParams.UserEquationSource ?? "return z*z + c;");
            dlg.ShowError(_userEquationCalculator.LastError);
            if (_userEquationCalculator.IsCompiled)
            {
                _lastUploadedBuffer = null;
                ApplyUserEquationSuggestions();
                TriggerCalculation();
            }
        };
        dlg.PromotionChanged += () => PopulateFractalTypeCombo();
        dlg.RenderRequested += () =>
        {
            if (_userEquationCalculator == null) return;
            _lastUploadedBuffer = null;
            TriggerCalculation();
        };
        dlg.FormClosed += (_, _) => { _userEqDialog = null; };
        _userEqDialog = dlg;
        dlg.Show(this);
        // Trigger initial compile.
        dlg.TriggerCompile();
    }

    private void ShowUserBulbDialog()
    {
        if (_userBulbDialog != null && !_userBulbDialog.IsDisposed)
        {
            _userBulbDialog.BringToFront();
            _userBulbDialog.Activate();
            return;
        }

        var dlg = new Views.UserBulbDialog(_fractalParams);
        var loc = PointToScreen(new Point(_toolbar.Right - dlg.Width - 10, _toolbar.Bottom + 10));
        dlg.Location = loc;
        dlg.CompileRequested += () =>
        {
            if (_userBulbCalculator == null) return;
            _userBulbCalculator.Compile(_fractalParams.UserBulbSource ?? string.Empty);
            dlg.ShowError(_userBulbCalculator.LastError);
            if (_userBulbCalculator.IsCompiled)
            {
                _lastUploadedBuffer = null;
                TriggerCalculation();
            }
        };
        dlg.RenderRequested += () =>
        {
            if (_userBulbCalculator == null) return;
            _lastUploadedBuffer = null;
            TriggerCalculation();
        };
        dlg.PromotionChanged += () => PopulateFractalTypeCombo();
        dlg.ExportMeshRequested += (n, range, path) =>
        {
            if (_userBulbCalculator == null) return;
            try
            {
                int tris = FracturingFog.Export.UserBulbMeshExporter.ExportObjVoxelSurface(
                    path,
                    (x, y, z) => _userBulbCalculator.SampleDE(x, y, z),
                    _userBulbCalculator.CenterX, -_userBulbCalculator.CenterY, 0,
                    range, n);
                MessageBox.Show(this, $"Exported {tris} triangles to {path}", "Mesh export");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Export failed: {ex.Message}", "Mesh export error");
            }
        };
        dlg.FormClosed += (_, _) => { _userBulbDialog = null; };
        _userBulbDialog = dlg;
        dlg.Show(this);
        dlg.TriggerCompile();
    }

    private void ShowSandboxDialog()
    {
        if (_sandboxDialog != null && !_sandboxDialog.IsDisposed)
        {
            _sandboxDialog.BringToFront();
            _sandboxDialog.Activate();
            return;
        }

        var dlg = new Views.SandboxDialog(_fractalParams);
        var loc = PointToScreen(new Point(_toolbar.Right - dlg.Width - 10, _toolbar.Bottom + 10));
        dlg.Location = loc;
        dlg.CompileRequested += () =>
        {
            if (_sandboxCalculator == null) return;
            _sandboxCalculator.Compile(_fractalParams.SandboxSource ?? "z*z + c");
            dlg.ShowError(_sandboxCalculator.LastError);
            if (_sandboxCalculator.IsCompiled)
            {
                _lastUploadedBuffer = null;
                ApplySandboxEquationSuggestions();
                TriggerCalculation();
            }
        };
        dlg.PromotionChanged += () => PopulateFractalTypeCombo();
        dlg.FormClosed += (_, _) => { _sandboxDialog = null; };
        _sandboxDialog = dlg;
        dlg.Show(this);
        dlg.TriggerCompile();
    }

    private void OnColorThemeChanged(object? sender, EventArgs e)
    {
        if (sender != null)
        {
            ComboBox _cb = (ComboBox)sender;
            _currentColorThemeName = _cb.SelectedItem?.ToString() ?? "";
            _currentColorThemeLabel?.Text = _currentColorThemeName;
            _currentColorThemeSelection = _cb.SelectedIndex;
            var map = Models.ColorPalette.GetPaletteByName(_currentColorThemeName);
            if (_calculator != null)
            {
                _calculator.ColorMap = map;
                // Snap post-FX sliders (Brightness/Contrast/Adaptive) if this
                // theme carries defaults. Built-in themes return null fields
                // via Export, so the sliders stay where the user left them.
                var data = DataDrivenColorThemes.Export(map);
                ApplyThemePostFx(data);
                TriggerCalculation();
            }
            _miniMapPanel?.RequestRedraw();
            _miniDepthPanel?.RequestRedraw();

            // Sync the other combo (toolbar vs floating menu) so both
            // surfaces always show the same active theme, regardless of which
            // one the user clicked.
            SyncThemeCombos(_currentColorThemeName, exclude: _cb);
        }
    }

    /// <summary>
    /// Mirrors the named theme into both the toolbar combo and the
    /// FloatingMenu combo, suppressing each combo's change handler so the
    /// sync does not re-enter OnColorThemeChanged. Pass the originating
    /// combo as <paramref name="exclude"/> to skip touching it (it already
    /// has the right value).
    /// </summary>
    private void SyncThemeCombos(string name, ComboBox? exclude = null)
    {
        if (_colorThemeCombo != null && !_colorThemeCombo.IsDisposed && _colorThemeCombo != exclude)
        {
            _colorThemeCombo.SelectedIndexChanged -= OnColorThemeChanged;
            try
            {
                int idx = _colorThemeCombo.FindStringExact(name ?? string.Empty);
                if (idx >= 0 && _colorThemeCombo.SelectedIndex != idx)
                    _colorThemeCombo.SelectedIndex = idx;
            }
            finally
            {
                _colorThemeCombo.SelectedIndexChanged += OnColorThemeChanged;
            }
        }
        if (_floatingMenu != null && !_floatingMenu.IsDisposed)
            _floatingMenu.SetThemeSilent(name ?? string.Empty);
    }

    /// <summary>
    /// Mirrors the named region into both region combos without re-firing
    /// OnRegionComboChanged. See <see cref="SyncThemeCombos"/>.
    /// </summary>
    private void SyncRegionCombos(string name, ComboBox? exclude = null)
    {
        if (_regionCombo != null && !_regionCombo.IsDisposed && _regionCombo != exclude)
        {
            _regionCombo.SelectedIndexChanged -= OnRegionComboChanged;
            try
            {
                int idx = _regionCombo.FindStringExact(name ?? string.Empty);
                if (idx >= 0 && _regionCombo.SelectedIndex != idx)
                    _regionCombo.SelectedIndex = idx;
            }
            finally
            {
                _regionCombo.SelectedIndexChanged += OnRegionComboChanged;
            }
        }
        if (_floatingMenu != null && !_floatingMenu.IsDisposed)
            _floatingMenu.SetRegionSilent(name ?? string.Empty);
    }

    private void OnResetClick(object? sender, EventArgs e)
    {
        StopSlideshow();

        // 3D modes need a different default centre/zoom than the Mandelbrot
        // (-0.5, 0, 0.13) — the bulb is at origin and camDist = baseDist / zoom
        // so a 0.13 zoom pushes the camera way past the bulb and the offset
        // centre shifts the fractal off-screen, producing a black frame.
        bool is3D = _currentFractalType is FractalType.Mandelbulb or FractalType.UserBulb;
        if (is3D)
        {
            _centerX = 0.0; _centerY = 0.0; _zoom = 1.0;
        }
        else
        {
            _centerX = DefaultCenterX;
            _centerY = DefaultCenterY;
            _zoom = DefaultZoom;
        }
        _centerXLo = 0.0; _centerX2 = 0.0; _centerX3 = 0.0;
        _centerYLo = 0.0; _centerY2 = 0.0; _centerY3 = 0.0;
        _regionCombo.SelectedIndex = 0;

        // Reset brightness and contrast to defaults.
        _brightness = 0;
        _contrast = 0;
        if (_floatingMenu != null) _floatingMenu.ResetView(_centerX, _centerY, _zoom);

        // 3D modes: restore camera + light to FractalParameters defaults so the
        // bulb is framed in view. Without this, an off-axis camera left over from
        // previous interaction can hide the fractal even after a reset.
        if (_currentFractalType == FractalType.Mandelbulb)
        {
            _fractalParams.BulbCameraDistance = 3.0;
            _fractalParams.BulbCameraTheta = Math.PI * 0.25;
            _fractalParams.BulbCameraPhi = Math.PI * 0.35;
            _fractalParams.BulbLightTheta = Math.PI * 0.25;
            _fractalParams.BulbLightPhi = Math.PI * 0.45;
            _lastUploadedBuffer = null;
        }
        else if (_currentFractalType == FractalType.UserBulb)
        {
            _fractalParams.UserBulbCameraDistance = 3.0;
            _fractalParams.UserBulbCameraTheta = Math.PI * 0.25;
            _fractalParams.UserBulbCameraPhi = Math.PI * 0.35;
            _fractalParams.UserBulbLightTheta = Math.PI * 0.25;
            _fractalParams.UserBulbLightPhi = Math.PI * 0.45;
            _lastUploadedBuffer = null;
        }

        ApplyViewState();
        TriggerCalculation();
    }

    private void OnIterLockChanged(object? s, EventArgs e, object? t, int iterations)
    {
        _iterLocked = !_iterLocked;
        if (_iterLocked && _calculator != null)
        {
            _lockedIterations = iterations;
            // Capture current iteration value when lock is engaged.
            //if (int.TryParse(_txIter.Text.Trim(), out int parsed) && parsed >= 64)
            //    _lockedIterations = parsed;
            //else
            //    _lockedIterations = _calculator.MaxIterations;
            if (t != null && t.GetType() == typeof(TextBox)) ((TextBox)t).BackColor = Color.FromArgb(55, 50, 30);   // tinted to show locked state
        }
        else
        {
            if (t != null && t.GetType() == typeof(TextBox)) ((TextBox)t).BackColor = Color.FromArgb(40, 40, 40);    // restore normal colour
        }
    }

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

        if (_floatingMenu != null &&
            !_floatingMenu.IsDisposed)
        {

            // Textboxes now carry the full QD value as "Hi|Lo|X2|X3" (FormatCoord).
            // Parse all limbs so that an unedited box preserves deep-zoom precision
            // and a pasted full-precision string is honoured without zeroing Lo/X2/X3.
            if (TryParseQDCoord(_floatingMenu.CX, out double newCX, out double newCXLo,
                                 out double newCX2, out double newCX3))
            {
                _centerX = newCX; _centerXLo = newCXLo; _centerX2 = newCX2; _centerX3 = newCX3;
            }
            if (TryParseQDCoord(_floatingMenu.CY, out double newCY, out double newCYLo,
                                 out double newCY2, out double newCY3))
            {
                _centerY = newCY; _centerYLo = newCYLo; _centerY2 = newCY2; _centerY3 = newCY3;
            }

            // Auto-promote quality preset when typed/pasted zoom exceeds current ZoomMax,
            // so deep coords aren't silently clamped to a shallow render. Paste only
            // promotes — never demotes — to avoid surprising the user mid-edit.
            if (zoom > _quality.ZoomMax && AdaptQualityForZoom(zoom))
                SetStatus($"Quality → {_quality.Name} (zoom {zoom:G3} requires it).");
            _zoom = System.Math.Clamp(zoom, _quality.ZoomMin, _quality.ZoomMax);
            _floatingMenu.Zoom = _zoom;

            if (_calculator != null && iter > 0)
                _calculator.MaxIterations = iter;

            // When "Go" is pressed while locked, update the locked value too.
            if (_iterLocked)
                _lockedIterations = iter;

            ApplyViewState(iter);
            TriggerCalculation();
        }
    }

    private void OnRegionComboChanged(object? sender, EventArgs e)
    {
        if (sender != null)
        {
            ComboBox _cb = (ComboBox)sender;
            UpdateDelRegionButton(_cb, _delRegionButton);

            _currentRegionName = _cb.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(_currentRegionName) ||
                _currentRegionName == "— select region —") return;

            var region = JumpToRegion(_currentRegionName);
            if (region != null)
                _toolTip.SetToolTip(_cb, region.Description);

            SyncRegionCombos(_currentRegionName, exclude: _cb);
        }
    }

    /// <summary>
    /// Applies the named region to the view and triggers a re-render. Returns
    /// the resolved region (or null if not found / sentinel item). Shared by
    /// the FloatingMenu region combo and the Color Theme Editor region combo.
    /// </summary>
    public FractalRegion? JumpToRegion(string name)
    {
        if (string.IsNullOrEmpty(name) || name == "— select region —") return null;

        var region = FractalRegionLibrary.Instance.FindByName(name);
        if (region == null) return null;

        ApplyRegion(region);
        TriggerCalculation();
        return region;
    }

    /// <summary>
    /// Injects a transient color theme (built live by the Color Theme Editor)
    /// into the calculator and re-renders. Does not change combo selection.
    /// Applies the theme's post-FX (Brightness / Contrast / Adaptive) when
    /// those fields are non-null and the corresponding slider is not locked.
    /// </summary>
    public void ApplyPreviewTheme(ColorThemeData? data)
    {
        if (data == null || _calculator == null) return;
        var map = DataDrivenColorThemes.Create(data);
        if (map == null) return;
        _previewingTransientMap = true;
        _calculator.ColorMap = map;
        ApplyThemePostFx(data);
        TriggerCalculation();
        _miniMapPanel?.RequestRedraw();
        _miniDepthPanel?.RequestRedraw();
    }

    /// <summary>
    /// Snaps the post-FX sliders (Brightness/Contrast/Adaptive) on the
    /// FloatingMenu to the values carried by <paramref name="data"/>. A null
    /// field on the data means "theme has no opinion" → slider resets to the
    /// neutral default (0). Locked sliders are left alone in either case so
    /// the user can pin a preferred value across theme switches.
    /// </summary>
    private void ApplyThemePostFx(ColorThemeData? data)
    {
        if (_floatingMenu == null || _floatingMenu.IsDisposed) return;

        if (!_floatingMenu.BrightnessLocked)
            _floatingMenu.SetBrightness(data?.Brightness ?? 0);

        if (!_floatingMenu.ContrastLocked)
            _floatingMenu.SetContrast(data?.Contrast ?? 0);

        if (!_floatingMenu.AdaptiveLocked)
            _floatingMenu.SetAdaptive(data?.Adaptive ?? 0);
    }

    /// <summary>
    /// Injects a transient color map (no theme metadata) into the calculator.
    /// Retained for callers that don't have a ColorThemeData on hand.
    /// </summary>
    public void ApplyPreviewMap(Interefaces.IColorMap map)
    {
        if (map == null || _calculator == null) return;
        _previewingTransientMap = true;
        _calculator.ColorMap = map;
        TriggerCalculation();
        _miniMapPanel?.RequestRedraw();
        _miniDepthPanel?.RequestRedraw();
    }

    /// <summary>
    /// Restores the calculator color map to the committed selection (the
    /// one named in <c>_currentColorThemeName</c>) and clears the preview flag.
    /// Called by the editor on close/Revert.
    /// </summary>
    public void ClearPreview()
    {
        if (!_previewingTransientMap || _calculator == null) { _previewingTransientMap = false; return; }
        _previewingTransientMap = false;
        var map = Models.ColorPalette.GetPaletteByName(_currentColorThemeName ?? "");
        _calculator.ColorMap = map;
        TriggerCalculation();
        _miniMapPanel?.RequestRedraw();
        _miniDepthPanel?.RequestRedraw();
    }

    private void OnSaveViewClick(object? sender, EventArgs e)
    {
        // Sync textboxes → internal state so manual edits land in the saved region
        // even when the user hasn't pressed Go first.
        if (_calculator == null) return;
        //if (TryParseQDCoord(_txCX.Text, out double sCX, out double sCXLo,
        //                     out double sCX2, out double sCX3))
        //{ _centerX = sCX; _centerXLo = sCXLo; _centerX2 = sCX2; _centerX3 = sCX3; }
        //if (TryParseQDCoord(_txCY.Text, out double sCY, out double sCYLo,
        //                     out double sCY2, out double sCY3))
        //{ _centerY = sCY; _centerYLo = sCYLo; _centerY2 = sCY2; _centerY3 = sCY3; }

        _centerX = _calculator.CenterX; _centerXLo = _calculator.CenterXLo; _centerX2 = _calculator.CenterX2; _centerX3 = _calculator.CenterX3;
        _centerY = _calculator.CenterY; _centerYLo = _calculator.CenterYLo; _centerY2 = _calculator.CenterY2; _centerY3 = _calculator.CenterY3;
        //var _ic = System.Globalization.CultureInfo.InvariantCulture;
        //var _ns = System.Globalization.NumberStyles.Float;
        //if (double.TryParse(_txZoom.Text.Trim(), _ns, _ic, out double tz) && tz > 0)
        //    _zoom = tz;
        _zoom = _calculator.Zoom;
        //if (int.TryParse(_txIter.Text.Trim(), out int ti) && ti >= 64 && _calculator != null)
        //    _calculator.MaxIterations = ti;


        string suggestedName = BuildSuggestedRegionName();
        using var dlg = new InputDialog("Save Current View", "Region name:", suggestedName);
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
            FractalType = _currentFractalType,
            UserEquationName = _currentFractalType == FractalType.UserEquation
                ? _fractalParams.UserEquationName
                : null,
            SandboxName = _currentFractalType == FractalType.Sandbox
                ? _fractalParams.SandboxName
                : null,
            UserBulbName = _currentFractalType == FractalType.UserBulb
                ? _fractalParams.UserBulbName
                : null,
            UserBulbSource = _currentFractalType == FractalType.UserBulb
                ? _fractalParams.UserBulbSource
                : null,
            UserBulbCameraDistance = _currentFractalType == FractalType.UserBulb
                ? _fractalParams.UserBulbCameraDistance : 0,
            UserBulbCameraTheta = _currentFractalType == FractalType.UserBulb
                ? _fractalParams.UserBulbCameraTheta : 0,
            UserBulbCameraPhi = _currentFractalType == FractalType.UserBulb
                ? _fractalParams.UserBulbCameraPhi : 0,
            UserBulbLightTheta = _currentFractalType == FractalType.UserBulb
                ? _fractalParams.UserBulbLightTheta : 0,
            UserBulbLightPhi = _currentFractalType == FractalType.UserBulb
                ? _fractalParams.UserBulbLightPhi : 0,
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

    private void OnExportRegionsClick(object? sender, EventArgs e)
    {
        // Omit UserEquation and UserBulb (3D) regions — their source isn't
        // portable via a plain regions JSON: UserEquation references by name
        // only, and UserBulb embeds source that isn't useful without the
        // surrounding compile pipeline.
        var userRegions = FractalRegionLibrary.Instance.UserRegions
            .Where(r => r.FractalType != FractalType.UserEquation
                     && r.FractalType != FractalType.UserBulb)
            .ToList();
        if (userRegions.Count == 0)
        {
            MessageBox.Show("There are no exportable custom regions.\n\nUse \"Save View\" to create one first. (User Equation and UserBulb 3D regions are excluded.)",
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

        // Bundle any Sandbox equations referenced by the exported regions so
        // the recipient can recall the saved view without manually copying
        // the equation source.
        var sandboxNames = userRegions
            .Where(r => r.FractalType == FractalType.Sandbox && !string.IsNullOrWhiteSpace(r.SandboxName))
            .Select(r => r.SandboxName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sandboxEquations = new List<SandboxEquationEntry>();
        foreach (var name in sandboxNames)
        {
            var entry = SandboxEquationStore.Instance.GetByName(name);
            if (entry != null)
                sandboxEquations.Add(new SandboxEquationEntry
                {
                    Name = entry.Name,
                    Source = entry.Source,
                    Promoted = entry.Promoted
                });
        }

        var bundle = new RegionExportBundle
        {
            Version = 2,
            Regions = userRegions,
            SandboxEquations = sandboxEquations
        };

        try
        {
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(bundle, opts));
            string suffix = sandboxEquations.Count > 0
                ? $" + {sandboxEquations.Count} sandbox equation(s)"
                : string.Empty;
            SetStatus($"Exported {userRegions.Count} region(s){suffix}  →  {Path.GetFileName(dlg.FileName)}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Export failed:\n\n{ex.Message}", "Export Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnImportRegionsClick(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Import Custom Regions",
            Filter = "JSON File (*.json)|*.json|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        List<FractalRegion>? imported = null;
        List<SandboxEquationEntry>? importedSandbox = null;
        try
        {
            string text = File.ReadAllText(dlg.FileName);
            // Try new bundle format first (object with Version/Regions/SandboxEquations).
            // Fall back to legacy List<FractalRegion> for backwards compatibility.
            string trimmed = text.TrimStart();
            if (trimmed.StartsWith("{"))
            {
                var bundle = JsonSerializer.Deserialize<RegionExportBundle>(text);
                imported = bundle?.Regions;
                importedSandbox = bundle?.SandboxEquations;
            }
            else
            {
                imported = JsonSerializer.Deserialize<List<FractalRegion>>(text);
            }
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

        // Merge bundled Sandbox equations into the store BEFORE adding regions,
        // so SandboxName references resolve on first recall. Skip duplicates
        // (existing names are left untouched to protect local edits).
        int sandboxAdded = 0;
        if (importedSandbox != null && importedSandbox.Count > 0)
        {
            SandboxEquationStore.Instance.Load();
            foreach (var eq in importedSandbox)
            {
                if (eq == null || string.IsNullOrWhiteSpace(eq.Name)) continue;
                if (SandboxEquationStore.Instance.GetByName(eq.Name) != null) continue;
                SandboxEquationStore.Instance.Equations.Add(new SandboxEquationEntry
                {
                    Name = eq.Name,
                    Source = eq.Source ?? string.Empty,
                    Promoted = eq.Promoted
                });
                sandboxAdded++;
            }
            if (sandboxAdded > 0) SandboxEquationStore.Instance.Save();
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

        if (added == 0 && sandboxAdded == 0)
        {
            MessageBox.Show("No valid regions found.", "Import Regions",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        FractalRegionLibrary.Instance.Save();
        RebuildRegionCombo();

        string summary = added == 1 ? "1 region imported" : $"{added} regions imported";
        if (renamed > 0) summary += $" ({renamed} renamed with '-imp')";
        if (sandboxAdded > 0) summary += $" + {sandboxAdded} sandbox equation(s)";
        SetStatus(summary + $"  ←  {Path.GetFileName(dlg.FileName)}");
    }

    /// <summary>
    /// On-disk format for region export bundles (Version >= 2). Carries the
    /// region list plus any referenced Sandbox equations so a recipient can
    /// open a Sandbox region without manually copying the equation source.
    /// Legacy exports (a bare JSON array) are still accepted on import.
    /// </summary>
    private sealed class RegionExportBundle
    {
        public int Version { get; set; } = 2;
        public List<FractalRegion> Regions { get; set; } = new();
        public List<SandboxEquationEntry> SandboxEquations { get; set; } = new();
    }

    private void OnChangeDimensions(object? sender, EventArgs e)
    {
        if (sender != null)
        {
            Resolution? res = ResolutionDimensions.Resolutions.Where(r => r.Name == ((ComboBox)sender).Text).FirstOrDefault<Resolution>();
            if (res == null) return;
            Size = new Size(res.Width, res.Height);
            CenterToParent();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _renderer?.Dispose();
        base.Dispose(disposing);
    }

    #endregion Events

    #region Utilities

    private static bool NameExistsInPalettes(string name)
    {
        foreach (var p in Models.ColorPalette.Palettes)
            if (string.Equals(Models.ColorPalette.GetStaticName(p), name, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
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

    private void BuildResolutionSelection()
    {
        return;
        //int totalW = 0;
        //int totalH = 0;
        //foreach (var s in Screen.AllScreens)
        //{
        //    totalH += s.Bounds.Height;
        //    totalW += s.Bounds.Width;
        //}

        //_formResolutionCombo.Items.Clear();
        //foreach (var rt in ResolutionDimensions.ResolutionTypeName)
        //{
        //    string restypeName = rt.Value;
        //    _formResolutionCombo.Items.Add($" -- {rt.Value} -- ");
        //    foreach (Resolution res in ResolutionDimensions.Resolutions.Where(r => r.ResolutionType == rt.Key))
        //    {
        //        if (res == null) continue;
        //        if (res.Width == 0 || res.Width > totalW) return;
        //        if (res.Height == 0 || res.Height > totalH) return;
        //        _formResolutionCombo.Items.Add(res.Name);
        //    }
        //}
    }

    private void RebuildRegionCombo()
    {
        FormHelpers.RebuildRegionCombo(_regionCombo, OnRegionComboChanged);
        UpdateDelRegionButton(_regionCombo, _delRegionButton);
    }

    private void BuildColorThemesSelection()
    {
        BuildColorCombo(_colorThemeCombo, OnColorThemeChanged);
    }

    /// <summary>
    /// After a Sandbox equation compiles cleanly, analyse its AST and feed the
    /// resulting <see cref="EquationProfile"/> to the colour-theme combo so a
    /// "— Suggested for equation —" section appears at the top of the list.
    /// Safe to call repeatedly; identical profiles are no-ops.
    /// </summary>
    private void ApplySandboxEquationSuggestions()
    {
        var src = _fractalParams.SandboxSource;
        var profile = EquationAnalyzer.TryAnalyze(src ?? string.Empty);
        ApplyEquationProfileToCombos(profile);
    }

    /// <summary>
    /// Mirrors <see cref="ApplySandboxEquationSuggestions"/> but for the Roslyn-
    /// backed UserEquation source. Uses <see cref="UserEquationAnalyzer"/> to
    /// extract the same <see cref="EquationProfile"/> shape from the C# syntax
    /// tree, then feeds it to the theme-combo suggestion sections.
    /// </summary>
    private void ApplyUserEquationSuggestions()
    {
        var src = _fractalParams.UserEquationSource;
        var profile = UserEquationAnalyzer.TryAnalyze(src ?? string.Empty);
        ApplyEquationProfileToCombos(profile);
    }

    private void ApplyEquationProfileToCombos(EquationProfile? profile)
    {
        FormHelpers.ApplyEquationProfile(_colorThemeCombo, profile, OnColorThemeChanged);
        if (_floatingMenu != null && !_floatingMenu.IsDisposed)
            _floatingMenu.ApplyEquationProfile(profile);
    }

    private Color GetSwatchColor()
    {
        if (_calculator?.ColorMap == null) return Color.White;
        _calculator.ColorMap.MaxIterations = 500;
        int argb = _calculator.ColorMap.SwatchSample;
        return Color.FromArgb((argb >> 16) & 0xFF, (argb >> 8) & 0xFF, argb & 0xFF);
    }

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

    private void ToggleMiniDepth()
    {
        if (_miniDepthPanel == null)
        {
            _miniDepthPanel = new MiniDepthPanel();
            _miniDepthPanel.Configure(
                getZoom: () => _zoom,
                getZoomMax: () => _quality.ZoomMax,
                getColorMap: () => _calculator?.ColorMap,
                getSwatchColor: GetSwatchColor);

            _miniDepthPanel.Left = 4;
            _miniDepthPanel.Top = _renderPanel.ClientSize.Height - _miniDepthPanel.Height - 4;
            _miniDepthPanel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _renderPanel.Controls.Add(_miniDepthPanel);
            _miniDepthPanel.BringToFront();
            _miniDepthPanel.RequestRedraw();
        }
        else
        {
            _renderPanel.Controls.Remove(_miniDepthPanel);
            _miniDepthPanel.Dispose();
            _miniDepthPanel = null;
        }
    }

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
                getSwatchColor: GetSwatchColor,
                getFractalType: () => _currentFractalType,
                getFractalParams: () => _fractalParams);

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

    private bool TryParseCoords(out double cx, out double cy,
                                 out double zoom, out int iter)
    {
        cx = _centerX;
        cy = _centerY;
        zoom = _zoom;
        iter = _calculator?.MaxIterations ?? 512;

        var ic = System.Globalization.CultureInfo.InvariantCulture;
        var ns = System.Globalization.NumberStyles.Float;

        if (_floatingMenu != null &&
            !_floatingMenu.IsDisposed)
        {
            // CX/CY may be pipe-separated QD format, a single decimal/scientific
            // QD digest, or a plain decimal. Parse all forms and validate the Hi limb.
            bool okCx = FormHelpers.TryParseCoordAny(_floatingMenu.CX, out cx, out _, out _, out _);
            bool okCy = FormHelpers.TryParseCoordAny(_floatingMenu.CY, out cy, out _, out _, out _);
            return okCx && okCy
                && double.TryParse(_floatingMenu.ZoomString.Trim(), ns, ic, out zoom) && zoom > 0
                && int.TryParse(_floatingMenu.Iter.Trim(), out iter) && iter >= 64;

        }
        else return false;
    }

    private void SetStatus(string text)
    {
        if (InvokeRequired) Invoke(() => _statusLabel.Text = text);
        else _statusLabel.Text = text;
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
        return Models.ColorPalette.GetStaticName(_calculator.ColorMap);
    }

    private string CurrentFractalTypeName()
    {

        return Fractals.FractalNameByNameType[_currentFractalType];
    }

    private string BuildSuggestedRegionName()
    {
        if (_currentFractalType == FractalType.Mandelbrot) return "";

        string typeName = _currentFractalType.ToString();
        string? equationName = _currentFractalType switch
        {
            FractalType.UserEquation => _fractalParams.UserEquationName,
            FractalType.Sandbox => _fractalParams.SandboxName,
            FractalType.UserBulb => _fractalParams.UserBulbName,
            _ => null
        };

        return string.IsNullOrEmpty(equationName)
            ? $"{typeName} - "
            : $"{typeName} - {equationName}";
    }

    private void CheckForNewColorThemes()
    {
        //if (string.IsNullOrEmpty(ColorPalette.))
    }

    #region Region management

    /// <summary>
    /// Loads region-specific fractal parameters (UserEquation source, Sandbox
    /// source, UserBulb source/camera/light) into _fractalParams and compiles
    /// the affected calculators. Does NOT touch center/zoom/quality — callers
    /// own the view-state apply. Pulled out of ApplyRegion so Video Slideshow
    /// can switch fractal type + load params without triggering the full
    /// ApplyViewState path.
    /// </summary>
    private void LoadRegionFractalParams(FractalRegion region)
    {
        // UserEquation regions reference a saved entry by name — pull the live
        // source from the store so edits to the named equation propagate to every
        // region that uses it.
        if (region.FractalType == FractalType.UserEquation
            && !string.IsNullOrWhiteSpace(region.UserEquationName))
        {
            var entry = UserEquationStore.Instance.GetByName(region.UserEquationName);
            if (entry != null)
            {
                _fractalParams.UserEquationSource = entry.Source;
                _fractalParams.UserEquationName = entry.Name;
                _userEquationCalculator?.Compile(entry.Source);
                if (_userEqDialog != null && !_userEqDialog.IsDisposed)
                    _userEqDialog.LoadEquationByName(entry.Name);
            }
        }

        if (region.FractalType == FractalType.Sandbox
            && !string.IsNullOrWhiteSpace(region.SandboxName))
        {
            var entry = SandboxEquationStore.Instance.GetByName(region.SandboxName);
            if (entry != null)
            {
                _fractalParams.SandboxSource = entry.Source;
                _fractalParams.SandboxName = entry.Name;
                _sandboxCalculator?.Compile(entry.Source);
                if (_sandboxDialog != null && !_sandboxDialog.IsDisposed)
                    _sandboxDialog.LoadEquationByName(entry.Name);
            }
        }

        if (region.FractalType == FractalType.UserBulb)
        {
            string? source = null;
            UserBulbEntry? entry = !string.IsNullOrWhiteSpace(region.UserBulbName)
                ? UserBulbStore.Instance.GetByName(region.UserBulbName)
                : null;
            if (entry != null)
            {
                source = entry.Source;
                _fractalParams.UserBulbSource = entry.Source;
                _fractalParams.UserBulbName = entry.Name;
            }
            else if (!string.IsNullOrWhiteSpace(region.UserBulbSource))
            {
                source = region.UserBulbSource;
                _fractalParams.UserBulbSource = region.UserBulbSource;
                _fractalParams.UserBulbName = region.UserBulbName;
            }

            if (region.UserBulbCameraDistance > 0)
            {
                _fractalParams.UserBulbCameraDistance = region.UserBulbCameraDistance;
                _fractalParams.UserBulbCameraTheta = region.UserBulbCameraTheta;
                _fractalParams.UserBulbCameraPhi = region.UserBulbCameraPhi;
                _fractalParams.UserBulbLightTheta = region.UserBulbLightTheta;
                _fractalParams.UserBulbLightPhi = region.UserBulbLightPhi;
            }

            if (!string.IsNullOrWhiteSpace(source))
                _userBulbCalculator?.Compile(source);
            if (entry != null && _userBulbDialog != null && !_userBulbDialog.IsDisposed)
                _userBulbDialog.LoadEquationByName(entry.Name);
        }
    }

    /// <summary>Applies a FractalRegion to the view state, respecting the iteration lock.</summary>
    private void ApplyRegion(FractalRegion region)
    {
        // Auto-switch active fractal type if the region targets a different one.
        // Done before applying coords so the calculator (set in ApplyViewState) sees the new type.
        if (region.FractalType != _currentFractalType)
            SwitchFractalTypeForRegion(region.FractalType);

        LoadRegionFractalParams(region);

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

    //private void UpdateDelRegionButton()
    //{
    //    string? name = _regionCombo.SelectedItem?.ToString();
    //    if (string.IsNullOrEmpty(name) || name == "— select region —")
    //    { _delRegionButton.Enabled = false; return; }
    //    var region = FractalRegionLibrary.Instance.FindByName(name);
    //    _delRegionButton.Enabled = region != null && !region.IsBuiltIn;
    //}

    //private void UpdateDeleteColorThemeButton()
    //{
    //    string? name = _colorThemeCombo.SelectedItem?.ToString();
    //    if (string.IsNullOrEmpty(name) || name.StartsWith("—"))
    //    { _deleteColorThemeButton.Enabled = false; return; }

    //    _deleteColorThemeButton.Enabled = UserColorThemeLibrary.Instance.Themes
    //        .Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    //}

    #endregion Region management

    #endregion Utilities

    #region Monitor Spanning

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

        if (_floatingMenu != null && _floatingMenu.Visible)
        {
            _floatingMenu.TopMost = true;
            _floatingMenu.BringToFront();
        }
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

    #endregion Monitor Spanning

    #region Mouse/Keyboard

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && _spanning)
        { ExitSpanMode(); e.Handled = true; return; }
        if (e.KeyCode == Keys.Escape && _slideshowRunning)
        { StopSlideshow(); e.Handled = true; return; }

        // HP-path acceleration toggles — diagnostic aids for visual artefacts
        // at deep zoom. Status text reports the new state; render is retriggered
        // so the change is immediately visible.
        //   Ctrl+Shift+S → toggle Series Approximation prelude (BLA stays on)
        //   Ctrl+Shift+A → toggle ALL HP acceleration (SA + BLA)
        if (_calculator != null && e.Control && e.Shift)
        {
            if (e.KeyCode == Keys.S)
            {
                _calculator.DisableSeriesApproximation = !_calculator.DisableSeriesApproximation;
                Text = $"SA {(_calculator.DisableSeriesApproximation ? "OFF" : "ON")}";
                TriggerCalculation(progressive: false);
                e.Handled = true;
                return;
            }
            if (e.KeyCode == Keys.A)
            {
                _calculator.DisableAcceleration = !_calculator.DisableAcceleration;
                Text = $"HP accel {(_calculator.DisableAcceleration ? "OFF" : "ON")}";
                TriggerCalculation(progressive: false);
                e.Handled = true;
                return;
            }
        }

        // Don't steal letter keys from a focused text input.
        if (ActiveControl is TextBox || ActiveControl is NumericUpDown || ActiveControl is ComboBox)
            return;
        if (_slideshowRunning || _spanning) return;
        if (e.Control || e.Alt || e.Shift) return;

        bool is3D = Is3DFractalType(_currentFractalType);

        switch (e.KeyCode)
        {
            // ── Universal commands ────────────────────────────────────────────
            case Keys.M:
                ToggleFloatingMenu();
                e.Handled = true; return;

            case Keys.T:
                OnEditColorThemeClick(this, EventArgs.Empty);
                e.Handled = true; return;

            case Keys.R:
                OnResetClick(this, EventArgs.Empty);
                e.Handled = true; return;

            case Keys.V:
                OnSaveViewClick(this, EventArgs.Empty);
                e.Handled = true; return;
        }

        if (!is3D)
        {
            // ── 2D: W/S = zoom, A/D = pan ────────────────────────────────────
            const double zoomFactor = 1.25;
            const double panFrac = 0.125;   // pan ~1/8 of viewport per key
            switch (e.KeyCode)
            {
                case Keys.W: CenterZoomBy(zoomFactor); e.Handled = true; return;
                case Keys.S: CenterZoomBy(1.0 / zoomFactor); e.Handled = true; return;
                case Keys.A: PanByPixels((int)(_renderPanel.ClientSize.Width * panFrac), 0); e.Handled = true; return;
                case Keys.D: PanByPixels(-(int)(_renderPanel.ClientSize.Width * panFrac), 0); e.Handled = true; return;
                case Keys.Q: PanByPixels(0, (int)(_renderPanel.ClientSize.Height * panFrac)); e.Handled = true; return;
                case Keys.E: PanByPixels(0, -(int)(_renderPanel.ClientSize.Height * panFrac)); e.Handled = true; return;
            }
            return;
        }

        // ── 3D: W/S = distance, A/D = pan, arrows = camera, Pg/Home/End = light ─
        const double distStep = 0.25;
        const double rotStep = Math.PI / 36.0; // 5°
        const double pan3DFrac = 0.125;
        switch (e.KeyCode)
        {
            case Keys.W: Adjust3DDistance(-distStep); e.Handled = true; return;
            case Keys.S: Adjust3DDistance(distStep); e.Handled = true; return;
            case Keys.A: PanByPixels((int)(_renderPanel.ClientSize.Width * pan3DFrac), 0); e.Handled = true; return;
            case Keys.D: PanByPixels(-(int)(_renderPanel.ClientSize.Width * pan3DFrac), 0); e.Handled = true; return;
            case Keys.Q: PanByPixels(0, (int)(_renderPanel.ClientSize.Height * pan3DFrac)); e.Handled = true; return;
            case Keys.E: PanByPixels(0, -(int)(_renderPanel.ClientSize.Height * pan3DFrac)); e.Handled = true; return;

            case Keys.Up: Adjust3DCameraPhi(rotStep); e.Handled = true; return;
            case Keys.Down: Adjust3DCameraPhi(-rotStep); e.Handled = true; return;
            case Keys.Left: Adjust3DCameraTheta(-rotStep); e.Handled = true; return;
            case Keys.Right: Adjust3DCameraTheta(rotStep); e.Handled = true; return;

            case Keys.PageUp: Adjust3DLightTheta(-rotStep); e.Handled = true; return;
            case Keys.PageDown: Adjust3DLightTheta(rotStep); e.Handled = true; return;
            case Keys.Home: Adjust3DLightPhi(-rotStep); e.Handled = true; return;
            case Keys.End: Adjust3DLightPhi(rotStep); e.Handled = true; return;
        }
    }

    /// <summary>Arrow / PgUp / PgDn / Home / End are usually consumed by Forms as
    /// dialog keys before they reach KeyDown.  Route them into OnKeyDown when
    /// no editable control has focus so 3D camera/light bindings work.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (ActiveControl is TextBox || ActiveControl is NumericUpDown || ActiveControl is ComboBox)
            return base.ProcessCmdKey(ref msg, keyData);

        Keys key = keyData & Keys.KeyCode;
        if (key is Keys.Up or Keys.Down or Keys.Left or Keys.Right
                or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End)
        {
            var args = new KeyEventArgs(keyData);
            OnKeyDown(this, args);
            if (args.Handled) return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private static bool Is3DFractalType(FractalType t)
        => t == FractalType.Mandelbulb || t == FractalType.UserBulb;

    private void ToggleFloatingMenu()
    {
        if (_floatingMenu == null || _floatingMenu.IsDisposed) return;
        if (_showFloatingMenu && _floatingMenu.Visible)
            OnCloseCoordPanelClick(this, EventArgs.Empty);
        else
            OnShowCoordPanelClick();
    }

    /// <summary>Zoom by <paramref name="factor"/> around the view centre, mirroring
    /// the precision-aware path used by <see cref="OnMouseWheel"/>.  Used by W/S
    /// key bindings in non-3D fractal modes.</summary>
    private void CenterZoomBy(double factor)
    {
        if (_calculator == null) return;

        double targetZoom = System.Math.Clamp(
            _zoom * factor,
            QualityPreset.Draft.ZoomMin,
            QualityPreset.Extreme.ZoomMax);
        if (AdaptQualityForWheel(_zoom, targetZoom))
            SetStatus($"Quality → {_quality.Name} (zoom {targetZoom:G3}).");

        _zoom = System.Math.Clamp(_zoom * factor, _quality.ZoomMin, _quality.ZoomMax);
        ApplyViewState();
        TriggerCalculation(progressive: false);
    }

    /// <summary>Shift the view centre by (dx,dy) screen pixels, respecting the
    /// active precision tier.  Negative dx pans the view left (centre moves
    /// right in complex-plane terms).</summary>
    private void PanByPixels(int dx, int dy)
    {
        if (_calculator == null) return;
        double scale = CurrentScale();
        if (_zoom > QDZoomThreshold)
        {
            var qdCX = new FracturingFog.FFMath.QD(_centerX, _centerXLo, _centerX2, _centerX3) + dx * scale;
            var qdCY = new FracturingFog.FFMath.QD(_centerY, _centerYLo, _centerY2, _centerY3) + dy * scale;
            _centerX = qdCX.X0; _centerXLo = qdCX.X1; _centerX2 = qdCX.X2; _centerX3 = qdCX.X3;
            _centerY = qdCY.X0; _centerYLo = qdCY.X1; _centerY2 = qdCY.X2; _centerY3 = qdCY.X3;
        }
        else if (_quality.NeedsHighPrecision(_zoom))
        {
            var newCX = new FracturingFog.FFMath.DD(_centerX, _centerXLo) + dx * scale;
            var newCY = new FracturingFog.FFMath.DD(_centerY, _centerYLo) + dy * scale;
            _centerX = newCX.Hi; _centerXLo = newCX.Lo; _centerX2 = 0; _centerX3 = 0;
            _centerY = newCY.Hi; _centerYLo = newCY.Lo; _centerY2 = 0; _centerY3 = 0;
        }
        else
        {
            _centerX += dx * scale; _centerXLo = 0; _centerX2 = 0; _centerX3 = 0;
            _centerY += dy * scale; _centerYLo = 0; _centerY2 = 0; _centerY3 = 0;
        }
        ApplyViewState();
        TriggerCalculation(progressive: false);
    }

    private void Adjust3DDistance(double delta)
    {
        if (_currentFractalType == FractalType.UserBulb)
            _fractalParams.UserBulbCameraDistance = System.Math.Clamp(
                _fractalParams.UserBulbCameraDistance + delta, 0.1, 50.0);
        else if (_currentFractalType == FractalType.Mandelbulb)
            _fractalParams.BulbCameraDistance = System.Math.Clamp(
                _fractalParams.BulbCameraDistance + delta, 0.1, 50.0);
        else return;
        _lastUploadedBuffer = null;
        TriggerCalculation();
    }

    private void Adjust3DCameraTheta(double delta)
    {
        if (_currentFractalType == FractalType.UserBulb)
            _fractalParams.UserBulbCameraTheta = NormalizeAngle(_fractalParams.UserBulbCameraTheta + delta);
        else if (_currentFractalType == FractalType.Mandelbulb)
            _fractalParams.BulbCameraTheta = NormalizeAngle(_fractalParams.BulbCameraTheta + delta);
        else return;
        _lastUploadedBuffer = null;
        TriggerCalculation();
    }

    private void Adjust3DCameraPhi(double delta)
    {
        // Phi is polar: clamp away from poles to avoid gimbal singularity.
        const double phiMin = 0.01;
        const double phiMax = Math.PI - 0.01;
        if (_currentFractalType == FractalType.UserBulb)
            _fractalParams.UserBulbCameraPhi = System.Math.Clamp(
                _fractalParams.UserBulbCameraPhi + delta, phiMin, phiMax);
        else if (_currentFractalType == FractalType.Mandelbulb)
            _fractalParams.BulbCameraPhi = System.Math.Clamp(
                _fractalParams.BulbCameraPhi + delta, phiMin, phiMax);
        else return;
        _lastUploadedBuffer = null;
        TriggerCalculation();
    }

    private void Adjust3DLightTheta(double delta)
    {
        if (_currentFractalType == FractalType.UserBulb)
            _fractalParams.UserBulbLightTheta = NormalizeAngle(_fractalParams.UserBulbLightTheta + delta);
        else if (_currentFractalType == FractalType.Mandelbulb)
            _fractalParams.BulbLightTheta = NormalizeAngle(_fractalParams.BulbLightTheta + delta);
        else return;
        _lastUploadedBuffer = null;
        TriggerCalculation();
    }

    private void Adjust3DLightPhi(double delta)
    {
        const double phiMin = 0.01;
        const double phiMax = Math.PI - 0.01;
        if (_currentFractalType == FractalType.UserBulb)
            _fractalParams.UserBulbLightPhi = System.Math.Clamp(
                _fractalParams.UserBulbLightPhi + delta, phiMin, phiMax);
        else if (_currentFractalType == FractalType.Mandelbulb)
            _fractalParams.BulbLightPhi = System.Math.Clamp(
                _fractalParams.BulbLightPhi + delta, phiMin, phiMax);
        else return;
        _lastUploadedBuffer = null;
        TriggerCalculation();
    }

    private static double NormalizeAngle(double a)
    {
        const double twoPi = Math.PI * 2.0;
        a %= twoPi;
        if (a < 0) a += twoPi;
        return a;
    }

    /// <summary>
    /// Auto-promote / demote the quality preset to the smallest tier whose
    /// ZoomMax accommodates <paramref name="targetZoom"/>.  Keeps UI combos
    /// and the calculator in sync.  No-op if the current tier already fits.
    /// </summary>
    private bool AdaptQualityForZoom(double targetZoom)
    {
        QualityPreset fit = NaturalQualityForZoom(targetZoom);
        if (fit.Tier == _quality.Tier) return false;

        _quality = fit;
        if (_calculator != null) _calculator.Quality = _quality;

        _qualityCombo.SelectedIndexChanged -= OnQualityComboChanged;
        _qualityCombo.Text = _quality.Name;
        _qualityCombo.SelectedIndexChanged += OnQualityComboChanged;
        if (_floatingMenu != null && !_floatingMenu.IsDisposed)
            _floatingMenu.Quality = _quality.Name;
        return true;
    }

    /// <summary>
    /// Returns the smallest tier whose <c>ZoomMax</c> accommodates
    /// <paramref name="z"/>.  Falls back to <see cref="QualityPreset.Extreme"/>.
    /// </summary>
    private static QualityPreset NaturalQualityForZoom(double z)
    {
        foreach (var p in QualityPreset.All)
            if (p.ZoomMax >= z) return p;
        return QualityPreset.Extreme;
    }

    /// <summary>
    /// Wheel-zoom variant of <see cref="AdaptQualityForZoom"/> that respects
    /// a manually-chosen tier.  Adjusts ONLY when the wheel actually crosses
    /// a tier boundary — i.e. the natural tier for the post-wheel zoom
    /// differs from the natural tier for the pre-wheel zoom AND the current
    /// _quality tier matches the pre-wheel natural (meaning auto-tracking was
    /// already in effect).  If the user has manually picked a different tier,
    /// the choice persists across wheel scrolls within the same tier band.
    /// Forces a promote when targetZoom exceeds the current tier's ZoomMax
    /// (otherwise the view would silently clamp).
    /// </summary>
    private bool AdaptQualityForWheel(double oldZoom, double newZoom)
    {
        // Hard cap: if the new zoom literally cannot be rendered at the current
        // tier, we must promote regardless of manual choice.
        if (newZoom > _quality.ZoomMax)
            return AdaptQualityForZoom(newZoom);

        QualityPreset natOld = NaturalQualityForZoom(oldZoom);
        QualityPreset natNew = NaturalQualityForZoom(newZoom);
        if (natOld.Tier == natNew.Tier) return false;          // no boundary crossed
        if (_quality.Tier != natOld.Tier) return false;        // user override — respect it

        return AdaptQualityForZoom(newZoom);
    }

    private void OnMouseWheel(object? sender, MouseEventArgs e)
    {
        if (_calculator == null || _slideshowRunning) return;

        double wf = _quality.WheelZoomFactor;
        double factor = e.Delta > 0 ? wf : 1.0 / wf;

        // 3D: zoom dollies camera (camDist /= Zoom). No complex-plane anchor —
        // FOV is constant, so cursor-anchor math doesn't apply. Plain dolly.
        if (Is3DFractalType(_currentFractalType))
        {
            _zoom = System.Math.Clamp(_zoom * factor, _quality.ZoomMin, _quality.ZoomMax);
            ApplyViewState();
            TriggerCalculation(progressive: false);
            return;
        }

        double scale = CurrentScale();
        double ox = e.X - _renderPanel.ClientSize.Width * 0.5;
        double oy = e.Y - _renderPanel.ClientSize.Height * 0.5;

        // Adapt quality preset to the post-wheel zoom BEFORE the per-precision
        // anchor math runs, so the Clamp() inside each branch uses the right
        // ZoomMax. Wheel factor may cross tier boundaries either direction.
        // Manual user picks persist until the wheel crosses a tier threshold.
        double targetZoom = System.Math.Clamp(
            _zoom * factor,
            QualityPreset.Draft.ZoomMin,
            QualityPreset.Extreme.ZoomMax);
        if (AdaptQualityForWheel(_zoom, targetZoom))
            SetStatus($"Quality → {_quality.Name} (zoom {targetZoom:G3}).");
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

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (_slideshowRunning) return;

        if (e.Button == MouseButtons.Right)
        {
            _rightDownTimeUtc = DateTime.UtcNow;
            _rightDragStart = e.Location;
        }

        // Right-click drag in 3D modes rotates the camera (theta = X, phi = Y).
        if (e.Button == MouseButtons.Right && Is3DFractalType(_currentFractalType))
        {
            _rightDragging = true;
            _rightDragStart = e.Location;
            _rightDragStartTheta = _currentFractalType == FractalType.UserBulb
                ? _fractalParams.UserBulbCameraTheta : _fractalParams.BulbCameraTheta;
            _rightDragStartPhi = _currentFractalType == FractalType.UserBulb
                ? _fractalParams.UserBulbCameraPhi : _fractalParams.BulbCameraPhi;
            _renderPanel.Cursor = Cursors.NoMove2D;
            return;
        }

        if (e.Button != MouseButtons.Left) return;
        _lastMouseDownPos = e.Location;
        _panning = true;
        _panStartScreen = e.Location;
        _panStartCX = _centerX;
        _panStartCY = _centerY;
        // High-precision center captures are only meaningful for the 2D
        // complex-plane pan path. 3D modes use NDC pan units, no HP needed.
        if (!Is3DFractalType(_currentFractalType))
        {
            _panStartDDCX = new FracturingFog.FFMath.DD(_centerX, _centerXLo);
            _panStartDDCY = new FracturingFog.FFMath.DD(_centerY, _centerYLo);
            _panStartQDCX = new FracturingFog.FFMath.QD(_centerX, _centerXLo, _centerX2, _centerX3);
            _panStartQDCY = new FracturingFog.FFMath.QD(_centerY, _centerYLo, _centerY2, _centerY3);
        }
        _renderPanel.Cursor = Cursors.SizeAll;
    }

    private void OnMouseDoubleClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _calculator == null || _slideshowRunning) return;

        // Cancel any pan that started on the first click of the double-click.
        _panning = false;
        _renderPanel.Cursor = Cursors.Cross;

        // 3D: pan in NDC so the clicked point becomes the new view center.
        if (Is3DFractalType(_currentFractalType))
        {
            double s3 = CurrentScale3D();
            double ox3 = e.X - _renderPanel.ClientSize.Width * 0.5;
            double oy3 = e.Y - _renderPanel.ClientSize.Height * 0.5;
            _centerX += ox3 * s3;
            _centerY += oy3 * s3;
            _centerXLo = 0; _centerX2 = 0; _centerX3 = 0;
            _centerYLo = 0; _centerY2 = 0; _centerY3 = 0;
            ApplyViewState();
            TriggerCalculation();
            SetStatus($"Centered on NDC ({_centerX:G6}, {_centerY:G6})");
            return;
        }

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
        if (_rightDragging && _calculator != null && Is3DFractalType(_currentFractalType))
        {
            // Pixel → radians: ~180° across the panel width / height.
            double w = Math.Max(1, _renderPanel.ClientSize.Width);
            double h = Math.Max(1, _renderPanel.ClientSize.Height);
            double dTheta = (e.X - _rightDragStart.X) / w * Math.PI;
            double dPhi = (e.Y - _rightDragStart.Y) / h * Math.PI;
            // UserBulb: invert vertical drag — drag down should look up.
            if (_currentFractalType == FractalType.UserBulb) dPhi = -dPhi;

            const double phiMin = 0.01;
            const double phiMax = Math.PI - 0.01;
            double newTheta = NormalizeAngle(_rightDragStartTheta + dTheta);
            double newPhi = System.Math.Clamp(_rightDragStartPhi + dPhi, phiMin, phiMax);

            if (_currentFractalType == FractalType.UserBulb)
            {
                _fractalParams.UserBulbCameraTheta = newTheta;
                _fractalParams.UserBulbCameraPhi = newPhi;
            }
            else
            {
                _fractalParams.BulbCameraTheta = newTheta;
                _fractalParams.BulbCameraPhi = newPhi;
            }
            _lastUploadedBuffer = null;
            TriggerCalculation();
            return;
        }

        if (!_panning || _calculator == null) return;

        // 3D: CenterX/Y are NDC pan units, not complex-plane coords.
        if (Is3DFractalType(_currentFractalType))
        {
            double s3 = CurrentScale3D();
            _centerX = _panStartCX - (e.X - _panStartScreen.X) * s3;
            _centerY = _panStartCY - (e.Y - _panStartScreen.Y) * s3;
            _centerXLo = 0; _centerX2 = 0; _centerX3 = 0;
            _centerYLo = 0; _centerY2 = 0; _centerY3 = 0;
            ApplyViewState();
            _panStopTimer.Stop();
            _panStopTimer.Start();
            TriggerCalculationFast();
            return;
        }

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
        if (e.Button == MouseButtons.Right && _rightDragging)
        {
            _rightDragging = false;
            _renderPanel.Cursor = Cursors.Cross;
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        _panning = false; _renderPanel.Cursor = Cursors.Cross;
        // If the timer is still running let it fire the full render naturally.
    }

    #endregion Mouse/Keyboard

    #region Rendering/Calculating

    /// <summary>
    /// Fires a calculation with iterations capped for interactive responsiveness.
    /// Full-quality render is triggered by _panStopTimer after dragging stops.
    /// </summary>
    private void TriggerCalculationFast()
    {
        if (_calculator == null) return;
        int saved = _calculator.MaxIterations;
        _calculator.MaxIterations = System.Math.Min(128, saved);
        if (_userBulbCalculator != null) _userBulbCalculator.LowResPreview = true;
        TriggerCalculation(progressive: false);
        _calculator.MaxIterations = saved;
        if (_userBulbCalculator != null) _userBulbCalculator.LowResPreview = false;
    }

    private void PositionGridPanel()
    {
        // The grid is blended into the ColorBuffer directly; no window to reposition.
        // Trigger a repaint so the grid is redrawn at the new panel size.
        if (_gridVisible) RepaintWithBrightnessContrast();
    }

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
        IFractalCalculator? altCalc = SelectAltCalculator(_currentFractalType);
        bool useAlt = altCalc != null;

        // Mirror the most recent Mandelbrot calculator state onto whichever
        // alt calculator is active so theme / iter / view changes routed only
        // through the primary calculator still take effect on this render.
        if (useAlt)
        {
            altCalc!.CenterX = calc.CenterX;
            altCalc.CenterY = calc.CenterY;
            altCalc.Zoom = calc.Zoom;
            altCalc.MaxIterations = calc.MaxIterations;
            altCalc.Quality = calc.Quality;
            altCalc.ColorMap = calc.ColorMap;
            // Per-engine parameter assignment.
            switch (altCalc)
            {
                case EscapeTimeCalculator e:
                    e.FractalType = _currentFractalType;
                    e.FractalParameters = _fractalParams;
                    break;
                case IFSCalculator ifs: ifs.FractalParameters = _fractalParams; break;
                case LSystemCalculator ls: ls.FractalParameters = _fractalParams; break;
                case AttractorCalculator a: a.FractalParameters = _fractalParams; break;
                case BuddhabrotCalculator b: b.FractalParameters = _fractalParams; break;
                case NewtonCalculator n: n.FractalParameters = _fractalParams; break;
                case UserEquationCalculator u: u.FractalParameters = _fractalParams; break;
                case MandelbulbCalculator m: m.FractalParameters = _fractalParams; break;
                case SandboxCalculator sb: sb.FractalParameters = _fractalParams; break;
                case UserBulbCalculator ub: ub.FractalParameters = _fractalParams; break;
            }
        }

        SetStatus("Calculating…");
        var sw = Stopwatch.StartNew();

        // ── Full-resolution render ────────────────────────────────────────────
        Task.Run(() =>
        {
            if (useAlt) altCalc!.Calculate(token);
            else calc.Calculate(token);
            return sw.ElapsedMilliseconds;
        }, token)
        .ContinueWith(t =>
        {
            if (t.IsCanceled || token.IsCancellationRequested)
            {
                // Cancelled render also counts as "done" for animation
                // gating — otherwise a mid-animation cancel (e.g. user
                // drags camera) would leave _renderInFlight=true forever.
                if (_currentFractalType == FractalType.UserBulb)
                    _userBulbDialog?.NotifyRenderDone();
                return;
            }
            if (renderer == null) return;

            long ms = t.IsCompletedSuccessfully ? t.Result : -1;

            if (IsHandleCreated && !_disposed)
            {
                Invoke(() =>
                {
                    if (_disposed) return;
                    // Adaptive contrast — Mandelbrot only.
                    if (!useAlt && _histogramEq > 0)
                        calc.ApplyHistogramEqualization(_histogramEq / 100.0);
                    // Apply brightness/contrast and grid overlay, then upload.
                    if (useAlt)
                        UploadProcessedBuffer(altCalc!.ColorBuffer, altCalc.Width, altCalc.Height, renderer);
                    else
                        UploadProcessedBuffer(calc, renderer);
                    _miniMapPanel?.RefreshIndicator();
                    _miniDepthPanel?.RefreshIndicator();
                    bool hp = !useAlt && calc.IsHighPrecisionActive;
                    int curW = useAlt ? altCalc!.Width : calc.Width;
                    int curH = useAlt ? altCalc!.Height : calc.Height;
                    int curIter = useAlt ? altCalc!.MaxIterations : calc.MaxIterations;
                    double curCx = useAlt ? altCalc!.CenterX : calc.CenterX;
                    double curCy = useAlt ? altCalc!.CenterY : calc.CenterY;
                    double curZoom = useAlt ? altCalc!.Zoom : calc.Zoom;
                    string precTag = hp ? "[DD]" : "[SP]";
                    string typeTag = $"[{_currentFractalType}]";
                    SetStatus(
                        $"{typeTag}  cx={curCx:G12}  cy={curCy:G12}  " +
                        $"zoom={curZoom:G6}  iter={curIter}  " +
                        $"{precTag}  [{ms} ms  {curW}×{curH}]" +
                        (_iterLocked ? "  [ITER LOCKED]" : ""));
                    // Animation gating: tell UserBulb dialog the frame landed
                    // so its next animation tick can fire. Without this the
                    // 30 Hz timer would cancel every render mid-flight.
                    if (_currentFractalType == FractalType.UserBulb)
                        _userBulbDialog?.NotifyRenderDone();
                });
            }
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Picks the alternate calculator (if any) for the given fractal type.
    /// Returns null for Mandelbrot (uses MandelbrotCalculator directly).
    /// </summary>
    private IFractalCalculator? SelectAltCalculator(FractalType type) => type switch
    {
        FractalType.Mandelbrot => null,
        FractalType.Julia => _escapeCalculator,
        FractalType.BurningShip => _escapeCalculator,
        FractalType.Tricorn => _escapeCalculator,
        FractalType.Multibrot => _escapeCalculator,
        FractalType.Phoenix => _escapeCalculator,
        FractalType.IFS => _ifsCalculator,
        FractalType.LSystem => _lsystemCalculator,
        FractalType.StrangeAttractor => _attractorCalculator,
        FractalType.BuddhaBrot => _buddhabrotCalculator,
        FractalType.Newton => _newtonCalculator,
        FractalType.Nova => _newtonCalculator, // share path for now
        FractalType.UserEquation => _userEquationCalculator,
        FractalType.Mandelbulb => _mandelbulbCalculator,
        FractalType.Sandbox => _sandboxCalculator,
        FractalType.UserBulb => _userBulbCalculator,
        FractalType.TearDrop => _tearDropCalculator,
        _ => null
    };

    #endregion Rendering/Calculating

    #region View state helpers

    private double CurrentScale()
    {
        if (_calculator == null) return 3.5;
        return 3.5 / (System.Math.Max(_calculator.Width, _calculator.Height) * _zoom);
    }

    // FOV-scale must match MandelbulbCalculator / UserBulbCalculator: tan(π/6).
    private const double Bulb3DFovScale = 0.57735026918962576; // tan(30°)

    /// <summary>
    /// NDC-per-pixel for 3D camera pan. CenterX/CenterY in Mandelbulb &
    /// UserBulb are consumed as NDC pan units, so screen-pixel deltas must
    /// be converted to NDC, not to a complex-plane scale.
    /// </summary>
    private double CurrentScale3D()
    {
        int h = _renderPanel?.ClientSize.Height ?? 1;
        if (h < 1) h = 1;
        return 2.0 * Bulb3DFovScale / h;
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

        if (_escapeCalculator != null)
        {
            _escapeCalculator.CenterX = _centerX;
            _escapeCalculator.CenterY = _centerY;
            _escapeCalculator.Zoom = _zoom;
            _escapeCalculator.Quality = _quality;
            _escapeCalculator.MaxIterations = _calculator.MaxIterations;
            _escapeCalculator.FractalType = _currentFractalType;
            _escapeCalculator.FractalParameters = _fractalParams;
            _escapeCalculator.ColorMap = _calculator.ColorMap;
        }

        if (_tearDropCalculator != null)
        {
            _tearDropCalculator.CenterX = _centerX;
            _tearDropCalculator.CenterXLo = _centerXLo;
            _tearDropCalculator.CenterX2 = _centerX2;
            _tearDropCalculator.CenterX3 = _centerX3;
            _tearDropCalculator.CenterY = _centerY;
            _tearDropCalculator.CenterYLo = _centerYLo;
            _tearDropCalculator.CenterY2 = _centerY2;
            _tearDropCalculator.CenterY3 = _centerY3;
            _tearDropCalculator.Zoom = _zoom;
            _tearDropCalculator.Quality = _quality;
            _tearDropCalculator.MaxIterations = _calculator.MaxIterations;
            _tearDropCalculator.FractalParameters = _fractalParams;
            _tearDropCalculator.ColorMap = _calculator.ColorMap;
        }

        UpdateCoordBoxes();
    }



    // Parses a coordinate string: accepts either a pipe-separated
    // "Hi|Lo|X2|X3" (native FormatCoord output) or a single decimal/scientific
    // string (FormatCoordSingle output, used by the on-screen textboxes).
    // Returns false if the input cannot be parsed in either form.
    private static bool TryParseQDCoord(string text,
        out double hi, out double lo, out double x2, out double x3)
        => FormHelpers.TryParseCoordAny(text, out hi, out lo, out x2, out x3);

    private void UpdateCoordBoxes()
    {
        if (_suppressCoordUpdate) return;
        _suppressCoordUpdate = true;
        try
        {
            if (_calculator != null && _floatingMenu != null)
            {
                _floatingMenu.UpdateCoordBoxes(
                FormatCoordSingle(_centerX, _centerXLo, _centerX2, _centerX3),
                FormatCoordSingle(_centerY, _centerYLo, _centerY2, _centerY3),
                _zoom.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                _calculator.MaxIterations.ToString());

                _floatingMenu.RegionName = _currentRegionName;
                _floatingMenu.ColorTheme = _currentColorThemeName; ;
                _floatingMenu.Quality = _currentQualityName;
                _floatingMenu.SetCurrentZoom(_zoom);
            }

            // Push current zoom into the toolbar's color theme combo so themes
            // exceeding their MaxRecommendedZoom render dimmed + strikethrough.
            if (_colorThemeCombo is Views.ColorComboBox toolbarCombo)
                toolbarCombo.CurrentZoom = _zoom;
        }
        finally { _suppressCoordUpdate = false; }
    }

    #endregion View state helpers

    #region Post-Processing: brightness / contrast / grid / watermark

    /// <summary>
    /// Called when brightness/contrast sliders change or the grid is toggled.
    /// Re-applies post-processing to the existing ColorBuffer and re-uploads
    /// to the GPU without re-running the fractal calculation.
    /// </summary>
    private void RepaintWithBrightnessContrast()
    {
        if (_calculator == null || _renderer == null || _disposed) return;
        // Route to the currently active calculator's buffer so brightness /
        // contrast / grid post-processing affects whatever fractal is on
        // screen, not just the (possibly stale) Mandelbrot buffer.
        IFractalCalculator? alt = SelectAltCalculator(_currentFractalType);
        if (alt != null)
            UploadProcessedBuffer(alt.ColorBuffer, alt.Width, alt.Height, _renderer);
        else
            UploadProcessedBuffer(_calculator, _renderer);
    }

    /// <summary>
    /// Applies brightness/contrast adjustment and optional grid overlay to
    /// <paramref name="calc"/>.ColorBuffer, then uploads the result to the GPU.
    /// The original ColorBuffer is never modified — a temporary buffer is used.
    /// </summary>
    private void UploadProcessedBuffer(MandelbrotCalculator calc, IFractalRenderer renderer)
        => UploadProcessedBuffer(calc.ColorBuffer, calc.Width, calc.Height, renderer);

    private void UploadProcessedBuffer(EscapeTimeCalculator calc, IFractalRenderer renderer)
        => UploadProcessedBuffer(calc.ColorBuffer, calc.Width, calc.Height, renderer);

    private void UploadProcessedBuffer(uint[] src, int w, int h, IFractalRenderer renderer)
    {
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
        string wm = $"{(!string.IsNullOrEmpty(CurrentRegionName()) ? CurrentRegionName() : "")}" +
                    $"{(!string.IsNullOrEmpty(CurrentColorMapName()) ? " - " + CurrentColorMapName() : "")}";
        string subText = $"{_programName} v{_programVersion} {DateTime.Now.Year}";

        Rectangle bbox = MeasureWatermarkBBox(wm, subText, w, h);
        if (bbox.Width <= 0 || bbox.Height <= 0) return;

        int bx = bbox.X, by = bbox.Y, bw = bbox.Width, bh = bbox.Height;

        using var bmp = new Bitmap(bw, bh, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            // Sample contrast colour from full destination (lower-right region).
            Color fontColor = ComputeContrastColor(
                GetSwatchColor(), watermark: true, pixels: dst, imgW: w, imgH: h);

            // Draw at full-image coordinates, shifted into the small bitmap.
            g.TranslateTransform(-bx, -by);
            AddWaterMark(g, wm, w, h, fontColor, subText);
        }

        var data = bmp.LockBits(new Rectangle(0, 0, bw, bh),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte* srcPtr = (byte*)data.Scan0;
            int stride = data.Stride;
            for (int row = 0; row < bh; row++)
            {
                byte* rowPtr = srcPtr + (long)row * stride;
                int dstRowBase = (by + row) * w + bx;
                for (int col = 0; col < bw; col++)
                {
                    byte gA = rowPtr[col * 4 + 3];
                    if (gA == 0) continue;
                    byte gB = rowPtr[col * 4 + 0];
                    byte gG = rowPtr[col * 4 + 1];
                    byte gR = rowPtr[col * 4 + 2];
                    int idx = dstRowBase + col;
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

    #endregion Post-Processing: brightness / contrast / grid / watermark

}