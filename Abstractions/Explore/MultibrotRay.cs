// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Explore/MultibrotRay.cs
//
// Finder S7 follow-up (#1242, epic #1184): parameter rays of z^d + c.
// Outside the Multibrot set M_d the Böttcher map satisfies
// f^{k+1}_c(0) ≈ Φ(c)^{d^k}, so the point of the ray R_θ at depth k has
// f^{k+1}_c(0) = r·e^{2πi·d^kθ}. As in ExternalRay (z² + c), the trace walks
// k = 0, 1, 2… and within each digit takes S substeps of r from
// R^{(1/d)^{(j+½)/S}}, each solved by Newton on c from the previous point.
//
// Landing (Douady–Hubbard, unicritical case):
//   • periodic θ of period p → a parabolic point of a period-p component (its
//     root or, for d > 2, a co-root; every component has d − 1 of them). The
//     general nucleus Newton (GeneralFinder.FindNucleus, which checks minimal
//     period and an attracting cycle) takes the trace end to that component's
//     nucleus, accepted only when the trace end lies on it (within RootReach
//     body radii);
//   • preperiodic θ (preperiod l, period p) → the Misiurewicz point
//     M(l + 1, p′) with p′ | p, found by GeneralFinder.FindMisiurewicz (minimal
//     (k, p), repelling cycle).
//
// Double precision: Multibrot renders in double (finders stop at zoom 1e13),
// so the trace is plain double and stops once the ray stalls at that limit;
// the landing Newton takes over from there.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

using SMath = System.Math;

namespace FracturingFog.Abstractions.Explore;

/// <summary>Result of <see cref="MultibrotRay.Land"/>; <see cref="Nucleus"/> is
/// set for Component, <see cref="Misiurewicz"/> for Misiurewicz.</summary>
public readonly record struct GeneralRayLanding(
    RayLandingStatus Status, BaseAngle Angle, V2 RayEnd, double Scale,
    GeneralNucleus Nucleus, GeneralMisiurewicz Misiurewicz, string? Detail = null, double ContextRadius = 0);

/// <summary>Parameter-ray tracing for f_c(z) = z^d + c. UI-free.</summary>
public static class MultibrotRay
{
    /// <summary>Escape radius the trace starts from.</summary>
    public const double StartRadius = 65536.0;
    /// <summary>Substeps per digit (sharpness).</summary>
    public const int DefaultSharpness = 8;
    /// <summary>Most digits the trace walks.</summary>
    public const int DefaultMaxDigits = 400;
    /// <summary>A periodic ray is walked at least this many digits…</summary>
    public const int PeriodicMinDigits = 48;
    /// <summary>…and this many per period (it creeps into a parabolic point).</summary>
    public const int PeriodicDigitsPerPeriod = 10;
    /// <summary>A preperiodic trace stops once a period moves the ray by this
    /// fraction of its first cycle past the preperiod.</summary>
    public const double PreperiodicStop = 1e-8;
    /// <summary>The ray has stalled at double precision once a digit moves it
    /// less than this, relative to |c|.</summary>
    public const double StallFraction = 1e-13;
    /// <summary>A parabolic point lies within this many body radii of its
    /// component's nucleus (roots and co-roots alike).</summary>
    public const double RootReach = 2.5;

    private const int NewtonSteps = 64;

    /// <summary>Incremental inward trace of R_θ for degree d.</summary>
    public sealed class Tracer
    {
        private readonly BaseAngle _angle;
        private readonly int _degree;
        private readonly int _sharpness;

        public int Digits { get; private set; }
        public Complex C { get; private set; }

        public Tracer(int degree, BaseAngle angle, int sharpness = DefaultSharpness)
        {
            _angle = angle ?? throw new ArgumentNullException(nameof(angle));
            if (degree != angle.Base) throw new ArgumentException($"A degree-{degree} ray needs a base-{degree} angle.", nameof(angle));
            if (sharpness < 1) throw new ArgumentOutOfRangeException(nameof(sharpness));
            _degree = degree;
            _sharpness = sharpness;
            C = Complex.FromPolarCoordinates(StartRadius, 2 * SMath.PI * angle.ShiftedValue(0));
        }

        /// <summary>Walk one digit. False when a Newton substep fails;
        /// <see cref="C"/> is unchanged.</summary>
        public bool Advance()
        {
            int k = Digits;
            double phase = 2 * SMath.PI * _angle.ShiftedValue(k);
            var c = C;
            for (int j = 0; j < _sharpness; j++)
            {
                double r = SMath.Pow(StartRadius, SMath.Pow(1.0 / _degree, (j + 0.5) / _sharpness));
                if (!SolveDepth(_degree, ref c, k, Complex.FromPolarCoordinates(r, phase))) return false;
            }
            C = c;
            Digits = k + 1;
            return true;
        }
    }

