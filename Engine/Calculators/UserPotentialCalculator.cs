// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// UserPotentialCalculator.cs (#1994, User Potential)
//
// Exit basins of a user-written potential V(x, y) (Scattering/UserPotential.cs): each pixel is
// a launch at energy E, traced with the shared scattering integrator (#1923) until it leaves
// through an exit sector or meets the user's "exit if" rule. The Hénon–Heiles family (#1942)
// with V written by the user; its default V is Hénon–Heiles itself, so the default frame is
// that family's basin map.
//
// View, launch rules, colouring and channels follow HenonHeilesCalculator: gate k is
// categorical (a billiard theme colours it directly, else Okabe-Ito per gate, shaded by escape
// time); SmoothBuffer carries log(1 + escape time). A V or exit rule that does not parse paints
// the frame in ErrorColor (yellow, colour-blind safe) and the status bar shows the message.
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

public sealed class UserPotentialCalculator : IFractalCalculator, IHeightFieldSource,
    FracturingFog.Rendering.ILowResPreviewCalculator, FracturingFog.Rendering.IAdaptiveAaCalculator
{
    public byte[]? PixelMask { get; set; }
    public FracturingFog.Rendering.AdaptiveAa AdaptiveAa => FracturingFog.Rendering.AdaptiveAa.From(FractalParameters);
    public bool MaskExact => true;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();
    public float[] SmoothBuffer { get; private set; } = Array.Empty<float>();

    /// <summary>Per-pixel gate: exit sector / exit-if gate, -1 undecided, <see cref="ForbiddenGate"/>
    /// where the point is not an allowed launch.</summary>
    public sbyte[] GateBuffer { get; private set; } = Array.Empty<sbyte>();
    public const sbyte ForbiddenGate = -2;

    public double CenterX { get; set; } = 0.0;
    public double CenterY { get; set; } = HenonHeilesCalculator.DefaultCenterY;
    public double Zoom { get; set; } = HenonHeilesCalculator.DefaultZoom;
    public int MaxIterations { get; set; } = 256;
    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();
    public bool SupportsZoomPan => true;
    public FractalParameters FractalParameters { get; set; } = new();

    public bool LowResPreview { get; set; }
    public double? LowResPreviewScaleOverride { get; set; }
    private UserPotentialCalculator? _preview;

    /// <summary>The parse error of the last render, or null.</summary>
    public string? LastError { get; private set; }
    /// <summary>Status-bar text: the error, if any.</summary>
    public string? LastPrecisionLabel => LastError;

    public const uint ForbiddenColor = HenonHeilesCalculator.ForbiddenColor;
    /// <summary>Errors are yellow, not red (colour-blind-safe convention).</summary>
    public const uint ErrorColor = 0xFFFFCC00;
    public const int MaxTurnsShade = HenonHeilesCalculator.MaxTurnsShade;

    public UserPotentialCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[width * height];
        SmoothBuffer = new float[width * height];
        GateBuffer = new sbyte[width * height];
    }

    public static (double A, double B) PlaneAt(double x, double y, double width, double height,
                                               double centerX, double centerY, double zoom)
        => HenonHeilesCalculator.PlaneAt(x, y, width, height, centerX, centerY, zoom);

    public void Calculate(CancellationToken ct = default)
    {
        if (LowResPreview && RenderPreview(ct)) return;
        var scene = UserPotentialScene.Build(FractalParameters);
        LastError = scene.Model.Error;
        if (LastError != null)
        {
            Array.Fill(ColorBuffer, ErrorColor);
            Array.Fill(SmoothBuffer, 0f);
            Array.Fill(GateBuffer, ForbiddenGate);
            return;
        }
        var billiardMap = ColorMap as IBilliardColorMap;
        ColorMap.MaxIterations = MaxIterations;
        uint undecided = ColorMap.InSetColor | 0xFF000000u;
        double logCap = Math.Log(1.0 + scene.TimeCap);
        int gateCount = Math.Max(scene.Sectors, scene.Model.ExitIf != null ? 4 : 1);

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
                    GateBuffer[idx] = ForbiddenGate;
                    SmoothBuffer[idx] = 0f;
                    ColorBuffer[idx] = ForbiddenColor;
                    continue;
                }
                GateBuffer[idx] = (sbyte)o.Gate;
                double logT = Math.Log(1.0 + o.Time);
                SmoothBuffer[idx] = (float)logT;
                float pl = (float)Math.Clamp(logT / Math.Max(1e-9, logCap), 0.0, 1.0);
                if (billiardMap != null)
                    ColorBuffer[idx] = unchecked((uint)billiardMap.MapBilliard(o.Gate, gateCount, Math.Min(o.Turns, MaxTurnsShade), MaxTurnsShade, pl)) | 0xFF000000u;
                else if (o.Gate < 0)
                    ColorBuffer[idx] = undecided;
                else
                    ColorBuffer[idx] = unchecked((uint)BilliardColorHelper.Shade(BilliardColorHelper.GateColor(o.Gate), 1f - 0.75f * pl)) | 0xFF000000u;
            }
        });
    }

    private bool RenderPreview(CancellationToken ct)
    {
        double sc = FracturingFog.Rendering.LowResPreview.EffectiveScale(true, LowResPreviewScaleOverride,
                                                                          FractalParameters.LowResPreviewScale);
        if (sc >= 1.0) return false;
        var dims = FracturingFog.Rendering.LowResPreview.ComputeDims(Width, Height, sc);
        if (_preview == null || _preview.Width != dims.Width || _preview.Height != dims.Height)
            _preview = new UserPotentialCalculator(dims.Width, dims.Height);
        _preview.FractalParameters = FractalParameters;
        _preview.CenterX = CenterX; _preview.CenterY = CenterY; _preview.Zoom = Zoom;
        _preview.MaxIterations = MaxIterations; _preview.ColorMap = ColorMap; _preview.Quality = Quality;
        _preview.Calculate(ct);
        LastError = _preview.LastError;
        FracturingFog.Rendering.LowResPreview.UpscaleNearest(_preview.ColorBuffer, dims.Width, dims.Height, ColorBuffer, Width, Height);
        return true;
    }
}
