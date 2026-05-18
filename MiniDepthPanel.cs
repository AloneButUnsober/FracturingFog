// MiniDepthPanel.cs
//
// A small dockable panel that visualises the current zoom depth as a vertical
// log-scale strip coloured with the active palette.  Zoom 0 (the "surface" of
// the Mandelbrot set) is at the top, the deepest zoom permitted by the active
// quality preset is at the bottom.  A horizontal indicator shows where the
// current view sits along that depth axis.
//
// Architecture mirrors MiniMapPanel:
//   • Child of _renderPanel, Anchor = Bottom | Left so it stays bottom-left.
//   • The gradient bitmap is built on the UI thread (a 1×N strip, very cheap)
//     by sampling the current IColorMap at evenly spaced smooth iteration
//     counts.  No background calculator is required.
//   • RefreshIndicator() repaints just the overlay after each pan/zoom.
//   • RequestRedraw() rebuilds the gradient bitmap when the theme changes.
//
// Depth metric: log10(max(zoom, 1)).  Below zoom = 1 we are still effectively
// at the "surface", so the indicator clamps to the top of the bar.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using FracturingFog.Interefaces;

namespace FracturingFog;

/// <summary>
/// Miniature zoom-depth indicator.  Configure() must be called once after
/// construction before the panel is added to a parent control.
/// </summary>
public sealed class MiniDepthPanel : Control
{
    // ── Fixed panel parameters ────────────────────────────────────────────────
    private const int PanelW   = 90;     // total panel width
    private const int PanelH   = 220;    // total panel height
    private const int Pad      = 4;      // outer border padding
    private const int BarW     = 22;     // gradient bar width
    private const int BarTop   = 22;     // distance from panel top to bar top (room for label)
    private const int BarBot   = 16;     // distance from panel bottom to bar bottom
    private const int GradSamples = 256; // resolution of the gradient strip

    // ── Callbacks ─────────────────────────────────────────────────────────────
    private Func<double>?     _getZoom;
    private Func<double>?     _getZoomMax;
    private Func<IColorMap?>? _getColorMap;
    private Func<Color>?      _getSwatchColor;

    // ── Rendering state ───────────────────────────────────────────────────────
    private Bitmap? _gradientBitmap;
    private double  _cachedZoomMax = 1e13;

    // ─────────────────────────────────────────────────────────────────────────
    // Construction / configuration
    // ─────────────────────────────────────────────────────────────────────────

