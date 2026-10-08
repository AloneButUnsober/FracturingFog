// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Explore/MisiurewiczFinder.cs
//
// Interesting-location finder S3 (#1187, epic #1184). Design:
// Docs/Technical/Interesting-Location-Finder-DesignPlan.md §3.4.
//
// A Misiurewicz point M(k,p) of z² + c is a parameter whose critical orbit is
// strictly preperiodic: z_{k+p} = z_k with k ≥ 1 and p ≥ 1 both minimal
// (z₀ = 0, z₁ = c). Near M(k,p) the set looks like the Julia set J_c there
// (Tan Lei), so these are the spiral centres and branch hubs of filaments.
//
// Search from a clicked point c₀ (same propose-then-verify shape as S2):
//   1. Reference orbit Zₙ and dZₙ = dzₙ/dc at c₀ (OD orbit, stored as double).
//   2. For every (k, p): first-order distance to a root of
//      G = z_{k+p} − z_k is |G/G′| = |Z_{k+p} − Z_k| / |dZ_{k+p} − dZ_k|.
//      If M(k,p) is a root, so is every (k′ ≥ k, p) and (k, multiple of p),
//      so per p only the smallest k within reach is kept, and candidates are
//      tried simplest first (k + p ascending).
//   3. Deflated Newton (Heiland-Allen): divide G by the factors whose roots are
//      the unwanted solutions — lower preperiods i < k (i = 0 is the
//      hyperbolic centres) and proper divisor periods q | p — so Newton cannot
//      slide onto them. Only the log-derivative is needed:
//        G′/G = (dz_{k+p} − dz_k)/(z_{k+p} − z_k)
//             − Σ_{i<k} (dz_{i+p} − dz_i)/(z_{i+p} − z_i)
//             − Σ_{q|p, q<p} (dz_{k+q} − dz_k)/(z_{k+q} − z_k)
//      and Δc = 1/(G′/G). It runs perturbed in double (εₙ about Zₙ), then is
//      polished and verified in octuple-double.
//   4. Accepted only if (k, p) is minimal at the polished point and it lies
//      within the accept radius of the click.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

using FracturingFog.FFMath;

using SMath = System.Math;

namespace FracturingFog.Abstractions.Explore;

/// <summary>Outcome of a Misiurewicz search / refinement.</summary>
public enum MisiurewiczStatus
{
    /// <summary>Converged to a Misiurewicz point with minimal (k, p) in reach.</summary>
    Found,
    /// <summary>No candidate converged within reach.</summary>
    NotFound,
    /// <summary>Converged, but the true preperiod is smaller than requested.</summary>
    LowerPreperiod,
    /// <summary>Converged, but the true period is a proper divisor of the request.</summary>
    LowerPeriod,
    /// <summary>The view is beyond octuple-double precision.</summary>
    PrecisionLimit,
}

/// <summary>Result of <see cref="MisiurewiczFinder"/>. <see cref="Multiplier"/>
/// λ = (fᵖ)′ on the repelling cycle: zooming in by |λ| about the point repeats
/// the picture, rotated by arg λ (valid only when Found).</summary>
public readonly record struct MisiurewiczResult(
    MisiurewiczStatus Status, DeepComplex Point, int Preperiod, int Period, Complex Multiplier, int Attempts)
{
    public bool Found => Status == MisiurewiczStatus.Found;
}

/// <summary>Misiurewicz-point finder for f_c(z) = z² + c. UI-free; see the file
/// header for the method.</summary>
public static class MisiurewiczFinder
{
    /// <summary>Longest preperiod + period searched (reference orbit length).</summary>
    public const int DefaultMaxOrbit = 4096;

    /// <summary>Largest period searched.</summary>
    public const int DefaultMaxPeriod = 512;

    /// <summary>Candidates tried before giving up.</summary>
    public const int DefaultMaxAttempts = 64;

    private const int MaxSteps = 64;
    private const double ReferenceEscape = 1e6;

