// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// DualOrbitEscapeCalculator.Interior.cs (#1121, epic #1114 S7)
//
// Interior cycle phase lag (Docs/Technical/DualOrbit-Coloring-RnD.md §3.D).
// Inside M_c both orbits are bounded and — in a hyperbolic component — converge
// to the SAME attracting p-cycle {ζ_0 … ζ_{p−1}} of u → u² + s. They need not
// arrive in step: the c-orbit tracks the critical orbit shifted by k steps,
// c_N ≈ z_{N+k}. That offset k ∈ {0 … p−1} exists only with two orbits; it
// partitions the Fatou components into p classes (strongest in the CxCy /
// Julia slice, where the critical orbit is the fixed reference).
//
// It is the discrete partner of the S2 secant Lyapunov exponent: lag 0 ⇔
// λ → ln|μ|/p (the pair contracts onto one point), lag ≠ 0 ⇔ λ → 0 (D_n becomes
// periodic) — verified in the doc §5 and re-checked by a test here.
//
// Read from the cached end states (z_N, c_N): the critical orbit is iterated on
// for at most CycleSearchMax steps to find its period p (first return within
// CycleTol); then k minimises |c_N − z_{N+j}|, accepted only if the c-orbit has
// converged onto the cycle (within ConvergeTol). Pixels that fail either test —
// escaping, not yet converged (near a parabolic boundary), period above the
// search cap — have no value (interior colour). ComplexPlane map only.

using System;

namespace FracturingFog;

public sealed partial class DualOrbitEscapeCalculator
{
    internal const int CycleSearchMax = 1024;
    internal const double CycleTol = 1e-8;
    internal const double ConvergeTol = 1e-5;

    /// <summary>Okabe–Ito (colour-blind-safe) reordered so the first classes
    /// contrast on the blue↔amber axis: orange, blue, yellow, sky blue,
    /// vermillion, bluish green, reddish purple.</summary>
    internal static readonly uint[] LagPalette =
    {
        0xFFE69F00u, 0xFF0072B2u, 0xFFF0E442u, 0xFF56B4E9u, 0xFFD55E00u, 0xFF009E73u, 0xFFCC79A7u,
    };

    /// <summary>Categorical colour for a PhaseLag (index k) / CyclePeriod (index
    /// p − 1) scalar; null for other fields.</summary>
    internal static uint? CategoricalColor(DualOrbitField field, float scalar)
    {
        if (field == DualOrbitField.PhaseLag || field == DualOrbitField.BinaryXor)   // #1118: XOR bit
            return LagPalette[(int)MathF.Round(scalar) % LagPalette.Length];   // LiveFloor rounds to 0
        if (field == DualOrbitField.CyclePeriod)
            return LagPalette[((int)MathF.Round(scalar) - 1) % LagPalette.Length];
        return null;
    }

    internal static bool IsInteriorField(DualOrbitField f)
        => f is DualOrbitField.PhaseLag or DualOrbitField.PhaseLagFraction or DualOrbitField.CyclePeriod;

    /// <summary>Period p of the attracting cycle the bounded critical orbit has
    /// converged to (first return of z_N within CycleTol), or 0 if none found.
    /// Also returns the lag k of the c-orbit (−1 if it is not on the cycle).</summary>
    internal static int CycleAndLag(double zx, double zy, double cx, double cy, double sx, double sy,
        bool cBounded, out int lag)
    {
        lag = -1;
        double tol = CycleTol * (1.0 + Math.Sqrt(zx * zx + zy * zy));
        double wx = zx, wy = zy;
        int p = 0;
        for (int j = 1; j <= CycleSearchMax; j++)
        {
            double nx = wx * wx - wy * wy + sx;
            wy = 2.0 * wx * wy + sy;
            wx = nx;
            if (!double.IsFinite(wx) || wx * wx + wy * wy > 16.0) return 0;
            if (Math.Abs(wx - zx) < tol && Math.Abs(wy - zy) < tol) { p = j; break; }
        }
        if (p == 0 || !cBounded) return p;

        // ζ_j = z_{N+j}: nearest cycle point to c_N.
        double best = double.PositiveInfinity;
        wx = zx; wy = zy;
        for (int j = 0; j < p; j++)
        {
            double dx = cx - wx, dy = cy - wy, d = dx * dx + dy * dy;
            if (d < best) { best = d; lag = j; }
            double nx = wx * wx - wy * wy + sx;
            wy = 2.0 * wx * wy + sy;
            wx = nx;
        }
        if (Math.Sqrt(best) > ConvergeTol * (1.0 + Math.Sqrt(cx * cx + cy * cy))) lag = -1;
        return p;
    }

    // Scalar for the interior fields, [LiveFloor, maxIter]; live = false where the
    // field has no value.
    private static double InteriorScalar(DualOrbitField field, in Orbit oz, in Orbit oc,
        double sx, double sy, int maxIter, out bool live)
    {
        live = false;
        if (oz.Escaped) return 0.0;
        int p = CycleAndLag(oz.Ex, oz.Ey, oc.Ex, oc.Ey, sx, sy, !oc.Escaped, out int k);
        if (p == 0) return 0.0;
        double v;
        switch (field)
        {
            case DualOrbitField.CyclePeriod:
                v = p;
                break;
            case DualOrbitField.PhaseLag:
                if (k < 0) return 0.0;
                v = k;
                break;
            case DualOrbitField.PhaseLagFraction:
                if (k < 0) return 0.0;
                v = (k + 0.5) / p * maxIter;    // p classes spread across the palette
                break;
            default:
                return 0.0;
        }
        live = true;
        return Math.Clamp(v, LiveFloor, maxIter);
    }
}
