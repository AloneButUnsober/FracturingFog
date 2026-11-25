// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ThreeBodyCalculator.cs (#1943, gravitational-scattering set §2.6)
//
// The planar circular restricted three-body problem (Scattering/ThreeBody.cs) in the
// rotating frame, two views:
//  • Fates: each pixel is a launch position at the Jacobi constant C; the trajectory is
//    traced with the shared scattering integrator (#1923) until it crashes into a primary,
//    escapes, or is still bounded at the time limit. Nagler mapped these fates for the
//    equal-mass Copenhagen problem (Phys. Rev. E 69, 066218, 2004) and for general mass
//    ratio (Phys. Rev. E 71, 026227, 2005).
//  • Lagrange basins: each pixel starts multivariate Newton-Raphson on ∇Ω = 0 and is
//    coloured by which equilibrium L1-L5 it reaches, the method Zotos (Astrophys. Space Sci.
//    361, 181, 2016) applied with oblateness and radiation pressure added (FF has neither).
//
// View: the standard 2D pan / zoom (ViewCamera.PixelPitch), y up, rotating-frame units
// (unit separation of the primaries, centre of mass at the origin).
//
// Colouring: categorical. Fates: gate 0 = crash into primary 1 (mass 1 − μ, left),
// 1 = crash into primary 2, 2 = escape, −1 = bounded. Lagrange: gate 0..4 = L1..L5, −1 = no
// convergence. A billiard theme (IBilliardColorMap) colours gates directly; in the Lagrange
// view a Newton theme (INewtonColorMap) is used as for the root-finding fractals; otherwise
// each gate gets its Okabe-Ito colour, shaded by time / iterations. SmoothBuffer carries
// log(1 + time) (Fates) or the iteration count (Lagrange), so relief and palettes work.
// Adaptive AA: every pixel is computed from its own coordinate, so the mask is exact.
//
// #2049 part 2 — GPU: with GPU render on (Lighting & FX), the Fates view's trajectories run on
// the GPU ODE template (Scattering/ThreeBodyGpu.cs; D3D11 or Vulkan) through the shared
// OdeGpuFrame, and the colour pass below reads the outcomes. FF_GPU_ODE forces GPU / CPU; Auto
// measures. The Lagrange view (a Newton map) stays on the CPU.

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Scattering;

namespace FracturingFog;

