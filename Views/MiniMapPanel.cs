// MiniMapPanel.cs
//
// A small dockable overview panel that renders the *active* fractal at low
// resolution in the current colour theme. A crosshair + circle mark the
// current view centre of the main window. Double-clicking a position on the
// mini-map centres the main view on those parameter-plane coordinates while
// keeping the current zoom and iteration count unchanged.
//
// Multi-fractal support
// ─────────────────────
//   • The mini-map asks MainForm (via the getFractalType / getFractalParams
//     callbacks) which fractal is active, then instantiates the matching
//     calculator at 220×180 with framing chosen by MiniMapDefaults.For().
//   • 3D fractals (Mandelbulb, UserBulb) have no 2D parameter-plane overview;
//     the panel renders a "3D — Overview N/A" placeholder and disables the
//     double-click navigation for those types.
//   • Chaos-game and density methods (IFS, L-System, Strange Attractor,
//     Buddhabrot) render with reduced iteration budgets so the thumbnail
//     refresh stays cheap.
//
// Architecture
// ────────────
//   • Sits as a child control of _renderPanel (Fill-docked), positioned by
//     Anchor = Bottom | Right so it stays in the lower-right corner.
//   • The fractal is rendered on a background thread inside a fresh
//     calculator instance — never reuses MainForm's calculator (different
//     dimensions + buffers).
//   • The indicator is repainted on every TriggerCalculation completion via
//     RefreshIndicator(), which just calls Invalidate() — no fractal
//     recalculation required.
//   • A new background render is triggered whenever RequestRedraw() is
//     called (after a colour-theme change, fractal-type change, or any
//     fractal-parameter change).

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
/// Miniature fractal overview panel. Configure() must be called once
/// after construction before the panel is added to a parent control.
/// </summary>
public sealed class MiniMapPanel : Control
{
    // ── Fixed overview parameters ─────────────────────────────────────────────
    private const int MapW = 220;    // pixel width  of the mini-map bitmap
    private const int MapH = 180;    // pixel height of the mini-map bitmap
    private const int Pad  = 4;      // border padding around bitmap

    // ── Callbacks ─────────────────────────────────────────────────────────────
    private Func<(double cx, double cy)>? _getCenter;
    private Func<double>?                 _getZoom;
    private Func<IColorMap?>?             _getColorMap;
    private Action<double, double>?       _navigateTo;
    private Func<Color>?                  _getSwatchColor;
    private Func<FractalType>?            _getFractalType;
    private Func<FractalParameters>?      _getFractalParams;

    // ── Rendering state ───────────────────────────────────────────────────────
    private Bitmap? _mapBitmap;
    private CancellationTokenSource? _renderCts;
    private readonly object _renderLock = new();

