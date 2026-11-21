// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// SpinningBinaryCalculator.cs (#1945, gravitational-scattering set §2.6)
//
// Spinning compact binaries under post-Newtonian equations of motion (Scattering/SpinningBinary.cs:
// Kidder, Phys. Rev. D 52, 821, 1995, the equations Levin used in Phys. Rev. Lett. 84, 3515, 2000).
// Each pixel is a pair of initial spin polar angles (θ1 across, θ2 up, radians from the initial
// orbital angular momentum); the binary starts at separation r0 with speed v0 and is integrated with
// the shared scattering integrator (#1923) until it merges, separates or completes the orbit cap.
//
// Honesty about the dispute: Levin reported chaos and fractal basin boundaries; comments by Hughes
// and by Cornish (PRL 85, 2000), Schnittman & Rasio (PRL 87, 2001) and Hartl & Buonanno (PRD 71,
// 2005) dispute how far that holds, especially with radiation reaction or realistic spins. FF renders
// the outcome map at the chosen resolution and calls the structure "fractal-looking at finite
// resolution"; radiation reaction is a switch so both cases can be compared.
//
// Colouring: categorical (see SpinningBinaryScene gates) through IBilliardColorMap or Okabe-Ito per
// gate, shaded by orbits completed. SmoothBuffer = orbits. Adaptive AA: each pixel is computed from
// its own coordinate, so the mask is exact.

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Scattering;

namespace FracturingFog;

public sealed class SpinningBinaryCalculator : IFractalCalculator, IHeightFieldSource,
    FracturingFog.Rendering.ILowResPreviewCalculator, FracturingFog.Rendering.IAdaptiveAaCalculator
{
    public byte[]? PixelMask { get; set; }
    public FracturingFog.Rendering.AdaptiveAa AdaptiveAa => FracturingFog.Rendering.AdaptiveAa.From(FractalParameters);
    /// <summary>Always true: no pass reads neighbouring pixels.</summary>
    public bool MaskExact => true;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();
    public float[] SmoothBuffer { get; private set; } = Array.Empty<float>();
    /// <summary>Per-pixel gate (see <see cref="SpinningBinaryScene.GateCount"/>), −1 none.</summary>
    public sbyte[] GateBuffer { get; private set; } = Array.Empty<sbyte>();

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
    private SpinningBinaryCalculator? _preview;

    /// <summary>θ1 from −π to π across a landscape frame.</summary>
    public const double DefaultZoom = 0.557;
    public const int ShadeCap = 64;

    public SpinningBinaryCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[width * height];
        SmoothBuffer = new float[width * height];
        GateBuffer = new sbyte[width * height];
    }

    /// <summary>(θ1, θ2) in radians of pixel position (x, y) (pixel centres at +0.5), θ2 up.</summary>
    public static (double Theta1, double Theta2) PlaneAt(double x, double y, double width, double height,
                                                         double centerX, double centerY, double zoom)
    {
        double pitch = FracturingFog.ViewState.ViewCamera.PixelPitch(width, height, zoom);
        return (centerX + (x - width * 0.5) * pitch, -(centerY + (y - height * 0.5) * pitch));
    }

    public void Calculate(CancellationToken ct = default)
    {
        if (LowResPreview && RenderPreview(ct)) return;
        var scene = SpinningBinaryScene.Build(FractalParameters);
        var billiardMap = ColorMap as IBilliardColorMap;
        ColorMap.MaxIterations = MaxIterations;
        uint none = ColorMap.InSetColor | 0xFF000000u;
        double maxOrbits = scene.MaxOrbits;

        int width = Width, height = Height;
        double cx = CenterX, cy = CenterY, zoom = Zoom;
        var mask = PixelMask is { } m && m.Length >= width * height ? m : null;

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            if (ct.IsCancellationRequested) return;
            Span<double> s = stackalloc double[SpinningBinarySystem.StateSize];
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int idx = row + x;
                if (mask != null && mask[idx] == 0) continue;
                if (ct.IsCancellationRequested) return;
                var (t1, t2) = PlaneAt(x + 0.5, y + 0.5, width, height, cx, cy, zoom);
                var o = scene.Trace(t1, t2, s);
                GateBuffer[idx] = (sbyte)o.Gate;
                SmoothBuffer[idx] = (float)o.Orbits;
                double t = Math.Clamp(o.Orbits / Math.Max(1.0, maxOrbits), 0.0, 1.0);
                uint col;
                if (billiardMap != null)
                    col = unchecked((uint)billiardMap.MapBilliard(o.Gate, SpinningBinaryScene.GateCount, (int)Math.Round(t * ShadeCap), ShadeCap, (float)t)) | 0xFF000000u;
                else if (o.Gate < 0)
                    col = none;
                else
                    col = unchecked((uint)BilliardColorHelper.Shade(BilliardColorHelper.GateColor(o.Gate), (float)(1.0 - 0.6 * t))) | 0xFF000000u;
                ColorBuffer[idx] = col;
            }
        });
    }

    // #1373 drag preview: a smaller sibling, upscaled.
    private bool RenderPreview(CancellationToken ct)
    {
        double sc = FracturingFog.Rendering.LowResPreview.EffectiveScale(true, LowResPreviewScaleOverride,
                                                                          FractalParameters.LowResPreviewScale);
        if (sc >= 1.0) return false;
        var dims = FracturingFog.Rendering.LowResPreview.ComputeDims(Width, Height, sc);
        if (_preview == null || _preview.Width != dims.Width || _preview.Height != dims.Height)
            _preview = new SpinningBinaryCalculator(dims.Width, dims.Height);
        _preview.FractalParameters = FractalParameters;
        _preview.CenterX = CenterX; _preview.CenterY = CenterY; _preview.Zoom = Zoom;
        _preview.MaxIterations = MaxIterations; _preview.ColorMap = ColorMap; _preview.Quality = Quality;
        _preview.Calculate(ct);
        FracturingFog.Rendering.LowResPreview.UpscaleNearest(_preview.ColorBuffer, dims.Width, dims.Height, ColorBuffer, Width, Height);
        return true;
    }
}
