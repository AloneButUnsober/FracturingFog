// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Controls/MiniDepthControl.cs
//
// Avalonia port of the legacy WinForms MiniDepthPanel. Renders a vertical
// log-scale depth strip coloured by the host-supplied palette plus an
// indicator showing where the current view sits along the depth axis.
//
// Decoupled from FracturingFog.Interefaces.IColorMap on purpose — the host
// passes plain Func<int, uint> sampleColor / Func<uint> getSwatchColor
// callbacks that translate smooth iteration counts into packed ARGB. Keeps
// the UI assembly free of the renderer-side colour-map zoo and means future
// non-Mandelbrot backends can drive the depth strip without implementing
// IColorMap.
//
// DIP-only layout — width/height default to 90×220 but the control honours
// Width/Height set from XAML / parent so a high-DPI host can size it up.

using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace FracturingFog.UI.Avalonia.Controls;

public sealed class MiniDepthControl : Control
{
    private const int GradSamples = 256;

    public static readonly StyledProperty<double> DefaultPanelWidthProperty =
        AvaloniaProperty.Register<MiniDepthControl, double>(nameof(DefaultPanelWidth), 90.0);

    public static readonly StyledProperty<double> DefaultPanelHeightProperty =
        AvaloniaProperty.Register<MiniDepthControl, double>(nameof(DefaultPanelHeight), 220.0);

    public double DefaultPanelWidth
    {
        get => GetValue(DefaultPanelWidthProperty);
        set => SetValue(DefaultPanelWidthProperty, value);
    }

    public double DefaultPanelHeight
    {
        get => GetValue(DefaultPanelHeightProperty);
        set => SetValue(DefaultPanelHeightProperty, value);
    }

    private Func<double>? _getZoom;
    private Func<double>? _getZoomMax;
    private Func<int, uint>? _sampleColor;   // smooth iter index → ARGB
    private Func<int>? _getMaxIterations;
    private Func<uint>? _getSwatchArgb;

    private WriteableBitmap? _gradientBitmap;
    private double _cachedZoomMax = 1e13;

    public MiniDepthControl()
    {
        Width = 90;
        Height = 220;
    }

    /// <summary>
    /// Wire the control to host-supplied view state. All callbacks are
    /// invoked on the UI thread during Render(); they must be cheap.
    /// </summary>
    /// <param name="getZoom">Current zoom factor (1.0 = surface).</param>
    /// <param name="getZoomMax">Maximum zoom the active quality preset permits.</param>
    /// <param name="getMaxIterations">Current max-iteration depth for the gradient sample.</param>
    /// <param name="sampleColor">Smooth-iteration index → packed ARGB (0xFFRRGGBB).</param>
    /// <param name="getSwatchArgb">Current theme's representative swatch colour, packed ARGB.</param>
    public void Configure(
        Func<double> getZoom,
        Func<double> getZoomMax,
        Func<int> getMaxIterations,
        Func<int, uint> sampleColor,
        Func<uint> getSwatchArgb)
    {
        _getZoom = getZoom;
        _getZoomMax = getZoomMax;
        _getMaxIterations = getMaxIterations;
        _sampleColor = sampleColor;
        _getSwatchArgb = getSwatchArgb;
        Width = DefaultPanelWidth;
        Height = DefaultPanelHeight;
    }

    /// <summary>Rebuild the gradient strip after a theme change.</summary>
    public void RequestRedraw()
    {
        BuildGradient();
        InvalidateVisual();
    }

    /// <summary>Repaint the indicator overlay after pan/zoom. Cheap.</summary>
    public void RefreshIndicator() => InvalidateVisual();

