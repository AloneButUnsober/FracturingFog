// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Explore/DeepExternalRay.cs
//
// #1870 (epic #1428, C7c) — ExternalRay past octuple-double: the same inward parameter-
// ray trace and landing (design: Docs/Technical/Interesting-Location-Finder-DesignPlan.md
// §3.5; external rays and the Böttcher map credited in Docs/Resources-Bibliography.md:
// Douady–Hubbard, Claude Heiland-Allen), carried in BigFloat and floatexp:
//
//   • Each bit rebases the reference to the current ray point as an exact centre
//     (BigFloat) and takes its orbit Z_0..Z_{k+1} there (stored as double: Z is O(R)).
//     The substeps solve for the offset Δ in CFx with ε_{n+1} = 2Z_n ε_n + ε_n² + Δ:
//     Δ is one bit's movement, which falls below double's range long before the ray
//     reaches a landing point past 1e308. The reference's precision follows the ray: a
//     bit that moved it by 2^-m is kept to m + 128 bits.
//   • #1872 — the substeps run in double while every value is a normal double, in CFx
//     past that. (The reference is still rebased every bit: an older ray point's orbit
//     escapes far faster — |Z| about squares per extra iteration — so perturbing a later
//     bit from it loses all precision; measured, it broke the n = 60 tuned landing.)
//   • Landing as ExternalRay.Land: a periodic angle's trace seeds Newton for the period-p
//     nucleus (DeepNucleusFinder.Find, growing reach), accepted when the root it came from
//     lies within RootReach atom sizes of it; a preperiodic one seeds the Misiurewicz
//     refinement (DeepMisiurewiczFinder.Refine) over the divisors of its period.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

using FracturingFog.FFMath;

using SMath = System.Math;

namespace FracturingFog.Abstractions.Explore;

/// <summary>#1870 — <see cref="RayLanding"/> past OD. <see cref="Nucleus"/> is set for
/// Component, <see cref="Misiurewicz"/> for Misiurewicz. <see cref="ScaleLog10"/> is log10
/// of the trace's last per-period step; <see cref="ContextRadiusLog10"/> (Misiurewicz
/// only) log10 of a view radius that shows the hub with its first ring of structure.
/// #1872: <see cref="Substeps"/> / <see cref="DoubleSubsteps"/>, the trace's Newton
/// substeps and how many of them ran in double.</summary>
public readonly record struct DeepRayLanding(
    RayLandingStatus Status, ExternalAngle Angle, ExactCentre RayEnd, double ScaleLog10,
    DeepNucleusResult Nucleus, DeepMisiurewiczResult Misiurewicz, string? Detail = null,
    double ContextRadiusLog10 = double.NaN, int Substeps = 0, int DoubleSubsteps = 0);

/// <summary>#1872 — the landing's speed-ups, each switchable so tests can check them
/// against the plain path: the trace's substeps in double where every value is a normal
/// double; skipping, in the wider reach windows, a divisor whose Newton ran out of steps.</summary>
public readonly record struct DeepRayTuning(bool DoubleSubsteps = true, bool SkipExhaustedWindows = true)
{
    public static DeepRayTuning Default => new(true, true);
    /// <summary>As before #1872 (the BigFloat rounding rewrite aside, which is exact).</summary>
    public static DeepRayTuning Plain => new(false, false);
}

/// <summary>#1870 — parameter-ray tracing for z² + c past octuple-double. See the file header.</summary>
public static class DeepExternalRay
{
    /// <summary>Most bits the deep trace walks (its cost grows with the square of the bits).</summary>
    public const int DefaultMaxBits = 4096;

    private const int NewtonSteps = 64;
    private const double RootReachLog2 = 1.0;   // RootReach = 2 atom sizes, as ExternalRay
    private const double Log2Of10 = 3.3219280948873622;
    private const int MinPrecision = 192;

    /// <summary>Incremental inward trace of R_θ (as <see cref="ExternalRay.Tracer"/>), with
    /// the reference an exact centre and the offset in CFx.</summary>
    public sealed class Tracer
    {
        private readonly ExternalAngle _angle;
        private readonly int _sharpness;
        private ExactCentre _ref;
        private CFx _delta = CFx.Zero;
        private int _prec = MinPrecision;
        private Complex[] _z = Array.Empty<Complex>();

