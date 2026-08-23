// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering/Lighting/FroxelVolumePass.cs
//
// Roadmap slice S6 integration follow-up (3D-Rendering-Roadmap.md, parent #389):
// assemble the froxel PRIMITIVES (FroxelGrid + FroxelIntegrator) into a usable
// volume PASS. The primitives shipped in #409; what was missing is the code that
//   (1) POPULATES each froxel with scattering + extinction from a fog medium
//       (base density × 3D-noise heterogeneity, single directional in-scatter with
//       a Henyey-Greenstein phase — the same model as ShadingPipeline's per-surface
//       march, so a froxel scene reads like the existing fog), then
//   (2) INTEGRATES every froxel column front-to-back (FroxelIntegrator), then
//   (3) COMPOSITES the integrated volume over a beauty buffer by per-pixel depth.
//
// This is the froxel superpower: populate/integrate ONCE, then every pixel is a
// cheap depth-indexed read instead of a per-pixel ray march — and the volume is a
// stable 3D LUT the Scene Engine can temporally reproject (a later, additive step).
//
// Pure + deterministic (no RNG, FbmCloud3D is a hash-noise) → identical live and
// under --batch, and a twin for a future GPU froxel compute pass. The world
// mapping here is a simple axis-aligned slab (depth along the froxel Z, X/Y over a
// symmetric extent); wiring the live camera frustum + replacing the relief
// background march is the remaining step tracked on the slice issue.

using System;

namespace FracturingFog.Rendering.Lighting;

/// <summary>One light for the froxel in-scatter (roadmap S6 multi-light, #408).
/// Directional (Type 0) uses the constant <see cref="Lx"/>/<see cref="Ly"/>/<see
/// cref="Lz"/> "toward-light" direction with attenuation 1; Point (1) / Spot (2)
/// resolve direction + inverse-square (× cone) falloff per froxel via
/// <see cref="LightSampler"/>. Intensity 0 = off (no contribution).</summary>
public readonly struct FroxelLight
{
    public int Type { get; init; }
    public uint Color { get; init; }
    public double Intensity { get; init; }
    /// <summary>Unit direction toward the light (directional aim / spot cone axis).</summary>
    public double Lx { get; init; } public double Ly { get; init; } public double Lz { get; init; }
    /// <summary>World position (Point / Spot).</summary>
    public double PosX { get; init; } public double PosY { get; init; } public double PosZ { get; init; }
    /// <summary>Range window (0 = pure inverse-square) + spot cone cosines.</summary>
    public double Range { get; init; } public double InnerCos { get; init; } public double OuterCos { get; init; }
}

/// <summary>Homogeneous-plus-noise fog medium sampled to populate a froxel volume
/// (roadmap S6). Mirrors the knobs of the per-surface volumetric march.</summary>
public readonly struct FroxelMedium
{
    /// <summary>Uniform density floor (before noise modulation).</summary>
    public double BaseDensity { get; init; }
    /// <summary>Extinction (absorption+out-scatter) coefficient per unit density.</summary>
    public double Extinction { get; init; }

    /// <summary>Light color 0xAARRGGBB (RGB used).</summary>
    public uint LightColor { get; init; }
    /// <summary>Light intensity scaling the in-scatter.</summary>
    public double LightIntensity { get; init; }
    /// <summary>Unit direction toward the light.</summary>
    public double Lx { get; init; } public double Ly { get; init; } public double Lz { get; init; }

    /// <summary>View direction (for the Henyey-Greenstein phase).</summary>
    public double ViewDx { get; init; } public double ViewDy { get; init; } public double ViewDz { get; init; }
    /// <summary>HG anisotropy g in (-1,1); 0 = isotropic (phase 1).</summary>
    public double Anisotropy { get; init; }

    /// <summary>Noise heterogeneity amount (0 = homogeneous).</summary>
    public double NoiseAmount { get; init; }
    /// <summary>Noise spatial frequency.</summary>
    public double NoiseScale { get; init; }
    /// <summary>FBM octaves (clamped 1..6; 0 → 3).</summary>
    public int NoiseOctaves { get; init; }

    /// <summary>Half-extent of the froxel slab in world X/Y (depth spans the grid's
    /// near..far). Only affects where the noise field is sampled.</summary>
    public double WorldExtent { get; init; }

    /// <summary>Multi-light in-scatter (roadmap S6, #408). When non-null and
    /// non-empty, EVERY light's contribution is summed per froxel (each with its own
    /// direction / colour / phase, and per-cell positional falloff for point/spot) —
    /// the same three-light model as the per-surface march (#388). When null, the
    /// single <see cref="LightColor"/>/<see cref="LightIntensity"/>/<see cref="Lx"/>
    /// key light above is used (byte-identical to the pre-multi-light populate).</summary>
    public FroxelLight[]? Lights { get; init; }
}

