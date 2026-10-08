// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Explore/NucleusFinder.cs
//
// Interesting-location finder S2 (#1186, epic #1184). Design:
// Docs/Technical/Interesting-Location-Finder-DesignPlan.md §3.2–3.3.
//
// Newton's method for the nucleus (centre) of a period-p minibrot of z² + c:
// solve F(c) = fᵖ_c(0) = 0 with F′(c) = dzₚ, where dz₀ = 0, dzₙ₊₁ = 2zₙ·dzₙ + 1.
//
//     z = 0; dz = 0
//     repeat p times: dz = 2·z·dz + 1; z = z² + c
//     c ← c − z/dz
//
// Shallow views (seed disk ≥ PeriodDetector.DoublePathMinRadius) converge in
// double first and then polish in octuple-double; deep views run in OD from
// the start, because a double orbit cannot even locate a minibrot smaller than
// ~1e-16·|c|. Either way the result is carried to full OD precision, so a
// minibrot far smaller than the view still gets an exact centre.
//
// A converged root is only accepted when it passes independent invariants:
//   1. minimal period — no proper divisor d of p has fᵈ(0) ≈ 0 there (Newton
//      on fᵖ also converges to lower-period nuclei, which are roots of fᵖ too);
//   2. inside the view — the nucleus lies in the seed disk, otherwise Newton
//      wandered to a different minibrot and the caller must not navigate.

using System;
using System.Numerics;
using System.Threading;

using FracturingFog.FFMath;

using SMath = System.Math;

namespace FracturingFog.Abstractions.Explore;

/// <summary>Outcome of a nucleus search.</summary>
public enum NucleusStatus
{
    /// <summary>Converged to a period-p nucleus inside the view.</summary>
    Found,
    /// <summary>Newton did not converge within the step budget.</summary>
    NoConvergence,
    /// <summary>Newton converged (or diverged) outside the view disk.</summary>
    LeftView,
    /// <summary>Converged to a root whose true period is a proper divisor of p.</summary>
    LowerPeriod,
    /// <summary>The view is beyond octuple-double precision.</summary>
    PrecisionLimit,
}

/// <summary>Result of <see cref="NucleusFinder.Find"/>. <see cref="Size"/> is the
/// complex atom size: |Size| scales the main set onto this minibrot and
/// arg(Size) is its rotation (valid only when <see cref="Status"/> is Found).
/// <see cref="ActualPeriod"/> carries the divisor period for LowerPeriod.</summary>
public readonly record struct NucleusResult(
    NucleusStatus Status, DeepComplex Nucleus, int Period, Complex Size, int NewtonSteps, int ActualPeriod = 0)
{
    public bool Found => Status == NucleusStatus.Found;
}

/// <summary>Newton nucleus finder + atom-size estimate for f_c(z) = z² + c.
/// UI-free; see the file header for the method and acceptance checks.</summary>
public static class NucleusFinder
{
    /// <summary>Default Newton step budget (shared by both phases).</summary>
    public const int DefaultMaxSteps = 64;

    /// <summary>Smallest view radius we accept: OD carries ~124 digits, and a
    /// period scan + Newton polish needs headroom below the view scale.</summary>
    public const double MinViewRadius = 1e-110;

    /// <summary>Newton may overshoot while converging; abandon it once it is
    /// this many view radii from the seed.</summary>
    private const double WanderLimit = 8.0;

    // Relative step size at which each phase counts as converged. Double stops
    // a little above its epsilon; OD runs to its floor so a minibrot much
    // smaller than the view still gets an exact centre.
    private const double DoubleConvergedRel = 1e-14;
    private const double OdConvergedRel = 1e-110;

    /// <summary>Find the nucleus of the period-<paramref name="period"/> minibrot
    /// nearest <paramref name="seed"/>, accepting it only inside the disk
    /// |c − seed| ≤ <paramref name="viewRadius"/>.</summary>
    public static NucleusResult Find(
        DeepComplex seed, int period, double viewRadius,
        int maxSteps = DefaultMaxSteps, CancellationToken ct = default)
    {
        Validate(period, viewRadius);
        if (viewRadius < MinViewRadius)
            return new NucleusResult(NucleusStatus.PrecisionLimit, seed, period, Complex.Zero, 0);
        return NewtonFrom(seed, period, seed, viewRadius, maxSteps, ct);
    }

