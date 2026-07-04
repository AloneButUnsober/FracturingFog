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
}