/// <summary>A populated, column-integrated froxel volume + a depth-composite over a
/// beauty buffer (roadmap S6). Build once per frame, then composite cheaply.</summary>
public sealed class FroxelVolumePass
{
    private readonly FroxelGrid _grid;
    private readonly int _nx, _ny, _nz;
    // Integrated per-froxel accumulated in-scatter + transmittance, laid out
    // column-major: index = (cy*_nx + cx)*_nz + z, so a column is contiguous.
    private readonly double[] _inR, _inG, _inB, _trans;
    private bool _populated;

    public FroxelVolumePass(FroxelGrid grid)
    {
        _grid = grid ?? throw new ArgumentNullException(nameof(grid));
        _nx = grid.DimX; _ny = grid.DimY; _nz = grid.DimZ;
        int cells = _nx * _ny * _nz;
        _inR = new double[cells]; _inG = new double[cells];
        _inB = new double[cells]; _trans = new double[cells];
    }

    public FroxelGrid Grid => _grid;

    /// <summary>Populate + integrate the volume from a fog medium. Every froxel gets
    /// noise-modulated density → extinction + single-light in-scatter (HG phase);
    /// each column is then integrated front-to-back via <see cref="FroxelIntegrator"/>.</summary>
    public void Populate(in FroxelMedium m)
    {
        // Resolve the light list once. Null → a single directional key light from the
        // legacy scalar fields (byte-identical to the pre-multi-light populate).
        FroxelLight[] lights = (m.Lights != null && m.Lights.Length > 0)
            ? m.Lights
            : new[]
            {
                new FroxelLight
                {
                    Type = 0, Color = m.LightColor, Intensity = m.LightIntensity,
                    Lx = m.Lx, Ly = m.Ly, Lz = m.Lz,
                }
            };
        int nl = lights.Length;
        // Precompute each light's colour in [0,1] once.
        var lr = new double[nl]; var lg = new double[nl]; var lb = new double[nl];
        for (int i = 0; i < nl; i++)
        {
            lr[i] = ((lights[i].Color >> 16) & 0xFF) / 255.0;
            lg[i] = ((lights[i].Color >> 8) & 0xFF) / 255.0;
            lb[i] = (lights[i].Color & 0xFF) / 255.0;
        }

        double extent = m.WorldExtent;
        double scale = m.NoiseScale;
        int oct = m.NoiseOctaves <= 0 ? 3 : m.NoiseOctaves;

        var sR = new double[_nz]; var sG = new double[_nz]; var sB = new double[_nz];
        var ext = new double[_nz]; var th = new double[_nz];
        var oR = new double[_nz]; var oG = new double[_nz]; var oB = new double[_nz]; var oT = new double[_nz];

        for (int cy = 0; cy < _ny; cy++)
        {
            double wy = ((cy + 0.5) / _ny * 2.0 - 1.0) * extent;
            for (int cx = 0; cx < _nx; cx++)
            {
                double wx = ((cx + 0.5) / _nx * 2.0 - 1.0) * extent;
                for (int z = 0; z < _nz; z++)
                {
                    double wz = 0.5 * (_grid.SliceDepth(z) + _grid.SliceDepth(z + 1));
                    double noiseMul = 1.0;
                    if (m.NoiseAmount > 0.0)
                    {
                        double n = ShadingPipeline.FbmCloud3D(wx * scale, wy * scale, wz * scale, oct);
                        noiseMul = Math.Max(0.0, 1.0 + m.NoiseAmount * (2.0 * n - 1.0));
                    }
                    double density = m.BaseDensity * noiseMul;
                    ext[z] = m.Extinction * density;
                    th[z] = _grid.SliceThickness(z);

                    // #388-style multi-light in-scatter: sum every light, each with its
                    // own direction (per-cell for point/spot), attenuation and HG phase.
                    double accR = 0, accG = 0, accB = 0;
                    for (int i = 0; i < nl; i++)
                    {
                        var L = lights[i];
                        if (L.Intensity <= 0.0) continue;
                        double dx = L.Lx, dy = L.Ly, dz = L.Lz, atten = 1.0;
                        if (L.Type != 0)
                        {
                            var s = LightSampler.Sample(
                                (LightType)L.Type, L.Lx, L.Ly, L.Lz, L.PosX, L.PosY, L.PosZ,
                                L.Range, L.InnerCos, L.OuterCos, wx, wy, wz);
                            dx = s.lx; dy = s.ly; dz = s.lz; atten = s.atten;
                        }
                        double phase = HgPhase(m.Anisotropy, m.ViewDx * dx + m.ViewDy * dy + m.ViewDz * dz);
                        double sc = density * L.Intensity * atten * phase;
                        accR += sc * lr[i]; accG += sc * lg[i]; accB += sc * lb[i];
                    }
                    sR[z] = accR; sG[z] = accG; sB[z] = accB;
                }

                FroxelIntegrator.IntegrateColumn(sR, sG, sB, ext, th, _nz, oR, oG, oB, oT);
                int baseIdx = (cy * _nx + cx) * _nz;
                for (int z = 0; z < _nz; z++)
                {
                    _inR[baseIdx + z] = oR[z]; _inG[baseIdx + z] = oG[z];
                    _inB[baseIdx + z] = oB[z]; _trans[baseIdx + z] = oT[z];
                }
            }
        }
        _populated = true;
    }

