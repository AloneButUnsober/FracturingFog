// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Explore/DeepNucleusFinder.cs
//
// #1866 (epic #1428, C7a) — PeriodDetector + NucleusFinder past octuple-double: the same
// methods (design: Docs/Technical/Interesting-Location-Finder-DesignPlan.md §3.1–3.3; the
// ball method, Newton for nuclei and the atom-domain size are credited in
// Docs/Resources-Bibliography.md: Munafo's Mu-Ency, Claude Heiland-Allen), carried in
// BigFloat and floatexp so they work at any depth:
//
//   • Period detection (ball arithmetic): the centre's orbit in BigFloat at the view's
//     precision (BigFloat.PrecisionForZoom), the ball radius in FloatExp (the radius of a
//     view past 1e308 is not a double).
//   • Minibrot search: periods scanned upward from the ball's candidate. Each attempt is
//     Newton PERTURBED around one reference orbit Zₙ at the exact centre (BigFloat,
//     stored as double: Z is O(1)), with the deviation ε, the offset δ and the derivative
//     in CFx (complex floatexp): εₙ₊₁ = 2Zₙεₙ + εₙ² + δ. A proposal is then polished by
//     Newton in BigFloat, the precision raised to the minibrot's own size (its framing
//     zooms to 1/|Size|, often far past the view), and accepted only by the same
//     invariants as NucleusFinder: inside the view disk, and the minimal period.
//   • Atom size: NucleusFinder.AtomSize's recurrence with the orbit in BigFloat and l, b in
//     CFx (l = ∏2z overflows double long before 1e300).
//   • Framing: c = nucleus + Size·c′ at the home framing, as an exact centre + log zoom.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

using FracturingFog.FFMath;

using SMath = System.Math;

namespace FracturingFog.Abstractions.Explore;

/// <summary>#1866 — <see cref="NucleusResult"/> past OD: the nucleus as an exact centre and
/// the complex atom size in floatexp (valid when <see cref="Status"/> is Found).</summary>
public readonly record struct DeepNucleusResult(
    NucleusStatus Status, ExactCentre Nucleus, int Period, CFx Size, int NewtonSteps, int ActualPeriod = 0)
{
    public bool Found => Status == NucleusStatus.Found;

    /// <summary>log10 |Size| (the minibrot's scale relative to the whole set).</summary>
    public double SizeLog10 => Size.Log2Abs() / Log2Of10;

    internal const double Log2Of10 = 3.3219280948873622;
}

/// <summary>#1866 — period detection and the nucleus finder in BigFloat / floatexp, for
/// views past octuple-double. See the file header.</summary>
public static class DeepNucleusFinder
{
    private const double Log2Of10 = DeepNucleusResult.Log2Of10;
    private const double WanderLimitLog2 = 3.0;   // 8 view radii, as NucleusFinder
    private const double ReferenceEscape = 1e6;

    /// <summary>Views from this deep (log10 zoom) use this finder: past the Extreme tier,
    /// where OD has ~1e30 of headroom left (NucleusFinder.MinViewRadius is 1e-110).</summary>
    public const double ZoomLog10Threshold = 100;

    /// <summary>BigFloat bits for a view of radius 10^<paramref name="radiusLog10"/>: the
    /// zoom's precision plus 4 decades of headroom.</summary>
    public static int PrecisionFor(double radiusLog10) => BigFloat.PrecisionForZoom(-radiusLog10 + 4);

    /// <summary>log10 of <see cref="PeriodDetector.ViewDiskRadius"/> at any depth.</summary>
    public static double ViewDiskRadiusLog10(double zoomLog10, int viewW, int viewH, double planeExtent = 3.5)
        => SMath.Log10(PeriodDetector.ViewDiskRadius(1.0, viewW, viewH, planeExtent)) - zoomLog10;

    // ── Period detection ─────────────────────────────────────────────────────