        public int Bits { get; private set; }

        /// <summary>#1872 — Newton substeps so far, and those solved in double.</summary>
        public int Substeps { get; private set; }
        public int DoubleSubsteps { get; private set; }

        /// <summary>The current point on the ray, exact.</summary>
        public ExactCentre Point => Add(_ref, _delta, _prec);

        private readonly bool _inDouble = true;   // #1872

        internal Tracer(ExternalAngle angle, DeepRayTuning tuning, int sharpness = ExternalRay.DefaultSharpness)
            : this(angle, sharpness) => _inDouble = tuning.DoubleSubsteps;

        public Tracer(ExternalAngle angle, int sharpness = ExternalRay.DefaultSharpness)
        {
            _angle = angle ?? throw new ArgumentNullException(nameof(angle));
            if (sharpness < 1) throw new ArgumentOutOfRangeException(nameof(sharpness));
            _sharpness = sharpness;
            var c0 = Complex.FromPolarCoordinates(ExternalRay.StartRadius, 2 * SMath.PI * angle.DoubledValue(0));
            _ref = new ExactCentre(BigFloat.FromDouble(c0.Real, _prec), BigFloat.FromDouble(c0.Imaginary, _prec));
        }

        /// <summary>Walk one bit; false when a substep fails (the point is unchanged).</summary>
        public bool Advance(CancellationToken ct = default)
        {
            int k = Bits;
            var keepRef = _ref; var keepDelta = _delta; int keepPrec = _prec;
            // The last bit's move sets how far down the reference must be exact.
            if (!_delta.IsZero)
            {
                double moveLog2 = _delta.Log2Abs();
                _prec = SMath.Max(_prec, (int)SMath.Ceiling(-moveLog2) + 128);
            }
            _ref = Point.WithPrecision(_prec);
            _delta = CFx.Zero;
            _z = Orbit(_ref, k + 1, _prec, _z, ct);

            double phase = 2 * SMath.PI * _angle.DoubledValue(k);
            for (int j = 0; j < _sharpness; j++)
            {
                double r = SMath.Pow(ExternalRay.StartRadius, SMath.Pow(0.5, (j + 0.5) / _sharpness));
                Substeps++;
                if (!SolveDepth(_z, ref _delta, k, Complex.FromPolarCoordinates(r, phase), _inDouble, out bool usedDouble))
                {
                    (_ref, _delta, _prec) = (keepRef, keepDelta, keepPrec);
                    return false;
                }
                if (usedDouble) DoubleSubsteps++;
            }
            Bits = k + 1;
            return true;
        }
    }

    // Z_0..Z_n at c in BigFloat, stored as double.
    private static Complex[] Orbit(ExactCentre c, int n, int prec, Complex[] buffer, CancellationToken ct)
    {
        var z = buffer.Length >= n + 1 ? buffer : new Complex[SMath.Max(n + 1, 2 * buffer.Length)];
        BigFloat x = BigFloat.Zero(prec), y = BigFloat.Zero(prec);
        z[0] = Complex.Zero;
        for (int i = 1; i <= n; i++)
        {
            if ((i & 0xFF) == 0) ct.ThrowIfCancellationRequested();
            var x2 = BigFloat.Mul(x, x, prec); var y2 = BigFloat.Mul(y, y, prec); var xy = BigFloat.Mul(x, y, prec);
            x = BigFloat.Add(BigFloat.Sub(x2, y2, prec), c.Re, prec);
            y = BigFloat.Add(xy.ScaleB(1), c.Im, prec);
            z[i] = new Complex(x.ToDouble(), y.ToDouble());
        }
        return z;
    }

    // Newton on Δ (c = reference + Δ) for f^{k+1}_c(0) = target, perturbed. #1872: in
    // double while every value is a normal double, else in CFx.
    internal static bool SolveDepth(Complex[] z, ref CFx delta, int k, Complex target, bool inDouble = true)
        => SolveDepth(z, ref delta, k, target, inDouble, out _);

