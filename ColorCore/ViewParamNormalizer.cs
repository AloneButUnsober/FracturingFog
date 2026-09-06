// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Imaging/ViewParamNormalizer.cs
//
// Roadmap slice S10-LW.1 (PaletteBuilder-Design.md §4a, #392 / #690) — the pure half of
// the view-parameter keystone: turn a render's raw smooth-iteration field into the
// palette parameter t∈[0,1] the palette tool + PaletteHistogram (S10.3) consume. Kept
// separate from the host wiring so it is deterministic and unit-tested (the render-side
// accessor and the Hosting service just feed it).

using System;

namespace FracturingFog.Imaging;

/// <summary>Normalises a render's smooth-iteration buffer into the palette parameter
/// t∈[0,1] (roadmap S10-LW.1, #690).</summary>
public static class ViewParamNormalizer
{
    /// <summary>Map a smooth-iteration field to t = smooth / maxIterations, clamped to
    /// [0,1]. In-set pixels (smooth 0), negatives and non-finite values collapse to 0 —
    /// the ramp's start — matching the render's convention that in-set pixels read 0.
    /// Returns a fresh array of length <paramref name="count"/> (clamped to the source
    /// length); a non-positive <paramref name="maxIterations"/> yields all-zero.</summary>
    public static float[] NormalizeSmooth(float[] smooth, int maxIterations, int count)
    {
        if (count < 0 || smooth == null) count = 0;
        else if (count > smooth.Length) count = smooth.Length;
        var t = new float[count];
        if (smooth == null || maxIterations <= 0) return t;

        float inv = 1f / maxIterations;
        for (int i = 0; i < count; i++)
        {
            float s = smooth[i];
            if (!float.IsFinite(s) || s <= 0f) { t[i] = 0f; continue; }
            float v = s * inv;
            t[i] = v < 0f ? 0f : (v > 1f ? 1f : v);
        }
        return t;
    }
}
