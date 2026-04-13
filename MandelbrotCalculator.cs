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

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.FFMath;     // DD
using FracturingFog.Models;   // QualityPreset, IColorMap

namespace FracturingFog;

/// <summary>
/// Computes the Mandelbrot set with SIMD acceleration (standard depth) or
/// double-double scalar arithmetic (extended precision at deep zoom).
/// Outputs iteration, smooth, distance, and surface-normal buffers.
/// </summary>
public sealed class MandelbrotCalculator
{
    // ── Public view / quality state ───────────────────────────────────────────

    public int Width  { get; private set; }
    public int Height { get; private set; }

    /// <summary>Real part of the complex-plane view centre (default –0.5).</summary>
    public double CenterX { get; set; } = -0.5;

    /// <summary>Imaginary part of the complex-plane view centre (default 0.0).</summary>
    public double CenterY { get; set; } = 0.0;
    public double Zoom    { get; set; } =  1.0;

    public int MaxIterations { get; set; } = 512;

    /// <summary>
    /// Active quality preset.  The calculator uses it to decide whether to
    /// engage double-double arithmetic for the current frame.
    /// </summary>
    public QualityPreset Quality { get; set; } = QualityPreset.Standard;

    /// <summary>
    /// Set to true after each <see cref="Calculate"/> call when the
    /// double-double path was used.  Read by the UI for the status bar "[DD]" tag.
    /// </summary>
    public bool IsHighPrecisionActive { get; private set; }

    public IColorMap ColorMap { get; set; } = new HsvPalette();

    // ── Output buffers ────────────────────────────────────────────────────────

    /// <summary>Raw escape-iteration count per pixel (MaxIterations for in-set pixels).</summary>
    public int[]   IterationBuffer  { get; private set; } = Array.Empty<int>();

    /// <summary>Smooth (continuous) iteration value; 0 for in-set pixels.</summary>
    public float[] SmoothBuffer     { get; private set; } = Array.Empty<float>();

    /// <summary>Exterior distance estimate in world units; 0 for in-set pixels.</summary>
    public float[] DistanceBuffer   { get; private set; } = Array.Empty<float>();

    /// <summary>
    /// X component of the surface normal (range ≈ [−1, 1]).
    /// 0 for in-set pixels.  See file header for computation details.
    /// </summary>
    public float[] NormalXBuffer   { get; private set; } = Array.Empty<float>();

    /// <summary>
    /// Y component of the surface normal (range ≈ [−1, 1]).
    /// 0 for in-set pixels.
    /// </summary>
    public float[] NormalYBuffer   { get; private set; } = Array.Empty<float>();

    /// <summary>Packed BGRA colour per pixel (DXGI B8G8R8A8_UNorm layout).</summary>
    public uint[]  ColorBuffer      { get; private set; } = Array.Empty<uint>();

    // ── Private constants ─────────────────────────────────────────────────────

    // Large escape radius eliminates banding artefacts in smooth colouring.
    private const double EscapeRadius  = 512.0;
    private const double EscapeRadius2 = EscapeRadius * EscapeRadius;

    // SIMD vector width (4 on AVX2, 2 on SSE2).
    private static readonly int VecLen = Vector<double>.Count;

    // ── Constructor / resize ──────────────────────────────────────────────────

    public MandelbrotCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        if (width < 1 || height < 1)
            throw new ArgumentException("Dimensions must be positive.");

        Width  = width;
        Height = height;
        int n  = width * height;

