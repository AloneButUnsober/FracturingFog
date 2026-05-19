// MiniMapPanel.cs
//
// A small dockable overview panel that renders the complete Mandelbrot set at
// low resolution in the current colour theme.  A crosshair and circle mark the
// current view centre of the main window.  Double-clicking any position in the
// mini-map centres the main view on those complex-plane coordinates while
// keeping the current zoom and iteration count unchanged.
//
// Architecture
// ────────────
//   • Sits as a child control of _renderPanel (Fill-docked), positioned by
//     Anchor = Bottom | Right so it stays in the lower-right corner.
//   • The mini-map fractal is rendered on a background thread using a
//     dedicated MandelbrotCalculator at fixed 200×160 resolution with Draft
//     quality and 256 iterations — fast enough to be imperceptible.
//   • The indicator is repainted on every TriggerCalculation completion via
//     RefreshIndicator(), which just calls Invalidate() — no fractal
//     recalculation required.
//   • A new background render is triggered whenever RequestRedraw() is called
//     (e.g. after a colour-theme change).

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog.Views;

/// <summary>
/// Miniature Mandelbrot overview panel.  Configure() must be called once
/// after construction before the panel is added to a parent control.
/// </summary>
public sealed class MiniMapPanel : Control
{
    // ── Fixed overview parameters ─────────────────────────────────────────────
    private const int MapW      = 220;    // pixel width  of the mini-map bitmap
    private const int MapH      = 180;    // pixel height of the mini-map bitmap
    private const int Pad       = 4;      // border padding around bitmap
    private const double FullCX = -0.5;  // classic full-set centre (real)
    private const double FullCY =  0.0;  // classic full-set centre (imag)
    private const double FullZoom = 1.5; // zoom that shows the complete set

    // ── Callbacks ─────────────────────────────────────────────────────────────
    private Func<(double cx, double cy)>? _getCenter;
    private Func<double>?                 _getZoom;
    private Func<IColorMap?>?             _getColorMap;
    private Action<double, double>?       _navigateTo;
    private Func<Color>?                  _getSwatchColor;

    // ── Rendering state ───────────────────────────────────────────────────────
    private Bitmap? _mapBitmap;
    private CancellationTokenSource? _renderCts;
    private readonly object _renderLock = new();

    // ─────────────────────────────────────────────────────────────────────────
    // Construction / configuration
    // ─────────────────────────────────────────────────────────────────────────

