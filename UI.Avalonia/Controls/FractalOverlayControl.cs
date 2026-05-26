// UI.Avalonia/Controls/FractalOverlayControl.cs
//
// Cross-platform port of the legacy GridOverlayPanel + AddWaterMark routines
// from MainForm. Renders the Cartesian grid and the slideshow watermark
// directly via Avalonia.Media (DrawingContext + Pen + FormattedText) instead
// of GDI+ blending into the swap-chain buffer.
//
// Lives above the GpuSurfaceControl with IsHitTestVisible="False" so the
// transparent InputSponge below it still receives pointer events.
//
// Bound state (StyledProperty<T>):
//   • ViewSource          — FractalViewState (read CenterX/CenterY/Zoom/ColorMap)
//   • ShowGrid            — bool toggle
//   • ShowWatermark       — bool toggle
//   • RegionName, ThemeName, ProgramName, ProgramVersion (watermark text)
//
// Because FractalViewState mutates without raising INotifyPropertyChanged
// here (it lives in the shared abstractions assembly), the control kicks
// a ~10 Hz DispatcherTimer that calls InvalidateVisual when ShowGrid or
// ShowWatermark are true. Cheap — render is one pass of trivial line draws.

using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using FracturingFog.ViewState;

namespace FracturingFog.UI.Avalonia.Controls;

public sealed class FractalOverlayControl : Control
{
    public static readonly StyledProperty<FractalViewState?> ViewSourceProperty =
        AvaloniaProperty.Register<FractalOverlayControl, FractalViewState?>(nameof(ViewSource));

    public static readonly StyledProperty<bool> ShowGridProperty =
        AvaloniaProperty.Register<FractalOverlayControl, bool>(nameof(ShowGrid));

    public static readonly StyledProperty<bool> ShowWatermarkProperty =
        AvaloniaProperty.Register<FractalOverlayControl, bool>(nameof(ShowWatermark));

    public static readonly StyledProperty<string?> RegionNameProperty =
        AvaloniaProperty.Register<FractalOverlayControl, string?>(nameof(RegionName));

    public static readonly StyledProperty<string?> ThemeNameProperty =
        AvaloniaProperty.Register<FractalOverlayControl, string?>(nameof(ThemeName));

    public static readonly StyledProperty<string?> ProgramNameProperty =
        AvaloniaProperty.Register<FractalOverlayControl, string?>(nameof(ProgramName), "Fracturing Fog");

    public static readonly StyledProperty<string?> ProgramVersionProperty =
        AvaloniaProperty.Register<FractalOverlayControl, string?>(nameof(ProgramVersion));

    public FractalViewState? ViewSource
    {
        get => GetValue(ViewSourceProperty);
        set => SetValue(ViewSourceProperty, value);
    }

    public bool ShowGrid
    {
        get => GetValue(ShowGridProperty);
        set => SetValue(ShowGridProperty, value);
    }

    public bool ShowWatermark
    {
        get => GetValue(ShowWatermarkProperty);
        set => SetValue(ShowWatermarkProperty, value);
    }

    public string? RegionName
    {
        get => GetValue(RegionNameProperty);
        set => SetValue(RegionNameProperty, value);
    }

    public string? ThemeName
    {
        get => GetValue(ThemeNameProperty);
        set => SetValue(ThemeNameProperty, value);
    }

    public string? ProgramName
    {
        get => GetValue(ProgramNameProperty);
        set => SetValue(ProgramNameProperty, value);
    }

    public string? ProgramVersion
    {
        get => GetValue(ProgramVersionProperty);
        set => SetValue(ProgramVersionProperty, value);
    }

    private readonly DispatcherTimer _ticker;