    internal static bool SolveDepth(Complex[] z, ref CFx delta, int k, Complex target, bool inDouble, out bool usedDouble)
    {
        bool ok = false;
        usedDouble = inDouble && SolveDepthDouble(z, ref delta, k, target, out ok);
        if (usedDouble) return ok;
        double last = double.PositiveInfinity;
        var t = CFx.Of(target.Real, target.Imaginary);
        for (int i = 0; i < NewtonSteps; i++)
        {
            CFx e = CFx.Zero, dc = CFx.Zero;
            for (int n = 0; n <= k; n++)
            {
                var zn = CFx.Of(z[n].Real, z[n].Imaginary);
                dc = ((zn + e) * dc).Twice().PlusOne();
                e = (zn.Twice() + e) * e + delta;
            }
            var step = (CFx.Of(z[k + 1].Real, z[k + 1].Imaginary) + e - t) / dc;
            if (!step.IsFinite) return false;
            delta -= step;
            if (!delta.IsFinite) return false;
            double m = step.Log2Abs(), d = delta.Log2Abs();
            if (m <= d - 46.5) return true;                       // 1e-14
            if (m <= d - 29.9 && m >= last) return true;           // noise floor, 1e-9
            last = m;
        }
        return last <= delta.Log2Abs() - 26.6;                     // 1e-8
    }

    // #1872 — the same Newton in double, mirroring CFx's operations (a product as
    // (ac − bd, ad + bc), a quotient as a product with the reciprocal). Returns false (and
    // leaves Δ) when a value leaves the normal-double range, so CFx redoes the bit.
    private static readonly double NormalLo = SMath.ScaleB(1.0, -900), NormalHi = SMath.ScaleB(1.0, 900);
    private static bool SolveDepthDouble(Complex[] z, ref CFx delta, int k, Complex target, out bool ok)
    {
        ok = false;
        double dr = delta.Re.ToDouble(), di = delta.Im.ToDouble();
        if (!InRange(dr, di)) return false;
        double last = double.PositiveInfinity;
        for (int i = 0; i < NewtonSteps; i++)
        {
            double er = 0, ei = 0, cr = 0, ci = 0;
            for (int n = 0; n <= k; n++)
            {
                double zr = z[n].Real, zi = z[n].Imaginary;
                double ar = zr + er, ai = zi + ei;                       // (zn + e) · dc, ·2, + 1
                double pr = ar * cr - ai * ci, pi = ar * ci + ai * cr;
                cr = pr * 2 + 1.0; ci = pi * 2;
                double br = zr * 2 + er, bi = zi * 2 + ei;               // (2zn + e) · e + Δ
                double qr = br * er - bi * ei, qi = br * ei + bi * er;
                er = qr + dr; ei = qi + di;
            }
            if (!(SMath.Abs(cr) <= NormalHi && SMath.Abs(ci) <= NormalHi && SMath.Abs(er) <= NormalHi && SMath.Abs(ei) <= NormalHi))
                return false;
            double nr = z[k + 1].Real + er - target.Real, ni = z[k + 1].Imaginary + ei - target.Imaginary;
            // 1 / dc, scaled by a power of two first so |dc|² stays in range.
            int sc = SMath.ILogB(SMath.Max(SMath.Abs(cr), SMath.Abs(ci)));
            double ar2 = SMath.ScaleB(cr, -sc), ai2 = SMath.ScaleB(ci, -sc), n2 = ar2 * ar2 + ai2 * ai2;
            double rr = SMath.ScaleB(ar2 / n2, -sc), ri = SMath.ScaleB(-ai2 / n2, -sc);
            double sr = nr * rr - ni * ri, si = nr * ri + ni * rr;
            if (!double.IsFinite(sr) || !double.IsFinite(si)) return false;
            dr -= sr; di -= si;
            if (!InRange(dr, di) || !InRange(sr, si)) return false;
            double m = Log2Abs(sr, si), d = Log2Abs(dr, di);
            if (m <= d - 46.5) { delta = CFx.Of(dr, di); ok = true; return true; }
            if (m <= d - 29.9 && m >= last) { delta = CFx.Of(dr, di); ok = true; return true; }
            last = m;
        }
        delta = CFx.Of(dr, di);
        ok = last <= Log2Abs(dr, di) - 26.6;
        return true;
    }

