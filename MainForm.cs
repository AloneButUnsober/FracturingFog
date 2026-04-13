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
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using FracturingFog.Models;
using FracturingFog.Interefaces;

namespace FracturingFog;

public sealed class MainForm : Form
{
    // ── UI: top toolbar ───────────────────────────────────────────────────────
    private readonly Panel       _toolbar;
    private readonly Button      _resetButton;
    private readonly Button      _spanButton;
    private readonly Button      _screenshotButton;
    private readonly ComboBox _qualityCombo;
    private readonly ComboBox    _colorThemeCombo;
    private readonly Label       _statusLabel;

    // ── UI: coordinate / region bar ───────────────────────────────────────────
    private readonly Panel       _coordPanel;
    private readonly TextBox     _txCX;
    private readonly TextBox     _txCY;
    private readonly TextBox     _txZoom;
    private readonly TextBox     _txIter;
    private readonly Button      _goButton;
    private readonly ComboBox    _regionCombo;
    private readonly Button      _saveViewButton;
    private readonly Button      _delRegionButton;

    // ── Render panel ──────────────────────────────────────────────────────────
    private readonly RenderPanel _renderPanel;
    // ── Core objects ──────────────────────────────────────────────────────────

    private DirectXRenderer?      _renderer;
    private MandelbrotCalculator? _calculator;

    // ── View state ────────────────────────────────────────────────────────────

    private const double DefaultCenterX = -0.5;
    private const double DefaultCenterY =  0.0;
    private const double DefaultZoom    =  1.0;

    private double _centerX = DefaultCenterX;
    private double _centerY = DefaultCenterY;
    private double _zoom    = DefaultZoom;

    // Active quality preset — Standard by default.
    private QualityPreset _quality = QualityPreset.Standard;

    // Guard: prevents coord boxes being repopulated while user types.
    private bool _suppressCoordUpdate;
    // ── Pan state ─────────────────────────────────────────────────────────────

    private bool   _panning;
    private Point  _panStartScreen;   // screen coords where left-button was pressed
    private double _panStartCX;       // complex-plane centre at that moment
    private double _panStartCY;

    // ── Multi-monitor span state ─────────────────────────────────────────────
    //
    // When _spanning is true the form is borderless and positioned exactly over
    // SystemInformation.VirtualScreen (the bounding rectangle of all monitors).
    // _preSpanBounds and _preSpanBorderStyle store what to restore to.

    private bool            _spanning;
    private Rectangle       _preSpanBounds;
    private FormBorderStyle _preSpanBorderStyle;
    private FormWindowState _preSpanWindowState;
    // ── Async calculation ─────────────────────────────────────────────────────

    private CancellationTokenSource? _calcCts;
    private readonly object          _calcLock = new();
    private bool                     _disposed;

    // ─────────────────────────────────────────────────────────────────────────
    // Constructor
    // ─────────────────────────────────────────────────────────────────────────

    public MainForm()
    {
        Text        = "Fracturing Fog  —  Mandelbrot Explorer  (DirectX 11 · Vortice 3.8.3)";
        ClientSize  = new Size(1440, 870);
        MinimumSize = new Size(640, 420);
        BackColor   = Color.Black;

        KeyPreview  = true;

        // ── Helpers ───────────────────────────────────────────────────────────

        Button MakeBtn(string text, int w = 108) => new Button
            {
                Text      = text,
                Width     = w,
                Height    = 26,
                Top       = 6,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(55, 55, 55),
                ForeColor = Color.White,
                Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
                Cursor    = Cursors.Hand
        }.Also(b => b.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90));

        Label MakeLbl(string text, int left, Panel p) => new Label
            {
                Text      = text,
                Left      = left,
            Top       = 9,
                AutoSize  = true,
            ForeColor = Color.FromArgb(155, 155, 155),
                Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                BackColor = Color.Transparent
        }.AlsoAdd(p);

        TextBox MakeTx(int left, int w, Panel p, string tip) => new TextBox
            {
                Left      = left,
                Top       = 7,
                Width     = w,
                Height    = 22,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.FromArgb(220, 220, 220),
                Font      = new Font("Consolas", 9f),
                BorderStyle = BorderStyle.FixedSingle
        }.AlsoAdd(p, tip);