    private void BuildGradient()
    {
        if (_sampleColor is null || _getMaxIterations is null) return;

        int maxIter = Math.Max(1, _getMaxIterations());
        var bmp = new WriteableBitmap(
            new PixelSize(1, GradSamples),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using (var fb = bmp.Lock())
        {
            // Avoid /unsafe — Marshal.WriteInt32 lets us populate the locked
            // buffer four bytes at a time without raw pointer arithmetic.
            for (int i = 0; i < GradSamples; i++)
            {
                float t = i / (float)(GradSamples - 1);
                int smoothIdx = (int)(t * maxIter);
                uint argb = _sampleColor(smoothIdx);
                uint bgra = ArgbToBgraPremul(argb);
                Marshal.WriteInt32(fb.Address, i * 4, unchecked((int)bgra));
            }
        }

        _gradientBitmap?.Dispose();
        _gradientBitmap = bmp;
    }

    private static uint ArgbToBgraPremul(uint argb)
    {
        // Source is 0xAARRGGBB; convert to 0xAABBGGRR premultiplied (alpha
        // is always 0xFF in our palette callbacks so premul == straight).
        byte a = (byte)((argb >> 24) & 0xFF);
        byte r = (byte)((argb >> 16) & 0xFF);
        byte g = (byte)((argb >> 8)  & 0xFF);
        byte b = (byte)(argb         & 0xFF);
        return ((uint)a << 24) | ((uint)b << 16) | ((uint)g << 8) | r;
    }

    public override void Render(DrawingContext g)
    {
        base.Render(g);

        var rect = new Rect(0, 0, Bounds.Width, Bounds.Height);

        // Background.
        g.FillRectangle(new SolidColorBrush(Color.FromArgb(255, 18, 18, 18)), rect);

        _cachedZoomMax = Math.Max(10.0, _getZoomMax?.Invoke() ?? 1e13);
        double depthMax = Math.Log10(_cachedZoomMax);

        // Bar layout — proportional to control size so per-monitor DPI works.
        double pad = 4;
        double barTop = 22;
        double barBot = 16;
        double barLeft = pad + 6;
        double barWidth = Math.Max(8, Math.Min(22, rect.Width * 0.25));
        var barRect = new Rect(barLeft, barTop, barWidth, Math.Max(8, rect.Height - barTop - barBot));

        // Gradient strip.
        if (_gradientBitmap is not null)
        {
            g.DrawImage(_gradientBitmap,
                new Rect(0, 0, 1, GradSamples),
                barRect);
        }
        else
        {
            g.FillRectangle(new SolidColorBrush(Color.FromArgb(255, 40, 40, 40)), barRect);
        }

        // Bar border.
        var barBorder = new Pen(new SolidColorBrush(Color.FromArgb(255, 90, 90, 90)), 1);
        g.DrawRectangle(null, barBorder, barRect);

        // Ticks.
        DrawTicks(g, barRect, depthMax);

        // Indicator.
        if (_getZoom is not null)
        {
            double zoom = _getZoom();
            double depth = Math.Max(0.0, Math.Log10(Math.Max(zoom, 1.0)));
            DrawIndicator(g, barRect, depth, depthMax, zoom);
        }

        // Outer border + title.
        var outerPen = new Pen(new SolidColorBrush(Color.FromArgb(255, 75, 75, 75)), 1);
        g.DrawRectangle(null, outerPen, new Rect(0.5, 0.5, rect.Width - 1, rect.Height - 1));

        var titleBrush = new SolidColorBrush(Color.FromArgb(255, 150, 150, 150));
        var titleText = new FormattedText("Depth", System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Typeface.Default, 10, titleBrush);
        g.DrawText(titleText, new Point(pad + 2, pad));
    }

    private static void DrawTicks(DrawingContext g, Rect barRect, double depthMax)
    {
        int step = depthMax switch
        {
            <= 6 => 1,
            <= 15 => 2,
            <= 30 => 5,
            _ => 10
        };

        var tickPen = new Pen(new SolidColorBrush(Color.FromArgb(255, 140, 140, 140)), 1);
        var lblBrush = new SolidColorBrush(Color.FromArgb(255, 160, 160, 160));

        for (int d = 0; d <= (int)Math.Ceiling(depthMax); d += step)
        {
            double t = d / depthMax;
            double y = barRect.Top + t * barRect.Height;

            g.DrawLine(tickPen,
                new Point(barRect.Right, y),
                new Point(barRect.Right + 3, y));

            string lbl = d == 0 ? "0" : "10" + ToSuperscript(d);
            var ft = new FormattedText(lbl, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, Typeface.Default, 9, lblBrush);
            g.DrawText(ft, new Point(barRect.Right + 4, y - 6));
        }
    }

    private static string ToSuperscript(int n)
    {
        ReadOnlySpan<char> sup = "⁰¹²³⁴⁵⁶⁷⁸⁹";
        string s = n.ToString();
        Span<char> buf = stackalloc char[s.Length];
        for (int i = 0; i < s.Length; i++)
            buf[i] = sup[s[i] - '0'];
        return new string(buf);
    }

    private void DrawIndicator(DrawingContext g, Rect barRect, double depth, double depthMax, double zoom)
    {
        double t = Math.Clamp(depth / depthMax, 0.0, 1.0);
        double y = barRect.Top + t * barRect.Height;

        uint swatch = _getSwatchArgb?.Invoke() ?? 0xFF808080u;
        byte sr = (byte)((swatch >> 16) & 0xFF);
        byte sg = (byte)((swatch >> 8) & 0xFF);
        byte sb = (byte)(swatch & 0xFF);
        float lum = (sr * 0.299f + sg * 0.587f + sb * 0.114f) / 255f;
        Color indColor = lum > 0.45f
            ? Color.FromArgb(220, 0, 0, 0)
            : Color.FromArgb(230, 255, 255, 255);
        var indBrush = new SolidColorBrush(indColor);
        var indPen = new Pen(indBrush, 1.4);

        // Horizontal indicator line.
        g.DrawLine(indPen,
            new Point(barRect.Left - 2, y),
            new Point(barRect.Right + 2, y));

        // Arrow on the left of the bar.
        var arrow = new StreamGeometry();
        using (var ctx = arrow.Open())
        {
            ctx.BeginFigure(new Point(barRect.Left - 6, y - 3.5), true);
            ctx.LineTo(new Point(barRect.Left - 1, y));
            ctx.LineTo(new Point(barRect.Left - 6, y + 3.5));
            ctx.EndFigure(true);
        }
        g.DrawGeometry(indBrush, null, arrow);

        // Numeric depth readout.
        string text = FormatZoom(zoom);
        var lblBrush = indBrush;
        var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface(Typeface.Default.FontFamily, FontStyle.Normal, FontWeight.Bold),
            10, lblBrush);
        double lblX = barRect.Left + (barRect.Width - ft.Width) * 0.5;
        double lblY = y - ft.Height - 1;
        if (lblY < barRect.Top - ft.Height + 2) lblY = y + 2;

        // Tiny shadow box for legibility over bright gradients.
        Color shadow = lum > 0.45f
            ? Color.FromArgb(140, 255, 255, 255)
            : Color.FromArgb(140, 0, 0, 0);
        g.FillRectangle(new SolidColorBrush(shadow),
            new Rect(lblX - 1, lblY, ft.Width + 2, ft.Height));
        g.DrawText(ft, new Point(lblX, lblY));
    }

    private static string FormatZoom(double zoom)
    {
        if (zoom < 10.0)
            return zoom.ToString("0.0×");
        double exp = Math.Floor(Math.Log10(zoom));
        double man = zoom / Math.Pow(10, exp);
        return $"{man:0.0}e{(int)exp}";
    }
}