    public MiniDepthPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint            |
                 ControlStyles.OptimizedDoubleBuffer, true);
    }

    /// <summary>
    /// Wires the panel to the main form's view state.
    /// </summary>
    public void Configure(
        Func<double>     getZoom,
        Func<double>     getZoomMax,
        Func<IColorMap?> getColorMap,
        Func<Color>      getSwatchColor)
    {
        _getZoom        = getZoom;
        _getZoomMax     = getZoomMax;
        _getColorMap    = getColorMap;
        _getSwatchColor = getSwatchColor;

        Width  = PanelW;
        Height = PanelH;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds the gradient bitmap using the current theme.  Call after a
    /// theme change.  Safe to call only from the UI thread.
    /// </summary>
    public void RequestRedraw()
    {
        BuildGradient();
        if (IsHandleCreated) Invalidate();
    }

    /// <summary>
    /// Repaints the indicator overlay without rebuilding the gradient.
    /// Call after every pan / zoom.
    /// </summary>
    public void RefreshIndicator()
    {
        if (IsHandleCreated) BeginInvoke(Invalidate);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Gradient construction
    // ─────────────────────────────────────────────────────────────────────────

    private void BuildGradient()
    {
        var map = _getColorMap?.Invoke();
        if (map == null) return;

        int maxIter = Math.Max(1, map.MaxIterations);

        var bmp = new Bitmap(1, GradSamples,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        // Sample colour at evenly spaced smooth iteration counts.  Top of bar
        // (row 0) = surface = low smooth value; bottom = deep = high smooth.
        for (int i = 0; i < GradSamples; i++)
        {
            float t = i / (float)(GradSamples - 1);
            float smooth = t * maxIter;
            // Mild tilt so 3D themes show shading, mirroring SwatchSample.
            int argb = map.Map(smooth, 0.05f, maxIter, 0.30f, 0.20f);
            bmp.SetPixel(0, i, Color.FromArgb(argb));
        }

        _gradientBitmap?.Dispose();
        _gradientBitmap = bmp;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Painting
    // ─────────────────────────────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var clientRect = ClientRectangle;

        // Background fill.
        using var bgBrush = new SolidBrush(Color.FromArgb(18, 18, 18));
        g.FillRectangle(bgBrush, clientRect);

        // Cache zoom-max so tick labels can use it even outside this method.
        _cachedZoomMax = Math.Max(10.0, _getZoomMax?.Invoke() ?? 1e13);
        double depthMax = Math.Log10(_cachedZoomMax);

        // Bar rectangle (left-aligned with room for labels on the right).
        var barRect = new Rectangle(
            Pad + 6,
            BarTop,
            BarW,
            clientRect.Height - BarTop - BarBot);

        // Gradient strip.
        if (_gradientBitmap != null)
        {
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.DrawImage(_gradientBitmap, barRect);
        }
        else
        {
            using var phBrush = new SolidBrush(Color.FromArgb(40, 40, 40));
            g.FillRectangle(phBrush, barRect);
        }

        // Bar border.
        using var barBorder = new Pen(Color.FromArgb(90, 90, 90), 1f);
        g.DrawRectangle(barBorder, barRect);

        // Tick marks + labels at decade boundaries.
        DrawTicks(g, barRect, depthMax);

        // Indicator for current depth.
        if (_getZoom != null)
        {
            double zoom = _getZoom();
            double depth = Math.Max(0.0, Math.Log10(Math.Max(zoom, 1.0)));
            DrawIndicator(g, barRect, depth, depthMax, zoom);
        }

        // Outer border.
        using var borderPen = new Pen(Color.FromArgb(75, 75, 75), 1f);
        g.DrawRectangle(borderPen, 0, 0, clientRect.Width - 1, clientRect.Height - 1);

        // Title.
        using var labelBrush = new SolidBrush(Color.FromArgb(150, 150, 150));
        using var labelFont  = new Font("Segoe UI", 6.5f, FontStyle.Regular, GraphicsUnit.Point);
        g.DrawString("Depth", labelFont, labelBrush, Pad + 2, Pad + 2);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Tick marks
    // ─────────────────────────────────────────────────────────────────────────

    private static void DrawTicks(Graphics g, Rectangle barRect, double depthMax)
    {
        // Pick a tick step so we get roughly 6–10 labelled ticks across the bar.
        int step;
        if      (depthMax <= 6)  step = 1;
        else if (depthMax <= 15) step = 2;
        else if (depthMax <= 30) step = 5;
        else                     step = 10;

        using var tickPen   = new Pen(Color.FromArgb(140, 140, 140), 1f);
        using var labelBr   = new SolidBrush(Color.FromArgb(160, 160, 160));
        using var labelFont = new Font("Segoe UI", 6.0f, FontStyle.Regular, GraphicsUnit.Point);

        for (int d = 0; d <= (int)Math.Ceiling(depthMax); d += step)
        {
            float t = (float)(d / depthMax);
            float y = barRect.Top + t * barRect.Height;

            // Short tick on the right edge of the bar.
            g.DrawLine(tickPen, barRect.Right, y, barRect.Right + 3, y);

            string lbl = d == 0 ? "0" : "10" + ToSuperscript(d);
            g.DrawString(lbl, labelFont, labelBr,
                barRect.Right + 4, y - 6);
        }
    }

    private static string ToSuperscript(int n)
    {
        // Unicode superscript digits for nicer "10ⁿ" rendering.
        ReadOnlySpan<char> sup = "⁰¹²³⁴⁵⁶⁷⁸⁹";
        string s = n.ToString();
        var buf = new char[s.Length];
        for (int i = 0; i < s.Length; i++)
            buf[i] = sup[s[i] - '0'];
        return new string(buf);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Indicator
    // ─────────────────────────────────────────────────────────────────────────

    private void DrawIndicator(Graphics g, Rectangle barRect, double depth, double depthMax, double zoom)
    {
        float t = (float)Math.Clamp(depth / depthMax, 0.0, 1.0);
        float y = barRect.Top + t * barRect.Height;

        // Contrast colour vs. current swatch (same heuristic as MiniMapPanel).
        Color swatch = _getSwatchColor?.Invoke() ?? Color.Gray;
        float lum    = (swatch.R * 0.299f + swatch.G * 0.587f + swatch.B * 0.114f) / 255f;
        Color ind    = lum > 0.45f
            ? Color.FromArgb(220, 0,   0,   0)
            : Color.FromArgb(230, 255, 255, 255);

        using var pen = new Pen(ind, 1.4f);

        // Horizontal line across the bar.
        g.DrawLine(pen, barRect.Left - 2, y, barRect.Right + 2, y);

        // Left-side arrow pointing right at the bar.
        using var fillBr = new SolidBrush(ind);
        var arrow = new[]
        {
            new PointF(barRect.Left - 6, y - 3.5f),
            new PointF(barRect.Left - 1, y),
            new PointF(barRect.Left - 6, y + 3.5f),
        };
        g.FillPolygon(fillBr, arrow);

        // Numeric depth readout near the indicator.
        using var lblBr   = new SolidBrush(ind);
        using var lblFont = new Font("Segoe UI", 6.5f, FontStyle.Bold, GraphicsUnit.Point);
        string text = FormatZoom(zoom);

        // Place label slightly above the line, flipping below near the top so
        // it doesn't get cropped.
        var size = g.MeasureString(text, lblFont);
        float lblX = barRect.Left + (barRect.Width - size.Width) * 0.5f;
        float lblY = y - size.Height - 1;
        if (lblY < barRect.Top - size.Height + 2) lblY = y + 2;
        // Tiny shadow box for readability over bright gradients.
        using var shadowBr = new SolidBrush(Color.FromArgb(140, 0, 0, 0));
        if (lum > 0.45f)
            shadowBr.Color = Color.FromArgb(140, 255, 255, 255);
        g.FillRectangle(shadowBr, lblX - 1, lblY, size.Width + 2, size.Height);
        g.DrawString(text, lblFont, lblBr, lblX, lblY);
    }

    private static string FormatZoom(double zoom)
    {
        if (zoom < 10.0)
            return zoom.ToString("0.0×");
        double exp = Math.Floor(Math.Log10(zoom));
        double man = zoom / Math.Pow(10, exp);
        return $"{man:0.0}e{(int)exp}";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Disposal
    // ─────────────────────────────────────────────────────────────────────────

    protected override void Dispose(bool disposing)
    {
        if (disposing) _gradientBitmap?.Dispose();
        base.Dispose(disposing);
    }
}