    /// <summary>Find the simplest Misiurewicz point within
    /// <paramref name="acceptRadius"/> of <paramref name="click"/>.</summary>
    public static MisiurewiczResult FindNear(
        DeepComplex click, double acceptRadius,
        int maxOrbit = DefaultMaxOrbit, int maxPeriod = DefaultMaxPeriod, int maxAttempts = DefaultMaxAttempts,
        CancellationToken ct = default)
    {
        if (!(acceptRadius > 0) || double.IsInfinity(acceptRadius))
            throw new ArgumentOutOfRangeException(nameof(acceptRadius), acceptRadius, "Accept radius must be positive and finite.");
        if (acceptRadius < NucleusFinder.MinViewRadius)
            return new MisiurewiczResult(MisiurewiczStatus.PrecisionLimit, click, 0, 0, Complex.Zero, 0);

        var refo = ReferenceOrbit(click, maxOrbit, ct);
        var (z, dz) = (refo.Z, refo.Dz);
        int n = z.Length;   // valid indices 0..n-1

        // Per period: the smallest preperiod whose first-order root estimate is
        // within reach (a few accept radii — Newton covers the rest).
        double reach = 4 * acceptRadius;
        var candidates = new List<(int K, int P, double Est)>();
        for (int p = 1; p <= SMath.Min(maxPeriod, n - 2); p++)
        {
            if ((p & 0x3F) == 0) ct.ThrowIfCancellationRequested();
            for (int k = 1; k + p < n; k++)
            {
                Complex den = dz[k + p] - dz[k];
                if (den == Complex.Zero) continue;
                double dm = den.Magnitude;
                // Cheap double difference first; it is only ~1e-16·|Z| accurate,
                // so fall back to the exact OD difference when that error could
                // still put the root within reach (always, at depth).
                double rough = (z[k + p] - z[k]).Magnitude;
                double err = 4e-16 * (z[k + p].Magnitude + z[k].Magnitude);
                if ((rough - err) / dm > reach) continue;
                double est = refo.Diff(k + p, k).Magnitude / dm;
                if (est <= reach) { candidates.Add((k, p, est)); break; }
            }
        }
        candidates.Sort((a, b) => a.K + a.P != b.K + b.P ? (a.K + a.P).CompareTo(b.K + b.P) : a.Est.CompareTo(b.Est));

        int attempts = 0;
        foreach (var (k, p, _) in candidates)
        {
            if (attempts >= maxAttempts) break;
            ct.ThrowIfCancellationRequested();
            attempts++;
            if (!PerturbedNewton(refo, k, p, acceptRadius, out Complex delta)) continue;
            var r = Refine(click.Translate(delta.Real, delta.Imaginary), k, p, click, acceptRadius, ct);
            if (r.Found) return r with { Attempts = attempts };
        }
        return new MisiurewiczResult(MisiurewiczStatus.NotFound, click, 0, 0, Complex.Zero, attempts);
    }

    /// <summary>Polish <paramref name="seed"/> to M(<paramref name="preperiod"/>,
    /// <paramref name="period"/>) in octuple-double and verify (k, p) is minimal
    /// there. The result must lie within <paramref name="acceptRadius"/> of
    /// <paramref name="anchor"/>.</summary>
    public static MisiurewiczResult Refine(
        DeepComplex seed, int preperiod, int period, DeepComplex anchor, double acceptRadius,
        CancellationToken ct = default)
    {
        if (preperiod < 1) throw new ArgumentOutOfRangeException(nameof(preperiod));
        if (period < 1) throw new ArgumentOutOfRangeException(nameof(period));
        int k = preperiod, p = period;
        OD cx = seed.Re, cy = seed.Im;
        double lastStep = double.PositiveInfinity;
        bool converged = false;
        for (int s = 0; s < MaxSteps; s++)
        {
            ct.ThrowIfCancellationRequested();
            var (zs, dzs) = OrbitOd(cx, cy, k + p, ct);
            if (zs == null) break;
            Complex? step = DeflatedStep(zs, dzs, k, p);
            if (step is not Complex d || !IsFinite(d)) break;
            cx = OdExact.Add(cx, new OD(-d.Real)); cy = OdExact.Add(cy, new OD(-d.Imaginary));
            double mag = d.Magnitude;
            double fromAnchor = Hypot(OdExact.Sub(cx, anchor.Re).X0, OdExact.Sub(cy, anchor.Im).X0);
            if (fromAnchor > 8 * acceptRadius) break;
            double cMag = SMath.Max(Hypot(cx.X0, cy.X0), acceptRadius);
            if (mag <= 1e-110 * cMag || (mag >= lastStep && mag <= 1e-100 * cMag))
            {
                lastStep = SMath.Min(lastStep, mag);   // never leave it ∞: it scales the minimality tolerance
                converged = true;
                break;
            }
            lastStep = mag;
        }
        var point = new DeepComplex(cx, cy);
        if (!converged)
            return new MisiurewiczResult(MisiurewiczStatus.NotFound, point, k, p, Complex.Zero, 0);
        if (Hypot(OdExact.Sub(cx, anchor.Re).X0, OdExact.Sub(cy, anchor.Im).X0) > acceptRadius)
            return new MisiurewiczResult(MisiurewiczStatus.NotFound, point, k, p, Complex.Zero, 0);

        // Minimality at the polished point: the defining equation must hold for
        // (k, p) and fail for every smaller preperiod and every divisor period.
        var (z, d1) = OrbitOd(cx, cy, k + p, ct);
        if (z == null)
            return new MisiurewiczResult(MisiurewiczStatus.NotFound, point, k, p, Complex.Zero, 0);
        double tol = SMath.Max(lastStep * 1e3, SMath.Max(Hypot(cx.X0, cy.X0), 1e-300) * 1e-100);
        if (!IsRoot(z, d1, k, p, tol))
            return new MisiurewiczResult(MisiurewiczStatus.NotFound, point, k, p, Complex.Zero, 0);
        for (int i = 1; i < k; i++)
            if (IsRoot(z, d1, i, p, tol))
                return new MisiurewiczResult(MisiurewiczStatus.LowerPreperiod, point, i, p, Complex.Zero, 0);
        for (int q = 1; q < p; q++)
            if (p % q == 0 && IsRoot(z, d1, k, q, tol))
                return new MisiurewiczResult(MisiurewiczStatus.LowerPeriod, point, k, q, Complex.Zero, 0);

        return new MisiurewiczResult(MisiurewiczStatus.Found, point, k, p, Multiplier(z, k, p), 0);
    }

