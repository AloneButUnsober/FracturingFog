// MandelbrotCalculator.cs  — v5  (surface normal output)
//
// Changes over v4
//   • Two new output buffers: NormalXBuffer and NormalYBuffer (float[]).
//   • FillNormal() computes the outward normal to the escape-potential level
//     curve using the Inigo Quilez derivative technique.
//   • The HP (double-double) path now also tracks the complex derivative as
//     double (sufficient precision for smooth surface normals), so 3D colour
//     maps work at all zoom depths.
//   • BuildColorBuffer calls the five-parameter Map overload so 3D themes
//     receive normal data; flat themes use the default no-op override.
//
// Normal computation algorithm
// ──────────────────────────────────────────────────────────────────────────
//   Given:
//     z_n = (zr, zi)   — z value at escape
//     d_n = (dr, di)   — dz/dc derivative at escape (tracked in inner loop)
//
//   The outward normal direction is proportional to the complex expression:
//
//       z_n · conj(d_n)  =  (zr·dr + zi·di)  +  (zi·dr − zr·di)·i
//                            ──────────────────   ────────────────────
//                                  u (nx)                v (ny)
//
//   Reference: Inigo Quilez — "Rendering the Mandelbrot Set"
//              https://iquilezles.org/articles/mandelbrot/
//
//   Normalise by m = sqrt(u²+v²) to obtain nx ∈ [−1,1], ny ∈ [−1,1].
//   For in-set pixels, (nx, ny) = (0, 0).

// MandelbrotCalculator.cs  — v6
//
// Changes over v5
//   • HIGH IMPACT 1: 4-wide SIMD DD path (DD4, AVX2+FMA) replaces scalar
//     CalculateHighPrecision.  ~3–3.5× speedup on the HP path.
//   • HIGH IMPACT 2: IColorMap virtual dispatch eliminated for the live
//     render path via a generic Calculate<TMap> overload + concrete-type
//     dispatch in the public Calculate() entry point.
//   • HIGH IMPACT 3: Color computed inline — the live render path fills
//     ColorBuffer directly inside the per-pixel helpers, eliminating the
//     separate BuildColorBuffer pass and its cache-thrashing float[] reads.
//   • Intermediate float[] buffers (Smooth/Distance/NormalX/NormalY) are
//     still filled for the screenshot/poster path that needs them.
//     A bool parameter skips filling them on the live render path.

using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

using FracturingFog.Interefaces;
using FracturingFog.FFMath;
using FracturingFog.Models;
using System.Diagnostics;

namespace FracturingFog;

public sealed class MandelbrotCalculator
{
    // ── Public state ──────────────────────────────────────────────────────────

    public int Width { get; private set; }
    public int Height { get; private set; }

    public double CenterX { get; set; } = -0.5;
    public double CenterXLo { get; set; } = 0.0;
    public double CenterY { get; set; } = 0.0;
    public double CenterYLo { get; set; } = 0.0;

    // QD limbs 2 and 3 — populated only at zoom > ~5e27 to support deeper
    // zoom (up to ~5e58). Default 0 → behaves as DD. Set by MainForm pan/zoom
    // when QD-aware navigation is engaged.
    public double CenterX2 { get; set; } = 0.0;
    public double CenterX3 { get; set; } = 0.0;
    public double CenterY2 { get; set; } = 0.0;
    public double CenterY3 { get; set; } = 0.0;

    public double Zoom { get; set; } = 1.0;

    /// <summary>Zoom threshold above which QD ref orbit is used (else DD).</summary>
    private const double QDZoomThreshold = 1e25;

    public int MaxIterations { get; set; } = 512;

    public QualityPreset Quality { get; set; } = QualityPreset.Standard;

    /// <summary>True when the last Calculate() used double-double arithmetic.</summary>
    public bool IsHighPrecisionActive { get; private set; }

    /// <summary>
    /// When true the HP path runs the perturbation loop without SA prelude
    /// or BLA skip — used by the benchmark harness to measure raw AVX2/AVX-512
    /// loop cost as a baseline against the accelerated path.
    /// </summary>
    public bool DisableAcceleration { get; set; } = false;

    /// <summary>
    /// Disable SA prelude only (BLA still applies). Use this to isolate
    /// SA-induced visual artefacts at problem zoom levels.
    /// </summary>
    public bool DisableSeriesApproximation { get; set; } = false;

    /// <summary>
    /// Complex-plane units per pixel for the most recent <see cref="Calculate"/>
    /// invocation.  Set at entry so colour maps (notably distance-estimation
    /// themes) can normalise the raw exterior distance value to pixel units.
    /// </summary>
    public static double LastPixelScale { get; private set; } = 1.0;

    public IColorMap ColorMap { get; set; } = new HsvPalette();

    // ── Output buffers ────────────────────────────────────────────────────────

    public int[] IterationBuffer { get; private set; } = Array.Empty<int>();
    public float[] SmoothBuffer { get; private set; } = Array.Empty<float>();
    public float[] DistanceBuffer { get; private set; } = Array.Empty<float>();
    public float[] NormalXBuffer { get; private set; } = Array.Empty<float>();
    public float[] NormalYBuffer { get; private set; } = Array.Empty<float>();
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    // Orbit-aware auxiliary buffers — populated only when an
    // IOrbitAwareColorMap is active.  Zero-filled otherwise.
    public float[] TrapBuffer { get; private set; } = Array.Empty<float>();
    public float[] StripeBuffer { get; private set; } = Array.Empty<float>();
    public float[] TiaBuffer { get; private set; } = Array.Empty<float>();

    // Final-state buffers — z and dz/dc at escape.  Populated on every
    // Calculate() so the recolor / histogram paths can pass them to the
    // 9-parameter Map() overload used by final-state-aware themes
    // (binary decomp, angle decomp, potential, field lines, domain coloring,
    // derivative bailout).  0 for in-set pixels.
    public float[] FinalZrBuffer { get; private set; } = Array.Empty<float>();
    public float[] FinalZiBuffer { get; private set; } = Array.Empty<float>();
    public float[] FinalDrBuffer { get; private set; } = Array.Empty<float>();
    public float[] FinalDiBuffer { get; private set; } = Array.Empty<float>();

    // Interior-cycle buffers — populated only when an IInteriorAwareColorMap
    // is active.  Phase 4 infrastructure (Atom Domains, Argument, Multiplier,
    // Cycle Period, Fake DE themes consume these).  Zero-filled otherwise.
    public int[] InteriorPeriodBuffer { get; private set; } = Array.Empty<int>();
    public float[] AttractorZrBuffer { get; private set; } = Array.Empty<float>();
    public float[] AttractorZiBuffer { get; private set; } = Array.Empty<float>();
    public float[] MultiplierMagBuffer { get; private set; } = Array.Empty<float>();

    // ── Constants ─────────────────────────────────────────────────────────────

    private const double EscapeRadius = 512.0;
    private const double EscapeRadius2 = EscapeRadius * EscapeRadius;
    private static readonly int VecLen = Vector<double>.Count;  // SIMD width (SP path)

    // ── In-set early-out helpers ──────────────────────────────────────────────
    // Two cheap closed-form tests + a per-pixel periodicity check that together
    // collapse the cost of "large black region" pixels from O(MaxIter) to
    // O(detection delay). Fidelity is preserved bit-exact: in-set pixels still
    // end with iter == MaxIter and route through FillAuxAndColor's in-set
    // branch, producing the same InSetColor. The exterior path is untouched.
    //
    // Main cardioid:  q(q + (x − 1/4)) ≤ y²/4    where q = (x − 1/4)² + y²
    // Period-2 bulb:  (x + 1)² + y² ≤ 1/16
    //
    // Both regions are mathematically guaranteed in-set; loop would always run
    // to MaxIter for these points. Test cost ≈ 5 mul + 4 add per pixel — net
    // win whenever a non-trivial number of pixels would otherwise iterate past
    // ~10. Only fires for shallow zooms still showing the parent set, so the
    // overhead at deep zoom is negligible (returns false on the first compare).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsInMainCardioidOrBulb(double cx, double cy)
    {
        double bx = cx + 1.0;
        if (bx * bx + cy * cy <= 0.0625) return true;
        double xm = cx - 0.25;
        double q = xm * xm + cy * cy;
        return q * (q + xm) <= 0.25 * cy * cy;
    }

    // Snapshot interval for the scalar periodicity check. Once a pixel reaches
    // its attracting cycle, z snaps to bit-exact-equal values within a few
    // hundred iterations (doubles round onto the attractor). A 16-iter snapshot
    // gives detection latency ≤ 32 iterations for any cycle of period ≤ 16,
    // and the snapshot interval doubles up to 1024 to cover longer cycles —
    // standard Brent-style schedule.
    private const int PeriodicitySnapshotStart = 16;
    private const int PeriodicitySnapshotMax = 1024;

    // ── Perturbation theory reference orbit ───────────────────────────────────

    private double[] _refZr = Array.Empty<double>();
    private double[] _refZi = Array.Empty<double>();
    // Low limbs of the reference orbit. Populated by ComputeReferenceOrbit (DD:
    // Lo only, X2/X3 = 0) and ComputeReferenceOrbitQD (all three). Consumed by
    // the high-precision per-pixel SA seed in ComputePixelHP / ComputePixelQD
    // to avoid the precision loss that otherwise paints the image-centre as a
    // single colour at extreme zoom (SIMD PT path keeps using only _refZr/_refZi
    // — perturbation by design tracks Z at double precision).
    private double[] _refZrLo = Array.Empty<double>();
    private double[] _refZiLo = Array.Empty<double>();
    private double[] _refZrX2 = Array.Empty<double>();
    private double[] _refZiX2 = Array.Empty<double>();
    private double[] _refZrX3 = Array.Empty<double>();
    private double[] _refZiX3 = Array.Empty<double>();
    private int _refOrbitLen;
    // Bumped on every reference-orbit rebuild. BLA/SA cache derived
    // coefficients from a specific orbit snapshot; recentre that keeps
    // orbit length identical would otherwise reuse stale coefficients
    // (visible as geometric distortion after small double-click recentres).
    private int _refOrbitGen;

    // Reference-orbit cache: zoom-only / theme-only / pan-stable redraws skip
    // recomputation entirely. Key = (center limbs, maxIter at compute time).
    private double _refCxHi = double.NaN, _refCxLo, _refCx2, _refCx3;
    private double _refCyHi, _refCyLo, _refCy2, _refCy3;
    private int _refCachedMaxIter = -1;
    private bool _refCachedEscaped;  // true when orbit terminated by escape

    // BLA (Bilinear Approximation) cache — skip thousands of perturbation
    // iterations per pixel when |δ| stays inside the validity radius. Built
    // lazily after reference orbit is ready, invalidated on orbit change
    // or significant dcMaxAbs drift.
    private BlaTable? _blaTable;
    private double _blaDcMaxAbs;
    private int _blaForRefMaxIter = -1;
    private int _blaForRefOrbitLen = -1;
    private int _blaForRefOrbitGen = -1;
    // Diagnostic counters — reset per Calculate, totalled after Parallel.For
    private long _blaSkipsTotal;
    private long _blaIterSkippedTotal;

    // Series approximation prelude — third-order polynomial in dc, used to
    // skip the early perturbation iterations from z=0 where BLA validity
    // radius is tiny (Z near origin). SA picks up the loose slack BLA leaves.
    private SeriesApproximation? _sa;
    private int _saForRefOrbitLen = -1;
    private int _saForRefMaxIter = -1;
    private int _saForRefOrbitGen = -1;
    // Tolerance for truncation bound. 1e-3 is the classical Zhuoran / KF
    // default. Tested visually stable when paired with BLA Epsilon=1e-6.
    // (Was preemptively tightened to 1e-9 during banding investigation;
    // root cause turned out to be BLA's Epsilon at 1e-4, not SA tolerance.)
    private const double SaTolerance = 1e-3;
    private long _saAppliedTotal;
    private long _saIterSkippedTotal;
    private bool _loggedSimdPath;

    // Cached ParallelOptions reused across every Parallel.For inside a
    // single Calculate. Cheaper than `new ParallelOptions { ct }` per row
    // band (one alloc per band × 6 paths = 6 allocs per Calculate). The
    // calculator is single-instance per host and Calculate is serialised
    // by the host's _calcLock, so swapping CancellationToken in-place is
    // safe.
    private readonly ParallelOptions _po = new();

