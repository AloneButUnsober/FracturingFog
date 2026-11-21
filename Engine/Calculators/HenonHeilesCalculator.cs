// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// HenonHeilesCalculator.cs (#1942, gravitational-scattering set §2.6)
//
// Exit basins of the Hénon-Heiles potential (Hénon & Heiles 1964): each pixel is a launch
// condition at fixed energy E, traced with the shared scattering integrator
// (Scattering/HenonHeiles.cs on ScatteringIntegrator, #1923) until the particle leaves
// through one of the three channels. Above the escape energy 1/6 the basins are fractal
// and Wada, every boundary point touching all three basins (Aguirre, Vallejo & Sanjuán,
// Phys. Rev. E 64, 066208, 2001). The smooth-potential sibling of the Wada mirror
// spheres (#1439) and the chaotic billiards (#626).
//
// View: the standard 2D pan / zoom (the camera's pitch, ViewCamera.PixelPitch), y up.
// Plane: the pixel is the start position; Section: the pixel is (y, py) on x = 0.
//
// Colouring: categorical, like the chaotic billiard. Gate k = left through channel k
// (0 up, 1 lower left, 2 lower right), -1 = undecided at the time limit. A billiard theme
// (IBilliardColorMap) colours gates directly (turning points as its bounce count, escape
// time as its path length); otherwise each exit gets its Okabe-Ito colour, shaded by
// escape time. The other channels go through the active palette. SmoothBuffer carries
// log(1 + escape time), so relief and every escape-time palette work.
//
// Anti-aliasing: every pixel is traced from its own coordinate alone, so the adaptive AA
// mask (PixelMask) is always exact.

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Scattering;

namespace FracturingFog;

