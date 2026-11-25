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
// mask (PixelMask) is exact, except with solid blocks (a tile's fill depends on its ring).
//
// #2049 — GPU: with GPU render on (Lighting & FX), the trajectories run on the GPU ODE template
// (Scattering/HenonHeilesGpu.cs; D3D11 or Vulkan, OdeTraceBackends), double precision by
// default, and the colour pass below reads the outcomes, so the frame is coloured by the same
// code. Auto (FF_GPU_HENON_HEILES unset) measures once per kernel and keeps the faster path
// (OdeGpuRoute). #2049 — solid blocks (opt-in): with flat exit colours (no escape-time
// shading, no billiard theme) a pixel's colour is a function of its exit alone, so the shared
// SolidBlockFill fills uniform 16 x 16 tiles from their traced ring (CPU path).

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;
using FracturingFog.Scattering;

namespace FracturingFog;

public sealed class HenonHeilesCalculator : IFractalCalculator, IHeightFieldSource,
    FracturingFog.Rendering.ILowResPreviewCalculator, FracturingFog.Rendering.IAdaptiveAaCalculator,
    FracturingFog.Render.IGpuRouteSource
{
    /// <summary>Adaptive anti-aliasing: with a mask (Width x Height bytes) only the marked
    /// pixels are traced; the others keep their buffer contents.</summary>
    public byte[]? PixelMask { get; set; }
    public FracturingFog.Rendering.AdaptiveAa AdaptiveAa => FracturingFog.Rendering.AdaptiveAa.From(FractalParameters);
    /// <summary>True unless solid blocks fill tiles from their ring (#2049).</summary>
    public bool MaskExact => !SolidBlocksActive(FractalParameters, ColorMap);

    /// <summary>#2049 — where the last frame ran.</summary>
    public FracturingFog.Render.GpuRoute LastGpuRoute { get; private set; }
    /// <summary>#2049 — the GPU backend of the last frame ("D3D11 (device)"), or null for the CPU.</summary>
    public string? LastGpuBackend { get; private set; }
    /// <summary>#2049 — the Auto measurement behind the last frame's path, or null.</summary>
    public FracturingFog.Calculators.Gpu.OdeGpuRoute.Measurement? LastGpuMeasurement { get; private set; }
    /// <summary>#2049 — pixels the last frame filled from solid blocks instead of tracing.</summary>
    public long LastSolidFilled { get; private set; }
    private bool _isPreviewChild;   // the drag preview's smaller sibling stays on the CPU

    /// <summary>#2049 — GPU vs CPU when GPU render is on (FF_GPU_HENON_HEILES); a set value
    /// overrides for the calling thread only (tests).</summary>
    public static FracturingFog.Calculators.Gpu.OdeGpuRoute.Mode GpuSelection
    {
        get => t_gpuSelection ?? s_gpuEnv;
        set => t_gpuSelection = value;
    }
    private static readonly FracturingFog.Calculators.Gpu.OdeGpuRoute.Mode s_gpuEnv =
        FracturingFog.Calculators.Gpu.OdeGpuRoute.Parse(Environment.GetEnvironmentVariable("FF_GPU_HENON_HEILES"));
    [ThreadStatic] private static FracturingFog.Calculators.Gpu.OdeGpuRoute.Mode? t_gpuSelection;

    /// <summary>#2049 — solid blocks run: opted in and a pixel's colour is its exit's alone.</summary>
    public static bool SolidBlocksActive(FractalParameters p, IColorMap? map)
        => p.HenonHeilesSolidBlocks && p.HenonHeilesChannel == HenonHeilesChannel.Exit
           && !p.HenonHeilesShadeExits && map is not IBilliardColorMap;

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
        bool shade = p.HenonHeilesShadeExits;

        int width = Width, height = Height;
        double cx = CenterX, cy = CenterY, zoom = Zoom;
        var mask = PixelMask is { } m && m.Length >= width * height ? m : null;
        LastSolidFilled = 0;
        LastGpuBackend = null;
        LastGpuMeasurement = null;

        void Store(int idx, in HenonHeilesOutcome o)
        {
            if (!o.Valid)
            {
                ExitBuffer[idx] = ForbiddenGate;
                SmoothBuffer[idx] = 0f;
                ColorBuffer[idx] = ForbiddenColor;
                return;
            }
            ExitBuffer[idx] = (sbyte)o.Exit;
            double logT = Math.Log(1.0 + o.Time);
            SmoothBuffer[idx] = (float)logT;
            ColorBuffer[idx] = Colour(channel, o, logT, logCap, scale, billiardMap, colorMap, undecided, shade);
        }
        void Pixel(int x, int y, Span<double> s)
        {
            var (a, b) = PlaneAt(x + 0.5, y + 0.5, width, height, cx, cy, zoom);
            Store(y * width + x, scene.Trace(a, b, s));
        }

        // #2049 — the GPU route.
        var fx = p.Lighting;
        var gate = FracturingFog.Calculators.Gpu.Gpu3DRoute.Gate(in fx, lowRes: false,
            _isPreviewChild ? "preview frame" : null, _isPreviewChild ? "drag-preview frames trace on the CPU" : null);
        LastGpuRoute = gate ?? default;
        if (gate is null && TryGpu(p, scene, mask, Store, Pixel, ct)) return;
        if (ct.IsCancellationRequested) return;

        if (mask == null && SolidBlocksActive(p, colorMap))
        {
            LastSolidFilled = SolidBlockFill.Run(width, height,
                (x, y) => { Span<double> s = stackalloc double[4]; Pixel(x, y, s); },
                idx => ExitBuffer[idx],
                _ => true,   // flat colours: every outcome (an exit, undecided, the wall) is one colour
                (from, to) => { ExitBuffer[to] = ExitBuffer[from]; SmoothBuffer[to] = SmoothBuffer[from]; ColorBuffer[to] = ColorBuffer[from]; },
                ct);
            return;
        }

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            if (ct.IsCancellationRequested) return;
            Span<double> s = stackalloc double[4];
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                if (mask != null && mask[row + x] == 0) continue;
                Pixel(x, y, s);
            }
        });
    }

    private delegate void StoreFn(int idx, in HenonHeilesOutcome o);
    private delegate void PixelFn(int x, int y, Span<double> s);

    /// <summary>#2049 — trace the frame on the GPU when the route allows; false = the CPU traces it.</summary>
    private bool TryGpu(FractalParameters p, HenonHeilesScene scene, byte[]? mask, StoreFn store, PixelFn pixel, CancellationToken ct)
    {
        var mode = GpuSelection;
        if (mode == FracturingFog.Calculators.Gpu.OdeGpuRoute.Mode.Cpu)
        {
            LastGpuRoute = FracturingFog.Render.GpuRoute.Cpu("CPU forced", "FF_GPU_HENON_HEILES selects the CPU trace");
            return false;
        }
        bool single = p.HenonHeilesGpuPrecision == BlackHoleGpuPrecision.Single;
        var kernel = FracturingFog.Calculators.Gpu.OdeTraceBackends.KernelFor(FracturingFog.Calculators.Gpu.OdeTraceBackends.Selection, needDouble: !single);
        if (kernel == null)
        {
            LastGpuRoute = FracturingFog.Render.GpuRoute.Cpu("no GPU backend", "no D3D11 / Vulkan ODE kernel on this machine");
            return false;
        }
        int width = Width, height = Height;
        double cx = CenterX, cy = CenterY, zoom = Zoom;
        var known = mode == FracturingFog.Calculators.Gpu.OdeGpuRoute.Mode.Auto
            ? FracturingFog.Calculators.Gpu.OdeGpuRoute.Known(kernel, HenonHeilesGpu.Family.Key, single, width * height) : null;
        if (known is { GpuFaster: false })
        {
            LastGpuMeasurement = known;
            LastGpuRoute = FracturingFog.Render.GpuRoute.Cpu("CPU faster", known.Summary);
            return false;
        }
        // The pixels to integrate: allowed launches inside the exit circle. The rest cost
        // nothing and are labelled by the CPU code (forbidden, or already outside).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var items = new System.Collections.Generic.List<int>(width * height);
        var trivial = new System.Collections.Generic.List<int>();
        Span<double> ls = stackalloc double[4];
        double r2 = HenonHeilesSystem.ExitRadius * HenonHeilesSystem.ExitRadius;
        for (int idx = 0; idx < width * height; idx++)
        {
            if (mask != null && mask[idx] == 0) continue;
            var (a, b) = PlaneAt(idx % width + 0.5, idx / width + 0.5, width, height, cx, cy, zoom);
            if (scene.LaunchState(a, b, ls) && ls[0] * ls[0] + ls[1] * ls[1] <= r2) items.Add(idx);
            else trivial.Add(idx);
        }
        var outc = HenonHeilesGpu.Trace(kernel, scene, items,
            idx => PlaneAt(idx % width + 0.5, idx / width + 0.5, width, height, cx, cy, zoom), single, ct);
        double gpuMs = sw.Elapsed.TotalMilliseconds;
        LastGpuRoute = FracturingFog.Calculators.Gpu.Gpu3DRoute.AfterRender(outc != null, kernel.LastError);
        if (outc == null) return false;
        LastGpuBackend = $"{kernel.Backend} ({kernel.DeviceName}){(single ? ", single" : "")}";
        Parallel.For(0, items.Count, i => store(items[i], outc[i]));
        Parallel.ForEach(trivial, idx =>
        {
            Span<double> s = stackalloc double[4];
            pixel(idx % width, idx / width, s);
        });
        if (known != null) LastGpuMeasurement = known;
        else if (mode == FracturingFog.Calculators.Gpu.OdeGpuRoute.Mode.Auto && mask == null)
        {
            // Auto's first GPU frame here: time the CPU on every 8th row of the same frame.
            int rows = 0;
            var cpu = System.Diagnostics.Stopwatch.StartNew();
            int stride = FracturingFog.Calculators.Gpu.OdeGpuRoute.MeasureRowStride;
            Parallel.For(0, (height + stride - 1) / stride, new ParallelOptions { CancellationToken = ct }, rr =>
            {
                Span<double> s = stackalloc double[4];
                int y = rr * stride;
                for (int x = 0; x < width; x++)
                {
                    var (a, b) = PlaneAt(x + 0.5, y + 0.5, width, height, cx, cy, zoom);
                    scene.Trace(a, b, s);
                }
                Interlocked.Increment(ref rows);
            });
            var mm = new FracturingFog.Calculators.Gpu.OdeGpuRoute.Measurement(
                cpu.Elapsed.TotalMilliseconds * 1e6 / Math.Max(1, rows * width), gpuMs * 1e6 / Math.Max(1, width * height), width * height);
            FracturingFog.Calculators.Gpu.OdeGpuRoute.Record(kernel, HenonHeilesGpu.Family.Key, single, mm);
            LastGpuMeasurement = mm;
        }
        return true;
    }

    private static uint Colour(HenonHeilesChannel channel, in HenonHeilesOutcome o, double logT, double logCap,
                               double scale, IBilliardColorMap? billiard, IColorMap map, uint undecided, bool shadeExits = true)
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
            float shade = shadeExits ? (float)(1.0 - 0.75 * Math.Clamp(logT / Math.Max(1e-9, logCap), 0.0, 1.0)) : 1f;   // #2049 flat option
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
            _preview = new HenonHeilesCalculator(dims.Width, dims.Height) { _isPreviewChild = true };
        _preview.FractalParameters = FractalParameters;
        _preview.CenterX = CenterX; _preview.CenterY = CenterY; _preview.Zoom = Zoom;
        _preview.MaxIterations = MaxIterations; _preview.ColorMap = ColorMap; _preview.Quality = Quality;
        _preview.Calculate(ct);
        FracturingFog.Rendering.LowResPreview.UpscaleNearest(_preview.ColorBuffer, dims.Width, dims.Height, ColorBuffer, Width, Height);
        return true;
    }
}
