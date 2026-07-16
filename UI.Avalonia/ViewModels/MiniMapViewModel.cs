// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using Avalonia;
using Avalonia.Media.Imaging;
using FracturingFog;
using FracturingFog.Models;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

/// <summary>
/// View model for <see cref="Controls.MiniMapControl"/>. Renderer-agnostic —
/// the host owns whatever calculator/thread actually produces the thumbnail
/// bitmap and pushes it in via <see cref="SetThumbnail"/>. The VM just
/// surfaces:
/// • the bitmap to paint,
/// • current view centre + zoom for the indicator overlay,
/// • supported / placeholder state for 3D fractal types,
/// • a NavigationRequested event raised on double-click.
/// </summary>
public sealed class MiniMapViewModel : ViewModelBase
{
    private FractalType _activeType = FractalType.Mandelbrot;
    public FractalType ActiveType
    {
        get => _activeType;
        set
        {
            this.RaiseAndSetIfChanged(ref _activeType, value);
            this.RaisePropertyChanged(nameof(IsSupported));
            this.RaisePropertyChanged(nameof(PlaceholderText));
        }
    }

    /// <summary>Mirrors MiniMapDefaults.IsSupported. False = render placeholder, no nav.</summary>
    public bool IsSupported => MiniMapDefaults.IsSupported(_activeType);
    public string PlaceholderText => "3D — Overview N/A";

    private Bitmap? _thumbnail;
    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        private set => this.RaiseAndSetIfChanged(ref _thumbnail, value);
    }

    /// <summary>Centre point in the host's parameter-plane coordinates.</summary>
    private double _centerX;
    public double CenterX { get => _centerX; set => this.RaiseAndSetIfChanged(ref _centerX, value); }

    private double _centerY;
    public double CenterY { get => _centerY; set => this.RaiseAndSetIfChanged(ref _centerY, value); }

    /// <summary>Current host-window zoom. Drives the indicator radius.</summary>
    private double _hostZoom = 1.0;
    public double HostZoom
    {
        get => _hostZoom;
        set { this.RaiseAndSetIfChanged(ref _hostZoom, Math.Max(1e-9, value)); }
    }

    /// <summary>Push a freshly-rendered thumbnail into the control.</summary>
    public void SetThumbnail(Bitmap? bmp) => Thumbnail = bmp;

    /// <summary>
    /// Fired on double-click. Args are parameter-plane coordinates (already
    /// mapped from pixel space by the control using <see cref="ActiveType"/>'s
    /// default view bounds). Host re-centres the main view on the point.
    /// </summary>
    public event EventHandler<Point>? NavigationRequested;

    /// <summary>
    /// Called by the control when the user double-clicks a pixel inside the
    /// thumbnail. Translates pixel → parameter coords using
    /// <see cref="MiniMapDefaults.For"/> for the active type and the supplied
    /// control size, then fires <see cref="NavigationRequested"/>.
    /// </summary>
    public void RaiseNavigationFromPixel(double px, double py, double thumbWidth, double thumbHeight)
    {
        if (!IsSupported || thumbWidth <= 0 || thumbHeight <= 0) return;

        var bounds = MiniMapDefaults.For(_activeType);
        double maxDim = Math.Max(thumbWidth, thumbHeight);
        double scale = (3.5 / maxDim) / bounds.Zoom;

        // Convert pixel offset from the bitmap's centre into parameter delta.
        double dx = (px - thumbWidth  * 0.5) * scale;
        double dy = (py - thumbHeight * 0.5) * scale;

        double targetX = bounds.CenterX + dx;
        double targetY = bounds.CenterY + dy;

        NavigationRequested?.Invoke(this, new Point(targetX, targetY));
    }
}
