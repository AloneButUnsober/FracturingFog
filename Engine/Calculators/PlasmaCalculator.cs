// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// PlasmaCalculator.cs
//
// Diamond-square midpoint-displacement noise. Not strictly a fractal — a
// procedural 2D height field with fractional-Brownian statistics. Lives
// alongside the L-system / IFS calculators because it shares their shape:
// fill ColorBuffer once, no per-pixel iteration, ignores zoom (the
// generated field IS the image).
//
// Algorithm: pick smallest power-of-two grid (n+1) covering max(W, H).
// Seed the four corners with random values in [0, 1]. Repeat:
//   Square step  — each cell centre = avg of 4 corners + jitter
//   Diamond step — each edge midpoint = avg of (up to) 4 neighbours + jitter
// Halve the step. Multiply jitter amplitude by 2^(-roughness): roughness=0
// smooths to flat, roughness=1 keeps the full amplitude (very rough).
// After the field is built, normalise to [0, 1] and sample bilinearly into
// the output buffer, mapped through the active IColorMap.

using System;
using System.Threading;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed class PlasmaCalculator : IFractalCalculator
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    public double CenterX { get; set; } = 0.0;
    public double CenterY { get; set; } = 0.0;
    public double Zoom { get; set; } = 1.0;
    public int MaxIterations { get; set; } = 0;

    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();

    // Plasma is a one-shot procedural fill — pan/zoom would just rescale the
    // same generated field, which is misleading. Disable so the UI hides
    // mini-map zoom anchoring.
    public bool SupportsZoomPan => false;

    public FractalParameters FractalParameters { get; set; } = new();

    public PlasmaCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[width * height];
    }

    public void Calculate(CancellationToken ct = default)
    {
        ColorMap.MaxIterations = 256;

        int maxDim = Math.Max(Width, Height);
        int n = 1;
        while (n + 1 < maxDim) n <<= 1;
        // Hard cap at 4097×4097 (~67 MB float buffer). 4K renders fit at the
        // boundary; beyond that the field gets downsampled.
        if (n > 4096) n = 4096;
        int size = n + 1;
        var grid = new float[size * size];

        double roughness = Math.Clamp(FractalParameters.PlasmaRoughness, 0.0, 1.0);
        var rng = new Random(FractalParameters.PlasmaSeed);

        grid[0]                     = (float)rng.NextDouble();
        grid[n]                     = (float)rng.NextDouble();
        grid[n * size]              = (float)rng.NextDouble();
        grid[n * size + n]          = (float)rng.NextDouble();

        double amp = 1.0;
        double decay = Math.Pow(2.0, -roughness);
        int step = n;

        while (step > 1)
        {
            if (ct.IsCancellationRequested) return;
            int half = step / 2;

            // Square step: cell centres.
            for (int y = half; y < size; y += step)
            {
                int yRow = y * size;
                int yUp = (y - half) * size;
                int yDn = (y + half) * size;
                for (int x = half; x < size; x += step)
                {
                    float a = grid[yUp + (x - half)];
                    float b = grid[yUp + (x + half)];
                    float c = grid[yDn + (x - half)];
                    float d = grid[yDn + (x + half)];
                    float avg = (a + b + c + d) * 0.25f;
                    float jitter = (float)((rng.NextDouble() - 0.5) * amp);
                    grid[yRow + x] = avg + jitter;
                }
            }

            // Diamond step: edge midpoints. Offset rows so we hit the
            // checkerboard of midpoints created by the square step.
            for (int y = 0; y < size; y += half)
            {
                int xStart = ((y / half) % 2 == 0) ? half : 0;
                int yRow = y * size;
                for (int x = xStart; x < size; x += step)
                {
                    float sum = 0f;
                    int count = 0;
                    if (x >= half)        { sum += grid[yRow + (x - half)]; count++; }
                    if (x + half < size)  { sum += grid[yRow + (x + half)]; count++; }
                    if (y >= half)        { sum += grid[(y - half) * size + x]; count++; }
                    if (y + half < size)  { sum += grid[(y + half) * size + x]; count++; }
                    float avg = sum / count;
                    float jitter = (float)((rng.NextDouble() - 0.5) * amp);
                    grid[yRow + x] = avg + jitter;
                }
            }

            step = half;
            amp *= decay;
        }

        if (ct.IsCancellationRequested) return;

        // Normalize to [0, 1].
        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        for (int i = 0; i < grid.Length; i++)
        {
            float v = grid[i];
            if (v < min) min = v;
            if (v > max) max = v;
        }
        float range = max - min;
        if (range < 1e-9f) range = 1f;

        // Bilinear sample grid → output buffer.
        double sx = (double)(size - 1) / Math.Max(1, Width  - 1);
        double sy = (double)(size - 1) / Math.Max(1, Height - 1);
        for (int j = 0; j < Height; j++)
        {
            if (ct.IsCancellationRequested) return;
            double gy = j * sy;
            int gy0 = (int)gy;
            if (gy0 >= size - 1) gy0 = size - 2;
            int gy1 = gy0 + 1;
            float fy = (float)(gy - gy0);
            int row0 = gy0 * size;
            int row1 = gy1 * size;
            int outRow = j * Width;
            for (int i = 0; i < Width; i++)
            {
                double gx = i * sx;
                int gx0 = (int)gx;
                if (gx0 >= size - 1) gx0 = size - 2;
                int gx1 = gx0 + 1;
                float fx = (float)(gx - gx0);
                float v00 = grid[row0 + gx0];
                float v10 = grid[row0 + gx1];
                float v01 = grid[row1 + gx0];
                float v11 = grid[row1 + gx1];
                float top = v00 + (v10 - v00) * fx;
                float bot = v01 + (v11 - v01) * fx;
                float v = top + (bot - top) * fy;
                float t = (v - min) / range;
                ColorBuffer[outRow + i] = (uint)ColorMap.Map(t * 256f, 0f, 256);
            }
        }
    }
}
