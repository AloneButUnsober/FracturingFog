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

    public IColorMap ColorMap { get; set; } = new HsvPalette();

    // ── Output buffers ────────────────────────────────────────────────────────

    public int[] IterationBuffer { get; private set; } = Array.Empty<int>();
    public float[] SmoothBuffer { get; private set; } = Array.Empty<float>();
    public float[] DistanceBuffer { get; private set; } = Array.Empty<float>();
    public float[] NormalXBuffer { get; private set; } = Array.Empty<float>();
    public float[] NormalYBuffer { get; private set; } = Array.Empty<float>();
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    // ── Constants ─────────────────────────────────────────────────────────────

    private const double EscapeRadius = 512.0;
    private const double EscapeRadius2 = EscapeRadius * EscapeRadius;
    private static readonly int VecLen = Vector<double>.Count;  // SIMD width (SP path)

    // ── Perturbation theory reference orbit ───────────────────────────────────

    private double[] _refZr = Array.Empty<double>();
    private double[] _refZi = Array.Empty<double>();
    private int _refOrbitLen;

    // Reference-orbit cache: zoom-only / theme-only / pan-stable redraws skip
    // recomputation entirely. Key = (center limbs, maxIter at compute time).
    private double _refCxHi = double.NaN, _refCxLo, _refCx2, _refCx3;
    private double _refCyHi, _refCyLo, _refCy2, _refCy3;
    private int _refCachedMaxIter = -1;
    private bool _refCachedEscaped;  // true when orbit terminated by escape

    // ── Constructor / resize ──────────────────────────────────────────────────

    public MandelbrotCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        if (width < 1 || height < 1)
            throw new ArgumentException("Dimensions must be positive.");
        Width = width;
        Height = height;
        int n = width * height;
        IterationBuffer = new int[n];
        SmoothBuffer = new float[n];
        DistanceBuffer = new float[n];
        NormalXBuffer = new float[n];
        NormalYBuffer = new float[n];
        ColorBuffer = new uint[n];
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
        var callingMethod = new StackTrace().GetFrame(1)?.GetMethod();
        Debug.WriteLine($"Calculate() called from {callingMethod?.DeclaringType?.Name}.{callingMethod?.Name}{Environment.NewLine} with ColorMap={ColorMap.GetType().Name}, MaxIterations={MaxIterations}");
        ColorMap.MaxIterations = MaxIterations;

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
            default:
                // Unknown concrete type — fall back to virtual dispatch.
                // Still correct; just not devirtualized.
                CalculateCore(ColorMap, ct);
                break;
        }
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

        var po = new ParallelOptions { CancellationToken = ct };
        Parallel.For(0, Height, po, y =>
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

        int x = 0;

        // ── Vectorized lanes ──────────────────────────────────────────────────
        for (; x + VecLen <= Width; x += VecLen)
        {
            for (int k = 0; k < VecLen; k++)
                cxBuf[k] = centerX + ((x + k) - Width * 0.5) * scale;
            var cx = new Vector<double>(cxBuf);

            var zr = zeroV; var zi = zeroV;
            var dr = oneV; var di = zeroV;
            var iterCountV = zeroV;

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

                if ((iter & 7) == 7)
                {
                    var newMag2 = zr * zr + zi * zi;
                    if (!Vector.LessThanAny(newMag2, escRad2V)) break;
                }
            }

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

        // ── Scalar tail ───────────────────────────────────────────────────────
        for (; x < Width; x++)
        {
            double cx2 = centerX + (x - Width * 0.5) * scale;
            ComputePixelSP(cx2, cy, maxIter, rowBase + x, colorMap);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ComputePixelSP<TMap>(
        double cx, double cy, int maxIter, int idx, TMap colorMap)
        where TMap : IColorMap
    {
        double zr = 0, zi = 0, dr = 1, di = 0;
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
            float smooth = (float)(iters + 1.0
                - Math.Log(Math.Log(mag) / Math.Log(2.0)) / Math.Log(2.0));
            SmoothBuffer[idx] = smooth;

            double dMag = Math.Sqrt(dr * dr + di * di);
            float dist = dMag > 1e-10
                ? (float)(mag * Math.Log(mag) / dMag) : 0f;
            DistanceBuffer[idx] = dist;

            FillNormal(idx, zr, zi, dr, di);

            // HIGH IMPACT 3: color computed HERE, no second pass
            ColorBuffer[idx] = (uint)colorMap.Map(
                smooth, dist, maxIter,
                NormalXBuffer[idx], NormalYBuffer[idx]);
        }
        else
        {
            SmoothBuffer[idx] = 0f;
            DistanceBuffer[idx] = 0f;
            NormalXBuffer[idx] = 0f;
            NormalYBuffer[idx] = 0f;
            ColorBuffer[idx] = colorMap.InSetColor; // theme-defined interior, default opaque black
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

        bool useSimd = DD4.IsSupported;
        var po = new ParallelOptions { CancellationToken = ct };
        Parallel.For(0, Height, po, y =>
        {
            if (ct.IsCancellationRequested) return;
            int rowBase = y * Width;
            if (useSimd)
                ComputeRowPT4(y, scale, maxIt, rowBase, colorMap);
            else
                ComputeRowPTScalar(y, scale, maxIt, rowBase, colorMap);
        });
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
            float smooth = (float)(iters + 1.0
                - Math.Log(Math.Log(mag) / Math.Log(2.0)) / Math.Log(2.0));
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

            ColorBuffer[idx] = (uint)colorMap.Map(
                smooth, dist, maxIter,
                NormalXBuffer[idx], NormalYBuffer[idx]);
        }
        else
        {
            SmoothBuffer[idx] = 0f;
            DistanceBuffer[idx] = 0f;
            NormalXBuffer[idx] = 0f;
            NormalYBuffer[idx] = 0f;
            ColorBuffer[idx] = colorMap.InSetColor;
        }
    }

    // ── Full-DD per-pixel fallback (used for PT glitches at zoom ≤ QDZoomThreshold) ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ComputePixelHP<TMap>(DD cx, DD cy, int maxIter, int idx, TMap colorMap)
        where TMap : IColorMap
    {
        DD zr = DD.Zero, zi = DD.Zero;
        double dr = 1.0, di = 0.0;
        int iter;

        for (iter = 0; iter < maxIter; iter++)
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
        QD zr = QD.Zero, zi = QD.Zero;
        double dr = 1.0, di = 0.0;
        int iter;

        for (iter = 0; iter < maxIter; iter++)
        {
            double zrH = zr.X0, ziH = zi.X0;
            if (zrH * zrH + ziH * ziH >= EscapeRadius2) break;

            double newDr = 2.0 * (zrH * dr - ziH * di) + 1.0;
            double newDi = 2.0 * (zrH * di + ziH * dr);
            dr = newDr; di = newDi;

            QD newZi = (zr * zi) * 2.0 + cy;
            QD newZr = zr.Square() - zi.Square() + cx;
            zr = newZr; zi = newZi;
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
            _refZr = new double[maxIter + 1];
            _refZi = new double[maxIter + 1];
        }
        DD zr = DD.Zero, zi = DD.Zero;
        int n;
        for (n = 0; n < maxIter; n++)
        {
            _refZr[n] = zr.Hi;
            _refZi[n] = zi.Hi;
            if (zr.Hi * zr.Hi + zi.Hi * zi.Hi >= EscapeRadius2) break;
            DD newZi = (zr * zi) * 2.0 + cy;
            zr = zr.Square() - zi.Square() + cx;
            zi = newZi;
        }
        _refZr[n] = zr.Hi;
        _refZi[n] = zi.Hi;
        _refOrbitLen = n;  // == maxIter when centre is interior

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
            _refZr = new double[maxIter + 1];
            _refZi = new double[maxIter + 1];
        }
        QD zr = QD.Zero, zi = QD.Zero;
        int n;
        for (n = 0; n < maxIter; n++)
        {
            _refZr[n] = zr.X0;
            _refZi[n] = zi.X0;
            if (zr.X0 * zr.X0 + zi.X0 * zi.X0 >= EscapeRadius2) break;
            QD newZi = (zr * zi) * 2.0 + cy;
            zr = zr.Square() - zi.Square() + cx;
            zi = newZi;
        }
        _refZr[n] = zr.X0;
        _refZi[n] = zi.X0;
        _refOrbitLen = n;

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
        int x = 0;

        for (; x + 4 <= Width; x += 4)
        {
            var dcRv = Vector256.Create(
                (x - halfW) * scale,
                (x + 1 - halfW) * scale,
                (x + 2 - halfW) * scale,
                (x + 3 - halfW) * scale);

            var dr = Vector256<double>.Zero;
            var di = Vector256<double>.Zero;
            var drv = one;
            var div = Vector256<double>.Zero;
            var iterCount = Vector256<double>.Zero;
            int escapedMask = 0;
            bool glitched = false;

            for (int iter = 0; iter < maxIter; iter++)
            {
                if (iter > refLen) { glitched = true; break; }

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
                int iters = (int)iterCount.GetElement(k);
                // Reconstruct z at escape: Z_iters + δ_iters (δ frozen when lane escaped)
                double zrF = (iters <= refLen ? _refZr[iters] : 0.0) + dr.GetElement(k);
                double ziF = (iters <= refLen ? _refZi[iters] : 0.0) + di.GetElement(k);
                IterationBuffer[idx] = iters;
                FillAuxAndColorHP(idx, iters, maxIter, zrF, ziF,
                    drv.GetElement(k), div.GetElement(k), colorMap);
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
        if (strength < 0.0) strength = 0.0;
        if (strength > 1.0) strength = 1.0;

        int w = Width, h = Height;
        int n = w * h;
        int maxIter = MaxIterations;
        if (n == 0 || maxIter <= 0) return;

        ColorMap.MaxIterations = maxIter;

        // ── Build histogram over escaped pixels ──────────────────────────────
        // Bin count: enough resolution for smooth gradients, capped to avoid
        // sparse bins on small images.
        int bins = Math.Min(2048, Math.Max(256, maxIter));
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

        // No escaped pixels (entire view interior) — nothing to equalize.
        if (totalEscaped == 0)
        {
            RecolorFromBuffers();
            return;
        }

        // CDF normalized to [0, 1].
        double[] cdf = new double[bins];
        long cum = 0;
        double invTotal = 1.0 / totalEscaped;
        for (int i = 0; i < bins; i++)
        {
            cum += hist[i];
            cdf[i] = cum * invTotal;
        }

        // ── Re-color every pixel using lerp(linear t, cdf t, strength) ───────
        var po = new ParallelOptions();
        Parallel.For(0, h, po, y =>
        {
            int rowBase = y * w;
            for (int x = 0; x < w; x++)
            {
                int idx = rowBase + x;
                int iters = IterationBuffer[idx];
                if (iters >= maxIter)
                {
                    ColorBuffer[idx] = ColorMap.InSetColor;
                    continue;
                }
                float s = SmoothBuffer[idx];
                float tLin = s * invMax;
                float tLinC = tLin < 0f ? 0f : (tLin > 0.9999999f ? 0.9999999f : tLin);
                int b = (int)(tLinC * bins);
                double tEq = cdf[b];
                double tBlend = tLin + (tEq - tLin) * strength;
                float smoothEq = (float)(tBlend * maxIter);
                ColorBuffer[idx] = (uint)ColorMap.Map(
                    smoothEq, DistanceBuffer[idx], maxIter,
                    NormalXBuffer[idx], NormalYBuffer[idx]);
            }
        });
    }

    // Recompute ColorBuffer from filled aux buffers without changing iteration
    // values.  Used as the strength=0 / no-escape fast path of equalization.
    private void RecolorFromBuffers()
    {
        int w = Width, h = Height;
        int maxIter = MaxIterations;
        ColorMap.MaxIterations = maxIter;
        var po = new ParallelOptions();
        Parallel.For(0, h, po, y =>
        {
            int rowBase = y * w;
            for (int x = 0; x < w; x++)
            {
                int idx = rowBase + x;
                int iters = IterationBuffer[idx];
                if (iters >= maxIter)
                {
                    ColorBuffer[idx] = ColorMap.InSetColor;
                }
                else
                {
                    ColorBuffer[idx] = (uint)ColorMap.Map(
                        SmoothBuffer[idx], DistanceBuffer[idx], maxIter,
                        NormalXBuffer[idx], NormalYBuffer[idx]);
                }
            }
        });
    }
}