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
// Precision (#1237): the trace is PERTURBED. Each bit rebases a reference
// point to the current ray point in octuple-double and computes its orbit
// Z_n there (exact OD); the substeps then solve for a double offset Δ with
// ε_{n+1} = 2Z_n·ε_n + ε_n² + Δ. Δ is one bit's movement, so double keeps
// full relative precision at any depth (to OD's ~1e-120); a plain double trace
// lost the ray after ~16–20 bits of a periodic angle (c itself has only 1e-16).

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
    /// walks one more bit (doubling) in <c>sharpness</c> substeps, perturbed
    /// about an OD reference rebased to the ray point every bit (file header).</summary>
    public sealed class Tracer
    {
        private readonly ExternalAngle _angle;
        private readonly int _sharpness;
        private DeepComplex _ref;
        private Complex _delta;
        private Complex[] _z = Array.Empty<Complex>();

        /// <summary>Bits walked so far.</summary>
        public int Bits { get; private set; }

        /// <summary>Current point on the ray, full precision.</summary>
        public DeepComplex Point => _ref.Translate(_delta.Real, _delta.Imaginary);

        /// <summary>Current point on the ray (double).</summary>
        public Complex C { get { var p = Point; return new Complex(p.Re.X0, p.Im.X0); } }

        public Tracer(ExternalAngle angle, int sharpness = DefaultSharpness)
        {
            _angle = angle ?? throw new ArgumentNullException(nameof(angle));
            if (sharpness < 1) throw new ArgumentOutOfRangeException(nameof(sharpness));
            _sharpness = sharpness;
            var c0 = Complex.FromPolarCoordinates(StartRadius, 2 * SMath.PI * angle.DoubledValue(0));
            _ref = new DeepComplex(c0.Real, c0.Imaginary);
        }

        /// <summary>Walk one bit. False when a Newton substep fails;
        /// <see cref="Point"/> is unchanged.</summary>
        public bool Advance()
        {
            int k = Bits;
            // Rebase the reference to the current point and take its exact
            // orbit Z_0..Z_{k+1} (stored as doubles; the ε recursion only needs
            // them to relative precision).
            var keepRef = _ref; var keepDelta = _delta;
            _ref = Point;
            _delta = Complex.Zero;
            _z = ReferenceOrbit(_ref, k + 1, _z);

            double phase = 2 * SMath.PI * _angle.DoubledValue(k);
            for (int j = 0; j < _sharpness; j++)
            {
                double r = SMath.Pow(StartRadius, SMath.Pow(0.5, (j + 0.5) / _sharpness));
                if (!SolveDepth(_z, ref _delta, k, Complex.FromPolarCoordinates(r, phase)))
                {
                    (_ref, _delta) = (keepRef, keepDelta);
                    return false;
                }
            }
            Bits = k + 1;
            return true;
        }
    }

    /// <summary>Z_0..Z_n of c = <paramref name="c"/> (Z_0 = 0), computed exactly
    /// in OD and stored as doubles.</summary>
    internal static Complex[] ReferenceOrbit(DeepComplex c, int n, Complex[] buffer)
    {
        var z = buffer.Length >= n + 1 ? buffer : new Complex[SMath.Max(n + 1, 2 * buffer.Length)];
        OD x = new(0), y = new(0);
        z[0] = Complex.Zero;
        for (int i = 1; i <= n; i++)
        {
            (x, y) = OdExact.SquareAdd(x, y, c.Re, c.Im);
            z[i] = new Complex(x.X0, y.X0);
        }
        return z;
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

    // Newton on the offset Δ (c = reference + Δ) for f^{k+1}_c(0) = target,
    // perturbed about the reference orbit z (z[n] = Z_n).
    internal static bool SolveDepth(Complex[] z, ref Complex delta, int k, Complex target)
    {
        double last = double.PositiveInfinity;
        for (int i = 0; i < NewtonSteps; i++)
        {
            Complex e = Complex.Zero, dc = Complex.Zero;
            for (int n = 0; n <= k; n++)
            {
                Complex zn = z[n];
                dc = 2.0 * (zn + e) * dc + Complex.One;
                e = 2.0 * zn * e + e * e + delta;
            }
            Complex step = (z[k + 1] + e - target) / dc;
            if (!double.IsFinite(step.Real) || !double.IsFinite(step.Imaginary)) return false;
            delta -= step;
            double m = step.Magnitude;
            if (m <= 1e-14 * delta.Magnitude) return true;
            // Noise floor: tiny and no longer shrinking.
            if (m <= 1e-9 * delta.Magnitude && m >= last) return true;
            last = m;
        }
        return last <= 1e-8 * delta.Magnitude;
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
        var history = new List<DeepComplex> { t.Point };
        bool precisionStop = false;
        // A periodic ray creeps into its parabolic root; walk it this far past
        // one period (more only costs time) before the landing Newton.
        int periodicBits = SMath.Min(maxBits, SMath.Max(PeriodicMinBits, PeriodicBitsPerPeriod * p));
        while (t.Bits < (angle.IsPeriodic ? periodicBits : maxBits))
        {
            ct.ThrowIfCancellationRequested();
            if (!t.Advance()) { precisionStop = true; break; }
            history.Add(t.Point);
            // Early stop only for preperiodic rays (geometric approach): stop
            // once a period moves the ray by a tiny fraction of its first full
            // cycle past the preperiod — converged relative to its own scale.
            if (!angle.IsPeriodic && t.Bits >= l + 2 * p && (t.Bits - l) % p == 0)
            {
                double move = Dist(history[^1], history[^(p + 1)]);
                double first = Dist(history[l + p], history[l]);
                if (move <= PreperiodicStop * first) break;
            }
        }
        if (t.Bits < l + p)
            return Fail(angle, default, 0, "The ray could not be traced that far.");

        var seed = t.Point;
        int back = SMath.Min(p, history.Count - 1);
        double scale = SMath.Max(Dist(history[^1], history[^(back + 1)]), 1e-110 * SMath.Max(1.0, Mag(seed)));
        string limit = precisionStop ? " (the trace stopped early)" : "";

        if (angle.IsPeriodic)
        {
            // Landing at a root: Newton for the period-p nucleus from there.
            // The ray creeps into a parabolic root slowly, so the trace scale
            // says nothing about the component's size; let Newton range out
            // (growing reach) and accept a nucleus only when the root it was
            // reached from lies on ITS component — within RootReach atom sizes,
            // which a neighbouring period-p component's nucleus never is.
            double cap = 2.0 * SMath.Max(1.0, Mag(seed));
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
                        double context = Dist(history[SMath.Min(l + p + 1, history.Count - 1)], m.Point);
                        return new RayLanding(RayLandingStatus.Misiurewicz, angle, seed, scale, default, m,
                                              ContextRadius: SMath.Max(context, 1e3 * scale));
                    }
                    if (m.Status is not (MisiurewiczStatus.LowerPreperiod or MisiurewiczStatus.LowerPeriod)) break;
                    (k, qq, at) = (m.Preperiod, m.Period, m.Point);
                }
            }
        return Fail(angle, seed, scale, $"No Misiurewicz point M({l + 1},{p}) found near the ray's landing point{limit}.");
    }

    /// <summary>Bits a periodic ray is walked: at least this many…</summary>
    public const int PeriodicMinBits = 64;
    /// <summary>…and this many per period.</summary>
    public const int PeriodicBitsPerPeriod = 10;
    /// <summary>A preperiodic trace stops once a period moves the ray by this
    /// fraction of its first cycle past the preperiod.</summary>
    public const double PreperiodicStop = 1e-8;

    public static double Dist(in DeepComplex a, in DeepComplex b)
    {
        double dx = OdExact.Sub(a.Re, b.Re).X0, dy = OdExact.Sub(a.Im, b.Im).X0;
        return SMath.Sqrt(dx * dx + dy * dy);
    }

    private static double Mag(in DeepComplex a) => SMath.Sqrt(a.Re.X0 * a.Re.X0 + a.Im.X0 * a.Im.X0);

    private static RayLanding Fail(ExternalAngle a, DeepComplex end, double scale, string why)
        => new(RayLandingStatus.Failed, a, end, scale, default, default, why);
}
