// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Imaging/PaletteLookApply.cs
//
// Roadmap slice S10-LW.4b (PaletteBuilder-Design.md §4a, #392 / #695) — apply a scene
// "look" (roadmap S10.10) to the live render. A look pairs a ramp with a material preset
// (roughness / metallic) and palette-drawn lights; this contract lets the render-free
// palette tool push the MATERIAL + LIGHT-TINT half into the render — the ramp half already
// travels the ordinary stops path (LW.4a). Host-implemented against the live render's
// lighting params (Engine access lives in FracturingFog.Hosting); consumed by the palette
// VM. Signature is primitives only (no render types) so Abstractions stays render-free.
//
// Emission is intentionally omitted — LightingFxData has no emission field yet (the Look's
// EmissionTint is a forward-hook for roadmap S5).

namespace FracturingFog.Imaging
{
    /// <summary>Host service that writes a look's material + light tints into the live
    /// render (roadmap S10-LW.4b, #695), and applies a palette (gradient) to the live
    /// render's colour map.</summary>
    public interface IPaletteLookApplyService
    {
        /// <summary>Apply a gradient to the LIVE render's colour map and re-render, so a
        /// generated ramp / adopted look changes the fractal immediately — not only after
        /// the picker's Apply. <paramref name="stops"/> are the ordered gradient stops
        /// (position + RGB). Fewer than two stops is a no-op.</summary>
        void ApplyPalette(System.Collections.Generic.IReadOnlyList<PaletteStop> stops);

        /// <summary>Apply a look's material preset + palette-drawn lights to the current
        /// view and re-render: <paramref name="roughness"/> / <paramref name="metallic"/>
        /// (each [0,1]) set the surface material; <paramref name="keyTint"/> tints the key
        /// light; <paramref name="skyTint"/> tints the sky/ambient backdrop.</summary>
        void ApplyLook(
            double roughness, double metallic,
            (byte r, byte g, byte b) keyTint,
            (byte r, byte g, byte b) skyTint);
    }
}