        // ── Top toolbar ───────────────────────────────────────────────────────

        _toolbar = new Panel
        {
            Height    = 38,
            Dock      = DockStyle.Top,
            BackColor = Color.FromArgb(28, 28, 28),
        };

        int bx = 6;

        _resetButton = MakeBtn("Reset", 78);
        _resetButton.Left = bx; bx += 86;
        _resetButton.Click += OnResetClick;
        _toolbar.Controls.Add(_resetButton);

        _spanButton = MakeBtn("Span Monitors");
        _spanButton.Left = bx; bx += 116;
        _spanButton.Click += OnSpanMonitorsClick;
        _toolbar.Controls.Add(_spanButton);

        _screenshotButton = MakeBtn("Screenshot");
        _screenshotButton.Left = bx; bx += 116;
        _screenshotButton.Click += OnScreenshotClick;
        _toolbar.Controls.Add(_screenshotButton);

        // Thin separator.
        _toolbar.Controls.Add(new Label
        {
            Left = bx, Top = 4, Width = 1, Height = 30,
            BackColor = Color.FromArgb(65, 65, 65)
        }); bx += 10;

        // Quality label + combo.
        var qlbl = new Label
        {
            Text      = "Quality:",
            Left      = bx, Top = 10, AutoSize = true,
            ForeColor = Color.FromArgb(155, 155, 155),
            Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            BackColor = Color.Transparent
        };
        _toolbar.Controls.Add(qlbl);
        bx += qlbl.PreferredWidth + 4;

        _qualityCombo = new ComboBox
        {
            Left          = bx, Top = 7, Width = 112, Height = 26,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor     = Color.FromArgb(45, 45, 45),
            ForeColor   = Color.White,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            FlatStyle     = FlatStyle.Flat,
            Cursor        = Cursors.Hand
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
        bx += 120;

        // Theme separator.
        _toolbar.Controls.Add(new Label
        {
            Left = bx, Top = 4, Width = 1, Height = 30,
            BackColor = Color.FromArgb(65, 65, 65)
        }); bx += 10;

        // Theme label + combo.
        var tlbl = new Label
        {
            Text      = "Theme:",
            Left      = bx, Top = 10, AutoSize = true,
            ForeColor = Color.FromArgb(155, 155, 155),
            Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            BackColor = Color.Transparent
        };
        _toolbar.Controls.Add(tlbl);
        bx += tlbl.PreferredWidth + 4;

        _colorThemeCombo = new ColorComboBox
        {
            Left = bx, Top = 7, Width = 162, Height = 26,
            BackColor = Color.FromArgb(55, 55, 55), ForeColor = Color.White,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold), Cursor = Cursors.Hand
        };
        BuildColorThemesSelection();
        _colorThemeCombo.SelectedIndex = 0;
        _colorThemeCombo.SelectedIndexChanged += OnColorThemeChanged;

        _toolbar.Controls.Add(_colorThemeCombo);
        bx += 170;

        // Status label — fills the remainder.
        _statusLabel = new Label
        {
            Left = bx + 8, Top = 0, Width = 700, Height = 38, AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(140, 140, 140),
            BackColor = Color.Transparent,
            Font      = new Font("Consolas", 8f),
            Text      = "Initialising…"
        };

        _toolbar.Controls.Add(_statusLabel);

        // ── Coordinate / region panel ─────────────────────────────────────────

        _coordPanel = new Panel
        {
            Height    = 34,
            Dock      = DockStyle.Top,
            BackColor = Color.FromArgb(22, 22, 22),
        };

