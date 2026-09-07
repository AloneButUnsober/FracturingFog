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
// Two reprojection models:
//
//   * Same-cell (BlendAndStore) — the original. The froxel grid is camera-framed;
//     while its identity (dims + near/far) is unchanged, cell (cx,cy,z) maps to the
//     SAME cell in the previous frame, so the blend is identity per cell. A grid-key
//     change (near/far moved) invalidates history (a=0) so the volume re-seeds. This
//     is correct for a STATIC camera but ghosts under continuous camera motion.
//
//   * Sub-cell (BlendAndStoreReproject, #408) — for continuous camera motion. Each
//     current cell's WORLD position (from the current camera basis + grid) is
//     transformed into the PREVIOUS frame's camera-local froxel coordinates and the
//     history is trilinearly resampled there, so a static fog feature stays put in
//     the world as the camera pans / orbits / dollies (a near/far change no longer
//     forces a re-seed — the depth remaps through the previous grid). Cells whose
//     reprojection lands outside the previous frustum are treated as disoccluded
//     (a=0, pass current through) — the same fallback temporal AA uses.
//
// Pure + deterministic (no RNG, no device state): given the same history state +
// current grid/camera it always produces the same blend, so it is --batch-stable and
// twinnable. Caller-owned (one instance per render host / scene track); a null
// history or feedback 0 leaves the single-frame path byte-identical.

using System;

namespace FracturingFog.Rendering.Lighting;

/// <summary>Persistent previous-frame froxel scattering + extinction for temporal
/// reprojection (roadmap S6, #408). Caller-owned; see the file header.</summary>
public sealed class FroxelHistory
{
    /// <summary>Camera basis (world position + right / up / forward unit vectors) a
    /// froxel grid was framed from — enough to map a camera-local froxel cell to /
    /// from world for sub-cell reprojection (roadmap S6, #408).</summary>
    public readonly record struct CamBasis(
        double PosX, double PosY, double PosZ,
        double Rx, double Ry, double Rz,
        double Ux, double Uy, double Uz,
        double Fx, double Fy, double Fz);

    // Previous frame's (already-blended) per-cell scatter RGB + extinction, laid
    // out column-major exactly like FroxelVolumePass (index = (cy*nx+cx)*nz + z).
    private double[]? _scR, _scG, _scB, _ext;
    private long _key;      // grid identity (dims + near/far) the history was built for
    private int _cells;
    private int _nx, _ny, _nz;
    private bool _valid;

    // Reprojection state (#408): the grid + camera basis + lateral extent the stored
    // frame was framed from, so a later frame can resample it in world space.
    private FroxelGrid? _prevGrid;
    private CamBasis _prevCam;
    private double _prevExtent;
    private bool _hasPrevCam;

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

