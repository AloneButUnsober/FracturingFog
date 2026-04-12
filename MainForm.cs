// MainForm.cs
// WinForms host for the Mandelbrot Explorer.
//
// Responsibilities
//   • Owns the DirectXRenderer and MandelbrotCalculator lifecycles.
//   • Mouse-wheel zoom centred on the cursor position.
//   • Left-button drag-to-pan with live recalculation (cancels in-flight work).
//   • Reset button restores the initial view.
//   • Handles maximise / minimise / arbitrary resize correctly.
//   • Application.Idle render loop keeps the GPU busy without a dedicated thread.

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using FracturingFog.Models;
using FracturingFog.Interefaces;

namespace FracturingFog;

public sealed class MainForm : Form
{
    // ── UI controls ───────────────────────────────────────────────────────────

    private readonly RenderPanel _renderPanel;
    private readonly Button      _resetButton;
    private readonly Button      _spanButton;
    private readonly Button      _screenshotButton;
    private readonly ComboBox    _colorThemeCombo;
    private readonly Panel       _toolbar;
    private readonly Label       _statusLabel;

    // ── Core objects ──────────────────────────────────────────────────────────

    private DirectXRenderer?      _renderer;
    private MandelbrotCalculator? _calculator;

    // ── Default view ─────────────────────────────────────────────────────────

    private const double DefaultCenterX = -0.5;
    private const double DefaultCenterY =  0.0;
    private const double DefaultZoom    =  1.0;

    private double _centerX = DefaultCenterX;
    private double _centerY = DefaultCenterY;
    private double _zoom    = DefaultZoom;

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

    // ── Constructor ───────────────────────────────────────────────────────────

    public MainForm()
    {
        // ── Form ──
        Text        = "Mandelbrot Explorer  —  DirectX 11  (Vortice 3.8.3)";
        ClientSize  = new Size(1280, 800);
        MinimumSize = new Size(400, 300);
        BackColor   = Color.Black;

        // ── Toolbar (docked to top) ──
        _toolbar = new Panel
        {
            Height    = 38,
            Dock      = DockStyle.Top,
            BackColor = Color.FromArgb(28, 28, 28),
            Padding   = new Padding(6, 0, 6, 0)
        };

        // Helper: build a uniform toolbar button.
        Button MakeToolbarButton(string text, int left)
        {
            var btn = new Button
            {
                Text      = text,
                Width     = 120,
                Height    = 26,
                Left      = left,
                Top       = 6,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(55, 55, 55),
                ForeColor = Color.White,
                Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
                Cursor    = Cursors.Hand
            };
            btn.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 90);
            return btn;
        }

        _resetButton = MakeToolbarButton("Reset", left: 6);
        _resetButton.Width = 80;
        _resetButton.Click += OnResetClick;

        _spanButton = MakeToolbarButton("Span Monitors", left: 94);
        _spanButton.Click += OnSpanMonitorsClick;

        _screenshotButton = MakeToolbarButton("Screenshot", left: 222);
        _screenshotButton.Click += OnScreenshotClick;

