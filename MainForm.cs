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
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using FracturingFog.Models;
using FracturingFog.Interefaces;
using System.Collections.Immutable;

namespace FracturingFog;

public sealed class MainForm : Form
{
    // ── UI: top toolbar ───────────────────────────────────────────────────────
    private readonly Panel _toolbar;
    private readonly Button _resetButton;
    private readonly Button _spanButton;
    private readonly Button _screenshotButton;
    private readonly Button _slideshowButton;
    private readonly ComboBox _qualityCombo;
    private readonly ComboBox _colorThemeCombo;
    private readonly ComboBox _regionCombo;
    private readonly Button _saveViewButton;
    private readonly Button _delRegionButton;
    private readonly Button _exportRegionsButton;
    private readonly Button _importRegionsButton;
    private readonly Label _statusLabel;

    // ── UI: coordinate / region bar ───────────────────────────────────────────
    private readonly Panel _coordPanel;
    private readonly TextBox _txCX;
    private readonly TextBox _txCY;
    private readonly TextBox _txZoom;
    private readonly TextBox _txIter;
    private readonly CheckBox _chkLockIter;   // NEW: iteration lock
    private readonly Button _goButton;

    // ── Render panel ──────────────────────────────────────────────────────────
    private readonly RenderPanel _renderPanel;

    // GridOverlayPanel — sibling of _renderPanel; see architecture note below.
    // Visible is always false until the user ticks "Grid" AFTER OnLoad.
    private readonly GridOverlayPanel _gridPanel;
    private bool _gridVisible;

    // ── UI: Footer panel ──────────────────────────────────────────────────────
    private readonly Panel _footerPanel;

    // ── Core objects ──────────────────────────────────────────────────────────

    private DirectXRenderer? _renderer;
    private MandelbrotCalculator? _calculator;

    // ── View state ────────────────────────────────────────────────────────────

    private const double DefaultCenterX = -0.5;
    private const double DefaultCenterY = 0.0;
    private const double DefaultZoom = 0.3;

    private double _centerX = DefaultCenterX;
    private double _centerY = DefaultCenterY;
    private double _zoom = DefaultZoom;

    // Active quality preset — Standard by default.
    private QualityPreset _quality = QualityPreset.Standard;

    // Guard: prevents coord boxes being repopulated while user types.
    private bool _suppressCoordUpdate;

    // ── Pan state ─────────────────────────────────────────────────────────────

    private bool _panning;
    private Point _panStartScreen;
    private double _panStartCX;
    private double _panStartCY;

    // ── Multi-monitor span state ──────────────────────────────────────────────

    private bool _spanning;
    private Rectangle _preSpanBounds;
    private FormBorderStyle _preSpanBorderStyle;
    private FormWindowState _preSpanWindowState;

    // ── Async calculation ─────────────────────────────────────────────────────

    private CancellationTokenSource? _calcCts;
    private readonly object _calcLock = new();

    private CancellationTokenSource? _wallpaperCts;
    private readonly object _wallpaperLock = new();

    // ── Slideshow state ───────────────────────────────────────────────────────

    private bool _slideshowRunning;
    private CancellationTokenSource? _slideshowCts;
    private readonly object _slideshowLock = new();
    private readonly Random _slideshowRng = new();

    // ── Iteration lock ────────────────────────────────────────────────────────

    private bool _iterLocked;       // mirrors _chkLockIter.Checked
    private int _lockedIterations; // value held while locked

    private bool _disposed;

    // ─────────────────────────────────────────────────────────────────────────
    // Constructor
    // ─────────────────────────────────────────────────────────────────────────

    public MainForm()
    {
        Text = "Fracturing Fog  —  Mandelbrot Explorer  (DirectX 11 · Vortice 3.8.3)";
        ClientSize = new Size(1333, 768);
        MinimumSize = new Size(880, 480);
        BackColor = Color.Black;
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        // ── Helpers ───────────────────────────────────────────────────────────

        Button MakeBtn(string text, int w = 108) => new Button
        {
            Text = text,
            Width = w,
            Height = 26,
            Top = 6,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(55, 55, 55),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Cursor = Cursors.Hand
        }.Also(b => b.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90));

        Label MakeLbl(string text, int left, Panel p) => new Label
        {
            Text = text,
            Left = left,
            Top = 9,
            AutoSize = true,
            ForeColor = Color.FromArgb(155, 155, 155),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            BackColor = Color.Transparent
        }.AlsoAdd(p);

        TextBox MakeTx(int left, int w, Panel p, string tip) => new TextBox
        {
            Left = left,
            Top = 7,
            Width = w,
            Height = 22,
            BackColor = Color.FromArgb(40, 40, 40),
            ForeColor = Color.FromArgb(220, 220, 220),
            Font = new Font("Consolas", 9f),
            BorderStyle = BorderStyle.FixedSingle
        }.AlsoAdd(p, tip);