    // Type the current bitmap was rendered for — used to decide whether the
    // crosshair / navigation mapping is meaningful for the active fractal.
    private FractalType _bitmapType = FractalType.Mandelbrot;

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
    /// Wires the panel to the main form's view state. Must be called before
    /// the panel is added to a parent control.
    /// </summary>
    public void Configure(
        Func<(double cx, double cy)> getCenter,
        Func<double>                 getZoom,
        Func<IColorMap?>             getColorMap,
        Action<double, double>       navigateTo,
        Func<Color>                  getSwatchColor,
        Func<FractalType>            getFractalType,
        Func<FractalParameters>      getFractalParams)
    {
        _getCenter        = getCenter;
        _getZoom          = getZoom;
        _getColorMap      = getColorMap;
        _navigateTo       = navigateTo;
        _getSwatchColor   = getSwatchColor;
        _getFractalType   = getFractalType;
        _getFractalParams = getFractalParams;

        Width  = MapW + Pad * 2;
        Height = MapH + Pad * 2;

        DoubleClick += OnMapDoubleClick;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Launches a background render of the mini-map for the active fractal
    /// in the current colour theme. Safe to call from any thread.
    /// </summary>
    public void RequestRedraw()
    {
        var map  = _getColorMap?.Invoke();
        var type = _getFractalType?.Invoke() ?? FractalType.Mandelbrot;
        if (map == null) return;

        // Snapshot the parameters on the calling thread — the FractalParameters
        // instance lives on the UI side and can mutate during background render.
        var parms = _getFractalParams?.Invoke().Clone() ?? new FractalParameters();

        CancellationTokenSource cts;
        lock (_renderLock)
        {
            _renderCts?.Cancel();
            _renderCts = new CancellationTokenSource();
            cts = _renderCts;
        }

        if (!MiniMapDefaults.IsSupported(type))
        {
            // Drop any stale bitmap so OnPaint shows the placeholder.
            BeginInvoke(() =>
            {
                _mapBitmap?.Dispose();
                _mapBitmap = null;
                _bitmapType = type;
                Invalidate();
            });
            return;
        }

        Task.Run(() => RenderBackground(type, parms, map, cts.Token), cts.Token);
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

    private void RenderBackground(
        FractalType type, FractalParameters parms, IColorMap map, CancellationToken ct)
    {
        try
        {
            var bounds = MiniMapDefaults.For(type);
            int iter   = MiniMapDefaults.IterationsFor(type);

            uint[]? buffer = RenderThumbnail(type, parms, MapW, MapH, map, bounds, iter, ct);
            if (ct.IsCancellationRequested || buffer == null) return;

            var bmp = BufferToBitmap(buffer, MapW, MapH);

            if (ct.IsCancellationRequested) { bmp.Dispose(); return; }
            if (!IsHandleCreated)           { bmp.Dispose(); return; }

            BeginInvoke(() =>
            {
                _mapBitmap?.Dispose();
                _mapBitmap = bmp;
                _bitmapType = type;
                Invalidate();
            });
        }
        catch (OperationCanceledException) { /* expected */ }
        catch { /* swallow — mini-map is non-critical */ }
    }

    private static uint[]? RenderThumbnail(
        FractalType type, FractalParameters parms,
        int w, int h, IColorMap map,
        MiniMapDefaults.ViewBounds bounds, int iter,
        CancellationToken ct)
    {
        switch (type)
        {
            case FractalType.Mandelbrot:
            {
                var c = new MandelbrotCalculator(w, h)
                {
                    CenterX = bounds.CenterX,
                    CenterY = bounds.CenterY,
                    Zoom    = bounds.Zoom,
                    MaxIterations = iter,
                    ColorMap = map,
                    Quality  = QualityPreset.Draft,
                };
                c.Calculate(ct);
                return c.ColorBuffer;
            }

            case FractalType.Julia:
            case FractalType.BurningShip:
            case FractalType.Tricorn:
            case FractalType.Multibrot:
            case FractalType.Phoenix:
            {
                var c = new EscapeTimeCalculator(w, h)
                {
                    FractalType = type,
                    FractalParameters = parms,
                };
                ApplyCommon(c, bounds, iter, map);
                c.Calculate(ct);
                return c.ColorBuffer;
            }

            case FractalType.Newton:
            case FractalType.Nova:
            {
                var c = new NewtonCalculator(w, h) { FractalParameters = parms };
                ApplyCommon(c, bounds, iter, map);
                c.Calculate(ct);
                return c.ColorBuffer;
            }

            case FractalType.TearDrop:
            {
                var c = new TearDropCalculator(w, h) { FractalParameters = parms };
                ApplyCommon(c, bounds, iter, map);
                c.Calculate(ct);
                return c.ColorBuffer;
            }

            case FractalType.UserEquation:
            {
                var c = new UserEquationCalculator(w, h) { FractalParameters = parms };
                ApplyCommon(c, bounds, iter, map);
                c.Calculate(ct);
                return c.ColorBuffer;
            }

            case FractalType.Sandbox:
            {
                var c = new SandboxCalculator(w, h) { FractalParameters = parms };
                ApplyCommon(c, bounds, iter, map);
                c.Calculate(ct);
                return c.ColorBuffer;
            }

            case FractalType.BuddhaBrot:
            {
                // Lower sample count keeps the thumbnail fast.
                var thumbParms = parms.Clone();
                thumbParms.BuddhaSamples = Math.Min(parms.BuddhaSamples, 30_000);
                var c = new BuddhabrotCalculator(w, h) { FractalParameters = thumbParms };
                ApplyCommon(c, bounds, iter, map);
                c.Calculate(ct);
                return c.ColorBuffer;
            }

            case FractalType.IFS:
            {
                var thumbParms = parms.Clone();
                thumbParms.IFSIterations = Math.Min(parms.IFSIterations, 80_000);
                var c = new IFSCalculator(w, h) { FractalParameters = thumbParms };
                ApplyCommon(c, bounds, iter, map);
                c.Calculate(ct);
                return c.ColorBuffer;
            }

            case FractalType.LSystem:
            {
                var thumbParms = parms.Clone();
                thumbParms.LSystemDepth = Math.Min(parms.LSystemDepth, 4);
                var c = new LSystemCalculator(w, h) { FractalParameters = thumbParms };
                ApplyCommon(c, bounds, iter, map);
                c.Calculate(ct);
                return c.ColorBuffer;
            }

            case FractalType.StrangeAttractor:
            {
                var thumbParms = parms.Clone();
                thumbParms.AttractorIterations = Math.Min(parms.AttractorIterations, 80_000);
                var c = new AttractorCalculator(w, h) { FractalParameters = thumbParms };
                ApplyCommon(c, bounds, iter, map);
                c.Calculate(ct);
                return c.ColorBuffer;
            }

            default:
                return null; // 3D types handled earlier via IsSupported
        }
    }

    private static void ApplyCommon(
        IFractalCalculator c, MiniMapDefaults.ViewBounds bounds, int iter, IColorMap map)
    {
        c.CenterX = bounds.CenterX;
        c.CenterY = bounds.CenterY;
        c.Zoom    = bounds.Zoom;
        c.MaxIterations = iter;
        c.ColorMap = map;
        c.Quality  = QualityPreset.Draft;
    }

    private static unsafe Bitmap BufferToBitmap(uint[] src, int w, int h)
    {
        var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(
            new Rectangle(0, 0, w, h),
            System.Drawing.Imaging.ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        fixed (uint* p = src)
        {
            if (data.Stride == w * 4)
                Buffer.MemoryCopy(p, (void*)data.Scan0, (long)w * h * 4, (long)w * h * 4);
            else
            {
                byte* dst = (byte*)data.Scan0;
                for (int row = 0; row < h; row++)
                    Buffer.MemoryCopy(
                        (byte*)p + (long)row * w * 4,
                        dst + (long)row * data.Stride,
                        (long)w * 4, (long)w * 4);
            }
        }
        bmp.UnlockBits(data);
        return bmp;
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

        var activeType = _getFractalType?.Invoke() ?? FractalType.Mandelbrot;

        if (!MiniMapDefaults.IsSupported(activeType))
        {
            DrawPlaceholder(g, mapRect, "3D fractal\nOverview N/A");
        }
        else if (_mapBitmap != null)
        {
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.DrawImage(_mapBitmap, mapRect);

            // Indicator only meaningful when the bitmap matches the active type.
            if (_getCenter != null && _bitmapType == activeType)
            {
                var (cx, cy) = _getCenter();
                DrawIndicator(g, mapRect, activeType, cx, cy);
            }
        }
        else
        {
            DrawPlaceholder(g, mapRect, "Loading…");
        }

        // Border.
        using var borderPen = new Pen(Color.FromArgb(75, 75, 75), 1f);
        g.DrawRectangle(borderPen, 0, 0, clientRect.Width - 1, clientRect.Height - 1);

        // Label.
        using var labelBrush = new SolidBrush(Color.FromArgb(110, 110, 110));
        using var labelFont  = new Font("Segoe UI", 6.5f, FontStyle.Regular, GraphicsUnit.Point);
        g.DrawString("Overview", labelFont, labelBrush, Pad + 2, Pad + 2);
    }

    private static void DrawPlaceholder(Graphics g, Rectangle rect, string text)
    {
        using var brush = new SolidBrush(Color.FromArgb(110, 110, 110));
        using var font  = new Font("Segoe UI", 7.5f, FontStyle.Regular, GraphicsUnit.Point);
        var sf = new StringFormat
        {
            Alignment     = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        g.DrawString(text, font, brush, rect, sf);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Indicator drawing
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawIndicator(Graphics g, Rectangle mapRect, FractalType type, double cx, double cy)
    {
        var bounds = MiniMapDefaults.For(type);

        // Map complex coords → mini-map pixel coords using this type's framing.
        double scale = 3.5 / (Math.Max(MapW, MapH) * bounds.Zoom);
        double xMin  = bounds.CenterX - MapW * scale * 0.5;
        double yMin  = bounds.CenterY - MapH * scale * 0.5;

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
            ? Color.FromArgb(210, 0,   0,   0)
            : Color.FromArgb(220, 255, 255, 255);

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

        var activeType = _getFractalType?.Invoke() ?? FractalType.Mandelbrot;
        if (!MiniMapDefaults.IsSupported(activeType)) return;

        var me = e as MouseEventArgs;
        if (me == null) return;

        var clientRect = ClientRectangle;
        var mapRect    = new Rectangle(Pad, Pad,
            clientRect.Width - Pad * 2, clientRect.Height - Pad * 2);

        if (!mapRect.Contains(me.Location)) return;

        var bounds = MiniMapDefaults.For(activeType);

        // Pixel within the map rect → parameter-plane coordinates.
        float scaleX = MapW / (float)mapRect.Width;
        float scaleY = MapH / (float)mapRect.Height;
        float mapPx  = (me.X - mapRect.Left) * scaleX;
        float mapPy  = (me.Y - mapRect.Top)  * scaleY;

        double scale = 3.5 / (Math.Max(MapW, MapH) * bounds.Zoom);
        double xMin  = bounds.CenterX - MapW * scale * 0.5;
        double yMin  = bounds.CenterY - MapH * scale * 0.5;

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
