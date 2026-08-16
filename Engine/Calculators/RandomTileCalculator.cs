// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// RandomTileCalculator.cs
//
// Random space filling of the plane — Paul Bourke,
// https://paulbourke.net/fractals/randomtile/
//
// Non-escape-time 2D packing. Shapes of power-law-decreasing size are placed at
// random, non-overlapping positions until the plane is filled (or the shape
// count / sub-pixel floor is hit). The i-th shape radius follows
//   r_i = rMax / (i + 1)^(1/α)
// where α (RandomTileSizeExponent) controls the size falloff: larger α yields a
// few big shapes plus a heavy tail of tiny ones. Each candidate position is
// drawn from a single seeded PRNG (RandomTileSeed) and accepted only when it
// clears every previously placed shape by RandomTileGap; rejected candidates
// retry up to a fixed attempt budget, then that index is skipped (the radius
// keeps shrinking, so later shapes slot into the remaining gaps).
//
// Overlap test uses a uniform spatial-hash grid (cell = rMax), so each candidate
// only checks the shapes in its neighbourhood — ~O(N) placement instead of the
// naive O(N²). Same acceleration DlaCalculator uses for aggregate proximity.
//
// Determinism: (Width, Height, Seed, Count, SizeExponent, Gap) uniquely
// determine the output — one seeded Random drives every draw in a fixed order.
// SupportsZoomPan is false: the packing IS the image and pan/zoom can't reuse it
// (mirrors DLA). Relief: each disk is a raised sphere-cap dome written to
// SmoothBuffer, so Relief3D / 3D themes / volumetric ride the shared
// IHeightFieldSource path exactly as ApollonianCalculator does.