    /// <summary>Sample the integrated column at (<paramref name="cx"/>,
    /// <paramref name="cy"/>) at continuous slice <paramref name="slice"/> ∈
    /// [0, DimZ-1], linearly interpolating in-scatter + transmittance.</summary>
    public (double inR, double inG, double inB, double trans) SampleColumn(int cx, int cy, double slice)
    {
        cx = cx < 0 ? 0 : (cx >= _nx ? _nx - 1 : cx);
        cy = cy < 0 ? 0 : (cy >= _ny ? _ny - 1 : cy);
        int baseIdx = (cy * _nx + cx) * _nz;
        if (slice <= 0.0) return (0, 0, 0, 1.0);
        if (slice >= _nz - 1)
        {
            int last = baseIdx + _nz - 1;
            return (_inR[last], _inG[last], _inB[last], _trans[last]);
        }
        int i0 = (int)slice;
        double f = slice - i0, omf = 1.0 - f;
        int a = baseIdx + i0, b = a + 1;
        return (_inR[a] * omf + _inR[b] * f,
                _inG[a] * omf + _inG[b] * f,
                _inB[a] * omf + _inB[b] * f,
                _trans[a] * omf + _trans[b] * f);
    }

    /// <summary>Composite the volume over a beauty buffer: each pixel's color is
    /// attenuated by the transmittance in front of its depth and the accumulated
    /// in-scatter added on top. <paramref name="depth01"/> is per-pixel normalized
    /// depth 0 (near) .. 1 (far). Returns a new buffer; alpha is preserved.</summary>
    public uint[] Composite(uint[] beauty, float[] depth01, int w, int h)
    {
        if (!_populated) throw new InvalidOperationException("FroxelVolumePass: Populate() must run before Composite().");
        if (beauty == null) throw new ArgumentNullException(nameof(beauty));
        if (depth01 == null) throw new ArgumentNullException(nameof(depth01));
        long n = (long)w * h;
        if (beauty.Length < n || depth01.Length < n)
            throw new ArgumentException("Froxel composite: buffer smaller than width*height.");

        var outBuf = new uint[beauty.Length];
        for (int y = 0; y < h; y++)
        {
            int cy = (int)((y + 0.5) / h * _ny);
            for (int x = 0; x < w; x++)
            {
                int idx = y * w + x;
                uint p = beauty[idx];
                int cx = (int)((x + 0.5) / w * _nx);
                double d = depth01[idx];
                if (d < 0) d = 0; else if (d > 1) d = 1;
                double slice = d * (_nz - 1);
                var (ir, ig, ib, tr) = SampleColumn(cx, cy, slice);

                double r = ((p >> 16) & 0xFF) * tr + ir * 255.0;
                double g = ((p >> 8) & 0xFF) * tr + ig * 255.0;
                double b = (p & 0xFF) * tr + ib * 255.0;
                uint R = (uint)(r < 0 ? 0 : (r > 255 ? 255 : r));
                uint G = (uint)(g < 0 ? 0 : (g > 255 ? 255 : g));
                uint B = (uint)(b < 0 ? 0 : (b > 255 ? 255 : b));
                outBuf[idx] = (p & 0xFF000000u) | (R << 16) | (G << 8) | B;
            }
        }
        return outBuf;
    }