        int cx = 8;
        MakeLbl("CX:", cx, _coordPanel);       cx += 28;
        _txCX   = MakeTx(cx, 182, _coordPanel, "Real part of the view centre");
        cx += 190;
        MakeLbl("CY:", cx, _coordPanel);       cx += 28;
        _txCY   = MakeTx(cx, 182, _coordPanel, "Imaginary part of the view centre");
        cx += 190;
        MakeLbl("Zoom:", cx, _coordPanel);     cx += 44;
        _txZoom = MakeTx(cx, 112, _coordPanel, "Zoom factor (1 = full view; larger = zoomed in)");
        cx += 120;
        MakeLbl("Iter:", cx, _coordPanel);     cx += 38;
        _txIter = MakeTx(cx, 64,  _coordPanel, "Maximum iteration count (auto-computed by quality+zoom)");
        cx += 72;

        _goButton = new Button
        {
            Text = "Go", Left = cx, Top = 4, Width = 48, Height = 26,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(40, 80, 40),
            ForeColor = Color.White,
            Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
            Cursor    = Cursors.Hand
        };
        _goButton.FlatAppearance.BorderColor = Color.FromArgb(70, 120, 70);
        _goButton.Click += OnGoClick;
        _coordPanel.Controls.Add(_goButton);
        cx += 56;

        // Separator.
        _coordPanel.Controls.Add(new Label
        {
            Left = cx, Top = 2, Width = 1, Height = 30,
            BackColor = Color.FromArgb(60, 60, 60)
        }); cx += 10;

        var rlbl = new Label
        {
            Text      = "Region:",
            Left      = cx, Top = 9, AutoSize = true,
            ForeColor = Color.FromArgb(155, 155, 155),
            Font      = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            BackColor = Color.Transparent
        };
        _coordPanel.Controls.Add(rlbl); cx += rlbl.PreferredWidth + 4;

        _regionCombo = new ComboBox
        {
            Left = cx, Top = 5, Width = 192, Height = 24,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor     = Color.FromArgb(45, 45, 45),
            ForeColor     = Color.White,
            Font          = new Font("Segoe UI", 9f),
            FlatStyle     = FlatStyle.Flat
        };
        _coordPanel.Controls.Add(_regionCombo); cx += 200;