    // T2.5: chunked range partitioner. Default `Parallel.For(0, h, body)`
    // schedules one work item per row, so a Calculate over `h` rows pays
    // `h` enqueue/dispatch overheads. With Partitioner.Create(0, h, chunk)
    // each worker grabs a contiguous block of rows in a single dispatch,
    // collapsing scheduling cost to `procCount * 4` work items regardless
    // of height. Largest win at low maxIter / small frames where the
    // scheduling overhead dominated the actual row work.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RowChunk(int count)
    {
        int chunk = count / (Environment.ProcessorCount * 4);
        return chunk < 1 ? 1 : chunk;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ParallelForRows(int from, int to, ParallelOptions po, Action<int> body)
    {
        int count = to - from;
        if (count <= 0) return;
        Parallel.ForEach(Partitioner.Create(from, to, RowChunk(count)), po, range =>
        {
            for (int y = range.Item1; y < range.Item2; y++)
                body(y);
        });
    }

    // ── Constructor / resize ──────────────────────────────────────────────────

    public MandelbrotCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        if (width < 1 || height < 1)
            throw new ArgumentException("Dimensions must be positive.");
        Width = width;
        Height = height;
        int n = width * height;
        // Pinned LOH allocation: these buffers are large (8 MB+ at 1080p),
        // long-lived (resize is rare), and consumed by GPU upload / native
        // memcpy. Pinned avoids the GCHandle.Alloc/Free pair every frame
        // the upload path used to need and removes the buffers from the GC
        // mark-and-compact scan. Alignment is 8 byte on the LOH which is
        // sufficient for our Vector256/AVX2 SIMD writes.
        IterationBuffer = GC.AllocateUninitializedArray<int>(n, pinned: true);
        SmoothBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
        DistanceBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
        NormalXBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
        NormalYBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
        ColorBuffer = GC.AllocateUninitializedArray<uint>(n, pinned: true);
        TrapBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
        StripeBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
        TiaBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
        FinalZrBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
        FinalZiBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
        FinalDrBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
        FinalDiBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
        InteriorPeriodBuffer = GC.AllocateUninitializedArray<int>(n, pinned: true);
        AttractorZrBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
        AttractorZiBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
        MultiplierMagBuffer = GC.AllocateUninitializedArray<float>(n, pinned: true);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public entry point — HIGH IMPACT 2: devirtualized dispatch
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fills all output buffers.  Dispatches to a concrete-type generic
    /// overload so the JIT can devirtualize and inline IColorMap.Map().
    /// </summary>
    public void Calculate(CancellationToken ct = default)
    {
#if DEBUG
        // Stacktrace alloc + reflection. Skipped in Release so the hot
        // video/preview path does not pay for the diagnostic.
        var callingMethod = new StackTrace().GetFrame(1)?.GetMethod();
        Debug.WriteLine($"Calculate() called from {callingMethod?.DeclaringType?.Name}.{callingMethod?.Name}{Environment.NewLine} with ColorMap={ColorMap.GetType().Name}, MaxIterations={MaxIterations}");
#endif
        ColorMap.MaxIterations = MaxIterations;

        // Update pixel scale so DE-style themes can normalise raw distance
        // (complex-plane units) into pixel units for stable rendering at any zoom.
        LastPixelScale = (3.5 / Math.Max(Width, Height)) / Zoom;
        if (ColorMap is IColorMapWithPixelScale pxs) pxs.PixelScale = LastPixelScale;

        // ── Orbit-aware dispatch ─────────────────────────────────────────────
        // Orbit traps, stripe average and triangle-inequality average themes
        // need per-iteration z samples and run on a dedicated scalar SP path.
        // Each concrete orbit theme is enumerated so the generic dispatch
        // CalculateOrbitAware<TMap> JIT-specialises and inlines Sample().
        switch (ColorMap)
        {
            case OrbitTrapPointMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapCrossMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapCircleMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapLineMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapStarMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapPickoverStalksMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapBiomorphMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapImageRainbowMap m: CalculateOrbitAware(m, ct); return;
            // Additional orbit-trap shapes (OrbitTrapExtraThemes.cs)
            case OrbitTrapSquareMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapRingMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapHyperbolaMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapLemniscateMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapCardioidMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapDiagonalCrossMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapTriangleMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapHexagonMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapHeartMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapSineWaveMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapConcentricMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapGridMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapPinwheelMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapPolarRoseMap m: CalculateOrbitAware(m, ct); return;
            // 3D-lit orbit-trap variants (OrbitTrap3DThemes.cs)
            case OrbitTrapPointPhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapCrossPhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapCirclePhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapLinePhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapStarPhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapSquarePhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapRingPhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapHyperbolaPhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapLemniscatePhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapCardioidPhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapDiagonalCrossPhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapTrianglePhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapHexagonPhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapHeartPhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapSineWavePhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapConcentricPhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapGridPhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapPinwheelPhong3DMap m: CalculateOrbitAware(m, ct); return;
            case OrbitTrapPolarRosePhong3DMap m: CalculateOrbitAware(m, ct); return;
            case StripeAverageClassicMap m: CalculateOrbitAware(m, ct); return;
            case TriangleInequalityMap m: CalculateOrbitAware(m, ct); return;
            case StripeTiaBlendMap m: CalculateOrbitAware(m, ct); return;
            case CurvatureAverageMap m: CalculateOrbitAware(m, ct); return;
            case LyapunovExponentMap m: CalculateOrbitAware(m, ct); return;
            case GaussianIntegerMap m: CalculateOrbitAware(m, ct); return;
            case ExponentialSmoothingMap m: CalculateOrbitAware(m, ct); return;
        }

        // Pattern-match to the concrete type so the JIT sees a non-virtual call
        // inside Calculate<TMap> and can inline Map() completely.
        // Add new palette types here as the library grows.
        switch (ColorMap)
        {
            case PhongStoneMap m: CalculateCore(m, ct); break;
            case MoltenMetalMap m: CalculateCore(m, ct); break;
            case CrystalCaveMap m: CalculateCore(m, ct); break;
            case GoldReliefMap m: CalculateCore(m, ct); break;
            case MarbleReliefMap m: CalculateCore(m, ct); break;
            case VolcanicRockMap m: CalculateCore(m, ct); break;
            case LunarSurfaceMap m: CalculateCore(m, ct); break;
            case AncientBronzeMap m: CalculateCore(m, ct); break;
            case NeonReliefMap m: CalculateCore(m, ct); break;
            case PolarNight3DMap m: CalculateCore(m, ct); break;
            case Inferno3DMap m: CalculateCore(m, ct); break;
            case Blackbody3DMap m: CalculateCore(m, ct); break;
            case CosmicLatte3DMap m: CalculateCore(m, ct); break;
            case Aurora3DMap m: CalculateCore(m, ct); break;
            case DeepSpaceBlue3DMap m: CalculateCore(m, ct); break;
            case EarthTone3DMap m: CalculateCore(m, ct); break;
            case Icefire3DMap m: CalculateCore(m, ct); break;
            case LavaLamp3DMap m: CalculateCore(m, ct); break;
            case Plasma3DMap m: CalculateCore(m, ct); break;
            case Purplebody3DMap m: CalculateCore(m, ct); break;
            case TriColor3DMap m: CalculateCore(m, ct); break;
            case Tropical3DMap m: CalculateCore(m, ct); break;
            case OceanDepth3DMap m: CalculateCore(m, ct); break;
            case CesiumSpectrumPhong3D m: CalculateCore(m, ct); break;
            case WoodGrainPhong3D m: CalculateCore(m, ct); break;
            case CesiumSpectrumPbr3D m: CalculateCore(m, ct); break;
            case CesiumSpectrumPbr3D_Realistic m: CalculateCore(m, ct); break;
            case CesiumSpectrumPbr3D_UltraGlow m: CalculateCore(m, ct); break;
            case RadioInterferencePhong3D m: CalculateCore(m, ct); break;
            case RadioInterferencePbr3D m: CalculateCore(m, ct); break;

            // ── Algorithmic 3D — Phong ────────────────────────────────────────
            case BernsteinPhong3D m: CalculateCore(m, ct); break;
            case CopperSheenPhong3D m: CalculateCore(m, ct); break;
            case DigitalMatrixPhong3D m: CalculateCore(m, ct); break;
            case DistanceGlowPhong3D m: CalculateCore(m, ct); break;
            case FirePhong3D m: CalculateCore(m, ct); break;
            case GoldenRatioPhong3D m: CalculateCore(m, ct); break;
            case GrayscalePhong3D m: CalculateCore(m, ct); break;
            case HsvPhong3D m: CalculateCore(m, ct); break;
            case MonoBandPhong3D m: CalculateCore(m, ct); break;
            case NebulaDustPhong3D m: CalculateCore(m, ct); break;
            case PaintedPhong3D m: CalculateCore(m, ct); break;
            case PaintedReversedPhong3D m: CalculateCore(m, ct); break;
            case PastellyPhong3D m: CalculateCore(m, ct); break;
            case PsychedelicPhong3D m: CalculateCore(m, ct); break;
            case RadioInterferenceOriginalPhong3D m: CalculateCore(m, ct); break;
            case RadioInterferenceOriginalBluePhong3D m: CalculateCore(m, ct); break;
            case RainbowPhong3D m: CalculateCore(m, ct); break;
            case SolarWindPhong3D m: CalculateCore(m, ct); break;
            case SolarWindModPhong3D m: CalculateCore(m, ct); break;
            case TwilightCyclicPhong3D m: CalculateCore(m, ct); break;
            case VintageSepiaPhong3D m: CalculateCore(m, ct); break;
            case WarpedHsvPhong3D m: CalculateCore(m, ct); break;

            // ── Algorithmic 3D — PBR ──────────────────────────────────────────
            case BernsteinPbr3D m: CalculateCore(m, ct); break;
            case CopperSheenPbr3D m: CalculateCore(m, ct); break;
            case DigitalMatrixPbr3D m: CalculateCore(m, ct); break;
            case DistanceGlowPbr3D m: CalculateCore(m, ct); break;
            case FirePbr3D m: CalculateCore(m, ct); break;
            case GoldenRatioPbr3D m: CalculateCore(m, ct); break;
            case GrayscalePbr3D m: CalculateCore(m, ct); break;
            case HsvPbr3D m: CalculateCore(m, ct); break;
            case MonoBandPbr3D m: CalculateCore(m, ct); break;
            case NebulaDustPbr3D m: CalculateCore(m, ct); break;
            case PaintedPbr3D m: CalculateCore(m, ct); break;
            case PaintedReversedPbr3D m: CalculateCore(m, ct); break;
            case PastellyPbr3D m: CalculateCore(m, ct); break;
            case PsychedelicPbr3D m: CalculateCore(m, ct); break;
            case RadioInterferenceOriginalPbr3D m: CalculateCore(m, ct); break;
            case RainbowPbr3D m: CalculateCore(m, ct); break;
            case SolarWindPbr3D m: CalculateCore(m, ct); break;
            case SolarWindModPbr3D m: CalculateCore(m, ct); break;
            case TwilightCyclicPbr3D m: CalculateCore(m, ct); break;
            case VintageSepiaPbr3D m: CalculateCore(m, ct); break;
            case WarpedHsvPbr3D m: CalculateCore(m, ct); break;

            case HsvPalette m: CalculateCore(m, ct); break;
            case Painted m: CalculateCore(m, ct); break;
            case PaintedReversed m: CalculateCore(m, ct); break;
            case Pastelly m: CalculateCore(m, ct); break;
            case WarpedHsvMap m: CalculateCore(m, ct); break;
            case RainbowColorMap m: CalculateCore(m, ct); break;
            case GoldenRatioMap m: CalculateCore(m, ct); break;
            case MonoBandMap m: CalculateCore(m, ct); break;
            case BernsteinMap m: CalculateCore(m, ct); break;
            case RedAndBlack m: CalculateCore(m, ct); break;
            case BlackbodyColorMap m: CalculateCore(m, ct); break;
            case PurplebodyColorMap m: CalculateCore(m, ct); break;
            case DeepSpaceBlueMap m: CalculateCore(m, ct); break;
            case EarthToneMap m: CalculateCore(m, ct); break;
            case IcefireColorMap m: CalculateCore(m, ct); break;
            case InfernoColorMap m: CalculateCore(m, ct); break;
            case OceanDepthMap m: CalculateCore(m, ct); break;
            case AuroraColorMap m: CalculateCore(m, ct); break;
            case PolarNightMap m: CalculateCore(m, ct); break;
            case CesiumSpectrumGradient m: CalculateCore(m, ct); break;
            case WoodGrainGradient m: CalculateCore(m, ct); break;
            case RadioInterferenceGradient m: CalculateCore(m, ct); break;
            case FirePalette m: CalculateCore(m, ct); break;
            case CosmicLatteMap m: CalculateCore(m, ct); break;
            case TropicalMap m: CalculateCore(m, ct); break;
            case LavaLampMap m: CalculateCore(m, ct); break;
            case TriColorMap m: CalculateCore(m, ct); break;
            case CesiumSpectrumCycling m: CalculateCore(m, ct); break;
            case WoodGrainCycling m: CalculateCore(m, ct); break;
            case RadioInterferenceCycling m: CalculateCore(m, ct); break;
            case NebulaDustMap m: CalculateCore(m, ct); break;
            case DigitalMatrixMap m: CalculateCore(m, ct); break;
            case PsychedelicMap m: CalculateCore(m, ct); break;
            case TwilightCyclicMap m: CalculateCore(m, ct); break;
            case SolarWindMap m: CalculateCore(m, ct); break;
            case SolarWindMapMOD m: CalculateCore(m, ct); break;
            case CopperSheenMap m: CalculateCore(m, ct); break;
            case VintageSepiaMap m: CalculateCore(m, ct); break;
            case GrayscalePalette m: CalculateCore(m, ct); break;
            case ViridisColorMap m: CalculateCore(m, ct); break;
            case PlasmaColorMap m: CalculateCore(m, ct); break;
            case DistanceFieldChromaticMap m: CalculateCore(m, ct); break;
            case DistanceFieldGlowMap m: CalculateCore(m, ct); break;
            case DistanceFieldSilverMap m: CalculateCore(m, ct); break;
            case LambertShadingMap m: CalculateCore(m, ct); break;
            case SlopeShadingMap m: CalculateCore(m, ct); break;
            case EmbossBumpMap m: CalculateCore(m, ct); break;
            case AmbientOcclusionMap m: CalculateCore(m, ct); break;
            case SoftShadowMap m: CalculateCore(m, ct); break;
            case CyclePeriodMap m: CalculateCore(m, ct); break;
            case MultiplierMap m: CalculateCore(m, ct); break;
            case AtomDomainsMap m: CalculateCore(m, ct); break;
            case InteriorArgumentMap m: CalculateCore(m, ct); break;
            case FakeDistanceEstimateMap m: CalculateCore(m, ct); break;
            default:
                // Unknown concrete type — fall back to virtual dispatch.
                // Still correct; just not devirtualized.
                CalculateCore(ColorMap, ct);
                break;
        }

        // ── Optional interior-aware pass ─────────────────────────────────────
        // Themes that colour the in-set region (Atom Domains, Multiplier,
        // Cycle Period, Fake DE, Argument) implement IInteriorAwareColorMap.
        // For each in-set pixel we run Brent cycle detection on z² + c starting
        // from 0, recover (period, attractor, |multiplier|), and let the theme
        // colour the pixel.  Exterior pixels are untouched.
        if (ColorMap is IInteriorAwareColorMap interiorMap)
            RunInteriorPass(interiorMap, ct);

        // ── Optional post-process pass ───────────────────────────────────────
        // Themes that need neighbourhood information (emboss, AO, soft shadow)
        // implement IPostProcessColorMap and run a second pass over the
        // finished ColorBuffer + aux float buffers.  No-op for everything else.
        if (ColorMap is IPostProcessColorMap pp)
            pp.PostProcess(ColorBuffer, SmoothBuffer,
                           NormalXBuffer, NormalYBuffer,
                           Width, Height, MaxIterations);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Phase 4 — Interior orbit / attracting-cycle detection
    //
    // For each in-set pixel we want to know:
    //   • period p of the attracting cycle (1, 2, 3, …)
    //   • a point on the cycle (the attractor sample)
    //   • the cycle multiplier |λ| = |∏_{k=0}^{p−1} 2 z_k|, which classifies
    //     the bulb (hyperbolic if < 1, parabolic if = 1).
    //
    // Algorithm — Brent's cycle detection on z_{n+1} = z_n² + c:
    //   1. SETTLE: iterate from z=0 for `Settle` steps so we land near the
    //      attractor (transient dies out).  Settle = MaxIterations gives the
    //      cleanest result but is expensive; Settle = min(MaxIter, 2000) is
    //      a good compromise.
    //   2. BRENT: search for a cycle.  Tortoise is fixed, hare advances; when
    //      the hare lands within ε of the tortoise we have a cycle of length
    //      lam.  Power-of-two snapshots make this O(p) memory-free.
    //   3. MULTIPLIER: walk the detected cycle once and accumulate the complex
    //      product ∏ 2 z_k.
    //
    // Cost: every in-set pixel pays roughly (Settle + 2·MaxPeriod) extra
    // iterations.  Only invoked when a theme actually consumes the data.
    // ─────────────────────────────────────────────────────────────────────────

    private void RunInteriorPass(IInteriorAwareColorMap interiorMap, CancellationToken ct)
    {
        int maxIt = MaxIterations;
        if (maxIt <= 0) return;

        int w = Width, h = Height;
        double scale = (3.5 / Math.Max(w, h)) / Zoom;
        double cx0 = CenterX, cy0 = CenterY;

        // Settle budget — long enough to reach the attractor for high-period
        // bulbs.  Capped so deep-zoom renders don't pay an unbounded cost.
        int settle = Math.Min(maxIt, 2000);
        // Period search budget — covers all visible secondary / tertiary bulbs.
        const int maxPeriod = 1024;

        _po.CancellationToken = ct;
        var po = _po;
        ParallelForRows(0, h, po, y =>
        {
            if (ct.IsCancellationRequested) return;
            double cy = cy0 + (y - h * 0.5) * scale;
            int rowBase = y * w;
            for (int x = 0; x < w; x++)
            {
                int idx = rowBase + x;
                if (IterationBuffer[idx] < maxIt) continue;     // exterior pixel

                double cx = cx0 + (x - w * 0.5) * scale;
                DetectCycle(cx, cy, settle, maxPeriod,
                            out int period, out double aZr, out double aZi, out double multMag);

                InteriorPeriodBuffer[idx] = period;
                AttractorZrBuffer[idx] = (float)aZr;
                AttractorZiBuffer[idx] = (float)aZi;
                MultiplierMagBuffer[idx] = (float)multMag;

                ColorBuffer[idx] = (uint)interiorMap.MapInterior(
                    period, (float)aZr, (float)aZi, (float)multMag, cx, cy);
            }
        });
    }

    /// <summary>
    /// Brent cycle detection on the Mandelbrot iteration z² + c starting from
    /// z = 0.  Returns the detected cycle period, a point on the cycle, and
    /// |∏ 2 z_k| over one period.  Period = 0 if no cycle found within budget.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DetectCycle(
        double cx, double cy, int settle, int maxPeriod,
        out int period, out double attractorZr, out double attractorZi, out double multMag)
    {
        // ── Step 1: settle near the attractor ───────────────────────────────
        double zr = 0.0, zi = 0.0;
        for (int i = 0; i < settle; i++)
        {
            double zr2 = zr * zr, zi2 = zi * zi;
            if (zr2 + zi2 > 4.0) { period = 0; attractorZr = 0; attractorZi = 0; multMag = 0; return; }
            double nzr = zr2 - zi2 + cx;
            zi = 2.0 * zr * zi + cy;
            zr = nzr;
        }

        // ── Step 2: Brent's algorithm — find cycle length `lam` ─────────────
        // Tortoise snapshot resets at power-of-two boundaries; hare always
        // advances.  Cycle detected when |hare − tortoise|² < eps².  The
        // tolerance scales with attractor magnitude so deeply nested bulbs
        // (small attractors) still detect cleanly.
        double tZr = zr, tZi = zi;
        double hZr = zr, hZi = zi;
        int power = 1, lam = 0;
        double eps2Base = 1e-12;
        // Search budget: enough hops to find any period ≤ maxPeriod.
        int budget = maxPeriod * 4 + 16;

        for (int step = 0; step < budget; step++)
        {
            if (power == lam)
            {
                tZr = hZr; tZi = hZi;
                power *= 2;
                lam = 0;
            }
            double hzr2 = hZr * hZr, hzi2 = hZi * hZi;
            if (hzr2 + hzi2 > 4.0) { period = 0; attractorZr = 0; attractorZi = 0; multMag = 0; return; }
            double nhr = hzr2 - hzi2 + cx;
            hZi = 2.0 * hZr * hZi + cy;
            hZr = nhr;
            lam++;

            double dx = hZr - tZr, dy = hZi - tZi;
            double dd = dx * dx + dy * dy;
            // Scale tolerance by attractor radius² (with floor) so we don't
            // miss tight cycles near origin.
            double scale2 = Math.Max(1.0, hzr2 + hzi2);
            if (dd < eps2Base * scale2 && lam <= maxPeriod)
            {
                period = lam;
                attractorZr = hZr;
                attractorZi = hZi;

                // ── Step 3: cycle multiplier λ = ∏_{k=0}^{p−1} 2 z_k ────────
                // Walk one full period; accumulate the complex product.
                double mr = 1.0, mi = 0.0;
                double pr = hZr, pi = hZi;
                for (int k = 0; k < period; k++)
                {
                    // m *= 2 p
                    double twoPr = 2.0 * pr, twoPi = 2.0 * pi;
                    double nmr = mr * twoPr - mi * twoPi;
                    double nmi = mr * twoPi + mi * twoPr;
                    mr = nmr; mi = nmi;
                    // p = p² + c
                    double p2r = pr * pr - pi * pi + cx;
                    pi = 2.0 * pr * pi + cy;
                    pr = p2r;
                }
                multMag = Math.Sqrt(mr * mr + mi * mi);
                return;
            }
        }

        period = 0;
        attractorZr = 0; attractorZi = 0; multMag = 0;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Generic core — TMap is resolved at JIT time → no virtual call per pixel
    // ─────────────────────────────────────────────────────────────────────────

    private void CalculateCore<TMap>(TMap colorMap, CancellationToken ct)
        where TMap : IColorMap
    {
        bool useHP = Quality.NeedsHighPrecision(Zoom);
        IsHighPrecisionActive = useHP;

        if (useHP)
            CalculateHighPrecision(colorMap, ct);
        else
            CalculateDoublePrecision(colorMap, ct);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PATH A — Standard double + SIMD (unchanged algorithm, inline color)
    // ─────────────────────────────────────────────────────────────────────────

    private void CalculateDoublePrecision<TMap>(TMap colorMap, CancellationToken ct)
        where TMap : IColorMap
    {
        double scale = (3.5 / Math.Max(Width, Height)) / Zoom;
        int maxIt = MaxIterations;

        _po.CancellationToken = ct;
        var po = _po;
        ParallelForRows(0, Height, po, y =>
        {
            if (ct.IsCancellationRequested) return;
            double cy = CenterY + (y - Height * 0.5) * scale;
            ComputeRowSP(cy, CenterX, scale, maxIt, y * Width, colorMap);
        });
    }

    private void ComputeRowSP<TMap>(
        double cy, double centerX, double scale,
        int maxIter, int rowBase, TMap colorMap)
        where TMap : IColorMap
    {
        var escRad2V = new Vector<double>(EscapeRadius2);
        var twoV = new Vector<double>(2.0);
        var oneV = Vector<double>.One;
        var zeroV = Vector<double>.Zero;
        var cyV = new Vector<double>(cy);

        Span<double> cxBuf = stackalloc double[VecLen];
        Span<double> iterCntBuf = stackalloc double[VecLen];

        // Hoisted out of column loop to avoid CA2014 stack growth.
        // Only used when VecLen == 4 and vMap != null, but cheap to allocate
        // unconditionally — single 16 B span × 9 = 144 B once per row.
        Span<float> sm   = stackalloc float[4];
        Span<float> ds   = stackalloc float[4];
        Span<float> nxs  = stackalloc float[4];
        Span<float> nys  = stackalloc float[4];
        Span<float> fzrS = stackalloc float[4];
        Span<float> fziS = stackalloc float[4];
        Span<float> fdrS = stackalloc float[4];
        Span<float> fdiS = stackalloc float[4];
        Span<int>   colors = stackalloc int[4];

        int x = 0;

        // ── Vectorized lanes ──────────────────────────────────────────────────
        for (; x + VecLen <= Width; x += VecLen)
        {
            for (int k = 0; k < VecLen; k++)
                cxBuf[k] = centerX + ((x + k) - Width * 0.5) * scale;
            var cx = new Vector<double>(cxBuf);

            // Whole-block cardioid/bulb skip — fires often on shallow-zoom
            // video frames sweeping past the parent set. Per-lane test on the
            // four/eight x values; if every lane is in-set, we can write the
            // result directly without entering the iteration loop.
            int bulbBits = 0;
            for (int k = 0; k < VecLen; k++)
                if (IsInMainCardioidOrBulb(cxBuf[k], cy)) bulbBits |= 1 << k;
            int allLanesMask = (1 << VecLen) - 1;
            if (bulbBits == allLanesMask)
            {
                for (int k = 0; k < VecLen; k++)
                {
                    int idx = rowBase + x + k;
                    IterationBuffer[idx] = maxIter;
                    FillAuxAndColorSP(idx, maxIter, maxIter, 0, 0, 1, 0, colorMap);
                }
                continue;
            }

            var zr = zeroV; var zi = zeroV;
            var dr = oneV; var di = zeroV;
            var iterCountV = zeroV;

            // Block-level periodicity. Snapshot z every snapInterval iters;
            // when every lane equals its snapshot, no lane is escaping and no
            // lane is still on a transient orbit — escaped lanes are frozen
            // by ConditionalSelect (so they always self-match after one
            // interval), and in-set lanes have reached their attracting
            // cycle. Promote the unescaped lanes to maxIter and exit early.
            var zrSnap = zeroV;
            var ziSnap = zeroV;
            int snapInterval = PeriodicitySnapshotStart;
            int snapCounter = 0;

            for (int iter = 0; iter < maxIter; iter++)
            {
                var zr2 = zr * zr;
                var zi2 = zi * zi;
                var mag2 = zr2 + zi2;
                var notEscaped = Vector.LessThan(mag2, escRad2V);

                iterCountV += Vector.ConditionalSelect(notEscaped, oneV, zeroV);

                var newDr = twoV * (zr * dr - zi * di) + oneV;
                var newDi = twoV * (zr * di + zi * dr);
                dr = Vector.ConditionalSelect(notEscaped, newDr, dr);
                di = Vector.ConditionalSelect(notEscaped, newDi, di);

                var newZr = zr2 - zi2 + cx;
                var newZi = twoV * zr * zi + cyV;
                zr = Vector.ConditionalSelect(notEscaped, newZr, zr);
                zi = Vector.ConditionalSelect(notEscaped, newZi, zi);

                // Per-iter escape check: break as soon as every lane has
                // escaped. Cheaper than every-8 gating because branch predictor
                // handles the steady "still iterating" path, and we save up to
                // 7 iterations per block when all lanes diverge quickly.
                if (!Vector.LessThanAny(mag2, escRad2V)) break;

                if (++snapCounter >= snapInterval)
                {
                    if (Vector.EqualsAll(zr, zrSnap) && Vector.EqualsAll(zi, ziSnap))
                    {
                        // All lanes either frozen-escaped or on cycle. Promote
                        // any lane whose mag² is still below escape radius
                        // (i.e., never escaped) to maxIter so the post-loop
                        // FillAux routes it through the in-set branch.
                        iterCountV.CopyTo(iterCntBuf);
                        for (int k = 0; k < VecLen; k++)
                        {
                            double m2 = zr[k] * zr[k] + zi[k] * zi[k];
                            if (m2 < EscapeRadius2) iterCntBuf[k] = maxIter;
                        }
                        iterCountV = new Vector<double>(iterCntBuf);
                        break;
                    }
                    zrSnap = zr; ziSnap = zi;
                    snapCounter = 0;
                    if (snapInterval < PeriodicitySnapshotMax) snapInterval <<= 1;
                }
            }

            // SIMD-batched color path: when the active theme implements
            // IVectorColorMap and VecLen == 4 (AVX2 double-lane width), gather
            // the four lanes' aux outputs into Vector128<float> packs and call
            // MapV once instead of four scalar Map() calls. Cast is constant-
            // folded by JIT generic specialisation when TMap is a concrete
            // type known not to implement the interface.
            var vMap = colorMap as IVectorColorMap;
            if (vMap != null && VecLen == 4)
            {
                // Scratch spans hoisted above loop (see CA2014 fix).
                int inSetBits = 0;

                for (int k = 0; k < 4; k++)
                {
                    int idx = rowBase + x + k;
                    int iters = (int)iterCountV[k];
                    IterationBuffer[idx] = iters;
                    FillAuxOnlySP(idx, iters, maxIter,
                        zr[k], zi[k], dr[k], di[k],
                        out sm[k], out ds[k], out nxs[k], out nys[k],
                        out fzrS[k], out fziS[k], out fdrS[k], out fdiS[k]);
                    if (iters >= maxIter) inSetBits |= 1 << k;
                }

                var smV  = Vector128.Create(sm[0],   sm[1],   sm[2],   sm[3]);
                var dsV  = Vector128.Create(ds[0],   ds[1],   ds[2],   ds[3]);
                var nxV  = Vector128.Create(nxs[0],  nxs[1],  nxs[2],  nxs[3]);
                var nyV  = Vector128.Create(nys[0],  nys[1],  nys[2],  nys[3]);
                var fzrV = Vector128.Create(fzrS[0], fzrS[1], fzrS[2], fzrS[3]);
                var fziV = Vector128.Create(fziS[0], fziS[1], fziS[2], fziS[3]);
                var fdrV = Vector128.Create(fdrS[0], fdrS[1], fdrS[2], fdrS[3]);
                var fdiV = Vector128.Create(fdiS[0], fdiS[1], fdiS[2], fdiS[3]);
                var colorV = vMap.MapV(smV, dsV, maxIter, nxV, nyV, fzrV, fziV, fdrV, fdiV);

                colorV.CopyTo(colors);
                uint inSetColor = colorMap.InSetColor;
                for (int k = 0; k < 4; k++)
                {
                    int idx = rowBase + x + k;
                    ColorBuffer[idx] = ((inSetBits >> k) & 1) != 0 ? inSetColor : (uint)colors[k];
                }
            }
            else
            {
                for (int k = 0; k < VecLen; k++)
                {
                    int idx = rowBase + x + k;
                    int iters = (int)iterCountV[k];
                    IterationBuffer[idx] = iters;
                    // HIGH IMPACT 3: fill aux buffers AND color in one pass
                    FillAuxAndColorSP(idx, iters, maxIter,
                        zr[k], zi[k], dr[k], di[k], colorMap);
                }
            }
        }

        // ── Scalar tail ───────────────────────────────────────────────────────
        for (; x < Width; x++)
        {
            double cx2 = centerX + (x - Width * 0.5) * scale;
            ComputePixelSP(cx2, cy, maxIter, rowBase + x, colorMap);
        }
    }

    /// <summary>
    /// Aux-only twin of FillAuxAndColorSP — writes per-pixel buffers but
    /// emits the values it computed via out-params so the batched color
    /// stage can gather them into Vector128 packs without an extra read of
    /// SmoothBuffer / NormalXBuffer / etc. Color stage is then a single
    /// MapV() call per 4-pixel block.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FillAuxOnlySP(
        int idx, int iters, int maxIter,
        double zr, double zi, double dr, double di,
        out float smooth, out float dist,
        out float nx, out float ny,
        out float fzr, out float fzi, out float fdr, out float fdi)
    {
        if (iters < maxIter)
        {
            double mag = Math.Sqrt(zr * zr + zi * zi);
            smooth = (float)(iters + 1.0 - Math.Log2(Math.Log2(mag)));
            SmoothBuffer[idx] = smooth;

            double dMag = Math.Sqrt(dr * dr + di * di);
            dist = dMag > 1e-10 ? (float)(mag * Math.Log(mag) / dMag) : 0f;
            DistanceBuffer[idx] = dist;

            FillNormal(idx, zr, zi, dr, di);
            nx = NormalXBuffer[idx];
            ny = NormalYBuffer[idx];

            fzr = (float)zr; fzi = (float)zi;
            fdr = (float)dr; fdi = (float)di;
            FinalZrBuffer[idx] = fzr;
            FinalZiBuffer[idx] = fzi;
            FinalDrBuffer[idx] = fdr;
            FinalDiBuffer[idx] = fdi;
        }
        else
        {
            smooth = 0f; dist = 0f;
            nx = 0f; ny = 0f;
            fzr = 0f; fzi = 0f; fdr = 0f; fdi = 0f;
            SmoothBuffer[idx] = 0f;
            DistanceBuffer[idx] = 0f;
            NormalXBuffer[idx] = 0f;
            NormalYBuffer[idx] = 0f;
            FinalZrBuffer[idx] = 0f;
            FinalZiBuffer[idx] = 0f;
            FinalDrBuffer[idx] = 0f;
            FinalDiBuffer[idx] = 0f;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ComputePixelSP<TMap>(
        double cx, double cy, int maxIter, int idx, TMap colorMap)
        where TMap : IColorMap
    {
        if (IsInMainCardioidOrBulb(cx, cy))
        {
            IterationBuffer[idx] = maxIter;
            FillAuxAndColorSP(idx, maxIter, maxIter, 0, 0, 1, 0, colorMap);
            return;
        }

        double zr = 0, zi = 0, dr = 1, di = 0;
        double zrSnap = 0, ziSnap = 0;
        int snapInterval = PeriodicitySnapshotStart;
        int snapCounter = 0;

        int iter;
        for (iter = 0; iter < maxIter; iter++)
        {
            double zr2 = zr * zr, zi2 = zi * zi;
            if (zr2 + zi2 >= EscapeRadius2) break;
            double newDr = 2.0 * (zr * dr - zi * di) + 1.0;
            double newDi = 2.0 * (zr * di + zi * dr);
            dr = newDr; di = newDi;
            double newZr = zr2 - zi2 + cx;
            zi = 2.0 * zr * zi + cy;
            zr = newZr;

            if (zr == zrSnap && zi == ziSnap) { iter = maxIter; break; }
            if (++snapCounter >= snapInterval)
            {
                zrSnap = zr; ziSnap = zi;
                snapCounter = 0;
                if (snapInterval < PeriodicitySnapshotMax) snapInterval <<= 1;
            }
        }
        IterationBuffer[idx] = iter;
        FillAuxAndColorSP(idx, iter, maxIter, zr, zi, dr, di, colorMap);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FillAuxAndColorSP<TMap>(
        int idx, int iters, int maxIter,
        double zr, double zi, double dr, double di,
        TMap colorMap)
        where TMap : IColorMap
    {
        if (iters < maxIter)
        {
            double mag = Math.Sqrt(zr * zr + zi * zi);
            // Smooth iteration: iters + 1 - log2(log2(mag)). Math.Log2 is a
            // single hardware-backed call vs 3× Math.Log + 2 divides in the
            // log-ratio formulation. Identical algebraic result.
            float smooth = (float)(iters + 1.0 - Math.Log2(Math.Log2(mag)));
            SmoothBuffer[idx] = smooth;

            double dMag = Math.Sqrt(dr * dr + di * di);
            float dist = dMag > 1e-10
                ? (float)(mag * Math.Log(mag) / dMag) : 0f;
            DistanceBuffer[idx] = dist;

            FillNormal(idx, zr, zi, dr, di);

            float fzr = (float)zr, fzi = (float)zi;
            float fdr = (float)dr, fdi = (float)di;
            FinalZrBuffer[idx] = fzr;
            FinalZiBuffer[idx] = fzi;
            FinalDrBuffer[idx] = fdr;
            FinalDiBuffer[idx] = fdi;

            // HIGH IMPACT 3: color computed HERE, no second pass
            // Themes that implement IColorMapHandlesInSet get the true escape
            // iteration count so their `isInSet` / `iter` inputs are accurate;
            // every other theme keeps the documented maxIter contract.
            int iterArg = colorMap is IColorMapHandlesInSet ? iters : maxIter;
            ColorBuffer[idx] = (uint)colorMap.Map(
                smooth, dist, iterArg,
                NormalXBuffer[idx], NormalYBuffer[idx],
                fzr, fzi, fdr, fdi);
        }
        else
        {
            SmoothBuffer[idx] = 0f;
            DistanceBuffer[idx] = 0f;
            NormalXBuffer[idx] = 0f;
            NormalYBuffer[idx] = 0f;
            FinalZrBuffer[idx] = 0f;
            FinalZiBuffer[idx] = 0f;
            FinalDrBuffer[idx] = 0f;
            FinalDiBuffer[idx] = 0f;
            if (colorMap is IColorMapHandlesInSet)
            {
                // Route interior through Map() so the theme can colour the
                // inside of the set; iters = maxIter triggers isInSet = 1.0.
                ColorBuffer[idx] = (uint)colorMap.Map(
                    0f, 0f, maxIter,
                    0f, 0f, 0f, 0f, 0f, 0f);
            }
            else
            {
                ColorBuffer[idx] = colorMap.InSetColor; // theme-defined interior, default opaque black
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Normal computation (shared by both paths)
    // ─────────────────────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FillNormal(int idx, double zr, double zi, double dr, double di)
    {
        double u = zr * dr + zi * di;
        double v = zi * dr - zr * di;
        double m = Math.Sqrt(u * u + v * v);
        if (m > 1e-10)
        {
            NormalXBuffer[idx] = (float)(u / m);
            NormalYBuffer[idx] = (float)(v / m);
        }
        else
        {
            NormalXBuffer[idx] = 0f;
            NormalYBuffer[idx] = 0f;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PATH C — orbit-aware scalar (SP only)
    //
    // Used for orbit traps, stripe average, and triangle-inequality average
    // colour maps that require per-iteration z samples.  No SIMD, no high
    // precision — these themes are not supported at extreme zoom.
    // ─────────────────────────────────────────────────────────────────────────

    private void CalculateOrbitAware<TMap>(TMap colorMap, CancellationToken ct)
        where TMap : IOrbitAwareColorMap
    {
        bool useHP = Quality.NeedsHighPrecision(Zoom);
        IsHighPrecisionActive = useHP;

        double scale = (3.5 / Math.Max(Width, Height)) / Zoom;
        int maxIt = MaxIterations;

        _po.CancellationToken = ct;
        var po = _po;
        ParallelForRows(0, Height, po, y =>
        {
            if (ct.IsCancellationRequested) return;
            double cy = CenterY + (y - Height * 0.5) * scale;
            int rowBase = y * Width;
            for (int x = 0; x < Width; x++)
            {
                double cx = CenterX + (x - Width * 0.5) * scale;
                ComputePixelOrbit(cx, cy, maxIt, rowBase + x, colorMap);
            }
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ComputePixelOrbit<TMap>(
        double cx, double cy, int maxIter, int idx, TMap colorMap)
        where TMap : IOrbitAwareColorMap
    {
        colorMap.InitOrbit(out var acc);

        // Bulb early-out: pixel guaranteed in-set, so the orbit accumulator
        // would be unused (in-set branch below ignores acc). Skip the loop.
        if (IsInMainCardioidOrBulb(cx, cy))
        {
            IterationBuffer[idx] = maxIter;
            SmoothBuffer[idx] = 0f;
            DistanceBuffer[idx] = 0f;
            NormalXBuffer[idx] = 0f;
            NormalYBuffer[idx] = 0f;
            TrapBuffer[idx] = 0f;
            StripeBuffer[idx] = 0f;
            TiaBuffer[idx] = 0f;
            FinalZrBuffer[idx] = 0f;
            FinalZiBuffer[idx] = 0f;
            FinalDrBuffer[idx] = 0f;
            FinalDiBuffer[idx] = 0f;
            ColorBuffer[idx] = colorMap.InSetColor;
            return;
        }

        double zr = 0, zi = 0, dr = 1, di = 0;
        double zrSnap = 0, ziSnap = 0;
        int snapInterval = PeriodicitySnapshotStart;
        int snapCounter = 0;
        int iter;
        for (iter = 0; iter < maxIter; iter++)
        {
            double zr2 = zr * zr, zi2 = zi * zi;
            if (zr2 + zi2 >= EscapeRadius2) break;

            // Sample BEFORE update so iter==0 is skipped (z_0 = 0 has no arg).
            if (iter > 0) colorMap.Sample(ref acc, zr, zi, cx, cy, iter);

            double newDr = 2.0 * (zr * dr - zi * di) + 1.0;
            double newDi = 2.0 * (zr * di + zi * dr);
            dr = newDr; di = newDi;

            double newZr = zr2 - zi2 + cx;
            zi = 2.0 * zr * zi + cy;
            zr = newZr;

            // Periodicity early-out — orbit accumulators are unused for in-set
            // pixels (see in-set branch below), so terminating the iteration
            // here doesn't affect the final color, only the runtime.
            if (zr == zrSnap && zi == ziSnap) { iter = maxIter; break; }
            if (++snapCounter >= snapInterval)
            {
                zrSnap = zr; ziSnap = zi;
                snapCounter = 0;
                if (snapInterval < PeriodicitySnapshotMax) snapInterval <<= 1;
            }
        }
        IterationBuffer[idx] = iter;

        if (iter < maxIter)
        {
            double mag = Math.Sqrt(zr * zr + zi * zi);
            float smooth = (float)(iter + 1.0 - Math.Log2(Math.Log2(mag)));
            SmoothBuffer[idx] = smooth;

            double dMag = Math.Sqrt(dr * dr + di * di);
            float dist = dMag > 1e-10 ? (float)(mag * Math.Log(mag) / dMag) : 0f;
            DistanceBuffer[idx] = dist;

            FillNormal(idx, zr, zi, dr, di);

            TrapBuffer[idx] = acc.TrapMin == float.MaxValue ? 0f : acc.TrapMin;
            StripeBuffer[idx] = acc.StripeCount > 0 ? (float)(acc.StripeSum / acc.StripeCount) : 0f;
            TiaBuffer[idx] = acc.TiaCount > 0 ? (float)(acc.TiaSum / acc.TiaCount) : 0f;

            FinalZrBuffer[idx] = (float)zr;
            FinalZiBuffer[idx] = (float)zi;
            FinalDrBuffer[idx] = (float)dr;
            FinalDiBuffer[idx] = (float)di;

            ColorBuffer[idx] = (uint)colorMap.MapWithOrbit(
                smooth, dist, maxIter,
                NormalXBuffer[idx], NormalYBuffer[idx], in acc);
        }
        else
        {
            SmoothBuffer[idx] = 0f;
            DistanceBuffer[idx] = 0f;
            NormalXBuffer[idx] = 0f;
            NormalYBuffer[idx] = 0f;
            TrapBuffer[idx] = 0f;
            StripeBuffer[idx] = 0f;
            TiaBuffer[idx] = 0f;
            FinalZrBuffer[idx] = 0f;
            FinalZiBuffer[idx] = 0f;
            FinalDrBuffer[idx] = 0f;
            FinalDiBuffer[idx] = 0f;
            ColorBuffer[idx] = colorMap.InSetColor;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PATH B — HIGH IMPACT 1: 4-wide AVX2 DD (DD4) with scalar fallback
    // ─────────────────────────────────────────────────────────────────────────

    private void CalculateHighPrecision<TMap>(TMap colorMap, CancellationToken ct)
        where TMap : IColorMap
    {
        double scale = (3.5 / Math.Max(Width, Height)) / Zoom;
        int maxIt = MaxIterations;

        // One reference orbit at the view centre. Each pixel iterates only
        // the double-precision delta δ_n = z_n − Z_n.
        //
        // Centre precision selection:
        //   • Zoom ≤ 1e25  →  DD center (~31 digits) is sufficient.
        //   • Zoom > 1e25  →  promote to QD center (~62 digits), supporting
        //                     zoom to ~5×10⁵⁸. Adds 4 limbs (X2, X3, Y2, Y3)
        //                     populated by MainForm pan/zoom when active.
        if (Zoom > QDZoomThreshold)
        {
            var cxQD = new QD(CenterX, CenterXLo, CenterX2, CenterX3);
            var cyQD = new QD(CenterY, CenterYLo, CenterY2, CenterY3);
            ComputeReferenceOrbitQD(cxQD, cyQD, maxIt);
        }
        else
        {
            ComputeReferenceOrbit(new DD(CenterX, CenterXLo), new DD(CenterY, CenterYLo), maxIt);
        }

        // Build / refresh the BLA table now that the reference orbit is current.
        // dcMaxAbs is the worst-case pixel offset from view centre (corner distance).
        double halfWS = Width * 0.5 * scale;
        double halfHS = Height * 0.5 * scale;
        double dcMaxAbs = Math.Sqrt(halfWS * halfWS + halfHS * halfHS);
        EnsureBlaTable(dcMaxAbs);
        EnsureSeriesApproximation();

        _blaSkipsTotal = 0;
        _blaIterSkippedTotal = 0;
        _saAppliedTotal = 0;
        _saIterSkippedTotal = 0;

        bool useSimd512 = Avx512F.IsSupported && Vector512.IsHardwareAccelerated;
        bool useSimd = DD4.IsSupported;
        if (!_loggedSimdPath)
        {
            Debug.WriteLine(useSimd512 ? "PT path: AVX-512 (8 lanes)"
                                       : useSimd ? "PT path: AVX2 (4 lanes)"
                                                 : "PT path: scalar");
            _loggedSimdPath = true;
        }
        _po.CancellationToken = ct;
        var po = _po;
        ParallelForRows(0, Height, po, y =>
        {
            if (ct.IsCancellationRequested) return;
            int rowBase = y * Width;
            if (useSimd512)
                ComputeRowPT8(y, scale, maxIt, rowBase, colorMap);
            else if (useSimd)
                ComputeRowPT4(y, scale, maxIt, rowBase, colorMap);
            else
                ComputeRowPTScalar(y, scale, maxIt, rowBase, colorMap);
        });

        if (_blaTable != null)
            Debug.WriteLine(
                $"BLA: {_blaSkipsTotal:N0} skips, {_blaIterSkippedTotal:N0} iter saved " +
                $"(avg {(_blaSkipsTotal == 0 ? 0 : _blaIterSkippedTotal / (double)_blaSkipsTotal):F1}/skip), " +
                $"refLen={_refOrbitLen}, levels={_blaTable.Levels}, dcMax={dcMaxAbs:E2}");

        if (_sa != null)
            Debug.WriteLine(
                $"SA : {_saAppliedTotal:N0} applied, {_saIterSkippedTotal:N0} iter saved " +
                $"(avg {(_saAppliedTotal == 0 ? 0 : _saIterSkippedTotal / (double)_saAppliedTotal):F1}/apply), " +
                $"safeMax={_sa.SafeMax}");
    }

    /// <summary>
    /// Build / refresh the series approximation table for the current
    /// reference orbit. Recomputed only when the orbit changes — coefficients
    /// are dc-independent so no zoom-drift invalidation is needed.
    /// </summary>
    private void EnsureSeriesApproximation()
    {
        if (_sa != null
            && _saForRefOrbitLen == _refOrbitLen
            && _saForRefMaxIter == _refCachedMaxIter
            && _saForRefOrbitGen == _refOrbitGen)
            return;

        if (_refOrbitLen < 4) { _sa = null; return; }
        _sa = new SeriesApproximation(_refZr, _refZi, _refOrbitLen);
        _saForRefOrbitLen = _refOrbitLen;
        _saForRefMaxIter = _refCachedMaxIter;
        _saForRefOrbitGen = _refOrbitGen;
    }

    /// <summary>
    /// Build or refresh the BLA hierarchical table. Invalidated when the
    /// reference orbit changes (maxIter, length) or when dcMaxAbs has drifted
    /// far enough that previously-merged validity radii are stale.
    /// </summary>
    private void EnsureBlaTable(double dcMaxAbs)
    {
        bool refChanged = _blaForRefMaxIter != _refCachedMaxIter
                       || _blaForRefOrbitLen != _refOrbitLen
                       || _blaForRefOrbitGen != _refOrbitGen;
        // Relative tolerance: BLA merge uses dcMaxAbs in its validity bound,
        // so a 5% drift is safely within the linearisation margin (Epsilon=1e-6).
        double dcDrift = _blaDcMaxAbs <= 0 ? double.PositiveInfinity
                                           : Math.Abs(dcMaxAbs - _blaDcMaxAbs) / _blaDcMaxAbs;
        if (!refChanged && _blaTable != null && dcDrift < 0.05) return;

        if (_refOrbitLen < 4) { _blaTable = null; return; }
        _blaTable = new BlaTable(_refZr, _refZi, _refOrbitLen, dcMaxAbs);
        _blaDcMaxAbs = dcMaxAbs;
        _blaForRefMaxIter = _refCachedMaxIter;
        _blaForRefOrbitLen = _refOrbitLen;
        _blaForRefOrbitGen = _refOrbitGen;
    }

    // ── Blend helpers (replaces VBLENDVPD masking) ────────────────────────────

    /// <summary>
    /// Converts a 4-bit integer mask (bit i = lane i active) to a
    /// Vector256 of all-ones (active) or all-zeros (frozen) per lane.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<double> MaskToVector(int mask)
    {
        // Each lane gets all-bits-set (-1 reinterpreted as double) if active
        long m0 = (mask & 1) != 0 ? -1L : 0L;
        long m1 = (mask & 2) != 0 ? -1L : 0L;
        long m2 = (mask & 4) != 0 ? -1L : 0L;
        long m3 = (mask & 8) != 0 ? -1L : 0L;
        return Vector256.Create(m0, m1, m2, m3).AsDouble();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<double> BlendActive(
        Vector256<double> frozen, Vector256<double> updated, int activeMask)
    {
        var mask = MaskToVector(activeMask);
        return Avx.BlendVariable(frozen, updated, mask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static DD4 BlendDD4Active(DD4 frozen, DD4 updated, int activeMask)
    {
        var mask = MaskToVector(activeMask);
        return new DD4(
            Avx.BlendVariable(frozen.Hi, updated.Hi, mask),
            Avx.BlendVariable(frozen.Lo, updated.Lo, mask));
    }

    /// <summary>
    /// Horizontal max over Vector256&lt;double&gt;, restricted to lanes selected
    /// by the 4-bit active mask. Inactive lanes are ignored. Returns 0 when
    /// activeMask is empty.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double HMaxMasked(Vector256<double> v, int activeMask)
    {
        double max = 0.0;
        if ((activeMask & 1) != 0) { double d = v.GetElement(0); if (d > max) max = d; }
        if ((activeMask & 2) != 0) { double d = v.GetElement(1); if (d > max) max = d; }
        if ((activeMask & 4) != 0) { double d = v.GetElement(2); if (d > max) max = d; }
        if ((activeMask & 8) != 0) { double d = v.GetElement(3); if (d > max) max = d; }
        return max;
    }

    // ── 8-lane helpers (AVX-512 path) ─────────────────────────────────────────

    /// <summary>8-bit mask → Vector512&lt;double&gt; with all-ones in active lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<double> MaskToVector512(int mask)
    {
        return Vector512.Create(
            (mask & 0x01) != 0 ? -1L : 0L,
            (mask & 0x02) != 0 ? -1L : 0L,
            (mask & 0x04) != 0 ? -1L : 0L,
            (mask & 0x08) != 0 ? -1L : 0L,
            (mask & 0x10) != 0 ? -1L : 0L,
            (mask & 0x20) != 0 ? -1L : 0L,
            (mask & 0x40) != 0 ? -1L : 0L,
            (mask & 0x80) != 0 ? -1L : 0L).AsDouble();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<double> BlendActive512(
        Vector512<double> frozen, Vector512<double> updated, int activeMask)
    {
        var mask = MaskToVector512(activeMask);
        return Vector512.ConditionalSelect(mask, updated, frozen);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double HMaxMasked8(Vector512<double> v, int activeMask)
    {
        double max = 0.0;
        if ((activeMask & 0x01) != 0) { double d = v.GetElement(0); if (d > max) max = d; }
        if ((activeMask & 0x02) != 0) { double d = v.GetElement(1); if (d > max) max = d; }
        if ((activeMask & 0x04) != 0) { double d = v.GetElement(2); if (d > max) max = d; }
        if ((activeMask & 0x08) != 0) { double d = v.GetElement(3); if (d > max) max = d; }
        if ((activeMask & 0x10) != 0) { double d = v.GetElement(4); if (d > max) max = d; }
        if ((activeMask & 0x20) != 0) { double d = v.GetElement(5); if (d > max) max = d; }
        if ((activeMask & 0x40) != 0) { double d = v.GetElement(6); if (d > max) max = d; }
        if ((activeMask & 0x80) != 0) { double d = v.GetElement(7); if (d > max) max = d; }
        return max;
    }

    // ── HP per-pixel color fill (inline, no second pass) ─────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FillAuxAndColorHP<TMap>(
        int idx, int iters, int maxIter,
        double zrD, double ziD, double drD, double diD,
        TMap colorMap)
        where TMap : IColorMap
    {
        if (iters < maxIter)
        {
            double mag = Math.Sqrt(zrD * zrD + ziD * ziD);
            float smooth = (float)(iters + 1.0 - Math.Log2(Math.Log2(mag)));
            SmoothBuffer[idx] = smooth;

            // Distance estimate: mag·ln(mag)/|d|. Derivative tracked as plain
            // double in the PT path — same magnitude domain as SP, so the
            // formula transfers without precision loss. Restores 3D-theme
            // shading (Phong/PBR) at deep zoom.
            double dMag = Math.Sqrt(drD * drD + diD * diD);
            float dist = dMag > 1e-10
                ? (float)(mag * Math.Log(mag) / dMag) : 0f;
            DistanceBuffer[idx] = dist;

            FillNormal(idx, zrD, ziD, drD, diD);

            float fzr = (float)zrD, fzi = (float)ziD;
            float fdr = (float)drD, fdi = (float)diD;
            FinalZrBuffer[idx] = fzr;
            FinalZiBuffer[idx] = fzi;
            FinalDrBuffer[idx] = fdr;
            FinalDiBuffer[idx] = fdi;

            int iterArg = colorMap is IColorMapHandlesInSet ? iters : maxIter;
            ColorBuffer[idx] = (uint)colorMap.Map(
                smooth, dist, iterArg,
                NormalXBuffer[idx], NormalYBuffer[idx],
                fzr, fzi, fdr, fdi);
        }
        else
        {
            SmoothBuffer[idx] = 0f;
            DistanceBuffer[idx] = 0f;
            NormalXBuffer[idx] = 0f;
            NormalYBuffer[idx] = 0f;
            FinalZrBuffer[idx] = 0f;
            FinalZiBuffer[idx] = 0f;
            FinalDrBuffer[idx] = 0f;
            FinalDiBuffer[idx] = 0f;
            if (colorMap is IColorMapHandlesInSet)
            {
                ColorBuffer[idx] = (uint)colorMap.Map(
                    0f, 0f, maxIter,
                    0f, 0f, 0f, 0f, 0f, 0f);
            }
            else
            {
                ColorBuffer[idx] = colorMap.InSetColor;
            }
        }
    }

    // ── Full-DD per-pixel fallback (used for PT glitches at zoom ≤ QDZoomThreshold) ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ComputePixelHP<TMap>(DD cx, DD cy, int maxIter, int idx, TMap colorMap)
        where TMap : IColorMap
    {
        // Bulb early-out — practically only fires for shallow-zoom HP frames
        // (the parent set's main bulb/cardioid lives at zoom ~1). At deep
        // zoom the test returns false in O(1), so the overhead is trivial.
        if (IsInMainCardioidOrBulb(cx.Hi, cy.Hi))
        {
            IterationBuffer[idx] = maxIter;
            FillAuxAndColorHP(idx, maxIter, maxIter, 0, 0, 1, 0, colorMap);
            return;
        }

        DD zr = DD.Zero, zi = DD.Zero;
        double dr = 1.0, di = 0.0;
        int iterStart = 0;

        // SA prelude — skip the first k DD iterations by evaluating the
        // polynomial in dc and seeding (zr, zi) = ref[k] + δ.
        //
        // dc must be computed via DD subtraction. At deep zoom the pixel
        // offset (~3e-15 at z=1e14) is well below ULP(CenterX) (~6e-17 at
        // |x|=0.5), so naive `cx.Hi - _refCxHi` rounds adjacent pixels to
        // identical doubles, producing identical SA δ and visible pixelation.
        // DD subtraction preserves the offset in the resulting Hi limb.
        var sa = _sa;
        if (sa != null && sa.SafeMax >= 16 && !DisableAcceleration && !DisableSeriesApproximation)
        {
            DD dcRdd = cx - new DD(_refCxHi, _refCxLo);
            DD dcIdd = cy - new DD(_refCyHi, _refCyLo);
            double dcR = dcRdd.Hi;
            double dcI = dcIdd.Hi;
            int k = sa.FindSkip(dcR, dcI, SaTolerance, maxIter - 1);
            if (k >= 16 && k <= _refOrbitLen)
            {
                sa.EvalDelta(k, dcR, dcI, out double dR, out double dI);
                sa.EvalDDelta(k, dcR, dcI, out double ddR, out double ddI);
                // Seed from full-precision reference orbit limbs. Using only
                // _refZr[k] (Hi limb) drops DD precision of ref_k, so all
                // near-centre pixels start from the same wrong z and converge
                // to one colour at deep zoom — the "growing centre dot" bug.
                zr = new DD(_refZr[k], _refZrLo[k]) + new DD(dR, 0);
                zi = new DD(_refZi[k], _refZiLo[k]) + new DD(dI, 0);
                dr = ddR;
                di = ddI;
                iterStart = k;
            }
        }

        double zrSnapHi = 0, zrSnapLo = 0, ziSnapHi = 0, ziSnapLo = 0;
        int snapInterval = PeriodicitySnapshotStart;
        int snapCounter = 0;

        int iter;
        for (iter = iterStart; iter < maxIter; iter++)
        {
            DD zr2 = zr.Square();
            DD zi2 = zi.Square();
            DD mag2 = zr2 + zi2;
            if (mag2 >= EscapeRadius2) break;

            double newDr = 2.0 * (zr.Hi * dr - zi.Hi * di) + 1.0;
            double newDi = 2.0 * (zr.Hi * di + zi.Hi * dr);
            dr = newDr; di = newDi;

            DD newZi = (zr * zi) * 2.0 + cy;
            DD newZr = zr2 - zi2 + cx;
            zr = newZr; zi = newZi;

            // Periodicity check on full DD limbs — at deep zoom the Lo limbs
            // carry significant cycle-distinguishing bits, so comparing only
            // Hi could yield false positives in tightly packed mini-set
            // interiors.
            if (zr.Hi == zrSnapHi && zr.Lo == zrSnapLo
                && zi.Hi == ziSnapHi && zi.Lo == ziSnapLo)
            { iter = maxIter; break; }
            if (++snapCounter >= snapInterval)
            {
                zrSnapHi = zr.Hi; zrSnapLo = zr.Lo;
                ziSnapHi = zi.Hi; ziSnapLo = zi.Lo;
                snapCounter = 0;
                if (snapInterval < PeriodicitySnapshotMax) snapInterval <<= 1;
            }
        }

        IterationBuffer[idx] = iter;
        FillAuxAndColorHP(idx, iter, maxIter, zr.Hi, zi.Hi, dr, di, colorMap);
    }

    // ── Full-QD per-pixel fallback (used for PT glitches at zoom > QDZoomThreshold) ─
    // DD cannot distinguish adjacent pixels at zoom > ~5e27 (pixel spacing falls below
    // DD precision ~6e-32), causing all glitched pixels in a block to produce identical
    // coordinates and colors. QD (~62 digits) resolves pixels down to zoom ~5e58.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ComputePixelQD<TMap>(QD cx, QD cy, int maxIter, int idx, TMap colorMap)
        where TMap : IColorMap
    {
        // Bulb test — at QD zoom (>1e25) this never fires but the cost is
        // a single compare-and-branch, negligible compared to QD math.
        if (IsInMainCardioidOrBulb(cx.X0, cy.X0))
        {
            IterationBuffer[idx] = maxIter;
            FillAuxAndColorHP(idx, maxIter, maxIter, 0, 0, 1, 0, colorMap);
            return;
        }

        QD zr = QD.Zero, zi = QD.Zero;
        double dr = 1.0, di = 0.0;
        int iterStart = 0;

        // SA prelude — mirror of ComputePixelHP, with QD-precision dc.
        // QD subtraction preserves the pixel offset even at zoom > 1e25 where
        // the offset lives in the X2/X3 limbs; collapsing to X0 after the
        // subtraction yields the small offset at double precision, suitable
        // for SA polynomial input.
        var sa = _sa;
        if (sa != null && sa.SafeMax >= 16 && !DisableAcceleration && !DisableSeriesApproximation)
        {
            QD refCx = new QD(_refCxHi, _refCxLo, _refCx2, _refCx3);
            QD refCy = new QD(_refCyHi, _refCyLo, _refCy2, _refCy3);
            QD dcRqd = cx - refCx;
            QD dcIqd = cy - refCy;
            double dcR = dcRqd.X0;
            double dcI = dcIqd.X0;
            int k = sa.FindSkip(dcR, dcI, SaTolerance, maxIter - 1);
            if (k >= 16 && k <= _refOrbitLen)
            {
                sa.EvalDelta(k, dcR, dcI, out double dR, out double dI);
                sa.EvalDDelta(k, dcR, dcI, out double ddR, out double ddI);
                // Seed from full QD reference orbit. Constructing the QD from
                // only `_refZr[k] + dR` discards X1/X2/X3 of ref_k — at deep
                // zoom (>~1e32) the lost limbs swamp pixel-level differences,
                // collapsing near-centre pixels to a single trajectory and a
                // single colour ("growing centre dot" bug).
                QD refZk = new QD(_refZr[k], _refZrLo[k], _refZrX2[k], _refZrX3[k]);
                QD refIk = new QD(_refZi[k], _refZiLo[k], _refZiX2[k], _refZiX3[k]);
                zr = refZk + new QD(dR);
                zi = refIk + new QD(dI);
                dr = ddR;
                di = ddI;
                iterStart = k;
            }
        }

        double zrSnap0 = 0, zrSnap1 = 0, ziSnap0 = 0, ziSnap1 = 0;
        int snapInterval = PeriodicitySnapshotStart;
        int snapCounter = 0;

        int iter;
        for (iter = iterStart; iter < maxIter; iter++)
        {
            double zrH = zr.X0, ziH = zi.X0;
            if (zrH * zrH + ziH * ziH >= EscapeRadius2) break;

            double newDr = 2.0 * (zrH * dr - ziH * di) + 1.0;
            double newDi = 2.0 * (zrH * di + ziH * dr);
            dr = newDr; di = newDi;

            QD newZi = (zr * zi) * 2.0 + cy;
            QD newZr = zr.Square() - zi.Square() + cx;
            zr = newZr; zi = newZi;

            // Periodicity — comparing X0/X1 limbs is sufficient even at QD
            // zoom because attracting cycles snap to bit-equal X0 bits well
            // before they could diverge on X2/X3.
            if (zr.X0 == zrSnap0 && zr.X1 == zrSnap1
                && zi.X0 == ziSnap0 && zi.X1 == ziSnap1)
            { iter = maxIter; break; }
            if (++snapCounter >= snapInterval)
            {
                zrSnap0 = zr.X0; zrSnap1 = zr.X1;
                ziSnap0 = zi.X0; ziSnap1 = zi.X1;
                snapCounter = 0;
                if (snapInterval < PeriodicitySnapshotMax) snapInterval <<= 1;
            }
        }

        IterationBuffer[idx] = iter;
        FillAuxAndColorHP(idx, iter, maxIter, zr.X0, zi.X0, dr, di, colorMap);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PATH B perturbation theory — reference orbit + double-precision delta
    // ─────────────────────────────────────────────────────────────────────────

    private void ComputeReferenceOrbit(DD cx, DD cy, int maxIter)
    {
        // Cache hit: same center (all 4 QD limbs match — DD path uses limbs 2,3 = 0)
        // and either previous run already escaped (valid for any maxIter) or
        // previous maxIter cap >= current cap.
        bool centerSame = cx.Hi == _refCxHi && cx.Lo == _refCxLo
                       && _refCx2 == 0 && _refCx3 == 0
                       && cy.Hi == _refCyHi && cy.Lo == _refCyLo
                       && _refCy2 == 0 && _refCy3 == 0;
        if (centerSame && (_refCachedEscaped || maxIter <= _refCachedMaxIter))
            return;

        if (_refZr.Length <= maxIter)
        {
            int sz = maxIter + 1;
            _refZr = new double[sz];
            _refZi = new double[sz];
            _refZrLo = new double[sz];
            _refZiLo = new double[sz];
            _refZrX2 = new double[sz];
            _refZiX2 = new double[sz];
            _refZrX3 = new double[sz];
            _refZiX3 = new double[sz];
        }
        DD zr = DD.Zero, zi = DD.Zero;
        int n;
        for (n = 0; n < maxIter; n++)
        {
            _refZr[n] = zr.Hi;  _refZrLo[n] = zr.Lo;
            _refZi[n] = zi.Hi;  _refZiLo[n] = zi.Lo;
            _refZrX2[n] = 0;    _refZrX3[n] = 0;
            _refZiX2[n] = 0;    _refZiX3[n] = 0;
            if (zr.Hi * zr.Hi + zi.Hi * zi.Hi >= EscapeRadius2) break;
            DD newZi = (zr * zi) * 2.0 + cy;
            zr = zr.Square() - zi.Square() + cx;
            zi = newZi;
        }
        _refZr[n] = zr.Hi;  _refZrLo[n] = zr.Lo;
        _refZi[n] = zi.Hi;  _refZiLo[n] = zi.Lo;
        _refZrX2[n] = 0;    _refZrX3[n] = 0;
        _refZiX2[n] = 0;    _refZiX3[n] = 0;
        _refOrbitLen = n;  // == maxIter when centre is interior
        _refOrbitGen++;

        _refCxHi = cx.Hi; _refCxLo = cx.Lo; _refCx2 = 0; _refCx3 = 0;
        _refCyHi = cy.Hi; _refCyLo = cy.Lo; _refCy2 = 0; _refCy3 = 0;
        _refCachedMaxIter = maxIter;
        _refCachedEscaped = n < maxIter;
    }

    // Quad-double reference orbit — engaged at zoom > 1e25. Storage of Z_n
    // remains a single double per slot (the Hi limb), since the per-pixel PT
    // delta loop only consumes Z_n at double precision. The QD math here just
    // ensures Z_n is *correctly rounded* to that double after thousands of
    // iterations from a 62-digit centre.
    private void ComputeReferenceOrbitQD(QD cx, QD cy, int maxIter)
    {
        bool centerSame = cx.X0 == _refCxHi && cx.X1 == _refCxLo
                       && cx.X2 == _refCx2 && cx.X3 == _refCx3
                       && cy.X0 == _refCyHi && cy.X1 == _refCyLo
                       && cy.X2 == _refCy2 && cy.X3 == _refCy3;
        if (centerSame && (_refCachedEscaped || maxIter <= _refCachedMaxIter))
            return;

        if (_refZr.Length <= maxIter)
        {
            int sz = maxIter + 1;
            _refZr = new double[sz];
            _refZi = new double[sz];
            _refZrLo = new double[sz];
            _refZiLo = new double[sz];
            _refZrX2 = new double[sz];
            _refZiX2 = new double[sz];
            _refZrX3 = new double[sz];
            _refZiX3 = new double[sz];
        }
        QD zr = QD.Zero, zi = QD.Zero;
        int n;
        for (n = 0; n < maxIter; n++)
        {
            _refZr[n] = zr.X0;  _refZrLo[n] = zr.X1;  _refZrX2[n] = zr.X2;  _refZrX3[n] = zr.X3;
            _refZi[n] = zi.X0;  _refZiLo[n] = zi.X1;  _refZiX2[n] = zi.X2;  _refZiX3[n] = zi.X3;
            if (zr.X0 * zr.X0 + zi.X0 * zi.X0 >= EscapeRadius2) break;
            QD newZi = (zr * zi) * 2.0 + cy;
            zr = zr.Square() - zi.Square() + cx;
            zi = newZi;
        }
        _refZr[n] = zr.X0;  _refZrLo[n] = zr.X1;  _refZrX2[n] = zr.X2;  _refZrX3[n] = zr.X3;
        _refZi[n] = zi.X0;  _refZiLo[n] = zi.X1;  _refZiX2[n] = zi.X2;  _refZiX3[n] = zi.X3;
        _refOrbitLen = n;
        _refOrbitGen++;

        _refCxHi = cx.X0; _refCxLo = cx.X1; _refCx2 = cx.X2; _refCx3 = cx.X3;
        _refCyHi = cy.X0; _refCyLo = cy.X1; _refCy2 = cy.X2; _refCy3 = cy.X3;
        _refCachedMaxIter = maxIter;
        _refCachedEscaped = n < maxIter;
    }

    // Returns false when the reference orbit was exhausted before this pixel
    // escaped (glitch condition) — caller must fall back to ComputePixelHP.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool ComputePixelPT<TMap>(
        double dcR, double dcI, int maxIter, int idx, TMap colorMap)
        where TMap : IColorMap
    {
        double dr = 0.0, di = 0.0;    // δ_0 = 0
        double drv = 1.0, div = 0.0;  // dz/dc for surface normals (IQ convention)
        int refLen = _refOrbitLen;
        double escZr = 0.0, escZi = 0.0;
        int iter;

        for (iter = 0; iter < maxIter; iter++)
        {
            if (iter > refLen) return false;  // reference exhausted → glitch

            double Zr = _refZr[iter];
            double Zi = _refZi[iter];
            double zr = Zr + dr;
            double zi = Zi + di;

            if (zr * zr + zi * zi >= EscapeRadius2)
            {
                escZr = zr; escZi = zi;
                break;
            }

            // Precision glitch: dr/di absorbed by Z in double arithmetic
            // (|dc| << ULP(Z) at extreme zoom). All pixels share identical
            // escape check → wrong iteration counts. Fall back to DD or QD.
            if (zr == Zr && zi == Zi && (dr != 0.0 || di != 0.0))
                return false;

            double newDrv = 2.0 * (zr * drv - zi * div) + 1.0;
            double newDiv = 2.0 * (zr * div + zi * drv);
            drv = newDrv; div = newDiv;

            // δ_{n+1} = (2·Z_n + δ_n)·δ_n + dc
            double a = 2.0 * Zr + dr;
            double b = 2.0 * Zi + di;
            double newDr = a * dr - b * di + dcR;
            double newDi = a * di + b * dr + dcI;
            dr = newDr; di = newDi;
        }

        IterationBuffer[idx] = iter;
        FillAuxAndColorHP(idx, iter, maxIter, escZr, escZi, drv, div, colorMap);
        return true;
    }

    private void ComputeRowPTScalar<TMap>(
        int y, double scale, int maxIter, int rowBase, TMap colorMap)
        where TMap : IColorMap
    {
        double halfH = Height * 0.5;
        double halfW = Width * 0.5;
        double dcY = (y - halfH) * scale;
        bool useQD = Zoom > QDZoomThreshold;
        DD cy_dd = DD.FromCenterOffset(new DD(CenterY, CenterYLo), y - halfH, scale);
        QD cy_qd = useQD
            ? QD.FromCenterOffset(new QD(CenterY, CenterYLo, CenterY2, CenterY3), y - halfH, scale)
            : QD.Zero;

        for (int x = 0; x < Width; x++)
        {
            double dcX = (x - halfW) * scale;
            int idx = rowBase + x;
            if (!ComputePixelPT(dcX, dcY, maxIter, idx, colorMap))
            {
                if (useQD)
                {
                    QD cx_qd = QD.FromCenterOffset(
                        new QD(CenterX, CenterXLo, CenterX2, CenterX3), x - halfW, scale);
                    ComputePixelQD(cx_qd, cy_qd, maxIter, idx, colorMap);
                }
                else
                {
                    DD cx_dd = DD.FromCenterOffset(new DD(CenterX, CenterXLo), x - halfW, scale);
                    ComputePixelHP(cx_dd, cy_dd, maxIter, idx, colorMap);
                }
            }
        }
    }

    private void ComputeRowPT4<TMap>(
        int y, double scale, int maxIter, int rowBase, TMap colorMap)
        where TMap : IColorMap
    {
        double halfW = Width * 0.5;
        double halfH = Height * 0.5;
        double dcY = (y - halfH) * scale;
        bool useQD = Zoom > QDZoomThreshold;
        DD cy_dd = DD.FromCenterOffset(new DD(CenterY, CenterYLo), y - halfH, scale);
        QD cy_qd = useQD
            ? QD.FromCenterOffset(new QD(CenterY, CenterYLo, CenterY2, CenterY3), y - halfH, scale)
            : QD.Zero;

        var er2v = Vector256.Create(EscapeRadius2);
        var one = Vector256.Create(1.0);
        var two = Vector256.Create(2.0);
        var dcYv = Vector256.Create(dcY);
        int refLen = _refOrbitLen;
        var bla = DisableAcceleration ? null : _blaTable;
        var sa = (DisableAcceleration || DisableSeriesApproximation) ? null : _sa;
        long rowBlaSkips = 0;
        long rowBlaIterSaved = 0;
        long rowSaApplied = 0;
        long rowSaIterSaved = 0;
        int x = 0;

        // Hoisted out of column loop to avoid CA2014 stack growth.
        Span<double> icSpan  = stackalloc double[4];
        Span<double> drSpan  = stackalloc double[4];
        Span<double> diSpan  = stackalloc double[4];
        Span<double> drvSpan = stackalloc double[4];
        Span<double> divSpan = stackalloc double[4];

        for (; x + 4 <= Width; x += 4)
        {
            double dcR0 = (x - halfW) * scale;
            double dcR1 = (x + 1 - halfW) * scale;
            double dcR2 = (x + 2 - halfW) * scale;
            double dcR3 = (x + 3 - halfW) * scale;
            var dcRv = Vector256.Create(dcR0, dcR1, dcR2, dcR3);

            var dr = Vector256<double>.Zero;
            var di = Vector256<double>.Zero;
            var drv = one;
            var div = Vector256<double>.Zero;
            var iterCount = Vector256<double>.Zero;
            int escapedMask = 0;
            bool glitched = false;
            int iterStart = 0;

            // ── SA prelude ─────────────────────────────────────────────────
            // Skip the first iterations from z=0 by evaluating the third-order
            // polynomial in dc. Lane-uniform k: use min skip across lanes
            // (largest dc has smallest valid skip; conservative — every lane
            // is safe at that k). Per-lane scalar polynomial eval to seed δ
            // and the chain-rule derivative.
            if (sa != null && sa.SafeMax >= 4)
            {
                int k0 = sa.FindSkip(dcR0, dcY, SaTolerance, maxIter - 1);
                int k1 = sa.FindSkip(dcR1, dcY, SaTolerance, maxIter - 1);
                int k2 = sa.FindSkip(dcR2, dcY, SaTolerance, maxIter - 1);
                int k3 = sa.FindSkip(dcR3, dcY, SaTolerance, maxIter - 1);
                int k = k0;
                if (k1 < k) k = k1;
                if (k2 < k) k = k2;
                if (k3 < k) k = k3;
                if (k >= 4)   // amortise polynomial cost only when skip is meaningful
                {
                    sa.EvalDelta(k, dcR0, dcY, out double d0r, out double d0i);
                    sa.EvalDelta(k, dcR1, dcY, out double d1r, out double d1i);
                    sa.EvalDelta(k, dcR2, dcY, out double d2r, out double d2i);
                    sa.EvalDelta(k, dcR3, dcY, out double d3r, out double d3i);
                    dr = Vector256.Create(d0r, d1r, d2r, d3r);
                    di = Vector256.Create(d0i, d1i, d2i, d3i);

                    sa.EvalDDelta(k, dcR0, dcY, out double v0r, out double v0i);
                    sa.EvalDDelta(k, dcR1, dcY, out double v1r, out double v1i);
                    sa.EvalDDelta(k, dcR2, dcY, out double v2r, out double v2i);
                    sa.EvalDDelta(k, dcR3, dcY, out double v3r, out double v3i);
                    drv = Vector256.Create(v0r, v1r, v2r, v3r);
                    div = Vector256.Create(v0i, v1i, v2i, v3i);

                    iterCount = Vector256.Create((double)k);
                    iterStart = k;
                    rowSaApplied += 4;
                    rowSaIterSaved += 4L * k;
                }
            }

            for (int iter = iterStart; iter < maxIter; iter++)
            {
                if (iter > refLen) { glitched = true; break; }

                // ── BLA skip attempt (per-lane via active-mask blending) ───
                // Lookup uses max |δ|² over ACTIVE lanes only — escaped lanes
                // have frozen (possibly large) δ that would block any skip.
                // Escaped lanes are preserved via BlendActive, so the skip
                // continues to compound even after individual lanes diverge.
                if (bla != null)
                {
                    int activeBla = ~escapedMask & 0b1111;
                    if (activeBla != 0)
                    {
                        var dmag2v = Avx.Add(Avx.Multiply(dr, dr), Avx.Multiply(di, di));
                        double maxActiveDmag2 = HMaxMasked(dmag2v, activeBla);
                        int blaIdx = bla.Lookup(iter, maxActiveDmag2, maxIter);
                        if (blaIdx >= 0)
                        {
                            ref readonly var bEntry = ref bla.Data[blaIdx];
                            if (bEntry.L >= 2)
                            {
                                var aRev = Vector256.Create(bEntry.ARe);
                                var aImv = Vector256.Create(bEntry.AIm);
                                var bRev = Vector256.Create(bEntry.BRe);
                                var bImv = Vector256.Create(bEntry.BIm);

                                // δ' = A·δ + B·dc  (active lanes only)
                                var aDr = Avx.Subtract(Avx.Multiply(aRev, dr), Avx.Multiply(aImv, di));
                                var aDi = Avx.Add(Avx.Multiply(aRev, di), Avx.Multiply(aImv, dr));
                                var bDcR = Avx.Subtract(Avx.Multiply(bRev, dcRv), Avx.Multiply(bImv, dcYv));
                                var bDcI = Avx.Add(Avx.Multiply(bRev, dcYv), Avx.Multiply(bImv, dcRv));
                                var newDrBla = Avx.Add(aDr, bDcR);
                                var newDiBla = Avx.Add(aDi, bDcI);
                                dr = BlendActive(dr, newDrBla, activeBla);
                                di = BlendActive(di, newDiBla, activeBla);

                                // Derivative: linearised d_{n+L} ≈ A·d_n
                                // (drops the +1 constants over L steps —
                                // tiny relative error vs |A|·|d_n| at deep
                                // zoom; affects distance estimate by < 1 px).
                                var aDrv = Avx.Subtract(Avx.Multiply(aRev, drv), Avx.Multiply(aImv, div));
                                var aDiv = Avx.Add(Avx.Multiply(aRev, div), Avx.Multiply(aImv, drv));
                                drv = BlendActive(drv, aDrv, activeBla);
                                div = BlendActive(div, aDiv, activeBla);

                                // iterCount += L only for active lanes.
                                var lVec = Vector256.Create((double)bEntry.L);
                                iterCount = Avx.Add(iterCount, Avx.And(lVec, MaskToVector(activeBla)));
                                iter += bEntry.L - 1;    // for-loop ++iter restores net +L
                                rowBlaSkips++;
                                rowBlaIterSaved += bEntry.L - 1;
                                continue;
                            }
                        }
                    }
                }

                var Zrv = Vector256.Create(_refZr[iter]);
                var Ziv = Vector256.Create(_refZi[iter]);
                var zr = Avx.Add(Zrv, dr);
                var zi = Avx.Add(Ziv, di);

                var mag2 = Avx.Add(Avx.Multiply(zr, zr), Avx.Multiply(zi, zi));
                var escV = Avx.Compare(mag2, er2v,
                    FloatComparisonMode.OrderedGreaterThanOrEqualNonSignaling);
                int newEsc = Avx.MoveMask(escV);

                // Register escapes first so active excludes this iteration's escapes.
                // This matches the scalar convention: iterCount = N for escape at iter N.
                escapedMask |= newEsc;
                int active = ~escapedMask & 0b1111;
                if (active == 0) break;

                // Precision glitch at extreme zoom: δ absorbed by Z in double arithmetic.
                // Only check in QD range where dc << ULP(Z) always holds.
                if (useQD && !glitched)
                {
                    double Zr_s = _refZr[iter], Zi_s = _refZi[iter];
                    for (int k = 0; k < 4; k++)
                    {
                        double drk = dr.GetElement(k), dik = di.GetElement(k);
                        if ((drk != 0.0 || dik != 0.0) &&
                            zr.GetElement(k) == Zr_s && zi.GetElement(k) == Zi_s)
                        { glitched = true; break; }
                    }
                    if (glitched) break;
                }

                iterCount = Avx.Add(iterCount, Avx.And(one, MaskToVector(active)));

                var newDrv = Avx.Add(
                    Avx.Multiply(two, Avx.Subtract(Avx.Multiply(zr, drv), Avx.Multiply(zi, div))),
                    one);
                var newDiv = Avx.Multiply(two,
                    Avx.Add(Avx.Multiply(zr, div), Avx.Multiply(zi, drv)));
                drv = BlendActive(drv, newDrv, active);
                div = BlendActive(div, newDiv, active);

                // δ_{n+1} = (2·Z_n + δ_n)·δ_n + dc
                var a = Avx.Add(Avx.Multiply(two, Zrv), dr);
                var b = Avx.Add(Avx.Multiply(two, Ziv), di);
                var newDr = Avx.Add(Avx.Subtract(Avx.Multiply(a, dr), Avx.Multiply(b, di)), dcRv);
                var newDi = Avx.Add(Avx.Add(Avx.Multiply(a, di), Avx.Multiply(b, dr)), dcYv);
                dr = BlendActive(dr, newDr, active);
                di = BlendActive(di, newDi, active);
            }

            // Bulk-extract lanes via Vector256.CopyTo (one vmovupd per vector)
            // instead of five GetElement(k) calls per k (each costs a lane-
            // extract). ~5× fewer cross-domain transitions for the tail.
            // Scratch spans hoisted above loop (see CA2014 fix).
            iterCount.CopyTo(icSpan);
            dr.CopyTo(drSpan);
            di.CopyTo(diSpan);
            drv.CopyTo(drvSpan);
            div.CopyTo(divSpan);

            for (int k = 0; k < 4; k++)
            {
                int idx = rowBase + x + k;
                // Glitched pixels that never escaped need full fallback.
                // At zoom > QDZoomThreshold, DD cannot distinguish adjacent pixels
                // (pixel spacing ~2e-32 < DD precision ~6e-32); use QD instead.
                if (glitched && ((escapedMask >> k) & 1) == 0)
                {
                    if (useQD)
                    {
                        QD cx_qd = QD.FromCenterOffset(
                            new QD(CenterX, CenterXLo, CenterX2, CenterX3), x + k - halfW, scale);
                        ComputePixelQD(cx_qd, cy_qd, maxIter, idx, colorMap);
                    }
                    else
                    {
                        DD cx_dd = DD.FromCenterOffset(
                            new DD(CenterX, CenterXLo), x + k - halfW, scale);
                        ComputePixelHP(cx_dd, cy_dd, maxIter, idx, colorMap);
                    }
                    continue;
                }
                int iters = (int)icSpan[k];
                // Reconstruct z at escape: Z_iters + δ_iters (δ frozen when lane escaped)
                double zrF = (iters <= refLen ? _refZr[iters] : 0.0) + drSpan[k];
                double ziF = (iters <= refLen ? _refZi[iters] : 0.0) + diSpan[k];
                IterationBuffer[idx] = iters;
                FillAuxAndColorHP(idx, iters, maxIter, zrF, ziF,
                    drvSpan[k], divSpan[k], colorMap);
            }
        }

        // Scalar tail (0–3 remaining pixels)
        for (; x < Width; x++)
        {
            double dcX = (x - halfW) * scale;
            int idx = rowBase + x;
            if (!ComputePixelPT(dcX, dcY, maxIter, idx, colorMap))
            {
                if (useQD)
                {
                    QD cx_qd = QD.FromCenterOffset(
                        new QD(CenterX, CenterXLo, CenterX2, CenterX3), x - halfW, scale);
                    ComputePixelQD(cx_qd, cy_qd, maxIter, idx, colorMap);
                }
                else
                {
                    DD cx_dd = DD.FromCenterOffset(new DD(CenterX, CenterXLo), x - halfW, scale);
                    ComputePixelHP(cx_dd, cy_dd, maxIter, idx, colorMap);
                }
            }
        }

        if (rowBlaSkips != 0)
        {
            Interlocked.Add(ref _blaSkipsTotal, rowBlaSkips);
            Interlocked.Add(ref _blaIterSkippedTotal, rowBlaIterSaved);
        }
        if (rowSaApplied != 0)
        {
            Interlocked.Add(ref _saAppliedTotal, rowSaApplied);
            Interlocked.Add(ref _saIterSkippedTotal, rowSaIterSaved);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PATH B-512 — 8-wide AVX-512 perturbation, mirrors ComputeRowPT4
    // Cross-platform Vector512 API: RyuJIT emits vaddpd/vmulpd zmm on
    // AVX512F-capable CPUs. SA prelude + per-lane BLA carry over unchanged
    // (per-lane scalar polynomial eval just runs 8 times instead of 4).
    // ─────────────────────────────────────────────────────────────────────────

    private void ComputeRowPT8<TMap>(
        int y, double scale, int maxIter, int rowBase, TMap colorMap)
        where TMap : IColorMap
    {
        double halfW = Width * 0.5;
        double halfH = Height * 0.5;
        double dcY = (y - halfH) * scale;
        bool useQD = Zoom > QDZoomThreshold;
        DD cy_dd = DD.FromCenterOffset(new DD(CenterY, CenterYLo), y - halfH, scale);
        QD cy_qd = useQD
            ? QD.FromCenterOffset(new QD(CenterY, CenterYLo, CenterY2, CenterY3), y - halfH, scale)
            : QD.Zero;

        var er2v = Vector512.Create(EscapeRadius2);
        var one = Vector512.Create(1.0);
        var two = Vector512.Create(2.0);
        var dcYv = Vector512.Create(dcY);
        int refLen = _refOrbitLen;
        var bla = DisableAcceleration ? null : _blaTable;
        var sa = (DisableAcceleration || DisableSeriesApproximation) ? null : _sa;
        long rowBlaSkips = 0;
        long rowBlaIterSaved = 0;
        long rowSaApplied = 0;
        long rowSaIterSaved = 0;
        int x = 0;

        // Hoisted out of column loop to avoid CA2014 stack growth.
        Span<double> icSpan  = stackalloc double[8];
        Span<double> drSpan  = stackalloc double[8];
        Span<double> diSpan  = stackalloc double[8];
        Span<double> drvSpan = stackalloc double[8];
        Span<double> divSpan = stackalloc double[8];

        for (; x + 8 <= Width; x += 8)
        {
            double dcR0 = (x - halfW) * scale;
            double dcR1 = (x + 1 - halfW) * scale;
            double dcR2 = (x + 2 - halfW) * scale;
            double dcR3 = (x + 3 - halfW) * scale;
            double dcR4 = (x + 4 - halfW) * scale;
            double dcR5 = (x + 5 - halfW) * scale;
            double dcR6 = (x + 6 - halfW) * scale;
            double dcR7 = (x + 7 - halfW) * scale;
            var dcRv = Vector512.Create(dcR0, dcR1, dcR2, dcR3, dcR4, dcR5, dcR6, dcR7);

            var dr = Vector512<double>.Zero;
            var di = Vector512<double>.Zero;
            var drv = one;
            var div = Vector512<double>.Zero;
            var iterCount = Vector512<double>.Zero;
            int escapedMask = 0;
            bool glitched = false;
            int iterStart = 0;

            // ── SA prelude (8 lanes) ───────────────────────────────────────
            if (sa != null && sa.SafeMax >= 4)
            {
                int k0 = sa.FindSkip(dcR0, dcY, SaTolerance, maxIter - 1);
                int k1 = sa.FindSkip(dcR1, dcY, SaTolerance, maxIter - 1);
                int k2 = sa.FindSkip(dcR2, dcY, SaTolerance, maxIter - 1);
                int k3 = sa.FindSkip(dcR3, dcY, SaTolerance, maxIter - 1);
                int k4 = sa.FindSkip(dcR4, dcY, SaTolerance, maxIter - 1);
                int k5 = sa.FindSkip(dcR5, dcY, SaTolerance, maxIter - 1);
                int k6 = sa.FindSkip(dcR6, dcY, SaTolerance, maxIter - 1);
                int k7 = sa.FindSkip(dcR7, dcY, SaTolerance, maxIter - 1);
                int k = k0;
                if (k1 < k) k = k1;
                if (k2 < k) k = k2;
                if (k3 < k) k = k3;
                if (k4 < k) k = k4;
                if (k5 < k) k = k5;
                if (k6 < k) k = k6;
                if (k7 < k) k = k7;
                if (k >= 4)
                {
                    sa.EvalDelta(k, dcR0, dcY, out double d0r, out double d0i);
                    sa.EvalDelta(k, dcR1, dcY, out double d1r, out double d1i);
                    sa.EvalDelta(k, dcR2, dcY, out double d2r, out double d2i);
                    sa.EvalDelta(k, dcR3, dcY, out double d3r, out double d3i);
                    sa.EvalDelta(k, dcR4, dcY, out double d4r, out double d4i);
                    sa.EvalDelta(k, dcR5, dcY, out double d5r, out double d5i);
                    sa.EvalDelta(k, dcR6, dcY, out double d6r, out double d6i);
                    sa.EvalDelta(k, dcR7, dcY, out double d7r, out double d7i);
                    dr = Vector512.Create(d0r, d1r, d2r, d3r, d4r, d5r, d6r, d7r);
                    di = Vector512.Create(d0i, d1i, d2i, d3i, d4i, d5i, d6i, d7i);

                    sa.EvalDDelta(k, dcR0, dcY, out double v0r, out double v0i);
                    sa.EvalDDelta(k, dcR1, dcY, out double v1r, out double v1i);
                    sa.EvalDDelta(k, dcR2, dcY, out double v2r, out double v2i);
                    sa.EvalDDelta(k, dcR3, dcY, out double v3r, out double v3i);
                    sa.EvalDDelta(k, dcR4, dcY, out double v4r, out double v4i);
                    sa.EvalDDelta(k, dcR5, dcY, out double v5r, out double v5i);
                    sa.EvalDDelta(k, dcR6, dcY, out double v6r, out double v6i);
                    sa.EvalDDelta(k, dcR7, dcY, out double v7r, out double v7i);
                    drv = Vector512.Create(v0r, v1r, v2r, v3r, v4r, v5r, v6r, v7r);
                    div = Vector512.Create(v0i, v1i, v2i, v3i, v4i, v5i, v6i, v7i);

                    iterCount = Vector512.Create((double)k);
                    iterStart = k;
                    rowSaApplied += 8;
                    rowSaIterSaved += 8L * k;
                }
            }

            for (int iter = iterStart; iter < maxIter; iter++)
            {
                if (iter > refLen) { glitched = true; break; }

                // ── BLA skip (8-lane, per-lane via active mask) ────────────
                if (bla != null)
                {
                    int activeBla = ~escapedMask & 0xFF;
                    if (activeBla != 0)
                    {
                        var dmag2v = dr * dr + di * di;
                        double maxActiveDmag2 = HMaxMasked8(dmag2v, activeBla);
                        int blaIdx = bla.Lookup(iter, maxActiveDmag2, maxIter);
                        if (blaIdx >= 0)
                        {
                            ref readonly var bEntry = ref bla.Data[blaIdx];
                            if (bEntry.L >= 2)
                            {
                                var aRev = Vector512.Create(bEntry.ARe);
                                var aImv = Vector512.Create(bEntry.AIm);
                                var bRev = Vector512.Create(bEntry.BRe);
                                var bImv = Vector512.Create(bEntry.BIm);

                                var aDr = aRev * dr - aImv * di;
                                var aDi = aRev * di + aImv * dr;
                                var bDcR = bRev * dcRv - bImv * dcYv;
                                var bDcI = bRev * dcYv + bImv * dcRv;
                                dr = BlendActive512(dr, aDr + bDcR, activeBla);
                                di = BlendActive512(di, aDi + bDcI, activeBla);

                                var aDrv = aRev * drv - aImv * div;
                                var aDiv = aRev * div + aImv * drv;
                                drv = BlendActive512(drv, aDrv, activeBla);
                                div = BlendActive512(div, aDiv, activeBla);

                                var lVec = Vector512.Create((double)bEntry.L);
                                iterCount += lVec & MaskToVector512(activeBla);
                                iter += bEntry.L - 1;
                                rowBlaSkips++;
                                rowBlaIterSaved += bEntry.L - 1;
                                continue;
                            }
                        }
                    }
                }

                var Zrv = Vector512.Create(_refZr[iter]);
                var Ziv = Vector512.Create(_refZi[iter]);
                var zr = Zrv + dr;
                var zi = Ziv + di;

                var mag2 = zr * zr + zi * zi;
                var escV = Vector512.GreaterThanOrEqual(mag2, er2v);
                int newEsc = (int)escV.ExtractMostSignificantBits();

                escapedMask |= newEsc;
                int active = ~escapedMask & 0xFF;
                if (active == 0) break;

                if (useQD && !glitched)
                {
                    double Zr_s = _refZr[iter], Zi_s = _refZi[iter];
                    for (int k = 0; k < 8; k++)
                    {
                        double drk = dr.GetElement(k), dik = di.GetElement(k);
                        if ((drk != 0.0 || dik != 0.0) &&
                            zr.GetElement(k) == Zr_s && zi.GetElement(k) == Zi_s)
                        { glitched = true; break; }
                    }
                    if (glitched) break;
                }

                iterCount += one & MaskToVector512(active);

                var newDrv = two * (zr * drv - zi * div) + one;
                var newDiv = two * (zr * div + zi * drv);
                drv = BlendActive512(drv, newDrv, active);
                div = BlendActive512(div, newDiv, active);

                // δ_{n+1} = (2·Z_n + δ_n)·δ_n + dc
                var a = two * Zrv + dr;
                var b = two * Ziv + di;
                var newDr = a * dr - b * di + dcRv;
                var newDi = a * di + b * dr + dcYv;
                dr = BlendActive512(dr, newDr, active);
                di = BlendActive512(di, newDi, active);
            }

            // Scratch spans hoisted above loop (see CA2014 fix).
            iterCount.CopyTo(icSpan);
            dr.CopyTo(drSpan);
            di.CopyTo(diSpan);
            drv.CopyTo(drvSpan);
            div.CopyTo(divSpan);

            for (int k = 0; k < 8; k++)
            {
                int idx = rowBase + x + k;
                if (glitched && ((escapedMask >> k) & 1) == 0)
                {
                    if (useQD)
                    {
                        QD cx_qd = QD.FromCenterOffset(
                            new QD(CenterX, CenterXLo, CenterX2, CenterX3), x + k - halfW, scale);
                        ComputePixelQD(cx_qd, cy_qd, maxIter, idx, colorMap);
                    }
                    else
                    {
                        DD cx_dd = DD.FromCenterOffset(
                            new DD(CenterX, CenterXLo), x + k - halfW, scale);
                        ComputePixelHP(cx_dd, cy_dd, maxIter, idx, colorMap);
                    }
                    continue;
                }
                int iters = (int)icSpan[k];
                double zrF = (iters <= refLen ? _refZr[iters] : 0.0) + drSpan[k];
                double ziF = (iters <= refLen ? _refZi[iters] : 0.0) + diSpan[k];
                IterationBuffer[idx] = iters;
                FillAuxAndColorHP(idx, iters, maxIter, zrF, ziF,
                    drvSpan[k], divSpan[k], colorMap);
            }
        }

        // Scalar tail (0–7 remaining pixels)
        for (; x < Width; x++)
        {
            double dcX = (x - halfW) * scale;
            int idx = rowBase + x;
            if (!ComputePixelPT(dcX, dcY, maxIter, idx, colorMap))
            {
                if (useQD)
                {
                    QD cx_qd = QD.FromCenterOffset(
                        new QD(CenterX, CenterXLo, CenterX2, CenterX3), x - halfW, scale);
                    ComputePixelQD(cx_qd, cy_qd, maxIter, idx, colorMap);
                }
                else
                {
                    DD cx_dd = DD.FromCenterOffset(new DD(CenterX, CenterXLo), x - halfW, scale);
                    ComputePixelHP(cx_dd, cy_dd, maxIter, idx, colorMap);
                }
            }
        }

        if (rowBlaSkips != 0)
        {
            Interlocked.Add(ref _blaSkipsTotal, rowBlaSkips);
            Interlocked.Add(ref _blaIterSkippedTotal, rowBlaIterSaved);
        }
        if (rowSaApplied != 0)
        {
            Interlocked.Add(ref _saAppliedTotal, rowSaApplied);
            Interlocked.Add(ref _saIterSkippedTotal, rowSaIterSaved);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Histogram equalization — adaptive contrast post-pass
    // ─────────────────────────────────────────────────────────────────────────
    //
    // Builds a CDF over the smooth iteration distribution of escaped pixels,
    // then re-samples the active color map using an iteration value blended
    // between the original linear value and the equalized value.
    //
    //   strength = 0.0 → identity (recolor matches inline color pass)
    //   strength = 1.0 → full equalization (iteration density flattened)
    //
    // Operates on the SmoothBuffer / IterationBuffer that Calculate() already
    // fills, so it can be re-run at any strength without recomputing the
    // fractal.  Always overwrites ColorBuffer from scratch using the current
    // ColorMap, so successive calls with different strengths compose correctly.
    public void ApplyHistogramEqualization(double strength)
    {
        if (!BuildHistogramCdf(out double[]? cdf, out int bins, out int sourceMaxIter))
        {
            // No escaped pixels — fall back to plain recolor.
            RecolorFromBuffers();
            return;
        }
        ApplyHistogramEqualizationWithCdf(cdf!, bins, sourceMaxIter, strength);
    }

    /// <summary>
    /// Builds the equalization CDF for the current SmoothBuffer/IterationBuffer
    /// without applying it. Returns false (and zeroed outputs) when the view
    /// has no escaped pixels — caller should treat that as the identity case.
    /// Used by the video path to lock the CDF for the duration of a leg so
    /// per-frame palette mapping does not flicker as image statistics drift.
    /// </summary>
    public bool BuildHistogramCdf(out double[]? cdf, out int bins, out int sourceMaxIter)
    {
        cdf = null;
        bins = 0;
        sourceMaxIter = MaxIterations;

        int w = Width, h = Height;
        int n = w * h;
        int maxIter = MaxIterations;
        if (n == 0 || maxIter <= 0) return false;

        bins = Math.Min(2048, Math.Max(256, maxIter));
        int[] hist = new int[bins];
        int totalEscaped = 0;
        float invMax = 1.0f / maxIter;

        for (int i = 0; i < n; i++)
        {
            if (IterationBuffer[i] >= maxIter) continue;
            float s = SmoothBuffer[i];
            float t = s * invMax;
            if (t < 0f) t = 0f; else if (t > 0.9999999f) t = 0.9999999f;
            int b = (int)(t * bins);
            hist[b]++;
            totalEscaped++;
        }

        if (totalEscaped == 0) return false;

        cdf = new double[bins];
        long cum = 0;
        double invTotal = 1.0 / totalEscaped;
        for (int i = 0; i < bins; i++)
        {
            cum += hist[i];
            cdf[i] = cum * invTotal;
        }
        return true;
    }

    /// <summary>
    /// Applies a previously-built equalization CDF to the current buffers.
    /// The smooth-iter → bin lookup is normalized against <paramref name="sourceMaxIter"/>
    /// (MaxIterations at build time) so a locked CDF stays valid across frames
    /// whose MaxIterations differs by tier auto-promote — pixels above the
    /// build-time iter ceiling fall into the last bin (saturating, no flicker).
    /// </summary>
    public void ApplyHistogramEqualizationWithCdf(double[] cdf, int bins, int sourceMaxIter, double strength)
        => ApplyHistogramEqualizationWithCdf(cdf, bins, sourceMaxIter, strength, 0.0, out _, out _);

    public void ApplyHistogramEqualizationWithCdf(double[] cdf, int bins, int sourceMaxIter, double strength, double ditherIterStrength)
        => ApplyHistogramEqualizationWithCdf(cdf, bins, sourceMaxIter, strength, ditherIterStrength, out _, out _);

    /// <summary>
    /// As above, but additionally applies a stable per-pixel spatial dither
    /// (in iteration units) to the smooth-iter value before palette lookup.
    /// Used by the video path to blur band boundaries so the per-frame shift
    /// across them becomes less visible. The dither is a function of (x, y)
    /// only, so it introduces no new temporal noise.
    ///
    /// Returns the count of escaped pixels and the count that saturated to the
    /// last CDF bin so the caller can detect when the locked CDF has drifted
    /// out of range (e.g. after tier auto-promote produced smooth-iter values
    /// above the build-time sourceMaxIter) and trigger a rebuild.
    /// </summary>
    public void ApplyHistogramEqualizationWithCdf(double[] cdf, int bins, int sourceMaxIter, double strength, double ditherIterStrength, out long escapedCount, out long saturatedCount)
    {
        escapedCount = 0;
        saturatedCount = 0;
        if (cdf == null || bins <= 0 || sourceMaxIter <= 0) return;
        if (strength < 0.0) strength = 0.0;
        if (strength > 1.0) strength = 1.0;
        if (ditherIterStrength < 0.0) ditherIterStrength = 0.0;

        int w = Width, h = Height;
        int maxIter = MaxIterations;
        if (w == 0 || h == 0 || maxIter <= 0) return;

        ColorMap.MaxIterations = maxIter;
        if (ColorMap is IColorMapWithPixelScale pxsEq) pxsEq.PixelScale = LastPixelScale;
        bool handlesInSet = ColorMap is IColorMapHandlesInSet;

        float invMax = 1.0f / maxIter;             // for current-frame coloring
        float invMaxSrc = 1.0f / sourceMaxIter;    // for CDF bin lookup
        int lastBin = bins - 1;
        float ditherIter = (float)ditherIterStrength;

        // Per-row counters then summed at the end to avoid contended atomics
        // in the hot loop.
        long[] rowEscaped = new long[h];
        long[] rowSaturated = new long[h];

        _po.CancellationToken = CancellationToken.None;
        var po = _po;
        ParallelForRows(0, h, po, y =>
        {
            int rowBase = y * w;
            long esc = 0;
            long sat = 0;
            for (int x = 0; x < w; x++)
            {
                int idx = rowBase + x;
                int iters = IterationBuffer[idx];
                if (iters >= maxIter)
                {
                    if (handlesInSet)
                    {
                        ColorBuffer[idx] = (uint)ColorMap.Map(
                            0f, 0f, maxIter,
                            0f, 0f, 0f, 0f, 0f, 0f);
                    }
                    else
                    {
                        ColorBuffer[idx] = ColorMap.InSetColor;
                    }
                    continue;
                }
                esc++;
                float s = SmoothBuffer[idx];
                float tLin = s * invMax;
                float tLinC = tLin < 0f ? 0f : (tLin > 0.9999999f ? 0.9999999f : tLin);

                float tLookupRaw = s * invMaxSrc;
                float tLookup = tLookupRaw;
                if (tLookup < 0f) tLookup = 0f;
                else if (tLookup > 0.9999999f) tLookup = 0.9999999f;
                int b = (int)(tLookup * bins);
                if (b > lastBin) b = lastBin;
                // A raw lookup at or above 1.0 means s exceeded the locked
                // sourceMaxIter — the pixel is being forced into the last bin
                // rather than mapped by its real distribution position.
                if (tLookupRaw >= 0.9999999f) sat++;

                double tEq = cdf[b];
                double tBlend = tLinC + (tEq - tLinC) * strength;
                float smoothEq = (float)(tBlend * maxIter);
                if (ditherIter > 0f)
                    smoothEq += SpatialDither(x, y) * ditherIter;
                int iterArgEq = handlesInSet ? iters : maxIter;
                ColorBuffer[idx] = (uint)ColorMap.Map(
                    smoothEq, DistanceBuffer[idx], iterArgEq,
                    NormalXBuffer[idx], NormalYBuffer[idx],
                    FinalZrBuffer[idx], FinalZiBuffer[idx],
                    FinalDrBuffer[idx], FinalDiBuffer[idx]);
            }
            rowEscaped[y] = esc;
            rowSaturated[y] = sat;
        });

        long totalEsc = 0;
        long totalSat = 0;
        for (int i = 0; i < h; i++) { totalEsc += rowEscaped[i]; totalSat += rowSaturated[i]; }
        escapedCount = totalEsc;
        saturatedCount = totalSat;
    }

    /// <summary>
    /// Recolors the ColorBuffer using the current SmoothBuffer + a stable
    /// spatial dither added to the smooth-iter value in iteration units.
    /// Used by the video path when band-edge dither is enabled but histogram
    /// equalization is not.
    /// </summary>
    public void ApplyBandDitherRecolor(double ditherIterStrength)
    {
        if (ditherIterStrength <= 0.0) { RecolorFromBuffers(); return; }

        int w = Width, h = Height;
        int maxIter = MaxIterations;
        if (w == 0 || h == 0 || maxIter <= 0) return;

        ColorMap.MaxIterations = maxIter;
        if (ColorMap is IColorMapWithPixelScale pxs0) pxs0.PixelScale = LastPixelScale;
        bool handlesInSet = ColorMap is IColorMapHandlesInSet;
        float ditherIter = (float)ditherIterStrength;

        _po.CancellationToken = CancellationToken.None;
        var po = _po;
        ParallelForRows(0, h, po, y =>
        {
            int rowBase = y * w;
            for (int x = 0; x < w; x++)
            {
                int idx = rowBase + x;
                int iters = IterationBuffer[idx];
                if (iters >= maxIter)
                {
                    if (handlesInSet)
                    {
                        ColorBuffer[idx] = (uint)ColorMap.Map(
                            0f, 0f, maxIter,
                            0f, 0f, 0f, 0f, 0f, 0f);
                    }
                    else
                    {
                        ColorBuffer[idx] = ColorMap.InSetColor;
                    }
                    continue;
                }
                float s = SmoothBuffer[idx] + SpatialDither(x, y) * ditherIter;
                int iterArg = handlesInSet ? iters : maxIter;
                ColorBuffer[idx] = (uint)ColorMap.Map(
                    s, DistanceBuffer[idx], iterArg,
                    NormalXBuffer[idx], NormalYBuffer[idx],
                    FinalZrBuffer[idx], FinalZiBuffer[idx],
                    FinalDrBuffer[idx], FinalDiBuffer[idx]);
            }
        });
    }

    // Stable spatial hash → [-0.5, 0.5). Pure function of (x, y), so the
    // dither pattern locks to image space and adds no temporal noise across
    // frames at a given pixel.
    private static float SpatialDither(int x, int y)
    {
        uint h = unchecked((uint)(x * 73856093) ^ (uint)(y * 19349663));
        h ^= h >> 13;
        h *= 0x5bd1e995u;
        h ^= h >> 15;
        return ((h & 0xFFFFu) * (1.0f / 65535.0f)) - 0.5f;
    }

    // Recompute ColorBuffer from filled aux buffers without changing iteration
    // values.  Used as the strength=0 / no-escape fast path of equalization.
    private void RecolorFromBuffers()
    {
        int w = Width, h = Height;
        int maxIter = MaxIterations;
        ColorMap.MaxIterations = maxIter;
        if (ColorMap is IColorMapWithPixelScale pxs) pxs.PixelScale = LastPixelScale;
        bool handlesInSet = ColorMap is IColorMapHandlesInSet;
        _po.CancellationToken = CancellationToken.None;
        var po = _po;
        ParallelForRows(0, h, po, y =>
        {
            int rowBase = y * w;
            for (int x = 0; x < w; x++)
            {
                int idx = rowBase + x;
                int iters = IterationBuffer[idx];
                if (iters >= maxIter)
                {
                    if (handlesInSet)
                    {
                        ColorBuffer[idx] = (uint)ColorMap.Map(
                            0f, 0f, maxIter,
                            0f, 0f, 0f, 0f, 0f, 0f);
                    }
                    else
                    {
                        ColorBuffer[idx] = ColorMap.InSetColor;
                    }
                }
                else
                {
                    int iterArg = handlesInSet ? iters : maxIter;
                    ColorBuffer[idx] = (uint)ColorMap.Map(
                        SmoothBuffer[idx], DistanceBuffer[idx], iterArg,
                        NormalXBuffer[idx], NormalYBuffer[idx],
                        FinalZrBuffer[idx], FinalZiBuffer[idx],
                        FinalDrBuffer[idx], FinalDiBuffer[idx]);
                }
            }
        });
    }
}