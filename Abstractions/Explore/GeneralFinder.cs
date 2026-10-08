// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Explore/GeneralFinder.cs
//
// Interesting-location finder S7 (#1191): minibrot and Misiurewicz finders for
// every IOrbitMap (Multibrot, Burning Ship, Tricorn, User Equation), by
// Newton in R² with the 2×2 Jacobian ∂z_n/∂c carried along the orbit.
//
// Nucleus of period p: z_p(c) = z₀(c). It is accepted only when
//   • Newton converged inside the view,
//   • p is minimal (no proper divisor q has a root at the same c), and
//   • the cycle through z₀ is ATTRACTING (spectral radius of the cycle
//     Jacobian < 1).
// The last test is what makes this sound for any start point: then z₀ is
// attracted to the cycle, so c lies inside a hyperbolic component of the
// rendered set. With z₀ critical (z^d + c, Burning Ship, Tricorn at 0) the
// radius is 0 — a true superattracting nucleus.
//
// Misiurewicz point M(k, p): z_{k+p}(c) = z_k(c), minimal (k ≥ 1, p), with a
// REPELLING cycle. Newton converges to whichever root is nearest; the result
// is re-classified to its minimal (k, p) afterwards instead of deflating.
//
// Double precision: these families render in double, so views deeper than
// AutoExplorer.DoubleMaxZoom (1e13) are refused by the caller.

using System;
using System.Collections.Generic;
using System.Threading;

using SMath = System.Math;

namespace FracturingFog.Abstractions.Explore;

public enum GeneralStatus { Found, NotFound }

/// <summary>A found minibrot. <see cref="BodyRadius"/> is the distance from
/// the nucleus to the farthest edge of its main body (measured by escape);
/// <see cref="Zoom"/> frames the whole minibrot.</summary>
public readonly record struct GeneralNucleus(
    GeneralStatus Status, V2 Point, int Period, double BodyRadius, double CycleRadius, int Attempts)
{
    public bool Found => Status == GeneralStatus.Found;

    /// <summary>The zoom that shows the whole minibrot, as the home view shows
    /// the whole Mandelbrot set (whose main body reaches 0.75 from its nucleus).</summary>
    public double Zoom => GeneralFinder.FrameZoom(BodyRadius);
}

/// <summary>A found Misiurewicz point M(Preperiod, Period).
/// <see cref="Multiplier"/> = spectral radius of the cycle Jacobian (the zoom
/// factor after which the pattern repeats); <see cref="TurnDegrees"/> is its
/// rotation when the cycle Jacobian is conformal, else null.</summary>
public readonly record struct GeneralMisiurewicz(
    GeneralStatus Status, V2 Point, int Preperiod, int Period, double Multiplier, double? TurnDegrees, int Attempts)
{
    public bool Found => Status == GeneralStatus.Found;
}

public static class GeneralFinder
{
    public const int DefaultMaxPeriod = 2048;
    /// <summary>Work cap (plain steps × map cost) of one minibrot search.</summary>
    public const long DefaultMaxWork = 60_000_000;
    public const int MaxNewtonSteps = 64;
    /// <summary>Two roots closer than this fraction of the search reach are
    /// the same root (lower-period / lower-preperiod tests).</summary>
    public const double SameRootFraction = 1e-6;
    /// <summary><see cref="BodyRadius"/> of the Mandelbrot main cardioid
    /// (measured: the median edge distance over the rays), so a minibrot frames
    /// the way the home view frames the whole set.</summary>
    public const double HomeBodyRadius = 0.72;
    /// <summary>Seed grid per side: Newton starts from the grid point in the
    /// view whose orbit survives longest (closest to the set).</summary>
    public const int SeedGrid = 9;
    /// <summary>Steps past escape for which Newton from the centre still
    /// starts (the orbit overflows a few steps after it escapes).</summary>
    public const int EscapeSlack = 4;
    /// <summary>Margin so the minibrot's antennae fit: frame zoom =
    /// FrameFit · HomeBodyRadius / BodyRadius.</summary>
    public const double FrameFit = 0.8;
    /// <summary>Escape-test iterations per period (and the floor) for the body radius.</summary>
    public const int BodyItersPerPeriod = 100;
    public const int BodyMinIters = 2000;
    public const int BodyMaxIters = 200_000;
    public const int BodyDirections = 12;
    /// <summary>|z|² beyond which Newton's orbit counts as overflowed.</summary>
    public const double OverflowRadius2 = 1e200;

    public static double FrameZoom(double bodyRadius)
        => bodyRadius > 0 && double.IsFinite(bodyRadius) ? FrameFit * HomeBodyRadius / bodyRadius : double.NaN;

    // ── Orbit with ∂/∂c ──────────────────────────────────────────────────