    /// <summary>As <see cref="PeriodDetector.Detect"/>, on the disk |c − centre| ≤
    /// 10^<paramref name="radiusLog10"/>.</summary>
    public static PeriodDetection Detect(
        ExactCentre centre, double radiusLog10,
        int maxPeriod = PeriodDetector.DefaultMaxPeriod,
        double escapeRadius = PeriodDetector.DefaultEscapeRadius,
        CancellationToken ct = default)
    {
        if (!double.IsFinite(radiusLog10))
            throw new ArgumentOutOfRangeException(nameof(radiusLog10), radiusLog10, "Disk radius must be finite.");
        if (maxPeriod < 1)
            throw new ArgumentOutOfRangeException(nameof(maxPeriod), maxPeriod, "maxPeriod must be at least 1.");
        int prec = PrecisionFor(radiusLog10);
        var c = centre.WithPrecision(prec);
        var rho = FloatExp.FromLog10(radiusLog10);
        var esc = FloatExp.FromDouble(escapeRadius);
        BigFloat zx = BigFloat.Zero(prec), zy = BigFloat.Zero(prec);
        var r = FloatExp.Zero;
        var az = FloatExp.Zero;
        for (int n = 1; n <= maxPeriod; n++)
        {
            if ((n & 0xFF) == 0) ct.ThrowIfCancellationRequested();
            r = az.ScaleB(1) * r + r * r + rho;          // r ← 2|z|·r + r² + ρ
            (zx, zy) = SquareAdd(zx, zy, c.Re, c.Im, prec);
            az = Mag(zx, zy);
            if (!double.IsFinite(r.M)) return new PeriodDetection(0, n, PeriodStop.BallTooLarge);
            if (FloatExp.AbsLess(az, r)) return new PeriodDetection(n, n, PeriodStop.Found);
            var gap = az - r;
            if (gap.M > 0 && FloatExp.AbsLess(esc, gap)) return new PeriodDetection(0, n, PeriodStop.Escaped);
            if (FloatExp.AbsLess(esc, r)) return new PeriodDetection(0, n, PeriodStop.BallTooLarge);
        }
        return new PeriodDetection(0, maxPeriod, PeriodStop.MaxPeriod);
    }

    // ── Minibrot search ──────────────────────────────────────────────────────

    /// <summary>As <see cref="NucleusFinder.FindMinibrot"/>, on the view disk
    /// (<paramref name="centre"/>, 10^<paramref name="radiusLog10"/>).</summary>
    public static DeepNucleusResult FindMinibrot(
        ExactCentre centre, double radiusLog10,
        int maxPeriod = PeriodDetector.DefaultMaxPeriod, int periodScan = NucleusFinder.DefaultPeriodScan,
        int maxSteps = NucleusFinder.DefaultMaxSteps, long maxWork = NucleusFinder.DefaultMaxWork,
        Action<int>? onPeriod = null, CancellationToken ct = default)
    {
        var first = Detect(centre, radiusLog10, maxPeriod, ct: ct);
        if (!first.Found)
            return new DeepNucleusResult(NucleusStatus.NoConvergence, centre, 0, CFx.Zero, 0);

        int prec = PrecisionFor(radiusLog10);
        int pEnd = (int)SMath.Min(maxPeriod, (long)first.Period + periodScan);
        Complex[] z = ReferenceOrbit(centre.WithPrecision(prec), pEnd, prec, ct);
        pEnd = SMath.Min(pEnd, z.Length - 1);
        double radiusLog2 = radiusLog10 * Log2Of10;

        int totalSteps = 0;
        long work = 0;
        var last = new DeepNucleusResult(NucleusStatus.NoConvergence, centre, first.Period, CFx.Zero, 0);
        for (int p = first.Period; p <= pEnd && work < maxWork; p++)
        {
            ct.ThrowIfCancellationRequested();
            onPeriod?.Invoke(p);
            int before = totalSteps;
            bool proposed = PerturbedNewton(z, p, radiusLog2, maxSteps, out CFx delta, ref totalSteps);
            work += (long)(totalSteps - before) * p;
            if (!proposed) continue;

            var start = new ExactCentre(
                BigFloat.Add(centre.Re.WithPrecision(prec), BigFloat.FromFloatExp(delta.Re, prec), prec),
                BigFloat.Add(centre.Im.WithPrecision(prec), BigFloat.FromFloatExp(delta.Im, prec), prec));
            var r = NewtonFrom(start, p, centre, radiusLog10, maxSteps, ct);
            totalSteps += r.NewtonSteps;
            if (r.Found) return r with { NewtonSteps = totalSteps };
            if (r.Status == NucleusStatus.LowerPeriod)
            {
                var lower = NewtonFrom(r.Nucleus, r.ActualPeriod, centre, radiusLog10, maxSteps, ct);
                totalSteps += lower.NewtonSteps;
                if (lower.Found) return lower with { NewtonSteps = totalSteps };
            }
            last = r;
        }
        return last with { NewtonSteps = totalSteps };
    }

    /// <summary>As <see cref="NucleusFinder.Find"/>: Newton for the period-<paramref name="period"/>
    /// nucleus from <paramref name="seed"/>, accepted only inside the disk of radius
    /// 10^<paramref name="radiusLog10"/> around it.</summary>
    public static DeepNucleusResult Find(
        ExactCentre seed, int period, double radiusLog10, int maxSteps = NucleusFinder.DefaultMaxSteps, CancellationToken ct = default)
    {
        if (period < 1) throw new ArgumentOutOfRangeException(nameof(period), period, "Period must be at least 1.");
        return NewtonFrom(seed, period, seed, radiusLog10, maxSteps, ct);
    }

