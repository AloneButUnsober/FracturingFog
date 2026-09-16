// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// TranscendentalJuliaCalculator.cs
//
// Julia sets of entire transcendental maps (#854, epic #850):
//     z_{n+1} = λ · f(z_n),   f ∈ { sin, cos, exp }
// with z_0 = the pixel and λ a complex constant. See
// Docs/Technical/Theoretical-Fractal-RnD.md §3.1 and Devaney's work on
// exploding Julia sets of λ·exp z / λ·sin z.
//
// THE CRUX — no escape radius. An entire transcendental map has an essential
// singularity at ∞, so |z| is NOT a membership test (the Julia set is where
// the "fast escaping set" accumulates; orbits can grow doubly-exponentially).
// The correct bailout is on a single AXIS, not the modulus:
//   • sin / cos:  |Im z| > R   — sin(x+iy), cos(x+iy) grow like e^|y|/2.
//   • exp:        Re z  > R     — |exp(x+iy)| = e^x.
// This per-map non-modulus bailout is the reusable primitive this slice adds
// (the DSL/CalcGen extension tracked in the R&D doc §3.1 generalises it).
//
// Colouring: a continuous escape count (log-space crossing of the bailout
// axis) rides the SmoothBuffer, and the chain-rule derivative dz/dz0 fills the
// distance / normal / final-z channels (Milnor DE — approximate for
// transcendental maps but usable), so every 2D theme, Relief-3D and 3D-Phong
// theme works. View is the standard 2D plane (pixel = z0; pan/zoom via
// CenterX/Y/Zoom).

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed class TranscendentalJuliaCalculator
    : IFractalCalculator, IHeightFieldSource
{
    public int Width { get; private set; }
    public int Height { get; private set; }

    public double CenterX { get; set; } = 0.0;
    public double CenterY { get; set; } = 0.0;
    public double Zoom { get; set; } = 0.5;
    public int MaxIterations { get; set; } = 200;

    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();
    public FractalParameters FractalParameters { get; set; } = new();

    public bool SupportsZoomPan => true;

    /// <summary>Global interior-alpha knob (#97).</summary>
    public int InteriorAlpha { get; set; } = 255;

    // ── Buffers ──────────────────────────────────────────────────────────────
    public int[] IterationBuffer { get; private set; } = Array.Empty<int>();
    public float[] SmoothBuffer { get; private set; } = Array.Empty<float>();
    public float[] DistanceBuffer { get; private set; } = Array.Empty<float>();
    public float[] NormalXBuffer { get; private set; } = Array.Empty<float>();
    public float[] NormalYBuffer { get; private set; } = Array.Empty<float>();
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();
    public float[] FinalZrBuffer { get; private set; } = Array.Empty<float>();
    public float[] FinalZiBuffer { get; private set; } = Array.Empty<float>();
    public float[] FinalDrBuffer { get; private set; } = Array.Empty<float>();
    public float[] FinalDiBuffer { get; private set; } = Array.Empty<float>();

    public TranscendentalJuliaCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        int n = width * height;
        IterationBuffer = new int[n];
        SmoothBuffer = new float[n];
        DistanceBuffer = new float[n];
        NormalXBuffer = new float[n];
        NormalYBuffer = new float[n];
        ColorBuffer = new uint[n];
        FinalZrBuffer = new float[n];
        FinalZiBuffer = new float[n];
        FinalDrBuffer = new float[n];
        FinalDiBuffer = new float[n];
    }

    public void Calculate(CancellationToken ct = default)
    {
        int width = Width, height = Height;
        if (width < 1 || height < 1) return;

        var map = ColorMap;
        map.MaxIterations = MaxIterations;
        int maxIt = MaxIterations;

        double scale = (3.5 / Math.Max(width, height)) / Zoom;
        double centerX = CenterX, centerY = CenterY;
        double lambdaR = FractalParameters.TranscendentalLambdaRe;
        double lambdaI = FractalParameters.TranscendentalLambdaIm;
        var kind = FractalParameters.TranscendentalMap;
        // Non-modulus bailout on the map's escape axis.
        double bailAxis = Math.Max(4.0, FractalParameters.TranscendentalBailout);
        double logBail = Math.Log(bailAxis);

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            if (ct.IsCancellationRequested) return;
            double cy = centerY + (y - height * 0.5) * scale;
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                int idx = rowBase + x;
                double zr = centerX + (x - width * 0.5) * scale;
                double zi = cy;
                double dr = 1.0, di = 0.0;      // dz/dz0, Julia seed = 1

                double prevAxis = AxisValue(kind, zr, zi);
                int iter = 0;
                bool escaped = false;
                double curAxis = prevAxis;
                for (; iter < maxIt; iter++)
                {
                    curAxis = AxisValue(kind, zr, zi);
                    if (curAxis > bailAxis) { escaped = true; break; }

                    // f, f' of the transcendental map at z.
                    Eval(kind, zr, zi, out double fr, out double fi,
                                       out double fpr, out double fpi);

                    // d := λ · f'(z) · d   (chain rule).
                    ComplexMul(fpr, fpi, dr, di, out double t1r, out double t1i);
                    ComplexMul(lambdaR, lambdaI, t1r, t1i, out double ndr, out double ndi);

                    // z := λ · f(z).
                    ComplexMul(lambdaR, lambdaI, fr, fi, out double nzr, out double nzi);

                    dr = ndr; di = ndi;
                    prevAxis = curAxis;
                    zr = nzr; zi = nzi;

                    if (double.IsNaN(zr) || double.IsNaN(zi) ||
                        double.IsInfinity(zr) || double.IsInfinity(zi))
                    { escaped = true; curAxis = AxisValue(kind, zr, zi); iter++; break; }
                }

                if (!escaped || iter >= maxIt)
                {
                    // In-set (bounded within maxIt).
                    IterationBuffer[idx] = maxIt;
                    WriteInSet(idx, map);
                    continue;
                }

                // Continuous escape count: log-space crossing of the bail axis
                // between prevAxis (≤ R) and curAxis (> R).
                float smooth = iter;
                if (iter > 0 && curAxis > prevAxis && prevAxis > 0.0)
                {
                    double lp = Math.Log(prevAxis), lc = Math.Log(curAxis);
                    double denom = lc - lp;
                    double frac = denom > 1e-12 ? (logBail - lp) / denom : 0.0;
                    if (frac < 0.0) frac = 0.0; else if (frac > 1.0) frac = 1.0;
                    smooth = (float)((iter - 1) + frac);
                }
                IterationBuffer[idx] = iter;
                WriteEscaped(idx, smooth, zr, zi, dr, di, maxIt, map);
            }
        });

        if (InteriorAlpha < 255)
            InteriorAlphaStamp.Apply(
                ColorBuffer, IterationBuffer, Width, Height, maxIt, InteriorAlpha,
                new ParallelOptions(), ct);
    }

    // Escape-axis value for the map: sin/cos bail on |Im z|, exp on Re z.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static double AxisValue(TranscendentalMap kind, double zr, double zi)
        => kind == TranscendentalMap.Exp ? zr : Math.Abs(zi);

    // f(z) and f'(z) for the selected transcendental map.
    private static void Eval(
        TranscendentalMap kind, double zr, double zi,
        out double fr, out double fi, out double fpr, out double fpi)
    {
        switch (kind)
        {
            case TranscendentalMap.Exp:
            {
                // exp(x+iy) = e^x (cos y + i sin y); f' = f.
                double ex = Math.Exp(zr);
                fr = ex * Math.Cos(zi); fi = ex * Math.Sin(zi);
                fpr = fr; fpi = fi;
                break;
            }
            case TranscendentalMap.Cosine:
            {
                // cos(x+iy) = cos x cosh y − i sin x sinh y; f' = −sin z.
                fr = Math.Cos(zr) * Math.Cosh(zi);
                fi = -Math.Sin(zr) * Math.Sinh(zi);
                // −sin(x+iy) = −(sin x cosh y + i cos x sinh y)
                fpr = -Math.Sin(zr) * Math.Cosh(zi);
                fpi = -Math.Cos(zr) * Math.Sinh(zi);
                break;
            }
            default: // Sine
            {
                // sin(x+iy) = sin x cosh y + i cos x sinh y; f' = cos z.
                fr = Math.Sin(zr) * Math.Cosh(zi);
                fi = Math.Cos(zr) * Math.Sinh(zi);
                fpr = Math.Cos(zr) * Math.Cosh(zi);
                fpi = -Math.Sin(zr) * Math.Sinh(zi);
                break;
            }
        }
    }

    private void WriteEscaped(
        int idx, float smooth, double zr, double zi, double dr, double di,
        int maxIt, IColorMap map)
    {
        SmoothBuffer[idx] = smooth;

        double mag = Math.Sqrt(zr * zr + zi * zi);
        double dMag = Math.Sqrt(dr * dr + di * di);
        float dist = dMag > 1e-30 && mag > 1.0
            ? (float)(mag * Math.Log(mag) / dMag) : 0f;
        DistanceBuffer[idx] = dist;

        // Normal from z·conj(d) (Milnor).
        double u = zr * dr + zi * di;
        double v = zi * dr - zr * di;
        double m = Math.Sqrt(u * u + v * v);
        float nx = m > 1e-30 ? (float)(u / m) : 0f;
        float ny = m > 1e-30 ? (float)(v / m) : 0f;
        NormalXBuffer[idx] = nx;
        NormalYBuffer[idx] = ny;

        float fzr = (float)zr, fzi = (float)zi, fdr = (float)dr, fdi = (float)di;
        FinalZrBuffer[idx] = fzr;
        FinalZiBuffer[idx] = fzi;
        FinalDrBuffer[idx] = fdr;
        FinalDiBuffer[idx] = fdi;

        ColorBuffer[idx] = (uint)map.Map(smooth, dist, maxIt, nx, ny, fzr, fzi, fdr, fdi);
    }

    private void WriteInSet(int idx, IColorMap map)
    {
        SmoothBuffer[idx] = 0f;
        DistanceBuffer[idx] = 0f;
        NormalXBuffer[idx] = 0f;
        NormalYBuffer[idx] = 0f;
        FinalZrBuffer[idx] = 0f;
        FinalZiBuffer[idx] = 0f;
        FinalDrBuffer[idx] = 0f;
        FinalDiBuffer[idx] = 0f;
        ColorBuffer[idx] = map.InSetColor;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void ComplexMul(double ar, double ai, double br, double bi,
        out double rr, out double ri)
    {
        rr = ar * br - ai * bi;
        ri = ar * bi + ai * br;
    }
}