    /// <summary>How many periods past the ball's first candidate
    /// <see cref="FindMinibrot"/> scans before giving up.</summary>
    public const int DefaultPeriodScan = 4096;

    /// <summary>Total perturbed-orbit iterations <see cref="FindMinibrot"/> may
    /// spend (~5 s). A view whose candidate periods run to the tens of
    /// thousands would otherwise scan for minutes.</summary>
    public const long DefaultMaxWork = 50_000_000;

    /// <summary>Find a low-period minibrot whose nucleus lies in the view disk
    /// (<paramref name="centre"/>, <paramref name="viewRadius"/>).</summary>
    /// <remarks>
    /// The S1 ball scan only gives a LOWER BOUND p₀: on a whole view it
    /// over-approximates, and after its first hit the ball saturates, so later
    /// "candidates" carry no information. The real lowest period in view is
    /// often several times p₀ (measured on the built-in regions: 116 → 261,
    /// 53 → 466). So periods are scanned upward from p₀, each with Newton from
    /// the view centre, and the first root that lands inside the view wins.
    ///
    /// The scan runs as PERTURBED Newton in double: one reference orbit Zₙ at
    /// the centre (computed in OD, stored as double), and for c = centre + δ the
    /// exact deviation εₙ₊₁ = 2Zₙεₙ + εₙ² + δ, so each attempt costs O(p) double
    /// operations at any zoom depth. A hit is then polished and verified in OD
    /// by the same checks <see cref="Find"/> applies (minimal period, inside
    /// the view), so the double scan can only propose, never accept.
    /// </remarks>
    public static NucleusResult FindMinibrot(
        DeepComplex centre, double viewRadius,
        int maxPeriod = PeriodDetector.DefaultMaxPeriod, int periodScan = DefaultPeriodScan,
        int maxSteps = DefaultMaxSteps, long maxWork = DefaultMaxWork,
        Action<int>? onPeriod = null, CancellationToken ct = default)
    {
        if (!(viewRadius > 0) || double.IsInfinity(viewRadius))
            throw new ArgumentOutOfRangeException(nameof(viewRadius), viewRadius, "View radius must be positive and finite.");
        if (viewRadius < MinViewRadius)
            return new NucleusResult(NucleusStatus.PrecisionLimit, centre, 0, Complex.Zero, 0);

        var first = PeriodDetector.Detect(centre, viewRadius, maxPeriod, ct: ct);
        if (!first.Found)
            return new NucleusResult(NucleusStatus.NoConvergence, centre, 0, Complex.Zero, 0);

        int pEnd = (int)SMath.Min(maxPeriod, (long)first.Period + periodScan);
        Complex[] z = ReferenceOrbit(centre, pEnd, ct);   // z[n] = Zₙ, n = 0..len-1
        pEnd = SMath.Min(pEnd, z.Length - 1);              // the reference escaped early

        int totalSteps = 0;
        long work = 0;
        var last = new NucleusResult(NucleusStatus.NoConvergence, centre, first.Period, Complex.Zero, 0);
        for (int p = first.Period; p <= pEnd && work < maxWork; p++)
        {
            ct.ThrowIfCancellationRequested();
            onPeriod?.Invoke(p);
            int before = totalSteps;
            bool proposed = PerturbedNewton(z, p, viewRadius, maxSteps, out Complex delta, ref totalSteps);
            work += (long)(totalSteps - before) * p;
            if (!proposed) continue;

            // Polish + verify in OD, starting from the proposed root.
            var start = centre.Translate(delta.Real, delta.Imaginary);
            var r = NewtonFrom(start, p, centre, viewRadius, maxSteps, ct);
            totalSteps += r.NewtonSteps;
            if (r.Found) return r with { NewtonSteps = totalSteps };
            if (r.Status == NucleusStatus.LowerPeriod)
            {
                // A lower-period nucleus inside the view is a real minibrot too.
                var lower = NewtonFrom(r.Nucleus, r.ActualPeriod, centre, viewRadius, maxSteps, ct);
                totalSteps += lower.NewtonSteps;
                if (lower.Found) return lower with { NewtonSteps = totalSteps };
            }
            last = r;
        }
        return last with { NewtonSteps = totalSteps };
    }

