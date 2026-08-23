// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering/Lighting/FroxelHistory.cs
//
// Roadmap slice S6 (3D-Rendering-Roadmap.md, parent #389 / issue #408) — temporal
// reprojection for the froxel volume. The froxel populate is a stable 3D LUT; the
// moment the Scene Engine animates fog (drifting FBM noise, pulsing density, moving
// lights) the single-frame volume flickers frame-to-frame. This holds the PREVIOUS
// frame's per-cell scattering + extinction and exponentially blends the current
// frame into it BEFORE integration (Frostbite/Hillaire's temporal accumulation, on
// the pre-integration grid so energy conservation is preserved).
//
// Reprojection model (first slice): the froxel grid is camera-framed (an axis-
// aligned depth slab). While the grid identity — dims + near/far — is unchanged
// frame-to-frame, cell (cx,cy,z) maps to the SAME cell in the previous frame, so
// the blend is identity per cell (no resampling). When the camera moves enough to
// change near/far, the grid key changes and history is invalidated (a=0) so the
// volume re-seeds cleanly that frame — the same disocclusion fallback temporal AA
// uses. Sub-cell reprojection under continuous camera motion is a documented
// follow-up.
//
// Pure + deterministic (no RNG, no device state): given the same history state +
// current grid it always produces the same blend, so it is --batch-stable and
// twinnable. Caller-owned (one instance per render host / scene track); a null
// history or feedback 0 leaves the single-frame path byte-identical.

using System;

namespace FracturingFog.Rendering.Lighting;

/// <summary>Persistent previous-frame froxel scattering + extinction for temporal
/// reprojection (roadmap S6, #408). Caller-owned; see the file header.</summary>
public sealed class FroxelHistory
{
    // Previous frame's (already-blended) per-cell scatter RGB + extinction, laid
    // out column-major exactly like FroxelVolumePass (index = (cy*nx+cx)*nz + z).
    private double[]? _scR, _scG, _scB, _ext;
    private long _key;      // grid identity (dims + near/far) the history was built for
    private int _cells;
    private bool _valid;

    /// <summary>Grid identity key from dims + near/far. History is only reused when
    /// this matches (else the camera grid changed → re-seed).</summary>
    public static long GridKey(FroxelGrid g)
    {
        // Mix the integer dims with the bit patterns of near/far. Order-sensitive
        // rotate so (near,far) and (far,near) differ.
        unchecked
        {
            long k = g.DimX;
            k = k * 1000003 + g.DimY;
            k = k * 1000003 + g.DimZ;
            k = k * 1000003 + BitConverter.DoubleToInt64Bits(g.Near);
            k = k * 1000003 + BitConverter.DoubleToInt64Bits(g.Far);
            return k;
        }
    }

    /// <summary>Whether a valid history exists for grid <paramref name="key"/> with at
    /// least <paramref name="cells"/> cells.</summary>
    public bool IsValidFor(long key, int cells) =>
        _valid && _key == key && _cells >= cells && _scR != null;

    /// <summary>Blend the current per-cell scatter (<paramref name="scR"/>/G/B) +
    /// extinction (<paramref name="ext"/>) IN PLACE with the stored history, then store
    /// the blended result as the new history. When no matching history exists (first
    /// frame or a grid-key change) the current values pass through unchanged (a=0) and
    /// become the seed. <paramref name="feedback"/> is the history weight in [0,1):
    /// out = current·(1-a) + history·a.</summary>
    public void BlendAndStore(double[] scR, double[] scG, double[] scB, double[] ext,
        int cells, long key, double feedback)
    {
        if (scR == null) throw new ArgumentNullException(nameof(scR));
        if (scG == null) throw new ArgumentNullException(nameof(scG));
        if (scB == null) throw new ArgumentNullException(nameof(scB));
        if (ext == null) throw new ArgumentNullException(nameof(ext));
        if (cells <= 0) return;

        double a = feedback;
        if (a < 0.0) a = 0.0; else if (a > 0.999) a = 0.999;   // keep some current in

        if (a > 0.0 && IsValidFor(key, cells))
        {
            double omA = 1.0 - a;
            for (int i = 0; i < cells; i++)
            {
                scR[i] = scR[i] * omA + _scR![i] * a;
                scG[i] = scG[i] * omA + _scG![i] * a;
                scB[i] = scB[i] * omA + _scB![i] * a;
                ext[i] = ext[i] * omA + _ext![i] * a;
            }
        }

        // Store the (possibly blended) current frame as the new history seed.
        EnsureCapacity(cells);
        Array.Copy(scR, _scR!, cells);
        Array.Copy(scG, _scG!, cells);
        Array.Copy(scB, _scB!, cells);
        Array.Copy(ext, _ext!, cells);
        _key = key;
        _cells = cells;
        _valid = true;
    }

    /// <summary>Drop the stored history so the next frame re-seeds (e.g. on a scene
    /// cut or when temporal is toggled off then on).</summary>
    public void Reset() => _valid = false;

    private void EnsureCapacity(int cells)
    {
        if (_scR != null && _scR.Length >= cells) return;
        _scR = new double[cells];
        _scG = new double[cells];
        _scB = new double[cells];
        _ext = new double[cells];
    }
}