        _colorThemeCombo = new ColorComboBox
        {
            Text        = "Color Theme",
            Width       = 100,
            Height      = 26,
            Left        = 350,
            Top         = 7,
            FlatStyle = FlatStyle.Flat,
            BackColor   = Color.FromArgb(55, 55, 55),
            ForeColor   = Color.White,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        BuildColorThemesSelection();
        _colorThemeCombo.SelectedIndex = 0;
        _colorThemeCombo.SelectedIndexChanged += (s, e) =>
        {
            string selectedName = _colorThemeCombo.SelectedItem?.ToString() ?? "";
            IColorMap selectedMap = Models.ColorPalette.GetPaletteByName(selectedName);
            if (_calculator != null)
            {
                _calculator.ColorMap = selectedMap;
                TriggerCalculation();
            }
        };


        _statusLabel = new Label
        {
            AutoSize  = false,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(170, 170, 170),
            BackColor = Color.Transparent,
            Font      = new Font("Consolas", 8.5f),
            Left      = 462,
            Top       = 0,
            Width     = 1000,
            Height    = 38,
            Text      = "Initialising…"
        };

        _toolbar.Controls.Add(_resetButton);
        _toolbar.Controls.Add(_spanButton);
        _toolbar.Controls.Add(_screenshotButton);
        _toolbar.Controls.Add(_colorThemeCombo);   
        _toolbar.Controls.Add(_statusLabel);

        // ── Render panel (DirectX surface, fills remaining space) ──
        _renderPanel = new RenderPanel { Dock = DockStyle.Fill, Cursor = Cursors.Cross };
        _renderPanel.MouseWheel += OnMouseWheel;
        _renderPanel.MouseDown  += OnMouseDown;
        _renderPanel.MouseMove  += OnMouseMove;
        _renderPanel.MouseUp    += OnMouseUp;

        // Add Fill panel first, then Top-docked toolbar (Controls.Add order matters for docking).
        Controls.Add(_renderPanel);
        Controls.Add(_toolbar);

        Load        += OnLoad;
        Resize      += OnFormResize;
        KeyDown     += OnKeyDown;
        FormClosing += OnFormClosing;

        // Escape to exit span mode.
        KeyPreview = true;

        Application.Idle += OnApplicationIdle;
    }

    // ── Initialisation ────────────────────────────────────────────────────────