public sealed class ThreeBodyCalculator : IFractalCalculator, IHeightFieldSource,
    FracturingFog.Rendering.ILowResPreviewCalculator, FracturingFog.Rendering.IAdaptiveAaCalculator,
    FracturingFog.Render.IGpuRouteSource
{
    /// <summary>#2049 — where the last frame ran.</summary>
    public FracturingFog.Render.GpuRoute LastGpuRoute { get; private set; }
    /// <summary>#2049 — the GPU backend of the last frame, or null for the CPU.</summary>
    public string? LastGpuBackend { get; private set; }
    /// <summary>#2049 — the Auto measurement behind the last frame's path, or null.</summary>
    public FracturingFog.Calculators.Gpu.OdeGpuRoute.Measurement? LastGpuMeasurement { get; private set; }
    private bool _isPreviewChild;

    public byte[]? PixelMask { get; set; }
    public FracturingFog.Rendering.AdaptiveAa AdaptiveAa => FracturingFog.Rendering.AdaptiveAa.From(FractalParameters);
    /// <summary>Always true: no pass reads neighbouring pixels.</summary>
    public bool MaskExact => true;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();
    public float[] SmoothBuffer { get; private set; } = Array.Empty<float>();

    /// <summary>Per-pixel gate (see file header), or <see cref="ForbiddenGate"/> where no launch
    /// is allowed (2Ω &lt; C, or inside a primary's crash radius).</summary>
    public sbyte[] GateBuffer { get; private set; } = Array.Empty<sbyte>();
    public const sbyte ForbiddenGate = -2;

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
    private ThreeBodyCalculator? _preview;

    public const double DefaultZoom = 1.0;   // 16:9 frame holds L4 / L5 (y = ±0.866) and L2 / L3

    /// <summary>Forbidden region (inside the zero-velocity curve, 2Ω &lt; C).</summary>
    public const uint ForbiddenColor = 0xFF3A3A3A;   // mid grey: bounded orbits take the theme interior (often black)

    /// <summary>Iteration / time-shade cap passed to billiard themes as the bounce range.</summary>
    public const int ShadeCap = 64;

    public ThreeBodyCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[width * height];
        SmoothBuffer = new float[width * height];
        GateBuffer = new sbyte[width * height];
    }

    /// <summary>Rotating-frame point of pixel position (x, y) (pixel centres at +0.5), y up.</summary>
    public static (double X, double Y) PlaneAt(double x, double y, double width, double height,
                                               double centerX, double centerY, double zoom)
    {
        double pitch = FracturingFog.ViewState.ViewCamera.PixelPitch(width, height, zoom);
        return (centerX + (x - width * 0.5) * pitch, -(centerY + (y - height * 0.5) * pitch));
    }

    /// <summary>Gates the view draws: 3 fates or 5 Lagrange points.</summary>
    public static int GateCount(ThreeBodyView view) => view == ThreeBodyView.LagrangeBasins ? 5 : ThreeBodySystem.GateCount;

    public void Calculate(CancellationToken ct = default)
    {
        if (LowResPreview && RenderPreview(ct)) return;
        var scene = ThreeBodyScene.Build(FractalParameters);
        bool lagrange = scene.View == ThreeBodyView.LagrangeBasins;
        int gateCount = GateCount(scene.View);
        var billiardMap = ColorMap as IBilliardColorMap;
        var newtonMap = lagrange ? ColorMap as INewtonColorMap : null;
        ColorMap.MaxIterations = MaxIterations;
        uint bounded = ColorMap.InSetColor | 0xFF000000u;
        double shadeRef = lagrange ? scene.NewtonIterations : Math.Log(1.0 + scene.TimeCap);

        int width = Width, height = Height;
        double cx = CenterX, cy = CenterY, zoom = Zoom;
        var mask = PixelMask is { } m && m.Length >= width * height ? m : null;

        void Store(int idx, double px, double py, in ThreeBodyOutcome o)
        {
            if (!o.Valid)
            {
                GateBuffer[idx] = ForbiddenGate;
                SmoothBuffer[idx] = 0f;
                ColorBuffer[idx] = ForbiddenColor;
                return;
            }
            GateBuffer[idx] = (sbyte)o.Gate;
            double v = lagrange ? o.Time : Math.Log(1.0 + o.Time);
            SmoothBuffer[idx] = (float)v;
            double t = Math.Clamp(v / Math.Max(1e-9, shadeRef), 0.0, 1.0);
            uint col;
            if (newtonMap != null)
                col = o.Gate < 0 ? bounded
                    : unchecked((uint)newtonMap.MapNewton(o.Gate, gateCount, (int)o.Time, scene.NewtonIterations, px, py)) | 0xFF000000u;
            else if (billiardMap != null)
                col = unchecked((uint)billiardMap.MapBilliard(o.Gate, gateCount, (int)Math.Round(t * ShadeCap), ShadeCap, (float)t)) | 0xFF000000u;
            else if (o.Gate < 0)
                col = bounded;
            else
                col = unchecked((uint)BilliardColorHelper.Shade(BilliardColorHelper.GateColor(o.Gate), (float)(1.0 - 0.75 * t))) | 0xFF000000u;
            ColorBuffer[idx] = col;
        }
        void Pixel(int idx, Span<double> s)
        {
            var (px, py) = PlaneAt(idx % width + 0.5, idx / width + 0.5, width, height, cx, cy, zoom);
            Store(idx, px, py, scene.Trace(px, py, s));
        }

        // #2049 part 2 — the Fates view on the GPU (shared ODE frame).
        LastGpuBackend = null; LastGpuMeasurement = null;
        LastGpuRoute = default;
        if (!lagrange)
        {
            double esc2 = scene.EscapeRadius * scene.EscapeRadius;
            var gpu = FracturingFog.Calculators.Gpu.OdeGpuFrame.TryRun(FractalParameters.Lighting, _isPreviewChild,
                FracturingFog.Calculators.Gpu.OdeGpuRoute.Shared, "FF_GPU_ODE",
                ThreeBodyGpu.Job(scene, FractalParameters.ThreeBodyGpuPrecision == BlackHoleGpuPrecision.Single),
                width, height, mask,
                (idx, st) =>
                {
                    var (px, py) = PlaneAt(idx % width + 0.5, idx / width + 0.5, width, height, cx, cy, zoom);
                    return scene.LaunchState(px, py, st) && px * px + py * py <= esc2;
                },
                (idx, res, i) =>
                {
                    var (px, py) = PlaneAt(idx % width + 0.5, idx / width + 0.5, width, height, cx, cy, zoom);
                    Store(idx, px, py, ThreeBodyGpu.OutcomeOf(res, i));
                },
                idx => { Span<double> s = stackalloc double[4]; Pixel(idx, s); },
                ct);
            LastGpuRoute = gpu.Route; LastGpuBackend = gpu.Backend; LastGpuMeasurement = gpu.Measurement;
            if (gpu.OnGpu || ct.IsCancellationRequested) return;
        }
        else LastGpuRoute = FracturingFog.Render.GpuRoute.Cpu("Lagrange view", "the Newton map runs on the CPU");

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            if (ct.IsCancellationRequested) return;
            Span<double> s = stackalloc double[4];
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int idx = row + x;
                if (mask != null && mask[idx] == 0) continue;
                Pixel(idx, s);
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
            _preview = new ThreeBodyCalculator(dims.Width, dims.Height) { _isPreviewChild = true };
        _preview.FractalParameters = FractalParameters;
        _preview.CenterX = CenterX; _preview.CenterY = CenterY; _preview.Zoom = Zoom;
        _preview.MaxIterations = MaxIterations; _preview.ColorMap = ColorMap; _preview.Quality = Quality;
        _preview.Calculate(ct);
        FracturingFog.Rendering.LowResPreview.UpscaleNearest(_preview.ColorBuffer, dims.Width, dims.Height, ColorBuffer, Width, Height);
        return true;
    }
}