using System;
using System.Collections.Generic;
using System.Threading;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed class RandomTileCalculator : IFractalCalculator, IHeightFieldSource
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] ColorBuffer { get; private set; } = Array.Empty<uint>();

    // Relief 3D height field. RandomTile has no escape/iteration count, so the
    // relief is synthesised geometrically: each shape is a raised dome
    // (sphere-cap profile, unit amplitude), matching the Apollonian sphere-
    // imposter shading. Written during rasterisation; smallest (last-painted)
    // shape wins per pixel.
    public float[] SmoothBuffer { get; private set; } = Array.Empty<float>();

    public double CenterX { get; set; } = 0.0;
    public double CenterY { get; set; } = 0.0;
    public double Zoom { get; set; } = 1.0;
    public int MaxIterations { get; set; } = 0;

    public QualityPreset Quality { get; set; } = QualityPreset.Standard;
    public IColorMap ColorMap { get; set; } = new HsvPalette();

    // The packing IS the image; pan/zoom would invalidate the cached tiling.
    public bool SupportsZoomPan => false;

    public FractalParameters FractalParameters { get; set; } = new();

    // Dome relief amplitude for the sphere-imposter paint path (see PaintDisk).
    private double _relief = 1.0;

    public RandomTileCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[width * height];
        SmoothBuffer = new float[width * height];
    }

    private readonly struct Tile
    {
        public readonly double X;      // world centre
        public readonly double Y;
        public readonly double R;      // world radius
        public readonly int Index;     // placement order (0 = biggest)
        public Tile(double x, double y, double r, int index) { X = x; Y = y; R = r; Index = index; }
    }

    public void Calculate(CancellationToken ct = default)
    {
        Array.Clear(ColorBuffer, 0, ColorBuffer.Length);
        Array.Clear(SmoothBuffer, 0, SmoothBuffer.Length);   // relief base plane

        int count = Math.Max(0, FractalParameters.RandomTileCount);
        double alpha = Math.Max(0.2, FractalParameters.RandomTileSizeExponent);
        double gap = Math.Max(0.0, FractalParameters.RandomTileGap);
        double minPx = Math.Max(0.25, FractalParameters.RandomTileMinPixelRadius);
        _relief = Math.Clamp(FractalParameters.RandomTileRelief, 0.0, 4.0);

        // World→screen scale matches the standard 2D convention: pixel pitch =
        // (4 / Width) / Zoom, fractal origin at (CenterX, CenterY).
        double pixelPitch = (4.0 / Math.Max(1, Width)) / Math.Max(1e-12, Zoom);
        double minWorldR = minPx * pixelPitch;

        // Visible world half-extents (the domain we fill).
        double halfW = Width * 0.5 * pixelPitch;
        double halfH = Height * 0.5 * pixelPitch;
        double x0 = CenterX - halfW, x1 = CenterX + halfW;
        double y0 = CenterY - halfH, y1 = CenterY + halfH;

        // Biggest tile ≈ 0.5× the smaller domain half-extent, so the largest few
        // read as clear anchors without swallowing the whole frame.
        double rMax = 0.5 * Math.Min(halfW, halfH);
        if (rMax < minWorldR || count == 0) return;

        // Colour spans the palette across placement order (big → small) when
        // colouring by index; the log-radius fallback emphasises scale instead.
        ColorMap.MaxIterations = Math.Max(16, count);

        // Uniform spatial hash. Cell = rMax; two tiles overlap only if their
        // centres are within r_a + r_b ≤ 2·rMax, so a candidate need only probe
        // cells within ⌈(r_cand + rMax)/cell⌉ = 2 of its own. Cell indices are
        // domain-relative (offset by x0/y0) and packed into a long key.
        double cell = Math.Max(rMax, 1e-12);
        var grid = new Dictionary<long, List<int>>(Math.Max(64, count));
        var tiles = new List<Tile>(count);

        static long PackCell(int cx, int cy) => ((long)cx << 32) ^ (uint)cy;
        int CellX(double wx) => (int)Math.Floor((wx - x0) / cell);
        int CellY(double wy) => (int)Math.Floor((wy - y0) / cell);

        bool Overlaps(double cx, double cy, double r, double margin)
        {
            int ccx = CellX(cx), ccy = CellY(cy);
            for (int dx = -2; dx <= 2; dx++)
            for (int dy = -2; dy <= 2; dy++)
            {
                if (!grid.TryGetValue(PackCell(ccx + dx, ccy + dy), out var bucket))
                    continue;
                foreach (int ti in bucket)
                {
                    var t = tiles[ti];
                    double ex = t.X - cx, ey = t.Y - cy;
                    double minDist = t.R + r + margin;
                    if (ex * ex + ey * ey < minDist * minDist) return true;
                }
            }
            return false;
        }

        var rng = new Random(FractalParameters.RandomTileSeed);
        const int maxAttempts = 200;

        for (int i = 0; i < count; i++)
        {
            if ((i & 0x3FF) == 0 && ct.IsCancellationRequested) return;

            double r = rMax / Math.Pow(i + 1, 1.0 / alpha);
            if (r < minWorldR) break;                       // sub-pixel — done

            double margin = gap * r;
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                // Draw a centre so the shape fits inside the visible domain.
                double cxw = x0 + r + rng.NextDouble() * Math.Max(0.0, (x1 - x0) - 2 * r);
                double cyw = y0 + r + rng.NextDouble() * Math.Max(0.0, (y1 - y0) - 2 * r);

                if (Overlaps(cxw, cyw, r, margin)) continue;

                int idx = tiles.Count;
                tiles.Add(new Tile(cxw, cyw, r, i));
                long key = PackCell(CellX(cxw), CellY(cyw));
                if (!grid.TryGetValue(key, out var b))
                    grid[key] = b = new List<int>(4);
                b.Add(idx);
                break;
                // A skipped index (all attempts overlapped) is fine — the radius
                // keeps shrinking so smaller shapes slot into the remaining gaps.
            }
        }

        // Tiles are generated in strictly-decreasing-radius order, so painting in
        // list order draws biggest first and smaller shapes nest on top.
        foreach (var t in tiles)
        {
            if (ct.IsCancellationRequested) return;
            PaintDisk(t);
        }
    }

    private void PaintDisk(in Tile t)
    {
        double pixelPitch = (4.0 / Math.Max(1, Width)) / Math.Max(1e-12, Zoom);
        if (pixelPitch <= 0.0) return;
        double invPitch = 1.0 / pixelPitch;

        // World → screen (pixel) coords.
        double sx = (t.X - CenterX) * invPitch + Width * 0.5;
        double sy = (t.Y - CenterY) * invPitch + Height * 0.5;
        double sr = t.R * invPitch;

        int x0 = (int)Math.Floor(sx - sr);
        int x1 = (int)Math.Ceiling(sx + sr);
        int y0 = (int)Math.Floor(sy - sr);
        int y1 = (int)Math.Ceiling(sy + sr);

        if (x1 < 0 || y1 < 0 || x0 >= Width || y0 >= Height) return;
        x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
        x1 = Math.Min(Width - 1, x1); y1 = Math.Min(Height - 1, y1);

        // Colour by placement index (palette sweep big→small) or log-radius.
        bool byIndex = FractalParameters.RandomTileColorByIndex;
        float tt = byIndex
            ? t.Index % Math.Max(1, ColorMap.MaxIterations)
            : (float)(Math.Log(Math.Max(1e-12, t.R)) * -16.0);

        double r2 = sr * sr;

        if (_relief <= 0.0)
        {
            // Flat fast path — one colour per disk.
            uint col = (uint)ColorMap.Map(tt, 0f, ColorMap.MaxIterations);
            for (int py = y0; py <= y1; py++)
            {
                double dy = py - sy;
                int row = py * Width;
                for (int px = x0; px <= x1; px++)
                {
                    double dx = px - sx;
                    double dd = dx * dx + dy * dy;
                    if (dd <= r2)
                    {
                        ColorBuffer[row + px] = col;
                        SmoothBuffer[row + px] = (float)Math.Sqrt(1.0 - dd / r2);
                    }
                }
            }
            return;
        }

        // Lit sphere-imposter path — each disk is a dome. In-plane normal grows
        // from centre (flat, facing viewer) to rim (grazing): (u, v) = (dx, dy)
        // / sr, scaled by relief. ny negated for the complex-plane y-up
        // convention the 3D themes expect.
        double invSr = sr > 1e-9 ? 1.0 / sr : 0.0;
        for (int py = y0; py <= y1; py++)
        {
            double dy = py - sy;
            int row = py * Width;
            for (int px = x0; px <= x1; px++)
            {
                double dx = px - sx;
                double dd = dx * dx + dy * dy;
                if (dd > r2) continue;
                float nx = (float)(_relief * dx * invSr);
                float ny = (float)(-_relief * dy * invSr);
                ColorBuffer[row + px] =
                    (uint)ColorMap.Map(tt, 0f, ColorMap.MaxIterations, nx, ny);
                SmoothBuffer[row + px] = (float)Math.Sqrt(1.0 - dd / r2);
            }
        }
    }
}
