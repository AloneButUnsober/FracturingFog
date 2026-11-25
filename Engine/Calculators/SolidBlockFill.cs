// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// SolidBlockFill.cs (#2049, physics epic #2048; the #1970 black hole solid blocks, moved here)
//
// Solid blocks for basin maps: the frame is cut into Block x Block tiles; each tile's outer
// ring is traced, and when every ring pixel has the same fillable outcome the inside is filled
// from the ring instead of traced. This is the rectangle idea of Mariani-Silver subdivision
// (FF's SolidRegionSubdivision, #1343 / #1394) at a single tile size, for scattering families
// whose pixel colour is a function of the outcome alone (a fill is then exact when it is right).
// The known risk is an island of another outcome wholly inside a tile, which the ring cannot
// see; the families keep it opt-in and their tests measure it.
//
// Moved from BlackHoleShadowCalculator (#1970) without change so Hénon-Heiles (#2049) and the
// later ODE families share it.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace FracturingFog;

internal static class SolidBlockFill
{
    /// <summary>Tile size in pixels (the ring is traced, the inside filled).</summary>
    public const int Block = 16;

    /// <summary>Run the frame. <paramref name="trace"/> computes pixel (x, y) into the frame's buffers;
    /// <paramref name="outcome"/> reads a computed pixel's outcome; <paramref name="fillable"/> says
    /// whether a uniform ring of that outcome may be filled; <paramref name="fill"/> copies pixel
    /// <c>from</c> into pixel <c>to</c>. Returns the number of filled pixels.</summary>
    public static long Run(int width, int height, Action<int, int> trace, Func<int, int> outcome,
        Func<int, bool> fillable, Action<int, int> fill, CancellationToken ct)
    {
        long filled = 0;
        int bw = (width + Block - 1) / Block, bh = (height + Block - 1) / Block;
        Parallel.For(0, bw * bh, new ParallelOptions { CancellationToken = ct }, b =>
        {
            if (ct.IsCancellationRequested) return;
            int x0 = (b % bw) * Block, y0 = (b / bw) * Block;
            int x1 = Math.Min(width, x0 + Block), y1 = Math.Min(height, y0 + Block);
            if (x1 - x0 < 3 || y1 - y0 < 3)
            {
                for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) trace(x, y);
                return;
            }
            for (int x = x0; x < x1; x++) { trace(x, y0); trace(x, y1 - 1); }
            for (int y = y0 + 1; y < y1 - 1; y++) { trace(x0, y); trace(x1 - 1, y); }
            int g0 = outcome(y0 * width + x0);
            bool uniform = fillable(g0);
            for (int x = x0; x < x1 && uniform; x++)
                uniform = outcome(y0 * width + x) == g0 && outcome((y1 - 1) * width + x) == g0;
            for (int y = y0 + 1; y < y1 - 1 && uniform; y++)
                uniform = outcome(y * width + x0) == g0 && outcome(y * width + x1 - 1) == g0;
            if (uniform)
            {
                int from = y0 * width + x0;
                for (int y = y0 + 1; y < y1 - 1; y++)
                    for (int x = x0 + 1; x < x1 - 1; x++) fill(from, y * width + x);
                Interlocked.Add(ref filled, (long)(x1 - x0 - 2) * (y1 - y0 - 2));
            }
            else
            {
                for (int y = y0 + 1; y < y1 - 1; y++) for (int x = x0 + 1; x < x1 - 1; x++) trace(x, y);
            }
        });
        return filled;
    }
}