    public MiniMapPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint            |
                 ControlStyles.OptimizedDoubleBuffer, true);
        Cursor = Cursors.Cross;
    }

    /// <summary>
    /// Wires the panel to the main form's view state.  Must be called before
    /// the panel is added to a parent control.
    /// </summary>
    public void Configure(
        Func<(double cx, double cy)> getCenter,
        Func<double>                 getZoom,
        Func<IColorMap?>             getColorMap,
        Action<double, double>       navigateTo,
        Func<Color>                  getSwatchColor)
    {
        _getCenter      = getCenter;
        _getZoom        = getZoom;
        _getColorMap    = getColorMap;
        _navigateTo     = navigateTo;
        _getSwatchColor = getSwatchColor;

        Width  = MapW + Pad * 2;
        Height = MapH + Pad * 2;

        DoubleClick += OnMapDoubleClick;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Launches a background render of the mini-map at the current colour
    /// theme.  Safe to call from any thread.
    /// </summary>
    public void RequestRedraw()
    {
        var map = _getColorMap?.Invoke();
        if (map == null) return;

        CancellationTokenSource cts;
        lock (_renderLock)
        {
            _renderCts?.Cancel();
            _renderCts = new CancellationTokenSource();
            cts = _renderCts;
        }

        // Capture a snapshot of the map reference — it may change on the
        // UI thread while we are rendering.
        var mapSnapshot = map;

        Task.Run(() => RenderBackground(mapSnapshot, cts.Token), cts.Token);
    }

    /// <summary>
    /// Repaints just the indicator overlay without re-rendering the fractal.
    /// Call this after every pan / zoom in the main window.
    /// </summary>
    public void RefreshIndicator()
    {
        if (IsHandleCreated) BeginInvoke(Invalidate);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Background rendering
    // ─────────────────────────────────────────────────────────────────────────

    private void RenderBackground(IColorMap map, CancellationToken ct)
    {
        try
        {
            var calc = new MandelbrotCalculator(MapW, MapH)
            {
                CenterX       = FullCX,
                CenterY       = FullCY,
                Zoom          = FullZoom,
                MaxIterations = 256,
                ColorMap      = map,
                Quality       = QualityPreset.Draft,
            };
            calc.Calculate(ct);
            if (ct.IsCancellationRequested) return;

            // Build bitmap from ColorBuffer (BGRA → ARGB conversion).
            var bmp = new Bitmap(MapW, MapH,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            unsafe
            {
                var data = bmp.LockBits(
                    new Rectangle(0, 0, MapW, MapH),
                    System.Drawing.Imaging.ImageLockMode.WriteOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                fixed (uint* src = calc.ColorBuffer)
                {
                    if (data.Stride == MapW * 4)
                        Buffer.MemoryCopy(src, (void*)data.Scan0,
                            (long)MapW * MapH * 4, (long)MapW * MapH * 4);
                    else
                    {
                        byte* dst = (byte*)data.Scan0;
                        for (int row = 0; row < MapH; row++)
                            Buffer.MemoryCopy(
                                (byte*)src + (long)row * MapW * 4,
                                dst + (long)row * data.Stride,
                                (long)MapW * 4, (long)MapW * 4);
                    }
                }
                bmp.UnlockBits(data);
            }

            if (ct.IsCancellationRequested) { bmp.Dispose(); return; }

            if (!IsHandleCreated) { bmp.Dispose(); return; }

            BeginInvoke(() =>
            {
                _mapBitmap?.Dispose();
                _mapBitmap = bmp;
                Invalidate();
            });
        }
        catch (OperationCanceledException) { /* expected */ }
        catch { /* swallow all errors — mini-map is non-critical */ }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Painting
    // ─────────────────────────────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var clientRect = ClientRectangle;
        var mapRect    = new Rectangle(Pad, Pad,
            clientRect.Width - Pad * 2, clientRect.Height - Pad * 2);

        // Background fill.
        using var bgBrush = new SolidBrush(Color.FromArgb(18, 18, 18));
        g.FillRectangle(bgBrush, clientRect);

        if (_mapBitmap != null)
        {
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.DrawImage(_mapBitmap, mapRect);

            // Draw indicator.
            if (_getCenter != null)
            {
                var (cx, cy) = _getCenter();
                DrawIndicator(g, mapRect, cx, cy);
            }
        }
        else
        {
            // Show a "Loading…" placeholder while the first render runs.
            using var placeholderBrush = new SolidBrush(Color.FromArgb(70, 70, 70));
            using var placeholderFont  = new Font("Segoe UI", 7.5f, FontStyle.Regular,
                GraphicsUnit.Point);
            var sf = new StringFormat
            {
                Alignment     = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString("Loading…", placeholderFont, placeholderBrush, mapRect, sf);
        }

        // Border.
        using var borderPen = new Pen(Color.FromArgb(75, 75, 75), 1f);
        g.DrawRectangle(borderPen, 0, 0, clientRect.Width - 1, clientRect.Height - 1);

        // Label.
        using var labelBrush = new SolidBrush(Color.FromArgb(110, 110, 110));
        using var labelFont  = new Font("Segoe UI", 6.5f, FontStyle.Regular, GraphicsUnit.Point);
        g.DrawString("Overview", labelFont, labelBrush, Pad + 2, Pad + 2);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Indicator drawing
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawIndicator(Graphics g, Rectangle mapRect, double cx, double cy)
    {
        // Map complex coords → mini-map pixel coords.
        double scale = 3.5 / (Math.Max(MapW, MapH) * FullZoom);
        double xMin  = FullCX - MapW * scale * 0.5;
        double yMin  = FullCY - MapH * scale * 0.5;

        float px = (float)((cx - xMin) / scale);
        float py = (float)((cy - yMin) / scale);

        // Scale to the mapRect on screen.
        float scaleX = mapRect.Width  / (float)MapW;
        float scaleY = mapRect.Height / (float)MapH;
        float sx = mapRect.Left + px * scaleX;
        float sy = mapRect.Top  + py * scaleY;

        // Clamp so indicator stays within the visible area.
        sx = Math.Clamp(sx, mapRect.Left + 1, mapRect.Right  - 1);
        sy = Math.Clamp(sy, mapRect.Top  + 1, mapRect.Bottom - 1);

        // Choose a contrasting colour based on the current theme swatch.
        Color swatch = _getSwatchColor?.Invoke() ?? Color.Gray;
        float lum    = (swatch.R * 0.299f + swatch.G * 0.587f + swatch.B * 0.114f) / 255f;
        Color ind    = lum > 0.45f
            ? Color.FromArgb(210, 0,   0,   0)    // dark indicator on light themes
            : Color.FromArgb(220, 255, 255, 255);  // light indicator on dark themes

        using var pen = new Pen(ind, 1.2f);

        // Circle.
        const float R = 5.5f;
        g.DrawEllipse(pen, sx - R, sy - R, R * 2f, R * 2f);

        // Crosshair lines (gap in the centre where the circle is).
        float gap = R + 1.5f;
        float arm = R + 6f;
        g.DrawLine(pen, sx - arm, sy, sx - gap, sy);
        g.DrawLine(pen, sx + gap, sy, sx + arm, sy);
        g.DrawLine(pen, sx, sy - arm, sx, sy - gap);
        g.DrawLine(pen, sx, sy + gap, sx, sy + arm);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Mouse: double-click to navigate
    // ─────────────────────────────────────────────────────────────────────────

    private void OnMapDoubleClick(object? sender, EventArgs e)
    {
        if (_navigateTo == null) return;

        // WinForms fires DoubleClick with a plain EventArgs; cast to MouseEventArgs
        // is safe because DoubleClick on a Control always provides mouse position.
        var me = e as MouseEventArgs;
        if (me == null) return;

        var clientRect = ClientRectangle;
        var mapRect    = new Rectangle(Pad, Pad,
            clientRect.Width - Pad * 2, clientRect.Height - Pad * 2);

        if (!mapRect.Contains(me.Location)) return;

        // Pixel within the map rect → complex coordinates.
        float scaleX = MapW / (float)mapRect.Width;
        float scaleY = MapH / (float)mapRect.Height;
        float mapPx  = (me.X - mapRect.Left) * scaleX;
        float mapPy  = (me.Y - mapRect.Top)  * scaleY;

        double scale = 3.5 / (Math.Max(MapW, MapH) * FullZoom);
        double xMin  = FullCX - MapW * scale * 0.5;
        double yMin  = FullCY - MapH * scale * 0.5;

        double newCX = xMin + mapPx * scale;
        double newCY = yMin + mapPy * scale;

        _navigateTo(newCX, newCY);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Disposal
    // ─────────────────────────────────────────────────────────────────────────

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_renderLock) { _renderCts?.Cancel(); _renderCts?.Dispose(); }
            _mapBitmap?.Dispose();
        }
        base.Dispose(disposing);
    }
}
