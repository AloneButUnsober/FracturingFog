// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Rendering/Lighting/FroxelVolume.cs
//
// Roadmap slice S6 (3D-Rendering-Roadmap.md, parent #389): a froxel (frustum-
// voxel) unified volume march, à la Frostbite / Hillaire 2015 ("Physically Based
// and Unified Volumetric Rendering"). Today's volumetrics are a per-surface
// single-scatter ray march (see ShadingPipeline.VolumetricInScatterSegment /
// #388); a froxel volume replaces the per-pixel march with a camera-aligned 3D
// LUT: populate each froxel with scattering + extinction once, integrate it
// front-to-back into a per-froxel (accumulated in-scatter, transmittance), then
// composite by a single depth-indexed read. This unifies fog across every 3D
// type and — crucially — gives temporal stability when the Scene Engine animates
// fog (the history-reprojection layer is a later, additive step).
//
// This first slice is the two deterministic, twinnable primitives:
//   * FroxelGrid — the exponential depth-slice distribution (near-dense, like
//     Frostbite's `depth = near * (far/near)^(z/dim)`).
//   * FroxelIntegrator — the energy-conserving front-to-back scattering
//     integration of one froxel column (Hillaire's slice accumulation) + a
//     depth sampler for compositing.
//
// Pure math, no device state, no RNG → identical live and under --batch, and a
// twin for a future GPU froxel pass. Nothing here changes the existing
// volumetric path, so default renders are unaffected.

using System;

namespace FracturingFog.Rendering.Lighting;

/// <summary>Camera-frustum froxel grid: the exponential near→far depth-slice
/// distribution shared by the populate, integrate and composite passes
/// (roadmap S6).</summary>
public sealed class FroxelGrid
{
    public FroxelGrid(int dimX, int dimY, int dimZ, double near, double far)
    {
        if (dimX <= 0 || dimY <= 0 || dimZ <= 0) throw new ArgumentException("Froxel dims must be positive.");
        if (near <= 0 || far <= near) throw new ArgumentException("Require 0 < near < far.");
        DimX = dimX; DimY = dimY; DimZ = dimZ;
        Near = near; Far = far;
        _ratio = far / near;
        _invLogRatio = 1.0 / Math.Log(_ratio);
    }

    public int DimX { get; }
    public int DimY { get; }
    public int DimZ { get; }
    public double Near { get; }
    public double Far { get; }

    private readonly double _ratio;
    private readonly double _invLogRatio;

    /// <summary>World depth at slice boundary <paramref name="z"/> ∈ [0, DimZ].
    /// Exponential: 0 → Near, DimZ → Far, near-dense so foreground fog resolves.</summary>
    public double SliceDepth(int z)
    {
        double t = (double)z / DimZ;
        return Near * Math.Pow(_ratio, t);
    }

    /// <summary>Thickness of slice <paramref name="z"/> ∈ [0, DimZ-1].</summary>
    public double SliceThickness(int z) => SliceDepth(z + 1) - SliceDepth(z);

    /// <summary>Continuous slice coordinate for a world depth (inverse of
    /// <see cref="SliceDepth"/>); clamped to [0, DimZ]. Sub-Near → 0, beyond
    /// Far → DimZ.</summary>
    public double DepthToSlice(double depth)
    {
        if (depth <= Near) return 0.0;
        if (depth >= Far) return DimZ;
        return DimZ * Math.Log(depth / Near) * _invLogRatio;
    }
}

/// <summary>Front-to-back scattering integration of a froxel column + depth
/// sampling (roadmap S6). One column = <c>DimZ</c> slices along view Z.</summary>
public static class FroxelIntegrator
{
    /// <summary>Integrate a froxel column front-to-back (Hillaire's energy-
    /// conserving slice accumulation). Inputs are per-slice scattered radiance
    /// (<paramref name="scatterR"/>/G/B), extinction coefficient
    /// (<paramref name="extinction"/>) and slice thickness. Outputs the
    /// per-slice accumulated in-scatter and the remaining transmittance in front
    /// of that slice's far boundary. All arrays are length <c>n</c>.</summary>
    public static void IntegrateColumn(
        double[] scatterR, double[] scatterG, double[] scatterB, double[] extinction,
        double[] sliceThickness, int n,
        double[] outInR, double[] outInG, double[] outInB, double[] outTrans)
    {
        double trans = 1.0, accR = 0, accG = 0, accB = 0;
        for (int i = 0; i < n; i++)
        {
            double ext = extinction[i];
            double d = sliceThickness[i];
            double sliceT = Math.Exp(-ext * d);

            // Energy-conserving in-scatter over the slice: ∫ scatter·T dt. As
            // ext → 0 this limits to scatter·thickness (no divide-by-zero).
            double factor = ext > 1e-8 ? (1.0 - sliceT) / ext : d;
            accR += trans * scatterR[i] * factor;
            accG += trans * scatterG[i] * factor;
            accB += trans * scatterB[i] * factor;
            trans *= sliceT;

            outInR[i] = accR; outInG[i] = accG; outInB[i] = accB;
            outTrans[i] = trans;
        }
    }

    /// <summary>Sample an integrated column at continuous slice coordinate
    /// <paramref name="slice"/> (from <see cref="FroxelGrid.DepthToSlice"/>),
    /// linearly interpolating in-scatter + transmittance between adjacent slices.
    /// Below slice 0 → (0, transmittance 1); at/after the last slice → its stored
    /// value.</summary>
    public static (double inR, double inG, double inB, double trans) Sample(
        double[] inR, double[] inG, double[] inB, double[] trans, int n, double slice)
    {
        if (n <= 0) return (0, 0, 0, 1.0);
        if (slice <= 0.0) return (0, 0, 0, 1.0);
        if (slice >= n - 1) return (inR[n - 1], inG[n - 1], inB[n - 1], trans[n - 1]);

        int i0 = (int)slice;
        double f = slice - i0;
        double omf = 1.0 - f;
        return (
            inR[i0] * omf + inR[i0 + 1] * f,
            inG[i0] * omf + inG[i0 + 1] * f,
            inB[i0] * omf + inB[i0 + 1] * f,
            trans[i0] * omf + trans[i0 + 1] * f);
    }
}
