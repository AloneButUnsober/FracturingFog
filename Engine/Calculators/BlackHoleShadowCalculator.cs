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
// (the camera's pitch, PixelPitch), in units of the hole masses (G = c = 1).
//
// #1969 — anti-aliasing: the quality preset's sub-pixel samples (Supersampling.AccumulateAlt)
// jitter the centre and re-trace; with Adaptive anti-aliasing on, only pixels that differ from
// a neighbour (shadow and eyebrow edges, the lensed sky) take the extra samples, through
// PixelMask. A marked pixel is traced from its own coordinate alone, so it gets exactly a full
// frame's colour, except where a pass reads neighbours (solid blocks, the double-double edge
// fix-up): MaskExact is false there and AA falls back to the full grid.
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

public sealed class BlackHoleShadowCalculator : IFractalCalculator, IHeightFieldSource, FracturingFog.Rendering.ILowResPreviewCalculator, FracturingFog.Render.IGpuRouteSource,
    FracturingFog.Rendering.IAdaptiveAaCalculator
{
    /// <summary>#1969 — adaptive anti-aliasing: with a mask (Width x Height bytes) the CPU trace
    /// computes only the marked pixels; the others keep their buffer contents. The GPU route and
    /// solid blocks compute every pixel (still exact).</summary>
    public byte[]? PixelMask { get; set; }
    public FracturingFog.Rendering.AdaptiveAa AdaptiveAa => FracturingFog.Rendering.AdaptiveAa.From(FractalParameters);

    /// <summary>#1969 — false while a pass reads neighbouring pixels: solid blocks (a block's fill
    /// depends on its ring) and the double-double edge fix-up past double precision (it retraces
    /// the fate edges of the whole frame).</summary>
    public bool MaskExact
    {
        get
        {
            var p = FractalParameters;
            bool fate = p.BlackHoleChannel == BlackHoleChannel.Fate;
            if (p.BlackHoleSolidBlocks && fate) return false;
            return !(p.BlackHoleDdEdges && fate && p.BlackHoleLayout != BlackHoleLayout.UserLens
                     && Scene.PastDoublePrecision(PixelPitch(Width, Height, Zoom)));
        }
    }

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

    /// <summary>#1927 — status-bar note: the tolerance in use, or that the view is past what
    /// double precision can resolve.</summary>
    public string? LastPrecisionLabel { get; private set; }

    public bool LowResPreview { get; set; }
    public double? LowResPreviewScaleOverride { get; set; }

    // ── GPU (#1926) ──────────────────────────────────────────────────────────
    /// <summary>#1972 — the GPU backend that traced the last frame ("ILGPU", "D3D11 (device)",
    /// "Vulkan (device)"), or null when it was traced on the CPU.</summary>
    public string? LastGpuBackend { get; private set; }

    /// <summary>#1926 — how the last frame was traced (GPU, or CPU and why).</summary>
    public FracturingFog.Render.GpuRoute LastGpuRoute { get; private set; }

    /// <summary>#1926 — where the trace runs when GPU render is on (Lighting &amp; FX).</summary>
    public enum GpuBlackHoleMode
    {
        /// <summary>Measure on the first GPU frame (per device), keep the faster, re-measure
        /// when the pixel count moves by more than <see cref="RemeasureFactor"/>.</summary>
        Auto,
        /// <summary>Always the GPU (<c>FF_GPU_BLACK_HOLE=1</c>).</summary>
        Gpu,
        /// <summary>Always the CPU (<c>FF_GPU_BLACK_HOLE=0</c>).</summary>
        Cpu,
    }

    /// <summary>#1926 — the GPU and CPU traces are the same arithmetic, so the path is a speed
    /// choice and the answer depends on the hardware (the Buddhabrot #1220 / mirror spheres
    /// #1443 pattern). Auto (default) measures; FF_GPU_BLACK_HOLE forces either path. Setting
    /// it overrides for the calling thread only (tests).</summary>
    public static GpuBlackHoleMode GpuSelection
    {
        get => t_gpuSelection ?? s_gpuSelectionEnv;
        set => t_gpuSelection = value;
    }
    private static readonly GpuBlackHoleMode s_gpuSelectionEnv =
        BuddhaFamilyCalculator.ParseGpuBuddhaMode(Environment.GetEnvironmentVariable("FF_GPU_BLACK_HOLE")) switch
        {
            BuddhaFamilyCalculator.GpuBuddhaMode.Gpu => GpuBlackHoleMode.Gpu,
            BuddhaFamilyCalculator.GpuBuddhaMode.Cpu => GpuBlackHoleMode.Cpu,
            _ => GpuBlackHoleMode.Auto,
        };
    [ThreadStatic] private static GpuBlackHoleMode? t_gpuSelection;

    /// <summary>#1926 — Auto re-measures when the pixel count differs from the measured frame by more than this factor.</summary>
    public const double RemeasureFactor = 4.0;
    /// <summary>#1926 — Auto times the CPU on every this-many-th row of the first GPU frame.</summary>
    public const int MeasureRowStride = 8;

    /// <summary>#1926 — one measured comparison: trace cost per pixel on each path.</summary>
    public sealed record GpuMeasurement(double CpuNsPerPixel, double GpuNsPerPixel, int Pixels)
    {
        public bool GpuFaster => GpuNsPerPixel < CpuNsPerPixel;
        public bool Covers(int pixels) => pixels <= Pixels * RemeasureFactor && Pixels <= pixels * RemeasureFactor;
        public string Summary => $"measured CPU {CpuNsPerPixel:0.#} vs GPU {GpuNsPerPixel:0.#} ns/pixel";
    }
    // Auto decisions per accelerator (freed with it), keyed by single precision.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, System.Collections.Generic.Dictionary<bool, GpuMeasurement>> s_gpuDecisions = new();
    /// <summary>#1926 — the measurement behind the last frame's path (Auto), or null.</summary>
    public GpuMeasurement? LastGpuMeasurement { get; private set; }
    private FracturingFog.Calculators.Gpu.BlackHoleShadowGpuCalculator? _gpu;
    private bool _isPreviewChild;   // the drag preview's smaller sibling stays on the CPU

    /// <summary>#1926 — why a frame with GPU render on still traces on the CPU (null = it may use the GPU).</summary>
    public static string? CpuOnlyReason(FractalParameters p)
    {
        if (p.BlackHoleLayout == BlackHoleLayout.UserLens) return "a user lens (#1995) runs on the CPU";
        if (p.BlackHoleShadeFates && p.BlackHoleChannel != BlackHoleChannel.Fate) return "shaded fates need the CPU channels";
        return p.BlackHoleChannel is BlackHoleChannel.Winding or BlackHoleChannel.ClosestApproach
            ? $"{p.BlackHoleChannel} needs per-step bookkeeping (CPU only)"
            : null;
    }

    private BlackHoleShadowCalculator? _preview;

    /// <summary>Default zoom: the frame's longer side is 3.5 / 0.14 = 25 mass units, which fits
    /// the default binary (unit masses, separation 2) with its eyebrows. (#1927: was 0.16 with the
    /// old 4 / width pitch, the same framing on a landscape view.)</summary>
    public const double DefaultZoom = 0.14;

    /// <summary>Palette scale of log(1 + delay) for escaped rays.</summary>
    public const float DelayPaletteScale = 24f;

    /// <summary>Shadow brightness of a hole's Okabe-Ito colour (the built-in colouring).</summary>
    public const float ShadowShade = 0.55f;

    /// <summary>#1995 — the frame colour when the user lens does not parse (yellow, colour-blind safe).</summary>
    public const uint LensErrorColor = 0xFFFFCC00;

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
        /// <summary>#1995 — the user lens (layout UserLens), else null.</summary>
        public UserLensModel? Lens { get; init; }
        /// <summary>#1995 — the lens's capture threshold on U (0: off).</summary>
        public double LensCapture { get; init; }
        public MpNullSystem NewSystem() => new(Holes, CaptureFraction, EscapeRadius, Lens, LensCapture);

        /// <summary>Trace the ray through image-plane point (u, v); the final state is written
        /// to <paramref name="state"/> (length ≥ 6) when given.</summary>
        /// <summary>#1996 — the ray through (u, v) with every state recorded (the launch, then each
        /// accepted step), channels on, for the Math Plane 3D geodesic statement.</summary>
        public (ScatterOutcome Outcome, MpNullSystem System) Trace<TRec>(double u, double v, ref TRec rec)
            where TRec : FracturingFog.Scattering.IPathRecorder
        {
            var sys = NewSystem();
            Span<double> s = stackalloc double[6];
            Camera.Launch(sys, u, v, s);
            sys.Begin(s, true);
            rec.Record(s);
            var wrapped = new FracturingFog.Scattering.RecordingSystem<MpNullSystem, TRec>(sys, rec);
            var o = ScatteringIntegrator.Trace(ref wrapped, s, Settings);
            rec = wrapped.Recorder;
            return (o, wrapped.Inner);
        }

        public (ScatterOutcome Outcome, MpNullSystem System) Trace(double u, double v, Span<double> state = default, bool channels = true)
        {
            var sys = NewSystem();
            Span<double> s = state.Length >= 6 ? state : stackalloc double[6];
            Camera.Launch(sys, u, v, s);
            sys.Begin(s, channels);
            var o = ScatteringIntegrator.Trace(ref sys, s, Settings);
            return (o, sys);
        }

        /// <summary>#1924 — the hole a ray's winding and closest-approach channels refer to:
        /// the capturing hole, otherwise the hole it came closest to (in units of its mass).</summary>
        public int ReferenceHole(in ScatterOutcome o, in MpNullSystem sys)
        {
            if (o.Fate == ScatterFate.Captured) return o.Body;
            int best = 0;
            double q = double.PositiveInfinity;
            for (int i = 0; i < Holes.Length; i++)
            {
                double m = Math.Max(1e-12, Holes[i].M);
                double qi = sys.Min[i] / m;
                if (qi < q) { q = qi; best = i; }
            }
            return best;
        }

        /// <summary>#1924 — deflection angle (radians) of a ray whose final state is
        /// <paramref name="s"/>: the angle between the camera's view direction (the launch
        /// direction) and the final momentum. Meaningful for escaped rays.</summary>
        public double Deflection(ReadOnlySpan<double> s, in MpNullSystem sys)
        {
            double pm = Math.Sqrt(s[3] * s[3] + s[4] * s[4] + s[5] * s[5]);
            if (!(pm > 0)) return double.NaN;
            // #1925 — against this ray's own launch direction (varies per pixel for the pinhole).
            double c = (s[3] * sys.LaunchX + s[4] * sys.LaunchY + s[5] * sys.LaunchZ) / pm;
            return Math.Acos(Math.Clamp(c, -1.0, 1.0));
        }

        /// <summary>#1925 — the frame's sky (Off: escaped rays use the palette).</summary>
        public BlackHoleSkies.Settings Sky { get; init; }

        /// <summary>#1925 — sky colour seen by an escaped ray with final state <paramref name="s"/>.</summary>
        public uint SkyColour(ReadOnlySpan<double> s) => BlackHoleSkies.Sample(Sky, s[3], s[4], s[5]);

        /// <summary>#1924 — the raw value of <paramref name="channel"/> for one traced ray
        /// (NaN where the channel has no value: deflection of a captured ray).</summary>
        public double ChannelValue(BlackHoleChannel channel, in ScatterOutcome o, in MpNullSystem sys, ReadOnlySpan<double> s)
        {
            switch (channel)
            {
                case BlackHoleChannel.Winding:
                    return sys.Wind[ReferenceHole(o, sys)] / (2.0 * Math.PI);
                case BlackHoleChannel.Delay:
                    return Math.Log(1.0 + Math.Max(0.0, sys.Delay));
                case BlackHoleChannel.Steps:
                    return Math.Log(1.0 + o.Steps);
                case BlackHoleChannel.ClosestApproach:
                {
                    double q = double.PositiveInfinity;
                    for (int i = 0; i < Holes.Length; i++) q = Math.Min(q, sys.Min[i] / Math.Max(1e-12, Holes[i].M));
                    return Math.Log(1.0 + 1.0 / Math.Max(1e-9, q));
                }
                case BlackHoleChannel.Deflection:
                    return o.Fate == ScatterFate.Escaped ? Deflection(s, sys) : double.NaN;
                default:
                    return o.Fate == ScatterFate.Escaped ? Math.Log(1.0 + Math.Max(0.0, sys.Delay)) : 0.0;
            }
        }

        /// <summary>#1970 — the tolerance actually used at <paramref name="zoom"/>: the parameter at
        /// the default zoom or wider, tightened by the cube root of the zoom past it. Measured on the
        /// default binary against a 1e-11 reference (480x270): a fixed 3e-8 changed no fate pixel at
        /// eyebrow zooms x6 to x600 while 1e-7 changed 2-4, so the cube root (3e-8 at x6, 6.4e-9 at
        /// x600) stays tighter than needed there without the cost of a proportional tie.
        /// #1927 (S5) — and never looser than <see cref="PitchToleranceFactor"/> x the pixel pitch
        /// (<paramref name="pixelPitch"/>, plane units per pixel; NaN = not known). Calibrated at
        /// three bisected boundary points on the eyebrow chain, 120x68, zoom x10 to x10^9 past the
        /// default, against a 1e-13 reference: the loosest tolerance that moved no fate pixel tracked
        /// the pitch (x10^6: 1e-8, x10^7: 1e-10, x10^9: 1e-12 at pitch 2e-10). 0.005 x pitch just
        /// covered every case (x10^9 sat on the edge), so the factor is 0.003 for margin. Floored at
        /// <see cref="MinTolerance"/>, below which double cannot step: past about x10^10 the 1e-12
        /// and 1e-13 references themselves disagree.</summary>
        public static double EffectiveTolerance(double tolerance, double zoom, double pixelPitch = double.NaN)
        {
            double t = Math.Clamp(double.IsFinite(tolerance) ? tolerance : 1e-7, MinTolerance, 1e-4);
            if (double.IsFinite(zoom) && zoom > DefaultZoom) t *= Math.Cbrt(DefaultZoom / zoom);
            if (double.IsFinite(pixelPitch) && pixelPitch > 0) t = Math.Min(t, PitchToleranceFactor * pixelPitch);
            return Math.Max(t, MinTolerance);
        }

        /// <summary>#1927 — tolerance per plane unit of pixel pitch (see <see cref="EffectiveTolerance"/>).</summary>
        public const double PitchToleranceFactor = 0.003;
        /// <summary>#1927 — the tightest tolerance double precision can integrate to.</summary>
        public const double MinTolerance = 1e-13;

        /// <summary>#1927 — true when the view needs a tighter tolerance than double can give
        /// (<see cref="PitchToleranceFactor"/> x pitch below <see cref="MinTolerance"/>): fate edges
        /// are then approximate (about x10^10 past the default zoom at 120 px wide).</summary>
        public static bool PastDoublePrecision(double pixelPitch)
            => double.IsFinite(pixelPitch) && pixelPitch > 0 && PitchToleranceFactor * pixelPitch < MinTolerance;

        public static Scene Build(FractalParameters p, double zoom = DefaultZoom, double pixelPitch = double.NaN)
        {
            var holes = BlackHoleLayouts.Build(p);
            var lens = p.BlackHoleLayout == BlackHoleLayout.UserLens ? UserLensModel.For(p.BlackHoleLensSource) : null;
            double extent = lens != null ? 2.0 : 0.0;   // a lens with no listed holes still gets a 10+ camera
            foreach (var h in holes)
                extent = Math.Max(extent, Math.Sqrt(h.X * h.X + h.Y * h.Y + h.Z * h.Z) + 4.0 * h.M);
            double d = Math.Max(Math.Max(10.0, 4.0 * extent), double.IsFinite(p.BlackHoleCameraDistance) ? p.BlackHoleCameraDistance : 50.0);
            var cam = new MpCamera(d, p.BlackHoleInclination, p.BlackHoleAzimuth, p.BlackHoleProjection == BlackHoleProjection.Pinhole);
            double tol = EffectiveTolerance(p.BlackHoleTolerance, zoom, pixelPitch);
            return new Scene
            {
                Holes = holes,
                Lens = lens,
                LensCapture = p.BlackHoleLensCapture,
                Camera = cam,
                Sky = new BlackHoleSkies.Settings
                {
                    Kind = p.BlackHoleSky,
                    Divisions = Math.Clamp(p.BlackHoleSkyDivisions, 1, 512),
                    Seed = p.BlackHoleSkySeed,
                    RotationRad = (double.IsFinite(p.BlackHoleSkyRotation) ? p.BlackHoleSkyRotation : 0.0) * Math.PI / 180.0,
                    Lighting = p.Lighting,
                },
                CaptureFraction = Math.Clamp(double.IsFinite(p.BlackHoleCaptureFraction) ? p.BlackHoleCaptureFraction : 0.25, 1e-5, 0.5),
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

    /// <summary>#1927 — plane units per pixel: <see cref="FracturingFog.ViewState.ViewCamera"/>'s scale,
    /// PlaneExtent / (max(width, height) · zoom), so box zoom, wheel zoom and pan land where the
    /// render put the pixels. (S1 used the billiard's 4 / (width · zoom), which put every zoom
    /// target at 3.5/4 of its distance from the centre: right near the middle, an eyebrow-width
    /// or more off at the shadow edges.)</summary>
    public static double PixelPitch(double width, double height, double zoom)
        => FracturingFog.ViewState.ViewCamera.PlaneExtent / (Math.Max(1.0, Math.Max(width, height)) * Math.Max(1e-12, zoom));

    /// <summary>Image-plane point of pixel (x, y): the camera's screen-to-world mapping, with v up.</summary>
    public static (double U, double V) PlaneAt(double x, double y, double width, double height,
                                               double centerX, double centerY, double zoom)
    {
        double pitch = PixelPitch(width, height, zoom);
        return (centerX + (x - width * 0.5) * pitch, -(centerY + (y - height * 0.5) * pitch));
    }

    public void Calculate(CancellationToken ct = default)
    {
        if (LowResPreview && RenderPreview(ct)) return;
        LastDdPixels = 0;
        CalculateMain(ct);
        if (ct.IsCancellationRequested) return;
        // #1927 part 2 — past double precision, retrace the fate edges in double-double.
        var p = FractalParameters;
        double pitch = PixelPitch(Width, Height, Zoom);
        // (Not for a user lens: the double-double trace is the hole sum's.)
        if (!_isPreviewChild && p.BlackHoleDdEdges && p.BlackHoleChannel == BlackHoleChannel.Fate
            && p.BlackHoleLayout != BlackHoleLayout.UserLens && Scene.PastDoublePrecision(pitch))
        {
            DdEdgeFixup(Scene.Build(p, Zoom, pitch), pitch, ct);
            LastPrecisionLabel = $"DD edges: {LastDdPixels} px";
        }
    }

    // ── Double-double edge fix-up (#1927 part 2) ─────────────────────────────

    /// <summary>#1927 — pixels the last frame retraced in double-double (0 when not past fp64).</summary>
    public long LastDdPixels { get; private set; }

    /// <summary>#1927 — the DD trace's tolerance at pixel pitch <paramref name="pixelPitch"/>: the
    /// calibrated <see cref="Scene.PitchToleranceFactor"/> x pitch, without double's 1e-13 floor
    /// (floored at <see cref="DdMinTolerance"/>, near DD's own resolution).</summary>
    public static double DdTolerance(double pixelPitch)
        => Math.Max(Scene.PitchToleranceFactor * pixelPitch, DdMinTolerance);
    public const double DdMinTolerance = 1e-28;

    /// <summary>#1927 — fix-up rounds cap (each round follows changed fates one pixel further).</summary>
    public const int DdMaxRounds = 256;

    private void DdEdgeFixup(Scene scene, double pitch, CancellationToken ct)
    {
        int w = Width, h = Height, total = w * h;
        if (total == 0) return;
        var fate = FateBuffer;
        int escapedGate = scene.EscapedGate, gateCount = scene.Holes.Length + 1;
        var billiardMap = ColorMap as IBilliardColorMap;
        uint undecided = ColorMap.InSetColor;
        bool sky = FractalParameters.BlackHoleSky != BlackHoleSky.Off;
        double tol = DdTolerance(pitch);
        int maxSteps = Math.Max(scene.Settings.MaxSteps, 1000);
        double cx = CenterX, cy = CenterY;

        bool Edge(int i)
        {
            int x = i % w, y = i / w; sbyte f = fate[i];
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((dx | dy) == 0 || nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    if (fate[ny * w + nx] != f) return true;
                }
            return false;
        }
        var done = new bool[total];
        var queue = new System.Collections.Generic.List<int>();
        var mark = new bool[total];
        void Push(int i) { if (!mark[i] && !done[i]) { mark[i] = true; queue.Add(i); } }
        void PushNeighbours(int i)
        {
            int x = i % w, y = i / w;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx >= 0 && ny >= 0 && nx < w && ny < h) Push(ny * w + nx);
                }
        }
        for (int i = 0; i < total; i++) if (Edge(i)) PushNeighbours(i);   // the edge band, one pixel of margin

        long traced = 0;
        // One final-state scratch for every round: a stackalloc inside the round loop would grow the
        // stack by 48 bytes per round until the method returns.
        Span<double> fin = stackalloc double[6];
        for (int round = 0; round < DdMaxRounds && queue.Count > 0 && !ct.IsCancellationRequested; round++)
        {
            var batch = queue.ToArray();
            queue.Clear();
            var newFate = new sbyte[batch.Length];
            var outs = new MpDdOutcome[batch.Length];
            System.Threading.Tasks.Parallel.For(0, batch.Length, new System.Threading.Tasks.ParallelOptions { CancellationToken = ct }, k =>
            {
                int i = batch[k];
                int x = i % w, y = i / w;
                double pitchV = PixelPitch(w, h, Zoom);
                var u = FracturingFog.FFMath.DD.FromCenterOffset(cx, x + 0.5 - w * 0.5, pitchV);
                var v = -FracturingFog.FFMath.DD.FromCenterOffset(cy, y + 0.5 - h * 0.5, pitchV);
                var o = MajumdarPapapetrouDD.Trace(scene, u, v, tol, maxSteps);
                outs[k] = o;
                newFate[k] = (sbyte)(o.Fate == ScatterFate.Captured ? o.Body : o.Fate == ScatterFate.Escaped ? escapedGate : -1);
            });
            traced += batch.Length;
            for (int k = 0; k < batch.Length; k++)
            {
                int i = batch[k];
                done[i] = true; mark[i] = false;
                bool changed = newFate[k] != fate[i];
                fate[i] = newFate[k];
                int gate = newFate[k];
                var o = outs[k];
                float smooth = gate == escapedGate ? (float)Math.Log(1.0 + Math.Max(0.0, o.Delay)) : 0f;
                SmoothBuffer[i] = smooth;
                fin[3] = o.Px; fin[4] = o.Py; fin[5] = o.Pz;
                ColorBuffer[i] = sky && gate == escapedGate
                    ? scene.SkyColour(fin) | 0xFF000000u
                    : Colour(gate, escapedGate, gateCount, smooth, billiardMap, ColorMap, undecided);
                if (changed) PushNeighbours(i);   // the edge moved: follow it
            }
        }
        LastDdPixels = traced;
    }

    private void CalculateMain(CancellationToken ct)
    {
        var p = FractalParameters;
        double framePitch = PixelPitch(Width, Height, Zoom);
        var scene = Scene.Build(p, Zoom, framePitch);
        LastPrecisionLabel = Scene.PastDoublePrecision(framePitch)
            ? "past fp64: edges approximate"
            : $"fp64 tol {scene.Settings.RelTol:0.#E+0}";
        if (scene.Lens?.Error is { } lensError)
        {
            // #1995 — a U that does not parse: the frame in the error colour, the message in the status bar.
            LastPrecisionLabel = lensError;
            Array.Fill(ColorBuffer, LensErrorColor);
            Array.Fill(SmoothBuffer, 0f);
            Array.Fill(FateBuffer, (sbyte)-1);
            return;
        }
        int n = scene.Holes.Length, escapedGate = scene.EscapedGate, gateCount = n + 1;
        var billiardMap = ColorMap as IBilliardColorMap;
        ColorMap.MaxIterations = MaxIterations;
        uint undecided = ColorMap.InSetColor;
        var channel = p.BlackHoleChannel;
        bool shadeFates = p.BlackHoleShadeFates && channel != BlackHoleChannel.Fate;
        bool sky = p.BlackHoleSky != BlackHoleSky.Off;
        bool needChannels = channel != BlackHoleChannel.Fate;   // #1970 — Fate reads only the delay
        double scale = ChannelPaletteScale * (double.IsFinite(p.BlackHoleChannelScale) ? Math.Clamp(p.BlackHoleChannelScale, 0.01, 100.0) : 1.0);

        int width = Width, height = Height;
        double cx = CenterX, cy = CenterY, zoom = Zoom;
        var colorMap = ColorMap;

        // #1926 — the GPU traces every ray (fate, steps, delay, final momentum) when GPU render is
        // on and the colouring needs nothing else; the colour pass below reads its outcomes.
        FracturingFog.Calculators.Gpu.BlackHoleShadowGpuCalculator.Outcomes? gpuOut = null;
        LastGpuMeasurement = null;
        LastGpuBackend = null;   // #1972
        var fx = p.Lighting;
        string? family = _isPreviewChild ? "preview frame" : CpuOnlyReason(p);
        var gpuGate = FracturingFog.Calculators.Gpu.Gpu3DRoute.Gate(in fx, lowRes: false, family,
            family == null ? null : _isPreviewChild ? "drag-preview frames trace on the CPU" : family);
        LastGpuRoute = gpuGate ?? default;
        bool measureCpu = false, measureSingle = false;
        double gpuMs = 0;
        object? device = null;
        if (gpuGate is null)
        {
            var mode = GpuSelection;
            // #1972 — the backend: ILGPU when it is there (auto) or chosen; else the D3D11 / Vulkan twin
            // (FF_GPU_BLACK_HOLE_BACKEND). Each backend is its own "device" for Auto's measurement.
            var choice = FracturingFog.Calculators.Gpu.BlackHoleTraceBackends.Selection;
            bool ilgpu = choice is FracturingFog.Calculators.Gpu.BlackHoleTraceBackends.Choice.Auto
                                or FracturingFog.Calculators.Gpu.BlackHoleTraceBackends.Choice.Ilgpu
                         && FracturingFog.Calculators.Gpu.GpuAcceleratorHost.TryAcquire(out var acc0) && (device = acc0) != null;
            var alt = ilgpu ? null : FracturingFog.Calculators.Gpu.BlackHoleTraceBackends.KernelFor(choice, ilgpuAvailable: false);
            if (alt != null) device = alt;
            bool single = p.BlackHoleGpuPrecision == BlackHoleGpuPrecision.Single;
            GpuMeasurement? known = null;
            if (mode == GpuBlackHoleMode.Auto && device != null && s_gpuDecisions.TryGetValue(device, out var kd))
                lock (kd) { if (kd.TryGetValue(single, out var k) && k.Covers(width * height)) known = k; }
            if (mode == GpuBlackHoleMode.Cpu)
            {
                LastGpuRoute = FracturingFog.Render.GpuRoute.Cpu("CPU forced", "FF_GPU_BLACK_HOLE selects the CPU trace");
            }
            else if (known is { GpuFaster: false })
            {
                LastGpuMeasurement = known;
                LastGpuRoute = FracturingFog.Render.GpuRoute.Cpu("CPU faster", known.Summary);
            }
            else
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var gp = FracturingFog.Calculators.Gpu.BlackHoleShadowGpuCalculator.ParamsFor(scene, width, height, cx, cy, zoom);
                if (alt != null)
                {
                    gpuOut = alt.Trace(gp, single, ct);   // #1972 — D3D11 / Vulkan
                    gpuMs = sw.Elapsed.TotalMilliseconds;
                    LastGpuRoute = FracturingFog.Calculators.Gpu.Gpu3DRoute.AfterRender(gpuOut != null, alt.LastError);
                    LastGpuBackend = gpuOut != null ? $"{alt.Backend} ({alt.DeviceName})" : null;
                }
                else
                {
                    _gpu ??= new FracturingFog.Calculators.Gpu.BlackHoleShadowGpuCalculator();
                    gpuOut = _gpu.Render(gp, ct, single);
                    gpuMs = sw.Elapsed.TotalMilliseconds;
                    LastGpuRoute = FracturingFog.Calculators.Gpu.Gpu3DRoute.AfterRender(gpuOut != null, _gpu.LastError);
                    LastGpuBackend = gpuOut != null ? "ILGPU" : null;
                }
                if (LastGpuBackend != null) LastPrecisionLabel += ", GPU: " + LastGpuBackend;   // #1972
                if (ct.IsCancellationRequested) return;
                measureCpu = gpuOut != null && mode == GpuBlackHoleMode.Auto && known == null && device != null;
                measureSingle = single;
                if (known != null) LastGpuMeasurement = known;
            }
        }

        // #1926 — one pixel from the GPU outcomes: the same colours the CPU path makes.
        void GpuPixel(int x, int y)
        {
            int idx = y * width + x;
            var g = gpuOut!;
            int gate = g.Gate(idx);
            FateBuffer[idx] = (sbyte)gate;
            double delay = g.Delay(idx);
            Span<double> fin = stackalloc double[6];
            fin[3] = g.Px(idx); fin[4] = g.Py(idx); fin[5] = g.Pz(idx);
            if (channel == BlackHoleChannel.Fate)
            {
                float smooth = gate == escapedGate ? (float)Math.Log(1.0 + Math.Max(0.0, delay)) : 0f;
                SmoothBuffer[idx] = smooth;
                ColorBuffer[idx] = sky && gate == escapedGate
                    ? scene.SkyColour(fin) | 0xFF000000u
                    : Colour(gate, escapedGate, gateCount, smooth, billiardMap, colorMap, undecided);
                return;
            }
            double value = channel switch
            {
                BlackHoleChannel.Delay => Math.Log(1.0 + Math.Max(0.0, delay)),
                BlackHoleChannel.Steps => Math.Log(1.0 + g.Steps(idx)),
                BlackHoleChannel.Deflection => gate == escapedGate ? DeflectionFrom(scene, x, y, fin) : double.NaN,
                _ => double.NaN,
            };
            SmoothBuffer[idx] = double.IsFinite(value) ? (float)value : 0f;
            ColorBuffer[idx] = ChannelColour(gate, value * scale, colorMap, undecided);
        }
        double DeflectionFrom(Scene sc, int x, int y, ReadOnlySpan<double> fin)
        {
            var (u, v) = PlaneAt(x + 0.5, y + 0.5, width, height, cx, cy, zoom);
            var (lx, ly, lz) = sc.Camera.Direction(u, v);
            double pm = Math.Sqrt(fin[3] * fin[3] + fin[4] * fin[4] + fin[5] * fin[5]);
            if (!(pm > 0)) return double.NaN;
            return Math.Acos(Math.Clamp((fin[3] * lx + fin[4] * ly + fin[5] * lz) / pm, -1.0, 1.0));
        }

        if (gpuOut != null)
        {
            Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
            {
                if (ct.IsCancellationRequested) return;
                for (int x = 0; x < width; x++) GpuPixel(x, y);
            });
            LastSolidFilled = 0;
            if (measureCpu)
            {
                // Auto's first GPU frame: time the CPU trace of every 8th row of the same frame,
                // per pixel, against the GPU frame (trace + readback).
                int rows = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Parallel.For(0, (height + MeasureRowStride - 1) / MeasureRowStride, new ParallelOptions { CancellationToken = ct }, rr =>
                {
                    Span<double> s = stackalloc double[6];
                    int y = rr * MeasureRowStride;
                    for (int x = 0; x < width; x++)
                    {
                        var (u, v) = PlaneAt(x + 0.5, y + 0.5, width, height, cx, cy, zoom);
                        scene.Trace(u, v, s, needChannels);
                    }
                    Interlocked.Increment(ref rows);
                });
                double cpuNs = sw.Elapsed.TotalMilliseconds * 1e6 / Math.Max(1, rows * width);
                var m = new GpuMeasurement(cpuNs, gpuMs * 1e6 / Math.Max(1, width * height), width * height);
                var dict = s_gpuDecisions.GetOrCreateValue(device!);
                lock (dict) dict[measureSingle] = m;
                LastGpuMeasurement = m;
            }
            return;
        }

        // One pixel: trace, record the fate, colour it.
        void Pixel(int x, int y, Span<double> s)
        {
            var (u, v) = PlaneAt(x + 0.5, y + 0.5, width, height, cx, cy, zoom);
            var (o, sys) = scene.Trace(u, v, s, needChannels);
            int idx = y * width + x;
            int gate = o.Fate switch
            {
                ScatterFate.Captured => o.Body,
                ScatterFate.Escaped => escapedGate,
                _ => -1,
            };
            FateBuffer[idx] = (sbyte)gate;
            if (channel == BlackHoleChannel.Fate)
            {
                float smooth = o.Fate == ScatterFate.Escaped ? (float)Math.Log(1.0 + Math.Max(0.0, sys.Delay)) : 0f;
                SmoothBuffer[idx] = smooth;
                // #1925 — a sky replaces the palette on escaped rays (the theme still colours
                // the shadows, or the fates when it is a billiard theme).
                ColorBuffer[idx] = sky && gate == escapedGate
                    ? scene.SkyColour(s) | 0xFF000000u
                    : Colour(gate, escapedGate, gateCount, smooth, billiardMap, colorMap, undecided);
                return;
            }
            double value = scene.ChannelValue(channel, o, sys, s);
            SmoothBuffer[idx] = double.IsFinite(value) ? (float)value : 0f;
            ColorBuffer[idx] = shadeFates
                ? ShadedFate(gate, gateCount, value * scale, sys.Wind[scene.ReferenceHole(o, sys)], billiardMap, undecided)
                : ChannelColour(gate, value * scale, colorMap, undecided);
        }

        // #1970 — solid blocks (opt-in, Fate colouring only): a block whose outer ring is all
        // captured by one hole is filled with that hole's colour without tracing its inside. A
        // captured pixel's Fate colour is constant (smooth 0), so a correct fill is exact; the
        // risk is an island of another fate wholly inside the block, which the ring cannot see.
        if (p.BlackHoleSolidBlocks && channel == BlackHoleChannel.Fate)
        {
            int bw = (width + SolidBlock - 1) / SolidBlock, bh = (height + SolidBlock - 1) / SolidBlock;
            Parallel.For(0, bw * bh, new ParallelOptions { CancellationToken = ct }, b =>
            {
                if (ct.IsCancellationRequested) return;
                Span<double> s = stackalloc double[6];
                int x0 = (b % bw) * SolidBlock, y0 = (b / bw) * SolidBlock;
                int x1 = Math.Min(width, x0 + SolidBlock), y1 = Math.Min(height, y0 + SolidBlock);
                if (x1 - x0 < 3 || y1 - y0 < 3)
                {
                    for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) Pixel(x, y, s);
                    return;
                }
                for (int x = x0; x < x1; x++) { Pixel(x, y0, s); Pixel(x, y1 - 1, s); }
                for (int y = y0 + 1; y < y1 - 1; y++) { Pixel(x0, y, s); Pixel(x1 - 1, y, s); }
                int g0 = FateBuffer[y0 * width + x0];
                bool uniform = g0 >= 0 && g0 < escapedGate;
                for (int x = x0; x < x1 && uniform; x++)
                    uniform = FateBuffer[y0 * width + x] == g0 && FateBuffer[(y1 - 1) * width + x] == g0;
                for (int y = y0 + 1; y < y1 - 1 && uniform; y++)
                    uniform = FateBuffer[y * width + x0] == g0 && FateBuffer[y * width + x1 - 1] == g0;
                if (uniform)
                {
                    uint col = ColorBuffer[y0 * width + x0];
                    for (int y = y0 + 1; y < y1 - 1; y++)
                    {
                        int row = y * width;
                        for (int x = x0 + 1; x < x1 - 1; x++)
                        {
                            FateBuffer[row + x] = (sbyte)g0; SmoothBuffer[row + x] = 0f; ColorBuffer[row + x] = col;
                        }
                    }
                    Interlocked.Add(ref _solidFilled, (long)(x1 - x0 - 2) * (y1 - y0 - 2));
                }
                else
                {
                    for (int y = y0 + 1; y < y1 - 1; y++) for (int x = x0 + 1; x < x1 - 1; x++) Pixel(x, y, s);
                }
            });
            LastSolidFilled = _solidFilled;
            _solidFilled = 0;
            return;
        }

        byte[]? aaMask = PixelMask is { } pm && pm.Length == width * height ? pm : null;   // #1969
        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            if (ct.IsCancellationRequested) return;
            Span<double> s = stackalloc double[6];
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                if (aaMask != null && aaMask[row + x] == 0) continue;
                Pixel(x, y, s);
            }
        });
        LastSolidFilled = 0;
    }

    /// <summary>#1970 — solid-block size in pixels (the ring is traced, the inside filled).</summary>
    public const int SolidBlock = 16;
    private long _solidFilled;
    /// <summary>#1970 — pixels the last frame filled from solid blocks instead of tracing.</summary>
    public long LastSolidFilled { get; private set; }

    /// <summary>#1924 — palette units per unit of a channel's raw value (times BlackHoleChannelScale).</summary>
    public const double ChannelPaletteScale = 24.0;

    // #1924 — a channel through the active palette. Undecided rays take the interior colour;
    // a ray with no value (deflection of a captured ray) keeps its hole's shadow colour.
    private static uint ChannelColour(int gate, double v, IColorMap map, uint undecided)
    {
        if (gate < 0) return undecided | 0xFF000000u;
        if (!double.IsFinite(v))
            return unchecked((uint)BilliardColorHelper.Shade(BilliardColorHelper.GateColor(gate), ShadowShade)) | 0xFF000000u;
        return PaletteColour(map, v);
    }

    /// <summary>#1924 — <paramref name="v"/> palette units through <paramref name="map"/>. The third
    /// Map argument is the iteration cap (a value at or past it is "in set" for escape-time
    /// palettes; S1 passed the value itself, so every palette drew the interior colour), so values
    /// wrap below <see cref="IColorMap.MaxIterations"/> instead.</summary>
    public static uint PaletteColour(IColorMap map, double v)
    {
        int max = Math.Max(2, map.MaxIterations);
        double w = Math.Max(0.0, v) % (max - 1);
        return unchecked((uint)map.Map((float)w, 0f, max)) | 0xFF000000u;
    }

    // #1924 — "hue = basin, brightness = channel": the fate colour with a triangle-wave
    // brightness in the channel (one cycle per ChannelPaletteScale palette units), so stripes
    // follow the channel's level sets. A billiard theme gets the half-turns as its bounce count.
    private static uint ShadedFate(int gate, int gateCount, double v, double windRadians,
                                   IBilliardColorMap? billiard, uint undecided)
    {
        if (gate < 0) return undecided | 0xFF000000u;
        double t = double.IsFinite(v) ? v / ChannelPaletteScale : 0.0;
        double frac = t - Math.Floor(t);
        float bright = (float)(0.3 + 0.7 * (1.0 - Math.Abs(2.0 * frac - 1.0)));
        if (billiard != null)
        {
            int halfTurns = (int)Math.Clamp(Math.Floor(windRadians / Math.PI), 0, MaxHalfTurns);
            return unchecked((uint)billiard.MapBilliard(gate, gateCount, halfTurns, MaxHalfTurns, Math.Clamp((float)frac, 0f, 1f))) | 0xFF000000u;
        }
        return unchecked((uint)BilliardColorHelper.Shade(BilliardColorHelper.GateColor(gate), bright)) | 0xFF000000u;
    }

    /// <summary>#1924 — half-turn cap passed to billiard themes as the bounce range.</summary>
    public const int MaxHalfTurns = 32;

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
            return PaletteColour(map, smooth * DelayPaletteScale);
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
            _preview = new BlackHoleShadowCalculator(dims.Width, dims.Height) { _isPreviewChild = true };
        _preview.FractalParameters = FractalParameters;
        _preview.CenterX = CenterX; _preview.CenterY = CenterY; _preview.Zoom = Zoom;
        _preview.MaxIterations = MaxIterations; _preview.ColorMap = ColorMap; _preview.Quality = Quality;
        _preview.Calculate(ct);
        FracturingFog.Rendering.LowResPreview.UpscaleNearest(_preview.ColorBuffer, dims.Width, dims.Height, ColorBuffer, Width, Height);
        return true;
    }
}