    // Zₙ at c for n = 0..maxN, in OD (cancellation-safe), stored as double.
    // Stops early once the orbit escapes: no nucleus beyond that is reachable
    // by perturbing around this reference.
    private static Complex[] ReferenceOrbit(DeepComplex c, int maxN, CancellationToken ct)
    {
        var z = new System.Collections.Generic.List<Complex>(SMath.Min(maxN + 1, 1 << 16)) { Complex.Zero };
        OD zx = OD.Zero, zy = OD.Zero;
        for (int n = 1; n <= maxN; n++)
        {
            if ((n & 0xFF) == 0) ct.ThrowIfCancellationRequested();
            (zx, zy) = OdExact.SquareAdd(zx, zy, c.Re, c.Im);
            var v = new Complex(zx.X0, zy.X0);
            if (v.Magnitude > ReferenceEscape) break;
            z.Add(v);
        }
        return z.ToArray();
    }

    private const double ReferenceEscape = 1e6;

    // Perturbed Newton for fᵖ(centre + δ) = 0, from δ = 0. Proposes δ inside
    // the view; acceptance is left to the OD polish.
    private static bool PerturbedNewton(Complex[] z, int p, double viewRadius, int maxSteps,
                                        out Complex delta, ref int steps)
    {
        delta = Complex.Zero;
        double prev = double.PositiveInfinity;
        for (int s = 0; s < maxSteps; s++)
        {
            steps++;
            Complex eps = Complex.Zero, dz = Complex.Zero;
            for (int n = 0; n < p; n++)
            {
                Complex zn = z[n] + eps;                 // full orbit value at c
                dz = 2.0 * zn * dz + Complex.One;
                eps = (2.0 * z[n] + eps) * eps + delta;  // εₙ₊₁ = 2Zₙεₙ + εₙ² + δ
            }
            Complex fp = z[p] + eps;
            if (!IsFinite(fp) || !IsFinite(dz) || dz == Complex.Zero) return false;
            Complex step = fp / dz;
            delta -= step;
            double mag = step.Magnitude;
            if (!(delta.Magnitude <= WanderLimit * viewRadius)) return false;
            // Converged to double precision near δ (or stalled at its floor).
            double scale = SMath.Max(delta.Magnitude, 1e-3 * viewRadius);
            if (mag <= 1e-13 * scale || (s >= 3 && mag >= 0.5 * prev && mag <= 1e-9 * scale))
                return delta.Magnitude <= viewRadius;
            prev = mag;
        }
        return false;
    }

    private static void Validate(int period, double viewRadius)
    {
        if (period < 1)
            throw new ArgumentOutOfRangeException(nameof(period), period, "Period must be at least 1.");
        if (!(viewRadius > 0) || double.IsInfinity(viewRadius))
            throw new ArgumentOutOfRangeException(nameof(viewRadius), viewRadius, "View radius must be positive and finite.");
    }

