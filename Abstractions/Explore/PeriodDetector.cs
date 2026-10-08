// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Explore/PeriodDetector.cs
//
// Interesting-location finder S1 (#1185, epic #1184). Design:
// Docs/Technical/Interesting-Location-Finder-DesignPlan.md §3.1.
//
// Answers "which minibrot (hyperbolic component of z² + c) has its nucleus
// inside this view, and what is its period?" by ball arithmetic: track the
// orbit of the disk centre c₀ together with a radius r that bounds the orbit
// of EVERY c in the disk |c − c₀| ≤ ρ:
//
//     z ← z² + c₀
//     r ← 2|z|·r + r² + ρ      since |(z+e)² + c₀ + δ − (z² + c₀)| ≤ 2|z||e| + |e|² + |δ|
//
// The first n ≥ 1 with |zₙ| < rₙ means the ball contains 0, i.e. some c in
// the disk may have fⁿ_c(0) = 0 — a period-n nucleus candidate. Ball
// arithmetic over-approximates, so the result is a CANDIDATE: S2's Newton
// confirms (or refutes) it.
//
// Precision: the orbit must be accurate to well below ρ, so a shallow disk
// (ρ ≥ DoublePathMinRadius) runs in plain double and a deep one runs the
// orbit in octuple-double (the view centre is already OD — DeepComplex).
// The radius only needs magnitude, so it stays in double throughout.

using System;
using System.Threading;

using FracturingFog.FFMath;

using SMath = System.Math;

namespace FracturingFog.Abstractions.Explore;

/// <summary>Result of a period scan. <see cref="Period"/> is 0 when no
/// component nucleus was found in the disk within the iteration budget.</summary>
public readonly record struct PeriodDetection(int Period, int Iterations, PeriodStop Stop)
{
    public bool Found => Period > 0;
}

/// <summary>Why a period scan stopped.</summary>
public enum PeriodStop
{
    /// <summary>The ball reached 0: <see cref="PeriodDetection.Period"/> is the candidate.</summary>
    Found,
    /// <summary>The whole disk escaped — no nucleus can be inside.</summary>
    Escaped,
    /// <summary>The ball grew past the escape bound and stopped being informative.</summary>
    BallTooLarge,
    /// <summary>The iteration budget (maxPeriod) ran out.</summary>
    MaxPeriod,
}

/// <summary>Ball-arithmetic period detection for f_c(z) = z² + c. UI-free;
/// see the file header for the method.</summary>
public static class PeriodDetector
{
    /// <summary>Default iteration budget. Deep minibrots reach periods in the
    /// tens of thousands; the OD path costs ~O(maxPeriod).</summary>
    public const int DefaultMaxPeriod = 100_000;

    /// <summary>Default escape bound for both the orbit and the ball radius.</summary>
    public const double DefaultEscapeRadius = 4.0;

    /// <summary>Disks at least this large run the orbit in double; smaller
    /// ones need octuple-double because double rounding (~1e-16·|z|) would
    /// swamp the ball radius.</summary>
    public const double DoublePathMinRadius = 1e-10;

    /// <summary>Radius of the disk covering a view: half its diagonal, in
    /// world units. <paramref name="zoom"/> uses the ViewCamera convention
    /// (PlaneExtent / zoom spans the larger viewport dimension).</summary>
    public static double ViewDiskRadius(double zoom, int viewW, int viewH, double planeExtent = 3.5)
    {
        int w = SMath.Max(1, viewW), h = SMath.Max(1, viewH);
        double scale = planeExtent / (SMath.Max(w, h) * zoom);   // world units per pixel
        return 0.5 * scale * SMath.Sqrt((double)w * w + (double)h * h);
    }

    /// <summary>Scan the disk |c − <paramref name="centre"/>| ≤
    /// <paramref name="radius"/> for the lowest-period nucleus candidate.</summary>
    public static PeriodDetection Detect(
        DeepComplex centre, double radius,
        int maxPeriod = DefaultMaxPeriod,
        double escapeRadius = DefaultEscapeRadius,
        CancellationToken ct = default)
    {
        if (!(radius > 0) || double.IsInfinity(radius))
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "Disk radius must be positive and finite.");
        if (maxPeriod < 1)
            throw new ArgumentOutOfRangeException(nameof(maxPeriod), maxPeriod, "maxPeriod must be at least 1.");

        return radius >= DoublePathMinRadius
            ? DetectDouble((double)centre.Re, (double)centre.Im, radius, maxPeriod, escapeRadius, ct)
            : DetectDeep(centre, radius, maxPeriod, escapeRadius, ct);
    }

    private static PeriodDetection DetectDouble(
        double cx, double cy, double rho, int maxPeriod, double escapeRadius, CancellationToken ct)
    {
        double zx = 0, zy = 0, r = 0;
        for (int n = 1; n <= maxPeriod; n++)
        {
            if ((n & 0x3FF) == 0) ct.ThrowIfCancellationRequested();

            double az = SMath.Sqrt(zx * zx + zy * zy);
            r = 2 * az * r + r * r + rho;
            double nx = zx * zx - zy * zy + cx;
            zy = 2 * zx * zy + cy;
            zx = nx;

            var stop = Check(SMath.Sqrt(zx * zx + zy * zy), r, escapeRadius);
            if (stop is PeriodStop s)
                return new PeriodDetection(s == PeriodStop.Found ? n : 0, n, s);
        }
        return new PeriodDetection(0, maxPeriod, PeriodStop.MaxPeriod);
    }

    private static PeriodDetection DetectDeep(
        DeepComplex c, double rho, int maxPeriod, double escapeRadius, CancellationToken ct)
    {
        OD zx = OD.Zero, zy = OD.Zero;
        double r = 0;
        for (int n = 1; n <= maxPeriod; n++)
        {
            if ((n & 0xFF) == 0) ct.ThrowIfCancellationRequested();

            double az = Magnitude(zx, zy);
            r = 2 * az * r + r * r + rho;
            (zx, zy) = OdExact.SquareAdd(zx, zy, c.Re, c.Im);

            var stop = Check(Magnitude(zx, zy), r, escapeRadius);
            if (stop is PeriodStop s)
                return new PeriodDetection(s == PeriodStop.Found ? n : 0, n, s);
        }
        return new PeriodDetection(0, maxPeriod, PeriodStop.MaxPeriod);
    }

    // |z| from the leading limbs. OD is normalised (X0 carries the value to
    // double accuracy, at any magnitude), and the comparison against r only
    // needs magnitude — so the tail limbs are irrelevant here.
    private static double Magnitude(OD x, OD y)
    {
        double a = x.X0, b = y.X0;
        return SMath.Sqrt(a * a + b * b);
    }

    private static PeriodStop? Check(double absZ, double r, double escapeRadius)
    {
        if (absZ < r) return PeriodStop.Found;
        if (absZ - r > escapeRadius) return PeriodStop.Escaped;
        if (r > escapeRadius || double.IsNaN(r)) return PeriodStop.BallTooLarge;
        return null;
    }
}