    /// <summary>Composite the volume over a beauty buffer by per-pixel WORLD depth
    /// (ray distance from the camera), the value the relief render produces. Unlike
    /// <see cref="Composite"/> (which takes a slice-linear 0..1), this routes each
    /// depth through the grid's exponential <see cref="FroxelGrid.DepthToSlice"/> so
    /// the near-dense froxel distribution lands correctly. Sub-near → no fog; beyond
    /// far (e.g. a sky-miss sentinel) → the full integrated column. Returns a new
    /// buffer; alpha preserved (roadmap S6, #408).</summary>
    public uint[] CompositeWorldDepth(uint[] beauty, float[] worldDepth, int w, int h)
    {
        if (!_populated) throw new InvalidOperationException("FroxelVolumePass: Populate() must run before CompositeWorldDepth().");
        if (beauty == null) throw new ArgumentNullException(nameof(beauty));
        if (worldDepth == null) throw new ArgumentNullException(nameof(worldDepth));
        long n = (long)w * h;
        if (beauty.Length < n || worldDepth.Length < n)
            throw new ArgumentException("Froxel composite: buffer smaller than width*height.");

        double maxSlice = _nz - 1;
        var outBuf = new uint[beauty.Length];
        for (int y = 0; y < h; y++)
        {
            int cy = (int)((y + 0.5) / h * _ny);
            for (int x = 0; x < w; x++)
            {
                int idx = y * w + x;
                uint p = beauty[idx];
                int cx = (int)((x + 0.5) / w * _nx);
                // Exponential depth → continuous slice, clamped to the last integrated
                // slice (DepthToSlice returns [0, DimZ]; SampleColumn wants [0, nz-1]).
                double slice = _grid.DepthToSlice(worldDepth[idx]);
                if (slice > maxSlice) slice = maxSlice;
                var (ir, ig, ib, tr) = SampleColumn(cx, cy, slice);

                double r = ((p >> 16) & 0xFF) * tr + ir * 255.0;
                double g = ((p >> 8) & 0xFF) * tr + ig * 255.0;
                double b = (p & 0xFF) * tr + ib * 255.0;
                uint R = (uint)(r < 0 ? 0 : (r > 255 ? 255 : r));
                uint G = (uint)(g < 0 ? 0 : (g > 255 ? 255 : g));
                uint B = (uint)(b < 0 ? 0 : (b > 255 ? 255 : b));
                outBuf[idx] = (p & 0xFF000000u) | (R << 16) | (G << 8) | B;
            }
        }
        return outBuf;
    }

    // Normalized Henyey-Greenstein phase (g=0 → 1), matching the per-surface march.
    private static double HgPhase(double g, double cosT)
    {
        if (g == 0.0) return 1.0;
        g = g < -0.99 ? -0.99 : (g > 0.99 ? 0.99 : g);
        double denom = 1.0 + g * g - 2.0 * g * cosT;
        return (1.0 - g * g) / (denom * Math.Sqrt(denom));
    }
}
