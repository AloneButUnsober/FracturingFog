// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// MagnetisedBlackHoleCalculator.cs (#1944, gravitational-scattering set §2.6)
//
// Charged test particles round a Schwarzschild black hole in Wald's asymptotically uniform
// magnetic field (Wald, Phys. Rev. D 10, 1680, 1974; physics in Scattering/MagnetisedBlackHole.cs),
// traced with the shared scattering integrator (#1923) until captured or escaped up / down the
// field. The exit basins of this system are prior art: Bautista & Vega (Class. Quantum Grav. 38,
// 155016, 2021) mapped capture / upstream / downstream basins for particles kicked vertically
// from circular orbits and found them Wada. The Kick view is their launch over their axes
// (orbit radius ρ0, energy ℰ); the Meridional view launches from every point of the (R, z) plane
// at fixed ℰ and ℓ. Kopáček, Karas, Kovář & Stuchlík (ApJ 722, 1240, 2010) studied the onset of
// chaos in the trapped motion of the same kind of system.
//
// View: the standard 2D pan / zoom (ViewCamera.PixelPitch), vertical axis up. Kick: x = ρ0,
// y = ℰ. Meridional: x = R (mirrored about the axis, |x| used), y = z. Units of r_g = 2M.
//
// Colouring: categorical. Gate 0 = captured, 1 = escaped upstream (+z), 2 = escaped downstream
// (−z), −1 = undecided at the time limit. A billiard theme (IBilliardColorMap) colours the gates
// directly; otherwise Okabe-Ito per gate, shaded by log proper time. SmoothBuffer = log(1 + σ).
// Adaptive AA: every pixel is computed from its own coordinate, so the mask is exact.

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Scattering;

namespace FracturingFog;

public sealed class MagnetisedBlackHoleCalculator : IFractalCalculator, IHeightFieldSource,
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

    /// <summary>Per-pixel gate (see file header), or <see cref="InvalidGate"/> where no launch exists.</summary>
    public sbyte[] GateBuffer { get; private set; } = Array.Empty<sbyte>();
    public const sbyte InvalidGate = -2;
    /// <summary>Inside the hole (the capture radius): Kick ρ0 &lt; 1.01, Meridional ρ &lt; 1.01.</summary>
    public const sbyte HoleGate = -3;

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
    private MagnetisedBlackHoleCalculator? _preview;

    /// <summary>Default framing (Kick view): ρ0 from about 0.7 to 5.7, ℰ from about 0.1 to 2.9.</summary>
    public const double DefaultCenterX = 3.2, DefaultCenterY = -1.5, DefaultZoom = 0.7;

    /// <summary>No launch here (no circular orbit, ℰ below the potential, inside the capture radius).</summary>
    public const uint InvalidColor = 0xFF3A3A3A;
    /// <summary>The hole itself (inside the capture radius), darker than <see cref="InvalidColor"/>.</summary>
    public const uint HoleColor = 0xFF161616;

    public const int ShadeCap = 64;

    public MagnetisedBlackHoleCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[width * height];
        SmoothBuffer = new float[width * height];
        GateBuffer = new sbyte[width * height];
    }

    /// <summary>View point of pixel position (x, y) (pixel centres at +0.5), vertical axis up.</summary>
    public static (double A, double C) PlaneAt(double x, double y, double width, double height,
                                               double centerX, double centerY, double zoom)
    {
        double pitch = FracturingFog.ViewState.ViewCamera.PixelPitch(width, height, zoom);
        return (centerX + (x - width * 0.5) * pitch, -(centerY + (y - height * 0.5) * pitch));
    }

    public void Calculate(CancellationToken ct = default)
    {
        if (LowResPreview && RenderPreview(ct)) return;
        var scene = MagnetisedBhScene.Build(FractalParameters);
        var billiardMap = ColorMap as IBilliardColorMap;
        ColorMap.MaxIterations = MaxIterations;
        uint undecided = ColorMap.InSetColor | 0xFF000000u;
        double shadeRef = Math.Log(1.0 + scene.TimeCap);

        int width = Width, height = Height;
        double cx = CenterX, cy = CenterY, zoom = Zoom;
        var mask = PixelMask is { } m && m.Length >= width * height ? m : null;

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            if (ct.IsCancellationRequested) return;
            Span<double> s = stackalloc double[4];
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int idx = row + x;
                if (mask != null && mask[idx] == 0) continue;
                var (a, c) = PlaneAt(x + 0.5, y + 0.5, width, height, cx, cy, zoom);
                var o = scene.Trace(a, c, s);
                if (!o.Valid)
                {
                    GateBuffer[idx] = o.InsideHole ? HoleGate : InvalidGate;
                    SmoothBuffer[idx] = 0f;
                    ColorBuffer[idx] = o.InsideHole ? HoleColor : InvalidColor;
                    continue;
                }
                GateBuffer[idx] = (sbyte)o.Gate;
                double v = Math.Log(1.0 + o.Time);
                SmoothBuffer[idx] = (float)v;
                double t = Math.Clamp(v / Math.Max(1e-9, shadeRef), 0.0, 1.0);
                uint col;
                if (billiardMap != null)
                    col = unchecked((uint)billiardMap.MapBilliard(o.Gate, MagnetisedBhSystem.GateCount, (int)Math.Round(t * ShadeCap), ShadeCap, (float)t)) | 0xFF000000u;
                else if (o.Gate < 0)
                    col = undecided;
                else
                    col = unchecked((uint)BilliardColorHelper.Shade(BilliardColorHelper.GateColor(o.Gate), (float)(1.0 - 0.75 * t))) | 0xFF000000u;
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
            _preview = new MagnetisedBlackHoleCalculator(dims.Width, dims.Height);
        _preview.FractalParameters = FractalParameters;
        _preview.CenterX = CenterX; _preview.CenterY = CenterY; _preview.Zoom = Zoom;
        _preview.MaxIterations = MaxIterations; _preview.ColorMap = ColorMap; _preview.Quality = Quality;
        _preview.Calculate(ct);
        FracturingFog.Rendering.LowResPreview.UpscaleNearest(_preview.ColorBuffer, dims.Width, dims.Height, ColorBuffer, Width, Height);
        return true;
    }
}