    /// <summary>The whole orbit z_0..z_n with Jacobians (stops early at escape).</summary>
    private static int OrbitAll(IOrbitMap map, V2 c, int n, V2[] zs, M2[] js)
    {
        map.Start(c, out var z, out var j);
        zs[0] = z; js[0] = j;
        const double esc = OverflowRadius2;
        for (int i = 1; i <= n; i++)
        {
            map.Step(z, c, out var nz, out var a, out var b);
            j = a * j + b;
            z = nz;
            if (!(z.X * z.X + z.Y * z.Y <= esc) || !j.IsFinite) return i - 1;
            zs[i] = z; js[i] = j;
        }
        return n;
    }

    // Newton on G(c) = z_{k+p} − z_k (k = 0: nucleus, G = z_p − z₀).
    private static bool Newton(IOrbitMap map, ref V2 c, int k, int p, V2 anchor, double reach, ref long work,
                               CancellationToken ct)
    {
        double last = double.PositiveInfinity;
        for (int it = 0; it < MaxNewtonSteps; it++)
        {
            ct.ThrowIfCancellationRequested();
            if (!Residual(map, c, k, p, out var g, out var jg, ref work)) return false;
            if (!jg.TrySolve(g, out var d)) return false;
            double step = d.Length;
            // Clamp wild steps to the reach.
            if (step > reach) d = (reach / step) * d;
            c -= d;
            if (!c.IsFinite || (c - anchor).Length > 4 * reach) return false;
            double floor = 4e-16 * SMath.Max(c.Length, 1e-300);
            if (step <= floor) return true;
            // Noise floor: tiny and no longer shrinking.
            if (step <= 1e-9 * reach && step >= last) return true;
            last = step;
        }
        return last <= 1e-7 * reach;
    }

    // G and ∂G/∂c for (k, p).
    private static bool Residual(IOrbitMap map, V2 c, int k, int p, out V2 g, out M2 jg, ref long work)
    {
        g = default; jg = default;
        map.Start(c, out var z, out var j);
        // Newton runs through escaping orbits (|z_p| large still gives a
        // useful step toward the root); only overflow stops it.
        const double esc = OverflowRadius2;
        V2 zk = z; M2 jk = j;
        for (int i = 1; i <= k + p; i++)
        {
            map.Step(z, c, out var nz, out var a, out var b);
            j = a * j + b;
            z = nz;
            if (!(z.X * z.X + z.Y * z.Y <= esc) || !j.IsFinite) { work += (long)i * map.StepCost; return false; }
            if (i == k) { zk = z; jk = j; }
        }
        work += (long)(k + p) * map.StepCost;
        g = z - zk; jg = j - jk;
        return true;
    }

    // Newton-step length from c to the nearest root of (k, p): how far c is
    // from being such a root. +∞ when the orbit escapes.
    private static double RootDistance(IOrbitMap map, V2 c, int k, int p)
    {
        long w = 0;
        if (!Residual(map, c, k, p, out var g, out var jg, ref w)) return double.PositiveInfinity;
        return jg.TrySolve(g, out var d) ? d.Length : (g.Length == 0 ? 0 : double.PositiveInfinity);
    }

    private static double SameRootThreshold(V2 c, double reach)
        => SMath.Max(SameRootFraction * reach, 64 * 2.2e-16 * SMath.Max(1, c.Length));

    /// <summary>The smallest q dividing <paramref name="p"/> for which c is
    /// (to the threshold) a nucleus of period q.</summary>
    public static int ActualPeriod(IOrbitMap map, V2 c, int p, double reach)
    {
        double thr = SameRootThreshold(c, reach);
        for (int q = 1; q < p; q++)
            if (p % q == 0 && RootDistance(map, c, 0, q) <= thr) return q;
        return p;
    }

    /// <summary>Spectral radius of the cycle Jacobian ∂z_{k+p}/∂z_k along the
    /// orbit of c (the multiplier magnitude of the cycle through z_k).</summary>
    public static double CycleRadius(IOrbitMap map, V2 c, int k, int p)
    {
        map.Start(c, out var z, out _);
        for (int i = 0; i < k; i++) z = map.Next(z, c);
        var m = M2.Identity;
        for (int i = 0; i < p; i++)
        {
            map.Step(z, c, out var nz, out var a, out _);
            m = a * m;
            z = nz;
        }
        return m.IsFinite ? m.SpectralRadius : double.PositiveInfinity;
    }

    private static M2 CycleJacobian(IOrbitMap map, V2 c, int k, int p)
    {
        map.Start(c, out var z, out _);
        for (int i = 0; i < k; i++) z = map.Next(z, c);
        var m = M2.Identity;
        for (int i = 0; i < p; i++)
        {
            map.Step(z, c, out var nz, out var a, out _);
            m = a * m;
            z = nz;
        }
        return m;
    }

