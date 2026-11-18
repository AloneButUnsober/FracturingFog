// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Calculators/MandelbrotCalculator.SharedOrbitAp.cs
//
// #1884 (epic #1428, C6c) — the cluster's shared reference orbit past OD (D-6b / D-6b2 ship
// DD / QD / OD orbits up to 1e115). Past ApReferenceZoomLog10 (110) every tile built the
// BigFloat orbit itself (#1840, ComputeReferenceOrbitAP): the master now builds it once,
// with the full image's period tolerance (#1614 / #1854), and each worker seeds its
// tier-3 cache with it. The calculator's own cache check (the exact centre, the precision,
// the cap, the period's tolerance) decides whether the seed is used: a mismatch rebuilds.

using System;
using System.Threading;
using FracturingFog.FFMath;

namespace FracturingFog;

public sealed partial class MandelbrotCalculator
{
    /// <summary>#1884 — the default <see cref="ApReferenceZoomLog10"/>: past it the reference
    /// orbit is built in BigFloat (tier 3), and the cluster ships that orbit.</summary>
    public const double DefaultApReferenceZoomLog10 = 110;

    /// <summary>#1884 — a tier-3 (BigFloat) reference orbit for the cluster: the stored Z_n
    /// as their eight limbs (<see cref="Od"/>), the exact centre and precision it was built
    /// at, and its period data (#1614 / #1854) and useful-zoom bound (#1427).</summary>
    public sealed class OrbitAP
    {
        public required OrbitOD Od { get; init; }
        public required ExactCentre Exact { get; init; }
        public required int Precision { get; init; }
        public int Period { get; init; }
        public double PeriodZ2 { get; init; }
        public FloatExp PeriodZ2Fx { get; init; }
        public double MaxUsefulZoomLog10 { get; init; } = double.PositiveInfinity;
    }

    /// <summary>#1884 — build the tier-3 reference orbit a frame of the full image
    /// (<paramref name="imageWidth"/> × <paramref name="imageHeight"/>, its limbs, exact
    /// centre and log zoom) would build, without rendering; null when the zoom keeps the OD
    /// orbit (≤ <see cref="DefaultApReferenceZoomLog10"/>).</summary>
    public static OrbitAP? ComputeReferenceOrbitAPPublic(
        DeepComplex limbs, ExactCentre? exact, double zoomLog10, int imageWidth, int imageHeight, int maxIter,
        CancellationToken ct = default)
    {
        if (maxIter <= 0) throw new ArgumentOutOfRangeException(nameof(maxIter));
        if (imageWidth <= 0 || imageHeight <= 0) throw new ArgumentOutOfRangeException(nameof(imageWidth));
        var calc = new MandelbrotCalculator(1, 1)
        {
            // The full image's pixel size sets the period tolerance (a tile's is the same).
            ImageWidth = imageWidth, ImageHeight = imageHeight,
            CenterX = limbs.Re.X0, CenterXLo = limbs.Re.X1, CenterX2 = limbs.Re.X2, CenterX3 = limbs.Re.X3,
            CenterX4 = limbs.Re.X4, CenterX5 = limbs.Re.X5, CenterX6 = limbs.Re.X6, CenterX7 = limbs.Re.X7,
            CenterY = limbs.Im.X0, CenterYLo = limbs.Im.X1, CenterY2 = limbs.Im.X2, CenterY3 = limbs.Im.X3,
            CenterY4 = limbs.Im.X4, CenterY5 = limbs.Im.X5, CenterY6 = limbs.Im.X6, CenterY7 = limbs.Im.X7,
            MaxIterations = maxIter,
        };
        calc.SetZoomLog10(zoomLog10);
        calc.SetDepth(exact);
        if (!calc.UseApReference) return null;
        calc.BuildReferenceOrbit(ct);
        return calc.ExportReferenceOrbitAP();
    }

    /// <summary>#1884 — the cached tier-3 orbit, copied out (null when the cache holds
    /// another tier).</summary>
    public OrbitAP? ExportReferenceOrbitAP()
    {
        if (_refCachedTier != 3 || _refExact is not { } exact) return null;
        int sz = _refOrbitLen + 1;
        static double[] Take(double[] a, int n) { var r = new double[n]; Array.Copy(a, r, n); return r; }
        var od = new OrbitOD
        {
            CentreX = _refCxHi, CentreXLo = _refCxLo, CentreX2 = _refCx2, CentreX3 = _refCx3,
            CentreX4 = _refCx4, CentreX5 = _refCx5, CentreX6 = _refCx6, CentreX7 = _refCx7,
            CentreY = _refCyHi, CentreYLo = _refCyLo, CentreY2 = _refCy2, CentreY3 = _refCy3,
            CentreY4 = _refCy4, CentreY5 = _refCy5, CentreY6 = _refCy6, CentreY7 = _refCy7,
            MaxIter = _refCachedMaxIter, RefLen = _refOrbitLen, Escaped = _refCachedEscaped,
            Zr = Take(_refZr, sz), Zi = Take(_refZi, sz), ZrLo = Take(_refZrLo, sz), ZiLo = Take(_refZiLo, sz),
            ZrX2 = Take(_refZrX2, sz), ZiX2 = Take(_refZiX2, sz), ZrX3 = Take(_refZrX3, sz), ZiX3 = Take(_refZiX3, sz),
            ZrX4 = Take(_refZrX4, sz), ZiX4 = Take(_refZiX4, sz), ZrX5 = Take(_refZrX5, sz), ZiX5 = Take(_refZiX5, sz),
            ZrX6 = Take(_refZrX6, sz), ZiX6 = Take(_refZiX6, sz), ZrX7 = Take(_refZrX7, sz), ZiX7 = Take(_refZiX7, sz),
        };
        return new OrbitAP
        {
            Od = od, Exact = exact, Precision = _refExactPrec,
            Period = _refPeriod, PeriodZ2 = _refPeriodZ2, PeriodZ2Fx = _refPeriodZ2Fx,
            MaxUsefulZoomLog10 = MaxUsefulZoomLog10,
        };
    }

    /// <summary>#1884 — seed the tier-3 cache with a shipped orbit (as
    /// <see cref="SeedReferenceOrbitOD"/> for OD). Set the view (limbs, zoom,
    /// <see cref="SetDepth"/>) first; the next frame uses the seed only where it would
    /// have built that same orbit (exact centre, precision, cap, period tolerance).</summary>
    public void SeedReferenceOrbitAP(OrbitAP orbit)
    {
        if (orbit == null) throw new ArgumentNullException(nameof(orbit));
        SeedReferenceOrbitOD(orbit.Od);
        _refExact = orbit.Exact;
        _refExactPrec = orbit.Precision;
        _refPeriod = orbit.Period;
        _refPeriodZ2 = orbit.PeriodZ2;
        _refPeriodZ2Fx = orbit.PeriodZ2Fx;
        MaxUsefulZoomLog10 = orbit.MaxUsefulZoomLog10;
        _refCachedTier = 3;   // AP (seeded)
    }
}