    /// <summary>#1866 — carry on a minibrot <see cref="NucleusFinder"/> found in OD whose
    /// framing lies past what OD can frame: its size can be far below the grain OD places
    /// the nucleus to (~|c|·1e-124), so a disk sized from the minibrot would not even hold
    /// the true nucleus. Newton in BigFloat from its OD nucleus, accepted on the same view disk
    /// (<paramref name="viewCentre"/>, <paramref name="viewRadius"/>) the OD search ran on,
    /// with the precision raised to the minibrot's own size.</summary>
    public static DeepNucleusResult CarryOn(
        in NucleusResult found, DeepComplex viewCentre, double viewRadius,
        int maxSteps = NucleusFinder.DefaultMaxSteps, CancellationToken ct = default)
    {
        if (found.Period < 1) throw new ArgumentOutOfRangeException(nameof(found), "A nucleus with a period is needed.");
        if (!(viewRadius > 0) || double.IsInfinity(viewRadius))
            throw new ArgumentOutOfRangeException(nameof(viewRadius), viewRadius, "View radius must be positive and finite.");
        double radiusLog10 = SMath.Log10(viewRadius);
        int prec = PrecisionFor(radiusLog10);
        return NewtonFrom(ExactCentre.FromDeep(found.Nucleus, prec), found.Period,
            ExactCentre.FromDeep(viewCentre, prec), radiusLog10, maxSteps, ct);
    }

    // Zₙ at c for n = 0..maxN in BigFloat, stored as double; stops once it escapes.
    private static Complex[] ReferenceOrbit(ExactCentre c, int maxN, int prec, CancellationToken ct)
    {
        var z = new List<Complex>(SMath.Min(maxN + 1, 1 << 16)) { Complex.Zero };
        BigFloat zx = BigFloat.Zero(prec), zy = BigFloat.Zero(prec);
        for (int n = 1; n <= maxN; n++)
        {
            if ((n & 0xFF) == 0) ct.ThrowIfCancellationRequested();
            (zx, zy) = SquareAdd(zx, zy, c.Re, c.Im, prec);
            var v = new Complex(zx.ToDouble(), zy.ToDouble());
            if (!(v.Magnitude <= ReferenceEscape)) break;
            z.Add(v);
        }
        return z.ToArray();
    }

    // Perturbed Newton for fᵖ(centre + δ) = 0 from δ = 0, in CFx. Proposes δ inside the
    // view; the BigFloat polish accepts or refutes.
    private static bool PerturbedNewton(Complex[] z, int p, double radiusLog2, int maxSteps, out CFx delta, ref int steps)
    {
        delta = CFx.Zero;
        double prev = double.PositiveInfinity;
        for (int s = 0; s < maxSteps; s++)
        {
            steps++;
            CFx eps = CFx.Zero, dz = CFx.Zero;
            for (int n = 0; n < p; n++)
            {
                var zr = CFx.Of(z[n].Real, z[n].Imaginary);
                CFx zn = zr + eps;                               // full orbit value at c
                dz = (zn * dz).Twice().PlusOne();
                eps = (zr.Twice() + eps) * eps + delta;          // εₙ₊₁ = 2Zₙεₙ + εₙ² + δ
            }
            CFx fp = CFx.Of(z[p].Real, z[p].Imaginary) + eps;
            if (!fp.IsFinite || !dz.IsFinite || dz.IsZero) return false;
            CFx step = fp / dz;
            delta -= step;
            if (!delta.IsFinite) return false;
            double ms = step.Log2Abs(), md = delta.Log2Abs();
            if (md > radiusLog2 + WanderLimitLog2) return false;
            double scale = SMath.Max(md, radiusLog2 - 9.97);   // max(|δ|, 1e-3·ρ)
            if (ms <= scale - 43.2 || (s >= 3 && ms >= prev - 1 && ms <= scale - 29.9))   // 1e-13 / 1e-9
                return md <= radiusLog2;
            prev = ms;
        }
        return false;
    }

