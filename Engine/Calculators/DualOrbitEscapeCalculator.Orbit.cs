// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// DualOrbitEscapeCalculator.Orbit.cs (#1117, epic #1114 S3)
//
// FF's orbit colourings, per orbit (Docs/Technical/DualOrbit-Coloring-RnD.md
// §3.A). Two routes:
//
// A. Scalar fields (palette-driven, cached like every other field):
//    TrapZ / TrapC   — minimum distance of that orbit to a trap shape. The shape
//                      is one of FF's 19 built-in trap SDFs, measured by the same
//                      sampler the orbit-trap themes use
//                      (DataDrivenOrbitTrap.ShapeImpl — one source of truth).
//                      Mapped d / (d + DualOrbitTrapScale).
//    TrapDelta       — trap_c − trap_z, centred (0 = mid-palette).
//    StripeZ / C     — stripe average 0.5 + 0.5·sin(density·arg u_k), smoothed
//                      across the escape step (Härkönen), DualOrbitStripeDensity.
//    StripeInterference — StripeZ × StripeC: the two stripe families multiplied
//                      (moiré where they disagree).
//    TiaZ / TiaC     — triangle-inequality average.
//    All are defined for bounded orbits too (average / minimum over the whole
//    orbit), so — like the secant Lyapunov field — they have no interior hole.
//    The trap fields also fill TrapBuffer (Relief "Trap" height source).
//
// B. Orbit-aware THEMES (IOrbitAwareColorMap: trap, stripe, TIA, curvature …):
//    Field OrbitThemeZ / OrbitThemeC runs the active theme's Sample() along that
//    orbit and colours with MapWithOrbit (interior via MapInteriorWithOrbit when
//    the theme asks for it); in PerOrbitLayers mode each orbit-aware layer theme
//    samples its own orbit. The accumulator cannot be cached, so whenever a theme
//    samples an orbit the calculator re-iterates on every Calculate (the host
//    already treats orbit-aware themes as "needs a full render").
//
// Sampling convention matches MandelbrotCalculator / UserEquationCalculator:
// Sample(u_k) after the escape test, for k ≥ 1 (the seed is not sampled).
// ComplexPlane map only; the quaternion map falls back to the plain path.

using System;

using FracturingFog.Interefaces;
using FracturingFog.Models;

namespace FracturingFog;

public sealed partial class DualOrbitEscapeCalculator : ITrapFieldSource
{
    /// <summary>#1117 — trap minimum per pixel for the trap fields (Relief "Trap"
    /// height source); empty otherwise.</summary>
    public float[] TrapBuffer { get; private set; } = Array.Empty<float>();

    internal static bool IsOrbitScalarField(DualOrbitField f)
        => f >= DualOrbitField.TrapZ && f <= DualOrbitField.TiaC;

    internal static bool IsOrbitThemeField(DualOrbitField f)
        => f is DualOrbitField.OrbitThemeZ or DualOrbitField.OrbitThemeC;

    // Which orbits a scalar field reads.
    private static (bool z, bool c) OrbitsRead(DualOrbitField f) => f switch
    {
        DualOrbitField.TrapZ or DualOrbitField.StripeZ or DualOrbitField.TiaZ or DualOrbitField.OrbitThemeZ => (true, false),
        DualOrbitField.TrapC or DualOrbitField.StripeC or DualOrbitField.TiaC or DualOrbitField.OrbitThemeC => (false, true),
        DualOrbitField.TrapDelta or DualOrbitField.StripeInterference => (true, true),
        _ => (false, false),
    };

    private static bool IsTrapField(DualOrbitField f)
        => f is DualOrbitField.TrapZ or DualOrbitField.TrapC or DualOrbitField.TrapDelta;
    private static bool IsStripeField(DualOrbitField f)
        => f is DualOrbitField.StripeZ or DualOrbitField.StripeC or DualOrbitField.StripeInterference;
    private static bool IsTiaField(DualOrbitField f)
        => f is DualOrbitField.TiaZ or DualOrbitField.TiaC;

    // Stripe / TIA running sums for one orbit.
    private struct OrbitStats
    {
        public double StripeSum, StripeLast; public int StripeCount;
        public double TiaSum, TiaLast; public int TiaCount;
        public double PrevMag2;

        // Average with Härkönen smoothing across the escape step (w = frac of
        // the smooth count); bounded orbits use the plain average.
        public readonly double Stripe(bool escaped, double smoothN) => Smoothed(StripeSum, StripeLast, StripeCount, escaped, smoothN);
        public readonly double Tia(bool escaped, double smoothN) => Smoothed(TiaSum, TiaLast, TiaCount, escaped, smoothN);

        private static double Smoothed(double sum, double last, int count, bool escaped, double smoothN)
        {
            if (count == 0) return 0.0;
            double a = sum / count;
            if (!escaped || count < 2) return a;
            double aPrev = (sum - last) / (count - 1);
            double w = smoothN - Math.Floor(smoothN);
            return w * a + (1.0 - w) * aPrev;
        }
    }

