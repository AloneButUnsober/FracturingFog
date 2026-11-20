// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// BlackHoleShadowCalculator.cs (#1923, epic #1922)
//
// Shadows of several extremally charged black holes in static balance (the
// Majumdar-Papapetrou spacetime, see Scattering/MajumdarPapapetrou.cs). Each pixel is
// an image-plane point (u, v) of a distant camera; its null ray is traced backwards
// with the shared scattering integrator until it falls into hole i or escapes. The
// fate map is the image: each hole's shadow in its own colour, plus the "eyebrows"
// (rays captured by one hole after swinging round the other), whose nested copies
// form Cantor-like sets along the axis (Shipley & Dolan 2016; Yumoto, Nitta, Chiba &
// Sugiyama 2012 for the eyebrows and the deformed main shadows).
//
// The view is the impact-parameter plane, mapped through the standard 2D pan / zoom
// (pitch (4 / width) / zoom), in units of the hole masses (G = c = 1).
//
// Colouring: categorical, like the chaotic billiard. Gate i = captured by hole i,
// gate N = escaped, -1 = undecided at the step cap. A billiard theme (IBilliardColorMap)
// colours gates directly; otherwise each hole gets its Okabe-Ito colour, darkened
// (shadows), and escaped rays are coloured by the active palette from their
// coordinate-time delay ∫(U² - 1)dλ, which peaks along the shadow edges where rays
// orbit before escaping. SmoothBuffer carries log(1 + delay) for escaped rays (0 for
// captured), so relief and every escape-time palette work.

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Scattering;

namespace FracturingFog;

