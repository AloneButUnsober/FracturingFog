// Controls/MiniMapControl.cs
//
// Avalonia port of the legacy WinForms MiniMapPanel. Renders a host-supplied
// fractal-overview Bitmap and overlays an indicator showing where the main
// view sits relative to the active fractal's canonical 2D framing
// (FracturingFog.Models.MiniMapDefaults).
//
// The control owns NO calculator / no threads. The host project's existing
// MiniMap render pipeline still drives the background calculation; the
// Avalonia control just consumes the resulting bitmap via the bound
// MiniMapViewModel. Keeps UI.Avalonia free of IFractalCalculator and the
// renderer-side colour-map zoo.
//
// All sizing in DIPs. Indicator stays sharp at any DPI because the
// drawing primitives use Avalonia's vector pipeline (DrawingContext).

using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using FracturingFog;
using FracturingFog.Models;
using FracturingFog.UI.Avalonia.ViewModels;

namespace FracturingFog.UI.Avalonia.Controls;

public sealed class MiniMapControl : Control
{
    private MiniMapViewModel? _attachedVm;

    public MiniMapControl()
    {
        // Default DIP size — host can override via XAML / parent.
        Width = 220;
        Height = 180;
        DoubleTapped += OnDoubleTapped;
    }

    private MiniMapViewModel? Vm => DataContext as MiniMapViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // Hook the VM's PropertyChanged so Thumbnail / CenterX / CenterY /
        // HostZoom updates trigger a repaint. Without this the control draws
        // its initial state and never refreshes when the host pushes new
        // values in via SetThumbnail / FrameCompleted.
        if (_attachedVm != null) _attachedVm.PropertyChanged -= OnVmPropertyChanged;
        _attachedVm = Vm;
        if (_attachedVm != null) _attachedVm.PropertyChanged += OnVmPropertyChanged;
        InvalidateVisual();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MiniMapViewModel.Thumbnail)
         || e.PropertyName == nameof(MiniMapViewModel.CenterX)
         || e.PropertyName == nameof(MiniMapViewModel.CenterY)
         || e.PropertyName == nameof(MiniMapViewModel.HostZoom)
         || e.PropertyName == nameof(MiniMapViewModel.ActiveType))
        {
            InvalidateVisual();
        }
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Vm is null || !Vm.IsSupported) return;
        var pos = e.GetPosition(this);
        Vm.RaiseNavigationFromPixel(pos.X, pos.Y, Bounds.Width, Bounds.Height);
    }

    public override void Render(DrawingContext g)
    {
        base.Render(g);
        var vm = Vm;
        var rect = new Rect(0, 0, Bounds.Width, Bounds.Height);

        // Background.
        g.FillRectangle(new SolidColorBrush(Color.FromArgb(255, 18, 18, 18)), rect);

        if (vm is null)
        {
            DrawBorder(g, rect);
            return;
        }

        if (!vm.IsSupported)
        {
            DrawPlaceholder(g, rect, vm.PlaceholderText);
            DrawBorder(g, rect);
            return;
        }

        // Bitmap fill — letterboxed at 88% so the indicator reticle has
        // breathing room against the window edge and the fractal doesn't
        // butt up against the border.
        var imgRect = ShrinkCentered(rect, 0.88);
        if (vm.Thumbnail is Bitmap bmp)
        {
            g.DrawImage(bmp, new Rect(0, 0, bmp.PixelSize.Width, bmp.PixelSize.Height), imgRect);
        }
        else
        {
            DrawPlaceholder(g, imgRect, "(rendering…)");
        }

        // Indicator (uses the same shrunk rect so reticle aligns with image).
        DrawIndicator(g, imgRect, vm);

        DrawBorder(g, rect);
    }

    private static void DrawPlaceholder(DrawingContext g, Rect rect, string text)
    {
        var fg = new SolidColorBrush(Color.FromArgb(255, 170, 170, 170));
        var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Typeface.Default, 12, fg);
        var pt = new Point(
            rect.Left + (rect.Width - ft.Width) * 0.5,
            rect.Top + (rect.Height - ft.Height) * 0.5);
        g.DrawText(ft, pt);
    }

    private static void DrawIndicator(DrawingContext g, Rect rect, MiniMapViewModel vm)
    {
        // Map the host's current view centre into thumbnail pixel space using
        // the same convention as MiniMapDefaults (3.5/maxDim scale).
        var bounds = MiniMapDefaults.For(vm.ActiveType);
        double maxDim = Math.Max(rect.Width, rect.Height);
        double scale = (3.5 / maxDim) / bounds.Zoom;

        // Host centre relative to canonical centre, in pixels.
        double pxCenter = rect.Width * 0.5 + (vm.CenterX - bounds.CenterX) / scale;
        double pyCenter = rect.Height * 0.5 + (vm.CenterY - bounds.CenterY) / scale;

        // Indicator radius — small dot when zoomed in close, larger ring when
        // the host view roughly matches the thumbnail's framing.
        double indicatorRadius = Math.Max(3.0, Math.Min(rect.Width, rect.Height) * 0.06 / Math.Max(1.0, Math.Log10(vm.HostZoom + 1)));

        // Halo pass: draw a wider dark stroke under the ring + crosshair so
        // the reticle stays legible against any colour theme (bright yellows
        // / pale palettes washed out the previous flat-yellow indicator).
        // Mirrors the ROI overlay halo trick used in Palette Builder.
        var haloRingPen  = new Pen(new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)), 3.5);
        var haloCrossPen = new Pen(new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)), 3.0);
        double crossExt = indicatorRadius + 4;

        g.DrawEllipse(null, haloRingPen,
            new Point(pxCenter, pyCenter), indicatorRadius, indicatorRadius);
        g.DrawLine(haloCrossPen,
            new Point(pxCenter - crossExt, pyCenter),
            new Point(pxCenter + crossExt, pyCenter));
        g.DrawLine(haloCrossPen,
            new Point(pxCenter, pyCenter - crossExt),
            new Point(pxCenter, pyCenter + crossExt));

        // Foreground reticle on top of the halo.
        var ringPen  = new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 220, 80)), 1.5);
        var crossPen = new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 220, 80)), 1);

        g.DrawEllipse(null, ringPen,
            new Point(pxCenter, pyCenter), indicatorRadius, indicatorRadius);
        g.DrawLine(crossPen,
            new Point(pxCenter - crossExt, pyCenter),
            new Point(pxCenter + crossExt, pyCenter));
        g.DrawLine(crossPen,
            new Point(pxCenter, pyCenter - crossExt),
            new Point(pxCenter, pyCenter + crossExt));

        // Centre dot — small bright pip with its own halo so the exact view
        // centre is pinpoint-readable even when the ring is small.
        g.DrawEllipse(
            new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
            null,
            new Point(pxCenter, pyCenter), 2.2, 2.2);
        g.DrawEllipse(
            new SolidColorBrush(Color.FromArgb(255, 255, 220, 80)),
            null,
            new Point(pxCenter, pyCenter), 1.2, 1.2);
    }

    private static Rect ShrinkCentered(Rect r, double factor)
    {
        double w = r.Width * factor;
        double h = r.Height * factor;
        double x = r.X + (r.Width  - w) * 0.5;
        double y = r.Y + (r.Height - h) * 0.5;
        return new Rect(x, y, w, h);
    }

    private static void DrawBorder(DrawingContext g, Rect rect)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(255, 75, 75, 75)), 1);
        g.DrawRectangle(null, pen, new Rect(0.5, 0.5, rect.Width - 1, rect.Height - 1));
    }
}
