// MainForm.cs  — v4
//
// Additions over v3
//   • Quality ComboBox in the top toolbar (Draft / Standard / High / Ultra).
//   • Quality-aware wheel zoom factor — coarser for exploration, finer for depth.
//   • Quality-aware zoom clamp — prevents zooming beyond the active tier's limit.
//   • Quality-aware iteration scaling — iterations auto-increase with zoom depth
//     according to each tier's IterBase + IterPerDecade formula.
//   • Precision indicator in the status bar: "SP" (standard double) or "DD"
//     (double-double, ~31 decimal digits) shown in brackets.
//   • When the user switches to a lower quality tier while zoomed beyond the new
//     tier's ZoomMax, the zoom is clamped and the view recalculates automatically.
//   • All v3 features (coordinate entry, region system, screenshot, span) preserved.

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

namespace FracturingFog;

public sealed class MainForm : Form
{
    // ── UI: top toolbar ───────────────────────────────────────────────────────
    private readonly Panel _toolbar;
    private readonly Button _resetButton;
    private readonly Button _spanButton;
    private readonly Button _screenshotButton;
    private readonly ComboBox _qualityCombo;
    private readonly ComboBox _colorThemeCombo;
    private readonly Label _statusLabel;

    // ── UI: coordinate / region bar ───────────────────────────────────────────
    private readonly Panel _coordPanel;
    private readonly TextBox _txCX;
    private readonly TextBox _txCY;
    private readonly TextBox _txZoom;
    private readonly TextBox _txIter;
    private readonly Button _goButton;
    private readonly ComboBox _regionCombo;
    private readonly Button _saveViewButton;
    private readonly Button _delRegionButton;
    private readonly Button _exportRegionsButton;
    private readonly Button _importRegionsButton;

    // ── Render panel ──────────────────────────────────────────────────────────
    private readonly RenderPanel _renderPanel;

    // ── Grid overlay panel ────────────────────────────────────────────────────
    // Transparent WinForms panel that sits directly on top of _renderPanel and
    // paints the Cartesian grid via GDI+.  Visible only when the Grid checkbox
    // is checked.  Because it has no DirectX involvement it never interferes
    // with the D3D11 swap-chain — it is simply a child control layered above it.
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
    private Point _panStartScreen;   // screen coords where left-button was pressed
    private double _panStartCX;       // complex-plane centre at that moment
    private double _panStartCY;

    // ── Multi-monitor span state ─────────────────────────────────────────────
    //
    // When _spanning is true the form is borderless and positioned exactly over
    // SystemInformation.VirtualScreen (the bounding rectangle of all monitors).
    // _preSpanBounds and _preSpanBorderStyle store what to restore to.

    private bool _spanning;
    private Rectangle _preSpanBounds;
    private FormBorderStyle _preSpanBorderStyle;
    private FormWindowState _preSpanWindowState;

    // ── Async calculation ─────────────────────────────────────────────────────

    private CancellationTokenSource? _calcCts;
    private readonly object _calcLock = new();

    // Separate CTS for offscreen wallpaper renders so they can be cancelled
    // independently of the live render (e.g. when the form is closed mid-render).
    private CancellationTokenSource? _wallpaperCts;
    private readonly object _wallpaperLock = new();

    private bool _disposed;

    // ─────────────────────────────────────────────────────────────────────────
    // Constructor
    // ─────────────────────────────────────────────────────────────────────────

    public MainForm()
    {
        Text = "Fracturing Fog  —  Mandelbrot Explorer  (DirectX 11 · Vortice 3.8.3)";
        ClientSize = new Size(1200, 768); // -38 for toolbar and chrome
        MinimumSize = new Size(1072, 384);
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

        _spanButton = MakeBtn("Span", 55); // Monitors");
        _spanButton.Left = buttonLeft;
        _spanButton.Click += OnSpanMonitorsClick;
        _toolbar.Controls.Add(_spanButton);

        buttonLeft += 58;

        _screenshotButton = MakeBtn("Image", 55); // "Screenshot");
        _screenshotButton.Left = buttonLeft;
        _screenshotButton.Click += OnScreenshotClick;
        _toolbar.Controls.Add(_screenshotButton);

        buttonLeft += 58;

        // Thin separator.
        _toolbar.Controls.Add(new Label
        {
            Left = buttonLeft,
            Top = 4,
            Width = 1,
            Height = 30,
            BackColor = Color.FromArgb(65, 65, 65)
        });

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
            Width = 80, //112, 
            Height = 26,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(45, 45, 45),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };

        foreach (var p in QualityPreset.All) _qualityCombo.Items.Add(p.Name);
        _qualityCombo.SelectedIndex = 1;  // Standard default

        // Tooltip shows description of selected quality tier.
        var qualityTip = new ToolTip();
        _qualityCombo.SelectedIndexChanged += (s, e) =>
        {
            int i = _qualityCombo.SelectedIndex;
            if (i >= 0 && i < QualityPreset.All.Length)
                qualityTip.SetToolTip(_qualityCombo, QualityPreset.All[i].Description);
            OnQualityComboChanged(s, e);
        };

