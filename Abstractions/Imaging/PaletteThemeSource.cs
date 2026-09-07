// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Imaging/PaletteThemeSource.cs
//
// Feature #709 (PaletteBuilder, #392) — load the CURRENT theme's palette back into the
// palette viewer, so a previously-created theme can be refined (advisor / CVD / harmony /
// histogram-redistribute) and applied back, instead of rebuilt from an image. Host-
// implemented against the live render's active colour map (Engine access lives in
// FracturingFog.Hosting); consumed by the render-free palette VM — the same injection
// shape as the other palette host services. Applying back uses the existing
// IPaletteLookApplyService.ApplyPalette hook (PR #707).

using System.Collections.Generic;

namespace FracturingFog.Imaging
{
    /// <summary>Host service exposing the live render's active theme as gradient stops
    /// (feature #709).</summary>
    public interface IPaletteThemeSourceService
    {
        /// <summary>Try to read the current view's active theme as ordered gradient stops.
        /// Returns false when the active theme is not a gradient (e.g. a procedural map) or
        /// has fewer than two stops; <paramref name="name"/> is the theme's display name.</summary>
        bool TryGetActiveTheme(out IReadOnlyList<PaletteStop> stops, out string name);
    }
}