    public FractalOverlayControl()
    {
        IsHitTestVisible = false;

        _ticker = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) =>
        {
            if (ShowGrid || ShowWatermark) InvalidateVisual();
        });
        AttachedToVisualTree += (_, _) => _ticker.Start();
        DetachedFromVisualTree += (_, _) => _ticker.Stop();
    }

    static FractalOverlayControl()
    {
        AffectsRender<FractalOverlayControl>(
            ShowGridProperty,
            ShowWatermarkProperty,
            ViewSourceProperty,
            RegionNameProperty,
            ThemeNameProperty,
            ProgramNameProperty,
            ProgramVersionProperty);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (!ShowGrid && !ShowWatermark) return;

        var bounds = Bounds;
        double w = bounds.Width, h = bounds.Height;
        if (w <= 1 || h <= 1) return;

        var state = ViewSource;

        if (ShowGrid && state != null)
            DrawCartesianGrid(context, w, h, state);

        if (ShowWatermark)
            DrawWatermark(context, w, h);
    }

    // ── Grid ──────────────────────────────────────────────────────────────

    private static void DrawCartesianGrid(DrawingContext ctx, double w, double h, FractalViewState s)
    {
        double cx = s.CenterX, cy = s.CenterY, zoom = s.Zoom;
        if (zoom <= 0 || double.IsNaN(zoom) || double.IsInfinity(zoom)) return;

        double scale = 3.5 / (Math.Max(w, h) * zoom);
        double xMin = cx - w * scale * 0.5, xMax = cx + w * scale * 0.5;
        double yMin = cy - h * scale * 0.5, yMax = cy + h * scale * 0.5;

        // Fixed light grid colour until IFractalRenderHost surfaces the
        // active IColorMap; see overlay TODO in PHASE2_AVALONIA_MIGRATION.md.
        Color contrast = Colors.White;
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(160, contrast.R, contrast.G, contrast.B)), 1.0);
        var axisPen = new Pen(new SolidColorBrush(Color.FromArgb(210, contrast.R, contrast.G, contrast.B)), 1.8);
        var labelBrush = new SolidColorBrush(Color.FromArgb(200, contrast.R, contrast.G, contrast.B));
        var shadowBrush = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0));

        var labelTypeface = new Typeface("Consolas");
        double labelSize = 10;
        double zeroSize = 12;

        double gridStep = NiceStep((xMax - xMin) / 7.0);
        if (gridStep <= 0) return;

        // Vertical lines.
        for (double wx = Math.Ceiling(xMin / gridStep) * gridStep;
             wx <= xMax + gridStep * 0.01; wx += gridStep)
        {
            double px = (wx - cx) / scale + w * 0.5;
            if (px < 0 || px > w) continue;
            bool isAxis = Math.Abs(wx) < gridStep * 0.01;
            ctx.DrawLine(isAxis ? axisPen : gridPen, new Point(px, 0), new Point(px, h));

            string lbl = FormatCoord(wx);
            var ft = new FormattedText(lbl, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                labelTypeface, labelSize, labelBrush);
            double lx = px - ft.Width * 0.5;
            double ly = h - ft.Height - 2;
            if (ly < 0) ly = 2;
            var ftShadow = new FormattedText(lbl, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                labelTypeface, labelSize, shadowBrush);
            ctx.DrawText(ftShadow, new Point(lx + 1, ly + 1));
            ctx.DrawText(ft, new Point(lx, ly));
        }

        // Horizontal lines.
        for (double wy = Math.Ceiling(yMin / gridStep) * gridStep;
             wy <= yMax + gridStep * 0.01; wy += gridStep)
        {
            double py = -(wy - cy) / scale + h * 0.5;
            if (py < 0 || py > h) continue;
            bool isAxis = Math.Abs(wy) < gridStep * 0.01;
            ctx.DrawLine(isAxis ? axisPen : gridPen, new Point(0, py), new Point(w, py));
            if (isAxis) continue;

            string lbl = FormatCoord(wy) + "i";
            var ft = new FormattedText(lbl, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                labelTypeface, labelSize, labelBrush);
            var ftShadow = new FormattedText(lbl, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                labelTypeface, labelSize, shadowBrush);
            ctx.DrawText(ftShadow, new Point(4, py - ft.Height * 0.5 + 1));
            ctx.DrawText(ft, new Point(3, py - ft.Height * 0.5));
        }

        // Origin marker.
        double ox = (0 - cx) / scale + w * 0.5;
        double oy = -(0 - cy) / scale + h * 0.5;
        if (ox >= 0 && ox <= w && oy >= 0 && oy <= h)
        {
            var zeroTypeface = new Typeface("Consolas", FontStyle.Normal, FontWeight.Bold);
            var ft = new FormattedText("0", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                zeroTypeface, zeroSize, labelBrush);
            var ftShadow = new FormattedText("0", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                zeroTypeface, zeroSize, shadowBrush);
            ctx.DrawText(ftShadow, new Point(ox + 3, oy + 3));
            ctx.DrawText(ft, new Point(ox + 2, oy + 2));
        }
    }

    private static double NiceStep(double raw)
    {
        if (raw <= 0) return 1.0;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double norm = raw / mag;
        double nice = norm <= 1.0 ? 1.0 : norm <= 2.0 ? 2.0 : norm <= 5.0 ? 5.0 : 10.0;
        return nice * mag;
    }

    private static string FormatCoord(double v)
    {
        if (v == 0.0) return "0";
        double abs = Math.Abs(v);
        int mag = (int)Math.Floor(Math.Log10(abs));
        int decimals = Math.Clamp(6 - mag, 0, 15);
        return v.ToString("F" + decimals, CultureInfo.InvariantCulture);
    }

    // ── Watermark ────────────────────────────────────────────────────────

    private void DrawWatermark(DrawingContext ctx, double w, double h)
    {
        string main = "";
        if (!string.IsNullOrEmpty(RegionName)) main = RegionName!;
        if (!string.IsNullOrEmpty(ThemeName))
            main = string.IsNullOrEmpty(main) ? ThemeName! : main + " - " + ThemeName;

        string sub = $"{ProgramName} v{ProgramVersion ?? "?"} {DateTime.Now.Year}";

        var mainTypeface = new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Bold);
        var subTypeface = new Typeface("Segoe UI", FontStyle.Normal, FontWeight.SemiBold);

        Color contrast = PickContrastColorFallback();
        var mainBrush = new SolidColorBrush(Color.FromArgb(205, contrast.R, contrast.G, contrast.B));
        var subBrush = new SolidColorBrush(Color.FromArgb(180, contrast.R, contrast.G, contrast.B));
        var shadowBrush = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0));

        var mainText = new FormattedText(main, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            mainTypeface, 18, mainBrush);
        var subText = new FormattedText(sub, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            subTypeface, 11, subBrush);

        double pad = 6;
        double bx = w - Math.Max(mainText.Width, subText.Width) - pad;
        double by = h - mainText.Height - subText.Height - pad;

        var mainShadow = new FormattedText(main, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            mainTypeface, 18, shadowBrush);
        var subShadow = new FormattedText(sub, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            subTypeface, 11, shadowBrush);

        if (!string.IsNullOrEmpty(main))
        {
            ctx.DrawText(mainShadow, new Point(bx + 1, by + 1));
            ctx.DrawText(mainText, new Point(bx, by));
        }
        double subY = by + mainText.Height;
        double subX = w - subText.Width - pad;
        ctx.DrawText(subShadow, new Point(subX + 1, subY + 1));
        ctx.DrawText(subText, new Point(subX, subY));
    }

    private static Color PickContrastColorFallback() => Colors.White;
}