    // ── Minibrots ────────────────────────────────────────────────────────

    /// <summary>Find the lowest-period minibrot whose nucleus lies within
    /// <paramref name="viewRadius"/> of <paramref name="centre"/>, scanning
    /// periods upward until the work budget runs out.</summary>
    public static GeneralNucleus FindMinibrot(
        IOrbitMap map, V2 centre, double viewRadius,
        int maxPeriod = DefaultMaxPeriod, long maxWork = DefaultMaxWork,
        Action<int>? onPeriod = null, CancellationToken ct = default)
    {
        long work = 0;
        int attempts = 0;
        // Lowest period first (the dominant minibrot in view). Newton starts
        // at the centre; once the centre's orbit escapes too early for the
        // period, from the grid point whose orbit survives longest instead.
        int centreLife = EscapeTime(map, centre, maxPeriod);
        var deep = centreLife >= maxPeriod ? centre : DeepestSeed(map, centre, viewRadius, maxPeriod, ref work);
        for (int p = 1; p <= maxPeriod && work <= maxWork; p++)
        {
            if (p % 8 == 0) onPeriod?.Invoke(p);
            attempts++;
            var start = p <= centreLife + EscapeSlack ? centre : deep;
            var r = TryNucleus(map, start, centre, viewRadius, p, ref work, ct);
            if (r is GeneralNucleus g) return g with { Attempts = attempts };
        }
        return new GeneralNucleus(GeneralStatus.NotFound, centre, 0, double.NaN, double.NaN, attempts);
    }

    /// <summary>Newton from <paramref name="seed"/> for a period-p nucleus,
    /// verified (inside the reach, minimal period, attracting cycle) and
    /// measured. Null when it fails.</summary>
    public static GeneralNucleus? FindNucleus(IOrbitMap map, V2 seed, int period, double reach, CancellationToken ct = default)
    {
        long w = 0;
        return TryNucleus(map, seed, seed, reach, period, ref w, ct);
    }

    // Newton from `start`; the nucleus must lie within `reach` of `centre`.
    private static GeneralNucleus? TryNucleus(IOrbitMap map, V2 start, V2 centre, double reach, int p, ref long work, CancellationToken ct)
    {
        var c = start;
        if (!Newton(map, ref c, 0, p, centre, reach, ref work, ct)) return null;
        if ((c - centre).Length > reach) return null;
        if (ActualPeriod(map, c, p, reach) != p) return null;
        double rho = CycleRadius(map, c, 0, p);
        if (!(rho < 1)) return null;
        double body = BodyRadius(map, c, p, reach, ct);
        return new GeneralNucleus(GeneralStatus.Found, c, p, body, rho, 0);
    }

    /// <summary>The point of a SeedGrid² grid over the view disk whose orbit
    /// survives longest (the centre on ties, and when it never escapes).</summary>
    private static V2 DeepestSeed(IOrbitMap map, V2 centre, double radius, int maxIter, ref long work)
    {
        V2 best = centre;
        int bestN = EscapeTime(map, centre, maxIter);
        work += bestN;
        if (bestN >= maxIter) return centre;
        for (int j = 0; j < SeedGrid; j++)
            for (int i = 0; i < SeedGrid; i++)
            {
                double u = 2.0 * i / (SeedGrid - 1) - 1, v = 2.0 * j / (SeedGrid - 1) - 1;
                if (u * u + v * v > 1) continue;
                var c = centre + new V2(u * radius, v * radius);
                int n = EscapeTime(map, c, maxIter);
                work += n;
                if (n > bestN) { bestN = n; best = c; }
            }
        return best;
    }

    private static int EscapeTime(IOrbitMap map, V2 c, int maxIter)
    {
        map.Start(c, out var z, out _);
        double esc = map.EscapeRadius * map.EscapeRadius;
        for (int i = 0; i < maxIter; i++)
        {
            z = map.Next(z, c);
            if (!(z.X * z.X + z.Y * z.Y <= esc)) return i;
        }
        return maxIter;
    }

