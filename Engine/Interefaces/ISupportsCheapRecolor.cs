// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Interefaces/ISupportsCheapRecolor.cs
//
// Capability marker for alt calculators that can re-colourise their current
// frame from cached intermediate data WITHOUT re-running Calculate() (#194).
//
// Mandelbrot already has this via the concrete
// MandelbrotCalculator.ApplyBandDitherRecolor path; alt calculators had no
// equivalent, so FractalRenderHost.ApplyColorMap fell back to a full Trigger()
// for every one of them. For the Buddhabrot family — a Monte Carlo density plot
// whose sample pass is by far the dominant cost — that meant a colour-theme
// change re-sampled the whole image. The accumulated hit histograms persist
// after Calculate, so recolouring is just a composite pass over cached data.
//
// Kept off IFractalCalculator so families with no cheap path (IFS, L-system,
// attractors, raymarch 3D) don't have to stub a meaningless method — the host
// pattern-matches `calc is ISupportsCheapRecolor` and only takes the cheap path
// when it's actually implemented.

namespace FracturingFog.Interefaces
{
    /// <summary>
    /// An alt calculator whose <see cref="IFractalCalculator.ColorBuffer"/> can
    /// be rebuilt from cached intermediate state (with the current
    /// <see cref="IFractalCalculator.ColorMap"/> and parameters) without a full
    /// <c>Calculate()</c>. Used by the render host so a colour-theme change
    /// recolours in place instead of triggering a full recompute.
    /// </summary>
    public interface ISupportsCheapRecolor
    {
        /// <summary>
        /// Recomposite <see cref="IFractalCalculator.ColorBuffer"/> from cached
        /// data using the currently-assigned <c>ColorMap</c> and parameters. Must
        /// be safe to call after a <c>Calculate()</c> (uses the retained
        /// intermediate buffers); a no-op-safe result (e.g. cleared buffer) is
        /// acceptable when no <c>Calculate()</c> has run yet.
        /// </summary>
        void Recolor();
    }
}
