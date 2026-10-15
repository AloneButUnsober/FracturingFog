// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Explore/OdExact.cs
//
// Cancellation-safe octuple-double arithmetic for the location finders
// (epic #1184). OD's OD+OD operator used to be the "sloppy" pairwise variant,
// which under heavy cancellation — exactly z² + c → 0 at a minibrot nucleus —
// floors at ~1e-65 relative; this class added b one limb at a time through the
// full-cascade OD+double operator instead. Since #1319 OD+OD itself is an exact
// expansion sum, so Add delegates to it; the class stays as the finders' named
// entry point.

using FracturingFog.FFMath;

namespace FracturingFog.Abstractions.Explore;

public static class OdExact
{
    /// <summary>a + b, accurate under cancellation.</summary>
    public static OD Add(OD a, OD b) => a + b;

    /// <summary>a − b, accurate under cancellation.</summary>
    public static OD Sub(OD a, OD b) => Add(a, -b);

    /// <summary>One step of z ← z² + c on (x, y), accurate under cancellation.</summary>
    public static (OD X, OD Y) SquareAdd(OD x, OD y, OD cx, OD cy)
    {
        OD xy = x * y;
        return (Add(Sub(x * x, y * y), cx), Add(xy + xy, cy));
    }
}
