// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Explore/ExternalRay.cs
//
// Interesting-location finder S4 (#1188, epic #1184). Design:
// Docs/Technical/Interesting-Location-Finder-DesignPlan.md §3.5.
//
// Parameter-ray tracing inward. Outside M, Φ(c) (the Böttcher map) satisfies
// f^{k+1}_c(0) ≈ Φ(c)^{2^k}, so the point of the ray R_θ at "depth" k has
// f^{k+1}_c(0) = r·e^{2πi·2^kθ} for a radius r. The trace walks k = 0, 1, 2…,
// and within each k takes S substeps of r from R^{0.5^{(j+½)/S}} (j = 0…S−1),
// each solved by a few Newton steps on c from the previous point:
//     c ← c − (f^{k+1}(0) − target) / (d f^{k+1}(0)/dc).
// After the last substep of k, the angle is doubled (the next bit).
//
// Landing: a periodic θ (period p) lands on the ROOT of a period-p hyperbolic
// component; the trace stops near it and S2's Newton (NucleusFinder.Find)
// takes the seed to that component's nucleus, verified in OD. A preperiodic θ
// (preperiod l, period p) lands on the Misiurewicz point M(l+1, p′) with p′ | p;
// S3's MisiurewiczFinder.Refine polishes and verifies it in OD. So the double
// trace only proposes; acceptance is the same verified OD Newton as S2/S3.
//
// Limit: the trace runs in double, so it resolves landing points down to about
// 1e-12 of scale. Longer angles whose landing structure is finer report
// "too deep" (OD / perturbed tracing is a follow-up).

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

using FracturingFog.FFMath;

using SMath = System.Math;

namespace FracturingFog.Abstractions.Explore;

/// <summary>Outcome of <see cref="ExternalRay.Land"/>.</summary>
public enum RayLandingStatus
{
    /// <summary>Periodic angle: landed on a component; <see cref="RayLanding.Nucleus"/> is its verified nucleus.</summary>
    Component,
    /// <summary>Preperiodic angle: landed on a verified Misiurewicz point.</summary>
    Misiurewicz,
    /// <summary>The trace or the landing Newton failed (or would need more than double precision).</summary>
    Failed,
}

/// <summary>Result of landing a ray. <see cref="Nucleus"/> is set for
/// Component; <see cref="Misiurewicz"/> for Misiurewicz. <see cref="RayEnd"/>
/// is where the double trace stopped; <see cref="Scale"/> is the trace's last
/// per-period step. <see cref="ContextRadius"/> (Misiurewicz only) is how far
/// the ray still was from the point one full cycle past its preperiod — a
/// natural view radius that shows the hub with its first ring of structure.</summary>
public readonly record struct RayLanding(
    RayLandingStatus Status, ExternalAngle Angle, DeepComplex RayEnd, double Scale,
    NucleusResult Nucleus, MisiurewiczResult Misiurewicz, string? Detail = null, double ContextRadius = 0);

/// <summary>Parameter-ray tracing for f_c(z) = z² + c. UI-free.</summary>
public static class ExternalRay
{
    /// <summary>Escape radius the trace starts from.</summary>
    public const double StartRadius = 65536.0;

    /// <summary>Substeps per bit (sharpness).</summary>
    public const int DefaultSharpness = 8;

    /// <summary>Most bits (doublings) the trace walks.</summary>
    public const int DefaultMaxBits = 512;

    private const int NewtonSteps = 64;

    /// <summary>A component's root lies within this many atom sizes of its
    /// nucleus (main cardioid 0.25, period-2 bulb 0.5); used to tie a landing
    /// root to the nucleus Newton found from it.</summary>
    private const double RootReach = 2.0;

    /// <summary>Incremental inward trace of R_θ: each <see cref="Advance"/>
    /// walks one more bit (doubling) in <c>sharpness</c> substeps.</summary>
    public sealed class Tracer
    {
        private readonly ExternalAngle _angle;
        private readonly int _sharpness;

        /// <summary>Bits walked so far.</summary>
        public int Bits { get; private set; }

        /// <summary>Current point on the ray.</summary>
        public Complex C { get; private set; }

        public Tracer(ExternalAngle angle, int sharpness = DefaultSharpness)
        {
            _angle = angle ?? throw new ArgumentNullException(nameof(angle));
            if (sharpness < 1) throw new ArgumentOutOfRangeException(nameof(sharpness));
            _sharpness = sharpness;
            C = Complex.FromPolarCoordinates(StartRadius, 2 * SMath.PI * angle.DoubledValue(0));
        }

        /// <summary>Walk one bit. False when a Newton substep fails — the ray
        /// is finer here than double resolves; <see cref="C"/> is unchanged.</summary>
        public bool Advance()
        {
            int k = Bits;
            double phase = 2 * SMath.PI * _angle.DoubledValue(k);
            Complex c = C;
            for (int j = 0; j < _sharpness; j++)
            {
                double r = SMath.Pow(StartRadius, SMath.Pow(0.5, (j + 0.5) / _sharpness));
                if (!SolveDepth(ref c, k, Complex.FromPolarCoordinates(r, phase))) return false;
            }
            C = c;
            Bits = k + 1;
            return true;
        }
    }

    /// <summary>Points of R_θ after each of the first <paramref name="bits"/>
    /// bits (index 0 = start), or null if the trace fails before that.</summary>
    public static List<Complex>? TraceIn(
        ExternalAngle angle, int bits, int sharpness = DefaultSharpness, CancellationToken ct = default)
    {
        if (bits < 0) throw new ArgumentOutOfRangeException(nameof(bits));
        var t = new Tracer(angle, sharpness);
        var points = new List<Complex>(bits + 1) { t.C };
        for (int k = 0; k < bits; k++)
        {
            ct.ThrowIfCancellationRequested();
            if (!t.Advance()) return null;
            points.Add(t.C);
        }
        return points;
    }