        IterationBuffer = new int[n];
        SmoothBuffer    = new float[n];
        DistanceBuffer  = new float[n];
        NormalXBuffer   = new float[n];
        NormalYBuffer   = new float[n];
        ColorBuffer     = new uint[n];
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public compute entry point
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fills all output buffers.  CPU-intensive; always call from a background
    /// thread.  Respects <paramref name="cancellationToken"/> for early abort.
    /// </summary>
    public void Calculate(CancellationToken cancellationToken = default)
    {
        bool useHP = Quality.NeedsHighPrecision(Zoom);
        IsHighPrecisionActive = useHP;

        if (useHP)
            CalculateHighPrecision(cancellationToken);
        else
            CalculateDoublePrecision(cancellationToken);

        if (!cancellationToken.IsCancellationRequested)
            BuildColorBuffer(MaxIterations);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PATH A — Standard double + SIMD
    // ─────────────────────────────────────────────────────────────────────────

    private void CalculateDoublePrecision(CancellationToken ct)
    {
        double scale = (3.5 / System.Math.Max(Width, Height)) / Zoom;
        double xMin  = CenterX - Width  * scale * 0.5;
        double yMin  = CenterY - Height * scale * 0.5;
        int    maxIt = MaxIterations;

        var po = new ParallelOptions { CancellationToken = ct };
        Parallel.For(0, Height, po, y =>
        {
            if (ct.IsCancellationRequested) return;
            ComputeRowSP(yMin + y * scale, xMin, scale, maxIt, y * Width);
        });

    }

    private void ComputeRowSP(double cy, double xMin, double scale,
                             int maxIter, int rowBase)
    {
        var escRad2V = new Vector<double>(EscapeRadius2);
        var twoV     = new Vector<double>(2.0);
        var oneV     = Vector<double>.One;
        var zeroV    = Vector<double>.Zero;
        var cyV      = new Vector<double>(cy);

        Span<double> cxBuf = stackalloc double[VecLen];

        int x = 0;

        // ── Vectorized lanes ──────────────────────────────────────────────────
        for (; x + VecLen <= Width; x += VecLen)
        {
            for (int k = 0; k < VecLen; k++)
                cxBuf[k] = xMin + (x + k) * scale;
            var cx = new Vector<double>(cxBuf);

            var zr = zeroV;  var zi = zeroV;
            var dr = oneV;   var di = zeroV;

            var iterCountV = zeroV;

            for (int iter = 0; iter < maxIter; iter++)
            {
                var zr2  = zr * zr;
                var zi2  = zi * zi;
                var mag2 = zr2 + zi2;

                // notEscaped: all-bits-set for lanes with |z|² < escapeRadius²
                var notEscaped = Vector.LessThan(mag2, escRad2V);

                // Accumulate iteration count only for still-active lanes.
                iterCountV += Vector.ConditionalSelect(notEscaped, oneV, zeroV);

                // Derivative: dz_new = 2·z·dz + 1
                var newDr = twoV * (zr * dr - zi * di) + oneV;
                var newDi = twoV * (zr * di + zi * dr);
                dr = Vector.ConditionalSelect(notEscaped, newDr, dr);
                di = Vector.ConditionalSelect(notEscaped, newDi, di);

                // z_new = z² + c
                var newZr = zr2 - zi2 + cx;
                var newZi = twoV * zr * zi + cyV;
                zr = Vector.ConditionalSelect(notEscaped, newZr, zr);
                zi = Vector.ConditionalSelect(notEscaped, newZi, zi);

                // Check early exit every 8 iterations to amortise overhead.
                if ((iter & 7) == 7 && !Vector.LessThanAny(mag2, escRad2V))
                    break;
            }

            // Extract results lane by lane.
            for (int k = 0; k < VecLen; k++)
            {
                int    idx   = rowBase + x + k;
                int    iters = (int)iterCountV[k];

                IterationBuffer[idx] = iters;
                FillAuxSP(idx, iters, maxIter, zr[k], zi[k], dr[k], di[k]);
            }
        }

        // ── Scalar tail ───────────────────────────────────────────────────────
        for (; x < Width; x++)
            ComputePixelSP(xMin + x * scale, cy, maxIter, rowBase + x);
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ComputePixelSP(double cx, double cy, int maxIter, int idx)
    {
        double zr = 0, zi = 0, dr = 1, di = 0;
        int    iter;

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
        FillAuxSP(idx, iter, maxIter, zr, zi, dr, di);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FillAuxSP(int idx, int iters, int maxIter,
                                        double zr, double zi, double dr, double di)
    {
        if (iters < maxIter)
        {
            double mag = System.Math.Sqrt(zr * zr + zi * zi);
            SmoothBuffer[idx] = (float)(iters + 1.0
                - System.Math.Log(System.Math.Log(mag) / System.Math.Log(2.0))
                  / System.Math.Log(2.0));

            double dMag = System.Math.Sqrt(dr * dr + di * di);
            DistanceBuffer[idx] = dMag > 1e-10
                ? (float)(mag * System.Math.Log(mag) / dMag)
                : 0f;
            FillNormal(idx, zr, zi, dr, di);
        }
        else
        {
            SmoothBuffer[idx]   = 0f;
            DistanceBuffer[idx] = 0f;
            NormalXBuffer[idx]  = 0f;
            NormalYBuffer[idx]  = 0f;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Normal computation (both paths call this)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fills NormalXBuffer[idx] and NormalYBuffer[idx] using the Inigo Quilez
    /// derivative technique.  Only called for escaped pixels.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FillNormal(int idx, double zr, double zi, double dr, double di)
    {
        // z_n · conj(d_n) = (zr·dr + zi·di) + (zi·dr - zr·di)·i
        double u = zr * dr + zi * di;
        double v = zi * dr - zr * di;
        double m = System.Math.Sqrt(u * u + v * v);
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
    // PATH B — Double-double extended precision
    // ─────────────────────────────────────────────────────────────────────────
    //
    // The complex derivative dz/dc is tracked using standard double arithmetic
    // (not DD).  The derivative orbit is used only for surface normal estimation
    // which requires ~6 significant digits — far less than the 31 digits that
    // DD provides for the orbit itself.

    private void CalculateHighPrecision(CancellationToken ct)
    {
        // Compute scale in double — even at zoom 1e20, 3.5e-23 is a valid
        // (possibly denormalized) double.  DD.FromCenterOffset then captures
        // the full precision of each pixel's offset from the centre.
        double scale = (3.5 / System.Math.Max(Width, Height)) / Zoom;
        int    maxIt = MaxIterations;

        var po = new ParallelOptions { CancellationToken = ct };
        Parallel.For(0, Height, po, y =>
        {
            if (ct.IsCancellationRequested) return;
            ComputeRowHP(y, scale, maxIt, y * Width);
        });
    }

    private void ComputeRowHP(int y, double scale, int maxIter, int rowBase)
    {
        // Build cy (imaginary coordinate) once per row.
        double yOffset = y - Height * 0.5;
        DD cy = DD.FromCenterOffset(CenterY, yOffset, scale);

        for (int x = 0; x < Width; x++)
        {
            DD cx = DD.FromCenterOffset(CenterX, x - Width * 0.5, scale);
            ComputePixelHP(cx, cy, maxIter, rowBase + x);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ComputePixelHP(DD cx, DD cy, int maxIter, int idx)
    {
        DD zr = DD.Zero;
        DD zi = DD.Zero;
        // Derivative tracked in double — sufficient for smooth normals.
        double dr = 1.0, di = 0.0;
        int iter;

        for (iter = 0; iter < maxIter; iter++)
        {
            // Compute |z|² using .Square() optimisation.
            DD zr2  = zr.Square();
            DD zi2  = zi.Square();
            DD mag2 = zr2 + zi2;

            // Escape check on the Hi word (sufficient — see DD.cs for rationale).
            if (mag2 >= EscapeRadius2) break;

            // Update derivative (double approximation via z.Hi — fine for normals).
            double newDr = 2.0 * (zr.Hi * dr - zi.Hi * di) + 1.0;
            double newDi = 2.0 * (zr.Hi * di + zi.Hi * dr);
            dr = newDr; di = newDi;
            // z_new = z² + c
            //   real part: zr_new = zr² - zi² + cx
            //   imag part: zi_new = 2·zr·zi + cy
            DD newZi = (zr * zi) * 2.0 + cy;
            DD newZr = zr2 - zi2 + cx;
            zr = newZr;
            zi = newZi;

            if ((iter & 7) == 7 && mag2 > EscapeRadius2) break;
        }

        IterationBuffer[idx] = iter;

        if (iter < maxIter)
        {
            double zrD = zr.Hi, ziD = zi.Hi;
            double mag = System.Math.Sqrt(zrD * zrD + ziD * ziD);

            SmoothBuffer[idx] = (float)(iter + 1.0
                - System.Math.Log(System.Math.Log(mag) / System.Math.Log(2.0))
                  / System.Math.Log(2.0));

            // Distance estimation omitted in HP mode (expensive DD derivative orbit).
            DistanceBuffer[idx] = 1.0f;

            // Surface normal — use double approximation; fully adequate for lighting.
            FillNormal(idx, zrD, ziD, dr, di);
        }
        else
        {
            SmoothBuffer[idx]   = 0f;
            DistanceBuffer[idx] = 0f;
            NormalXBuffer[idx]  = 0f;
            NormalYBuffer[idx]  = 0f;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Colour buffer — calls the five-parameter Map overload
    // ─────────────────────────────────────────────────────────────────────────

    private void BuildColorBuffer(int maxIter)
    {
        int n = Width * Height;
        for (int i = 0; i < n; i++)
            ColorBuffer[i] = ComputeColor(
                SmoothBuffer[i], IterationBuffer[i], maxIter,
                DistanceBuffer[i], NormalXBuffer[i], NormalYBuffer[i],
                ColorMap);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ComputeColor(float smooth, int iter, int maxIter,
                                      float distance, float nx, float ny,
                                      IColorMap colorMap)
    {
        if (iter >= maxIter) return PackBgra(0, 0, 0, 255);



        if (colorMap != null)
        {
            colorMap.MaxIterations = maxIter;
            // Five-parameter call — 3D maps use nx/ny; flat maps use the default
            // which delegates to the three-parameter version.
            return (uint)colorMap.Map(smooth, distance, iter, nx, ny);
        }
        // Fallback: plain HSV.
        float hue = smooth * 0.02f % 1.0f;
        float val = System.Math.Clamp(1f - (float)System.Math.Pow(iter / (double)maxIter, 0.2), 0f, 1f);
        return HsvToPackedBgra(hue, 1f, val);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint HsvToPackedBgra(float h, float s, float v)
    {
        if (s <= 0f) { byte lum = (byte)(v * 255f); return PackBgra(lum, lum, lum, 255); }

        float hh = (h % 360f) / 60f;
        int   i  = (int)hh;
        float ff = hh - i;
        float p  = v * (1f - s), q = v * (1f - s * ff), t = v * (1f - s * (1f - ff));

        float r, g, b;
        switch (i) {
            case 0:  r=v; g=t; b=p; break;  case 1: r=q; g=v; b=p; break;
            case 2:  r=p; g=v; b=t; break;  case 3: r=p; g=q; b=v; break;
            case 4:  r=t; g=p; b=v; break;  default: r=v; g=p; b=q; break;
        }

        return PackBgra((byte)(b * 255f), (byte)(g * 255f), (byte)(r * 255f), 255);
    }

    /// <summary>
    /// Packs bytes into a uint with B, G, R, A layout in memory (little-endian x64),
    /// compatible with DXGI Format.B8G8R8A8_UNorm.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint PackBgra(byte b, byte g, byte r, byte a)
        => (uint)((a << 24) | (r << 16) | (g << 8) | b);
}