    // Both parts zero or within the normal range away from under/overflow.
    private static bool InRange(double re, double im)
    {
        double m = SMath.Max(SMath.Abs(re), SMath.Abs(im));
        return m == 0 || (m >= NormalLo && m <= NormalHi);
    }

    private static double Log2Abs(double re, double im) => SMath.Log2(SMath.Sqrt(re * re + im * im));

    /// <summary>As <see cref="ExternalRay.Land"/>: trace R_θ to where it lands and verify
    /// the landing point in BigFloat.</summary>
    public static DeepRayLanding Land(ExternalAngle angle, int maxBits = DefaultMaxBits, CancellationToken ct = default)
        => Land(angle, DeepRayTuning.Default, maxBits, ct);

    /// <summary>#1872 — <see cref="Land(ExternalAngle, int, CancellationToken)"/> with the
    /// speed-ups chosen (tests compare <see cref="DeepRayTuning.Plain"/>).</summary>
    public static DeepRayLanding Land(ExternalAngle angle, DeepRayTuning tuning, int maxBits = DefaultMaxBits, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(angle);
        var t = new Tracer(angle, tuning);
        var r = LandCore(angle, t, tuning, maxBits, ct);
        return r with { Substeps = t.Substeps, DoubleSubsteps = t.DoubleSubsteps };
    }

    private static DeepRayLanding LandCore(ExternalAngle angle, Tracer t, DeepRayTuning tuning, int maxBits, CancellationToken ct)
    {
        if (angle.Pre.Length == 0 && angle.Per == "0")
            return Fail(angle, default, double.NaN, "Angle 0 lands on the cusp c = 1/4 (the main cardioid's root); go there with Reset.");

        int p = angle.Period, l = angle.Preperiod;
        var history = new List<ExactCentre> { t.Point };
        bool precisionStop = false;
        int periodicBits = SMath.Min(maxBits, SMath.Max(ExternalRay.PeriodicMinBits, ExternalRay.PeriodicBitsPerPeriod * p));
        double stopLog2 = SMath.Log2(ExternalRay.PreperiodicStop);
        DeepRayLanding? landed = null;
        while (t.Bits < (angle.IsPeriodic ? periodicBits : maxBits))
        {
            ct.ThrowIfCancellationRequested();
            if (!t.Advance(ct)) { precisionStop = true; break; }
            history.Add(t.Point);
            if (!angle.IsPeriodic && t.Bits >= l + 2 * p && (t.Bits - l) % p == 0)
            {
                double move = DistLog2(history[^1], history[^(p + 1)]);
                double first = DistLog2(history[l + p], history[l]);
                // Converged relative to its first cycle: try to land. When the seed is not
                // yet close enough (a long preperiod, e.g. a point tuned deep into a
                // minibrot), walk one period more and try again.
                if (move <= first + stopLog2)
                {
                    landed = TryMisiurewicz(angle, t.Point, history, tuning, ct);
                    if (landed != null) return landed.Value;
                }
            }
        }
        if (t.Bits < l + p)
            return Fail(angle, default, double.NaN, "The ray could not be traced that far.");

        var seed = t.Point;
        double scaleLog2 = ScaleLog2(history, p, seed);
        double scaleLog10 = scaleLog2 / Log2Of10;
        string limit = precisionStop ? " (the trace stopped early)" : "";

        if (angle.IsPeriodic)
        {
            double capLog2 = 1 + SMath.Max(0, MagLog2(seed));
            for (double reachLog2 = scaleLog2 + 2; ; reachLog2 = SMath.Min(capLog2, reachLog2 + 3))
            {
                ct.ThrowIfCancellationRequested();
                var n = DeepNucleusFinder.Find(seed, p, reachLog2 / Log2Of10, ct: ct);
                if (n.Found && DistLog2(n.Nucleus, seed) <= RootReachLog2 + n.Size.Log2Abs())
                    return new DeepRayLanding(RayLandingStatus.Component, angle, seed, scaleLog10, n, default);
                if (reachLog2 >= capLog2) break;
            }
            return Fail(angle, seed, scaleLog10, $"No period-{p} nucleus found near the ray's landing point{limit}.");
        }

        landed = TryMisiurewicz(angle, seed, history, tuning, ct);
        return landed ?? Fail(angle, seed, scaleLog10, $"No Misiurewicz point M({l + 1},{p}) found near the ray's landing point{limit}.");
    }

