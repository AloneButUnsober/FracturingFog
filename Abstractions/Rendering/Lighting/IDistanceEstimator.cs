// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// IDistanceEstimator.cs
//
// P3 — struct-generic DE interface. Replaces the indirect-dispatch
// DistanceEstimator delegate where call sites are perf-critical (shading
// inner loops). Concrete DE structs implement Evaluate; ShadingPipeline.Shade
// is generic on TDe : struct, IDistanceEstimator so the JIT generates a
// devirtualized specialisation per concrete struct — direct call, inlinable.
//
// The legacy delegate path keeps working via an adapter struct in
// ShadingPipeline; calculators can migrate incrementally.

namespace FracturingFog.Rendering.Lighting;

/// <summary>
/// Distance estimator contract. <see cref="Evaluate"/> returns a lower
/// bound on the distance from (x, y, z) to the fractal surface. Used during
/// the primary raymarch, AO sampling, shadow / reflection walks, and
/// volumetric scattering.
///
/// Implement as a <c>readonly struct</c> with captured parameters as fields.
/// The struct is passed by <c>in</c> through generic shading helpers so the
/// JIT specialises each Shade&lt;TDe&gt; instantiation into a direct,
/// inlinable Evaluate call — same cost as a hand-inlined function, no
/// virtual dispatch.
/// </summary>
public interface IDistanceEstimator
{
    double Evaluate(double x, double y, double z);

    /// <summary>#1033 — true when the estimator supplies <see cref="ShadeProbe"/>
    /// for the shading marches (soft shadow, AO, reflections). The generic shading
    /// helpers branch on it at JIT time, so an estimator that leaves it false keeps
    /// its exact <see cref="Evaluate"/>-only behaviour at no cost.</summary>
    static virtual bool HasShadeProbe => false;

    /// <summary>#1033 — one shading-march sample at (x, y, z) heading along
    /// (rdx, rdy, rdz). Returns a distance ESTIMATE to the surface (the penumbra /
    /// AO term; need not be a lower bound), sets <paramref name="step"/> to a safe
    /// advance along the ray and <paramref name="hit"/> when the surface is within
    /// <paramref name="hitEps"/>. For an estimator whose <see cref="Evaluate"/> is a
    /// loose global bound (a height field's single Lipschitz factor), this keeps the
    /// marches correct: steps stay safe, while occlusion is judged on real
    /// distances. The default is <see cref="Evaluate"/> for all three.</summary>
    double ShadeProbe(double x, double y, double z, double rdx, double rdy, double rdz,
                      double hitEps, out double step, out bool hit)
    {
        double d = Evaluate(x, y, z);
        step = d; hit = d < hitEps;
        return d;
    }
}
