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
using FracturingFog.Imaging;
using FracturingFog.Models;
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

    /// <summary>Pre-sampled mid-band luminance of the active colour map in
    /// [0, 255]. 255 = fully white image → overlay picks black ink; low values
    /// → overlay picks white. Sampled host-side and mirrored through
    /// MainViewModel.OverlayContrastLuma so UI.Avalonia stays free of the
    /// main-project IColorMap type.</summary>
    public static readonly StyledProperty<byte> ContrastLumaProperty =
        AvaloniaProperty.Register<FractalOverlayControl, byte>(nameof(ContrastLuma), defaultValue: (byte)0);

    /// <summary>Optional user-configured watermark override. Null = render the
    /// legacy default (region/theme + auto-contrast colour, lower-right). When
    /// set the control honours its TextColor / HighlightColor / BackgroundColor
    /// / Placement / Justify. Subtext (program/version line) is mandatory and
    /// always drawn — the StyledProperty does not need to carry it.</summary>
    public static readonly StyledProperty<WatermarkDef?> CustomWatermarkProperty =
        AvaloniaProperty.Register<FractalOverlayControl, WatermarkDef?>(nameof(CustomWatermark));

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

    /// <summary>Pre-sampled luminance of the active colour map's mid band.
    /// Bound from MainViewModel.OverlayContrastLuma.</summary>
    public byte ContrastLuma
    {
        get => GetValue(ContrastLumaProperty);
        set => SetValue(ContrastLumaProperty, value);
    }

    public WatermarkDef? CustomWatermark
    {
        get => GetValue(CustomWatermarkProperty);
        set => SetValue(CustomWatermarkProperty, value);
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
            ProgramVersionProperty,
            ContrastLumaProperty,
            CustomWatermarkProperty);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (!ShowGrid && !ShowWatermark) return;

        var bounds = Bounds;
        double w = bounds.Width, h = bounds.Height;
        if (w <= 1 || h <= 1) return;

        var state = ViewSource;
        // Pick contrast colour from pre-sampled luma byte the host writes
        // through MainViewModel.OverlayContrastLuma. White ink on dark images;
        // near-black on light. 0 default treats unbound as "dark" → white ink.
        Color contrast = ContrastLuma < 128 ? Colors.White : Color.FromRgb(20, 20, 20);

        if (ShowGrid && state != null)
            DrawCartesianGrid(context, w, h, state, contrast);

        if (ShowWatermark)
            DrawWatermark(context, w, h, contrast);
    }

    // ── Grid ──────────────────────────────────────────────────────────────

    private static void DrawCartesianGrid(DrawingContext ctx, double w, double h, FractalViewState s, Color contrast)
    {
        double cx = s.CenterX, cy = s.CenterY, zoom = s.Zoom;
        if (zoom <= 0 || double.IsNaN(zoom) || double.IsInfinity(zoom)) return;

        double scale = 3.5 / (Math.Max(w, h) * zoom);
        double xMin = cx - w * scale * 0.5, xMax = cx + w * scale * 0.5;
        double yMin = cy - h * scale * 0.5, yMax = cy + h * scale * 0.5;
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(160, contrast.R, contrast.G, contrast.B)), 1.0);
        var axisPen = new Pen(new SolidColorBrush(Color.FromArgb(210, contrast.R, contrast.G, contrast.B)), 1.8);
        var labelBrush = new SolidColorBrush(Color.FromArgb(200, contrast.R, contrast.G, contrast.B));
        // Halo inverts ink so dark lines/text get a bright halo and vice versa.
        // Mirrors the Palette Builder ROI + MiniMap reticle halo trick — a wider
        // stroke under the foreground line keeps the grid visible against any
        // tone, not just images that contrast with the chosen ink colour.
        bool darkInk = (contrast.R + contrast.G + contrast.B) < 384;
        byte hs = darkInk ? (byte)255 : (byte)0;
        var haloColor      = Color.FromArgb(180, hs, hs, hs);
        var haloGridPen    = new Pen(new SolidColorBrush(haloColor), 3.0);
        var haloAxisPen    = new Pen(new SolidColorBrush(haloColor), 4.0);
        var shadowBrush    = new SolidColorBrush(Color.FromArgb(200, hs, hs, hs));

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
            ctx.DrawLine(isAxis ? haloAxisPen : haloGridPen, new Point(px, 0), new Point(px, h));
            ctx.DrawLine(isAxis ? axisPen     : gridPen,     new Point(px, 0), new Point(px, h));

            string lbl = FormatCoord(wx);
            var ft = new FormattedText(lbl, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                labelTypeface, labelSize, labelBrush);
            double lx = px - ft.Width * 0.5;
            double ly = h - ft.Height - 2;
            if (ly < 0) ly = 2;
            var ftShadow = new FormattedText(lbl, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                labelTypeface, labelSize, shadowBrush);
            DrawTextHalo(ctx, ftShadow, lx, ly);
            ctx.DrawText(ft, new Point(lx, ly));
        }

        // Horizontal lines.
        for (double wy = Math.Ceiling(yMin / gridStep) * gridStep;
             wy <= yMax + gridStep * 0.01; wy += gridStep)
        {
            double py = -(wy - cy) / scale + h * 0.5;
            if (py < 0 || py > h) continue;
            bool isAxis = Math.Abs(wy) < gridStep * 0.01;
            ctx.DrawLine(isAxis ? haloAxisPen : haloGridPen, new Point(0, py), new Point(w, py));
            ctx.DrawLine(isAxis ? axisPen     : gridPen,     new Point(0, py), new Point(w, py));
            if (isAxis) continue;

            string lbl = FormatCoord(wy) + "i";
            var ft = new FormattedText(lbl, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                labelTypeface, labelSize, labelBrush);
            var ftShadow = new FormattedText(lbl, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                labelTypeface, labelSize, shadowBrush);
            DrawTextHalo(ctx, ftShadow, 3, py - ft.Height * 0.5);
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
            DrawTextHalo(ctx, ftShadow, ox + 2, oy + 2);
            ctx.DrawText(ft, new Point(ox + 2, oy + 2));
        }
    }

    // 8-direction halo: draw the shadow text once per offset so the foreground
    // ink reads against any underlying tone. Cheap — labels are short.
    private static void DrawTextHalo(DrawingContext ctx, FormattedText shadow, double x, double y)
    {
        const double d = 1.2;
        ctx.DrawText(shadow, new Point(x - d, y - d));
        ctx.DrawText(shadow, new Point(x,     y - d));
        ctx.DrawText(shadow, new Point(x + d, y - d));
        ctx.DrawText(shadow, new Point(x - d, y));
        ctx.DrawText(shadow, new Point(x + d, y));
        ctx.DrawText(shadow, new Point(x - d, y + d));
        ctx.DrawText(shadow, new Point(x,     y + d));
        ctx.DrawText(shadow, new Point(x + d, y + d));
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

    private void DrawWatermark(DrawingContext ctx, double w, double h, Color contrast)
    {
        // Resolve top-line text + colour + placement. If CustomWatermark is set,
        // honour its fields verbatim. Otherwise fall back to today's auto-contrast
        // region/theme default (the contrast colour passed in by Render).
        var defaultText = new RgbDef(contrast.R, contrast.G, contrast.B);
        var render = WatermarkResolver.Resolve(
            activeCustom: CustomWatermark,
            regionEmbedded: null,                                  // overlay only sees the override-or-default; embedded resolution happens host-side
            overrideRegionWatermark: CustomWatermark != null,      // a control bound to a non-null def is, by construction, the active choice
            useCustomWatermark: CustomWatermark != null,
            regionName: RegionName ?? string.Empty,
            themeName: ThemeName ?? string.Empty,
            programName: ProgramName ?? "Fracturing Fog",
            programVersion: ProgramVersion ?? string.Empty,
            defaultTextColor: defaultText);

        var mainTypeface = new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Bold);
        var subTypeface  = new Typeface("Segoe UI", FontStyle.Normal, FontWeight.SemiBold);

        var fillColor = Color.FromRgb(render.TextColor.R, render.TextColor.G, render.TextColor.B);
        var mainBrush = new SolidColorBrush(Color.FromArgb(render.IsCustom ? (byte)255 : (byte)205,
            fillColor.R, fillColor.G, fillColor.B));
        var subBrush  = new SolidColorBrush(Color.FromArgb(render.IsCustom ? (byte)230 : (byte)180,
            fillColor.R, fillColor.G, fillColor.B));

        // Halo / shadow — user HighlightColor wins; otherwise auto-pick by luminance.
        SolidColorBrush shadowBrush;
        if (render.HighlightColor != null)
        {
            shadowBrush = new SolidColorBrush(Color.FromArgb(
                render.HighlightColor.A, render.HighlightColor.R, render.HighlightColor.G, render.HighlightColor.B));
        }
        else
        {
            bool darkInk = (fillColor.R + fillColor.G + fillColor.B) < 384; // 128*3
            byte hs = darkInk ? (byte)255 : (byte)0;
            shadowBrush = new SolidColorBrush(Color.FromArgb(160, hs, hs, hs));
        }

        var mainText = new FormattedText(render.TopText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            mainTypeface, 18, mainBrush);
        var subText  = new FormattedText(render.SubText,  CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            subTypeface, 11, subBrush);
        var mainShadow = new FormattedText(render.TopText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            mainTypeface, 18, shadowBrush);
        var subShadow  = new FormattedText(render.SubText,  CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            subTypeface, 11, shadowBrush);

        const int pad = 6;
        int topW = (int)Math.Ceiling(mainText.Width);
        int topH = (int)Math.Ceiling(mainText.Height);
        int subW = (int)Math.Ceiling(subText.Width);
        int subH = (int)Math.Ceiling(subText.Height);

        var (bx, by, bw, bh) = WatermarkResolver.ComputeBlockBounds(
            render, (int)w, (int)h, topW, topH, subW, subH, pad);

        // Optional opaque background rect — drawn first so glyphs render on top.
        if (render.BackgroundColor != null)
        {
            var bg = new SolidColorBrush(Color.FromArgb(
                render.BackgroundColor.A, render.BackgroundColor.R, render.BackgroundColor.G, render.BackgroundColor.B));
            const int bgPad = 4;
            ctx.FillRectangle(bg, new Rect(bx - bgPad, by - bgPad, bw + bgPad * 2, bh + bgPad * 2));
        }

        int topX = WatermarkResolver.AlignLineX(bx, bw, topW, render.Justify);
        int subX = WatermarkResolver.AlignLineX(bx, bw, subW, render.Justify);

        if (!string.IsNullOrEmpty(render.TopText))
        {
            ctx.DrawText(mainShadow, new Point(topX + 1, by + 1));
            ctx.DrawText(mainText,   new Point(topX,     by));
        }
        if (!string.IsNullOrEmpty(render.SubText))
        {
            int subY = by + topH;
            ctx.DrawText(subShadow, new Point(subX + 1, subY + 1));
            ctx.DrawText(subText,   new Point(subX,     subY));
        }
    }

}