    // Newton from `start`, accepting only a root inside the view disk
    // (viewCentre, viewRadius); abandoned once it strays WanderLimit view radii
    // from `start`. The double/OD split follows the view scale.
    private static NucleusResult NewtonFrom(
        DeepComplex start, int period, DeepComplex viewCentre, double viewRadius, int maxSteps, CancellationToken ct)
    {
        var seed = viewCentre;
        int steps = 0;
        OD cx = start.Re, cy = start.Im;

        if (viewRadius >= PeriodDetector.DoublePathMinRadius)
        {
            double sx = (double)start.Re, sy = (double)start.Im;
            double x = sx, y = sy;
            bool converged = false;
            while (steps < maxSteps)
            {
                ct.ThrowIfCancellationRequested();
                steps++;
                var (dx, dy) = NewtonStepDouble(x, y, period);
                if (double.IsNaN(dx) || double.IsNaN(dy) || double.IsInfinity(dx) || double.IsInfinity(dy))
                    return new NucleusResult(NucleusStatus.NoConvergence, seed, period, Complex.Zero, steps);
                x -= dx; y -= dy;
                if (Hypot(x - sx, y - sy) > WanderLimit * viewRadius)
                    return new NucleusResult(NucleusStatus.LeftView, new DeepComplex(x, y), period, Complex.Zero, steps);
                if (Hypot(dx, dy) <= DoubleConvergedRel * SMath.Max(Hypot(x, y), viewRadius)) { converged = true; break; }
            }
            if (!converged)
                return new NucleusResult(NucleusStatus.NoConvergence, new DeepComplex(x, y), period, Complex.Zero, steps);
            cx = x; cy = y;
        }

        // OD phase: the whole search for deep views, a short polish otherwise.
        double lastStep = double.PositiveInfinity;
        bool odConverged = false;
        int odBudget = maxSteps;
        for (int i = 0; i < odBudget; i++)
        {
            ct.ThrowIfCancellationRequested();
            steps++;
            var (dx, dy) = NewtonStepOd(cx, cy, period, ct);
            double mag = Hypot(dx.X0, dy.X0);
            if (double.IsNaN(mag) || double.IsInfinity(mag))
                return new NucleusResult(NucleusStatus.NoConvergence, new DeepComplex(cx, cy), period, Complex.Zero, steps);
            cx = OdExact.Sub(cx, dx); cy = OdExact.Sub(cy, dy);

            double fromStart = Hypot(OdExact.Sub(cx, start.Re).X0, OdExact.Sub(cy, start.Im).X0);
            if (fromStart > WanderLimit * viewRadius)
                return new NucleusResult(NucleusStatus.LeftView, new DeepComplex(cx, cy), period, Complex.Zero, steps);

            double cMag = SMath.Max(Hypot(cx.X0, cy.X0), viewRadius);
            // Converged at the OD floor, or stalled there (rounding noise stops
            // the step shrinking once it is a few ulps of OD).
            if (mag <= OdConvergedRel * cMag || (mag >= lastStep && mag <= 1e-100 * cMag))
            {
                lastStep = SMath.Min(lastStep, mag);
                odConverged = true;
                break;
            }
            lastStep = mag;
        }
        var nucleus = new DeepComplex(cx, cy);
        if (!odConverged)
            return new NucleusResult(NucleusStatus.NoConvergence, nucleus, period, Complex.Zero, steps);

        // Invariant 2: inside the view.
        double offset = Hypot(OdExact.Sub(cx, seed.Re).X0, OdExact.Sub(cy, seed.Im).X0);
        if (offset > viewRadius)
            return new NucleusResult(NucleusStatus.LeftView, nucleus, period, Complex.Zero, steps);

        // Invariant 1: minimal period.
        int lower = LowerPeriodAt(nucleus, period, lastStep, ct);
        if (lower > 0)
            return new NucleusResult(NucleusStatus.LowerPeriod, nucleus, period, Complex.Zero, steps, lower);

        return new NucleusResult(NucleusStatus.Found, nucleus, period, AtomSize(nucleus, period), steps, period);
    }

    /// <summary>Complex atom size of the period-<paramref name="period"/>
    /// minibrot at <paramref name="nucleus"/>: c ≈ nucleus + Size·c′ maps the
    /// main set (c′) onto the minibrot, so |Size| is its scale relative to the
    /// whole set and arg(Size) its rotation.</summary>
    /// <remarks>z = 0, l = 1, b = 1; for q = 1..p−1 { z = z² + c; l = 2z·l; b += 1/l };
    /// Size = 1 / (b·l²). The orbit runs in OD (it must stay on the nucleus's
    /// superattracting cycle); l and b only need double.</remarks>
    public static Complex AtomSize(DeepComplex nucleus, int period)
    {
        if (period < 1) throw new ArgumentOutOfRangeException(nameof(period));
        OD zx = OD.Zero, zy = OD.Zero;
        Complex l = Complex.One, b = Complex.One;
        for (int q = 1; q < period; q++)
        {
            (zx, zy) = OdExact.SquareAdd(zx, zy, nucleus.Re, nucleus.Im);
            l = 2.0 * new Complex(zx.X0, zy.X0) * l;
            b += Complex.Reciprocal(l);
        }
        return Complex.Reciprocal(b * l * l);
    }

    /// <summary>Mandelbrot home view (FractalViewState.SnapToFractalDefault):
    /// centre −0.5, zoom 1. A minibrot is framed the way home frames the whole
    /// set, through the atom-size map c = nucleus + Size·c′.</summary>
    public const double HomeCenterX = -0.5;

