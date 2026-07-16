// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Math/DeepComplex.cs
//
// High-precision complex coordinate for the view centre.
//
// The interactive control path (pan / zoom / focus) carries the view centre
// in this ONE type at full octuple-double precision, ALWAYS — never in plain
// double that gets promoted at a zoom threshold. That promotion was the source
// of the recurring deep-zoom "anchor drift": a ~1e-16 world error frozen into
// a double centre at the HP transition blooms linearly in pixels as the zoom
// deepens. Keeping the centre in OD from the first pixel keeps every pan/zoom
// anchor exact to ~1e-28 world units at any depth.
//
// Precision is an INTERNAL detail. Call sites never see limbs or tiers, so a
// future move to arbitrary precision (or a different backing) touches only this
// file — not the six input handlers that were historically rewritten per tier.
// The render still reads the individual limbs (it tier-selects for per-pixel
// performance); this type converts to/from them losslessly.

using System;

namespace FracturingFog.FFMath
{
    /// <summary>Octuple-double complex value (~124 significant digits). Used as
    /// the single always-high-precision representation of the view centre in the
    /// interactive control path. Immutable value type — operations return a new
    /// value.</summary>
    public readonly struct DeepComplex
    {
        /// <summary>Real part (X axis of the complex plane).</summary>
        public readonly OD Re;

        /// <summary>Imaginary part (Y axis of the complex plane).</summary>
        public readonly OD Im;

        public DeepComplex(OD re, OD im) { Re = re; Im = im; }

        /// <summary>Build from the eight-limb X / eight-limb Y storage a
        /// <c>FractalViewState</c> keeps. Limbs beyond the active precision tier
        /// are simply zero, so this is exact at every tier.</summary>
        public static DeepComplex FromLimbs(
            double x0, double x1, double x2, double x3,
            double x4, double x5, double x6, double x7,
            double y0, double y1, double y2, double y3,
            double y4, double y5, double y6, double y7)
            => new(new OD(x0, x1, x2, x3, x4, x5, x6, x7),
                   new OD(y0, y1, y2, y3, y4, y5, y6, y7));

        /// <summary>Translate by a world-space delta (small doubles — a pixel
        /// offset times the plane scale). The add lands in the OD limbs, so the
        /// delta is preserved to OD precision no matter how small it is relative
        /// to the centre magnitude — this is what defeats the anchor-drift
        /// freeze.</summary>
        public DeepComplex Translate(double dxWorld, double dyWorld)
            => new(Re + dxWorld, Im + dyWorld);

        public override string ToString()
            => $"DeepComplex(Re={Re.X0:G17}…, Im={Im.X0:G17}…)";
    }
}
