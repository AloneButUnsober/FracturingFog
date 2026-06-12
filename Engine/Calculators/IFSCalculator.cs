// IFSCalculator.cs
//
// Iterated Function System renderer via chaos game. Picks one affine map per
// step (weighted), applies it to the current point, and increments a hit
// counter at the corresponding pixel. After N iterations, log-tone-maps the
// density buffer through the active IColorMap.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed class IFSCalculator : IFractalCalculator
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    public double CenterX { get; set; } = 0.0;
    public double CenterY { get; set; } = 0.0;
    public double Zoom { get; set; } = 1.0;
    public int MaxIterations { get; set; } = 0; // not used by IFS

    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();

    public bool SupportsZoomPan => true;

    public FractalParameters FractalParameters { get; set; } = new();

    /// <summary>Per-pixel hit counts.</summary>
    private uint[] _hits = Array.Empty<uint>();

    public IFSCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        int n = width * height;
        ColorBuffer = new uint[n];
        _hits = new uint[n];
    }

    public void Calculate(CancellationToken ct = default)
    {
        Array.Clear(_hits);

        var maps = FractalParameters.IFSMaps
            ?? (IFSPresets.All.TryGetValue(FractalParameters.IFSPresetName, out var preset) ? preset : IFSPresets.All["Sierpinski Triangle"]);

        if (maps.Count == 0) return;

        // Cumulative weights for fast weighted sampling.
        var cum = new double[maps.Count];
        double sum = 0;
        for (int i = 0; i < maps.Count; i++)
        {
            sum += maps[i].Weight;
            cum[i] = sum;
        }
        if (sum <= 0) return;

        // Compute attractor bbox once by running a settle iteration.
        ComputeAttractorBBox(maps, cum, sum, out double minX, out double maxX, out double minY, out double maxY);

        double spanX = Math.Max(1e-9, maxX - minX);
        double spanY = Math.Max(1e-9, maxY - minY);
        double worldSpan = Math.Max(spanX, spanY);

        // Map attractor → world units so its larger span occupies ~3 units.
        // Mandelbrot convention: pixelScale = (3.5 / maxDim) / zoom, screen
        // center at world (CenterX, CenterY). Pan and zoom from MainForm
        // therefore work identically across all calculators.
        double mapFit = 3.0 / worldSpan;
        double mx = (minX + maxX) * 0.5;
        double my = (minY + maxY) * 0.5;
        double pixelScale = (3.5 / Math.Max(Width, Height)) / Zoom;

        int width = Width;
        int height = Height;
        int iterations = FractalParameters.IFSIterations;
        double centerX = CenterX;
        double centerY = CenterY;

        int threadCount = Math.Max(1, Environment.ProcessorCount);
        int perThread = iterations / threadCount;

        // Per-thread local hit buffers to avoid contention.
        var localBuffers = new uint[threadCount][];
        for (int t = 0; t < threadCount; t++) localBuffers[t] = new uint[width * height];

        Parallel.For(0, threadCount, new ParallelOptions { CancellationToken = ct }, t =>
        {
            if (ct.IsCancellationRequested) return;
            var rng = new Random(unchecked(Environment.TickCount * 73856093 + t * 19349663));
            var local = localBuffers[t];

            // Warm up to settle on attractor.
            double x = 0, y = 0;
            for (int i = 0; i < 20; i++)
            {
                int idx = PickMap(rng, cum, sum);
                ApplyMap(maps[idx], ref x, ref y);
            }

            for (int i = 0; i < perThread; i++)
            {
                int idx = PickMap(rng, cum, sum);
                ApplyMap(maps[idx], ref x, ref y);

                // Attractor-native → world (centered + scaled). Flip Y so
                // positive y in attractor-space appears at the TOP of screen
                // (matches the way fern / dragon / etc. are conventionally drawn).
                double worldX = (x - mx) * mapFit;
                double worldY = -(y - my) * mapFit;

                // World → pixel (Mandelbrot convention).
                int ix = (int)((worldX - centerX) / pixelScale + width * 0.5);
                int iy = (int)((worldY - centerY) / pixelScale + height * 0.5);
                if ((uint)ix < (uint)width && (uint)iy < (uint)height)
                    local[iy * width + ix]++;
            }
        });

        // Reduce per-thread buffers.
        for (int t = 0; t < threadCount; t++)
        {
            var local = localBuffers[t];
            for (int i = 0; i < _hits.Length; i++) _hits[i] += local[i];
        }

        // Tone-map via log-density + IColorMap.
        uint maxHit = 0;
        for (int i = 0; i < _hits.Length; i++) if (_hits[i] > maxHit) maxHit = _hits[i];
        double invLogMax = maxHit > 1 ? 1.0 / Math.Log(maxHit + 1) : 1.0;

        ColorMap.MaxIterations = 256;
        for (int i = 0; i < _hits.Length; i++)
        {
            uint h = _hits[i];
            if (h == 0)
            {
                ColorBuffer[i] = ColorMap.InSetColor;
                continue;
            }
            double norm = Math.Log(h + 1) * invLogMax;
            float smooth = (float)(norm * 256);
            ColorBuffer[i] = (uint)ColorMap.Map(smooth, 0f, 256);
        }
    }

    private static void ComputeAttractorBBox(
        List<AffineMap> maps, double[] cum, double sum,
        out double minX, out double maxX, out double minY, out double maxY)
    {
        var rng = new Random(42);
        double x = 0, y = 0;
        // Warm-up.
        for (int i = 0; i < 100; i++)
        {
            int idx = PickMap(rng, cum, sum);
            ApplyMap(maps[idx], ref x, ref y);
        }
        minX = maxX = x;
        minY = maxY = y;
        for (int i = 0; i < 20_000; i++)
        {
            int idx = PickMap(rng, cum, sum);
            ApplyMap(maps[idx], ref x, ref y);
            if (x < minX) minX = x; else if (x > maxX) maxX = x;
            if (y < minY) minY = y; else if (y > maxY) maxY = y;
        }
    }

    private static int PickMap(Random rng, double[] cum, double sum)
    {
        double r = rng.NextDouble() * sum;
        for (int i = 0; i < cum.Length; i++)
            if (r <= cum[i]) return i;
        return cum.Length - 1;
    }

    private static void ApplyMap(AffineMap m, ref double x, ref double y)
    {
        double nx = m.A * x + m.B * y + m.E;
        double ny = m.C * x + m.D * y + m.F;
        x = nx; y = ny;
    }
}
