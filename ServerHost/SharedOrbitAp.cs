// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// ServerHost/SharedOrbitAp.cs
// #1884 (epic #1428, C6c) — the tier-3 (BigFloat) shared reference orbit between the
// engine's MandelbrotCalculator.OrbitAP and the wire blob (ReferenceOrbitBlobCodec format
// version 2). The Server library stays Engine-free; this glue lives with the host (as
// ClusterEntry's provider), and is linked into Server.Tests.

using System;
using FracturingFog.FFMath;

namespace FracturingFog.Server.Cluster;

public static class SharedOrbitAp
{
    /// <summary>The orbit as a version-2 blob.</summary>
    public static byte[] Encode(MandelbrotCalculator.OrbitAP ap)
    {
        if (ap == null) throw new ArgumentNullException(nameof(ap));
        var o = ap.Od;
        var (ex, ey) = ap.Exact.ToStrings();
        return ReferenceOrbitBlobCodec.EncodeAP(o.RefLen, o.MaxIter, o.Escaped,
            new[] { o.CentreX, o.CentreXLo, o.CentreX2, o.CentreX3, o.CentreX4, o.CentreX5, o.CentreX6, o.CentreX7 },
            new[] { o.CentreY, o.CentreYLo, o.CentreY2, o.CentreY3, o.CentreY4, o.CentreY5, o.CentreY6, o.CentreY7 },
            new[] { o.Zr, o.Zi, o.ZrLo, o.ZiLo, o.ZrX2, o.ZiX2, o.ZrX3, o.ZiX3,
                    o.ZrX4, o.ZiX4, o.ZrX5, o.ZiX5, o.ZrX6, o.ZiX6, o.ZrX7, o.ZiX7 },
            new ReferenceOrbitBlobCodec.ApExtension(ap.Precision, ap.Period, ap.PeriodZ2,
                ap.PeriodZ2Fx.M, ap.PeriodZ2Fx.E, ap.MaxUsefulZoomLog10, ap.Exact.Precision, ex, ey));
    }

    /// <summary>A decoded version-2 blob as the engine's orbit; null when the blob is not
    /// an AP blob or its exact centre does not parse.</summary>
    public static MandelbrotCalculator.OrbitAP? ToOrbit(ReferenceOrbitBlobCodec.DecodedOrbit d)
    {
        if (d?.Ap is not { } a || d.Limbs != ReferenceOrbitBlobCodec.LimbsOD) return null;
        if (!ExactCentre.TryParse(a.ExactX, a.ExactY, out var exact, a.ExactBits)) return null;
        var od = new MandelbrotCalculator.OrbitOD
        {
            CentreX = d.CentreX, CentreXLo = d.CentreXLo, CentreX2 = d.CentreX2, CentreX3 = d.CentreX3,
            CentreX4 = d.CentreX4, CentreX5 = d.CentreX5, CentreX6 = d.CentreX6, CentreX7 = d.CentreX7,
            CentreY = d.CentreY, CentreYLo = d.CentreYLo, CentreY2 = d.CentreY2, CentreY3 = d.CentreY3,
            CentreY4 = d.CentreY4, CentreY5 = d.CentreY5, CentreY6 = d.CentreY6, CentreY7 = d.CentreY7,
            MaxIter = d.MaxIter, RefLen = d.RefLen, Escaped = d.Escaped,
            Zr = d.RefZr, Zi = d.RefZi, ZrLo = d.RefZrLo, ZiLo = d.RefZiLo,
            ZrX2 = d.RefZrX2, ZiX2 = d.RefZiX2, ZrX3 = d.RefZrX3, ZiX3 = d.RefZiX3,
            ZrX4 = d.RefZrX4, ZiX4 = d.RefZiX4, ZrX5 = d.RefZrX5, ZiX5 = d.RefZiX5,
            ZrX6 = d.RefZrX6, ZiX6 = d.RefZiX6, ZrX7 = d.RefZrX7, ZiX7 = d.RefZiX7,
        };
        return new MandelbrotCalculator.OrbitAP
        {
            Od = od, Exact = exact, Precision = a.Precision,
            Period = a.Period, PeriodZ2 = a.PeriodZ2,
            PeriodZ2Fx = a.PeriodZ2FxMantissa == 0 ? FloatExp.Zero : FloatExp.FromDouble(a.PeriodZ2FxMantissa).ScaleB(a.PeriodZ2FxExponent),
            MaxUsefulZoomLog10 = a.MaxUsefulZoomLog10,
        };
    }
}
