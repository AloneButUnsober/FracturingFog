// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// MixmasterCalculator.cs (#1946, gravitational-scattering set §2.6)
//
// Outcome basins of the vacuum Bianchi IX ("Mixmaster") cosmology, Cornish & Levin's picture of its
// chaos (Phys. Rev. Lett. 78, 998, 1997; Phys. Rev. D 55, 7489, 1997): each pixel is an initial
// condition, traced with the shared scattering integrator (#1923, physics in Scattering/Mixmaster.cs)
// until it settles in a Kasner epoch with u above u_exit, and coloured by which axis is collapsing
// most quickly then. KasnerUV view: their Fig. 16 launch over (u, v); Anisotropy view: their Fig. 19
// launch over (β+, β−). Motter & Letelier (Phys. Lett. A 285, 127, 2001) criticised the method and
// confirmed the conclusion; the help says so.
//
// View: the standard 2D pan / zoom (ViewCamera.PixelPitch), vertical axis up.
// Colouring: categorical (0 a, 1 b, 2 c collapsing) through IBilliardColorMap or Okabe-Ito per axis,
// shaded by log τ to exit. SmoothBuffer = log(1 + τ). Adaptive AA: each pixel is computed from its own
// coordinate, so the mask is exact.

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Scattering;

namespace FracturingFog;

public sealed class MixmasterCalculator : IFractalCalculator, IHeightFieldSource,
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
    /// <summary>Per-pixel collapsing axis (0 a, 1 b, 2 c), −1 none by the limit, <see cref="InvalidGate"/>.</summary>
    public sbyte[] GateBuffer { get; private set; } = Array.Empty<sbyte>();
    public const sbyte InvalidGate = -2;

    public double CenterX { get; set; } = DefaultCenterX;
    public double CenterY { get; set; } = DefaultCenterY;
    public double Zoom { get; set; } = DefaultZoom;
    public int MaxIterations { get; set; } = 256;
    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();
    public bool SupportsZoomPan => true;
    public FractalParameters FractalParameters { get; set; } = new();

    public bool LowResPreview { get; set; }
    public double? LowResPreviewScaleOverride { get; set; }
    private MixmasterCalculator? _preview;

    /// <summary>Default framing (KasnerUV): u from 1 to 2 across a landscape frame, v about 0.2-0.8.</summary>
    public const double DefaultCenterX = 1.5, DefaultCenterY = -0.5, DefaultZoom = 3.5;

    /// <summary>No launch here (u &lt; 1 or v ≤ 0 in the KasnerUV view).</summary>
    public const uint InvalidColor = 0xFF3A3A3A;
    public const int ShadeCap = 64;

    public MixmasterCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[width * height];
        SmoothBuffer = new float[width * height];
        GateBuffer = new sbyte[width * height];
    }

    /// <summary>View point of pixel position (x, y) (pixel centres at +0.5), vertical axis up.</summary>
    public static (double X, double Y) PlaneAt(double x, double y, double width, double height,
                                               double centerX, double centerY, double zoom)
    {
        double pitch = FracturingFog.ViewState.ViewCamera.PixelPitch(width, height, zoom);
        return (centerX + (x - width * 0.5) * pitch, -(centerY + (y - height * 0.5) * pitch));
    }

    public void Calculate(CancellationToken ct = default)
    {
        if (LowResPreview && RenderPreview(ct)) return;
        var scene = MixmasterScene.Build(FractalParameters);
        var billiardMap = ColorMap as IBilliardColorMap;
        ColorMap.MaxIterations = MaxIterations;
        uint none = ColorMap.InSetColor | 0xFF000000u;
        double shadeRef = Math.Log(1.0 + scene.TimeCap);

        int width = Width, height = Height;
        double cx = CenterX, cy = CenterY, zoom = Zoom;
        var mask = PixelMask is { } m && m.Length >= width * height ? m : null;

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            if (ct.IsCancellationRequested) return;
            Span<double> s = stackalloc double[6];
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int idx = row + x;
                if (mask != null && mask[idx] == 0) continue;
                var (px, py) = PlaneAt(x + 0.5, y + 0.5, width, height, cx, cy, zoom);
                var o = scene.Trace(px, py, s);
                if (!o.Valid)
                {
                    GateBuffer[idx] = InvalidGate;
                    SmoothBuffer[idx] = 0f;
                    ColorBuffer[idx] = InvalidColor;
                    continue;
                }
                GateBuffer[idx] = (sbyte)o.Gate;
                double v = Math.Log(1.0 + o.Time);
                SmoothBuffer[idx] = (float)v;
                double t = Math.Clamp(v / Math.Max(1e-9, shadeRef), 0.0, 1.0);
                uint col;
                if (billiardMap != null)
                    col = unchecked((uint)billiardMap.MapBilliard(o.Gate, MixmasterSystem.GateCount, (int)Math.Round(t * ShadeCap), ShadeCap, (float)t)) | 0xFF000000u;
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
            _preview = new MixmasterCalculator(dims.Width, dims.Height);
        _preview.FractalParameters = FractalParameters;
        _preview.CenterX = CenterX; _preview.CenterY = CenterY; _preview.Zoom = Zoom;
        _preview.MaxIterations = MaxIterations; _preview.ColorMap = ColorMap; _preview.Quality = Quality;
        _preview.Calculate(ct);
        FracturingFog.Rendering.LowResPreview.UpscaleNearest(_preview.ColorBuffer, dims.Width, dims.Height, ColorBuffer, Width, Height);
        return true;
    }
}