        // ── Top toolbar ───────────────────────────────────────────────────────

        _toolbar = new Panel
        {
            Height = 38,
            Dock = DockStyle.Top,
            BackColor = Color.FromArgb(28, 28, 28),
        };

        int buttonLeft = 6;

        _resetButton = MakeBtn("Reset", 55);
        _resetButton.Left = buttonLeft;
        _resetButton.Click += OnResetClick;
        _toolbar.Controls.Add(_resetButton);
        buttonLeft += 58;

        _spanButton = MakeBtn("Span", 55);
        _spanButton.Left = buttonLeft;
        _spanButton.Click += OnSpanMonitorsClick;
        _toolbar.Controls.Add(_spanButton);
        buttonLeft += 58;

        _screenshotButton = MakeBtn("Image", 55);
        _screenshotButton.Left = buttonLeft;
        _screenshotButton.Click += OnScreenshotClick;
        _toolbar.Controls.Add(_screenshotButton);
        buttonLeft += 58;

        // Slideshow button (NEW)
        _slideshowButton = MakeBtn("Slideshow", 72);
        _slideshowButton.Left = buttonLeft;
        _slideshowButton.BackColor = Color.FromArgb(40, 55, 40);
        _slideshowButton.FlatAppearance.BorderColor = Color.FromArgb(60, 100, 60);
        new ToolTip().SetToolTip(_slideshowButton,
            "Start/stop slideshow — auto-cycles regions every 30 s, themes every 10 s");
        _slideshowButton.Click += OnSlideshowClick;
        _toolbar.Controls.Add(_slideshowButton);
        buttonLeft += 76;

        // Thin separator.
        _toolbar.Controls.Add(new Label { Left = buttonLeft, Top = 4, Width = 1, Height = 30, BackColor = Color.FromArgb(65, 65, 65) });
        buttonLeft += 8;

        // Quality label + combo.
        var qlbl = new Label
        {
            Text = "Quality:",
            Left = buttonLeft,
            Top = 10,
            AutoSize = true,
            ForeColor = Color.FromArgb(155, 155, 155),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            BackColor = Color.Transparent
        };
        _toolbar.Controls.Add(qlbl);
        buttonLeft += qlbl.PreferredWidth + 4;

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

        // Theme separator.
        _toolbar.Controls.Add(new Label { Left = buttonLeft, Top = 4, Width = 1, Height = 30, BackColor = Color.FromArgb(65, 65, 65) });
        buttonLeft += 10;

        // Theme label + combo.
        var tlbl = new Label
        {
            Text = "Theme:",
            Left = buttonLeft,
            Top = 10,
            AutoSize = true,
            ForeColor = Color.FromArgb(155, 155, 155),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            BackColor = Color.Transparent
        };
        _toolbar.Controls.Add(tlbl);
        buttonLeft += tlbl.PreferredWidth + 4;

        _colorThemeCombo = new ColorComboBox
        {
            Left = buttonLeft,
            Top = 7,
            Width = 162,
            Height = 26,
            BackColor = Color.FromArgb(55, 55, 55),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        BuildColorThemesSelection();
        _colorThemeCombo.SelectedIndex = 0;
        _colorThemeCombo.SelectedIndexChanged += OnColorThemeChanged;
        _toolbar.Controls.Add(_colorThemeCombo);
        buttonLeft += 170;

        // Regions separator.
        _toolbar.Controls.Add(new Label { Left = buttonLeft, Top = 2, Width = 1, Height = 30, BackColor = Color.FromArgb(60, 60, 60) });
        buttonLeft += 10;

        var rlbl = new Label
        {
            Text = "Region:",
            Left = buttonLeft,
            Top = 10,
            AutoSize = true,
            ForeColor = Color.FromArgb(155, 155, 155),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            BackColor = Color.Transparent
        };
        _toolbar.Controls.Add(rlbl);
        buttonLeft += rlbl.PreferredWidth + 3;

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

        _saveViewButton = MakeBtn("Save", 55);
        _saveViewButton.Left = buttonLeft;
        _saveViewButton.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90);
        _saveViewButton.Click += OnSaveViewClick;
        _toolbar.Controls.Add(_saveViewButton);
        buttonLeft += 58;