        // Set initial tooltip.
        qualityTip.SetToolTip(_qualityCombo, QualityPreset.Standard.Description);
        _toolbar.Controls.Add(_qualityCombo);
        buttonLeft += 80; // 120;

        // Theme separator.
        _toolbar.Controls.Add(new Label
        {
            Left = buttonLeft,
            Top = 4,
            Width = 1,
            Height = 30,
            BackColor = Color.FromArgb(65, 65, 65)
        }); buttonLeft += 10;

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
        _toolbar.Controls.Add(new Label
        {
            Left = buttonLeft,
            Top = 2,
            Width = 1,
            Height = 30,
            BackColor = Color.FromArgb(60, 60, 60)
        });

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
        buttonLeft += 180; // 200;

        _saveViewButton = MakeBtn("Save", 55); //new Button
        _saveViewButton.Left = buttonLeft;
        _saveViewButton.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90);
        _saveViewButton.Click += OnSaveViewClick;
        _toolbar.Controls.Add(_saveViewButton);

        buttonLeft += 58; // 96;

        _delRegionButton = MakeBtn("Delete", 55); //new Button
        _delRegionButton.Left = buttonLeft;
        _delRegionButton.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90);
        _delRegionButton.Click += OnDelRegionClick;
        _toolbar.Controls.Add(_delRegionButton);
        buttonLeft += 58;

        _exportRegionsButton = MakeBtn("Exp…", 55); //new Button
        _exportRegionsButton.Left = buttonLeft;
        _exportRegionsButton.FlatAppearance.BorderColor = Color.FromArgb(60, 90, 120);
        new ToolTip().SetToolTip(_exportRegionsButton, "Export all custom regions to a JSON file");
        _exportRegionsButton.Click += OnExportRegionsClick;
        _toolbar.Controls.Add(_exportRegionsButton);
        buttonLeft += 58;

        _importRegionsButton = MakeBtn("Imp…", 55); //new Button
        _importRegionsButton.Left = buttonLeft;
        _importRegionsButton.FlatAppearance.BorderColor = Color.FromArgb(60, 90, 120);
        new ToolTip().SetToolTip(_importRegionsButton, "Import custom regions from a JSON file (duplicates get '-imp' suffix)");
        _importRegionsButton.Click += OnImportRegionsClick;
        _toolbar.Controls.Add(_importRegionsButton);

        buttonLeft += 58;

        // Thin separator after import/export.
        _toolbar.Controls.Add(new Label
        {
            Left = buttonLeft,
            Top = 2,
            Width = 1,
            Height = 30,
            BackColor = Color.FromArgb(60, 60, 60)
        });

        buttonLeft += 10;

        CheckBox _checkBoxShowCoordPanel = new CheckBox
        {
            Text = "Navigate", //"Show coordinate panel",
            Left = buttonLeft,
            Top = 9,
            AutoSize = true,
            AutoCheck = true,
            ForeColor = Color.FromArgb(155, 155, 155),
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            BackColor = Color.Transparent,
            Checked = false,

        };

        buttonLeft += _checkBoxShowCoordPanel.Width - 8;

        CheckBox _checkBoxShowFooterPanel = new CheckBox
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

        buttonLeft += _checkBoxShowCoordPanel.Width + 12;

        // Grid overlay checkbox.
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
        new ToolTip().SetToolTip(checkBoxShowGrid,
            "Overlay a Cartesian complex-plane grid on the fractal view");
        _toolbar.Controls.Add(checkBoxShowGrid);

        _footerPanel = new Panel
        {
            Height = 22,
            Dock = DockStyle.Bottom,
            BackColor = Color.FromArgb(18, 18, 18),
        };

        // Status label — fills the remainder.
        _statusLabel = new Label
        {
            Left = 6, //buttonLeft + 8,
            Top = 0,
            //Width = 700,
            //Height = 22,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(140, 140, 140),
            BackColor = Color.Transparent,
            Font = new Font("Consolas", 8f),
            Text = "Initialising…"
        };

        _footerPanel.Controls.Add(_statusLabel);
        _footerPanel.Visible = true;

        // Assign event handler to the checkbox to toggle the footer panel visibility.
        _checkBoxShowFooterPanel.Click += (s, e) =>
        {
            _footerPanel.Visible = _checkBoxShowFooterPanel.Checked;
        };

        // ── Coordinate / region panel ─────────────────────────────────────────

        _coordPanel = new Panel
        {
            Height = 34,
            Dock = DockStyle.Top,
            BackColor = Color.FromArgb(22, 22, 22),
            Visible = _checkBoxShowCoordPanel.Checked,
        };

        // Assign event handler to the checkbox to toggle the coordinate panel visibility.
        _checkBoxShowCoordPanel.Click += (s, e) =>
        {
            _coordPanel.Visible = _checkBoxShowCoordPanel.Checked;
        };

        //int cx = 8;
        buttonLeft = 8;
        MakeLbl("CX:", buttonLeft, _coordPanel);
        buttonLeft += 28;
        _txCX = MakeTx(buttonLeft, 182, _coordPanel, "Real part of the view centre");
        buttonLeft += 190;
        MakeLbl("CY:", buttonLeft, _coordPanel);
        buttonLeft += 28;
        _txCY = MakeTx(buttonLeft, 182, _coordPanel, "Imaginary part of the view centre");
        buttonLeft += 190;
        MakeLbl("Zoom:", buttonLeft, _coordPanel);
        buttonLeft += 44;
        _txZoom = MakeTx(buttonLeft, 112, _coordPanel, "Zoom factor (1 = full view; larger = zoomed in)");
        buttonLeft += 120;
        MakeLbl("Iter:", buttonLeft, _coordPanel);
        buttonLeft += 38;
        _txIter = MakeTx(buttonLeft, 64, _coordPanel, "Maximum iteration count (auto-computed by quality+zoom)");
        _txIter.Enabled = false;
        buttonLeft += 72;

        _goButton = MakeBtn("Go", 38); //new Button
        _goButton.BackColor = Color.FromArgb(40, 80, 40);
        _goButton.Left = buttonLeft;
        _goButton.FlatAppearance.BorderColor = Color.FromArgb(70, 120, 70);
        _goButton.Click += OnGoClick;
        _coordPanel.Controls.Add(_goButton);
        buttonLeft += 56;


        // ── Render panel ──────────────────────────────────────────────────────
        _renderPanel = new RenderPanel { Dock = DockStyle.Fill, Cursor = Cursors.Cross };
        _renderPanel.MouseWheel += OnMouseWheel;
        _renderPanel.MouseDown += OnMouseDown;
        _renderPanel.MouseMove += OnMouseMove;
        _renderPanel.MouseUp += OnMouseUp;

        // ── Grid overlay panel ────────────────────────────────────────────────
        // GridOverlayPanel sits as a child of _renderPanel so it always matches
        // the render area exactly and resizes with it automatically.
        // It is transparent to mouse events (they fall through to _renderPanel)
        // and paints the Cartesian complex-plane grid in its OnPaint override.
        // The panel asks MainForm for view parameters via delegates each paint
        // so the grid coordinate labels stay perfectly in sync with the view.
        _gridPanel = new GridOverlayPanel(
            getCenter: () => (_centerX, _centerY),
            getZoom: () => _zoom,
            getPanelSize: () => _renderPanel.ClientSize,
            getSwatchColor: () =>
            {
                if (_calculator?.ColorMap == null) return Color.White;
                _calculator.ColorMap.MaxIterations = 500;
                int argb = _calculator.ColorMap.SwatchSample;
                return Color.FromArgb((argb >> 16) & 0xFF, (argb >> 8) & 0xFF, argb & 0xFF);
            })
        {
            Dock = DockStyle.Fill,
            Visible = true,
            Capture = false, // Important: allows mouse events to pass through to _renderPanel.
            
        };
        // Invalidate the grid whenever a new fractal frame arrives.
        _renderPanel.Controls.Add(_gridPanel);

        checkBoxShowGrid.Click += (s, e) =>
        {
            _gridVisible = checkBoxShowGrid.Checked;
            _gridPanel.Visible = _gridVisible;
            _gridPanel.Invalidate();
        };

        // Docking order: Fill first, then Top-docked panels in reverse order.
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

    // ─────────────────────────────────────────────────────────────────────────
    // Initialisation
    // ─────────────────────────────────────────────────────────────────────────

    private void OnLoad(object? sender, EventArgs e)
    {
        // Load persisted user regions.
        FractalRegionLibrary.Instance.Load();
        RebuildRegionCombo();
        int w = _renderPanel.ClientSize.Width;
        int h = _renderPanel.ClientSize.Height;

        try
        {
            _renderer = new DirectXRenderer(_renderPanel.Handle, w, h);
            _calculator = new MandelbrotCalculator(w, h);
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

        // Clamp zoom into the new quality's range.
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
            // Add a non-selectable header item for the type.
            _colorThemeCombo.Items.Add($"— {type} —");
            foreach (var name in palettes.Keys)
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

    // ─────────────────────────────────────────────────────────────────────────
    // Reset
    // ─────────────────────────────────────────────────────────────────────────

    private void OnResetClick(object? sender, EventArgs e)
    {
        _centerX = DefaultCenterX;
        _centerY = DefaultCenterY;
        _zoom = DefaultZoom;
        ApplyViewState();
        TriggerCalculation();
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
                "Iter: integer 64–65536",
                "Invalid Coordinates",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _centerX = cx;
        _centerY = cy;
        _zoom = System.Math.Clamp(zoom, _quality.ZoomMin, _quality.ZoomMax);

        if (_calculator != null && iter > 0)
            _calculator.MaxIterations = iter;

        ApplyViewState();
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

        return double.TryParse(_txCX.Text.Trim(), ns, ic, out cx)
            && double.TryParse(_txCY.Text.Trim(), ns, ic, out cy)
            && double.TryParse(_txZoom.Text.Trim(), ns, ic, out zoom) && zoom > 0
            && int.TryParse(_txIter.Text.Trim(), out iter) && iter >= 64 && iter <= 65536;
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

        _centerX = region.CenterX;
        _centerY = region.CenterY;
        _quality = region.QualityPreset;

        _qualityCombo.Text = region.QualityPresetName;
        _zoom = System.Math.Clamp(region.Zoom, _quality.ZoomMin, _quality.ZoomMax);

        if (_calculator != null && region.Iterations > 0)
        {
            _calculator.Quality = region.QualityPreset;
            _calculator.MaxIterations = region.Iterations;
        }

        ApplyViewState();
        TriggerCalculation();

        // Set tooltip to region description.
        new ToolTip().SetToolTip(_regionCombo, region.Description);
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
    //
    // Serialises the complete UserRegions list to a user-chosen JSON file.
    // The format is identical to the internal regions.json so the file can
    // be shared between installations and re-imported without conversion.
    //
    // Default filename: the currently selected region's name (if any user
    // region is selected) + ".json", otherwise "regions.json".

    private void OnExportRegionsClick(object? sender, EventArgs e)
    {
        var userRegions = FractalRegionLibrary.Instance.UserRegions;

        if (userRegions.Count == 0)
        {
            MessageBox.Show(
                "There are no custom regions to export.\n\n" +
                "Use \"Save View\" to create a custom region first.",
                "Export Regions",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Default filename: selected region name (if it's a user region) else "regions".
        string defaultName = "regions";
        string? selected = _regionCombo.SelectedItem?.ToString();
        if (!string.IsNullOrEmpty(selected) && selected != "— select region —")
        {
            var sel = FractalRegionLibrary.Instance.FindByName(selected);
            if (sel != null && !sel.IsBuiltIn)
                defaultName = selected;
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
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(userRegions, options);
            File.WriteAllText(dlg.FileName, json);

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
    //
    // Deserialises a JSON file (previously exported from this application or
    // hand-authored to the same schema) and appends its entries to the current
    // user-region library.
    //
    // Collision handling
    //   • If an imported region's name already exists (case-insensitive match
    //     against both built-in and user-defined regions), "-imp" is appended.
    //   • If that name also collides, "-imp-2", "-imp-3" … are tried until a
    //     unique name is found.  This prevents any silent overwrites.
    //
    // The internal regions.json is updated atomically (via
    // FractalRegionLibrary.Save) once all entries have been appended.

    private void OnImportRegionsClick(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Import Custom Regions",
            Filter = "JSON File (*.json)|*.json|All Files (*.*)|*.*"
        };

        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        // ── Deserialise the chosen file ───────────────────────────────────────
        List<FractalRegion>? imported;
        try
        {
            string json = File.ReadAllText(dlg.FileName);
            imported = JsonSerializer.Deserialize<List<FractalRegion>>(json);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not read or parse the selected file:\n\n{ex.Message}\n\n" +
                "The file must be a valid JSON array of region objects.",
                "Import Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (imported == null || imported.Count == 0)
        {
            MessageBox.Show(
                "The selected file contains no region entries.",
                "Import Regions",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // ── Append entries, resolving name collisions ─────────────────────────
        int added = 0;
        int renamed = 0;

        foreach (var region in imported)
        {
            // Skip entries with a blank name.
            if (string.IsNullOrWhiteSpace(region.Name)) continue;

            // Ensure RegionType is UserDefined (JsonIgnore means it won't be
            // in the file; default is already UserDefined, but be explicit).
            region.RegionType = RegionType.UserDefined;

            // Find a unique name: try original → original-imp → original-imp-2 …
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

            // AddUserRegion also calls Save() after each entry; we skip that
            // overhead by adding directly to the list and saving once at the end.
            FractalRegionLibrary.Instance.UserRegions.Add(region);
            added++;
        }

        if (added == 0)
        {
            MessageBox.Show(
                "No valid regions were found in the selected file.",
                "Import Regions",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Persist the updated list in one write.
        FractalRegionLibrary.Instance.Save();

        // Refresh the combo box.
        RebuildRegionCombo();

        string summary = added == 1 ? "1 region imported" : $"{added} regions imported";
        if (renamed > 0)
            summary += $" ({renamed} renamed with '-imp' to avoid name collision)";
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
        _spanButton.Text = "Back"; //"Restore";

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
        _spanButton.Text = "Span Monitors";

        FormBorderStyle = _preSpanBorderStyle;
        WindowState = FormWindowState.Normal;
        Bounds = _preSpanBounds;

        if (_preSpanWindowState == FormWindowState.Maximized)
            WindowState = FormWindowState.Maximized;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && _spanning) { ExitSpanMode(); e.Handled = true; return; }
        if (e.KeyCode == Keys.Return &&
            (ActiveControl == _txCX || ActiveControl == _txCY ||
             ActiveControl == _txZoom || ActiveControl == _txIter))
        { OnGoClick(null, EventArgs.Empty); e.Handled = true; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SCREENSHOT
    // ─────────────────────────────────────────────────────────────────────────
    //
    // Two code paths:
    //
    //   NORMAL (not spanning)
    //     Saves the current render-panel pixel buffer at its current dimensions.
    //     Fast — no new computation; uses the ColorBuffer already in memory.
    //
    //   SPAN / WALLPAPER
    //     In span mode the toolbar panels (top bar + coord bar, ~72 px total)
    //     sit at the top of the window.  The render panel — and therefore every
    //     screenshot saved from it — is that many pixels shorter than the full
    //     virtual desktop.  When set as a desktop wallpaper with "Span" mode,
    //     Windows adds black letterbox bars to pad the missing rows.
    //
    //     When _spanning is true this handler:
    //       1. Shows the SaveFileDialog first (no waiting if the user cancels).
    //       2. Allocates a temporary MandelbrotCalculator at VirtualScreen size
    //          (toolbar pixels included — the exact dimensions Windows expects).
    //       3. Copies the current view (CX, CY, Zoom, Iterations, ColorMap,
    //          Quality) so the output is visually identical to what is on screen,
    //          just taller by the toolbar height.
    //       4. Runs the calculation off-screen on a background thread; the live
    //          renderer continues unaffected throughout.
    //       5. Saves and re-enables the button on completion.

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

        // In span mode the suggested filename reflects the full desktop size.
        Rectangle vs = SystemInformation.VirtualScreen;
        string sizeTag = _spanning
            ? $"{vs.Width}x{vs.Height}_wallpaper"
            : $"{_calculator.Width}x{_calculator.Height}";

        using var dlg = new SaveFileDialog
        {
            Title = _spanning
                ? "Save Wallpaper Screenshot (full desktop dimensions)"
                : "Save Mandelbrot Screenshot",
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
        regionName = !string.IsNullOrEmpty(CurrentRegionName()) ? " - " + CurrentRegionName() : "";
        string colorTag = !string.IsNullOrEmpty(CurrentColorMapName()) ? " - " + CurrentColorMapName() : "";
        string waterMark = $"Fracturing Fog{regionName}{colorTag}";

        if (_spanning)
            TakeWallpaperScreenshot(path, format, waterMark);
        else
            TakeNormalScreenshot(path, format, waterMark);
    }

    // ── Normal screenshot — saves the existing ColorBuffer directly ───────────

    private void TakeNormalScreenshot(string path, ImageFormat format, string waterMark)
    {
        int w = _calculator!.Width;
        int h = _calculator!.Height;
        uint[] pixels = _calculator!.ColorBuffer;

        try
        {
            SavePixelsToFile(pixels, w, h, path, format, waterMark);
            SetStatus($"Saved  {Path.GetFileName(path)}  ({w}×{h},  {new FileInfo(path).Length / 1024:N0} KB)");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Save failed:\n{ex.Message}", "Screenshot Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ── Wallpaper screenshot — offscreen render at full virtual-desktop size ──

    private void TakeWallpaperScreenshot(string path, ImageFormat format, string waterMark)
    {
        // Full virtual desktop dimensions — what Windows expects for a spanning wallpaper.
        Rectangle vs = SystemInformation.VirtualScreen;
        int fullW = vs.Width;
        int fullH = vs.Height;

        // Compute the combined height of all DockStyle.Top panels dynamically
        // so the code never hardcodes a pixel value that could change with UI updates.
        int toolbarH = 0;
        foreach (Control c in Controls)
            if (c.Dock == DockStyle.Top) toolbarH += c.Height;

        // Snapshot current view onto the stack before the background thread starts.
        // The live _calculator must never be accessed off the UI thread.
        double cx = _calculator!.CenterX;
        double cy = _calculator!.CenterY;
        double zoom = _calculator!.Zoom;
        int maxIter = _calculator!.MaxIterations;
        IColorMap map = _calculator!.ColorMap;
        QualityPreset q = _quality;

        long mpix = (long)fullW * fullH / 1_000_000;

        // Disable the button to prevent queuing a second wallpaper render.
        _screenshotButton.Enabled = false;
        _screenshotButton.Text = "Rendering…";
        SetStatus($"Rendering wallpaper  {fullW}×{fullH}  ({mpix} MP, +{toolbarH} px over render panel)  …");

        // Cancel any previous wallpaper render still running.
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
            // Temporary calculator — completely independent of the live one.
            // The live renderer keeps running and presenting frames throughout.
            var tempCalc = new MandelbrotCalculator(fullW, fullH);
            tempCalc.CenterX = cx;
            tempCalc.CenterY = cy;
            tempCalc.Zoom = zoom;
            tempCalc.MaxIterations = maxIter;
            tempCalc.ColorMap = map;
            tempCalc.Quality = q;

            tempCalc.Calculate(token);
            token.ThrowIfCancellationRequested();
            return tempCalc;

        }, token)
        .ContinueWith(t =>
        {
            if (!IsHandleCreated || _disposed) return;
            Invoke(() =>
            {
                // Always restore the button regardless of outcome.
                _screenshotButton.Enabled = true;
                _screenshotButton.Text = "Screenshot";

                if (t.IsCanceled)
                {
                    SetStatus("Wallpaper render cancelled.");
                    return;
                }

                if (t.IsFaulted)
                {
                    MessageBox.Show(
                        $"Wallpaper render failed:\n\n{t.Exception?.InnerException?.Message}",
                        "Screenshot Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                MandelbrotCalculator result = t.Result;
                sw.Stop();

                try
                {
                    SavePixelsToFile(result.ColorBuffer, result.Width, result.Height, path, format, waterMark);
                    long kb = new FileInfo(path).Length / 1024;
                    SetStatus($"Wallpaper saved  →  {Path.GetFileName(path)}" +
                              $"  ({result.Width}×{result.Height} px,  {kb:N0} KB)" +
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

    // ── Shared pixel-to-file helper ───────────────────────────────────────────
    //
    // ColorBuffer layout (PackBgra, little-endian x64):
    //   uint = (A<<24)|(R<<16)|(G<<8)|B  →  memory bytes: B G R A
    // GDI Format32bppArgb memory layout: B G R A
    // Layouts are identical — MemoryCopy is always correct.

    private static unsafe void SavePixelsToFile(
        uint[] pixels, int w, int h, string path, ImageFormat format, string watermarkText = "")
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var bmpData = bmp.LockBits(new Rectangle(0, 0, w, h),
                                          ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            fixed (uint* src = pixels)
            {
                if (bmpData.Stride == w * 4)
                    Buffer.MemoryCopy(src, (void*)bmpData.Scan0,
                                      (long)w * h * 4, (long)w * h * 4);
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
        else
        {
            bmp.Save(path, format);
        }

        if (!string.IsNullOrEmpty(watermarkText))
        {
            AddWaterMark(Graphics.FromImage(bmp), watermarkText, w, h);
            bmp.Save(path, format);
        }
    }

    private static void AddWaterMark(Graphics g, string text, int width, int height)
    {
        using var font = new Font("Segoe UI", 16, FontStyle.Bold, GraphicsUnit.Pixel);
        var textSize = g.MeasureString(text, font);
        var pos = new PointF(width - textSize.Width - 10, height - textSize.Height - 10);
        using var brush = new SolidBrush(Color.FromArgb(128, Color.White));

        Debug.WriteLine($"Adding watermark '{text}' at {pos} with size {textSize}");
        g.DrawString(text, font, brush, pos);
        g.Save();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Mouse: zoom
    // ─────────────────────────────────────────────────────────────────────────

    private void OnMouseWheel(object? sender, MouseEventArgs e)
    {
        if (_calculator == null) return;

        // Use the quality preset's wheel factor for the active tier.
        double wf = _quality.WheelZoomFactor;
        double factor = e.Delta > 0 ? wf : 1.0 / wf;

        double scale = CurrentScale();
        double ox = e.X - _renderPanel.ClientSize.Width * 0.5;
        double oy = e.Y - _renderPanel.ClientSize.Height * 0.5;
        double compX = _centerX + ox * scale;
        double compY = _centerY + oy * scale;

        // Clamp to the quality tier's zoom range.
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
        if (e.Button != MouseButtons.Left) return;
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

    private void ApplyViewState()
    {
        if (_calculator == null) return;
        _calculator.CenterX = _centerX;
        _calculator.CenterY = _centerY;
        _calculator.Zoom = _zoom;
        _calculator.Quality = _quality;
        // Auto-compute iterations from quality+zoom (may be overridden by Go button).
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
        if (string.IsNullOrEmpty(selected) || selected == "— select region —")
            return "";
        return selected;
    }

    private string CurrentColorMapName()
    {
        if (_calculator?.ColorMap == null) return "";
        var prop = _calculator.ColorMap.GetType().GetProperty("Name", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (prop == null) return "Theme";
        return prop.GetValue(null)?.ToString() ?? "";
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
                    // Refresh grid overlay whenever a new frame lands.
                    if (_gridVisible) _gridPanel.Invalidate();
                    // Precision mode tag: [SP] or [DD].
                    string precTag = calc.IsHighPrecisionActive ? "[DD]" : "[SP]";
                    SetStatus(
                            $"cx={calc.CenterX:G12}  cy={calc.CenterY:G12}  " +
                            $"zoom={calc.Zoom:G6}  iter={calc.MaxIterations}  " +
                            $"{precTag}  [{ms} ms  {calc.Width}×{calc.Height}]");

                    if (_gridVisible) _gridPanel.Invalidate();
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

        // ── Swatch: sample the palette at 30 % of MaxIterations ──────────────
        // Uses SwatchSample (default interface method) instead of Map(0,0,0)
        // which always returned black in the previous version.
        map.MaxIterations = 500;
        int argb = map.SwatchSample;
        var swatch = Color.FromArgb((argb >> 16) & 0xFF, (argb >> 8) & 0xFF, argb & 0xFF);

        // Draw the swatch rectangle on the left.
        var swatchRect = new Rectangle(e.Bounds.X + 2, e.Bounds.Y + 3, 18, e.Bounds.Height - 6);
        using var sb = new SolidBrush(swatch);
        e.Graphics.FillRectangle(sb, swatchRect);
        e.Graphics.DrawRectangle(Pens.DimGray, swatchRect);

        // Draw the theme name to the right of the swatch.
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

        AcceptButton = ok; CancelButton = cancel;
        Controls.Add(ok); Controls.Add(cancel);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Cartesian grid overlay panel
// ─────────────────────────────────────────────────────────────────────────────
//
// Design: a transparent WinForms panel that sits as a child of RenderPanel and
// paints the Cartesian complex-plane grid entirely with GDI+ in OnPaint.
//
// Transparency trick
//   WinForms doesn't support true per-pixel transparency for child panels over
//   a DirectX surface.  Instead we set the panel's background to transparent
//   (via SetStyle + BackColor = Color.Transparent) and mark it with
//   WS_EX_TRANSPARENT so Windows skips painting its background and lets the
//   D3D11 swap-chain show through.  Mouse events are passed through to the
//   parent by forwarding WM_NCHITTEST to HTTRANSPARENT.
//
// Grid coordinate maths
//   The complex-plane visible width is  3.5 / zoom  (matching CurrentScale).
//   A "nice" grid spacing is chosen so that 4–10 grid lines appear across the
//   view at any zoom level.  We pick the largest power-of-10 multiplied by 1,
//   2, or 5 that satisfies that constraint.
//
// Colour contrast
//   The panel asks MainForm for the current colour-map swatch colour and
//   computes a contrasting colour by rotating the hue 180° and boosting/dimming
//   the luminance.  The grid is always drawn at 60% opacity so the fractal
//   detail underneath remains clearly visible.

internal sealed class GridOverlayPanel : Panel
{
    // Delegates into MainForm — evaluated each paint so the grid stays live.
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

        SetStyle(
            ControlStyles.SupportsTransparentBackColor |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint,
            true);

        BackColor = Color.Transparent;
    }

    // Pass mouse events through to the D3D11 surface underneath.
    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x0084;
        const int HTTRANSPARENT = -1;
        if (m.Msg == WM_NCHITTEST) { m.Result = (IntPtr)HTTRANSPARENT; return; }
        base.WndProc(ref m);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT
            return cp;
        }
    }

    // ── Paint ─────────────────────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        Debug.WriteLine($"GridOverlayPanel.OnPaint: size={_getPanelSize()} center={_getCenter()} zoom={_getZoom()} swatch={_getSwatchColor()}");
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        var size = _getPanelSize();
        int w = size.Width;
        int h = size.Height;
        if (w < 2 || h < 2) return;

        var (cx, cy) = _getCenter();
        double zoom = _getZoom();

        // Complex-plane scale: world units per pixel.
        double scale = 3.5 / (System.Math.Max(w, h) * zoom);

        // World-space extent of the view.
        double halfWW = w * scale * 0.5;
        double halfHW = h * scale * 0.5;
        double xMin = cx - halfWW;
        double xMax = cx + halfWW;
        double yMin = cy - halfHW;   // complex y points up
        double yMax = cy + halfHW;

        // ── Grid colour: complementary to the current swatch ─────────────────
        Color gridColor = ComputeContrastColor(_getSwatchColor());
        Debug.WriteLine($"Grid color: R: {gridColor.R}; G: {gridColor.G}; B: {gridColor.B}");
        var gridPen = new Pen(Color.FromArgb(180, gridColor), 1.0f);
        Debug.WriteLine($"Grid pen: R: {gridPen.Color.R}; G: {gridPen.Color.G}; B: {gridPen.Color.B}");

        var axisPen = new Pen(Color.FromArgb(220, gridColor), 1.5f);
        Debug.WriteLine($"Axis pen: R: {axisPen.Color.R}; G: {axisPen.Color .G}; B: {axisPen.Color.B}");

        var labelBrush = new SolidBrush(Color.FromArgb(200, gridColor));
        using var labelFont = new Font("Consolas", 7.5f, FontStyle.Regular, GraphicsUnit.Point);
        using var zeroFont = new Font("Consolas", 8.5f, FontStyle.Bold, GraphicsUnit.Point);

        // ── Choose a "nice" grid spacing ──────────────────────────────────────
        double viewSpan = xMax - xMin;  // wider axis drives the spacing decision
        double targetLines = 7.0;        // aim for ~7 grid lines across the view
        double rawStep = viewSpan / targetLines;
        double gridStep = NiceStep(rawStep);

        // ── Draw vertical lines (constant Re) ─────────────────────────────────
        double firstX = System.Math.Ceiling(xMin / gridStep) * gridStep;
        for (double wx = firstX; wx <= xMax + gridStep * 0.5; wx += gridStep)
        {
            float px = WorldToScreenX(wx, cx, scale, w);
            if (px < 0 || px > w) continue;

            bool isAxis = System.Math.Abs(wx) < gridStep * 0.01;
            g.DrawLine(isAxis ? axisPen : gridPen, px, 0, px, h);

            // Label along the bottom edge.
            string lbl = FormatCoord(wx);
            var sz = g.MeasureString(lbl, labelFont);
            float ly = h - sz.Height - 2;
            if (ly < 0) ly = 2;
            g.DrawString(lbl, labelFont, labelBrush, px - sz.Width * 0.5f, ly);
        }

        // ── Draw horizontal lines (constant Im) ───────────────────────────────
        // Complex y increases upward but screen y increases downward, so we
        // negate: a larger complex-Im value corresponds to a smaller screen-y.
        double firstY = System.Math.Ceiling(yMin / gridStep) * gridStep;
        for (double wy = firstY; wy <= yMax + gridStep * 0.5; wy += gridStep)
        {
            float py = WorldToScreenY(wy, cy, scale, h);
            if (py < 0 || py > h) continue;

            bool isAxis = System.Math.Abs(wy) < gridStep * 0.01;
            g.DrawLine(isAxis ? axisPen : gridPen, 0, py, w, py);

            // Label along the left edge (skip zero — the vertical axis already labels it).
            if (System.Math.Abs(wy) < gridStep * 0.01) continue;
            string lbl = FormatCoord(wy) + "i";
            var sz = g.MeasureString(lbl, labelFont);
            g.DrawString(lbl, labelFont, labelBrush, 3, py - sz.Height * 0.5f);
        }

        // ── Origin label ──────────────────────────────────────────────────────
        float ox = WorldToScreenX(0, cx, scale, w);
        float oy = WorldToScreenY(0, cy, scale, h);
        if (ox >= 0 && ox <= w && oy >= 0 && oy <= h)
        {
            var sz0 = g.MeasureString("0", zeroFont);
            g.DrawString("0", zeroFont, labelBrush, ox + 2, oy + 2);
        }

        gridPen.Dispose();
        axisPen.Dispose();
        labelBrush.Dispose();
    }

    // ── Coordinate helpers ────────────────────────────────────────────────────

    private static float WorldToScreenX(double wx, double cx, double scale, int w)
        => (float)((wx - cx) / scale + w * 0.5);

    private static float WorldToScreenY(double wy, double cy, double scale, int h)
        // Negate: larger complex-Im → smaller screen-y.
        => (float)(-(wy - cy) / scale + h * 0.5);

    /// <summary>
    /// Returns the smallest "nice" step ≥ rawStep.
    /// "Nice" means a value of the form  N × 10^k  where N ∈ {1, 2, 5}.
    /// </summary>
    private static double NiceStep(double rawStep)
    {
        if (rawStep <= 0) return 1.0;
        double mag = System.Math.Pow(10, System.Math.Floor(System.Math.Log10(rawStep)));
        double norm = rawStep / mag;
        double nice = norm <= 1.0 ? 1.0 : norm <= 2.0 ? 2.0 : norm <= 5.0 ? 5.0 : 10.0;
        return nice * mag;
    }

    /// <summary>
    /// Formats a coordinate value concisely — switches between fixed and
    /// scientific notation depending on magnitude.
    /// </summary>
    private static string FormatCoord(double v)
    {
        double abs = System.Math.Abs(v);
        if (abs == 0) return "0";
        if (abs >= 0.001 && abs < 10000)
        {
            // Fixed notation with just enough decimal places.
            int decimals = System.Math.Max(0, -(int)System.Math.Floor(System.Math.Log10(abs)) + 2);
            decimals = System.Math.Min(decimals, 6);
            return v.ToString($"F{decimals}",
                System.Globalization.CultureInfo.InvariantCulture);
        }
        return v.ToString("G4", System.Globalization.CultureInfo.InvariantCulture);
    }

    // ── Colour contrast ───────────────────────────────────────────────────────

    /// <summary>
    /// Produces a grid colour that contrasts with the given swatch colour by:
    ///   1. Converting the swatch to HSL.
    ///   2. Rotating hue by 180°.
    ///   3. Inverting luminance (dark themes get light grid, light themes dark).
    ///   4. Boosting saturation so the grid reads clearly against the fractal.
    /// </summary>
    private static Color ComputeContrastColor(Color swatch)
    {
        // Convert RGB → HSL.
        float r = swatch.R / 255f;
        float g = swatch.G / 255f;
        float b = swatch.B / 255f;

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

        float s2 = (delta < 0.001f) ? 0f : delta / (1f - System.Math.Abs(2f * l - 1f));

        // Contrast operations:
        //   • Rotate hue 180° for complementary colour.
        //   • Invert and boost luminance (dark → light, light → dark, pushed toward extremes).
        //   • Boost saturation.
        float hc = (h2 + 0.5f) % 1f;
        float lc = l < 0.5f ? System.Math.Clamp(1f - l * 0.6f, 0.65f, 1.0f)
                             : System.Math.Clamp(1f - l * 1.4f, 0.0f, 0.35f);
        float sc = System.Math.Clamp(s2 * 0.5f + 0.5f, 0.5f, 1.0f);

        // Convert HSL → RGB.
        float c = (1f - System.Math.Abs(2f * lc - 1f)) * sc;
        float xv = c * (1f - System.Math.Abs((hc * 6f) % 2f - 1f));
        float m = lc - c * 0.5f;

        float rr, gg, bb;
        int sector = (int)(hc * 6f);
        switch (sector)
        {
            case 0: rr = c; gg = xv; bb = 0; break;
            case 1: rr = xv; gg = c; bb = 0; break;
            case 2: rr = 0; gg = c; bb = xv; break;
            case 3: rr = 0; gg = xv; bb = c; break;
            case 4: rr = xv; gg = 0; bb = c; break;
            default: rr = c; gg = 0; bb = xv; break;
        }

        return Color.FromArgb(
            (int)System.Math.Clamp((rr + m) * 255f, 0, 255),
            (int)System.Math.Clamp((gg + m) * 255f, 0, 255),
            (int)System.Math.Clamp((bb + m) * 255f, 0, 255));
    }
}