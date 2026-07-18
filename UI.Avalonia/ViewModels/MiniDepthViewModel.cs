// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using System;
using Avalonia.Media.Imaging;
using ReactiveUI;

namespace FracturingFog.UI.Avalonia.ViewModels;

/// <summary>
/// View model for MiniDepthControl. Holds the current zoom, the quality's
/// max zoom, and an optional 1×N gradient strip sampled from the active
/// colour map. The control renders a vertical depth bar with a horizontal
/// indicator at log10(zoom) / log10(zoomMax).
/// </summary>
public sealed class MiniDepthViewModel : ViewModelBase
{
    private double _hostZoom = 1.0;
    public double HostZoom
    {
        get => _hostZoom;
        set => this.RaiseAndSetIfChanged(ref _hostZoom, Math.Max(1e-9, value));
    }

    private double _zoomMax = 1e13;
    public double ZoomMax
    {
        get => _zoomMax;
        set => this.RaiseAndSetIfChanged(ref _zoomMax, Math.Max(1.0, value));
    }

    private Bitmap? _gradient;
    public Bitmap? Gradient
    {
        get => _gradient;
        private set => this.RaiseAndSetIfChanged(ref _gradient, value);
    }

    public void SetGradient(Bitmap? bmp) => Gradient = bmp;
}