    // Run (identical arithmetic and args recording) plus per-step sampling:
    // an orbit-aware theme / trap sampler (`sampler`) and/or stripe + TIA sums.
    private static Orbit RunSampled(double u0x, double u0y, double sx, double sy, int maxIter,
        in Bailout b, Span<double> args, IOrbitAwareColorMap? sampler, bool stats, double stripeDensity,
        ref OrbitAccumulator acc, ref OrbitStats st, out int escapeIndex)
    {
        double zx = u0x, zy = u0y;
        bool track = !args.IsEmpty;
        double sMag = Math.Sqrt(sx * sx + sy * sy);
        st.PrevMag2 = zx * zx + zy * zy;
        for (int n = 0; n < maxIter; n++)
        {
            if (track) args[n] = Math.Atan2(zy, zx);
            double x2 = zx * zx, y2 = zy * zy;
            double r2 = x2 + y2;
            if (r2 > b.R2)
            {
                double logZn = Math.Log(r2) * 0.5;
                double nu = Math.Log(logZn / b.LogR) / Math.Log(2.0);
                escapeIndex = n;
                return new Orbit(true, zx, zy, n - nu);
            }
            if (n > 0)
            {
                sampler?.Sample(ref acc, zx, zy, sx, sy, n);
                if (stats)
                {
                    double t = 0.5 + 0.5 * Math.Sin(stripeDensity * Math.Atan2(zy, zx));
                    st.StripeSum += t; st.StripeLast = t; st.StripeCount++;
                    // TIA: |u_k| against the triangle-inequality bounds of u_{k−1}² + s.
                    double lo = Math.Abs(st.PrevMag2 - sMag), hi = st.PrevMag2 + sMag;
                    if (hi > lo)
                    {
                        double ti = (Math.Sqrt(r2) - lo) / (hi - lo);
                        st.TiaSum += ti; st.TiaLast = ti; st.TiaCount++;
                    }
                }
            }
            st.PrevMag2 = r2;
            double nzx = x2 - y2 + sx;
            zy = 2.0 * zx * zy + sy;
            zx = nzx;
        }
        escapeIndex = -1;
        return new Orbit(false, zx, zy, maxIter);
    }

    // Scalar for the S3 scalar fields, [LiveFloor, maxIter]. Live everywhere.
    private static double OrbitScalar(DualOrbitField field, in OrbitAccumulator accZ, in OrbitAccumulator accC,
        in OrbitStats stZ, in OrbitStats stC, in Orbit oz, in Orbit oc, double trapScale, int maxIter)
    {
        double v = field switch
        {
            DualOrbitField.TrapZ => TrapT(accZ.TrapMin, trapScale),
            DualOrbitField.TrapC => TrapT(accC.TrapMin, trapScale),
            DualOrbitField.TrapDelta => DeltaT(Finite(accC.TrapMin) - Finite(accZ.TrapMin), trapScale),
            DualOrbitField.StripeZ => stZ.Stripe(oz.Escaped, oz.SmoothN),
            DualOrbitField.StripeC => stC.Stripe(oc.Escaped, oc.SmoothN),
            DualOrbitField.StripeInterference => stZ.Stripe(oz.Escaped, oz.SmoothN) * stC.Stripe(oc.Escaped, oc.SmoothN),
            DualOrbitField.TiaZ => stZ.Tia(oz.Escaped, oz.SmoothN),
            DualOrbitField.TiaC => stC.Tia(oc.Escaped, oc.SmoothN),
            _ => 0.0,
        };
        return Math.Clamp(v * maxIter, LiveFloor, maxIter);

        static double Finite(float d) => d == float.MaxValue ? 0.0 : d;
        static double TrapT(float d, double k) => d == float.MaxValue ? 1.0 : d / (d + k);
        static double DeltaT(double d, double k) => 0.5 + 0.5 * d / (Math.Abs(d) + k);
    }

    // The trap sampler for the selected shape (FF's built-in SDF samplers).
    private static IOrbitAwareColorMap TrapSampler(int shape)
        => DataDrivenOrbitTrap.ShapeImpl((OrbitTrapShape)Math.Clamp(shape, 0, (int)OrbitTrapShape.PolarRose));

    // Colour one orbit through an orbit-aware theme: escaped → MapWithOrbit;
    // bounded → MapInteriorWithOrbit if the theme wants interior colour, else
    // the interior colour (alpha applied by the caller's value).
    private static uint OrbitThemeColor(IOrbitAwareColorMap theme, in Orbit o, in OrbitAccumulator acc,
        int maxIter, uint interiorColor)
    {
        if (o.Escaped)
            return unchecked((uint)theme.MapWithOrbit((float)Math.Max(LiveFloor, o.SmoothN), 0f, maxIter, 0f, 0f, in acc));
        return theme.WantsInteriorColor ? unchecked((uint)theme.MapInteriorWithOrbit(maxIter, in acc)) : interiorColor;
    }
}