public sealed class BlackHoleShadowCalculator : IFractalCalculator, IHeightFieldSource, FracturingFog.Rendering.ILowResPreviewCalculator
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();
    public float[] SmoothBuffer { get; private set; } = Array.Empty<float>();

    /// <summary>Per-pixel fate: hole index, <see cref="EscapedGate"/>(N) for escaped, -1 undecided.</summary>
    public sbyte[] FateBuffer { get; private set; } = Array.Empty<sbyte>();

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
    private BlackHoleShadowCalculator? _preview;

    /// <summary>Default zoom: the frame is 4 / 0.16 = 25 mass units wide, which fits the
    /// default binary (unit masses, separation 2) with its eyebrows.</summary>
    public const double DefaultZoom = 0.16;

    /// <summary>Palette scale of log(1 + delay) for escaped rays.</summary>
    public const float DelayPaletteScale = 24f;

    /// <summary>Shadow brightness of a hole's Okabe-Ito colour (the built-in colouring).</summary>
    public const float ShadowShade = 0.55f;

    public BlackHoleShadowCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[width * height];
        SmoothBuffer = new float[width * height];
        FateBuffer = new sbyte[width * height];
    }

    /// <summary>Everything a frame needs, built once from the parameters.</summary>
    public sealed class Scene
    {
        public required MpHole[] Holes { get; init; }
        public required MpCamera Camera { get; init; }
        public required ScatterSettings Settings { get; init; }
        public required double CaptureFraction { get; init; }
        public required double EscapeRadius { get; init; }
        public int EscapedGate => Holes.Length;
        public MpNullSystem NewSystem() => new(Holes, CaptureFraction, EscapeRadius);

        /// <summary>Trace the ray through image-plane point (u, v); the final state is written
        /// to <paramref name="state"/> (length ≥ 6) when given.</summary>
        public (ScatterOutcome Outcome, MpNullSystem System) Trace(double u, double v, Span<double> state = default)
        {
            var sys = NewSystem();
            Span<double> s = state.Length >= 6 ? state : stackalloc double[6];
            Camera.Launch(sys, u, v, s);
            var o = ScatteringIntegrator.Trace(ref sys, s, Settings);
            return (o, sys);
        }

        public static Scene Build(FractalParameters p)
        {
            var holes = BlackHoleLayouts.Build(p);
            double extent = 0.0;
            foreach (var h in holes)
                extent = Math.Max(extent, Math.Sqrt(h.X * h.X + h.Y * h.Y + h.Z * h.Z) + 4.0 * h.M);
            double d = Math.Max(Math.Max(10.0, 4.0 * extent), double.IsFinite(p.BlackHoleCameraDistance) ? p.BlackHoleCameraDistance : 50.0);
            var cam = new MpCamera(d, p.BlackHoleInclination, p.BlackHoleAzimuth);
            double tol = Math.Clamp(double.IsFinite(p.BlackHoleTolerance) ? p.BlackHoleTolerance : 1e-9, 1e-13, 1e-4);
            return new Scene
            {
                Holes = holes,
                Camera = cam,
                CaptureFraction = Math.Clamp(double.IsFinite(p.BlackHoleCaptureFraction) ? p.BlackHoleCaptureFraction : 0.02, 1e-5, 0.5),
                EscapeRadius = d,
                Settings = new ScatterSettings
                {
                    RelTol = tol,
                    AbsTol = tol * 1e-3,
                    MaxSteps = Math.Clamp(p.BlackHoleMaxSteps, 100, 1_000_000),
                    InitialStep = 1.0,
                },
            };
        }
    }

    /// <summary>Image-plane point of pixel (x, y): the standard 2D mapping, with v up.</summary>
    public static (double U, double V) PlaneAt(double x, double y, double width, double height,
                                               double centerX, double centerY, double zoom)
    {
        var (px, py) = ChaoticBilliardCalculator.PlaneAt(x, y, width, height, centerX, centerY, zoom);
        return (px, -py);
    }

    public void Calculate(CancellationToken ct = default)
    {
        if (LowResPreview && RenderPreview(ct)) return;

        var scene = Scene.Build(FractalParameters);
        int n = scene.Holes.Length, escapedGate = scene.EscapedGate, gateCount = n + 1;
        var billiardMap = ColorMap as IBilliardColorMap;
        ColorMap.MaxIterations = MaxIterations;
        uint undecided = ColorMap.InSetColor;

        int width = Width, height = Height;
        double cx = CenterX, cy = CenterY, zoom = Zoom;

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            if (ct.IsCancellationRequested) return;
            Span<double> s = stackalloc double[6];
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                var (u, v) = PlaneAt(x + 0.5, y + 0.5, width, height, cx, cy, zoom);
                var (o, sys) = scene.Trace(u, v, s);
                int idx = row + x;
                int gate = o.Fate switch
                {
                    ScatterFate.Captured => o.Body,
                    ScatterFate.Escaped => escapedGate,
                    _ => -1,
                };
                FateBuffer[idx] = (sbyte)gate;
                float smooth = o.Fate == ScatterFate.Escaped ? (float)Math.Log(1.0 + Math.Max(0.0, sys.Delay)) : 0f;
                SmoothBuffer[idx] = smooth;
                ColorBuffer[idx] = Colour(gate, escapedGate, gateCount, smooth, billiardMap, ColorMap, undecided);
            }
        });
    }

    private static uint Colour(int gate, int escapedGate, int gateCount, float smooth,
                               IBilliardColorMap? billiard, IColorMap map, uint undecided)
    {
        if (billiard != null)
        {
            float pl = Math.Clamp(smooth / 6f, 0f, 1f);
            return unchecked((uint)billiard.MapBilliard(gate, gateCount, 0, 1, pl)) | 0xFF000000u;
        }
        if (gate < 0) return undecided | 0xFF000000u;
        if (gate == escapedGate)
        {
            float sm = smooth * DelayPaletteScale;
            return unchecked((uint)map.Map(sm, 0f, (int)sm)) | 0xFF000000u;
        }
        return unchecked((uint)BilliardColorHelper.Shade(BilliardColorHelper.GateColor(gate), ShadowShade)) | 0xFF000000u;
    }

    // #1373 drag preview: a smaller sibling, upscaled.
    private bool RenderPreview(CancellationToken ct)
    {
        double sc = FracturingFog.Rendering.LowResPreview.EffectiveScale(true, LowResPreviewScaleOverride,
                                                                          FractalParameters.LowResPreviewScale);
        if (sc >= 1.0) return false;
        var dims = FracturingFog.Rendering.LowResPreview.ComputeDims(Width, Height, sc);
        if (_preview == null || _preview.Width != dims.Width || _preview.Height != dims.Height)
            _preview = new BlackHoleShadowCalculator(dims.Width, dims.Height);
        _preview.FractalParameters = FractalParameters;
        _preview.CenterX = CenterX; _preview.CenterY = CenterY; _preview.Zoom = Zoom;
        _preview.MaxIterations = MaxIterations; _preview.ColorMap = ColorMap; _preview.Quality = Quality;
        _preview.Calculate(ct);
        FracturingFog.Rendering.LowResPreview.UpscaleNearest(_preview.ColorBuffer, dims.Width, dims.Height, ColorBuffer, Width, Height);
        return true;
    }
}