        // Store the (possibly blended) current frame as the new history seed. No camera
        // basis recorded → a later reprojecting frame re-seeds once, then reprojects.
        Store(scR, scG, scB, ext, cells, key);
        _hasPrevCam = false;
    }

    /// <summary>Sub-cell reprojecting blend (roadmap S6, #408). Resamples the stored
    /// history in WORLD space: each current cell's world position (from
    /// <paramref name="cur"/> + <paramref name="grid"/> + <paramref name="extent"/>) is
    /// transformed into the previous frame's camera-local froxel coordinates and the
    /// history is trilinearly sampled there before the exponential blend. Cells whose
    /// reprojection falls outside the previous frustum are disoccluded (pass current
    /// through). Falls back to the same-cell path when no previous camera basis is
    /// stored (first reprojecting frame) or the dims differ. Then stores the blended
    /// grid + this frame's camera basis as the new history.</summary>
    public void BlendAndStoreReproject(double[] scR, double[] scG, double[] scB, double[] ext,
        int nx, int ny, int nz, FroxelGrid grid, in CamBasis cur, double extent,
        double feedback, long key)
    {
        if (scR == null) throw new ArgumentNullException(nameof(scR));
        if (scG == null) throw new ArgumentNullException(nameof(scG));
        if (scB == null) throw new ArgumentNullException(nameof(scB));
        if (ext == null) throw new ArgumentNullException(nameof(ext));
        int cells = nx * ny * nz;
        if (cells <= 0) return;

        double a = feedback;
        if (a < 0.0) a = 0.0; else if (a > 0.999) a = 0.999;

        // Reproject only when we hold a previous frame with a camera basis and matching
        // dims (the arrays are indexed by these dims). Otherwise re-seed this frame.
        bool canReproject = a > 0.0 && _valid && _hasPrevCam && _scR != null
                            && _nx == nx && _ny == ny && _nz == nz && _cells >= cells
                            && extent > 0.0 && _prevExtent > 0.0;

        if (canReproject)
        {
            double omA = 1.0 - a;
            for (int cy = 0; cy < ny; cy++)
            {
                double wy = ((cy + 0.5) / ny * 2.0 - 1.0) * extent;
                for (int cx = 0; cx < nx; cx++)
                {
                    double wx = ((cx + 0.5) / nx * 2.0 - 1.0) * extent;
                    int baseIdx = (cy * nx + cx) * nz;
                    for (int z = 0; z < nz; z++)
                    {
                        double d = 0.5 * (grid.SliceDepth(z) + grid.SliceDepth(z + 1));
                        // Current cell → world.
                        double px = cur.PosX + cur.Fx * d + cur.Rx * wx + cur.Ux * wy;
                        double py = cur.PosY + cur.Fy * d + cur.Ry * wx + cur.Uy * wy;
                        double pz = cur.PosZ + cur.Fz * d + cur.Rz * wx + cur.Uz * wy;
                        // World → previous camera-local froxel coordinates.
                        double relx = px - _prevCam.PosX, rely = py - _prevCam.PosY, relz = pz - _prevCam.PosZ;
                        double d2 = relx * _prevCam.Fx + rely * _prevCam.Fy + relz * _prevCam.Fz;
                        double wx2 = relx * _prevCam.Rx + rely * _prevCam.Ry + relz * _prevCam.Rz;
                        double wy2 = relx * _prevCam.Ux + rely * _prevCam.Uy + relz * _prevCam.Uz;
                        double cxf = (wx2 / _prevExtent + 1.0) * 0.5 * nx - 0.5;
                        double cyf = (wy2 / _prevExtent + 1.0) * 0.5 * ny - 0.5;
                        double zf = _prevGrid!.DepthToSlice(d2) - 0.5;   // cell-centred slice index

                        int idx = baseIdx + z;
                        if (SampleHistoryTrilinear(cxf, cyf, zf, out double hR, out double hG, out double hB, out double hE))
                        {
                            scR[idx] = scR[idx] * omA + hR * a;
                            scG[idx] = scG[idx] * omA + hG * a;
                            scB[idx] = scB[idx] * omA + hB * a;
                            ext[idx] = ext[idx] * omA + hE * a;
                        }
                        // else: disoccluded → keep the current value (a=0 for this cell).
                    }
                }
            }
        }

        Store(scR, scG, scB, ext, cells, key);
        _nx = nx; _ny = ny; _nz = nz;
        _prevGrid = grid; _prevCam = cur; _prevExtent = extent; _hasPrevCam = true;
    }

    /// <summary>Trilinear sample of the stored history at fractional cell coordinates.
    /// Returns false when the cell centre is outside the stored grid (disocclusion).
    /// Corner indices are clamped so an in-bounds centre near an edge still blends.</summary>
    private bool SampleHistoryTrilinear(double cxf, double cyf, double zf,
        out double r, out double g, out double b, out double e)
    {
        r = g = b = e = 0.0;
        if (_scR == null) return false;
        // Reject when the centre falls outside the grid (a real disocclusion), with a
        // half-cell margin so edge cells still resample.
        if (cxf < -0.5 || cxf > _nx - 0.5 || cyf < -0.5 || cyf > _ny - 0.5
            || zf < -0.5 || zf > _nz - 0.5)
            return false;

        int x0 = (int)Math.Floor(cxf), y0 = (int)Math.Floor(cyf), z0 = (int)Math.Floor(zf);
        double fx = cxf - x0, fy = cyf - y0, fz = zf - z0;
        int x1 = x0 + 1, y1 = y0 + 1, z1 = z0 + 1;
        x0 = Clamp(x0, 0, _nx - 1); x1 = Clamp(x1, 0, _nx - 1);
        y0 = Clamp(y0, 0, _ny - 1); y1 = Clamp(y1, 0, _ny - 1);
        z0 = Clamp(z0, 0, _nz - 1); z1 = Clamp(z1, 0, _nz - 1);

        Accum(x0, y0, z0, (1 - fx) * (1 - fy) * (1 - fz), ref r, ref g, ref b, ref e);
        Accum(x1, y0, z0, fx * (1 - fy) * (1 - fz), ref r, ref g, ref b, ref e);
        Accum(x0, y1, z0, (1 - fx) * fy * (1 - fz), ref r, ref g, ref b, ref e);
        Accum(x1, y1, z0, fx * fy * (1 - fz), ref r, ref g, ref b, ref e);
        Accum(x0, y0, z1, (1 - fx) * (1 - fy) * fz, ref r, ref g, ref b, ref e);
        Accum(x1, y0, z1, fx * (1 - fy) * fz, ref r, ref g, ref b, ref e);
        Accum(x0, y1, z1, (1 - fx) * fy * fz, ref r, ref g, ref b, ref e);
        Accum(x1, y1, z1, fx * fy * fz, ref r, ref g, ref b, ref e);
        return true;
    }

    private void Accum(int cx, int cy, int cz, double w,
        ref double r, ref double g, ref double b, ref double e)
    {
        int i = (cy * _nx + cx) * _nz + cz;
        r += _scR![i] * w; g += _scG![i] * w; b += _scB![i] * w; e += _ext![i] * w;
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

    private void Store(double[] scR, double[] scG, double[] scB, double[] ext, int cells, long key)
    {
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
    public void Reset() { _valid = false; _hasPrevCam = false; }

    private void EnsureCapacity(int cells)
    {
        if (_scR != null && _scR.Length >= cells) return;
        _scR = new double[cells];
        _scG = new double[cells];
        _scB = new double[cells];
        _ext = new double[cells];
    }
}