    private static double ScaleLog2(List<ExactCentre> history, int p, ExactCentre seed)
    {
        int back = SMath.Min(p, history.Count - 1);
        double s = DistLog2(history[^1], history[^(back + 1)]);
        return double.IsFinite(s) ? s : -seed.Precision + 64;
    }

    // A preperiodic landing from `seed`: M(l+1, q) over the divisors q of the angle's
    // period, the reach 4..4096 trace steps (as ExternalRay.Land); null when none verifies.
    // #1872: a divisor whose Newton ran out of steps without leaving its reach (it creeps,
    // next to a cluster of roots) does the same in the wider windows, so they skip it.
    private static DeepRayLanding? TryMisiurewicz(ExternalAngle angle, ExactCentre seed, List<ExactCentre> history,
        DeepRayTuning tuning, CancellationToken ct)
    {
        int p = angle.Period, l = angle.Preperiod;
        double scaleLog2 = ScaleLog2(history, p, seed);
        var exhausted = new HashSet<int>();
        for (double reachLog2 = scaleLog2 + 2; reachLog2 <= scaleLog2 + 12; reachLog2 += 3)
            for (int q = 1; q <= p; q++)
            {
                if (p % q != 0 || exhausted.Contains(q)) continue;
                int k = l + 1, qq = q;
                var at = seed;
                for (int fix = 0; fix < 4; fix++)
                {
                    ct.ThrowIfCancellationRequested();
                    var m = DeepMisiurewiczFinder.Refine(at, k, qq, seed, reachLog2 / Log2Of10, out bool ranOut, ct);
                    if (tuning.SkipExhaustedWindows && fix == 0 && ranOut) exhausted.Add(q);
                    if (m.Found)
                    {
                        double context = DistLog2(history[SMath.Min(l + p + 1, history.Count - 1)], m.Point);
                        return new DeepRayLanding(RayLandingStatus.Misiurewicz, angle, seed, scaleLog2 / Log2Of10, default, m,
                            ContextRadiusLog10: SMath.Max(context, scaleLog2 + 9.97) / Log2Of10);
                    }
                    if (m.Status is not (MisiurewiczStatus.LowerPreperiod or MisiurewiczStatus.LowerPeriod)) break;
                    (k, qq, at) = (m.Preperiod, m.Period, m.Point);
                }
            }
        return null;
    }

    private static ExactCentre Add(ExactCentre c, CFx d, int prec)
        => d.IsZero ? c.WithPrecision(prec)
           : new ExactCentre(BigFloat.Add(c.Re.WithPrecision(prec), BigFloat.FromFloatExp(d.Re, prec), prec),
                             BigFloat.Add(c.Im.WithPrecision(prec), BigFloat.FromFloatExp(d.Im, prec), prec));

    private static double DistLog2(ExactCentre a, ExactCentre b)
    {
        int prec = SMath.Max(a.Precision, b.Precision);
        return new CFx(BigFloat.Sub(a.Re.WithPrecision(prec), b.Re.WithPrecision(prec), prec).ToFloatExp(),
                       BigFloat.Sub(a.Im.WithPrecision(prec), b.Im.WithPrecision(prec), prec).ToFloatExp()).Log2Abs();
    }

    private static double MagLog2(ExactCentre a) => new CFx(a.Re.ToFloatExp(), a.Im.ToFloatExp()).Log2Abs();

    private static DeepRayLanding Fail(ExternalAngle a, ExactCentre end, double scaleLog10, string why)
        => new(RayLandingStatus.Failed, a, end, scaleLog10, default, default, why);
}
