// BuddhabrotCalculator.cs
//
// Samples random c values uniformly across the Mandelbrot bounding box. For
// each c whose orbit escapes within the relevant iteration band, replays the
// orbit and increments a per-pixel hit counter. Three iteration bands feed
// into R/G/B channels for the Nebulabrot effect.

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed class BuddhabrotCalculator : IFractalCalculator
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    public double CenterX { get; set; } = -0.5;
    public double CenterY { get; set; } = 0.0;
    public double Zoom { get; set; } = 1.0;
    public int MaxIterations { get; set; } = 50_000;

    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();

    public bool SupportsZoomPan => true;

    public FractalParameters FractalParameters { get; set; } = new();

    private uint[] _hitsR = Array.Empty<uint>();
    private uint[] _hitsG = Array.Empty<uint>();
    private uint[] _hitsB = Array.Empty<uint>();

    public BuddhabrotCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        int n = width * height;
        ColorBuffer = new uint[n];
        _hitsR = new uint[n];
        _hitsG = new uint[n];
        _hitsB = new uint[n];
    }

    public void Calculate(CancellationToken ct = default)
    {
        Array.Clear(_hitsR);
        Array.Clear(_hitsG);
        Array.Clear(_hitsB);

        int width = Width;
        int height = Height;
        int samples = FractalParameters.BuddhaSamples;
        int[] bands = { FractalParameters.BuddhaIterLow, FractalParameters.BuddhaIterMid, FractalParameters.BuddhaIterHigh };

        // Pixel mapping: (cx, cy) in [-2.5..1.5, -1.5..1.5] (≈ Mandelbrot bbox).
        // Apply user pan/zoom.
        double scale = (3.5 / Math.Max(width, height)) / Zoom;
        double midX = CenterX;
        double midY = CenterY;

        int threads = Math.Max(1, Environment.ProcessorCount);
        int perThread = samples / threads;
        var localR = new uint[threads][];
        var localG = new uint[threads][];
        var localB = new uint[threads][];
        for (int t = 0; t < threads; t++)
        {
            localR[t] = new uint[width * height];
            localG[t] = new uint[width * height];
            localB[t] = new uint[width * height];
        }

        Parallel.For(0, threads, new ParallelOptions { CancellationToken = ct }, t =>
        {
            if (ct.IsCancellationRequested) return;
            var rng = new Random(unchecked(Environment.TickCount * 73856093 + t * 19349663));
            int maxBand = bands[2];
            var orbitR = new double[maxBand];
            var orbitI = new double[maxBand];

            for (int s = 0; s < perThread; s++)
            {
                // Sample c uniformly in [-2.5..1.5] × [-1.5..1.5].
                double cx = -2.5 + rng.NextDouble() * 4.0;
                double cy = -1.5 + rng.NextDouble() * 3.0;

                // Iterate; record orbit.
                double zr = 0, zi = 0;
                int iter;
                for (iter = 0; iter < maxBand; iter++)
                {
                    orbitR[iter] = zr;
                    orbitI[iter] = zi;
                    double zr2 = zr * zr, zi2 = zi * zi;
                    if (zr2 + zi2 > 4.0) break;
                    double newZr = zr2 - zi2 + cx;
                    zi = 2.0 * zr * zi + cy;
                    zr = newZr;
                }
                if (iter == maxBand) continue; // didn't escape — skip

                // Pick which channel buffer based on which band the escape fell into.
                uint[] target;
                if (iter < bands[0]) target = localR[t];
                else if (iter < bands[1]) target = localG[t];
                else target = localB[t];

                for (int k = 0; k < iter; k++)
                {
                    double ozr = orbitR[k], ozi = orbitI[k];
                    int ix = (int)((ozr - midX) / scale + width * 0.5);
                    int iy = (int)((ozi - midY) / scale + height * 0.5);
                    if ((uint)ix < (uint)width && (uint)iy < (uint)height)
                        target[iy * width + ix]++;
                }
            }
        });

        for (int t = 0; t < threads; t++)
        {
            var lR = localR[t]; var lG = localG[t]; var lB = localB[t];
            for (int i = 0; i < _hitsR.Length; i++)
            {
                _hitsR[i] += lR[i];
                _hitsG[i] += lG[i];
                _hitsB[i] += lB[i];
            }
        }

        // Compute per-channel max for normalization.
        uint maxR = 0, maxG = 0, maxB = 0;
        for (int i = 0; i < _hitsR.Length; i++)
        {
            if (_hitsR[i] > maxR) maxR = _hitsR[i];
            if (_hitsG[i] > maxG) maxG = _hitsG[i];
            if (_hitsB[i] > maxB) maxB = _hitsB[i];
        }
        double invR = maxR > 1 ? 1.0 / Math.Log(maxR + 1) : 1.0;
        double invG = maxG > 1 ? 1.0 / Math.Log(maxG + 1) : 1.0;
        double invB = maxB > 1 ? 1.0 / Math.Log(maxB + 1) : 1.0;

        for (int i = 0; i < _hitsR.Length; i++)
        {
            double r = Math.Log(_hitsR[i] + 1) * invR;
            double g = Math.Log(_hitsG[i] + 1) * invG;
            double b = Math.Log(_hitsB[i] + 1) * invB;
            byte R = (byte)Math.Clamp(r * 255, 0, 255);
            byte G = (byte)Math.Clamp(g * 255, 0, 255);
            byte B = (byte)Math.Clamp(b * 255, 0, 255);
            ColorBuffer[i] = 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
        }
    }
}