        _saveViewButton = new Button
        {
            Text = "Save View", Left = cx, Top = 4, Width = 88, Height = 26,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(55, 55, 55),
            ForeColor = Color.White,
            Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
            Cursor    = Cursors.Hand
        };
        _saveViewButton.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90);
        _saveViewButton.Click += OnSaveViewClick;
        _coordPanel.Controls.Add(_saveViewButton); cx += 96;

        _delRegionButton = new Button
        {
            Text = "Del Region", Left = cx, Top = 4, Width = 90, Height = 26,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(55, 55, 55),
            ForeColor = Color.White,
            Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
            Cursor    = Cursors.Hand,
            Enabled   = false
        };
        _delRegionButton.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90);
        _delRegionButton.Click  += OnDelRegionClick;
        _coordPanel.Controls.Add(_delRegionButton);

        // ── Render panel ──────────────────────────────────────────────────────
        _renderPanel = new RenderPanel { Dock = DockStyle.Fill, Cursor = Cursors.Cross };
        _renderPanel.MouseWheel += OnMouseWheel;
        _renderPanel.MouseDown  += OnMouseDown;
        _renderPanel.MouseMove  += OnMouseMove;
        _renderPanel.MouseUp    += OnMouseUp;

        // Docking order: Fill first, then Top-docked panels in reverse order.
        Controls.Add(_renderPanel);
        Controls.Add(_coordPanel);
        Controls.Add(_toolbar);

        // ── Events ───────────────────────────────────────────────────────────
        Load        += OnLoad;
        Resize      += OnFormResize;
        KeyDown     += OnKeyDown;
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
            _renderer   = new DirectXRenderer(_renderPanel.Handle, w, h);
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
        foreach (var name in Models.ColorPalette.GetPaletteNames())
            _colorThemeCombo.Items.Add(name);
    }

    private void OnColorThemeChanged(object? sender, EventArgs e)
    {
        string name   = _colorThemeCombo.SelectedItem?.ToString() ?? "";
        var    map    = Models.ColorPalette.GetPaletteByName(name);
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
        _zoom    = DefaultZoom;
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
        _zoom    = System.Math.Clamp(zoom, _quality.ZoomMin, _quality.ZoomMax);

        if (_calculator != null && iter > 0)
            _calculator.MaxIterations = iter;

        ApplyViewState();
        TriggerCalculation();
    }

    private bool TryParseCoords(out double cx, out double cy,
                                 out double zoom, out int iter)
    {
        cx   = _centerX;
        cy   = _centerY;
        zoom = _zoom;
        iter = _calculator?.MaxIterations ?? 512;

        var ic = System.Globalization.CultureInfo.InvariantCulture;
        var ns = System.Globalization.NumberStyles.Float;

        return double.TryParse(_txCX.Text.Trim(),   ns, ic, out cx)
            && double.TryParse(_txCY.Text.Trim(),   ns, ic, out cy)
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
        _zoom    = System.Math.Clamp(region.Zoom, _quality.ZoomMin, _quality.ZoomMax);

        if (_calculator != null && region.Iterations > 0)
            _calculator.MaxIterations = region.Iterations;

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
            Name        = name,
            CenterX     = _centerX,
            CenterY     = _centerY,
            Zoom        = _zoom,
            Iterations  = _calculator?.MaxIterations ?? 512,
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

        _preSpanWindowState  = WindowState;
        _preSpanBorderStyle  = FormBorderStyle;
        if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
        _preSpanBounds = Bounds;

        _spanning = true;
        _spanButton.Text = "Restore";

        FormBorderStyle = FormBorderStyle.None;
        WindowState         = FormWindowState.Normal;
        Bounds = SystemInformation.VirtualScreen;

        TopMost = true;
        Activate();
    }

    private void ExitSpanMode()
    {
        if (!_spanning) return;

        _spanning = false;
        TopMost   = false;
        _spanButton.Text = "Span Monitors";

        FormBorderStyle = _preSpanBorderStyle;
        WindowState     = FormWindowState.Normal;
        Bounds          = _preSpanBounds;

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

    private void OnScreenshotClick(object? sender, EventArgs e)
    {
        if (_calculator == null)
        {
            MessageBox.Show("No fractal data to save yet.", "Screenshot",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string  colorName = _calculator.ColorMap?.GetType().GetProperty("Name")?.GetValue(null)?.ToString() ?? "Theme";
        string regionName = "";
        if (_regionCombo.SelectedItem != null &&
            !string.IsNullOrEmpty(_regionCombo.SelectedItem?.ToString()) &&
            _regionCombo.SelectedItem?.ToString() != "— select region —") 
            regionName = _regionCombo.SelectedItem?.ToString()?.Replace(" ", "") + "_" ?? "";

        using var dlg = new SaveFileDialog
        {
            Title = "Save Mandelbrot Screenshot",
            Filter = "PNG Image (*.png)|*.png|TIFF Image (*.tiff;*.tif)|*.tiff;*.tif|BMP Image (*.bmp)|*.bmp",
            FilterIndex = 1,
            DefaultExt = "png",
            FileName = $"Mandelbrot_{colorName}_{regionName}" +
            $"x{_txCX.Text.Replace(".", "")}_" +
            $"y{_txCY.Text.Replace(".", "")}_" +
            $"z{_txZoom.Text.Replace(".", "")}_" +
            $"i{_txIter.Text.Replace(".", "")}_" +
            $"{_calculator.Width}x{_calculator.Height}"
        };

        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        string path = dlg.FileName;

        string ext    = Path.GetExtension(path).ToLowerInvariant();
        var    format = ext switch { ".bmp" => ImageFormat.Bmp, ".tif" or ".tiff" => ImageFormat.Tiff, _ => ImageFormat.Png };

        int    w       = _calculator.Width;
        int    h       = _calculator.Height;
        uint[] pixels  = _calculator.ColorBuffer;

        try
        {
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);

            var       bmpData = bmp.LockBits(new Rectangle(0, 0, w, h),
                                             ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                unsafe
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

            SetStatus($"Saved  {Path.GetFileName(path)}  ({w}×{h},  {new FileInfo(path).Length / 1024:N0} KB)");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Save failed:\n{ex.Message}", "Screenshot Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
    // ─────────────────────────────────────────────────────────────────────────
    // Mouse: zoom
    // ─────────────────────────────────────────────────────────────────────────

    private void OnMouseWheel(object? sender, MouseEventArgs e)
    {
        if (_calculator == null) return;

        // Use the quality preset's wheel factor for the active tier.
        double wf     = _quality.WheelZoomFactor;
        double factor = e.Delta > 0 ? wf : 1.0 / wf;

        double scale    = CurrentScale();
        double ox      = e.X - _renderPanel.ClientSize.Width  * 0.5;
        double oy      = e.Y - _renderPanel.ClientSize.Height * 0.5;
        double compX   = _centerX + ox * scale;
        double compY   = _centerY + oy * scale;

        // Clamp to the quality tier's zoom range.
        _zoom = System.Math.Clamp(_zoom * factor, _quality.ZoomMin, _quality.ZoomMax);

        double ns      = CurrentScale();
        _centerX       = compX - ox * ns;
        _centerY       = compY - oy * ns;

        ApplyViewState();
        TriggerCalculation();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Mouse: pan
    // ─────────────────────────────────────────────────────────────────────────

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _panning         = true;
        _panStartScreen  = e.Location;
        _panStartCX      = _centerX;
        _panStartCY      = _centerY;
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
        _calculator.CenterX       = _centerX;
        _calculator.CenterY       = _centerY;
        _calculator.Zoom          = _zoom;
        _calculator.Quality       = _quality;
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
            if (ActiveControl != _txCX)   _txCX.Text   = _centerX.ToString("G15", ic);
            if (ActiveControl != _txCY)   _txCY.Text   = _centerY.ToString("G15", ic);
            if (ActiveControl != _txZoom) _txZoom.Text = _zoom.ToString("G8",     ic);
            if (ActiveControl != _txIter && _calculator != null)
                _txIter.Text = _calculator.MaxIterations.ToString();
        }
        finally { _suppressCoordUpdate = false; }
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
            cts      = _calcCts;
        }

        var token    = cts.Token;
        var calc     = _calculator;
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
                        // Precision mode tag: [SP] or [DD].
                        string precTag = calc.IsHighPrecisionActive ? "[DD]" : "[SP]";
                    SetStatus(
                            $"cx={calc.CenterX:G12}  cy={calc.CenterY:G12}  " +
                            $"zoom={calc.Zoom:G6}  iter={calc.MaxIterations}  " +
                            $"{precTag}  [{ms} ms  {calc.Width}×{calc.Height}]");
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
    protected override void OnPaint(PaintEventArgs e)           { }

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
        ItemHeight    = 20;
    }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        e.DrawBackground();

        string text      = Items[e.Index]?.ToString() ?? "";
        IColorMap map  = Models.ColorPalette.GetPaletteByName(text);

        // ── Swatch: sample the palette at 30 % of MaxIterations ──────────────
        // Uses SwatchSample (default interface method) instead of Map(0,0,0)
        // which always returned black in the previous version.
        map.MaxIterations = 500;
        int argb   = map.SwatchSample;
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
        Text            = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize      = new Size(360, 100);
        StartPosition   = FormStartPosition.CenterParent;
        MaximizeBox     = false;
        MinimizeBox     = false;
        BackColor       = Color.FromArgb(35, 35, 35);

        Controls.Add(new Label { Text = prompt, Left = 12, Top = 14, AutoSize = true,
            ForeColor = Color.LightGray, Font = new Font("Segoe UI", 9f) });

        _tx = new TextBox { Left = 12, Top = 36, Width = 336,
            BackColor = Color.FromArgb(50, 50, 50), ForeColor = Color.White,
            Font = new Font("Consolas", 10f), BorderStyle = BorderStyle.FixedSingle };
        Controls.Add(_tx);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK,
            Left = 196, Top = 66, Width = 72, Height = 26,
            BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel,
            Left = 276, Top = 66, Width = 72, Height = 26,
            BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };

        AcceptButton = ok; CancelButton = cancel;
        Controls.Add(ok); Controls.Add(cancel);
    }
}