        _delRegionButton = MakeBtn("Delete", 55);
        _delRegionButton.Left = buttonLeft;
        _delRegionButton.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90);
        _delRegionButton.Click += OnDelRegionClick;
        _toolbar.Controls.Add(_delRegionButton);
        buttonLeft += 58;

        _exportRegionsButton = MakeBtn("Exp…", 55);
        _exportRegionsButton.Left = buttonLeft;
        _exportRegionsButton.FlatAppearance.BorderColor = Color.FromArgb(60, 90, 120);
        new ToolTip().SetToolTip(_exportRegionsButton, "Export all custom regions to a JSON file");
        _exportRegionsButton.Click += OnExportRegionsClick;
        _toolbar.Controls.Add(_exportRegionsButton);
        buttonLeft += 58;

        _importRegionsButton = MakeBtn("Imp…", 55);
        _importRegionsButton.Left = buttonLeft;
        _importRegionsButton.FlatAppearance.BorderColor = Color.FromArgb(60, 90, 120);
        new ToolTip().SetToolTip(_importRegionsButton, "Import custom regions from a JSON file (duplicates get '-imp' suffix)");
        _importRegionsButton.Click += OnImportRegionsClick;
        _toolbar.Controls.Add(_importRegionsButton);
        buttonLeft += 58;

        // Thin separator.
        _toolbar.Controls.Add(new Label { Left = buttonLeft, Top = 2, Width = 1, Height = 30, BackColor = Color.FromArgb(60, 60, 60) });
        buttonLeft += 10;

        var checkBoxShowCoordPanel = new CheckBox
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
        buttonLeft += checkBoxShowCoordPanel.PreferredSize.Width + 6;

        var checkBoxShowFooterPanel = new CheckBox
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
        _toolbar.Controls.Add(checkBoxShowCoordPanel);
        _toolbar.Controls.Add(checkBoxShowFooterPanel);
        buttonLeft += checkBoxShowFooterPanel.PreferredSize.Width + 12;

        // Grid overlay toggle.
        var checkBoxShowGrid = new CheckBox
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
        new ToolTip().SetToolTip(checkBoxShowGrid, "Overlay a Cartesian complex-plane grid on the fractal view");
        _toolbar.Controls.Add(checkBoxShowGrid);

        // ── Footer panel ──────────────────────────────────────────────────────

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
        _footerPanel.Controls.Add(_statusLabel);
        _footerPanel.Visible = true;

        checkBoxShowFooterPanel.Click += (s, e) => _footerPanel.Visible = checkBoxShowFooterPanel.Checked;

        // ── Coordinate / Navigate panel ───────────────────────────────────────

        _coordPanel = new Panel
        {
            Height = 34,
            Dock = DockStyle.Top,
            BackColor = Color.FromArgb(22, 22, 22),
            Visible = false,   // hidden until user ticks Navigate
        };
        checkBoxShowCoordPanel.Click += (s, e) => _coordPanel.Visible = checkBoxShowCoordPanel.Checked;

        buttonLeft = 8;
        MakeLbl("CX:", buttonLeft, _coordPanel); buttonLeft += 28;
        _txCX = MakeTx(buttonLeft, 182, _coordPanel, "Real part of the view centre"); buttonLeft += 190;
        MakeLbl("CY:", buttonLeft, _coordPanel); buttonLeft += 28;
        _txCY = MakeTx(buttonLeft, 182, _coordPanel, "Imaginary part of the view centre"); buttonLeft += 190;
        MakeLbl("Zoom:", buttonLeft, _coordPanel); buttonLeft += 44;
        _txZoom = MakeTx(buttonLeft, 112, _coordPanel, "Zoom factor (1 = full view; larger = zoomed in)"); buttonLeft += 120;
        MakeLbl("Iter:", buttonLeft, _coordPanel); buttonLeft += 38;
        _txIter = MakeTx(buttonLeft, 72, _coordPanel, "Maximum iteration count (auto-computed by quality+zoom; no upper limit)");
        buttonLeft += 80;

        // Lock checkbox — NEW
        _chkLockIter = new CheckBox
        {
            Text = "Lock",
            Left = buttonLeft,
            Top = 8,
            AutoSize = true,
            AutoCheck = true,
            ForeColor = Color.FromArgb(200, 200, 120),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            BackColor = Color.Transparent,
            Checked = false,
        };
        new ToolTip().SetToolTip(_chkLockIter,
            "When checked, the Iter value is locked — pan, zoom and region changes will not recalculate it");
        _chkLockIter.CheckedChanged += OnIterLockChanged;
        _coordPanel.Controls.Add(_chkLockIter);
        buttonLeft += _chkLockIter.PreferredSize.Width + 8;

        _goButton = MakeBtn("Go", 38);
        _goButton.BackColor = Color.FromArgb(40, 80, 40);
        _goButton.Left = buttonLeft;
        _goButton.FlatAppearance.BorderColor = Color.FromArgb(70, 120, 70);
        _goButton.Click += OnGoClick;
        _coordPanel.Controls.Add(_goButton);

        // ── Render panel ──────────────────────────────────────────────────────

        _renderPanel = new RenderPanel { Dock = DockStyle.Fill, Cursor = Cursors.Cross };
        _renderPanel.MouseWheel += OnMouseWheel;
        _renderPanel.MouseDown += OnMouseDown;
        _renderPanel.MouseMove += OnMouseMove;
        _renderPanel.MouseUp += OnMouseUp;

        // ── Grid overlay panel ────────────────────────────────────────────────
        // FIX: Visible = false here (in constructor, before handle is created).
        // The handle for WS_EX_LAYERED windows is only valid once the form is
        // shown.  Setting Visible = true before then throws Win32Exception.
        // The grid checkbox handler (below) safely defers visibility until load.
        _gridPanel = new GridOverlayPanel(
            getCenter: () => (_centerX, _centerY),
            getZoom: () => _zoom,
            getPanelSize: () => _renderPanel.ClientSize,
            getSwatchColor: () => GetSwatchColor())
        {
            Visible = false,   // never set true before handle exists
        };

        // Grid toggle — safe: only runs after Load (user clicks the checkbox).
        checkBoxShowGrid.Click += (s, e) =>
        {
            _gridVisible = checkBoxShowGrid.Checked;
            if (_gridPanel.IsHandleCreated)
            {
                _gridPanel.Visible = _gridVisible;
                if (_gridVisible) _gridPanel.Invalidate();
            }
        };

        //Context menu for render panel (NEW)
        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("Save Image…", null, (s, e) => OnScreenshotClick(s, e));
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Reset View", null, (s, e) => OnResetClick(s, e));
        contextMenu.Items.Add("Span Monitors", null, (s, e) => OnSpanMonitorsClick(s, e));
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Start/Stop Slideshow", null, (s, e) => OnSlideshowClick(s, e));
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Save Current View", null, (s, e) => OnSaveViewClick(s, e));
        contextMenu.Items.Add("Navigate", null, (s, e) =>
        {
            checkBoxShowCoordPanel.Checked = !checkBoxShowCoordPanel.Checked;
            _coordPanel.Visible = checkBoxShowCoordPanel.Checked;
        });
        contextMenu.Items.Add("Status", null, (s, e) =>
        {
            checkBoxShowFooterPanel.Checked = !checkBoxShowFooterPanel.Checked;
            _footerPanel.Visible = checkBoxShowFooterPanel.Checked;
        });
        _renderPanel.ContextMenuStrip = contextMenu;

        // Docking / Z-order: Fill first, then Top-docked in reverse, footer last.
        Controls.Add(_renderPanel);
        Controls.Add(_gridPanel);
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
            _renderer = new DirectXRenderer(_renderPanel.Handle, w, h);
            _calculator = new MandelbrotCalculator(w, h);
            _colorThemeCombo.Text = Models.ColorPalette.GetStaticName(_calculator.ColorMap);
            ApplyViewState();
            TriggerCalculation();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"DirectX 11 initialisation failed:\n\n{ex.Message}\n\n" +
                "Ensure your GPU supports Feature Level 10.0+\n" +
                "and Vortice.DirectX 3.8.3 packages are installed.",
                "Initialisation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Application.Exit();
        }

        // Grid panel: handle now exists — safe to position (but NOT to show;
        // that only happens when user ticks the Grid checkbox).
        PositionGridPanel();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Quality
    // ─────────────────────────────────────────────────────────────────────────

    private void OnQualityComboChanged(object? sender, EventArgs e)
    {
        int idx = _qualityCombo.SelectedIndex;
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
            foreach (var name in palettes.ToImmutableSortedDictionary().Keys)
                _colorThemeCombo.Items.Add(name);
        }
    }

    private void OnColorThemeChanged(object? sender, EventArgs e)
    {
        string name = _colorThemeCombo.SelectedItem?.ToString() ?? "";
        var map = Models.ColorPalette.GetPaletteByName(name);
        if (_calculator != null)
        {
            _calculator.ColorMap = map;
            TriggerCalculation();
        }
    }

    private Color GetSwatchColor()
    {
        if (_calculator?.ColorMap == null) return Color.White;
        _calculator.ColorMap.MaxIterations = 500;
        int argb = _calculator.ColorMap.SwatchSample;
        return Color.FromArgb((argb >> 16) & 0xFF, (argb >> 8) & 0xFF, argb & 0xFF);
    }

    private static Color ComputeContrastColor(Color swatch, bool fade = false)
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
            fade ? 75 : 255,
            (int)System.Math.Clamp((rr + m) * 255f, 0, 255),
            (int)System.Math.Clamp((gg + m) * 255f, 0, 255),
            (int)System.Math.Clamp((bb + m) * 255f, 0, 255));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Reset
    // ─────────────────────────────────────────────────────────────────────────

    private void OnResetClick(object? sender, EventArgs e)
    {
        StopSlideshow();
        _centerX = DefaultCenterX;
        _centerY = DefaultCenterY;
        _zoom = DefaultZoom;
        _regionCombo.SelectedIndex = 0;
        ApplyViewState();
        TriggerCalculation();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Iteration Lock (NEW)
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
                "Iter: integer ≥ 64",
                "Invalid Coordinates",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _centerX = cx;
        _centerY = cy;
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
        _regionCombo.Items.Add("— select region —");
        foreach (var r in FractalRegionLibrary.Instance.All)
            _regionCombo.Items.Add(r.Name);
        _regionCombo.SelectedIndex = 0;
        _regionCombo.SelectedIndexChanged += OnRegionComboChanged;
        UpdateDelRegionButton();
    }

    private void OnRegionComboChanged(object? sender, EventArgs e)
    {
        UpdateDelRegionButton();

        string? name = _regionCombo.SelectedItem?.ToString();
        if (string.IsNullOrEmpty(name) || name == "— select region —") return;

        var region = FractalRegionLibrary.Instance.FindByName(name);
        if (region == null) return;

        ApplyRegion(region);
        TriggerCalculation();

        new ToolTip().SetToolTip(_regionCombo, region.Description);
    }

    /// <summary>Applies a FractalRegion to the view state, respecting the iteration lock.</summary>
    private void ApplyRegion(FractalRegion region)
    {
        _centerX = region.CenterX;
        _centerY = region.CenterY;
        _quality = region.QualityPreset;
        _qualityCombo.Text = region.QualityPresetName;
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
            CenterY = _centerY,
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
            if (FractalRegionLibrary.Instance.FindByName(candidate) != null)
            {
                candidate = region.Name + "-imp";
                int suffix = 2;
                while (FractalRegionLibrary.Instance.FindByName(candidate) != null)
                    candidate = region.Name + "-imp-" + suffix++;
                region.Name = candidate;
                renamed++;
            }

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

        Task.Run(() => SlideshowLoop(cts.Token), cts.Token)
            .ContinueWith(t =>
            {
                if (!IsHandleCreated || _disposed) return;
                Invoke(() =>
                {
                    _slideshowRunning = false;
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

    private async Task SlideshowLoop(CancellationToken ct)
    {
        var builtIns = new List<FractalRegion>(FractalRegionLibrary.Instance.BuiltIns);
        var paletteNames = GetAllPaletteNames();
        if (builtIns.Count == 0 || paletteNames.Count == 0) return;

        const int regionDurationMs = 30_000;   // 30 s per region
        const int themeDurationMs = 10_000;   // 10 s per theme within a region
        const int fadeDurationMs = 2_000;   // 2 s cross-fade
        const int fadeSteps = 20;        // frames during cross-fade
        const int fadeStepMs = fadeDurationMs / fadeSteps;

        int lastRegionIdx = -1;
        int lastThemeIdx = -1;

        while (!ct.IsCancellationRequested)
        {
            // ── Pick a new region different from the last ─────────────────────
            int regionIdx;
            do { regionIdx = _slideshowRng.Next(builtIns.Count); }
            while (builtIns.Count > 1 && regionIdx == lastRegionIdx);
            lastRegionIdx = regionIdx;
            var region = builtIns[regionIdx];

            // ── Pick an initial theme ─────────────────────────────────────────
            int themeIdx;
            do { themeIdx = _slideshowRng.Next(paletteNames.Count); }
            while (paletteNames.Count > 1 && themeIdx == lastThemeIdx);
            lastThemeIdx = themeIdx;
            string themeName = paletteNames[themeIdx];

            // ── Render the new region with the initial theme ───────────────────
            // The calculation runs on this background thread.
            uint[]? previousBuffer = null;
            if (_calculator != null && _renderer != null)
            {
                // Snapshot old buffer for region cross-fade.
                uint[] oldBuf = await Task.Run(() =>
                {
                    if (_calculator == null) return Array.Empty<uint>();
                    var copy = new uint[_calculator.ColorBuffer.Length];
                    _calculator.ColorBuffer.CopyTo(copy, 0);
                    return copy;
                }, ct);

                // Apply region & theme on UI thread.
                if (ct.IsCancellationRequested) return;
                await InvokeAsync(() =>
                {
                    if (_disposed) return;
                    ApplyRegion(region);
                    var map = Models.ColorPalette.GetPaletteByName(themeName);
                    if (_calculator != null) _calculator.ColorMap = map;
                    // Update UI controls to reflect the active region/theme.
                    for (int i = 0; i < _regionCombo.Items.Count; i++)
                        if (_regionCombo.Items[i]?.ToString() == region.Name)
                        { _regionCombo.SelectedIndex = i; break; }
                    _colorThemeCombo.Text = themeName;
                    SetStatus($"Slideshow: {region.Name}  •  {themeName}");
                });

                // Calculate the new frame (background thread).
                if (ct.IsCancellationRequested) return;
                uint[] newBuf = await Task.Run(() =>
                {
                    if (_calculator == null) return Array.Empty<uint>();
                    _calculator.Calculate(ct);
                    var copy = new uint[_calculator.ColorBuffer.Length];
                    _calculator.ColorBuffer.CopyTo(copy, 0);
                    return copy;
                }, ct);

                if (ct.IsCancellationRequested) return;

                // Cross-fade from old region buffer to new region buffer.
                if (oldBuf.Length == newBuf.Length && oldBuf.Length > 0)
                {
                    await CrossFade(oldBuf, newBuf, fadeSteps, fadeStepMs, ct);
                }
                else
                {
                    // Sizes differ (resize happened) — just show the new frame.
                    await InvokeAsync(() =>
                    {
                        if (!_disposed && _renderer != null && _calculator != null)
                            _renderer.UpdateTexture(newBuf, _calculator.Width, _calculator.Height);
                    });
                }

                previousBuffer = newBuf;
            }

            // ── Cycle through themes for the remainder of the region slot ─────
            long regionStartMs = Environment.TickCount64;
            while (!ct.IsCancellationRequested)
            {
                long elapsed = Environment.TickCount64 - regionStartMs;
                if (elapsed >= regionDurationMs) break;

                // Wait for the theme duration (minus fade time).
                int themeWait = System.Math.Max(0, themeDurationMs - fadeDurationMs);
                await DelayWithCancel(themeWait, ct);
                if (ct.IsCancellationRequested) return;

                elapsed = Environment.TickCount64 - regionStartMs;
                if (elapsed >= regionDurationMs) break;

                // Pick a new theme.
                int newThemeIdx;
                do { newThemeIdx = _slideshowRng.Next(paletteNames.Count); }
                while (paletteNames.Count > 1 && newThemeIdx == lastThemeIdx);
                lastThemeIdx = newThemeIdx;
                string newThemeName = paletteNames[newThemeIdx];

                // Render with new theme on background thread.
                if (_calculator == null || _renderer == null) break;

                uint[] oldThemeBuf = previousBuffer ?? Array.Empty<uint>();

                await InvokeAsync(() =>
                {
                    if (_disposed) return;
                    var map = Models.ColorPalette.GetPaletteByName(newThemeName);
                    if (_calculator != null) _calculator.ColorMap = map;
                    _colorThemeCombo.Text = newThemeName;
                    SetStatus($"Slideshow: {region.Name}  •  {newThemeName}");
                });

                if (ct.IsCancellationRequested) return;

                uint[] newThemeBuf = await Task.Run(() =>
                {
                    if (_calculator == null) return Array.Empty<uint>();
                    _calculator.Calculate(ct);
                    var copy = new uint[_calculator.ColorBuffer.Length];
                    _calculator.ColorBuffer.CopyTo(copy, 0);
                    return copy;
                }, ct);

                if (ct.IsCancellationRequested) return;

                if (oldThemeBuf.Length == newThemeBuf.Length && oldThemeBuf.Length > 0)
                    await CrossFade(oldThemeBuf, newThemeBuf, fadeSteps, fadeStepMs, ct);
                else
                {
                    await InvokeAsync(() =>
                    {
                        if (!_disposed && _renderer != null && _calculator != null)
                            _renderer.UpdateTexture(newThemeBuf, _calculator.Width, _calculator.Height);
                    });
                }

                previousBuffer = newThemeBuf;
            }
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

            await InvokeAsync(() =>
            {
                if (!_disposed && _renderer != null)
                    _renderer.UpdateTexture(blended, w, h);
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
        if (_spanning) ExitSpanMode(); else EnterSpanMode();
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
            Title = _spanning ? "Save Wallpaper Screenshot" : "Save Mandelbrot Screenshot",
            Filter = "PNG Image (*.png)|*.png|TIFF Image (*.tiff;*.tif)|*.tiff;*.tif|BMP Image (*.bmp)|*.bmp",
            FilterIndex = 1,
            DefaultExt = "png",
            FileName = $"Mandelbrot_{colorName}_{regionName}" +
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
        string wm = $"Fracturing Fog{(!string.IsNullOrEmpty(CurrentRegionName()) ? " - " + CurrentRegionName() : "")}" +
                      $"{(!string.IsNullOrEmpty(CurrentColorMapName()) ? " - " + CurrentColorMapName() : "")}";

        if (_spanning) TakeWallpaperScreenshot(path, format, wm);
        else TakeNormalScreenshot(path, format, wm);
    }

    private void TakeNormalScreenshot(string path, ImageFormat format, string waterMark)
    {
        int w = _calculator!.Width;
        int h = _calculator!.Height;
        uint[] pixels = _calculator!.ColorBuffer;
        try
        {
            SavePixelsToFile(pixels, w, h, path, format, waterMark, ComputeContrastColor(GetSwatchColor(), true));
            SetStatus($"Saved  {Path.GetFileName(path)}  ({w}×{h},  {new FileInfo(path).Length / 1024:N0} KB)");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Save failed:\n{ex.Message}", "Screenshot Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void TakeWallpaperScreenshot(string path, ImageFormat format, string waterMark)
    {
        Rectangle vs = SystemInformation.VirtualScreen;
        int fullW = vs.Width;
        int fullH = vs.Height;

        int toolbarH = 0;
        foreach (Control c in Controls)
            if (c.Dock == DockStyle.Top) toolbarH += c.Height;

        double cx = _calculator!.CenterX;
        double cy = _calculator!.CenterY;
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
                CenterY = cy,
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
                    SavePixelsToFile(result.ColorBuffer, result.Width, result.Height,
                        path, format, waterMark, ComputeContrastColor(GetSwatchColor(), true));
                    SetStatus($"Wallpaper saved  →  {Path.GetFileName(path)}" +
                              $"  ({result.Width}×{result.Height} px,  {new FileInfo(path).Length / 1024:N0} KB)" +
                              $"  [{sw.ElapsedMilliseconds} ms]");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to save wallpaper:\n\n{ex.Message}",
                        "Screenshot Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            });
        }, TaskScheduler.Default);
    }

    private static unsafe void SavePixelsToFile(
        uint[] pixels, int w, int h, string path, ImageFormat format,
        string watermarkText, Color fontColor)
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
                ep.Param[0] = new EncoderParameter(Encoder.Compression, (long)EncoderValue.CompressionLZW);
                bmp.Save(path, codec, ep);
            }
            else bmp.Save(path, format);
        }
        else bmp.Save(path, format);

        if (!string.IsNullOrEmpty(watermarkText))
        {
            using var g = Graphics.FromImage(bmp);
            AddWaterMark(g, watermarkText, w, h, fontColor);
            bmp.Save(path, format);
        }
    }

    private static void AddWaterMark(Graphics g, string text, int width, int height, Color fontColor)
    {
        using var font = new Font("Segoe UI", 16, FontStyle.Bold, GraphicsUnit.Pixel);
        var sz = g.MeasureString(text, font);
        var pos = new PointF(width - sz.Width - 20, height - sz.Height - 12);
        using var brush = new SolidBrush(fontColor);
        g.DrawString(text, font, brush, pos);
        using var fontSmall = new Font("Segoe UI", 8, FontStyle.Bold, GraphicsUnit.Pixel);
        var sz2 = g.MeasureString(text, fontSmall);
        g.DrawString("Something mundane to include in the image.", fontSmall, brush,
            new PointF(width - sz2.Width - 105, height - sz2.Height - 2));
        g.Save();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Mouse: zoom
    // ─────────────────────────────────────────────────────────────────────────

    private void OnMouseWheel(object? sender, MouseEventArgs e)
    {
        if (_calculator == null || _slideshowRunning) return;

        double wf = _quality.WheelZoomFactor;
        double factor = e.Delta > 0 ? wf : 1.0 / wf;
        double scale = CurrentScale();
        double ox = e.X - _renderPanel.ClientSize.Width * 0.5;
        double oy = e.Y - _renderPanel.ClientSize.Height * 0.5;
        double compX = _centerX + ox * scale;
        double compY = _centerY + oy * scale;

        _zoom = System.Math.Clamp(_zoom * factor, _quality.ZoomMin, _quality.ZoomMax);

        double ns = CurrentScale();
        _centerX = compX - ox * ns;
        _centerY = compY - oy * ns;

        ApplyViewState();
        TriggerCalculation();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Mouse: pan
    // ─────────────────────────────────────────────────────────────────────────

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _slideshowRunning) return;
        _panning = true;
        _panStartScreen = e.Location;
        _panStartCX = _centerX;
        _panStartCY = _centerY;
        _renderPanel.Cursor = Cursors.SizeAll;
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_panning || _calculator == null) return;
        double scale = CurrentScale();
        _centerX = _panStartCX - (e.X - _panStartScreen.X) * scale;
        _centerY = _panStartCY - (e.Y - _panStartScreen.Y) * scale;
        ApplyViewState();
        TriggerCalculation();
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _panning = false;
        _renderPanel.Cursor = Cursors.Cross;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Resize
    // ─────────────────────────────────────────────────────────────────────────

    private void OnFormResize(object? sender, EventArgs e)
    {
        if (_renderer == null || _calculator == null) return;
        if (WindowState == FormWindowState.Minimized) return;

        int w = _renderPanel.ClientSize.Width;
        int h = _renderPanel.ClientSize.Height;
        if (w < 1 || h < 1) return;

        _renderer.Resize(w, h);
        _calculator.Resize(w, h);
        ApplyViewState();
        TriggerCalculation();
        PositionGridPanel();
    }

    private void PositionGridPanel()
    {
        _gridPanel.Bounds = _renderPanel.Bounds;
        if (_gridVisible && _gridPanel.IsHandleCreated) _gridPanel.Invalidate();
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
        _calculator.CenterY = _centerY;
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
    // Async calculation
    // ─────────────────────────────────────────────────────────────────────────

    private void TriggerCalculation()
    {
        if (_calculator == null) return;

        CancellationTokenSource cts;
        lock (_calcLock)
        {
            _calcCts?.Cancel();
            _calcCts = new CancellationTokenSource();
            cts = _calcCts;
        }

        var token = cts.Token;
        var calc = _calculator;
        var renderer = _renderer;

        SetStatus("Calculating…");
        var sw = Stopwatch.StartNew();

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
                    renderer.UpdateTexture(calc.ColorBuffer, calc.Width, calc.Height);
                    if (_gridVisible && _gridPanel.IsHandleCreated) _gridPanel.Invalidate();
                    string precTag = calc.IsHighPrecisionActive ? "[DD]" : "[SP]";
                    SetStatus(
                        $"cx={calc.CenterX:G12}  cy={calc.CenterY:G12}  " +
                        $"zoom={calc.Zoom:G6}  iter={calc.MaxIterations}  " +
                        $"{precTag}  [{ms} ms  {calc.Width}×{calc.Height}]" +
                        (_iterLocked ? "  [ITER LOCKED]" : ""));

                    if (_gridVisible && _gridPanel.IsHandleCreated) _gridPanel.Invalidate();
                });
            }
        }, TaskScheduler.Default);
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
// Cartesian grid overlay panel
// ─────────────────────────────────────────────────────────────────────────────
// ARCHITECTURE NOTE — why sibling, not child:
// RenderPanel sets WS_EX_NOREDIRECTIONBITMAP so that D3D11 presents directly
// to the screen.  Child windows of such a panel never composite over it.
// GridOverlayPanel is therefore a sibling (added after _renderPanel in the
// form's Controls collection) and uses WS_EX_LAYERED + UpdateLayeredWindow
// with per-pixel alpha to composite GDI+ over the D3D11 surface.
//
// FIX (v5): Never set Visible = true before IsHandleCreated is true.
// The underlying HWND for a WS_EX_LAYERED window is only valid once the
// containing form has been shown.  Attempting to show the panel from the
// constructor throws Win32Exception "Error creating window handle."
// The grid checkbox handler now guards with IsHandleCreated.

internal sealed class GridOverlayPanel : Control
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
        ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc,
        uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    private const uint ULW_ALPHA = 2;

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

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00080000; // WS_EX_LAYERED
            cp.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT
            return cp;
        }
    }

    protected override void OnPaint(PaintEventArgs e) { }
    protected override void OnPaintBackground(PaintEventArgs e) { }

    public new void Invalidate()
    {
        if (!IsHandleCreated || Width < 1 || Height < 1) return;
        UpdateLayeredContent();
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x0084;
        const int HTTRANSPARENT = -1;
        if (m.Msg == WM_NCHITTEST) { m.Result = (IntPtr)HTTRANSPARENT; return; }
        base.WndProc(ref m);
    }

    private void UpdateLayeredContent()
    {
        int w = Width, h = Height;
        if (w < 1 || h < 1) return;

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            DrawCartesianGrid(g, w, h);
        }

        IntPtr screenDC = GetDC(IntPtr.Zero);
        IntPtr memDC = CreateCompatibleDC(screenDC);
        IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0, 0, 0, 0));
        IntPtr hOld = SelectObject(memDC, hBmp);

        var pos = new POINT { x = Left, y = Top };
        var size = new SIZE { cx = w, cy = h };
        var srcPt = new POINT { x = 0, y = 0 };
        var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };

        UpdateLayeredWindow(Handle, screenDC, ref pos, ref size, memDC, ref srcPt, 0, ref blend, ULW_ALPHA);

        SelectObject(memDC, hOld);
        DeleteObject(hBmp);
        DeleteDC(memDC);
        ReleaseDC(IntPtr.Zero, screenDC);
    }

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
        for (double wx = System.Math.Ceiling(xMin / gridStep) * gridStep; wx <= xMax + gridStep * 0.01; wx += gridStep)
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
        for (double wy = System.Math.Ceiling(yMin / gridStep) * gridStep; wy <= yMax + gridStep * 0.01; wy += gridStep)
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
        double abs = System.Math.Abs(v);
        if (abs == 0) return "0";
        if (abs >= 0.001 && abs < 10000)
        {
            int d = System.Math.Clamp(-(int)System.Math.Floor(System.Math.Log10(abs)) + 2, 0, 6);
            return v.ToString("F" + d, System.Globalization.CultureInfo.InvariantCulture);
        }
        return v.ToString("G4", System.Globalization.CultureInfo.InvariantCulture);
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