    private void OnLoad(object? sender, EventArgs e)
    {
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
                "Initialisation Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Application.Exit();
        }
    }

    // ── Reset ─────────────────────────────────────────────────────────────────

    private void OnResetClick(object? sender, EventArgs e)
    {
        _centerX = DefaultCenterX;
        _centerY = DefaultCenterY;
        _zoom    = DefaultZoom;
        ApplyViewState();
        TriggerCalculation();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FEATURE 1 — MULTI-MONITOR SPAN
    // ─────────────────────────────────────────────────────────────────────────
    //
    // SystemInformation.VirtualScreen is the bounding rectangle of all connected
    // monitors in the Windows virtual desktop coordinate system.  On a 3-monitor
    // rig with two 1920×1080 screens side-by-side and one 1440×900 above, it
    // might be Rectangle { X=-1440, Y=-900, Width=5280, Height=1980 } etc.
    //
    // Strategy
    //   1. Store the current form size, position, border style, and window state.
    //   2. Set FormBorderStyle.None  → removes the title bar so the window can
    //      sit flush against every monitor edge without OS chrome.
    //   3. Set WindowState = Normal  → required before setting Bounds, otherwise
    //      the OS ignores the assignment.
    //   4. Set Bounds = VirtualScreen → positions + sizes in one atomic call.
    //   5. The RenderPanel.Resize event fires, which calls Renderer.Resize() and
    //      Calculator.Resize(), and triggers a new calculation at the full
    //      multi-monitor pixel count automatically.
    //
    // Escape key or clicking "Restore" reverses all four steps.

    private void OnSpanMonitorsClick(object? sender, EventArgs e)
    {
        if (_spanning)
            ExitSpanMode();
        else
            EnterSpanMode();
    }

    private void EnterSpanMode()
    {
        if (_spanning) return;

        // Save restore state.
        _preSpanWindowState  = WindowState;
        _preSpanBorderStyle  = FormBorderStyle;
        // Bounds is meaningful only when WindowState == Normal; un-maximise first.
        if (WindowState != FormWindowState.Normal)
            WindowState = FormWindowState.Normal;
        _preSpanBounds = Bounds;

        _spanning = true;
        _spanButton.Text = "Restore";

        // Order matters: set border before Bounds so the OS applies the correct
        // non-client area (zero) when computing the client rectangle.
        FormBorderStyle = FormBorderStyle.None;
        WindowState     = FormWindowState.Normal;  // insurance against maximised state

        // VirtualScreen covers all monitors as one rectangle.
        Bounds = SystemInformation.VirtualScreen;

        // Bring to front so the window sits above any taskbars that aren't
        // set to "always on top".
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

        // If the user had maximised before spanning, restore that state.
        if (_preSpanWindowState == FormWindowState.Maximized)
            WindowState = FormWindowState.Maximized;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && _spanning)
        {
            ExitSpanMode();
            e.Handled = true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FEATURE 2 — LOSSLESS SCREENSHOT
    // ─────────────────────────────────────────────────────────────────────────
    //
    // The MandelbrotCalculator already holds the full-resolution BGRA pixel data
    // in ColorBuffer (uint[], Format B8G8R8A8_UNorm, same layout as GDI's
    // Format32bppArgb on little-endian x64).  We create a GDI Bitmap by locking
    // its bits and performing a single MemoryCopy — no pixel-by-pixel loop.
    //
    // Supported formats
    //   PNG  — lossless, indexed chunks, best for general use.
    //   BMP  — uncompressed lossless, largest files, maximum compatibility.
    //   TIFF — lossless with optional LZW, preferred in print/archival workflows.
    //
    // The default is PNG.  The SaveFileDialog filter lets the user pick any of
    // the three; the chosen extension drives the encoder.

    private void OnScreenshotClick(object? sender, EventArgs e)
    {
        if (_calculator == null)
        {
            MessageBox.Show("No fractal data to save yet.", "Screenshot",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // ── Save file dialog ──────────────────────────────────────────────────
        using var dlg = new SaveFileDialog
        {
            Title       = "Save Mandelbrot Screenshot",
            Filter      = "PNG Image (*.png)|*.png" +
                          "|TIFF Image (*.tiff;*.tif)|*.tiff;*.tif" +
                          "|BMP Image (*.bmp)|*.bmp",
            FilterIndex = 1,   // default to PNG
            DefaultExt  = "png",
            FileName    = BuildDefaultFilename()
        };

        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        string path = dlg.FileName;

        // Determine the encoder from the chosen filter / extension.
        ImageFormat format = GetImageFormat(path);

        // ── Build Bitmap from ColorBuffer ─────────────────────────────────────
        //
        // ColorBuffer layout  (PackBgra):
        //   uint bits = (A<<24)|(R<<16)|(G<<8)|B
        //   In memory (little-endian): byte0=B  byte1=G  byte2=R  byte3=A
        //
        // GDI Format32bppArgb in memory (little-endian):
        //   byte0=B  byte1=G  byte2=R  byte3=A
        //
        // They match — a straight memcpy is correct and safe.
        int    w       = _calculator.Width;
        int    h       = _calculator.Height;
        uint[] pixels  = _calculator.ColorBuffer;

        try
        {
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);

            // Lock the full bitmap for writing.
            var rect    = new Rectangle(0, 0, w, h);
            var bmpData = bmp.LockBits(rect, ImageLockMode.WriteOnly,
                                       PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    fixed (uint* src = pixels)
                    {
                        // GDI stride (bmpData.Stride) == w * 4 for 32 bpp
                        // because our width is not padded to an unusual alignment.
                        // Verify just in case to avoid memory corruption.
                        if (bmpData.Stride == w * 4)
                        {
                            // Single copy — fastest path.
                            Buffer.MemoryCopy(
                                src,
                                (void*)bmpData.Scan0,
                                (long)w * h * 4,
                                (long)w * h * 4);
                        }
                        else
                        {
                            // Row-by-row when GDI padding differs (rare for 32 bpp).
                            byte* dst = (byte*)bmpData.Scan0;
                            byte* s   = (byte*)src;
                            for (int row = 0; row < h; row++)
                            {
                                Buffer.MemoryCopy(
                                    s   + (long)row * w * 4,
                                    dst + (long)row * bmpData.Stride,
                                    (long)w * 4,
                                    (long)w * 4);
                            }
                        }
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(bmpData);
            }

            // ── Save ─────────────────────────────────────────────────────────
            //
            // For TIFF we configure the LZW encoder explicitly so the output is
            // compressed losslessly.  PNG uses its own internal deflate encoder.
            // BMP is always uncompressed (no encoder params needed).

            if (format == ImageFormat.Tiff)
            {
                SaveTiff(bmp, path);
            }
            else
            {
                bmp.Save(path, format);
            }

            SetStatus($"Screenshot saved  →  {Path.GetFileName(path)}" +
                      $"  ({w}×{h} px,  {new FileInfo(path).Length / 1024:N0} KB)");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to save screenshot:\n\n{ex.Message}",
                "Screenshot Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    // ── Screenshot helpers ────────────────────────────────────────────────────

    private static string BuildDefaultFilename()
    {
        return $"Mandelbrot_{DateTime.Now:yyyyMMdd_HHmmss}";
    }

    private static ImageFormat GetImageFormat(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".bmp"            => ImageFormat.Bmp,
            ".tif" or ".tiff" => ImageFormat.Tiff,
            _                 => ImageFormat.Png   // default / .png
        };
    }

    /// <summary>
    /// Saves a <see cref="Bitmap"/> as a lossless LZW-compressed TIFF.
    /// GDI+'s default TIFF encoder uses no compression; we force LZW via
    /// the <see cref="EncoderParameters"/> API.
    /// </summary>
    private static void SaveTiff(Bitmap bmp, string path)
    {
        // Find the TIFF codec.
        ImageCodecInfo? tiffCodec = null;
        foreach (var codec in ImageCodecInfo.GetImageEncoders())
        {
            if (codec.MimeType == "image/tiff") { tiffCodec = codec; break; }
        }

        if (tiffCodec == null)
        {
            // Fallback: save without compression (still lossless).
            bmp.Save(path, ImageFormat.Tiff);
            return;
        }

        using var encParams = new EncoderParameters(1);
        // LZW = 5 per the GDI+ Encoder.Compression enum values.
        encParams.Param[0] = new EncoderParameter(
            Encoder.Compression,
            (long)EncoderValue.CompressionLZW);

        bmp.Save(path, tiffCodec, encParams);
    }
    // ── Mouse: zoom toward cursor ─────────────────────────────────────────────

    private void OnMouseWheel(object? sender, MouseEventArgs e)
    {
        if (_calculator == null) return;

        // 20 % zoom change per mouse-wheel detent.
        double factor = e.Delta > 0 ? 1.20 : 1.0 / 1.20;

        // Map the cursor's screen position to complex-plane coordinates
        // before applying the zoom so the point under the cursor stays fixed.
        double scale    = CurrentScale();
        double offsetX  = e.X - _renderPanel.ClientSize.Width  * 0.5;
        double offsetY  = e.Y - _renderPanel.ClientSize.Height * 0.5;
        double complexX = _centerX + offsetX * scale;
        double complexY = _centerY + offsetY * scale;

        _zoom = Math.Clamp(_zoom * factor, 1e-1, 1e14);

        double newScale = CurrentScale();
        _centerX = complexX - offsetX * newScale;
        _centerY = complexY - offsetY * newScale;

        ApplyViewState();
        TriggerCalculation();
    }

    // ── Mouse: pan ────────────────────────────────────────────────────────────

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

    // ── Window resize ─────────────────────────────────────────────────────────

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

    // ── Idle render loop ──────────────────────────────────────────────────────
    //
    // Re-presents whatever texture is on the GPU at near-display rate.
    // Texture uploads only happen when a calculation finishes (see TriggerCalculation).

    private void OnApplicationIdle(object? sender, EventArgs e)
    {
        if (!_disposed && _renderer != null)
            _renderer.Render();
    }

    // Populate Color Themes
    private void BuildColorThemesSelection()
    {
        foreach (IColorMap colorMap in Models.ColorPalette.Palettes)
        {
            var name = colorMap.GetType().GetProperty("Name")?.GetValue(null)?.ToString();
            if (!string.IsNullOrEmpty(name))
                _colorThemeCombo.Items.Add(name);
        }
    }

    // ── View state helpers ────────────────────────────────────────────────────

    private double CurrentScale()
    {
        if (_calculator == null) return 3.5;
        return 3.5 / (Math.Max(_calculator.Width, _calculator.Height) * _zoom);
    }

    private void ApplyViewState()
    {
        if (_calculator == null) return;
        _calculator.CenterX       = _centerX;
        _calculator.CenterY       = _centerY;
        _calculator.Zoom          = _zoom;
        _calculator.MaxIterations = IterationsForZoom(_zoom);
    }

    /// <summary>
    /// Increases iteration depth logarithmically as zoom increases,
    /// balancing visual quality against compute time.
    /// </summary>
    private static int IterationsForZoom(double zoom)
    {
        int iters = 256 + (int)(Math.Log10(Math.Max(1.0, zoom)) * 128.0);
        return Math.Clamp(iters, 256, 4096);
    }

    // ── Async calculation with cancellation ───────────────────────────────────
    //
    // Each user gesture (zoom, pan, resize, reset) cancels any in-flight
    // calculation before launching a new one, so rapid input never queues up.

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
        var calc     = _calculator;   // capture for background thread
        var renderer = _renderer;

        SetStatus("Calculating…");

        var sw = Stopwatch.StartNew();

        Task.Run(() =>
        {
            calc.Calculate(token);
            return sw.ElapsedMilliseconds;
        }, token)
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
                    SetStatus(
                        $"cx={calc.CenterX:F10}  cy={calc.CenterY:F10}  " +
                        $"zoom={calc.Zoom:G5}  iter={calc.MaxIterations}  " +
                        $"[{ms} ms  {calc.Width}×{calc.Height}]");
                });
            }
        }, TaskScheduler.Default);
    }

    private void SetStatus(string text)
    {
        if (InvokeRequired)
            Invoke(() => _statusLabel.Text = text);
        else
            _statusLabel.Text = text;
    }

    // ── Disposal ──────────────────────────────────────────────────────────────

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _disposed = true;
        Application.Idle -= OnApplicationIdle;

        lock (_calcLock)
            _calcCts?.Cancel();

        _renderer?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _renderer?.Dispose();
        base.Dispose(disposing);
    }
}

