// NewtonCalculator.cs
//
// Renders Newton fractal for f(z) = z^d - 1. Iterates z := z - R·f(z)/f'(z)
// until convergence to a root. Color is basin (root index) hue blended with
// iteration count for shading.

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;
// INewtonColorMap lives in FracturingFog.Interefaces

namespace FracturingFog;

public sealed class NewtonCalculator : IFractalCalculator
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    public double CenterX { get; set; } = 0.0;
    public double CenterY { get; set; } = 0.0;
    public double Zoom { get; set; } = 1.0;
    public int MaxIterations { get; set; } = 64;

    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();

    public bool SupportsZoomPan => true;

    public FractalParameters FractalParameters { get; set; } = new();

    public NewtonCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[width * height];
    }

    public void Calculate(CancellationToken ct = default)
    {
        int d = Math.Clamp(FractalParameters.NewtonExponent, 2, 8);
        double R = FractalParameters.NewtonRelaxation;
        int maxIter = MaxIterations;
        if (maxIter < 8) maxIter = 64;

        // Roots of z^d = 1 are unit roots e^(2π·k/d).
        var rootsR = new double[d];
        var rootsI = new double[d];
        for (int k = 0; k < d; k++)
        {
            rootsR[k] = Math.Cos(2 * Math.PI * k / d);
            rootsI[k] = Math.Sin(2 * Math.PI * k / d);
        }

        double scale = (3.5 / Math.Max(Width, Height)) / Zoom;
        int width = Width;
        int height = Height;
        double centerX = CenterX;
        double centerY = CenterY;
        const double eps2 = 1e-12;

        var newtonMap = ColorMap as INewtonColorMap;
        if (ColorMap != null) ColorMap.MaxIterations = maxIter;

        Parallel.For(0, height, new ParallelOptions { CancellationToken = ct }, y =>
        {
            if (ct.IsCancellationRequested) return;
            double cy = centerY + (y - height * 0.5) * scale;
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                double cx = centerX + (x - width * 0.5) * scale;
                double zr = cx, zi = cy;
                int iter;
                int basin = -1;
                for (iter = 0; iter < maxIter; iter++)
                {
                    // f(z) = z^d - 1, f'(z) = d·z^(d-1)
                    // Compute z^(d-1) via polar to avoid repeated multiplication.
                    double r2 = zr * zr + zi * zi;
                    if (r2 < 1e-30) { zr = 1e-10; zi = 0; r2 = 1e-20; }
                    double r = Math.Sqrt(r2);
                    double theta = Math.Atan2(zi, zr);
                    double rPowD = Math.Pow(r, d);
                    double rPowDm1 = rPowD / r;
                    double zdR = rPowD * Math.Cos(d * theta);
                    double zdI = rPowD * Math.Sin(d * theta);
                    double zdm1R = rPowDm1 * Math.Cos((d - 1) * theta);
                    double zdm1I = rPowDm1 * Math.Sin((d - 1) * theta);

                    // f = z^d - 1
                    double fR = zdR - 1.0;
                    double fI = zdI;
                    // f' = d · z^(d-1)
                    double fpR = d * zdm1R;
                    double fpI = d * zdm1I;
                    // f / f' = (fR + i fI) * conj(fp) / |fp|²
                    double denom = fpR * fpR + fpI * fpI;
                    if (denom < 1e-30) break;
                    double quotR = (fR * fpR + fI * fpI) / denom;
                    double quotI = (fI * fpR - fR * fpI) / denom;
                    // z := z - R · quot
                    zr -= R * quotR;
                    zi -= R * quotI;

                    // Check convergence to any root.
                    for (int k = 0; k < d; k++)
                    {
                        double dx = zr - rootsR[k];
                        double dy = zi - rootsI[k];
                        if (dx * dx + dy * dy < eps2) { basin = k; goto converged;
                        }
                    }
                }
            converged:
                int idx = rowBase + x;
                if (newtonMap != null)
                {
                    int rgb = newtonMap.MapNewton(basin, d, iter, maxIter, zr, zi);
                    ColorBuffer[idx] = unchecked((uint)rgb);
                }
                else if (basin < 0)
                {
                    ColorBuffer[idx] = ColorMap.InSetColor;
                }
                else
                {
                    // Hue per basin, shade by iteration count.
                    float hue = (float)basin / d;
                    float shade = 1.0f - Math.Min(iter / (float)maxIter, 0.9f);
                    int rgb = HsvToArgb(hue, 1.0f, shade);
                    ColorBuffer[idx] = unchecked((uint)rgb);
                }
            }
        });
    }

    private static int HsvToArgb(float h, float s, float v)
    {
        h = h * 6f;
        int i = (int)Math.Floor(h);
        float f = h - i;
        float p = v * (1 - s);
        float q = v * (1 - s * f);
        float t = v * (1 - s * (1 - f));
        float rF, gF, bF;
        switch (i % 6)
        {
            case 0: rF = v; gF = t; bF = p; break;
            case 1: rF = q; gF = v; bF = p; break;
            case 2: rF = p; gF = v; bF = t; break;
            case 3: rF = p; gF = q; bF = v; break;
            case 4: rF = t; gF = p; bF = v; break;
            case 5: rF = v; gF = p; bF = q; break;
            default: rF = gF = bF = 0; break;
        }
        int r = (int)(rF * 255);
        int g = (int)(gF * 255);
        int b = (int)(bF * 255);
        return unchecked((int)0xFF000000 | (r << 16) | (g << 8) | b);
    }
}
