// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Abstractions/Slideshow/SolidFrame.cs
//
// #437 — "is this frame a flat, single-colour render?" for the slideshow's
// skip logic. Some region / theme combinations render as all black or one
// solid colour (a fully in-set view, a theme that paints in-set and escape the
// same, an iteration cap below the escape band). The slideshow used an exact
// "every pixel equals the first" test, so a frame with a handful of stray
// pixels — still a blank frame to the eye — passed as fine. This counts the
// dominant colour instead.

using System.Collections.Generic;

namespace FracturingFog.Slideshow;

public static class SolidFrame
{
    /// <summary>Default share of pixels one colour must cover for the frame to
    /// read as solid: 99.5 %. A 64×36 probe tolerates up to 11 stray pixels.</summary>
    public const double DefaultThreshold = 0.995;

    /// <summary>True when one colour covers at least <paramref name="threshold"/>
    /// of the first <paramref name="count"/> pixels (alpha included). Empty → false.</summary>
    public static bool IsEffectivelySolid(uint[] pixels, int count, double threshold = DefaultThreshold)
    {
        if (pixels == null) return false;
        if (count > pixels.Length) count = pixels.Length;
        if (count <= 0) return false;

        // Fast path: the exact single-colour frame (the common blank case).
        uint first = pixels[0];
        int i = 1;
        while (i < count && pixels[i] == first) i++;
        if (i == count) return true;

        int needed = (int)System.Math.Ceiling(threshold * count);
        var counts = new Dictionary<uint, int>();
        for (int k = 0; k < count; k++)
        {
            uint c = pixels[k];
            counts.TryGetValue(c, out int n);
            n++;
            if (n >= needed) return true;
            counts[c] = n;
        }
        return false;
    }

    /// <inheritdoc cref="IsEffectivelySolid(uint[], int, double)"/>
    public static bool IsEffectivelySolid(uint[] pixels, double threshold = DefaultThreshold)
        => IsEffectivelySolid(pixels, pixels?.Length ?? 0, threshold);
}