    /// <summary>View centre + zoom that show the minibrot of
    /// <paramref name="found"/> the way the home view shows the whole set,
    /// scaled by <paramref name="framing"/> (&gt; 1 = tighter).</summary>
    public static (DeepComplex Center, double Zoom) Frame(in NucleusResult found, double framing = 1.0)
    {
        if (!found.Found) throw new ArgumentException("Only a found nucleus can be framed.", nameof(found));
        if (!(framing > 0)) throw new ArgumentOutOfRangeException(nameof(framing));
        Complex offset = found.Size * HomeCenterX;
        return (found.Nucleus.Translate(offset.Real, offset.Imaginary), framing / found.Size.Magnitude);
    }

    /// <summary>Iteration count that resolves a period-<paramref name="period"/>
    /// minibrot framed at its own size: points just outside it shadow the
    /// cycle for many laps before escaping. Measured live: 10 laps leaves the
    /// minibrot a black smear, 100 renders it clean.</summary>
    public const int DefaultIterationsPerPeriod = 100;

    public static int SuggestedIterations(int period, int perPeriod = DefaultIterationsPerPeriod)
        => (int)SMath.Min(int.MaxValue, (long)period * perPeriod);

    // One Newton step in double: returns Δc = fᵖ(0) / (dfᵖ/dc). Complex
    // division (Smith's algorithm) avoids squaring a large derivative.
    private static (double dx, double dy) NewtonStepDouble(double cx, double cy, int period)
    {
        var c = new Complex(cx, cy);
        Complex z = Complex.Zero, dz = Complex.Zero;
        for (int n = 0; n < period; n++)
        {
            dz = 2.0 * z * dz + Complex.One;   // uses z before it is advanced
            z = z * z + c;
        }
        if (!IsFinite(z) || !IsFinite(dz) || dz == Complex.Zero)
            return (double.NaN, double.NaN);
        Complex step = z / dz;
        return (step.Real, step.Imaginary);
    }

    private static bool IsFinite(Complex v) => double.IsFinite(v.Real) && double.IsFinite(v.Imaginary);

    // One Newton step in OD. The derivative only scales the step, so it runs in
    // double magnitude-wise; z must be OD to resolve a deep nucleus.
    private static (OD dx, OD dy) NewtonStepOd(OD cx, OD cy, int period, CancellationToken ct)
    {
        OD zx = OD.Zero, zy = OD.Zero;
        Complex dz = Complex.Zero;
        for (int n = 0; n < period; n++)
        {
            if ((n & 0xFF) == 0xFF) ct.ThrowIfCancellationRequested();
            dz = 2.0 * new Complex(zx.X0, zy.X0) * dz + Complex.One;
            (zx, zy) = OdExact.SquareAdd(zx, zy, cx, cy);
        }
        // An overflowed derivative would make the step 0 and fake convergence.
        if (!IsFinite(dz) || dz == Complex.Zero || !double.IsFinite(zx.X0) || !double.IsFinite(zy.X0))
            return (new OD(double.NaN), new OD(double.NaN));

        // Δc = z / dz, with z kept in OD (it is tiny near the root but exact).
        Complex inv = Complex.Reciprocal(dz);
        OD dx = zx * inv.Real - zy * inv.Imaginary;
        OD dy = zx * inv.Imaginary + zy * inv.Real;
        return (dx, dy);
    }

    // Smallest proper divisor d of p at which fᵈ(0) is already a root at this
    // c — i.e. the Newton step toward a period-d nucleus is no bigger than our
    // own convergence error. 0 when the period is minimal.
    private static int LowerPeriodAt(DeepComplex c, int period, double lastStep, CancellationToken ct)
    {
        if (period == 1) return 0;
        double cMag = SMath.Max(Hypot(c.Re.X0, c.Im.X0), 1e-300);
        double tol = SMath.Max(lastStep * 1e3, cMag * 1e-100);
        OD zx = OD.Zero, zy = OD.Zero;
        Complex dz = Complex.Zero;
        for (int n = 1; n < period; n++)
        {
            if ((n & 0xFF) == 0) ct.ThrowIfCancellationRequested();
            dz = 2.0 * new Complex(zx.X0, zy.X0) * dz + Complex.One;
            (zx, zy) = OdExact.SquareAdd(zx, zy, c.Re, c.Im);
            if (period % n != 0) continue;
            double step = Hypot(zx.X0, zy.X0) / dz.Magnitude;
            if (step <= tol) return n;
        }
        return 0;
    }

    private static double Hypot(double a, double b) => SMath.Sqrt(a * a + b * b);
}
