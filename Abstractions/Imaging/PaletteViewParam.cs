// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Imaging/PaletteViewParam.cs
//
// Roadmap slice S10-LW.1 (PaletteBuilder-Design.md §4a, #392 / #690) — the KEYSTONE of
// the live-host-wiring track. A palette change never changes geometry, so previewing a
// palette on the live fractal does not need a re-render — it needs the current view's
// PER-PIXEL PALETTE PARAMETER (the smooth-iteration t∈[0,1] the render already computes).
// Given that, the palette tool re-tints locally with its own colour core and builds the
// view histogram (S10.3) itself.
//
// This is the service contract only (Abstractions stays UI-free / render-free): the host
// implements it against the live render (Engine access lives in FracturingFog.Hosting),
// and the palette VM consumes the interface — the same injection shape as
// IPaletteExtractionService.

namespace FracturingFog.Imaging
{
    /// <summary>A snapshot of the current view's per-pixel palette parameter (roadmap
    /// S10-LW.1, #690). <see cref="T"/> holds <see cref="Width"/>×<see cref="Height"/>
    /// values in row-major order, each the pixel's palette parameter in [0,1] (in-set
    /// pixels read 0). Empty when no compatible view is available.</summary>
    public readonly record struct PaletteViewSample(float[] T, int Width, int Height)
    {
        /// <summary>True when the snapshot holds a well-formed, non-empty buffer.</summary>
        public bool HasData => T is { Length: > 0 } && Width > 0 && Height > 0 && T.Length >= Width * Height;
    }

    /// <summary>Host service exposing the current fractal view's per-pixel palette
    /// parameter to the palette tool (roadmap S10-LW.1, #690). Implemented host-side
    /// against the live render; consumed by the render-free palette VM.</summary>
    public interface IPaletteViewParamService
    {
        /// <summary>Try to snapshot the current view's per-pixel palette parameter.
        /// Returns false (and an empty <paramref name="sample"/>) when the active view
        /// has no compatible 2D parameter field (e.g. a pure-3D view, or no render yet).</summary>
        bool TryGetViewParam(out PaletteViewSample sample);
    }
}
