// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Explore/OdExact.cs
//
// Cancellation-safe octuple-double arithmetic for the location finders
// (epic #1184). OD's own OD+OD operator is the "sloppy" pairwise variant: fine
// for a reference orbit, but under heavy cancellation — exactly z² + c → 0 at a
// minibrot nucleus — it floors at ~1e-65 relative, which stalls Newton and
// blinds a deep ball scan. Adding b one limb at a time through OD's
// full-cascade OD+double operator keeps every carry, so a sum that cancels to
// a tiny value is still accurate to ~OD epsilon of the operands.

using FracturingFog.FFMath;

namespace FracturingFog.Abstractions.Explore;

public static class OdExact
{
    /// <summary>a + b, accurate under cancellation.</summary>
    public static OD Add(OD a, OD b)
    {
        OD s = a + b.X0;
        if (b.X1 != 0) s += b.X1; else return s;
        if (b.X2 != 0) s += b.X2; else return s;
        if (b.X3 != 0) s += b.X3; else return s;
        if (b.X4 != 0) s += b.X4; else return s;
        if (b.X5 != 0) s += b.X5; else return s;
        if (b.X6 != 0) s += b.X6; else return s;
        if (b.X7 != 0) s += b.X7;
        return s;
    }

    /// <summary>a − b, accurate under cancellation.</summary>
    public static OD Sub(OD a, OD b) => Add(a, -b);

    /// <summary>One step of z ← z² + c on (x, y), accurate under cancellation.</summary>
    public static (OD X, OD Y) SquareAdd(OD x, OD y, OD cx, OD cy)
    {
        OD xy = x * y;
        return (Add(Sub(x * x, y * y), cx), Add(xy + xy, cy));
    }
}