public sealed class HenonHeilesCalculator : IFractalCalculator, IHeightFieldSource,
    FracturingFog.Rendering.ILowResPreviewCalculator, FracturingFog.Rendering.IAdaptiveAaCalculator
{
    /// <summary>Adaptive anti-aliasing: with a mask (Width x Height bytes) only the marked
    /// pixels are traced; the others keep their buffer contents.</summary>
    public byte[]? PixelMask { get; set; }
    public FracturingFog.Rendering.AdaptiveAa AdaptiveAa => FracturingFog.Rendering.AdaptiveAa.From(FractalParameters);
    /// <summary>Always true: no pass reads neighbouring pixels.</summary>
    public bool MaskExact => true;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();
    public float[] SmoothBuffer { get; private set; } = Array.Empty<float>();

    /// <summary>Per-pixel exit: channel 0..2, -1 undecided, <see cref="ForbiddenGate"/> where
    /// the point is not an allowed launch.</summary>
    public sbyte[] ExitBuffer { get; private set; } = Array.Empty<sbyte>();
    public const sbyte ForbiddenGate = -2;

    public double CenterX { get; set; } = 0.0;
    public double CenterY { get; set; } = DefaultCenterY;
    public double Zoom { get; set; } = DefaultZoom;
    public int MaxIterations { get; set; } = 256;
    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();
    public bool SupportsZoomPan => true;
    public FractalParameters FractalParameters { get; set; } = new();

    public bool LowResPreview { get; set; }
    public double? LowResPreviewScaleOverride { get; set; }
    private HenonHeilesCalculator? _preview;

    /// <summary>Default framing: the well and its three saddles (plane y = −physical y).</summary>
    public const double DefaultZoom = 0.85, DefaultCenterY = -0.25;

    /// <summary>Colour of points that are not allowed launches (V &gt; E): the potential wall.</summary>
    public const uint ForbiddenColor = 0xFF0A0A0A;

    /// <summary>Palette units per unit of a channel's raw value (times HenonHeilesChannelScale).</summary>
    public const double ChannelPaletteScale = 24.0;

    /// <summary>Turning-point cap passed to billiard themes as the bounce range.</summary>
    public const int MaxTurnsShade = 64;

    public HenonHeilesCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[width * height];
        SmoothBuffer = new float[width * height];
        ExitBuffer = new sbyte[width * height];
    }

    /// <summary>View point (a, b) of pixel position (x, y) (pixel centres at +0.5): the camera's
    /// screen-to-world mapping, with b up.</summary>
    public static (double A, double B) PlaneAt(double x, double y, double width, double height,
                                               double centerX, double centerY, double zoom)
    {
        double pitch = FracturingFog.ViewState.ViewCamera.PixelPitch(width, height, zoom);
        return (centerX + (x - width * 0.5) * pitch, -(centerY + (y - height * 0.5) * pitch));
    }

    public void Calculate(CancellationToken ct = default)
    {
        if (LowResPreview && RenderPreview(ct)) return;
        var p = FractalParameters;
        var scene = HenonHeilesScene.Build(p);
        var channel = p.HenonHeilesChannel;
        double scale = ChannelPaletteScale * (double.IsFinite(p.HenonHeilesChannelScale) ? Math.Clamp(p.HenonHeilesChannelScale, 0.01, 100.0) : 1.0);
        var billiardMap = ColorMap as IBilliardColorMap;
        ColorMap.MaxIterations = MaxIterations;
        var colorMap = ColorMap;
        uint undecided = colorMap.InSetColor | 0xFF000000u;
        double logCap = Math.Log(1.0 + scene.TimeCap);

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
                var (a, b) = PlaneAt(x + 0.5, y + 0.5, width, height, cx, cy, zoom);
                var o = scene.Trace(a, b, s);
                if (!o.Valid)
                {
                    ExitBuffer[idx] = ForbiddenGate;
                    SmoothBuffer[idx] = 0f;
                    ColorBuffer[idx] = ForbiddenColor;
                    continue;
                }
                ExitBuffer[idx] = (sbyte)o.Exit;
                double logT = Math.Log(1.0 + o.Time);
                SmoothBuffer[idx] = (float)logT;
                ColorBuffer[idx] = Colour(channel, o, logT, logCap, scale, billiardMap, colorMap, undecided);
            }
        });
    }

    private static uint Colour(HenonHeilesChannel channel, in HenonHeilesOutcome o, double logT, double logCap,
                               double scale, IBilliardColorMap? billiard, IColorMap map, uint undecided)
    {
        if (channel == HenonHeilesChannel.Exit)
        {
            if (billiard != null)
            {
                float pl = (float)Math.Clamp(logT / Math.Max(1e-9, logCap), 0.0, 1.0);
                return unchecked((uint)billiard.MapBilliard(o.Exit, HenonHeilesSystem.ExitCount,
                    Math.Min(o.Turns, MaxTurnsShade), MaxTurnsShade, pl)) | 0xFF000000u;
            }
            if (o.Exit < 0) return undecided;
            // Okabe-Ito colour per exit; brighter for quick exits, darker near the boundary.
            float shade = (float)(1.0 - 0.75 * Math.Clamp(logT / Math.Max(1e-9, logCap), 0.0, 1.0));
            return unchecked((uint)BilliardColorHelper.Shade(BilliardColorHelper.GateColor(o.Exit), shade)) | 0xFF000000u;
        }
        if (o.Exit < 0) return undecided;
        double v = channel switch
        {
            HenonHeilesChannel.EscapeTime => logT,
            HenonHeilesChannel.TurningPoints => o.Turns / 4.0,
            _ => Math.Max(0.0, -Math.Log(Math.Max(1e-12, o.MinSaddleDistance))),
        };
        return BlackHoleShadowCalculator.PaletteColour(map, v * scale);
    }

    /// <summary>Colour of exit <paramref name="gate"/> as the frame draws it (legends, tests).</summary>
    public static uint GateColor(IColorMap? map, int gate)
    {
        if (map is IBilliardColorMap b)
            return unchecked((uint)b.MapBilliard(gate, HenonHeilesSystem.ExitCount, 0, MaxTurnsShade, 0f)) | 0xFF000000u;
        if (gate < 0) return map != null ? map.InSetColor | 0xFF000000u : 0xFF000000u;
        return unchecked((uint)BilliardColorHelper.GateColor(gate)) | 0xFF000000u;
    }

    // #1373 drag preview: a smaller sibling, upscaled.
    private bool RenderPreview(CancellationToken ct)
    {
        double sc = FracturingFog.Rendering.LowResPreview.EffectiveScale(true, LowResPreviewScaleOverride,
                                                                          FractalParameters.LowResPreviewScale);
        if (sc >= 1.0) return false;
        var dims = FracturingFog.Rendering.LowResPreview.ComputeDims(Width, Height, sc);
        if (_preview == null || _preview.Width != dims.Width || _preview.Height != dims.Height)
            _preview = new HenonHeilesCalculator(dims.Width, dims.Height);
        _preview.FractalParameters = FractalParameters;
        _preview.CenterX = CenterX; _preview.CenterY = CenterY; _preview.Zoom = Zoom;
        _preview.MaxIterations = MaxIterations; _preview.ColorMap = ColorMap; _preview.Quality = Quality;
        _preview.Calculate(ct);
        FracturingFog.Rendering.LowResPreview.UpscaleNearest(_preview.ColorBuffer, dims.Width, dims.Height, ColorBuffer, Width, Height);
        return true;
    }
}