    // Newton on c for f^{k+1}_c(0) = target.
    internal static bool SolveDepth(int d, ref Complex c, int k, Complex target)
    {
        double last = double.PositiveInfinity;
        for (int i = 0; i < NewtonSteps; i++)
        {
            Complex z = Complex.Zero, dc = Complex.Zero;
            for (int n = 0; n <= k; n++)
            {
                Complex w = IntPow(z, d - 1);
                dc = d * w * dc + Complex.One;
                z = z * w + c;
            }
            Complex step = (z - target) / dc;
            if (!double.IsFinite(step.Real) || !double.IsFinite(step.Imaginary)) return false;
            c -= step;
            double m = step.Magnitude, scale = SMath.Max(c.Magnitude, 1e-300);
            if (m <= 4e-16 * scale) return true;
            if (m <= 1e-10 * scale && m >= last) return true;   // noise floor
            last = m;
        }
        return last <= 1e-9 * SMath.Max(c.Magnitude, 1e-300);
    }

    private static Complex IntPow(Complex z, int e)
    {
        Complex r = Complex.One;
        for (int i = 0; i < e; i++) r *= z;
        return r;
    }

    /// <summary>Points of R_θ after each of the first <paramref name="digits"/>
    /// digits (index 0 = start), or null if the trace fails before that.</summary>
    public static List<Complex>? TraceIn(int degree, BaseAngle angle, int digits, int sharpness = DefaultSharpness, CancellationToken ct = default)
    {
        var t = new Tracer(degree, angle, sharpness);
        var points = new List<Complex>(digits + 1) { t.C };
        for (int k = 0; k < digits; k++)
        {
            ct.ThrowIfCancellationRequested();
            if (!t.Advance()) return null;
            points.Add(t.C);
        }
        return points;
    }

    /// <summary>Trace R_θ for z^d + c to where it lands and verify the landing
    /// point with the general finders (file header).</summary>
    public static GeneralRayLanding Land(MultibrotMap map, BaseAngle angle, int maxDigits = DefaultMaxDigits, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(angle);
        int d = map.Degree;
        if (angle.Base != d) return Fail(angle, default, 0, $"z^{d} + c needs a base-{d} angle.");

        int p = angle.Period, l = angle.Preperiod;
        var t = new Tracer(d, angle);
        var history = new List<Complex> { t.C };
        int periodicDigits = SMath.Min(maxDigits, SMath.Max(PeriodicMinDigits, PeriodicDigitsPerPeriod * p));
        string limit = "";
        while (t.Digits < (angle.IsPeriodic ? periodicDigits : maxDigits))
        {
            ct.ThrowIfCancellationRequested();
            if (!t.Advance()) { limit = " (the trace stopped early)"; break; }
            history.Add(t.C);
            if ((history[^1] - history[^2]).Magnitude <= StallFraction * SMath.Max(1, t.C.Magnitude)) break;
            if (!angle.IsPeriodic && t.Digits >= l + 2 * p && (t.Digits - l) % p == 0)
            {
                double move = (history[^1] - history[^(p + 1)]).Magnitude;
                double first = (history[l + p] - history[l]).Magnitude;
                if (move <= PreperiodicStop * first) break;
            }
        }
        if (t.Digits < l + p)
            return Fail(angle, default, 0, "The ray could not be traced that far.");

        var seed = ToV2(t.C);
        int back = SMath.Min(p, history.Count - 1);
        double scale = SMath.Max((history[^1] - history[^(back + 1)]).Magnitude, 1e-15 * SMath.Max(1.0, seed.Length));

        if (angle.IsPeriodic)
        {
            // The ray creeps into a parabolic point slowly, so let Newton range
            // out and accept the nucleus only when the trace end lies on its
            // component (a neighbour's nucleus is many body radii away).
            double cap = 2.0 * SMath.Max(1.0, seed.Length);
            for (double reach = 4 * scale; ; reach = SMath.Min(cap, reach * 8))
            {
                if (GeneralFinder.FindNucleus(map, seed, p, reach, ct) is GeneralNucleus n
                    && (n.Point - seed).Length <= RootReach * n.BodyRadius)
                    return new GeneralRayLanding(RayLandingStatus.Component, angle, seed, scale, n, default);
                if (reach >= cap) break;
            }
            return Fail(angle, seed, scale, $"No period-{p} component found at the ray's landing point{limit}.");
        }

        // Preperiodic: M(l+1, p′) with p′ | p, smallest p′ first.
        for (double reach = 4 * scale; reach <= 4096 * scale; reach *= 8)
            for (int q = 1; q <= p; q++)
            {
                if (p % q != 0) continue;
                var m = GeneralFinder.FindMisiurewicz(map, seed, l + 1, q, reach, ct);
                if (!m.Found) continue;
                var mc = new Complex(m.Point.X, m.Point.Y);
                double context = (history[SMath.Min(l + p + 1, history.Count - 1)] - mc).Magnitude;
                return new GeneralRayLanding(RayLandingStatus.Misiurewicz, angle, seed, scale, default, m,
                                             ContextRadius: SMath.Max(context, 1e3 * scale));
            }
        return Fail(angle, seed, scale, $"No Misiurewicz point M({l + 1},{p}) found at the ray's landing point{limit}.");
    }

    private static V2 ToV2(Complex c) => new(c.Real, c.Imaginary);

    private static GeneralRayLanding Fail(BaseAngle a, V2 end, double scale, string why)
        => new(RayLandingStatus.Failed, a, end, scale, default, default, why);
}
