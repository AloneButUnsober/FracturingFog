// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ReliefHeightMip.cs — Relief 3D Slice 4f (#170) empty-space-skip acceleration.
//
// A coarse max-height grid over the compressed relief field (hbuf, hw*hh raw
// cells, pre-*sy). Each coarse cell holds the MAXIMUM raw height over the
// blk*blk base region it covers, expanded by a one-cell halo so it stays a
// conservative upper bound even for the sphere trace's bilinear height sample
// (which reads the neighbouring base cell at a block boundary).
//
// The sphere trace consults this grid to leap the empty air above flat interior:
// when the ray point is above the block max by more than the hit epsilon, no
// terrain in the block can be hit until the ray either descends to the block-max
// plane or exits the block laterally — so it advances by that (conservative)
// distance instead of the slope-limited point DE, which crawls near steep walls.
//
// Pure function of (hbuf, hw, hh, blk): the CPU parity twin and BOTH GPU kernels
// build the identical grid (kernels upload it as the t3 SRV), so the skip stays
// in lockstep across backends. Building is O(hw*hh) once per dispatch — cheap
// beside the raymarch; a GPU-side reduction is a deferred micro-opt.

using System;

namespace FracturingFog.Rendering.Lighting;

/// <summary>Slice 4f (#170) — coarse max-height grid builder for the relief
/// empty-space-skip. See the file header for the conservative-bound rationale.</summary>
public static class ReliefHeightMip
{
    /// <summary>Base cells per coarse cell (both axes). 8 keeps the grid tiny
    /// (~1/64 of the field) while still bounding a useful leap distance.</summary>
    public const int Blk = 8;

    /// <summary>Coarse-grid dimension for a field axis of length <paramref name="n"/>
    /// at block size <paramref name="blk"/> (ceil-div, ≥ 1).</summary>
    public static int GridDim(int n, int blk) => Math.Max(1, (n + blk - 1) / blk);

    /// <summary>Build the max-height grid: <c>grid[cz*mw + cx]</c> = max raw
    /// <paramref name="hbuf"/> value over the block at (cx, cz) expanded by a
    /// one-cell halo. <paramref name="mw"/>/<paramref name="mh"/> receive the grid
    /// dimensions. Raw (pre-*sy) — the consumer multiplies by the world height
    /// scale.</summary>
    public static float[] BuildMaxGrid(float[] hbuf, int hw, int hh, int blk,
                                       out int mw, out int mh)
    {
        if (blk < 1) blk = 1;
        mw = GridDim(hw, blk);
        mh = GridDim(hh, blk);
        var grid = new float[mw * mh];
        for (int cz = 0; cz < mh; cz++)
        for (int cx = 0; cx < mw; cx++)
        {
            // Halo of one base cell on every side covers the bilinear neighbour
            // read at block edges (SampleHeight fetches x0 and x0+1).
            int x0 = cx * blk - 1, x1 = (cx + 1) * blk;       // inclusive base range
            int z0 = cz * blk - 1, z1 = (cz + 1) * blk;
            if (x0 < 0) x0 = 0; if (x1 > hw - 1) x1 = hw - 1;
            if (z0 < 0) z0 = 0; if (z1 > hh - 1) z1 = hh - 1;
            float m = float.NegativeInfinity;
            for (int y = z0; y <= z1; y++)
            {
                int row = y * hw;
                for (int x = x0; x <= x1; x++)
                {
                    float v = hbuf[row + x];
                    if (v > m) m = v;
                }
            }
            grid[cz * mw + cx] = m;
        }
        return grid;
    }
}
