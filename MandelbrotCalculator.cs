// MandelbrotCalculator.cs
// SIMD-vectorized Mandelbrot set computation using System.Numerics.Vector<double>.
// On AVX2 hardware Vector<double>.Count == 4, giving ~4× throughput vs scalar.
//
// Four output buffers per pixel:
//   IterationBuffer  – raw escape iteration count
//   SmoothBuffer     – smooth (continuous) iteration for banding-free colour
//   DistanceBuffer   – exterior distance estimate (useful for outline/glow effects)
//   ColorBuffer      – packed BGRA (B8G8R8A8_UNorm) colour via HSV wheel

using System;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

/// <summary>
/// Computes the Mandelbrot set with SIMD acceleration.
/// Set the public view properties then call <see cref="Calculate"/>.
/// </summary>
public sealed class MandelbrotCalculator
{
    // ── Public view state ────────────────────────────────────────────────────

    public int Width  { get; private set; }
    public int Height { get; private set; }

    /// <summary>Real part of the complex-plane view centre (default –0.5).</summary>
    public double CenterX { get; set; } = -0.5;

    /// <summary>Imaginary part of the complex-plane view centre (default 0.0).</summary>
    public double CenterY { get; set; } = 0.0;

    /// <summary>
    /// Zoom factor: pixel scale = BaseScale / Zoom.
    /// 1.0 shows the full set; larger values zoom in.
    /// </summary>
    public double Zoom { get; set; } = 1.0;

    /// <summary>Maximum iteration depth (default 512).</summary>
    public int MaxIterations { get; set; } = 512;

    public IColorMap ColorMap { get; set; } = new HsvPalette();

    // ── Output buffers ────────────────────────────────────────────────────────

    /// <summary>Raw escape-iteration count per pixel (MaxIterations for in-set pixels).</summary>
    public int[]   IterationBuffer  { get; private set; } = Array.Empty<int>();

    /// <summary>Smooth (continuous) iteration value; 0 for in-set pixels.</summary>
    public float[] SmoothBuffer     { get; private set; } = Array.Empty<float>();

    /// <summary>Exterior distance estimate in world units; 0 for in-set pixels.</summary>
    public float[] DistanceBuffer   { get; private set; } = Array.Empty<float>();

    /// <summary>Packed BGRA colour per pixel (DXGI Format.B8G8R8A8_UNorm layout).</summary>
    public uint[]  ColorBuffer      { get; private set; } = Array.Empty<uint>();

    // ── Private constants ─────────────────────────────────────────────────────

    // Large escape radius eliminates banding artefacts in smooth colouring.
    private const double EscapeRadius  = 512.0;
    private const double EscapeRadius2 = EscapeRadius * EscapeRadius;