    // Newton on c for f^{k+1}_c(0) = target, from the current c.
    private static bool SolveDepth(ref Complex c, int k, Complex target)
    {
        for (int i = 0; i < NewtonSteps; i++)
        {
            Complex z = Complex.Zero, dc = Complex.Zero;
            for (int n = 0; n <= k; n++) { dc = 2.0 * z * dc + Complex.One; z = z * z + c; }
            Complex step = (z - target) / dc;
            if (!double.IsFinite(step.Real) || !double.IsFinite(step.Imaginary)) return false;
            c -= step;
            if (step.Magnitude <= 1e-15 * SMath.Max(1.0, c.Magnitude)) return true;
        }
        return false;
    }

    /// <summary>Trace R_θ to where it lands and verify the landing point in OD
    /// (see the file header).</summary>
    public static RayLanding Land(ExternalAngle angle, int maxBits = DefaultMaxBits, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(angle);
        if (angle.Pre.Length == 0 && angle.Per == "0")
            return Fail(angle, default, 0, "Angle 0 lands on the cusp c = 1/4 (the main cardioid's root); go there with Reset.");

        int p = angle.Period, l = angle.Preperiod;
        // Walk the ray. Misiurewicz landings converge geometrically, so stop
        // once a period barely moves the ray; component roots converge slowly
        // (parabolic), so the bit budget or the double limit ends those and the
        // landing Newton takes over from there.
        var t = new Tracer(angle);
        var history = new List<Complex> { t.C };
        bool precisionStop = false;
        while (t.Bits < maxBits)
        {
            ct.ThrowIfCancellationRequested();
            if (!t.Advance()) { precisionStop = true; break; }
            history.Add(t.C);
            // Early stop only for preperiodic rays (geometric approach): a
            // periodic ray creeps into its parabolic root, so "barely moving"
            // there can still be several component sizes away.
            if (!angle.IsPeriodic && t.Bits >= l + 2 * p && (t.Bits - l) % p == 0)
            {
                double move = (history[^1] - history[^(p + 1)]).Magnitude;
                if (move <= 1e-10 * SMath.Max(1.0, t.C.Magnitude)) break;
            }
        }
        if (t.Bits < l + p)
            return Fail(angle, default, 0, "The ray could not be traced that far (angle too long for double precision).");

        Complex end = t.C;
        int back = SMath.Min(p, history.Count - 1);
        double scale = SMath.Max((history[^1] - history[^(back + 1)]).Magnitude, 1e-14 * SMath.Max(1.0, end.Magnitude));
        var seed = new DeepComplex(end.Real, end.Imaginary);
        string limit = precisionStop ? " (the trace hit double precision first)" : "";

        if (angle.IsPeriodic)
        {
            // Landing at a root: Newton for the period-p nucleus from there.
            // The ray creeps into a parabolic root slowly, so the trace scale
            // says nothing about the component's size; let Newton range out
            // (growing reach) and accept a nucleus only when the root it was
            // reached from lies on ITS component — within RootReach atom sizes,
            // which a neighbouring period-p component's nucleus never is.
            double cap = 2.0 * SMath.Max(1.0, end.Magnitude);
            for (double reach = 4 * scale; ; reach = SMath.Min(cap, reach * 8))
            {
                var n = NucleusFinder.Find(seed, p, reach, ct: ct);
                if (n.Found)
                {
                    double off = SMath.Sqrt(SMath.Pow(OdExact.Sub(n.Nucleus.Re, seed.Re).X0, 2)
                                          + SMath.Pow(OdExact.Sub(n.Nucleus.Im, seed.Im).X0, 2));
                    if (off <= RootReach * n.Size.Magnitude)
                        return new RayLanding(RayLandingStatus.Component, angle, seed, scale, n, default);
                }
                if (reach >= cap) break;
            }
            return Fail(angle, seed, scale, $"No period-{p} nucleus found near the ray's landing point{limit}.");
        }

        // Preperiodic: M(l+1, p′) where the point's period p′ divides the
        // angle's period p (e.g. .010(001) lands on M(4,1)). Refine deflates
        // out divisor periods, so a too-long period can never reach the point:
        // try each divisor p′ of p, smallest first. A smaller true preperiod is
        // reported by Refine and followed.
        for (double reach = 4 * scale; reach <= 4096 * scale; reach *= 8)
            for (int q = 1; q <= p; q++)
            {
                if (p % q != 0) continue;
                int k = l + 1, qq = q;
                var at = seed;
                for (int fix = 0; fix < 4; fix++)
                {
                    var m = MisiurewiczFinder.Refine(at, k, qq, seed, reach, ct);
                    if (m.Found)
                    {
                        Complex pt = new(m.Point.Re.X0, m.Point.Im.X0);
                        double context = (history[SMath.Min(l + p + 1, history.Count - 1)] - pt).Magnitude;
                        return new RayLanding(RayLandingStatus.Misiurewicz, angle, seed, scale, default, m,
                                              ContextRadius: SMath.Max(context, 1e3 * scale));
                    }
                    if (m.Status is not (MisiurewiczStatus.LowerPreperiod or MisiurewiczStatus.LowerPeriod)) break;
                    (k, qq, at) = (m.Preperiod, m.Period, m.Point);
                }
            }
        return Fail(angle, seed, scale, $"No Misiurewicz point M({l + 1},{p}) found near the ray's landing point{limit}.");
    }

    private static RayLanding Fail(ExternalAngle a, DeepComplex end, double scale, string why)
        => new(RayLandingStatus.Failed, a, end, scale, default, default, why);
}