    // Newton in BigFloat from `start`, accepted only inside (viewCentre, 10^radiusLog10)
    // and at its minimal period. The precision starts at the view's and is raised to the
    // found minibrot's size, then Newton runs on to that floor.
    private static DeepNucleusResult NewtonFrom(
        ExactCentre start, int period, ExactCentre viewCentre, double radiusLog10, int maxSteps, CancellationToken ct)
    {
        double radiusLog2 = radiusLog10 * Log2Of10;
        int prec = PrecisionFor(radiusLog10);
        BigFloat cx = start.Re.WithPrecision(prec), cy = start.Im.WithPrecision(prec);
        int steps = 0;
        double lastLog2 = double.PositiveInfinity;

        for (int round = 0; round < 4; round++)
        {
            bool converged = false;
            lastLog2 = double.PositiveInfinity;
            for (int i = 0; i < maxSteps; i++)
            {
                ct.ThrowIfCancellationRequested();
                steps++;
                var (zx, zy, dz) = Orbit(cx, cy, period, prec, ct);
                if (!dz.IsFinite || dz.IsZero)
                    return new DeepNucleusResult(NucleusStatus.NoConvergence, new ExactCentre(cx, cy), period, CFx.Zero, steps);
                CFx step = new CFx(zx.ToFloatExp(), zy.ToFloatExp()) / dz;
                if (!step.IsFinite)
                    return new DeepNucleusResult(NucleusStatus.NoConvergence, new ExactCentre(cx, cy), period, CFx.Zero, steps);
                cx = BigFloat.Sub(cx, BigFloat.FromFloatExp(step.Re, prec), prec);
                cy = BigFloat.Sub(cy, BigFloat.FromFloatExp(step.Im, prec), prec);

                if (DistLog2(cx, cy, start.Re, start.Im, prec) > radiusLog2 + WanderLimitLog2)
                    return new DeepNucleusResult(NucleusStatus.LeftView, new ExactCentre(cx, cy), period, CFx.Zero, steps);

                double ms = step.Log2Abs();
                double cLog2 = SMath.Max(new CFx(cx.ToFloatExp(), cy.ToFloatExp()).Log2Abs(), radiusLog2);
                // Converged at the precision's floor, or stalled there.
                if (ms <= cLog2 - prec + 24 || (ms >= lastLog2 && ms <= cLog2 - prec + 64))
                {
                    lastLog2 = SMath.Min(lastLog2, ms);
                    converged = true;
                    break;
                }
                lastLog2 = ms;
            }
            if (!converged)
                return new DeepNucleusResult(NucleusStatus.NoConvergence, new ExactCentre(cx, cy), period, CFx.Zero, steps);

            // The minibrot is framed at zoom 1/|Size|: carry the nucleus to that depth.
            var size = AtomSize(new ExactCentre(cx, cy), period, prec, ct);
            double sizeLog10 = size.Log2Abs() / Log2Of10;
            int need = BigFloat.PrecisionForZoom(SMath.Max(-sizeLog10, -radiusLog10) + 6);
            if (need <= prec || !double.IsFinite(sizeLog10)) break;
            prec = need;
            cx = cx.WithPrecision(prec); cy = cy.WithPrecision(prec);
        }

        var nucleus = new ExactCentre(cx, cy);
        // Invariant 2: inside the view.
        if (DistLog2(cx, cy, viewCentre.Re, viewCentre.Im, prec) > radiusLog2)
            return new DeepNucleusResult(NucleusStatus.LeftView, nucleus, period, CFx.Zero, steps);
        // Invariant 1: minimal period.
        int lower = LowerPeriodAt(cx, cy, period, prec, lastLog2, ct);
        if (lower > 0)
            return new DeepNucleusResult(NucleusStatus.LowerPeriod, nucleus, period, CFx.Zero, steps, lower);
        return new DeepNucleusResult(NucleusStatus.Found, nucleus, period, AtomSize(nucleus, period, prec, ct), steps, period);
    }

    // fᵖ(0) at c in BigFloat and its c-derivative in CFx (dz₀ = 0, dzₙ₊₁ = 2zₙdzₙ + 1).
    private static (BigFloat zx, BigFloat zy, CFx dz) Orbit(BigFloat cx, BigFloat cy, int period, int prec, CancellationToken ct)
    {
        BigFloat zx = BigFloat.Zero(prec), zy = BigFloat.Zero(prec);
        CFx dz = CFx.Zero;
        for (int n = 0; n < period; n++)
        {
            if ((n & 0xFF) == 0xFF) ct.ThrowIfCancellationRequested();
            dz = (new CFx(zx.ToFloatExp(), zy.ToFloatExp()) * dz).Twice().PlusOne();
            (zx, zy) = SquareAdd(zx, zy, cx, cy, prec);
        }
        return (zx, zy, dz);
    }