    // SIMD lane width for double (4 on AVX2, 2 on SSE2).
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
        ColorBuffer     = new uint[n];
        ColorMap        = new HsvPalette();
    }

    // ── Public compute entry point ────────────────────────────────────────────

    /// <summary>
    /// Fills all four output buffers. CPU-intensive — call from a background thread.
    /// Respects <paramref name="cancellationToken"/> to abort early.
    /// </summary>
    public void Calculate(CancellationToken cancellationToken = default)
    {
        // Normalise scale against the larger dimension for consistent aspect ratio.
        double scale = (3.5 / Math.Max(Width, Height)) / Zoom;
        double xMin  = CenterX - Width  * scale * 0.5;
        double yMin  = CenterY - Height * scale * 0.5;
        int    maxIt = MaxIterations;

        var po = new ParallelOptions { CancellationToken = cancellationToken };
        Parallel.For(0, Height, po, y =>
        {
            if (cancellationToken.IsCancellationRequested) return;
            ComputeRow(y, yMin + y * scale, xMin, scale, maxIt, y * Width);
        });

        if (!cancellationToken.IsCancellationRequested)
            BuildColorBuffer(maxIt);
    }

    // ── Per-row vectorized inner loop ─────────────────────────────────────────

    private void ComputeRow(int _y, double cy, double xMin, double scale,
                             int maxIter, int rowBase)
    {
        var escRad2V = new Vector<double>(EscapeRadius2);
        var twoV     = new Vector<double>(2.0);
        var oneV     = Vector<double>.One;
        var zeroV    = Vector<double>.Zero;
        var cyV      = new Vector<double>(cy);

        Span<double> cxBuf = stackalloc double[VecLen];

        int x = 0;

        // ── Vectorized path ── (VecLen pixels per iteration of the outer loop)
        for (; x + VecLen <= Width; x += VecLen)
        {
            for (int k = 0; k < VecLen; k++)
                cxBuf[k] = xMin + (x + k) * scale;
            var cx = new Vector<double>(cxBuf);

            var zr = zeroV;
            var zi = zeroV;
            // Derivative orbit for exterior distance: dz/dc, initialised to (1, 0).
            var dr = oneV;
            var di = zeroV;

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
                double zrk   = zr[k], zik = zi[k];
                double drk   = dr[k], dik = di[k];

                IterationBuffer[idx] = iters;
                FillSmoothAndDistance(idx, iters, maxIter, zrk, zik, drk, dik);
            }
        }

        // ── Scalar tail (Width not a multiple of VecLen) ──
        for (; x < Width; x++)
            ComputePixelScalar(xMin + x * scale, cy, maxIter, rowBase + x);
    }

    // ── Scalar single-pixel fallback ──────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ComputePixelScalar(double cx, double cy, int maxIter, int idx)
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
        FillSmoothAndDistance(idx, iter, maxIter, zr, zi, dr, di);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FillSmoothAndDistance(int idx, int iters, int maxIter,
                                        double zr, double zi, double dr, double di)
    {
        if (iters < maxIter)
        {
            double mag = Math.Sqrt(zr * zr + zi * zi);

            // Smooth colouring: iter − log₂(log₂|z|) eliminates integer-step banding.
            SmoothBuffer[idx] = (float)(iters + 1.0
                - Math.Log(Math.Log(mag) / Math.Log(2.0)) / Math.Log(2.0));

            // Exterior distance estimate: |z|·log|z| / |dz|
            double dMag = Math.Sqrt(dr * dr + di * di);
            DistanceBuffer[idx] = dMag > 1e-10
                ? (float)(mag * Math.Log(mag) / dMag)
                : 0f;
        }
        else
        {
            SmoothBuffer[idx]   = 0f;
            DistanceBuffer[idx] = 0f;
        }
    }

    // ── HSV colourisation ─────────────────────────────────────────────────────

    private void BuildColorBuffer(int maxIter)
    {
        int n = Width * Height;
        for (int i = 0; i < n; i++)
            ColorBuffer[i] = ComputeColor(SmoothBuffer[i], IterationBuffer[i], maxIter, DistanceBuffer[i], ColorMap);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ComputeColor(float smooth, int iter, int maxIter, float distance, IColorMap colorMap)
    {
        // In-set: black.
        if (iter >= maxIter) return PackBgra(0, 0, 0, 255);


        // 8 full hue cycles across the iteration range → classic spiral gradient.
        float s = Math.Max(0f, smooth);
        float hue = s * 0.02F % 1.0F; // 8.0f % 360.0f;
        float sat = 1.0F; // 0.85f;
        //float lightness = 1.0f - MathF.Min(distance * 0.05f, 1.0f); // scale factor controls brightness falloff
        float val  = 1.0f - (float)Math.Pow(iter / (double)maxIter, 0.2);
        val = Math.Clamp(val, 0f, 1f);

        colorMap?.MaxIterations = maxIter;
        return colorMap != null ? (uint)colorMap.Map(s, distance, iter) : HsvToPackedBgra(hue, sat, val);
    }

    /// <summary>Converts HSV (h∈[0,360), s∈[0,1], v∈[0,1]) to a packed BGRA uint.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint HsvToPackedBgra(float h, float s, float v)
    {
        if (s <= 0f)
        {
            byte lum = (byte)(v * 255f);
            return PackBgra(lum, lum, lum, 255);
        }

        float hh = (h % 360f) / 60f;
        int   i  = (int)hh;
        float ff = hh - i;
        float p  = v * (1f - s);
        float q  = v * (1f - s * ff);
        float t  = v * (1f - s * (1f - ff));

        float r, g, b;
        switch (i)
        {
            case 0:  r = v; g = t; b = p; break;
            case 1:  r = q; g = v; b = p; break;
            case 2:  r = p; g = v; b = t; break;
            case 3:  r = p; g = q; b = v; break;
            case 4:  r = t; g = p; b = v; break;
            default: r = v; g = p; b = q; break;
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