    /// <summary>Typical distance from the nucleus to the edge of its main
    /// body: along <see cref="BodyDirections"/> rays, double until a point
    /// escapes, then bisect the inside/escape edge; the median over the rays
    /// (robust to a ray that runs on through an attached bulb). NaN when no
    /// ray escapes.</summary>
    public static double BodyRadius(IOrbitMap map, V2 nucleus, int period, double reach, CancellationToken ct = default)
    {
        int maxIter = (int)SMath.Clamp((long)BodyItersPerPeriod * period, BodyMinIters, BodyMaxIters);
        double start = SMath.Max(reach * 1e-6, 1e-14 * SMath.Max(1, nucleus.Length));
        var radii = new List<double>(BodyDirections);
        for (int k = 0; k < BodyDirections; k++)
        {
            ct.ThrowIfCancellationRequested();
            double th = 2 * SMath.PI * (k + 0.5) / BodyDirections;
            var dir = new V2(SMath.Cos(th), SMath.Sin(th));
            double inside = 0, t = start;
            bool escaped = false;
            for (int d = 0; d < 80; d++)
            {
                if (Escapes(map, nucleus + t * dir, maxIter)) { escaped = true; break; }
                inside = t;
                t *= 2;
                if (t > 64 * SMath.Max(reach, 1e-300) + 4) break;
            }
            if (!escaped) continue;
            double lo = inside, hi = t;
            for (int b = 0; b < 24; b++)
            {
                double mid = 0.5 * (lo + hi);
                if (Escapes(map, nucleus + mid * dir, maxIter)) hi = mid; else lo = mid;
            }
            radii.Add(hi);
        }
        if (radii.Count == 0) return double.NaN;
        radii.Sort();
        return radii[radii.Count / 2];
    }

    private static bool Escapes(IOrbitMap map, V2 c, int maxIter)
    {
        map.Start(c, out var z, out _);
        double esc = map.EscapeRadius * map.EscapeRadius;
        for (int i = 0; i < maxIter; i++)
        {
            z = map.Next(z, c);
            if (!(z.X * z.X + z.Y * z.Y <= esc)) return true;
        }
        return false;
    }

    // ── Misiurewicz points ───────────────────────────────────────────────

    /// <summary>Snap a click to the simplest Misiurewicz point within
    /// <paramref name="acceptRadius"/>: rank every (k, p) by the length of a
    /// Newton step from the click, refine the shortest candidates (lowest
    /// k + p first), then re-classify the root to its minimal (k, p) and
    /// require a repelling cycle.</summary>
    public static GeneralMisiurewicz FindNearMisiurewicz(
        IOrbitMap map, V2 click, double acceptRadius,
        int maxOrbit = 256, int maxPeriod = 64, int maxAttempts = 48, CancellationToken ct = default)
    {
        var zs = new V2[maxOrbit + 1];
        var js = new M2[maxOrbit + 1];
        int n = OrbitAll(map, click, maxOrbit, zs, js);

        var cands = new List<(int K, int P, double Est)>();
        for (int k = 1; k < n; k++)
            for (int p = 1; p <= maxPeriod && k + p <= n; p++)
            {
                var g = zs[k + p] - zs[k];
                var jg = js[k + p] - js[k];
                if (!jg.TrySolve(g, out var d)) continue;
                double est = d.Length;
                if (est <= acceptRadius) cands.Add((k, p, est));
            }
        cands.Sort((a, b) => a.K + a.P != b.K + b.P ? (a.K + a.P).CompareTo(b.K + b.P) : a.Est.CompareTo(b.Est));

        int attempts = 0;
        long work = 0;
        var seen = new List<V2>();
        foreach (var (k, p, _) in cands)
        {
            if (attempts >= maxAttempts) break;
            ct.ThrowIfCancellationRequested();
            attempts++;
            var c = click;
            if (!Newton(map, ref c, k, p, click, acceptRadius, ref work, ct)) continue;
            if ((c - click).Length > acceptRadius) continue;
            double thr = SameRootThreshold(c, acceptRadius);
            bool dup = false;
            foreach (var s in seen) if ((s - c).Length <= thr) { dup = true; break; }
            if (dup) continue;
            seen.Add(c);

            // Minimal period (a divisor of p), then minimal preperiod.
            int pp = p;
            for (int q = 1; q < p; q++)
                if (p % q == 0 && RootDistance(map, c, k, q) <= thr) { pp = q; break; }
            int kk = k;
            for (int j = 0; j < k; j++)
                if (RootDistance(map, c, j, pp) <= thr) { kk = j; break; }
            if (kk == 0) continue;   // periodic: a nucleus, not a Misiurewicz point

            var m = CycleJacobian(map, c, kk, pp);
            double rho = m.IsFinite ? m.SpectralRadius : double.PositiveInfinity;
            if (!(rho > 1 + 1e-9) || !double.IsFinite(rho)) continue;
            double? turn = m.IsConformal(1e-4) ? SMath.Atan2(m.C, m.A) * 180 / SMath.PI : null;
            return new GeneralMisiurewicz(GeneralStatus.Found, c, kk, pp, rho, turn, attempts);
        }
        return new GeneralMisiurewicz(GeneralStatus.NotFound, click, 0, 0, double.NaN, null, attempts);
    }
}
