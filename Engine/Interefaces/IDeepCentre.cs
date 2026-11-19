// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

using FracturingFog.FFMath;

namespace FracturingFog.Interefaces
{
    /// <summary>
    /// #1906 (epic #1253, D1) — a non-Mandelbrot calculator that takes more of the view's
    /// centre than <see cref="IFractalCalculator"/>'s double <c>CenterX</c> / <c>CenterY</c>:
    /// the sixteen octuple-double limbs and, past OD, the exact centre. The render host, the
    /// video engine, the poster / batch / cluster path and the explorer probe hand the full
    /// centre over through this, instead of naming each calculator's own limb properties.
    /// Before it, only the main live frame (Generated Z2–Z5, TearDrop) and the poster (not
    /// User Equation) copied QD limbs per type. The shared sync behind the preview sidecars,
    /// the slideshow cross-fade and re-renders, and the video engine, copied the Hi limb only,
    /// and Generated Tricorn / Burning Ship and Compile &amp; Load were named nowhere, so their
    /// QD tiers rendered around a centre ~1e-17 off past ~1e15 there.
    /// A calculator uses as many limbs as its precision tiers carry and keeps the rest, so
    /// <see cref="GetDeepCentre"/> returns what was set.
    /// </summary>
    public interface IDeepCentre
    {
        /// <summary>Set the centre: all sixteen limbs, and the exact centre (null when the
        /// limbs are the centre). Also sets <c>CenterX</c> / <c>CenterY</c> (the X0 limbs).</summary>
        void SetDeepCentre(DeepComplex centre, ExactCentre? exact);

        /// <summary>The centre as last set (all sixteen limbs).</summary>
        DeepComplex GetDeepCentre();

        /// <summary>The exact centre, or null when none was set or the limbs have moved
        /// since (then the limbs are the centre).</summary>
        ExactCentre? CenterExact { get; }
    }
}