    /// <summary>λ = Π 2zᵢ over the repelling cycle z_k … z_{k+p−1}.</summary>
    private static Complex Multiplier(OD[][] z, int k, int p)
    {
        Complex m = Complex.One;
        for (int i = k; i < k + p; i++) m *= 2.0 * new Complex(z[0][i].X0, z[1][i].X0);
        return m;
    }

    // Newton step toward a root of z_{a+b} − z_a would be ≤ tol: (a, b) holds here.
    private static bool IsRoot(OD[][] z, Complex[] dz, int a, int b, double tol)
    {
        double g = Hypot(OdExact.Sub(z[0][a + b], z[0][a]).X0, OdExact.Sub(z[1][a + b], z[1][a]).X0);
        double gp = (dz[a + b] - dz[a]).Magnitude;
        return gp > 0 && g / gp <= tol;
    }

    // Δc = 1 / (G′/G) for the deflated G (file header), from an OD orbit.
    private static Complex? DeflatedStep(OD[][] z, Complex[] dz, int k, int p)
    {
        Complex Diff(int a, int b) => new(OdExact.Sub(z[0][a], z[0][b]).X0, OdExact.Sub(z[1][a], z[1][b]).X0);
        Complex g = Diff(k + p, k);
        if (g == Complex.Zero) return Complex.Zero;   // exactly on the root
        Complex l = (dz[k + p] - dz[k]) / g;
        for (int i = 0; i < k; i++)
        {
            Complex f = Diff(i + p, i);
            if (f == Complex.Zero) return null;     // sits on an unwanted root
            l -= (dz[i + p] - dz[i]) / f;
        }
        for (int q = 1; q < p; q++)
        {
            if (p % q != 0) continue;
            Complex f = Diff(k + q, k);
            if (f == Complex.Zero) return null;
            l -= (dz[k + q] - dz[k]) / f;
        }
        return l == Complex.Zero ? null : Complex.Reciprocal(l);
    }

    // OD orbit z₀..z_len (as [re[], im[]]) and double dz/dc; null on escape.
    private static (OD[][]? Z, Complex[] Dz) OrbitOd(OD cx, OD cy, int len, CancellationToken ct)
    {
        var re = new OD[len + 1];
        var im = new OD[len + 1];
        var dz = new Complex[len + 1];
        re[0] = OD.Zero; im[0] = OD.Zero;
        for (int n = 0; n < len; n++)
        {
            if ((n & 0xFF) == 0xFF) ct.ThrowIfCancellationRequested();
            dz[n + 1] = 2.0 * new Complex(re[n].X0, im[n].X0) * dz[n] + Complex.One;
            (re[n + 1], im[n + 1]) = OdExact.SquareAdd(re[n], im[n], cx, cy);
            if (!double.IsFinite(re[n + 1].X0) || Hypot(re[n + 1].X0, im[n + 1].X0) > ReferenceEscape)
                return (null, dz);
        }
        return (new[] { re, im }, dz);
    }