// ── Custom DirectX render panel ───────────────────────────────────────────────

/// <summary>
/// A <see cref="Panel"/> that suppresses all GDI background painting, handing
/// the entire client area to DirectX without flicker or GDI/DX conflicts.
/// </summary>
internal sealed class RenderPanel : Panel
{
    public RenderPanel()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |  // combine WM_ERASEBKGND + WM_PAINT
            ControlStyles.Opaque               |  // no transparent background
            ControlStyles.UserPaint,              // suppress default WM_PAINT handling
            value: true);

        // Auto-focus on mouse-enter so the scroll wheel works without a click.
        MouseEnter += (_, _) => Focus();
    }

    // Prevent GDI from painting over the D3D11 back buffer.
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e)           { }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            // WS_EX_NOREDIRECTIONBITMAP: skip the DWM composition surface for
            // this child HWND so D3D11 presents directly to the screen.
            cp.ExStyle |= 0x00200000;
            return cp;
        }
    }
}

public class ColorComboBox : ComboBox
{
    public ColorComboBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        DropDownStyle = ComboBoxStyle.DropDownList;
    }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        string text = Items[e.Index].ToString() ?? "";
        IColorMap colorMap = Models.ColorPalette.GetPaletteByName(text);
        Color color = Color.FromArgb(colorMap.Map(0, 0, 0)); // Get a representative color
        e.Graphics.FillRectangle(new SolidBrush(color), e.Bounds);
        e.Graphics.DrawString(text, Font, Brushes.White, e.Bounds.X + 5, e.Bounds.Y + 3);
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        base.OnPaintBackground(pevent);
        using (var brush = new SolidBrush(BackColor))
        {
            pevent.Graphics.FillRectangle(brush, ClientRectangle);
            pevent.Graphics.DrawRectangle(Pens.DarkGray, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }
    }
}