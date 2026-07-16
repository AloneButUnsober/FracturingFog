// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// DlaCalculator.cs
//
// Diffusion-Limited Aggregation. A single seed cell sits at the grid centre.
// Particles spawn on a launch circle just outside the current aggregate's
// bounding radius, random-walk one cell per step, and stick the first time
// they land in a cell adjacent to the aggregate. Walks that wander past a
// kill radius restart from a fresh launch point — this is the standard
// Witten–Sander 1981 optimisation that drops simulation cost from
// O(N · grid_area) to roughly O(N²·log N).
//
// The fractal is the colour-by-arrival aggregate: each stuck cell records its
// arrival index, then the colour pass maps arrival/N through the active
// IColorMap so early-stuck cells differ from late-stuck cells, exposing the
// branching dendritic structure.
//
// Determinism: a single Random seeded by DlaSeed drives both launch-angle
// and walk-step draws, so (Width, Height, DlaSeed, DlaParticles) uniquely
// determines the output. SupportsZoomPan is false — the simulation IS the
// image, and pan/zoom can't reuse partial state.

using System;
using System.Threading;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed class DlaCalculator : IFractalCalculator
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

    public bool SupportsZoomPan => false;

    public FractalParameters FractalParameters { get; set; } = new();

    public DlaCalculator(int width, int height) => Resize(width, height);

    public void Resize(int width, int height)
    {
        Width = width;
        Height = height;
        ColorBuffer = new uint[width * height];
    }

    public void Calculate(CancellationToken ct = default)
    {
        Array.Clear(ColorBuffer, 0, ColorBuffer.Length);

        int particles = Math.Max(1, FractalParameters.DlaParticles);
        int seed      = FractalParameters.DlaSeed;
        var rng = new Random(seed);

        // Arrival-index grid. 0 = vacuum, ≥ 1 = the (1-based) step at which
        // that cell joined the aggregate. We use int (not bool) so the colour
        // pass can map arrival count → palette index directly.
        var grid = new int[Width * Height];
        int cx0 = Width / 2;
        int cy0 = Height / 2;
        grid[cy0 * Width + cx0] = 1;

        int gridMaxX = Width - 2;
        int gridMaxY = Height - 2;
        int hostStuck = 1;
        int maxR2 = 1;          // squared current-aggregate bounding radius
        ColorMap.MaxIterations = Math.Max(8, particles);

        // Launch circle radius = sqrt(maxR2) + spawnMargin (always at least
        // a few cells outside the aggregate). Kill radius = ~3× launch so
        // wanderers don't bias the joining distribution.
        const int spawnMargin = 4;

        for (int n = 1; n <= particles; n++)
        {
            if (ct.IsCancellationRequested) return;
            // Spawn on the launch circle.
            int launchR = (int)Math.Ceiling(Math.Sqrt(maxR2)) + spawnMargin;
            int killR = launchR * 3 + 8;
            int killR2 = killR * killR;

            int px = 0, py = 0;
            bool stuck = false;
            // A bounded number of "particle-life" restarts protect against the
            // rare case where every walk from this seed wanders into kill
            // territory.
            for (int restart = 0; restart < 16 && !stuck; restart++)
            {
                double angle = rng.NextDouble() * Math.PI * 2.0;
                px = cx0 + (int)Math.Round(Math.Cos(angle) * launchR);
                py = cy0 + (int)Math.Round(Math.Sin(angle) * launchR);

                // Bound the walk so a runaway particle terminates instead of
                // hogging the simulation indefinitely. The cap scales with
                // launchR so dense aggregates don't starve.
                int walkCap = launchR * launchR * 32;
                for (int step = 0; step < walkCap; step++)
                {
                    // Stick if any of the 4-neighbours is already aggregated.
                    if (px > 0 && px < Width - 1 && py > 0 && py < Height - 1)
                    {
                        int g = py * Width + px;
                        if (grid[g - 1] != 0 || grid[g + 1] != 0
                         || grid[g - Width] != 0 || grid[g + Width] != 0)
                        {
                            grid[g] = n + 1;
                            hostStuck++;
                            int dx = px - cx0; int dy = py - cy0;
                            int r2 = dx * dx + dy * dy;
                            if (r2 > maxR2) maxR2 = r2;
                            stuck = true;
                            break;
                        }
                    }

                    // Take one step. Drop a single random bit per axis: 4
                    // diagonal moves are simpler than the canonical 4-neighbour
                    // walk and converge to the same limit dimension (≈ 1.71).
                    int r = rng.Next(0, 4);
                    switch (r)
                    {
                        case 0: px--; break;
                        case 1: px++; break;
                        case 2: py--; break;
                        default: py++; break;
                    }

                    // Out-of-bounds / past-kill restart.
                    int kdx = px - cx0; int kdy = py - cy0;
                    if (kdx * kdx + kdy * kdy > killR2
                        || px <= 0 || py <= 0
                        || px >= gridMaxX || py >= gridMaxY)
                        break;
                }
            }
        }

        if (ct.IsCancellationRequested) return;

        // Colour pass — arrival index drives palette. Vacuum stays at the
        // ColorBuffer's pre-cleared 0 (transparent black).
        int totalArrivals = Math.Max(1, hostStuck);
        for (int j = 0; j < Height; j++)
        {
            if (ct.IsCancellationRequested) return;
            int row = j * Width;
            for (int i = 0; i < Width; i++)
            {
                int a = grid[row + i];
                if (a == 0) continue;
                // Normalise to [0, 256) so the palette sees a familiar range.
                float t = (float)(a - 1) * 256f / totalArrivals;
                ColorBuffer[row + i] = (uint)ColorMap.Map(t, 0f, 256);
            }
        }
    }
}
