// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// MicrolensingCalculator.cs (#1948, microlensing magnification maps)
//
// The source-plane magnification map of a field of point-mass lenses (Lensing/Microlensing.cs),
// by inverse ray shooting (Kayser, Refsdal & Stabell 1986). The view is the SOURCE plane (the
// standard 2D pan / zoom, ViewCamera.PixelPitch, y up); each frame shoots rays through the
// preimage of the view and counts where they land, so a pan or zoom re-shoots.
//
// Colour: log10 of the magnification through the active palette (magnification 0.1 at the
// palette's start, then MicroColourScale x 24 palette units per decade); pixels no ray reached
// take the interior colour. SmoothBuffer carries log10 of the magnification (relief, palettes).
// Not a fractal: caustic networks are piecewise-smooth curves with cusps (see the help).
//
// The status bar reports the stars, the rays and the measured mean magnification over the frame
// against the theoretical 1 / ((1 - kappa)^2 - gamma^2).
//
// #2007 — an optional light-curve inset (MicrolensingCalculator.LightCurve.cs) is drawn over the
// map: a source track and the magnification along it in a panel at the bottom.

using System;
using System.Threading;

using FracturingFog.Interefaces;
using FracturingFog.Lensing;
using FracturingFog.Models;

namespace FracturingFog;

public sealed partial class MicrolensingCalculator : IFractalCalculator, IHeightFieldSource, FracturingFog.Rendering.ILowResPreviewCalculator
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();
    public float[] SmoothBuffer { get; private set; } = Array.Empty<float>();
    /// <summary>Per-pixel magnification of the last frame.</summary>
    public double[] Magnification { get; private set; } = Array.Empty<double>();

    public double CenterX { get; set; } = 0.0;
    public double CenterY { get; set; } = 0.0;
    public double Zoom { get; set; } = DefaultZoom;
    public int MaxIterations { get; set; } = 256;
    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();
    public bool SupportsZoomPan => true;
    public FractalParameters FractalParameters { get; set; } = new();

    public bool LowResPreview { get; set; }
    public double? LowResPreviewScaleOverride { get; set; }
    private MicrolensingCalculator? _preview;

    /// <summary>Default framing: about 4 Einstein radii across (3.5 / 0.875).</summary>
    public const double DefaultZoom = 0.875;
    public const double PalettePerDecade = 24.0;

    /// <summary>The last frame's shot and field (for tests and the status bar).</summary>
    public MicrolensShot? LastShot { get; private set; }
    public MicrolensField? LastField { get; private set; }
    public string? LastPrecisionLabel { get; private set; }

    public MicrolensingCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[width * height];
        SmoothBuffer = new float[width * height];
        Magnification = new double[width * height];
    }

    public void Calculate(CancellationToken ct = default)
    {
        if (LowResPreview && RenderPreview(ct)) return;
        var p = FractalParameters;
        var field = MicrolensField.Build(p);
        var shot = MicrolensShooter.Shoot(field, Width, Height, CenterX, CenterY, Zoom, p.MicroRaysPerPixel, p.MicroPad, p.MicroSeed, ct);
        if (ct.IsCancellationRequested) return;
        LastField = field; LastShot = shot;
        ColorMap.MaxIterations = MaxIterations;
        uint none = ColorMap.InSetColor | 0xFF000000u;
        double scale = PalettePerDecade * (double.IsFinite(p.MicroColourScale) ? Math.Clamp(p.MicroColourScale, 0.01, 100) : 1.0);
        double sum = 0;
        for (int i = 0; i < shot.Counts.Length; i++)
        {
            double mu = shot.MagnificationOf(shot.Counts[i]);
            Magnification[i] = mu;
            sum += mu;
            if (shot.Counts[i] == 0) { SmoothBuffer[i] = -1f; ColorBuffer[i] = none; continue; }
            double lg = Math.Log10(mu);
            SmoothBuffer[i] = (float)lg;
            ColorBuffer[i] = BlackHoleShadowCalculator.PaletteColour(ColorMap, (lg + 1.0) * scale);
        }
        double mean = shot.Counts.Length > 0 ? sum / shot.Counts.Length : 0;
        DrawLightCurve(p, field);   // #2007
        LastPrecisionLabel = $"{field.Count} stars, {shot.RaysShot / 1e6:0.#}M rays, mean mag {mean:0.###} (theory {field.MeanMagnification:0.###})"
            + (shot.OutsideField ? "; view reaches past the star field: raise Field radius" : "");
    }

    private bool RenderPreview(CancellationToken ct)
    {
        double sc = FracturingFog.Rendering.LowResPreview.EffectiveScale(true, LowResPreviewScaleOverride, FractalParameters.LowResPreviewScale);
        if (sc >= 1.0) return false;
        var dims = FracturingFog.Rendering.LowResPreview.ComputeDims(Width, Height, sc);
        if (_preview == null || _preview.Width != dims.Width || _preview.Height != dims.Height)
            _preview = new MicrolensingCalculator(dims.Width, dims.Height);
        _preview.FractalParameters = FractalParameters;
        _preview.CenterX = CenterX; _preview.CenterY = CenterY; _preview.Zoom = Zoom;
        _preview.MaxIterations = MaxIterations; _preview.ColorMap = ColorMap; _preview.Quality = Quality;
        _preview.Calculate(ct);
        LastPrecisionLabel = _preview.LastPrecisionLabel;
        FracturingFog.Rendering.LowResPreview.UpscaleNearest(_preview.ColorBuffer, dims.Width, dims.Height, ColorBuffer, Width, Height);
        return true;
    }
}