    // Reference orbit at the click: OD values (for exact differences, which
    // cancel to ~0 near a Misiurewicz point) plus double copies and dZ/dc.
    private sealed class Reference
    {
        public required OD[] Re, Im;
        public required Complex[] Z, Dz;
        /// <summary>Z_a − Z_b, exact (OdExact), rounded to double.</summary>
        public Complex Diff(int a, int b)
            => new(OdExact.Sub(Re[a], Re[b]).X0, OdExact.Sub(Im[a], Im[b]).X0);
    }

    // Stops early once Zₙ escapes.
    private static Reference ReferenceOrbit(DeepComplex c, int maxN, CancellationToken ct)
    {
        var re = new List<OD> { OD.Zero };
        var im = new List<OD> { OD.Zero };
        var z = new List<Complex> { Complex.Zero };
        var dz = new List<Complex> { Complex.Zero };
        OD zx = OD.Zero, zy = OD.Zero;
        Complex d = Complex.Zero;
        for (int n = 1; n <= maxN; n++)
        {
            if ((n & 0xFF) == 0) ct.ThrowIfCancellationRequested();
            d = 2.0 * new Complex(zx.X0, zy.X0) * d + Complex.One;
            (zx, zy) = OdExact.SquareAdd(zx, zy, c.Re, c.Im);
            var v = new Complex(zx.X0, zy.X0);
            if (v.Magnitude > ReferenceEscape || !IsFinite(d)) break;
            re.Add(zx); im.Add(zy); z.Add(v); dz.Add(d);
        }
        return new Reference { Re = re.ToArray(), Im = im.ToArray(), Z = z.ToArray(), Dz = dz.ToArray() };
    }

    // Deflated Newton, perturbed about the reference orbit (c = c₀ + δ). Every
    // difference z_a − z_b is taken as (Z_a − Z_b) exact from OD plus
    // (ε_a − ε_b) in double, so its error scales with δ, not with |Z| — the
    // root stays resolvable at any depth.
    private static bool PerturbedNewton(Reference r, int k, int p, double acceptRadius, out Complex delta)
    {
        delta = Complex.Zero;
        int len = k + p;
        var Z = r.Z;
        var eps = new Complex[len + 1];
        var dz = new Complex[len + 1];

        // Reference differences the deflated log-derivative needs, once.
        Complex dTarget = r.Diff(k + p, k);
        var dLower = new Complex[k];
        for (int i = 0; i < k; i++) dLower[i] = r.Diff(i + p, i);
        var divisors = new List<int>();
        for (int q = 1; q < p; q++) if (p % q == 0) divisors.Add(q);
        var dDiv = new Complex[divisors.Count];
        for (int j = 0; j < divisors.Count; j++) dDiv[j] = r.Diff(k + divisors[j], k);

        double prev = double.PositiveInfinity;
        for (int s = 0; s < MaxSteps; s++)
        {
            eps[0] = Complex.Zero; dz[0] = Complex.Zero;
            for (int n = 0; n < len; n++)
            {
                Complex zn = Z[n] + eps[n];
                dz[n + 1] = 2.0 * zn * dz[n] + Complex.One;
                eps[n + 1] = (2.0 * Z[n] + eps[n]) * eps[n] + delta;   // εₙ₊₁ = 2Zₙεₙ + εₙ² + δ
            }
            Complex g = dTarget + (eps[k + p] - eps[k]);
            if (!IsFinite(g) || g == Complex.Zero) return g == Complex.Zero && delta.Magnitude <= acceptRadius;
            Complex l = (dz[k + p] - dz[k]) / g;
            for (int i = 0; i < k; i++) l -= (dz[i + p] - dz[i]) / (dLower[i] + (eps[i + p] - eps[i]));
            for (int j = 0; j < divisors.Count; j++)
            {
                int q = divisors[j];
                l -= (dz[k + q] - dz[k]) / (dDiv[j] + (eps[k + q] - eps[k]));
            }
            if (!IsFinite(l) || l == Complex.Zero) return false;
            Complex step = Complex.Reciprocal(l);
            delta -= step;
            double mag = step.Magnitude;
            if (!(delta.Magnitude <= 8 * acceptRadius)) return false;
            double scale = SMath.Max(delta.Magnitude, 1e-3 * acceptRadius);
            if (mag <= 1e-13 * scale || (s >= 3 && mag >= 0.5 * prev && mag <= 1e-9 * scale))
                return delta.Magnitude <= acceptRadius;
            prev = mag;
        }
        return false;
    }

    private static bool IsFinite(Complex v) => double.IsFinite(v.Real) && double.IsFinite(v.Imaginary);
    private static double Hypot(double a, double b) => SMath.Sqrt(a * a + b * b);
}
