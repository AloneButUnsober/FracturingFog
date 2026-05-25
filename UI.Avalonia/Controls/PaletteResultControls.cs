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
    public SwatchStripControl()
    {
        Height = 26;
    }

    private PaletteResultViewModel? Vm => DataContext as PaletteResultViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        InvalidateVisual();
    }

    public override void Render(DrawingContext g)
    {
        base.Render(g);
        var vm = Vm;
        var rect = new Rect(0, 0, Bounds.Width, Bounds.Height);
        g.FillRectangle(new SolidColorBrush(Color.FromArgb(255, 20, 20, 20)), rect);

        if (vm is null || vm.Palette.Count == 0) return;

        int n = vm.Palette.Count;
        double spacing = 2;
        double avail = rect.Width - spacing * (n - 1);
        if (avail <= 0) return;

        double w = Math.Max(2, avail / n);
        double x = 0;
        for (int i = 0; i < n; i++)
        {
            var c = vm.Palette[i];
            var brush = new SolidColorBrush(Color.FromArgb(255, c.R, c.G, c.B));
            g.FillRectangle(brush, new Rect(x, 0, w, rect.Height));
            x += w + spacing;
        }
    }
}

public sealed class GradientStripControl : Control
{
    public GradientStripControl()
    {
        Height = 30;
    }

    private PaletteResultViewModel? Vm => DataContext as PaletteResultViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        InvalidateVisual();
    }

    public override void Render(DrawingContext g)
    {
        base.Render(g);
        var rect = new Rect(0, 0, Bounds.Width, Bounds.Height);
        g.FillRectangle(new SolidColorBrush(Color.FromArgb(255, 20, 20, 20)), rect);

        var vm = Vm;
        if (vm is null || vm.Stops.Count == 0)
        {
            DrawBorder(g, rect);
            return;
        }

        // Avalonia's LinearGradientBrush handles the interpolation for us; we
        // just have to translate PaletteStop → GradientStop.
        var ordered = new List<PaletteStop>(vm.Stops);
        ordered.Sort((a, b) => a.Position.CompareTo(b.Position));

        if (ordered.Count == 1)
        {
            var c = ordered[0];
            g.FillRectangle(new SolidColorBrush(Color.FromArgb(255, c.R, c.G, c.B)), rect);
        }
        else
        {
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

        DrawBorder(g, rect);
    }

    private static void DrawBorder(DrawingContext g, Rect rect)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(255, 75, 75, 75)), 1);
        g.DrawRectangle(null, pen, new Rect(0.5, 0.5, rect.Width - 1, rect.Height - 1));
    }
}
