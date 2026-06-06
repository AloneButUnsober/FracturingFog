// Controls/PaletteResultControls.cs
//
// Two tiny custom-painted controls used by ImagePaletteView:
//
//   SwatchStripControl  — colour strip rendered from IReadOnlyList<PaletteSwatch>.
//   GradientStripControl — gradient rendered from IReadOnlyList<PaletteStop>,
//                          interpolated per-pixel for crisp output at any DIP size.
//
// Both pull data from the DataContext (PaletteResultViewModel) and react to
// DataContext / property changes via PropertyChanged listeners. No XAML —
// keeps the per-row template in the View lean.

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using FracturingFog.Imaging;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Controls;

public sealed class SwatchStripControl : Control
{
    private PaletteResultViewModel? _hookedVm;

    public SwatchStripControl()
    {
        Height = 26;
    }

    private PaletteResultViewModel? Vm => DataContext as PaletteResultViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_hookedVm is not null) _hookedVm.StopsChanged -= OnStopsChanged;
        _hookedVm = DataContext as PaletteResultViewModel;
        if (_hookedVm is not null) _hookedVm.StopsChanged += OnStopsChanged;
        InvalidateVisual();
    }

    private void OnStopsChanged() => global::Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);

    public override void Render(DrawingContext g)
    {
        base.Render(g);
        var vm = Vm;
        var rect = new Rect(0, 0, Bounds.Width, Bounds.Height);
        g.FillRectangle(new SolidColorBrush(Color.FromArgb(255, 20, 20, 20)), rect);

        if (vm is null) return;
        var src = vm.EffectivePalette;
        if (src.Count == 0) return;

        int n = src.Count;
        double spacing = 2;
        double avail = rect.Width - spacing * (n - 1);
        if (avail <= 0) return;

        double w = Math.Max(2, avail / n);
        double x = 0;
        for (int i = 0; i < n; i++)
        {
            var c = src[i];
            var brush = new SolidColorBrush(Color.FromArgb(255, c.R, c.G, c.B));
            g.FillRectangle(brush, new Rect(x, 0, w, rect.Height));
            x += w + spacing;
        }
    }
}

/// <summary>
/// Sampler hook so the gradient control can render in a non-sRGB
/// interpolation space without UI.Avalonia depending on the palette
/// extraction project. PaletteBuilder registers a sampler that delegates
/// to <c>GradientInterpolation.Sample</c>; with no hook the control falls
/// back to Avalonia's native sRGB LinearGradientBrush.
/// </summary>
public static class GradientRenderHook
{
    public delegate (byte R, byte G, byte B) SamplerFn(
        IReadOnlyList<(float Position, byte R, byte G, byte B)> sortedStops, float t);

    private static SamplerFn? _sampler;
    public static SamplerFn? Sampler
    {
        get => _sampler;
        set { _sampler = value; Changed?.Invoke(); }
    }

    /// <summary>Raised when Sampler changes — controls invalidate themselves.</summary>
    public static event Action? Changed;
}

public sealed class GradientStripControl : Control
{
    private PaletteResultViewModel? _hookedVm;

    public GradientStripControl()
    {
        Height = 30;
        GradientRenderHook.Changed += OnSettingsChanged;
    }

    private void OnSettingsChanged() => global::Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);

    private void OnStopsChanged() => global::Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);

    private PaletteResultViewModel? Vm => DataContext as PaletteResultViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_hookedVm is not null) _hookedVm.StopsChanged -= OnStopsChanged;
        _hookedVm = DataContext as PaletteResultViewModel;
        if (_hookedVm is not null) _hookedVm.StopsChanged += OnStopsChanged;
        InvalidateVisual();
    }

    public override void Render(DrawingContext g)
    {
        base.Render(g);
        var rect = new Rect(0, 0, Bounds.Width, Bounds.Height);
        g.FillRectangle(new SolidColorBrush(Color.FromArgb(255, 20, 20, 20)), rect);

        var vm = Vm;
        var src = vm?.EffectiveStops;
        if (vm is null || src is null || src.Count == 0)
        {
            DrawBorder(g, rect);
            return;
        }

        var ordered = new List<PaletteStop>(src);
        ordered.Sort((a, b) => a.Position.CompareTo(b.Position));

        if (ordered.Count == 1)
        {
            var c = ordered[0];
            g.FillRectangle(new SolidColorBrush(Color.FromArgb(255, c.R, c.G, c.B)), rect);
        }
        else if (GradientRenderHook.Sampler is null)
        {
            // Fast path — Avalonia native sRGB gradient brush.
            var brush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            };
            foreach (var s in ordered)
                brush.GradientStops.Add(new GradientStop(
                    Color.FromArgb(255, s.R, s.G, s.B),
                    Math.Clamp(s.Position, 0.0, 1.0)));
            g.FillRectangle(brush, rect);
        }
        else
        {
            // Perceptual path — sample each pixel column via host-provided
            // sampler (Lab / OkLab / whatever the host wires up).
            var tuples = new (float Position, byte R, byte G, byte B)[ordered.Count];
            for (int i = 0; i < ordered.Count; i++)
            {
                var s = ordered[i];
                tuples[i] = (s.Position, s.R, s.G, s.B);
            }
            int w = (int)Math.Ceiling(rect.Width);
            int h = (int)Math.Ceiling(rect.Height);
            var sampler = GradientRenderHook.Sampler;
            for (int x = 0; x < w; x++)
            {
                float t = (x + 0.5f) / Math.Max(1, w);
                var (r, gg, b) = sampler(tuples, t);
                g.FillRectangle(new SolidColorBrush(Color.FromArgb(255, r, gg, b)),
                    new Rect(x, 0, 1.0, h));
            }
        }

        DrawBorder(g, rect);
    }

    private static void DrawBorder(DrawingContext g, Rect rect)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(255, 75, 75, 75)), 1);
        g.DrawRectangle(null, pen, new Rect(0.5, 0.5, rect.Width - 1, rect.Height - 1));
    }
}
