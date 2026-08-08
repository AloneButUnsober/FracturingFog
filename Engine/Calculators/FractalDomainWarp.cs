// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// FractalDomainWarp.cs
//
// #253 / IDEA-3 — cross-fractal domain warp. Displaces a per-pixel sampling
// coordinate by a smooth sine-interference field *before* the fractal iterates,
// folding the straight coordinate grid into organic swirls. This is the same
// two-tap warp the Acid Fog pattern field uses (AcidWarpCalculator.DomainWarp),
// lifted to a shared helper so the escape-time fractals can reuse it as a
// pre-sample coordinate stage — "reuse the same field as a domain-warp layer
// that displaces the sampling coordinates of an existing fractal" (design §3
// IDEA-3).
//
// The field is evaluated in NORMALISED view space (the longer screen axis spans
// ~[-1, 1]) so the swirl looks the same at any zoom; the resulting displacement
// is scaled back into fractal-plane units. Strength 0 is an exact no-op — the
// callers guard on it so the un-warped fractal path stays byte-identical.

using System;

namespace FracturingFog.Models;

public static class FractalDomainWarp
{
    /// <summary>
    /// Displace the view-space offset (<paramref name="ox"/>, <paramref name="oy"/>)
    /// — the pixel's offset from the view centre, in fractal-plane units — by the
    /// warp field. <paramref name="halfSpan"/> is half the longer-axis span in
    /// plane units (used both to normalise the coordinate and to scale the
    /// displacement back). No-op when strength is 0 or the span is degenerate.
    /// </summary>
    public static void Apply(ref double ox, ref double oy, double halfSpan,
                             double strength, double frequency)
    {
        if (halfSpan <= 0.0 || strength == 0.0) return;
        double nx = ox / halfSpan;               // normalised view coordinate
        double ny = oy / halfSpan;
        double k = 3.0 * (frequency <= 0.0 ? 1.0 : frequency);
        double dx = Math.Sin(ny * k + nx * 1.3);
        double dy = Math.Sin(nx * k - ny * 1.3);
        ox += strength * dx * halfSpan;
        oy += strength * dy * halfSpan;
    }
}
