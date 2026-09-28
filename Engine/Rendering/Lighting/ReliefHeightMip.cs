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

    /// <summary>Conservative empty-space-skip distance along a ray at world point
    /// (px,py,pz). Looks up the coarse block max height; if the point is above it by
    /// more than the hit tolerance <paramref name="epsT"/>, returns the min of the
    /// distance to descend to <paramref name="epsT"/> above the block max and the
    /// distance to exit the block's XZ cell — no terrain can be hit within that span.
    /// Returns 0 (fall back to the point DE) otherwise. Shared by the CPU relief
    /// trace and the GPU parity twin; the HLSL <c>EmptySkipDist</c> mirrors it.</summary>
    public static double EmptySkipDist(double px, double py, double pz,
        double rdx, double rdy, double rdz, double epsT,
        double aspect, double sy, float[] mip, int mipW, int mipH)
    {
        double uu = px / aspect + 0.5, vv = pz + 0.5;
        int cx = (int)Math.Floor(uu * mipW);
        int cz = (int)Math.Floor(vv * mipH);
        if (cx < 0) cx = 0; else if (cx > mipW - 1) cx = mipW - 1;
        if (cz < 0) cz = 0; else if (cz > mipH - 1) cz = mipH - 1;
        double hmax = mip[cz * mipW + cx] * sy;
        if (py <= hmax + epsT) return 0.0;

        // Descend to epsT ABOVE the block max (not the plane itself) so the normal
        // march resumes with a tight hit-refine bracket instead of one spanning the
        // whole leap. Still conservative (y stays ≥ hmax over the span).
        double tPlane = rdy < -1e-9 ? (py - (hmax + epsT)) / (-rdy) : double.MaxValue;

        // Lateral exit of this coarse cell's world XZ AABB.
        double xLo = (cx / (double)mipW - 0.5) * aspect;
        double xHi = ((cx + 1) / (double)mipW - 0.5) * aspect;
        double zLo = cz / (double)mipH - 0.5;
        double zHi = (cz + 1) / (double)mipH - 0.5;
        double tExit = double.MaxValue;
        if (rdx > 1e-12) tExit = Math.Min(tExit, (xHi - px) / rdx);
        else if (rdx < -1e-12) tExit = Math.Min(tExit, (xLo - px) / rdx);
        if (rdz > 1e-12) tExit = Math.Min(tExit, (zHi - pz) / rdz);
        else if (rdz < -1e-12) tExit = Math.Min(tExit, (zLo - pz) / rdz);

        double skip = Math.Min(tPlane, tExit);
        return skip > 0.0 ? skip : 0.0;
    }
}