    // The smallest proper divisor d of p at which fᵈ(0) is already a root here: the Newton
    // step toward a period-d nucleus is no bigger than our own convergence error.
    private static int LowerPeriodAt(BigFloat cx, BigFloat cy, int period, int prec, double lastLog2, CancellationToken ct)
    {
        if (period == 1) return 0;
        double cLog2 = new CFx(cx.ToFloatExp(), cy.ToFloatExp()).Log2Abs();
        double tol = SMath.Max(lastLog2 + 10, cLog2 - prec + 64);   // max(lastStep·1e3, floor)
        BigFloat zx = BigFloat.Zero(prec), zy = BigFloat.Zero(prec);
        CFx dz = CFx.Zero;
        for (int n = 1; n < period; n++)
        {
            if ((n & 0xFF) == 0) ct.ThrowIfCancellationRequested();
            dz = (new CFx(zx.ToFloatExp(), zy.ToFloatExp()) * dz).Twice().PlusOne();
            (zx, zy) = SquareAdd(zx, zy, cx, cy, prec);
            if (period % n != 0) continue;
            double step = new CFx(zx.ToFloatExp(), zy.ToFloatExp()).Log2Abs() - dz.Log2Abs();
            if (step <= tol) return n;
        }
        return 0;
    }

    // ── Atom size and framing ────────────────────────────────────────────────

    /// <summary>As <see cref="NucleusFinder.AtomSize"/>: z = 0, l = 1, b = 1; for q = 1..p−1
    /// { z = z² + c; l = 2z·l; b += 1/l }; Size = 1 / (b·l²), the orbit in BigFloat at
    /// the nucleus's precision, l and b in CFx.</summary>
    public static CFx AtomSize(ExactCentre nucleus, int period, CancellationToken ct = default)
        => AtomSize(nucleus, period, nucleus.Precision, ct);

    private static CFx AtomSize(ExactCentre nucleus, int period, int prec, CancellationToken ct)
    {
        if (period < 1) throw new ArgumentOutOfRangeException(nameof(period));
        BigFloat zx = BigFloat.Zero(prec), zy = BigFloat.Zero(prec);
        var cx = nucleus.Re.WithPrecision(prec); var cy = nucleus.Im.WithPrecision(prec);
        CFx l = CFx.One, b = CFx.One;
        for (int q = 1; q < period; q++)
        {
            if ((q & 0xFF) == 0) ct.ThrowIfCancellationRequested();
            (zx, zy) = SquareAdd(zx, zy, cx, cy, prec);
            l = (new CFx(zx.ToFloatExp(), zy.ToFloatExp()) * l).Twice();
            b = b + l.Reciprocal();
        }
        return (b * l * l).Reciprocal();
    }

    /// <summary>As <see cref="NucleusFinder.Frame"/>: the exact centre and log10 zoom that
    /// show the minibrot the way the home view shows the whole set, scaled by
    /// <paramref name="framing"/> (&gt; 1 = tighter).</summary>
    public static (ExactCentre Centre, double ZoomLog10) Frame(in DeepNucleusResult found, double framing = 1.0)
    {
        if (!found.Found) throw new ArgumentException("Only a found nucleus can be framed.", nameof(found));
        if (!(framing > 0)) throw new ArgumentOutOfRangeException(nameof(framing));
        int prec = found.Nucleus.Precision;
        var offset = found.Size.Scale(NucleusFinder.HomeCenterX);
        var centre = new ExactCentre(
            BigFloat.Add(found.Nucleus.Re, BigFloat.FromFloatExp(offset.Re, prec), prec),
            BigFloat.Add(found.Nucleus.Im, BigFloat.FromFloatExp(offset.Im, prec), prec));
        return (centre, SMath.Log10(framing) - found.SizeLog10);
    }

    // ── BigFloat helpers ─────────────────────────────────────────────────────

    private static (BigFloat, BigFloat) SquareAdd(BigFloat zx, BigFloat zy, BigFloat cx, BigFloat cy, int prec)
    {
        var x2 = BigFloat.Mul(zx, zx, prec);
        var y2 = BigFloat.Mul(zy, zy, prec);
        var xy = BigFloat.Mul(zx, zy, prec);
        return (BigFloat.Add(BigFloat.Sub(x2, y2, prec), cx, prec), BigFloat.Add(xy.ScaleB(1), cy, prec));
    }

    private static FloatExp Mag(BigFloat x, BigFloat y) => new CFx(x.ToFloatExp(), y.ToFloatExp()).Magnitude();

    private static double DistLog2(BigFloat ax, BigFloat ay, BigFloat bx, BigFloat by, int prec)
        => new CFx(BigFloat.Sub(ax, bx, prec).ToFloatExp(), BigFloat.Sub(ay, by, prec).ToFloatExp()).Log2Abs();
}
